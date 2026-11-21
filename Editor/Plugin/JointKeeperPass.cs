#if JOINT_KEEPER_NDMF
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// NDMF Transforming フェーズ（MA 後・AAO 前）で JointKeeperComponent を処理する本パス (§4.2)。
    /// </summary>
    internal sealed class JointKeeperPass : Pass<JointKeeperPass>
    {
        public override string DisplayName => "JointKeeper";

        private const float RestDriftWarningThresholdMeters = 0.0005f; // 0.5 mm

        private sealed class OwnedAssignment
        {
            public JointKeeperComponent Component;
            public JointConfig Config;
            public ForearmTwistConfig TwistConfig;
            public bool IsManual;
            public int HierarchyDepth;
            public int ScanOrder;
        }

        private static readonly JointId[] AllJoints =
        {
            JointId.LeftElbow,
            JointId.RightElbow,
            JointId.LeftKnee,
            JointId.RightKnee,
        };

        protected override void Execute(BuildContext context)
        {
            var allComponents = context.AvatarRootObject.GetComponentsInChildren<JointKeeperComponent>(true);
            if (allComponents.Length == 0)
                return;

            try
            {
                var enabledComponents = new List<JointKeeperComponent>(allComponents.Length);
                for (int i = 0; i < allComponents.Length; i++)
                {
                    if (allComponents[i] != null && allComponents[i].enabled && !allComponents[i].IsBaked)
                        enabledComponents.Add(allComponents[i]);
                }

                if (enabledComponents.Count == 0)
                    return;

                var animator = context.AvatarRootObject.GetComponent<Animator>();
                if (animator == null)
                    animator = context.AvatarRootObject.GetComponentInChildren<Animator>(true);

                bool isHumanoid = animator != null
                    && (animator.isHuman || (animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman));
                if (!isHumanoid)
                {
                    Debug.LogWarning("[JointKeeper] Humanoid Animator が見つからないため処理をスキップしました。", context.AvatarRootObject);
                    return;
                }

                // 1b. Humanoid ボーン解決（MA RebindHumanoidAvatarPass 直後や非アクティブ時の GetBoneTransform==null 対策）
                var humanoidBones = HumanoidBoneResolver.ResolveAll(animator, out bool usedFallback);
                if (usedFallback)
                {
                    Debug.Log($"[JointKeeper] Avatar.humanDescription から Humanoid ボーン ({humanoidBones.Count} 本) を解決しました。", context.AvatarRootObject);
                }

                // 2. アバター全体の PhysBone / Constraint 依存グラフを 1 回構築
                var kinematicCtx = AvatarKinematicContext.Build(context.AvatarRootObject, animator, humanoidBones);

                // 2b. 関節ごとのベース JointFrame を 1 回だけ算出 (ADR-06)
                var measuredAxes = HingeAxisSampler.SampleAll(animator, humanoidBones, out var twistSamples);
                Vector3 avatarForward = JointFrameSolver.ComputeAvatarForward(animator, context.AvatarRootTransform, out string fwdWarning, humanoidBones);
                if (!string.IsNullOrEmpty(fwdWarning))
                    Debug.LogWarning($"[JointKeeper] {fwdWarning}", context.AvatarRootObject);
                Vector3 avatarUp = context.AvatarRootTransform.up;

                var baseFrames = new Dictionary<JointId, JointFrame>();
                foreach (var jointId in AllJoints)
                {
                    Transform upper = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetUpperHumanoidBone());
                    Transform lower = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetLowerHumanoidBone());
                    Transform child = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetChildHumanoidBone());
                    if (upper == null || lower == null) continue;

                    Vector3? childPos = child != null ? child.position : (Vector3?)null;
                    Vector3? measured = measuredAxes.TryGetValue(jointId, out var ax) ? ax : (Vector3?)null;

                    baseFrames[jointId] = JointFrameSolver.Solve(
                        jointId, upper.position, lower.position, childPos,
                        avatarForward, avatarUp, measured);
                }

                if (baseFrames.Count == 0)
                {
                    Debug.LogWarning("[JointKeeper] 肘・膝の Humanoid ボーンを 1 つも解決できなかったため処理をスキップしました。", context.AvatarRootObject);
                    return;
                }

                // 3. 所有権解決 (ADR-04, §4.3)
                var ownership = ResolveOwnership(enabledComponents, context.AvatarRootTransform);
                if (ownership.Count == 0)
                    return;

                // 4. SMR ごとにグループ化して処理
                var bySmr = new Dictionary<SkinnedMeshRenderer, List<(JointId jointId, OwnedAssignment assignment)>>();
                foreach (var kv in ownership)
                {
                    if (!bySmr.TryGetValue(kv.Key.smr, out var list))
                    {
                        list = new List<(JointId, OwnedAssignment)>();
                        bySmr[kv.Key.smr] = list;
                    }
                    list.Add((kv.Key.jointId, kv.Value));
                }

                var buildState = context.GetState<JointKeeperBuildState>();
                int modifiedSmrCount = 0;
                int totalAffectedVerts = 0;

                foreach (var smrEntry in bySmr)
                {
                    int affected = ProcessRenderer(context, smrEntry.Key, smrEntry.Value, humanoidBones, kinematicCtx, baseFrames, measuredAxes, twistSamples, buildState);
                    if (affected > 0)
                    {
                        modifiedSmrCount++;
                        totalAffectedVerts += affected;
                    }
                }

                Debug.Log(
                    $"[JointKeeper] ビルド適用完了: 対象 SMR {bySmr.Count} 件中 {modifiedSmrCount} 件を更新, 生成補助ボーン {buildState.CreatedBoneCount} 本, 前腕ツイストボーン {buildState.CreatedTwistBoneCount} 本, 影響頂点計 {totalAffectedVerts}",
                    context.AvatarRootObject);
            }
            finally
            {
                // Invariant 10: 成否に関わらず全 JointKeeperComponent を確実に除去
                for (int i = 0; i < allComponents.Length; i++)
                {
                    if (allComponents[i] != null)
                        Object.DestroyImmediate(allComponents[i]);
                }
            }
        }

        private static int ProcessRenderer(
            BuildContext context,
            SkinnedMeshRenderer smr,
            List<(JointId jointId, OwnedAssignment assignment)> assignments,
            Dictionary<HumanBodyBones, Transform> humanoidBones,
            AvatarKinematicContext kinematicCtx,
            Dictionary<JointId, JointFrame> baseFrames,
            Dictionary<JointId, Vector3> measuredAxes,
            Dictionary<JointId, TwistAxisSample> twistSamples,
            JointKeeperBuildState buildState)
        {
            if (smr == null || smr.sharedMesh == null)
                return 0;

            Mesh origMesh = smr.sharedMesh;
            Transform[] origBones = smr.bones;
            Matrix4x4[] origBindposes = origMesh.bindposes;
            BoneWeight[] origWeights = origMesh.boneWeights;

            if (origBones == null || origBones.Length == 0
                || origBindposes == null || origBindposes.Length == 0
                || origWeights == null || origWeights.Length == 0)
            {
                return 0;
            }

            // 5 本以上インフルエンスの検出ログ (§4.2)
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
                        $"[JointKeeper] '{smr.name}' に 5 ボーン以上の頂点が {over4} 個あります。VRChat 仕様に従い上位 4 本へ正規化して処理します。",
                        smr);
                }
            }

            // レスト姿勢ワールド頂点配列を 1 回だけ評価 (§5.3)
            int bsCount = origMesh.blendShapeCount;
            float[] bsWeights = bsCount > 0 ? new float[bsCount] : null;
            for (int i = 0; i < bsCount; i++)
                bsWeights[i] = smr.GetBlendShapeWeight(i);

            Vector3[] worldVerts = SkinningEvaluator.EvaluateWorldVerticesCpu(origMesh, origBones, bsWeights);

            var workingBones = new List<Transform>(origBones);
            var workingBindposes = new List<Matrix4x4>(origBindposes);
            BoneWeight[] workingWeights = (BoneWeight[])origWeights.Clone();
            bool anyModified = false;
            int smrAffectedVerts = 0;

            assignments.Sort((a, b) => a.jointId.CompareTo(b.jointId));

            bool[] combinedModified = new bool[origWeights.Length];

            foreach (var (jointId, owned) in assignments)
            {
                if (!baseFrames.TryGetValue(jointId, out JointFrame baseFrame))
                    continue;

                JointConfig cfg = owned.Config;
                ForearmTwistConfig twistCfg = owned.TwistConfig;
                Transform upper = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetUpperHumanoidBone());
                Transform lower = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetLowerHumanoidBone());
                Transform child = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetChildHumanoidBone());
                if (upper == null || lower == null)
                    continue;

                // 関節 × 機能単位の二重適用防止 (FEATURE_ForearmTwist §1.5)
                bool elbowOn = cfg.enabled;
                bool twistOn = jointId.IsElbow() && twistCfg != null && twistCfg.enabled;
                if (elbowOn && JointKeeperGeneratedNames.ReferencesHelperBone(origBones, jointId))
                {
                    Debug.LogWarning($"[JointKeeper] '{smr.name}' は既に {jointId} の補助ボーンを参照しているため、この関節のボリューム補正をスキップしました（Bake 済みの可能性があります）。", smr);
                    elbowOn = false;
                }
                if (twistOn && JointKeeperGeneratedNames.ReferencesTwistBone(origBones, jointId))
                {
                    Debug.LogWarning($"[JointKeeper] '{smr.name}' は既に {jointId} の前腕ツイストボーンを参照しているため、前腕ツイスト分散をスキップしました。", smr);
                    twistOn = false;
                }
                if (!elbowOn && !twistOn)
                    continue;

                JointFrame frame = baseFrame.WithOffsets(cfg.pivotOffset, cfg.outwardAngleOffset);

                // 5a. 運動学的依存分類 (ADR-05)
                Transform[] bonesSnapshot = workingBones.ToArray();
                var groups = TwistBoneClassifier.Classify(
                    jointId, upper, lower, child,
                    bonesSnapshot, cfg, kinematicCtx,
                    workingWeights, worldVerts, frame);

                foreach (var w in groups.DiagnosticWarnings)
                    Debug.LogWarning($"[JointKeeper] {w}", smr);

                if (groups.ProximalBoneIndices.Count == 0 || groups.DistalBoneIndices.Count == 0)
                {
                    Debug.Log($"[JointKeeper] '{smr.name}' ({jointId}) スキップ: G_P={groups.ProximalBoneIndices.Count}, G_D={groups.DistalBoneIndices.Count}", smr);
                    continue;
                }
                if (groups.ProximalReferenceBoneIndex < 0 || groups.DistalReferenceBoneIndex < 0)
                    continue;

                // 5c. 肘補助ボーン: 仮インデックスに対してウェイト再計算（未複製）
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
                        Debug.Log($"[JointKeeper] '{smr.name}' ({jointId}) 肘補助: 影響頂点 0 (G_P={groups.ProximalBoneIndices.Count}, G_D={groups.DistalBoneIndices.Count})", smr);
                        redResult = null;
                    }
                }

                // 5c'. 前腕ツイスト分散: 肘処理後のウェイトに対して計画（ボーン未生成）(FEATURE_ForearmTwist §5.1)
                ForearmTwistPlan twistPlan = null;
                ForearmTwistGeometry twistGeo = default;
                if (twistOn)
                {
                    BoneWeight[] twistInput = redResult != null ? redResult.NewWeights : workingWeights;
                    twistPlan = PlanForearmTwist(
                        smr, jointId, twistCfg, groups, kinematicCtx, bonesSnapshot,
                        twistInput, worldVerts, child, baseFrame, measuredAxes, twistSamples,
                        workingBones.Count + (redResult != null ? 1 : 0),
                        out twistGeo);
                }
                bool twistAffected = twistPlan != null && twistPlan.HasChanges;

                if (redResult == null && !twistAffected)
                    continue;

                // 5d. N を先に生成し、肘補助ボーンの遠位ソースに使う (ADR-10, §8-B6)
                Transform twistBase = twistAffected
                    ? buildState.GetOrCreateTwistBase(jointId, twistGeo, groups.ProximalSource, groups.DistalSource)
                    : null;

                if (redResult != null)
                {
                    Transform helperDistal = (twistBase != null && twistCfg.bendOnlyElbowHelper) ? twistBase : groups.DistalSource;

                    // 遅延生成・共有レジストリ照会 (ADR-02, ADR-03)
                    var rigKey = new RigSignatureKey(
                        jointId,
                        groups.ProximalSource.GetInstanceID(),
                        helperDistal.GetInstanceID(),
                        cfg.bendFollowRatio,
                        cfg.pushAt90Deg,
                        cfg.pivotOffset,
                        cfg.outwardAngleOffset);

                    Transform helperBone = buildState.GetOrCreateHelperBone(
                        rigKey,
                        frame,
                        groups.ProximalSource,
                        helperDistal,
                        cfg.bendFollowRatio,
                        cfg.pushAt90Deg);

                    // 5e. 近位/遠位ブレンド Bindpose 算出 (ADR-07)
                    int pRef = groups.ProximalReferenceBoneIndex;
                    int dRef = groups.DistalReferenceBoneIndex;
                    Matrix4x4 sp = workingBones[pRef].localToWorldMatrix * workingBindposes[pRef];
                    Matrix4x4 sd = workingBones[dRef].localToWorldMatrix * workingBindposes[dRef];
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
                        $"[JointKeeper] '{smr.name}' ({jointId}) 肘補助 適用: 影響頂点={redResult.Stats.AffectedVertices}, 最大補助ウェイト={redResult.Stats.MaxHelperWeight:F2}, Src=({groups.ProximalSource.name}, {helperDistal.name}), 補助ボーン={helperBone.name}",
                        smr);
                }

                if (twistAffected)
                {
                    int rRef = twistPlan.RigidReferenceBoneIndex;
                    Matrix4x4 sRef = workingBones[rRef].localToWorldMatrix * workingBindposes[rRef];
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
                            return buildState.GetOrCreateTwistKnot(jointId, geo, capturedBase, distalSource, knots[j]);
                        },
                        workingBones,
                        workingBindposes,
                        sRef);
                    anyModified = true;
                    smrAffectedVerts += twistPlan.Result.Stats.AffectedVertices;
                    for (int v = 0; v < combinedModified.Length; v++)
                        combinedModified[v] |= twistPlan.Result.ModifiedMask[v];

                    Debug.Log(
                        $"[JointKeeper] '{smr.name}' ({jointId}) 前腕ツイスト 適用: 影響頂点={twistPlan.Result.Stats.AffectedVertices}, ノット={knots.Length}, 4ボーン丸め={twistPlan.Result.Stats.QuantizedCount}, ロール軸={(twistGeo.RollAxisMeasured ? "実測" : "Hand 方向")}",
                        smr);
                }

                // 5f. レスト姿勢ズレ検証
                Vector3[] newWorldVerts = SkinningEvaluator.EvaluateWorldVerticesCpu(
                    origMesh, workingBones.ToArray(), bsWeights, workingWeights, workingBindposes.ToArray());
                float maxDrift = SkinningEvaluator.MeasureMaxRestPoseDrift(worldVerts, newWorldVerts, combinedModified);
                if (maxDrift > RestDriftWarningThresholdMeters)
                {
                    Debug.LogWarning(
                        $"[JointKeeper] '{smr.name}' ({jointId}) でレスト姿勢ズレ {maxDrift * 1000f:F2} mm を検出しました（衣装ボーンが bind pose から大きく動かされている可能性があります）。",
                        smr);
                }
            }

            if (!anyModified)
                return 0;

            // 6. 複製メッシュへ書き込み（bindposes → boneWeights → smr.bones の順序を厳守）
            Mesh clonedMesh = Object.Instantiate(origMesh);
            clonedMesh.name = origMesh.name + " (JointKeeper)";
            clonedMesh.bindposes = workingBindposes.ToArray();
            clonedMesh.boneWeights = workingWeights;

            smr.bones = workingBones.ToArray();
            smr.sharedMesh = clonedMesh;
            ObjectRegistry.RegisterReplacedObject(origMesh, clonedMesh);
            context.AssetSaver.SaveAsset(clonedMesh);
            return smrAffectedVerts;
        }

        /// <summary>
        /// 前腕ツイスト分散の診断・警告・計画 (FEATURE_ForearmTwist §1.3, §5, §8-B2, §8-B3)。適用不可なら null。
        /// </summary>
        private static ForearmTwistPlan PlanForearmTwist(
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
                Debug.Log($"[JointKeeper] '{smr.name}' ({jointId}) 前腕ツイスト スキップ: 剛体遠位ボーンへのウェイトがありません。", smr);
                return null;
            }

            var diag = ForearmTwistProcessor.Analyze(inputWeights, smrBones, groups, kinematicCtx, null);
            if (diag.HasExistingTwistRig && !twistCfg.applyEvenIfExistingTwistRig)
            {
                Debug.LogWarning(
                    $"[JointKeeper] '{smr.name}' ({jointId}) は既存の前腕ツイストボーン '{diag.ExistingRigBoneName}' にウェイトが乗っているため、前腕ツイスト分散をスキップしました（{diag.ExistingRigVertexCount} 頂点）。強制適用する場合は詳細設定の「既存ツイストリグがあっても適用」を有効にしてください。",
                    smr);
                return null;
            }
            if (diag.RigidVertexCount == 0)
                return null;
            if (diag.ForearmPhysBoneVertexCount > 0)
            {
                Debug.LogWarning(
                    $"[JointKeeper] '{smr.name}' ({jointId}) の前腕にある PhysBone ({string.Join(", ", diag.ForearmPhysBoneNames)}) は元のロールで動き続けるため、まわりの袖とずれる場合があります（{diag.ForearmPhysBoneVertexCount} 頂点）。",
                    smr);
            }
            if (diag.HandWeightVertexCount > 0 && twistCfg.twistFollow < 1f - 1e-4f)
            {
                Debug.LogWarning(
                    $"[JointKeeper] '{smr.name}' ({jointId}) は手首から先にもウェイトがある（{diag.HandWeightVertexCount} 頂点）ため、ひねり追従率 {twistCfg.twistFollow:F2} では手首にねじれの段差が出ます。手袋・素体などではひねり追従率 1.0 を推奨します。",
                    smr);
            }

            Vector3? measuredRoll = null;
            if (twistSamples != null && twistSamples.TryGetValue(jointId, out var sample) && sample.HasAxis)
                measuredRoll = sample.Axis;
            Vector3 hinge = (measuredAxes != null && measuredAxes.TryGetValue(jointId, out var h)) ? h : baseFrame.HingeAxis;

            if (!ForearmTwistGeometry.TryCompute(groups.DistalSource, child, measuredRoll, hinge, baseFrame.LimbLength, out geometry, out string error))
            {
                Debug.LogWarning($"[JointKeeper] '{smr.name}' ({jointId}) 前腕ツイスト スキップ: {error}", smr);
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

        private static Dictionary<(SkinnedMeshRenderer smr, JointId jointId), OwnedAssignment> ResolveOwnership(
            List<JointKeeperComponent> components,
            Transform avatarRoot)
        {
            var ownership = new Dictionary<(SkinnedMeshRenderer, JointId), OwnedAssignment>();

            for (int cIdx = 0; cIdx < components.Count; cIdx++)
            {
                JointKeeperComponent comp = components[cIdx];
                bool isManual = comp.targetMode == TargetRendererMode.ManualList;
                List<SkinnedMeshRenderer> targets = CollectTargets(comp, avatarRoot);

                foreach (var smr in targets)
                {
                    if (smr == null || smr.sharedMesh == null) continue;
                    int depth = ComputeAncestorDepth(smr.transform, comp.transform);

                    foreach (var jointId in AllJoints)
                    {
                        JointConfig resolved = comp.ResolveConfig(jointId);
                        ForearmTwistConfig twist = comp.ResolveTwistConfig(jointId);
                        if (!resolved.enabled && !(jointId.IsElbow() && twist.enabled)) continue;

                        var key = (smr, jointId);
                        var candidate = new OwnedAssignment
                        {
                            Component = comp,
                            Config = resolved,
                            TwistConfig = twist,
                            IsManual = isManual,
                            HierarchyDepth = depth,
                            ScanOrder = cIdx,
                        };

                        if (!ownership.TryGetValue(key, out OwnedAssignment existing))
                        {
                            ownership[key] = candidate;
                            continue;
                        }

                        int cmp = ComparePrecedence(candidate, existing);
                        if (cmp > 0)
                        {
                            ownership[key] = candidate;
                        }
                        else if (cmp == 0)
                        {
                            Debug.LogWarning(
                                $"[JointKeeper] '{smr.name}' ({jointId}) に同順位の JointKeeperComponent が重複しています。後方の '{comp.name}' を優先します。",
                                comp);
                            ownership[key] = candidate;
                        }
                    }
                }
            }

            return ownership;
        }

        private static int ComparePrecedence(OwnedAssignment candidate, OwnedAssignment existing)
        {
            if (candidate.IsManual != existing.IsManual)
                return candidate.IsManual ? 1 : -1;

            if (candidate.HierarchyDepth != existing.HierarchyDepth)
                return candidate.HierarchyDepth < existing.HierarchyDepth ? 1 : -1;

            return 0;
        }

        public static List<SkinnedMeshRenderer> CollectTargets(JointKeeperComponent comp, Transform avatarRoot)
        {
            return JointKeeperTargetCollector.CollectTargets(comp, avatarRoot, logOutOfAvatarWarning: true);
        }

        private static int ComputeAncestorDepth(Transform smrTransform, Transform componentTransform)
        {
            int depth = 0;
            Transform cur = smrTransform;
            while (cur != null)
            {
                if (cur == componentTransform) return depth;
                cur = cur.parent;
                depth++;
            }
            return int.MaxValue;
        }
    }
}
#endif
