using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using Dennokoworks.JointKeeper;
#if JOINT_KEEPER_VRCSDK
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;
#endif

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// アバター単位で事前収集する PhysBone 除外セットおよび Constraint 依存グラフ。
    /// </summary>
    internal sealed class AvatarKinematicContext
    {
        internal readonly struct ConstraintInfo
        {
            public readonly List<Transform> Sources;
            public readonly int AffectedRotationAxes;
            public readonly bool AffectsPosition;

            public ConstraintInfo(List<Transform> sources, int affectedRotationAxes, bool affectsPosition)
            {
                Sources = sources;
                AffectedRotationAxes = affectedRotationAxes;
                AffectsPosition = affectsPosition;
            }
        }

        public readonly HashSet<Transform> PhysBoneSet = new HashSet<Transform>();
        public readonly Dictionary<Transform, List<ConstraintInfo>> ConstraintsByTarget =
            new Dictionary<Transform, List<ConstraintInfo>>();
        public readonly HashSet<Transform> AllHumanoidBones = new HashSet<Transform>();

        public static AvatarKinematicContext Build(GameObject avatarRoot, Animator animator)
        {
            var ctx = new AvatarKinematicContext();
            if (avatarRoot == null) return ctx;

            // 1. 全 Humanoid ボーン収集
            if (animator != null && animator.isHuman)
            {
                for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
                {
                    var bone = animator.GetBoneTransform((HumanBodyBones)i);
                    if (bone != null)
                        ctx.AllHumanoidBones.Add(bone);
                }
            }

            // 2. 全 VRCPhysBone 対象ボーン収集
#if JOINT_KEEPER_VRCSDK
            var physBones = avatarRoot.GetComponentsInChildren<VRCPhysBone>(true);
            for (int i = 0; i < physBones.Length; i++)
            {
                var pb = physBones[i];
                if (pb == null) continue;
                Transform root = pb.rootTransform != null ? pb.rootTransform : pb.transform;
                var ignored = new HashSet<Transform>();
                if (pb.ignoreTransforms != null)
                {
                    for (int j = 0; j < pb.ignoreTransforms.Count; j++)
                    {
                        if (pb.ignoreTransforms[j] != null)
                            ignored.Add(pb.ignoreTransforms[j]);
                    }
                }
                CollectSubtreeExcluding(root, ignored, ctx.PhysBoneSet);
            }
#endif

            // 3. 全 VRCConstraintBase 収集
#if JOINT_KEEPER_VRCSDK
            var vrcConstraints = avatarRoot.GetComponentsInChildren<VRCConstraintBase>(true);
            for (int i = 0; i < vrcConstraints.Length; i++)
            {
                var c = vrcConstraints[i];
                if (c == null) continue;
                Transform target = c.TargetTransform != null ? c.TargetTransform : c.transform;
                var sources = new List<Transform>();
                if (c.Sources != null)
                {
                    for (int s = 0; s < c.Sources.Count; s++)
                    {
                        var src = c.Sources[s];
                        if (src.SourceTransform != null && src.Weight > 1e-5f)
                            sources.Add(src.SourceTransform);
                    }
                }
                if (sources.Count == 0) continue;

                InspectVrcConstraintAxes(c, out int rotAxes, out bool affectsPos);
                AddConstraintInfo(ctx.ConstraintsByTarget, target, new ConstraintInfo(sources, rotAxes, affectsPos));
            }
#endif

            // 4. Unity 標準 IConstraint 収集（MA ConvertConstraints 前やテスト環境用）
            var unityConstraints = avatarRoot.GetComponentsInChildren<IConstraint>(true);
            for (int i = 0; i < unityConstraints.Length; i++)
            {
                var c = unityConstraints[i];
                var comp = c as Component;
                if (c == null || comp == null) continue;

                var sources = new List<Transform>();
                for (int s = 0; s < c.sourceCount; s++)
                {
                    var src = c.GetSource(s);
                    if (src.sourceTransform != null && src.weight > 1e-5f)
                        sources.Add(src.sourceTransform);
                }
                if (sources.Count == 0) continue;

                InspectUnityConstraintAxes(c, out int rotAxes, out bool affectsPos);
                AddConstraintInfo(ctx.ConstraintsByTarget, comp.transform, new ConstraintInfo(sources, rotAxes, affectsPos));
            }

            return ctx;
        }

        public bool IsConstraintDriven(Transform t)
        {
            return t != null && ConstraintsByTarget.ContainsKey(t);
        }

        private static void CollectSubtreeExcluding(Transform root, HashSet<Transform> ignored, HashSet<Transform> output)
        {
            if (root == null || ignored.Contains(root)) return;
            output.Add(root);
            for (int i = 0; i < root.childCount; i++)
                CollectSubtreeExcluding(root.GetChild(i), ignored, output);
        }

        private static void AddConstraintInfo(
            Dictionary<Transform, List<ConstraintInfo>> map,
            Transform target,
            ConstraintInfo info)
        {
            if (!map.TryGetValue(target, out var list))
            {
                list = new List<ConstraintInfo>();
                map[target] = list;
            }
            list.Add(info);
        }

#if JOINT_KEEPER_VRCSDK
        private static void InspectVrcConstraintAxes(VRCConstraintBase c, out int rotAxes, out bool affectsPos)
        {
            rotAxes = 0;
            affectsPos = false;

            if (c is VRC.Dynamics.ManagedTypes.VRCRotationConstraintBase rot)
            {
                rotAxes = (rot.AffectsRotationX ? 1 : 0)
                        + (rot.AffectsRotationY ? 1 : 0)
                        + (rot.AffectsRotationZ ? 1 : 0);
            }
            else if (c is VRC.Dynamics.ManagedTypes.VRCParentConstraintBase parent)
            {
                rotAxes = (parent.AffectsRotationX ? 1 : 0)
                        + (parent.AffectsRotationY ? 1 : 0)
                        + (parent.AffectsRotationZ ? 1 : 0);
                affectsPos = parent.AffectsPositionX || parent.AffectsPositionY || parent.AffectsPositionZ;
            }
            else if (c is VRC.Dynamics.ManagedTypes.VRCPositionConstraintBase pos)
            {
                affectsPos = pos.AffectsPositionX || pos.AffectsPositionY || pos.AffectsPositionZ;
            }
            else if (c is VRC.Dynamics.ManagedTypes.VRCAimConstraintBase aim)
            {
                rotAxes = (aim.AffectsRotationX ? 1 : 0)
                        + (aim.AffectsRotationY ? 1 : 0)
                        + (aim.AffectsRotationZ ? 1 : 0);
            }
            else if (c is VRC.Dynamics.ManagedTypes.VRCLookAtConstraintBase)
            {
                rotAxes = 3;
            }
        }
#endif

        private static void InspectUnityConstraintAxes(IConstraint c, out int rotAxes, out bool affectsPos)
        {
            rotAxes = 0;
            affectsPos = false;

            if (c is RotationConstraint rot)
            {
                rotAxes = CountAxes(rot.rotationAxis);
            }
            else if (c is ParentConstraint parent)
            {
                rotAxes = CountAxes(parent.rotationAxis);
                affectsPos = parent.translationAxis != Axis.None;
            }
            else if (c is PositionConstraint pos)
            {
                affectsPos = pos.translationAxis != Axis.None;
            }
            else if (c is AimConstraint aim)
            {
                rotAxes = CountAxes(aim.rotationAxis);
            }
            else if (c is LookAtConstraint)
            {
                rotAxes = 3;
            }
        }

        private static int CountAxes(Axis axis)
        {
            int count = 0;
            if ((axis & Axis.X) != 0) count++;
            if ((axis & Axis.Y) != 0) count++;
            if ((axis & Axis.Z) != 0) count++;
            return count;
        }
    }

    /// <summary>
    /// 運動学的依存分類 (ADR-05, §5.1) により、SMR ボーンを近位群 G_P / 遠位群 G_D / 外部群 G_O に分類する。
    /// 名前・位置ヒューリスティクスは分類に使用しない。
    /// </summary>
    internal static class TwistBoneClassifier
    {
        [Flags]
        private enum DepFlags
        {
            None = 0,
            Proximal = 1,
            Distal = 2,
            Other = 4,
        }

        public static JointBoneGroups Classify(
            JointId jointId,
            Transform upper,
            Transform lower,
            Transform child,
            Transform[] smrBones,
            JointConfig config,
            AvatarKinematicContext kinematicCtx,
            BoneWeight[] boneWeights = null,
            Vector3[] worldVertices = null,
            JointFrame? jointFrame = null,
            Dictionary<Transform, Transform> boneAliasMap = null)
        {
            var result = new JointBoneGroups
            {
                UpperHumanoid = upper,
                LowerHumanoid = lower,
                ChildHumanoid = child,
            };

            if (upper == null || lower == null || smrBones == null || smrBones.Length == 0)
                return result;

            var ctx = kinematicCtx ?? new AvatarKinematicContext();

            // 1. 他 Humanoid ボーン集合（Upper / Lower を除く）
            var otherHumanoids = new HashSet<Transform>(ctx.AllHumanoidBones);
            otherHumanoids.Remove(upper);
            otherHumanoids.Remove(lower);

            // 2. 直列チェーン検出 (Upper → ... → Lower, Lower → ... → Child)
            var serialProximal = new List<Transform>(); // Lower 側から Upper へ向かう順
            var serialProximalSet = new HashSet<Transform>();
            {
                Transform cur = lower.parent;
                while (cur != null && cur != upper)
                {
                    serialProximal.Add(cur);
                    serialProximalSet.Add(cur);
                    cur = cur.parent;
                }
                if (cur != upper)
                {
                    serialProximal.Clear();
                    serialProximalSet.Clear();
                }
            }

            var serialDistal = new List<Transform>();
            var serialDistalSet = new HashSet<Transform>();
            if (child != null)
            {
                Transform cur = child.parent;
                while (cur != null && cur != lower)
                {
                    serialDistal.Add(cur);
                    serialDistalSet.Add(cur);
                    cur = cur.parent;
                }
                if (cur != lower)
                {
                    serialDistal.Clear();
                    serialDistalSet.Clear();
                }
            }

            // 3. 基本所属テーブル構築
            var proximalSet = new HashSet<Transform> { upper };
            foreach (var b in serialProximal) proximalSet.Add(b);

            var distalSet = new HashSet<Transform> { lower };
            foreach (var b in serialDistal) distalSet.Add(b);

            // Child サブツリー（Hand/Foot 配下）は曲げ運動に関して D 依存だが、G_D 自体は除外
            var childSubtree = new HashSet<Transform>();
            if (child != null) CollectAllDescendants(child, childSubtree);

            var lowerSubtree = new HashSet<Transform>();
            CollectAllDescendants(lower, lowerSubtree);

            // 4. 運動学的依存による候補分類 (autoDetectTwistBones == true 時)
            bool autoDetect = config == null || config.autoDetectTwistBones;
            var boneInfoMap = new Dictionary<Transform, ClassifiedBoneInfo>
            {
                [upper] = new ClassifiedBoneInfo(upper, BoneGroupKind.Proximal, ctx.IsConstraintDriven(upper), "Humanoid Upper"),
                [lower] = new ClassifiedBoneInfo(lower, BoneGroupKind.Distal, ctx.IsConstraintDriven(lower), "Humanoid Lower"),
            };

            foreach (var b in serialProximal)
                boneInfoMap[b] = new ClassifiedBoneInfo(b, BoneGroupKind.Proximal, ctx.IsConstraintDriven(b), "Serial Proximal Chain");
            foreach (var b in serialDistal)
                boneInfoMap[b] = new ClassifiedBoneInfo(b, BoneGroupKind.Distal, ctx.IsConstraintDriven(b), "Serial Distal Chain");

            if (autoDetect)
            {
                // Upper サブツリー（Lower サブツリー除外）と Lower サブツリー（Child サブツリー除外）
                var candidates = new List<(Transform bone, bool isUnderLower)>();
                CollectCandidates(upper, lower, otherHumanoids, ctx.PhysBoneSet, false, candidates);
                CollectCandidates(lower, child, otherHumanoids, ctx.PhysBoneSet, true, candidates);

                foreach (var (bone, isUnderLower) in candidates)
                {
                    if (proximalSet.Contains(bone) || distalSet.Contains(bone))
                        continue;

                    var visited = new HashSet<Transform>();
                    DepFlags dep = EvaluateDependency(
                        bone, upper, lower, proximalSet, distalSet, childSubtree, lowerSubtree,
                        ctx, visited, out int maxRotAxes, out bool anyPosAffected);

                    bool isConstrained = ctx.IsConstraintDriven(bone);

                    if (dep == DepFlags.Proximal)
                    {
                        proximalSet.Add(bone);
                        boneInfoMap[bone] = new ClassifiedBoneInfo(bone, BoneGroupKind.Proximal, isConstrained,
                            isConstrained ? "Kinematic Dep {P} (Constraint)" : "Rigid Child of Proximal");
                    }
                    else if (dep == DepFlags.Distal)
                    {
                        distalSet.Add(bone);
                        boneInfoMap[bone] = new ClassifiedBoneInfo(bone, BoneGroupKind.Distal, isConstrained,
                            isConstrained ? "Kinematic Dep {D} (Constraint)" : "Rigid Child of Distal");
                    }
                    else if (dep == (DepFlags.Proximal | DepFlags.Distal))
                    {
                        if (maxRotAxes <= 1 && !anyPosAffected)
                        {
                            // 1 軸（ロール）のみ伝播する上腕ツイスト等 → 親側グループへ所属
                            if (isUnderLower)
                            {
                                distalSet.Add(bone);
                                boneInfoMap[bone] = new ClassifiedBoneInfo(bone, BoneGroupKind.Distal, isConstrained, "1-Axis Roll Twist (Distal)");
                            }
                            else
                            {
                                proximalSet.Add(bone);
                                boneInfoMap[bone] = new ClassifiedBoneInfo(bone, BoneGroupKind.Proximal, isConstrained, "1-Axis Roll Twist (Proximal)");
                            }
                        }
                        else
                        {
                            // 既存肘/膝補助ボーンと判定 → 自動除外＋警告
                            result.ExistingJointHelpers.Add(bone);
                            boneInfoMap[bone] = new ClassifiedBoneInfo(bone, BoneGroupKind.ExistingHelper, isConstrained, "Existing Joint Helper (Excluded)");
                            result.DiagnosticWarnings.Add(
                                $"既存の関節補助ボーン '{bone.name}' ({jointId}) を検出したため自動対象から除外しました。");
                        }
                    }
                    // Other を含む場合は G_O（除外）
                }
            }

            // 5. ユーザー手動オーバーライド適用
            if (config != null)
            {
                if (config.extraProximalBones != null)
                {
                    foreach (var b in config.extraProximalBones)
                    {
                        if (b == null) continue;
                        distalSet.Remove(b);
                        proximalSet.Add(b);
                        boneInfoMap[b] = new ClassifiedBoneInfo(b, BoneGroupKind.Proximal, ctx.IsConstraintDriven(b), "Manual Override (Proximal)");
                    }
                }
                if (config.extraDistalBones != null)
                {
                    foreach (var b in config.extraDistalBones)
                    {
                        if (b == null) continue;
                        proximalSet.Remove(b);
                        distalSet.Add(b);
                        boneInfoMap[b] = new ClassifiedBoneInfo(b, BoneGroupKind.Distal, ctx.IsConstraintDriven(b), "Manual Override (Distal)");
                    }
                }
                if (config.excludedBones != null)
                {
                    foreach (var b in config.excludedBones)
                    {
                        if (b == null) continue;
                        proximalSet.Remove(b);
                        distalSet.Remove(b);
                        boneInfoMap.Remove(b);
                    }
                }
            }

            // 6. SMR.bones 上のインデックス集合を構築（プレビュー時のエイリアスマップ対応）
            int upperSmrIdx = -1;
            int lowerSmrIdx = -1;
            var smrBoneIndexLookup = new Dictionary<Transform, int>();

            for (int i = 0; i < smrBones.Length; i++)
            {
                Transform rawBone = smrBones[i];
                if (rawBone == null) continue;
                Transform resolved = (boneAliasMap != null && boneAliasMap.TryGetValue(rawBone, out var mapped))
                    ? mapped
                    : rawBone;

                if (!smrBoneIndexLookup.ContainsKey(resolved))
                    smrBoneIndexLookup[resolved] = i;

                if (resolved == upper)
                {
                    upperSmrIdx = i;
                    result.PrimaryHumanoidBoneIndices.Add(i);
                }
                else if (resolved == lower)
                {
                    lowerSmrIdx = i;
                    result.PrimaryHumanoidBoneIndices.Add(i);
                }

                if (proximalSet.Contains(resolved))
                    result.ProximalBoneIndices.Add(i);
                else if (distalSet.Contains(resolved))
                    result.DistalBoneIndices.Add(i);
            }

            foreach (var kv in boneInfoMap)
                result.ClassifiedBones.Add(kv.Value);

            // 7. Constraint ソース確定
            result.ProximalSource = (lower.parent != null && proximalSet.Contains(lower.parent))
                ? lower.parent
                : upper;

            if (config != null
                && config.distalSourceMode == DistalSourceMode.CustomOverride
                && config.distalSourceOverride != null)
            {
                result.DistalSource = config.distalSourceOverride;
            }
            else
            {
                result.DistalSource = lower;
            }

            result.ProximalSourceIsConstraintDriven = ctx.IsConstraintDriven(result.ProximalSource);
            result.DistalSourceIsConstraintDriven = ctx.IsConstraintDriven(result.DistalSource);

            // 8. 基準 Bindpose ボーン確定 (ADR-07)
            result.ProximalReferenceBoneIndex = SelectReferenceBoneIndex(
                upperSmrIdx, serialProximal, result.ProximalBoneIndices, smrBoneIndexLookup,
                boneWeights, worldVertices, jointFrame, config);

            result.DistalReferenceBoneIndex = SelectReferenceBoneIndex(
                lowerSmrIdx, serialDistal, result.DistalBoneIndices, smrBoneIndexLookup,
                boneWeights, worldVertices, jointFrame, config);

            // 片側が見つからない場合はもう一方でフォールバック
            if (result.ProximalReferenceBoneIndex < 0)
                result.ProximalReferenceBoneIndex = result.DistalReferenceBoneIndex;
            if (result.DistalReferenceBoneIndex < 0)
                result.DistalReferenceBoneIndex = result.ProximalReferenceBoneIndex;

            return result;
        }

        private static int SelectReferenceBoneIndex(
            int primaryHumanoidIdx,
            List<Transform> serialChain,
            HashSet<int> groupIndices,
            Dictionary<Transform, int> smrLookup,
            BoneWeight[] boneWeights,
            Vector3[] worldVertices,
            JointFrame? jointFrame,
            JointConfig config)
        {
            if (primaryHumanoidIdx >= 0 && groupIndices.Contains(primaryHumanoidIdx))
                return primaryHumanoidIdx;

            if (serialChain != null)
            {
                for (int i = 0; i < serialChain.Count; i++)
                {
                    if (smrLookup.TryGetValue(serialChain[i], out int idx) && groupIndices.Contains(idx))
                        return idx;
                }
            }

            if (groupIndices.Count == 0)
                return -1;

            // G 内から ROI 内総ウェイト最大のボーンを選択
            if (boneWeights != null && boneWeights.Length > 0)
            {
                var weightTotals = new Dictionary<int, float>();
                foreach (int idx in groupIndices)
                    weightTotals[idx] = 0f;

                bool useRoi = worldVertices != null
                    && worldVertices.Length == boneWeights.Length
                    && jointFrame.HasValue
                    && config != null;
                JointFrame frame = jointFrame.GetValueOrDefault();
                float axialR = useRoi ? Mathf.Max(config.axialRadiusScale * frame.LimbLength, 1e-4f) : 1f;
                float radialR = useRoi ? Mathf.Max(config.radialRadiusScale * frame.LimbLength, 1e-4f) : 1f;

                for (int v = 0; v < boneWeights.Length; v++)
                {
                    if (useRoi)
                    {
                        Vector3 rel = worldVertices[v] - frame.JointCenter;
                        float z = Vector3.Dot(rel, frame.LimbAxis);
                        float rPerp = (rel - frame.LimbAxis * z).magnitude;
                        float dNormSqr = (z / axialR) * (z / axialR) + (rPerp / radialR) * (rPerp / radialR);
                        if (dNormSqr >= 1f) continue;
                    }

                    BoneWeight bw = boneWeights[v];
                    AddIfInGroup(bw.boneIndex0, bw.weight0, weightTotals);
                    AddIfInGroup(bw.boneIndex1, bw.weight1, weightTotals);
                    AddIfInGroup(bw.boneIndex2, bw.weight2, weightTotals);
                    AddIfInGroup(bw.boneIndex3, bw.weight3, weightTotals);
                }

                int bestIdx = -1;
                float bestWeight = -1f;
                foreach (var kv in weightTotals)
                {
                    if (kv.Value > bestWeight)
                    {
                        bestWeight = kv.Value;
                        bestIdx = kv.Key;
                    }
                }
                if (bestIdx >= 0) return bestIdx;
            }

            foreach (int idx in groupIndices)
                return idx;
            return -1;
        }

        private static void AddIfInGroup(int boneIndex, float weight, Dictionary<int, float> totals)
        {
            if (weight > 0f && totals.TryGetValue(boneIndex, out float cur))
                totals[boneIndex] = cur + weight;
        }

        private static void CollectAllDescendants(Transform root, HashSet<Transform> output)
        {
            if (root == null) return;
            output.Add(root);
            for (int i = 0; i < root.childCount; i++)
                CollectAllDescendants(root.GetChild(i), output);
        }

        private static void CollectCandidates(
            Transform parent,
            Transform stopSubtreeRoot,
            HashSet<Transform> otherHumanoids,
            HashSet<Transform> physBones,
            bool isUnderLower,
            List<(Transform bone, bool isUnderLower)> output)
        {
            if (parent == null) return;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (c == null || c == stopSubtreeRoot) continue;
                if (otherHumanoids.Contains(c) || physBones.Contains(c)) continue;

                output.Add((c, isUnderLower));
                CollectCandidates(c, stopSubtreeRoot, otherHumanoids, physBones, isUnderLower, output);
            }
        }

        private static DepFlags EvaluateDependency(
            Transform bone,
            Transform upper,
            Transform lower,
            HashSet<Transform> proximalSet,
            HashSet<Transform> distalSet,
            HashSet<Transform> childSubtree,
            HashSet<Transform> lowerSubtree,
            AvatarKinematicContext ctx,
            HashSet<Transform> visited,
            out int maxRotAxes,
            out bool anyPosAffected)
        {
            maxRotAxes = 0;
            anyPosAffected = false;

            if (bone == null || !visited.Add(bone))
                return DepFlags.None;

            if (proximalSet.Contains(bone))
                return DepFlags.Proximal;
            if (distalSet.Contains(bone) || childSubtree.Contains(bone))
                return DepFlags.Distal;

            DepFlags dep = DepFlags.None;

            // 1. 親チェーン上の依存（Upper まで遡る）
            Transform cur = bone;
            while (cur != null)
            {
                if (ctx.ConstraintsByTarget.TryGetValue(cur, out var constraints))
                {
                    for (int i = 0; i < constraints.Count; i++)
                    {
                        var info = constraints[i];
                        if (info.AffectedRotationAxes > maxRotAxes)
                            maxRotAxes = info.AffectedRotationAxes;
                        if (info.AffectsPosition)
                            anyPosAffected = true;

                        for (int s = 0; s < info.Sources.Count; s++)
                        {
                            var src = info.Sources[s];
                            dep |= EvaluateSourceDependency(
                                src, upper, lower, proximalSet, distalSet, childSubtree, lowerSubtree, ctx, visited);
                        }
                    }
                }

                Transform parent = cur.parent;
                if (parent == null)
                {
                    dep |= DepFlags.Other;
                    break;
                }
                if (proximalSet.Contains(parent))
                {
                    dep |= DepFlags.Proximal;
                    break;
                }
                if (distalSet.Contains(parent) || childSubtree.Contains(parent))
                {
                    dep |= DepFlags.Distal;
                    break;
                }
                if (parent == upper)
                {
                    dep |= DepFlags.Proximal;
                    break;
                }
                if (parent == lower)
                {
                    dep |= DepFlags.Distal;
                    break;
                }
                cur = parent;
            }

            return dep;
        }

        private static DepFlags EvaluateSourceDependency(
            Transform src,
            Transform upper,
            Transform lower,
            HashSet<Transform> proximalSet,
            HashSet<Transform> distalSet,
            HashSet<Transform> childSubtree,
            HashSet<Transform> lowerSubtree,
            AvatarKinematicContext ctx,
            HashSet<Transform> visited)
        {
            if (src == null) return DepFlags.None;
            if (proximalSet.Contains(src) || src == upper) return DepFlags.Proximal;
            if (distalSet.Contains(src) || src == lower || childSubtree.Contains(src)) return DepFlags.Distal;

            // Lower サブツリー内の未分類ボーンなら再帰評価
            if (lowerSubtree.Contains(src) || IsDescendantOf(src, upper))
            {
                return EvaluateDependency(
                    src, upper, lower, proximalSet, distalSet, childSubtree, lowerSubtree,
                    ctx, visited, out _, out _);
            }

            return DepFlags.Other;
        }

        private static bool IsDescendantOf(Transform t, Transform ancestor)
        {
            Transform cur = t;
            while (cur != null)
            {
                if (cur == ancestor) return true;
                cur = cur.parent;
            }
            return false;
        }
    }
}
