using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    internal struct BakeResult
    {
        public bool Success;
        public string Message;
        public int ModifiedRendererCount;
        public int CreatedHelperBoneCount;
        public int TotalAffectedVertices;
        public bool DetectedScaleAdjuster;
    }

    /// <summary>
    /// 破壊的ベイク＆差し替え（および元に戻す Revert）を実行するエディタサービス。
    /// 他ツールとの相性問題や NDMF を使わない運用でも確実に動作するよう、
    /// 衣装側 Armature（MA Merge Armature 前提）またはアバター Armature に補助ボーンと
    /// ベイク済みメッシュ .asset を生成し、元メッシュ情報を保持したままコンポーネントを無効化する。
    /// </summary>
    internal static class JointKeeperBaker
    {
        private const string FallbackBakedFolder = "Assets/JointKeeper_Baked";
        private const float RestDriftWarningThresholdMeters = 0.0005f; // 0.5 mm

        private static readonly JointId[] AllJoints =
        {
            JointId.LeftElbow,
            JointId.RightElbow,
            JointId.LeftKnee,
            JointId.RightKnee,
        };

        /// <summary>
        /// 指定した JointKeeperComponent の設定で破壊的ベイクを実行し、対象 SMR をベイク済みメッシュへ差し替える。
        /// 既にベイク済みの場合は一度 Revert してから再ベイクする（1 回の Undo で取り消し可能）。
        /// </summary>
        public static BakeResult BakeComponent(JointKeeperComponent comp)
        {
            if (comp == null)
            {
                return new BakeResult { Success = false, Message = JointKeeperLocalization.Tr("bake.err_null") };
            }

            Animator animator = FindAvatarAnimator(comp.transform);
            if (animator == null)
            {
                return new BakeResult
                {
                    Success = false,
                    Message = JointKeeperLocalization.Tr("bake.err_no_animator"),
                };
            }

            Transform avatarRoot = animator.transform;
            var humanoidBones = HumanoidBoneResolver.ResolveAll(animator, out _);
            if (humanoidBones.Count == 0)
            {
                return new BakeResult
                {
                    Success = false,
                    Message = JointKeeperLocalization.Tr("bake.err_no_humanoid_bones"),
                };
            }

            // 関節設定が 1 つでも有効か事前確認（再ベイク時に Revert だけ走って中断するのを防ぐ）
            var enabledJoints = new List<(JointId jointId, JointConfig config)>();
            for (int i = 0; i < AllJoints.Length; i++)
            {
                var cfg = comp.ResolveConfig(AllJoints[i]);
                if (cfg.enabled)
                    enabledJoints.Add((AllJoints[i], cfg));
            }
            if (enabledJoints.Count == 0)
            {
                return new BakeResult
                {
                    Success = false,
                    Message = JointKeeperLocalization.Tr("bake.err_no_joints_enabled"),
                };
            }

            // 既にベイク済みの場合は再ベイクとして一度復元
            bool isRebake = comp.IsBaked;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(isRebake ? "JointKeeper Re-Bake" : "JointKeeper Bake & Replace");
            int undoGroup = Undo.GetCurrentGroup();

            if (isRebake)
            {
                RevertComponentInternal(comp, deleteGeneratedAssets: true, collapseUndo: false);
            }

            var targets = JointKeeperTargetCollector.CollectTargets(comp, avatarRoot, logOutOfAvatarWarning: true);
            if (targets.Count == 0)
            {
                Undo.CollapseUndoOperations(undoGroup);
                return new BakeResult
                {
                    Success = false,
                    Message = JointKeeperLocalization.Tr("bake.err_no_targets"),
                };
            }

            // ヒンジ軸実測は HideAndDontSave クローン上で安全に実行（元シーンの Transform を一切汚さない）
            var measuredAxes = JointKeeperPreviewState.GetCachedMeasuredAxes(avatarRoot.gameObject)
                ?? JointKeeperPreviewState.ResampleMeasuredAxesSafe(avatarRoot.gameObject)
                ?? new Dictionary<JointId, Vector3>();

            Vector3 avatarForward = JointFrameSolver.ComputeAvatarForward(animator, avatarRoot, out string fwdWarning, humanoidBones);
            if (!string.IsNullOrEmpty(fwdWarning))
                Debug.LogWarning($"[JointKeeper] {fwdWarning}", avatarRoot);
            Vector3 avatarUp = avatarRoot.up;

            var kinematicCtx = AvatarKinematicContext.Build(avatarRoot.gameObject, animator, humanoidBones);
            var mergeMappings = PreviewBoneResolver.BuildMergeArmatureMappings(avatarRoot.gameObject);
            var buildState = new JointKeeperBuildState();

            var bakedRecords = new List<BakedRendererRecord>();
            var createdHelpers = new List<GameObject>();
            int totalAffectedVerts = 0;
            bool detectedAnyScaleAdjuster = false;

            for (int tIdx = 0; tIdx < targets.Count; tIdx++)
            {
                SkinnedMeshRenderer smr = targets[tIdx];
                if (smr == null || smr.sharedMesh == null) continue;

                Mesh origMesh = smr.sharedMesh;
                Transform[] origBones = smr.bones;
                Matrix4x4[] origBindposes = origMesh.bindposes;
                BoneWeight[] origWeights = origMesh.boneWeights;

                if (origBones == null || origBones.Length == 0
                    || origBindposes == null || origBindposes.Length == 0
                    || origWeights == null || origWeights.Length == 0)
                {
                    continue;
                }

                // 5 本以上インフルエンス警告
                var bonesPerVert = origMesh.GetBonesPerVertex();
                if (bonesPerVert.IsCreated)
                {
                    int over4 = 0;
                    for (int i = 0; i < bonesPerVert.Length; i++)
                    {
                        if (bonesPerVert[i] > 4) over4++;
                    }
                    if (over4 > 0)
                    {
                        Debug.LogWarning(
                            $"[JointKeeper] '{smr.name}' に 5 ボーン以上の頂点が {over4} 個あります。VRChat 仕様に従い上位 4 本へ正規化してベイクします。",
                            smr);
                    }
                }

                // MA Scale Adjuster がある場合はビルド後と一致するよう実効スケールを織り込む
                var scaleAdjusterMap = PreviewBoneResolver.CollectScaleAdjusterScales(origBones);
                if (scaleAdjusterMap.Count > 0)
                    detectedAnyScaleAdjuster = true;

                int bsCount = origMesh.blendShapeCount;
                float[] bsWeights = bsCount > 0 ? new float[bsCount] : null;
                for (int i = 0; i < bsCount; i++)
                    bsWeights[i] = smr.GetBlendShapeWeight(i);

                Vector3[] worldVerts = SkinningEvaluator.EvaluateWorldVerticesCpu(
                    origMesh, origBones, bsWeights, null, null, scaleAdjusterMap);

                var workingBones = new List<Transform>(origBones);
                var workingBindposes = new List<Matrix4x4>(origBindposes);
                BoneWeight[] workingWeights = (BoneWeight[])origWeights.Clone();
                bool anyModified = false;
                int smrAffectedVerts = 0;

                for (int jIdx = 0; jIdx < enabledJoints.Count; jIdx++)
                {
                    var (jointId, cfg) = enabledJoints[jIdx];

                    Transform avatarUpper = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetUpperHumanoidBone());
                    Transform avatarLower = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetLowerHumanoidBone());
                    Transform avatarChild = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetChildHumanoidBone());
                    if (avatarUpper == null || avatarLower == null)
                        continue;

                    // MA Merge Armature を使用する衣装なら衣装側 Armature の対応ボーンを優先（衣装単体で完結）
                    bool useOutfitArmature = PreviewBoneResolver.TryResolveOutfitJointBones(
                        origBones, mergeMappings, avatarUpper, avatarLower, avatarChild,
                        out Transform targetUpper, out Transform targetLower, out Transform targetChild);

                    Vector3? childPos = targetChild != null
                        ? targetChild.position
                        : (avatarChild != null ? avatarChild.position : (Vector3?)null);
                    Vector3? measured = measuredAxes.TryGetValue(jointId, out var ax) ? ax : (Vector3?)null;

                    JointFrame baseFrame = JointFrameSolver.Solve(
                        jointId, targetUpper.position, targetLower.position, childPos,
                        avatarForward, avatarUp, measured);
                    JointFrame frame = baseFrame.WithOffsets(cfg.pivotOffset, cfg.outwardAngleOffset);

                    // 関節 × 機能単位の二重適用防止 (FEATURE_ForearmTwist §1.5)
                    if (JointKeeperGeneratedNames.ReferencesHelperBone(origBones, jointId))
                    {
                        Debug.LogWarning($"[JointKeeper] Bake '{smr.name}' は既に {jointId} の補助ボーンを参照しているため、この関節をスキップしました。", smr);
                        continue;
                    }

                    var groups = TwistBoneClassifier.Classify(
                        jointId, targetUpper, targetLower, targetChild,
                        workingBones.ToArray(), cfg, kinematicCtx,
                        workingWeights, worldVerts, frame);

                    foreach (var w in groups.DiagnosticWarnings)
                        Debug.LogWarning($"[JointKeeper] {w}", smr);

                    if (groups.ProximalBoneIndices.Count == 0 || groups.DistalBoneIndices.Count == 0)
                    {
                        Debug.Log($"[JointKeeper] Bake '{smr.name}' ({jointId}) スキップ: G_P={groups.ProximalBoneIndices.Count}, G_D={groups.DistalBoneIndices.Count}", smr);
                        continue;
                    }
                    if (groups.ProximalReferenceBoneIndex < 0 || groups.DistalReferenceBoneIndex < 0)
                        continue;

                    int placeholderIdx = workingBones.Count;
                    var redParams = WeightRedistributionParams.FromConfig(frame, cfg);
                    var redResult = WeightRedistributor.Redistribute(
                        workingWeights,
                        worldVerts,
                        groups.ProximalBoneIndices,
                        groups.DistalBoneIndices,
                        groups.PrimaryHumanoidBoneIndices,
                        placeholderIdx,
                        in redParams);

                    if (redResult.Stats.AffectedVertices == 0)
                    {
                        Debug.Log($"[JointKeeper] Bake '{smr.name}' ({jointId}) スキップ: 影響頂点 0", smr);
                        continue;
                    }

                    var rigKey = new RigSignatureKey(
                        jointId,
                        groups.ProximalSource.GetInstanceID(),
                        groups.DistalSource.GetInstanceID(),
                        cfg.bendFollowRatio,
                        cfg.pushAt90Deg,
                        cfg.pivotOffset,
                        cfg.outwardAngleOffset);

                    int prevBoneCount = buildState.CreatedBoneCount;
                    Transform helperBone = buildState.GetOrCreateHelperBone(
                        rigKey,
                        frame,
                        groups.ProximalSource,
                        groups.DistalSource,
                        cfg.bendFollowRatio,
                        cfg.pushAt90Deg);

                    if (buildState.CreatedBoneCount > prevBoneCount && helperBone != null)
                    {
                        Undo.RegisterCreatedObjectUndo(helperBone.gameObject, "JointKeeper Create Helper Bone");
                        createdHelpers.Add(helperBone.gameObject);
                    }

                    // 近位/遠位ブレンド Bindpose 算出（MA Scale Adjuster スケールも織り込む）
                    int pRef = groups.ProximalReferenceBoneIndex;
                    int dRef = groups.DistalReferenceBoneIndex;

                    Matrix4x4 pWorld = workingBones[pRef].localToWorldMatrix;
                    if (scaleAdjusterMap.TryGetValue(workingBones[pRef], out Vector3 pScale))
                        pWorld = pWorld * Matrix4x4.Scale(pScale);

                    Matrix4x4 dWorld = workingBones[dRef].localToWorldMatrix;
                    if (scaleAdjusterMap.TryGetValue(workingBones[dRef], out Vector3 dScale))
                        dWorld = dWorld * Matrix4x4.Scale(dScale);

                    Matrix4x4 sp = pWorld * workingBindposes[pRef];
                    Matrix4x4 sd = dWorld * workingBindposes[dRef];

                    Matrix4x4 helperBindpose = SkinningEvaluator.ComputeHelperBindpose(
                        helperBone.localToWorldMatrix, sp, sd, Mathf.Clamp(cfg.bendFollowRatio, 0.1f, 0.9f));

                    workingBones.Add(helperBone);
                    workingBindposes.Add(helperBindpose);
                    workingWeights = redResult.NewWeights;
                    anyModified = true;
                    smrAffectedVerts += redResult.Stats.AffectedVertices;

                    // レスト姿勢ズレ検証
                    Vector3[] newWorldVerts = SkinningEvaluator.EvaluateWorldVerticesCpu(
                        origMesh, workingBones.ToArray(), bsWeights, workingWeights, workingBindposes.ToArray(), scaleAdjusterMap);
                    float maxDrift = SkinningEvaluator.MeasureMaxRestPoseDrift(worldVerts, newWorldVerts, redResult.ModifiedMask);
                    if (maxDrift > RestDriftWarningThresholdMeters)
                    {
                        Debug.LogWarning(
                            $"[JointKeeper] Bake '{smr.name}' ({jointId}) でレスト姿勢ズレ {maxDrift * 1000f:F2} mm を検出しました。",
                            smr);
                    }

                    string armatureKind = useOutfitArmature ? "衣装 Armature" : "アバター Armature";
                    Debug.Log(
                        $"[JointKeeper] Bake '{smr.name}' ({jointId}) 適用 [{armatureKind}]: 影響頂点={redResult.Stats.AffectedVertices}, 最大補助ウェイト={redResult.Stats.MaxHelperWeight:F2}, Src=({groups.ProximalSource.name}, {groups.DistalSource.name}), 補助ボーン={helperBone.name}, レストズレ={maxDrift * 1000f:F3} mm",
                        smr);
                }

                if (!anyModified)
                    continue;

                // 複製メッシュを作成して .asset として永続保存
                string outDir = ResolveOutputDirectory(origMesh, smr.gameObject.scene);
                EnsureAssetFolderExists(outDir);
                string baseName = SanitizeAssetFileName(origMesh.name);
                string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{outDir}/{baseName}_JointKeeper.asset");

                Mesh bakedMesh = UnityEngine.Object.Instantiate(origMesh);
                bakedMesh.name = $"{baseName}_JointKeeper";
                bakedMesh.bindposes = workingBindposes.ToArray();
                bakedMesh.boneWeights = workingWeights;

                AssetDatabase.CreateAsset(bakedMesh, assetPath);

                // SMR の bones と sharedMesh を差し替え（Prefab Instance オーバーライドも記録）
                Undo.RecordObject(smr, "JointKeeper Bake & Replace");
                smr.bones = workingBones.ToArray();
                smr.sharedMesh = bakedMesh;
                EditorUtility.SetDirty(smr);
                PrefabUtility.RecordPrefabInstancePropertyModifications(smr);

                bakedRecords.Add(new BakedRendererRecord
                {
                    renderer = smr,
                    originalMesh = origMesh,
                    originalBones = (Transform[])origBones.Clone(),
                    bakedMesh = bakedMesh,
                    bakedAssetPath = assetPath,
                    affectedVertexCount = smrAffectedVerts,
                });
                totalAffectedVerts += smrAffectedVerts;
            }

            if (bakedRecords.Count == 0)
            {
                Undo.CollapseUndoOperations(undoGroup);
                return new BakeResult
                {
                    Success = false,
                    Message = JointKeeperLocalization.Tr("bake.err_no_affected"),
                };
            }

            AssetDatabase.SaveAssets();

            Undo.RecordObject(comp, "JointKeeper Bake & Replace");
            if (comp.bakeRecord == null)
                comp.bakeRecord = new JointBakeRecord();
            comp.bakeRecord.isBaked = true;
            comp.bakeRecord.renderers = bakedRecords;
            comp.bakeRecord.createdHelperBones = createdHelpers;
            comp.bakeRecord.bakedWithScaleAdjuster = detectedAnyScaleAdjuster;
            comp.enabled = false;
            EditorUtility.SetDirty(comp);
            PrefabUtility.RecordPrefabInstancePropertyModifications(comp);

            Undo.CollapseUndoOperations(undoGroup);
            JointKeeperPreviewState.BumpStructureVersion();

            string summary = JointKeeperLocalization.Format(
                "bake.msg_complete", bakedRecords.Count, createdHelpers.Count, totalAffectedVerts);
            if (detectedAnyScaleAdjuster)
            {
                summary += JointKeeperLocalization.Tr("bake.msg_scale_adjuster_note");
            }
            if (comp.ResolveTwistConfig(JointId.LeftElbow).enabled || comp.ResolveTwistConfig(JointId.RightElbow).enabled)
            {
                // 前腕ツイスト分散は初期実装では NDMF ビルド（非破壊）専用 (FEATURE_ForearmTwist §1.4)
                summary += JointKeeperLocalization.Tr("bake.msg_twist_not_supported");
            }
            Debug.Log($"[JointKeeper] {summary}", comp);

            return new BakeResult
            {
                Success = true,
                Message = summary,
                ModifiedRendererCount = bakedRecords.Count,
                CreatedHelperBoneCount = createdHelpers.Count,
                TotalAffectedVertices = totalAffectedVerts,
                DetectedScaleAdjuster = detectedAnyScaleAdjuster,
            };
        }

        /// <summary>
        /// ベイク済み状態を元に戻し、元のメッシュ・ボーン配列を復元し、生成した補助ボーンと .asset を削除する。
        /// </summary>
        public static bool RevertComponent(JointKeeperComponent comp, bool deleteGeneratedAssets = true)
        {
            if (comp == null || !comp.IsBaked)
                return false;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("JointKeeper Revert Bake");
            return RevertComponentInternal(comp, deleteGeneratedAssets, collapseUndo: true);
        }

        /// <summary>
        /// ベイク結果を確定し、JointKeeperComponent だけを除去する（メッシュと補助ボーンは残る）。
        /// </summary>
        public static void FinalizeAndRemoveComponent(JointKeeperComponent comp)
        {
            if (comp == null) return;
            Undo.DestroyObjectImmediate(comp);
            JointKeeperPreviewState.BumpStructureVersion();
        }

        private static bool RevertComponentInternal(
            JointKeeperComponent comp,
            bool deleteGeneratedAssets,
            bool collapseUndo)
        {
            int undoGroup = Undo.GetCurrentGroup();
            var record = comp.bakeRecord;
            if (record == null || !record.isBaked)
                return false;

            var assetPathsToDelete = new List<string>();

            // 1. 全 SMR を元の bones / sharedMesh へ復元
            if (record.renderers != null)
            {
                for (int i = 0; i < record.renderers.Count; i++)
                {
                    var rec = record.renderers[i];
                    if (rec == null) continue;

                    if (!string.IsNullOrEmpty(rec.bakedAssetPath))
                        assetPathsToDelete.Add(rec.bakedAssetPath);

                    if (rec.renderer == null) continue;

                    Undo.RecordObject(rec.renderer, "JointKeeper Revert Bake");
                    if (rec.originalBones != null && rec.originalBones.Length > 0)
                        rec.renderer.bones = rec.originalBones;
                    if (rec.originalMesh != null)
                        rec.renderer.sharedMesh = rec.originalMesh;
                    EditorUtility.SetDirty(rec.renderer);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(rec.renderer);
                }
            }

            // 2. 生成した補助ボーン GameObject を削除
            if (record.createdHelperBones != null)
            {
                for (int i = record.createdHelperBones.Count - 1; i >= 0; i--)
                {
                    GameObject helperGo = record.createdHelperBones[i];
                    if (helperGo != null)
                        Undo.DestroyObjectImmediate(helperGo);
                }
            }

            // 3. コンポーネント状態を非ベイクへ戻し再有効化
            Undo.RecordObject(comp, "JointKeeper Revert Bake");
            record.isBaked = false;
            record.renderers?.Clear();
            record.createdHelperBones?.Clear();
            record.bakedWithScaleAdjuster = false;
            comp.enabled = true;
            EditorUtility.SetDirty(comp);
            PrefabUtility.RecordPrefabInstancePropertyModifications(comp);

            if (collapseUndo)
                Undo.CollapseUndoOperations(undoGroup);

            // 4. 生成した .asset ファイルを削除
            if (deleteGeneratedAssets && assetPathsToDelete.Count > 0)
            {
                for (int i = 0; i < assetPathsToDelete.Count; i++)
                {
                    string path = assetPathsToDelete[i];
                    if (!string.IsNullOrEmpty(path)
                        && path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                        && AssetDatabase.LoadAssetAtPath<Mesh>(path) != null)
                    {
                        AssetDatabase.DeleteAsset(path);
                    }
                }
                AssetDatabase.SaveAssets();
            }

            JointKeeperPreviewState.BumpStructureVersion();
            Debug.Log("[JointKeeper] ベイク状態を元に戻しました。", comp);
            return true;
        }

        internal static string ResolveOutputDirectory(Mesh origMesh, Scene scene)
        {
            string meshPath = origMesh != null ? AssetDatabase.GetAssetPath(origMesh) : null;
            string scenePath = scene.IsValid() ? scene.path : null;
            return ResolveOutputDirectoryFromPaths(meshPath, scenePath);
        }

        internal static string ResolveOutputDirectoryFromPaths(string meshAssetPath, string scenePath)
        {
            if (TryGetAssetsDirectory(meshAssetPath, out string meshDir))
                return meshDir;

            if (TryGetAssetsDirectory(scenePath, out string sceneDir))
                return sceneDir;

            return FallbackBakedFolder;
        }

        private static bool TryGetAssetsDirectory(string assetPath, out string directory)
        {
            directory = null;
            if (string.IsNullOrEmpty(assetPath))
                return false;

            string normalized = assetPath.Replace('\\', '/');
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return false;

            int lastSlash = normalized.LastIndexOf('/');
            if (lastSlash <= 0)
                return false;

            directory = normalized.Substring(0, lastSlash);
            return directory.Equals("Assets", StringComparison.OrdinalIgnoreCase)
                || directory.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
        }

        internal static string SanitizeAssetFileName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return "BakedMesh";

            string cleaned = rawName.Trim();
            if (cleaned.EndsWith(" (JointKeeper)", StringComparison.Ordinal))
                cleaned = cleaned.Substring(0, cleaned.Length - " (JointKeeper)".Length).Trim();
            if (cleaned.EndsWith("_JointKeeper", StringComparison.Ordinal))
                cleaned = cleaned.Substring(0, cleaned.Length - "_JointKeeper".Length).Trim();

            char[] invalid = Path.GetInvalidFileNameChars();
            var chars = cleaned.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (chars[i] == invalid[j])
                    {
                        chars[i] = '_';
                        break;
                    }
                }
            }
            string result = new string(chars).Trim();
            return string.IsNullOrEmpty(result) ? "BakedMesh" : result;
        }

        private static void EnsureAssetFolderExists(string assetFolderPath)
        {
            if (string.IsNullOrEmpty(assetFolderPath) || AssetDatabase.IsValidFolder(assetFolderPath))
                return;

            string normalized = assetFolderPath.Replace('\\', '/').TrimEnd('/');
            string[] parts = normalized.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static Animator FindAvatarAnimator(Transform t)
        {
            Transform cur = t;
            Animator found = null;
            while (cur != null)
            {
                var anim = cur.GetComponent<Animator>();
                if (anim != null && (anim.isHuman || (anim.avatar != null && anim.avatar.isValid && anim.avatar.isHuman)))
                    found = anim;
                cur = cur.parent;
            }
            return found;
        }
    }
}
