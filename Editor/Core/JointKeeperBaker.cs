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

            // 関節設定（ボリューム維持または前腕ツイスト分散）が 1 つでも有効か事前確認
            var enabledJoints = new List<(JointId jointId, JointConfig config, ForearmTwistConfig twistConfig)>();
            for (int i = 0; i < AllJoints.Length; i++)
            {
                JointId jid = AllJoints[i];
                var cfg = comp.ResolveConfig(jid);
                var twistCfg = comp.ResolveTwistConfig(jid);
                if (cfg.enabled || (jid.IsElbow() && twistCfg.enabled))
                    enabledJoints.Add((jid, cfg, twistCfg));
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

            // ヒンジ軸・前腕ロール軸の実測は HideAndDontSave クローン上で安全に実行（元シーンの Transform を一切汚さない）
            var measuredAxes = JointKeeperPreviewState.GetCachedMeasuredAxes(avatarRoot.gameObject);
            var twistSamples = JointKeeperPreviewState.GetCachedTwistSamples(avatarRoot.gameObject);
            if (measuredAxes == null || twistSamples == null)
            {
                measuredAxes = JointKeeperPreviewState.ResampleMeasuredAxesSafe(avatarRoot.gameObject)
                    ?? measuredAxes
                    ?? new Dictionary<JointId, Vector3>();
                twistSamples = JointKeeperPreviewState.GetCachedTwistSamples(avatarRoot.gameObject);
            }

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

                bool[] combinedModified = new bool[origWeights.Length];

                for (int jIdx = 0; jIdx < enabledJoints.Count; jIdx++)
                {
                    var (jointId, cfg, twistCfg) = enabledJoints[jIdx];

                    Transform avatarUpper = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetUpperHumanoidBone());
                    Transform avatarLower = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetLowerHumanoidBone());
                    Transform avatarChild = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetChildHumanoidBone());
                    if (avatarUpper == null || avatarLower == null)
                        continue;

                    // 関節 × 機能単位の二重適用防止 (FEATURE_ForearmTwist §1.5)
                    bool elbowOn = cfg.enabled;
                    bool twistOn = jointId.IsElbow() && twistCfg != null && twistCfg.enabled;
                    if (elbowOn && JointKeeperGeneratedNames.ReferencesHelperBone(origBones, jointId))
                    {
                        Debug.LogWarning($"[JointKeeper] Bake '{smr.name}' は既に {jointId} の補助ボーンを参照しているため、ボリューム補正をスキップしました。", smr);
                        elbowOn = false;
                    }
                    if (twistOn && JointKeeperGeneratedNames.ReferencesTwistBone(origBones, jointId))
                    {
                        Debug.LogWarning($"[JointKeeper] Bake '{smr.name}' は既に {jointId} の前腕ツイストボーンを参照しているため、前腕ツイスト分散をスキップしました。", smr);
                        twistOn = false;
                    }
                    if (!elbowOn && !twistOn)
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

                    Transform[] bonesSnapshot = workingBones.ToArray();
                    var groups = TwistBoneClassifier.Classify(
                        jointId, targetUpper, targetLower, targetChild,
                        bonesSnapshot, cfg, kinematicCtx,
                        workingWeights, worldVerts, frame);

                    foreach (var w in groups.DiagnosticWarnings)
                        Debug.LogWarning($"[JointKeeper] {w}", smr);

                    if (groups.ProximalBoneIndices.Count == 0 || groups.DistalBoneIndices.Count == 0
                        || groups.ProximalReferenceBoneIndex < 0 || groups.DistalReferenceBoneIndex < 0)
                    {
                        if (elbowOn)
                        {
                            Debug.Log($"[JointKeeper] Bake '{smr.name}' ({jointId}) 補助ボーン スキップ: G_P={groups.ProximalBoneIndices.Count}, G_D={groups.DistalBoneIndices.Count}", smr);
                            elbowOn = false;
                        }
                    }
                    if (!elbowOn && !twistOn)
                        continue;

                    // 1. 肘・膝補助ボーンのウェイト再計算（未生成）
                    int helperPlaceholderIdx = workingBones.Count;
                    WeightRedistributionResult redResult = null;
                    if (elbowOn)
                    {
                        var redParams = WeightRedistributionParams.FromConfig(frame, cfg);
                        redResult = WeightRedistributor.Redistribute(
                            workingWeights,
                            worldVerts,
                            groups.ProximalBoneIndices,
                            groups.DistalBoneIndices,
                            groups.PrimaryHumanoidBoneIndices,
                            helperPlaceholderIdx,
                            in redParams);

                        if (redResult.Stats.AffectedVertices == 0)
                        {
                            Debug.Log($"[JointKeeper] Bake '{smr.name}' ({jointId}) 補助ボーン スキップ: 影響頂点 0", smr);
                            redResult = null;
                        }
                    }

                    // 2. 前腕ツイスト分散の計画（未生成）
                    ForearmTwistPlan twistPlan = null;
                    ForearmTwistGeometry twistGeo = default;
                    if (twistOn)
                    {
                        BoneWeight[] twistInput = redResult != null ? redResult.NewWeights : workingWeights;
                        Transform childForGeo = targetChild != null ? targetChild : avatarChild;
                        twistPlan = PlanForearmTwistForBake(
                            smr, jointId, twistCfg, groups, kinematicCtx, bonesSnapshot,
                            twistInput, worldVerts, childForGeo, baseFrame, measuredAxes, twistSamples,
                            workingBones.Count + (redResult != null ? 1 : 0),
                            out twistGeo);
                    }
                    bool twistAffected = twistPlan != null && twistPlan.HasChanges;

                    if (redResult == null && !twistAffected)
                        continue;

                    string armatureKind = useOutfitArmature ? "衣装 Armature" : "アバター Armature";

                    // 3. 曲げ専用基準ボーン N を先に生成し、肘補助ボーンの遠位ソースに使う (ADR-10)
                    Transform twistBase = null;
                    if (twistAffected)
                    {
                        twistBase = buildState.GetOrCreateTwistBase(
                            jointId, twistGeo, groups.ProximalSource, groups.DistalSource,
                            out Transform createdAimTarget, out bool baseCreated);
                        if (baseCreated)
                        {
                            if (createdAimTarget != null)
                            {
                                Undo.RegisterCreatedObjectUndo(createdAimTarget.gameObject, "JointKeeper Create Twist Aim Target");
                                createdHelpers.Add(createdAimTarget.gameObject);
                            }
                            if (twistBase != null)
                            {
                                Undo.RegisterCreatedObjectUndo(twistBase.gameObject, "JointKeeper Create Twist Base Bone");
                                createdHelpers.Add(twistBase.gameObject);
                            }
                        }
                    }

                    // 4. 肘・膝補助ボーンの確定
                    if (redResult != null)
                    {
                        Transform helperDistal = (twistBase != null && twistCfg.bendOnlyElbowHelper)
                            ? twistBase
                            : groups.DistalSource;

                        var rigKey = new RigSignatureKey(
                            jointId,
                            groups.ProximalSource.GetInstanceID(),
                            helperDistal.GetInstanceID(),
                            cfg.bendFollowRatio,
                            cfg.pushAt90Deg,
                            cfg.pivotOffset,
                            cfg.outwardAngleOffset);

                        int prevBoneCount = buildState.CreatedBoneCount;
                        Transform helperBone = buildState.GetOrCreateHelperBone(
                            rigKey,
                            frame,
                            groups.ProximalSource,
                            helperDistal,
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
                        for (int v = 0; v < combinedModified.Length; v++)
                            combinedModified[v] |= redResult.ModifiedMask[v];

                        Debug.Log(
                            $"[JointKeeper] Bake '{smr.name}' ({jointId}) 補助ボーン適用 [{armatureKind}]: 影響頂点={redResult.Stats.AffectedVertices}, 最大補助ウェイト={redResult.Stats.MaxHelperWeight:F2}, Src=({groups.ProximalSource.name}, {helperDistal.name}), 補助ボーン={helperBone.name}",
                            smr);
                    }

                    // 5. 前腕ツイスト分散ボーンの確定（MA Scale Adjuster スケールも織り込む）
                    if (twistAffected)
                    {
                        int rRef = twistPlan.RigidReferenceBoneIndex;
                        Matrix4x4 rWorld = workingBones[rRef].localToWorldMatrix;
                        if (scaleAdjusterMap.TryGetValue(workingBones[rRef], out Vector3 rScale))
                            rWorld = rWorld * Matrix4x4.Scale(rScale);
                        Matrix4x4 sRef = rWorld * workingBindposes[rRef];

                        float[] knots = twistPlan.Knots;
                        Transform capturedBase = twistBase;
                        Transform distalSource = groups.DistalSource;
                        ForearmTwistGeometry geo = twistGeo;

                        workingWeights = ForearmTwistProcessor.Commit(
                            twistPlan,
                            j =>
                            {
                                if (j == 0) return capturedBase;
                                if (knots[j] >= 1f - 1e-5f) return distalSource;
                                Transform knot = buildState.GetOrCreateTwistKnot(
                                    jointId, geo, capturedBase, distalSource, knots[j], out bool knotCreated);
                                if (knotCreated && knot != null)
                                {
                                    Undo.RegisterCreatedObjectUndo(knot.gameObject, "JointKeeper Create Twist Knot Bone");
                                    createdHelpers.Add(knot.gameObject);
                                }
                                return knot;
                            },
                            workingBones,
                            workingBindposes,
                            sRef);

                        anyModified = true;
                        smrAffectedVerts += twistPlan.Result.Stats.AffectedVertices;
                        for (int v = 0; v < combinedModified.Length; v++)
                            combinedModified[v] |= twistPlan.Result.ModifiedMask[v];

                        Debug.Log(
                            $"[JointKeeper] Bake '{smr.name}' ({jointId}) 前腕ツイスト適用 [{armatureKind}]: 影響頂点={twistPlan.Result.Stats.AffectedVertices}, ノット={knots.Length}, 4ボーン丸め={twistPlan.Result.Stats.QuantizedCount}, ロール軸={(twistGeo.RollAxisMeasured ? "実測" : "Hand 方向")}",
                            smr);
                    }

                    // 6. レスト姿勢ズレ検証
                    Vector3[] newWorldVerts = SkinningEvaluator.EvaluateWorldVerticesCpu(
                        origMesh, workingBones.ToArray(), bsWeights, workingWeights, workingBindposes.ToArray(), scaleAdjusterMap);
                    float maxDrift = SkinningEvaluator.MeasureMaxRestPoseDrift(worldVerts, newWorldVerts, combinedModified);
                    if (maxDrift > RestDriftWarningThresholdMeters)
                    {
                        Debug.LogWarning(
                            $"[JointKeeper] Bake '{smr.name}' ({jointId}) でレスト姿勢ズレ {maxDrift * 1000f:F2} mm を検出しました。",
                            smr);
                    }
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

        private static ForearmTwistPlan PlanForearmTwistForBake(
            SkinnedMeshRenderer smr,
            JointId jointId,
            ForearmTwistConfig twistCfg,
            JointBoneGroups groups,
            AvatarKinematicContext kinematicCtx,
            Transform[] smrBones,
            BoneWeight[] inputWeights,
            Vector3[] worldVerts,
            Transform child,
            JointFrame baseFrame,
            Dictionary<JointId, Vector3> measuredAxes,
            Dictionary<JointId, TwistAxisSample> twistSamples,
            int placeholderBase,
            out ForearmTwistGeometry geometry)
        {
            geometry = default;
            if (groups.DistalRigidBoneIndices.Count == 0)
            {
                Debug.Log($"[JointKeeper] Bake '{smr.name}' ({jointId}) 前腕ツイスト スキップ: 剛体遠位ボーンへのウェイトがありません。", smr);
                return null;
            }

            var diag = ForearmTwistProcessor.Analyze(inputWeights, smrBones, groups, kinematicCtx, null);
            if (diag.HasExistingTwistRig && !twistCfg.applyEvenIfExistingTwistRig)
            {
                Debug.LogWarning(
                    $"[JointKeeper] Bake '{smr.name}' ({jointId}) は既存の前腕ツイストボーン '{diag.ExistingRigBoneName}' にウェイトが乗っているため、前腕ツイスト分散をスキップしました（{diag.ExistingRigVertexCount} 頂点）。",
                    smr);
                return null;
            }
            if (diag.RigidVertexCount == 0)
                return null;

            if (diag.ForearmPhysBoneVertexCount > 0)
            {
                Debug.LogWarning(
                    $"[JointKeeper] Bake '{smr.name}' ({jointId}) の前腕にある PhysBone ({string.Join(", ", diag.ForearmPhysBoneNames)}) は元のロールで動き続けるため、まわりの袖とずれる場合があります（{diag.ForearmPhysBoneVertexCount} 頂点）。",
                    smr);
            }
            if (diag.HandWeightVertexCount > 0 && twistCfg.twistFollow < 1f - 1e-4f)
            {
                Debug.LogWarning(
                    $"[JointKeeper] Bake '{smr.name}' ({jointId}) は手首から先にもウェイトがある（{diag.HandWeightVertexCount} 頂点）ため、ひねり追従率 {twistCfg.twistFollow:F2} では手首にねじれの段差が出ます。",
                    smr);
            }

            Vector3? measuredRoll = null;
            if (twistSamples != null && twistSamples.TryGetValue(jointId, out var sample) && sample.HasAxis)
                measuredRoll = sample.Axis;
            Vector3 hinge = (measuredAxes != null && measuredAxes.TryGetValue(jointId, out var h)) ? h : baseFrame.HingeAxis;

            if (!ForearmTwistGeometry.TryCompute(groups.DistalSource, child, measuredRoll, hinge, baseFrame.LimbLength, out geometry, out string error))
            {
                Debug.LogWarning($"[JointKeeper] Bake '{smr.name}' ({jointId}) 前腕ツイスト スキップ: {error}", smr);
                return null;
            }

            var lookup = new Dictionary<Transform, int>();
            for (int i = 0; i < smrBones.Length; i++)
            {
                if (smrBones[i] != null && !lookup.ContainsKey(smrBones[i]))
                    lookup[smrBones[i]] = i;
            }

            return ForearmTwistProcessor.Plan(
                inputWeights, worldVerts, groups, twistCfg, geometry, placeholderBase,
                t => (t != null && lookup.TryGetValue(t, out int idx)) ? idx : -1);
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
