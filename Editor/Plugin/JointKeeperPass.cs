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
                    if (allComponents[i] != null && allComponents[i].enabled)
                        enabledComponents.Add(allComponents[i]);
                }

                if (enabledComponents.Count == 0)
                    return;

                var animator = context.AvatarRootObject.GetComponent<Animator>();
                if (animator == null)
                    animator = context.AvatarRootObject.GetComponentInChildren<Animator>(true);

                if (animator == null || !animator.isHuman)
                {
                    Debug.LogWarning("[JointKeeper] Humanoid Animator が見つからないため処理をスキップしました。", context.AvatarRootObject);
                    return;
                }

                // 2. アバター全体の PhysBone / Constraint 依存グラフを 1 回構築
                var kinematicCtx = AvatarKinematicContext.Build(context.AvatarRootObject, animator);

                // 2b. 関節ごとのベース JointFrame を 1 回だけ算出 (ADR-06)
                var measuredAxes = HingeAxisSampler.SampleAll(animator);
                Vector3 avatarForward = JointFrameSolver.ComputeAvatarForward(animator, context.AvatarRootTransform, out string fwdWarning);
                if (!string.IsNullOrEmpty(fwdWarning))
                    Debug.LogWarning($"[JointKeeper] {fwdWarning}", context.AvatarRootObject);
                Vector3 avatarUp = context.AvatarRootTransform.up;

                var baseFrames = new Dictionary<JointId, JointFrame>();
                foreach (var jointId in AllJoints)
                {
                    Transform upper = animator.GetBoneTransform(jointId.GetUpperHumanoidBone());
                    Transform lower = animator.GetBoneTransform(jointId.GetLowerHumanoidBone());
                    Transform child = animator.GetBoneTransform(jointId.GetChildHumanoidBone());
                    if (upper == null || lower == null) continue;

                    Vector3? childPos = child != null ? child.position : (Vector3?)null;
                    Vector3? measured = measuredAxes.TryGetValue(jointId, out var ax) ? ax : (Vector3?)null;

                    baseFrames[jointId] = JointFrameSolver.Solve(
                        jointId, upper.position, lower.position, childPos,
                        avatarForward, avatarUp, measured);
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

                foreach (var smrEntry in bySmr)
                {
                    ProcessRenderer(smrEntry.Key, smrEntry.Value, animator, kinematicCtx, baseFrames, buildState);
                }
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

        private static void ProcessRenderer(
            SkinnedMeshRenderer smr,
            List<(JointId jointId, OwnedAssignment assignment)> assignments,
            Animator animator,
            AvatarKinematicContext kinematicCtx,
            Dictionary<JointId, JointFrame> baseFrames,
            JointKeeperBuildState buildState)
        {
            if (smr == null || smr.sharedMesh == null)
                return;

            Mesh origMesh = smr.sharedMesh;
            Transform[] origBones = smr.bones;
            Matrix4x4[] origBindposes = origMesh.bindposes;
            BoneWeight[] origWeights = origMesh.boneWeights;

            if (origBones == null || origBones.Length == 0
                || origBindposes == null || origBindposes.Length == 0
                || origWeights == null || origWeights.Length == 0)
            {
                return;
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

            assignments.Sort((a, b) => a.jointId.CompareTo(b.jointId));

            foreach (var (jointId, owned) in assignments)
            {
                if (!baseFrames.TryGetValue(jointId, out JointFrame baseFrame))
                    continue;

                JointConfig cfg = owned.Config;
                Transform upper = animator.GetBoneTransform(jointId.GetUpperHumanoidBone());
                Transform lower = animator.GetBoneTransform(jointId.GetLowerHumanoidBone());
                Transform child = animator.GetBoneTransform(jointId.GetChildHumanoidBone());
                if (upper == null || lower == null)
                    continue;

                JointFrame frame = baseFrame.WithOffsets(cfg.pivotOffset, cfg.outwardAngleOffset);

                // 5a. 運動学的依存分類 (ADR-05)
                var groups = TwistBoneClassifier.Classify(
                    jointId, upper, lower, child,
                    workingBones.ToArray(), cfg, kinematicCtx,
                    workingWeights, worldVerts, frame);

                foreach (var w in groups.DiagnosticWarnings)
                    Debug.LogWarning($"[JointKeeper] {w}", smr);

                if (groups.ProximalBoneIndices.Count == 0 || groups.DistalBoneIndices.Count == 0)
                    continue;
                if (groups.ProximalReferenceBoneIndex < 0 || groups.DistalReferenceBoneIndex < 0)
                    continue;

                // 5c. 仮インデックスに対してウェイト再計算（未複製）
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
                    continue;

                // 5d. 遅延生成・共有レジストリ照会 (ADR-02, ADR-03)
                var rigKey = new RigSignatureKey(
                    jointId,
                    groups.ProximalSource.GetInstanceID(),
                    groups.DistalSource.GetInstanceID(),
                    cfg.bendFollowRatio,
                    cfg.pushAt90Deg,
                    cfg.pivotOffset,
                    cfg.outwardAngleOffset);

                Transform helperBone = buildState.GetOrCreateHelperBone(
                    rigKey,
                    frame,
                    groups.ProximalSource,
                    groups.DistalSource,
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

                // 5f. レスト姿勢ズレ検証
                Vector3[] newWorldVerts = SkinningEvaluator.EvaluateWorldVerticesCpu(
                    origMesh, workingBones.ToArray(), bsWeights, workingWeights, workingBindposes.ToArray());
                float maxDrift = SkinningEvaluator.MeasureMaxRestPoseDrift(worldVerts, newWorldVerts, redResult.ModifiedMask);
                if (maxDrift > RestDriftWarningThresholdMeters)
                {
                    Debug.LogWarning(
                        $"[JointKeeper] '{smr.name}' ({jointId}) でレスト姿勢ズレ {maxDrift * 1000f:F2} mm を検出しました（衣装ボーンが bind pose から大きく動かされている可能性があります）。",
                        smr);
                }
            }

            if (!anyModified)
                return;

            // 6. 複製メッシュへ書き込み（bindposes → boneWeights → smr.bones の順序を厳守）
            Mesh clonedMesh = Object.Instantiate(origMesh);
            clonedMesh.name = origMesh.name + " (JointKeeper)";
            clonedMesh.bindposes = workingBindposes.ToArray();
            clonedMesh.boneWeights = workingWeights;

            smr.bones = workingBones.ToArray();
            smr.sharedMesh = clonedMesh;
            ObjectRegistry.RegisterReplacedObject(origMesh, clonedMesh);
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
                        if (!resolved.enabled) continue;

                        var key = (smr, jointId);
                        var candidate = new OwnedAssignment
                        {
                            Component = comp,
                            Config = resolved,
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
            var result = new List<SkinnedMeshRenderer>();
            if (comp == null) return result;

            if (comp.targetMode == TargetRendererMode.ManualList)
            {
                if (comp.targetRenderers == null) return result;
                var seen = new HashSet<SkinnedMeshRenderer>();
                foreach (var smr in comp.targetRenderers)
                {
                    if (smr == null || !seen.Add(smr)) continue;
                    if (avatarRoot != null && !smr.transform.IsChildOf(avatarRoot))
                    {
                        Debug.LogWarning($"[JointKeeper] アバター外の SkinnedMeshRenderer '{smr.name}' が指定されているため無視しました。", comp);
                        continue;
                    }
                    result.Add(smr);
                }
                return result;
            }

            var excluded = new HashSet<SkinnedMeshRenderer>();
            if (comp.excludedRenderers != null)
            {
                foreach (var ex in comp.excludedRenderers)
                {
                    if (ex != null) excluded.Add(ex);
                }
            }

            var found = comp.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < found.Length; i++)
            {
                if (found[i] != null && !excluded.Contains(found[i]))
                    result.Add(found[i]);
            }
            return result;
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
