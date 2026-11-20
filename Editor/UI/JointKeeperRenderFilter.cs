#if JOINT_KEEPER_NDMF
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// Phase 2 (Step 4): NDMF Preview System (IRenderFilter) による非破壊リアルタイム曲げ・ツイスト＆ヒートマッププレビュー (ADR-08, §7.3)。
    /// </summary>
    internal sealed class JointKeeperRenderFilter : IRenderFilter
    {
        private static readonly TogglablePreviewNode PreviewEnableNode = TogglablePreviewNode.Create(
            () => "JointKeeper Preview",
            qualifiedName: "dennokoworks.joint-keeper/Preview",
            true);

        public IEnumerable<TogglablePreviewNode> GetPreviewControlNodes()
        {
            yield return PreviewEnableNode;
        }

        public bool IsEnabled(ComputeContext context)
        {
            return context.Observe(PreviewEnableNode.IsEnabled);
        }

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            // Inspector のプレビュー ON/OFF とフレーム再計算のみ監視（曲げ/ツイストは OnFrame で 60fps 即時反映）
            bool previewEnabled = context.Observe(JointKeeperPreviewState.EnabledPublished);
            context.Observe(JointKeeperPreviewState.StructureVersionPublished);
            if (!previewEnabled)
                return ImmutableList<RenderGroup>.Empty;

            var groups = ImmutableList.CreateBuilder<RenderGroup>();

            foreach (var root in context.GetAvatarRoots())
            {
                if (root == null || context.ActiveInHierarchy(root) == false)
                    continue;

                var jkComps = context.GetComponentsInChildren<JointKeeperComponent>(root, true);
                if (jkComps.Length == 0)
                    continue;

                bool anyEnabled = false;
                for (int i = 0; i < jkComps.Length; i++)
                {
                    if (jkComps[i] != null && jkComps[i].enabled && !jkComps[i].IsBaked)
                    {
                        anyEnabled = true;
                        context.Observe(jkComps[i]);
                    }
                }
                if (!anyEnabled)
                    continue;

                // 曲げプレビュー中は素体貫通を確認するためアバター配下の全 SMR を同一グループに含める
                var smrs = context.GetComponentsInChildren<SkinnedMeshRenderer>(root, true);
                if (smrs.Length == 0)
                    continue;

                groups.Add(RenderGroup.For(smrs.Cast<Renderer>()).WithData(root, ReferenceEquals));
            }

            return groups.ToImmutable();
        }

        public Task<IRenderFilterNode> Instantiate(
            RenderGroup group,
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context)
        {
            GameObject avatarRoot = group.GetData<GameObject>();
            var node = new JointKeeperRenderFilterNode(avatarRoot, proxyPairs, context);
            return Task.FromResult<IRenderFilterNode>(node);
        }
    }

    internal sealed class JointKeeperRenderFilterNode : IRenderFilterNode
    {
        private sealed class RendererPreviewData
        {
            public Mesh AfterMesh;
            public Mesh WeightHeatmapMesh;
            public Mesh DiagHeatmapMesh;
            public Transform[] AfterShadowBones;
            public Transform[] BeforeShadowBones;
            public Material[] HeatmapMaterials;
            public bool HasJointKeeperChanges;
        }

        private static readonly JointId[] AllJoints =
        {
            JointId.LeftElbow,
            JointId.RightElbow,
            JointId.LeftKnee,
            JointId.RightKnee,
        };

        private readonly JointKeeperPreviewPose _pose;
        private readonly Dictionary<Renderer, RendererPreviewData> _dataByOriginal =
            new Dictionary<Renderer, RendererPreviewData>();
        private readonly List<Mesh> _createdMeshes = new List<Mesh>();
        private Material _heatmapMaterial;

        public RenderAspects WhatChanged => RenderAspects.Mesh | RenderAspects.Shapes | RenderAspects.Material;

        public JointKeeperRenderFilterNode(
            GameObject avatarRoot,
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context)
        {
            if (avatarRoot == null) return;

            var animator = avatarRoot.GetComponent<Animator>();
            if (animator == null) animator = avatarRoot.GetComponentInChildren<Animator>(true);
            if (animator == null) return;

            var humanoidBones = HumanoidBoneResolver.ResolveAll(animator, out _);
            if (humanoidBones.Count == 0) return;

            var allComps = avatarRoot.GetComponentsInChildren<JointKeeperComponent>(true);
            var enabledComps = new List<JointKeeperComponent>();
            for (int i = 0; i < allComps.Length; i++)
            {
                if (allComps[i] != null && allComps[i].enabled && !allComps[i].IsBaked)
                {
                    enabledComps.Add(allComps[i]);
                    context.Observe(allComps[i]);
                }
            }
            if (enabledComps.Count == 0) return;

            // Edit モード用 MA Merge Armature ボーン対応付け (§7.3)
            var aliasMap = PreviewBoneResolver.BuildAliasMap(avatarRoot);
            var kinematicCtx = AvatarKinematicContext.Build(avatarRoot, animator, humanoidBones);
            var measuredAxes = JointKeeperPreviewState.GetCachedMeasuredAxes(avatarRoot);

            Vector3 avatarForward = JointFrameSolver.ComputeAvatarForward(animator, avatarRoot.transform, out _, humanoidBones);
            Vector3 avatarUp = avatarRoot.transform.up;

            var baseFrames = new Dictionary<JointId, JointFrame>();
            foreach (var jointId in AllJoints)
            {
                Transform upper = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetUpperHumanoidBone());
                Transform lower = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetLowerHumanoidBone());
                Transform child = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetChildHumanoidBone());
                if (upper == null || lower == null) continue;

                Vector3? childPos = child != null ? child.position : (Vector3?)null;
                Vector3? measured = (measuredAxes != null && measuredAxes.TryGetValue(jointId, out var ax)) ? ax : (Vector3?)null;
                baseFrames[jointId] = JointFrameSolver.Solve(
                    jointId, upper.position, lower.position, childPos, avatarForward, avatarUp, measured);
            }

            _pose = new JointKeeperPreviewPose(avatarRoot, humanoidBones, baseFrames, aliasMap);
            _heatmapMaterial = JointKeeperHeatmapDrawer.CreateHeatmapMaterial();

            // SMR ごとの適用設定を解決
            var ownership = ResolvePreviewOwnership(enabledComps, avatarRoot.transform);

            foreach (var (orig, _) in proxyPairs)
            {
                var smr = orig as SkinnedMeshRenderer;
                if (smr == null || smr.sharedMesh == null || smr.bones == null || smr.bones.Length == 0)
                    continue;

                var data = BuildRendererPreviewData(smr, ownership, humanoidBones, kinematicCtx, baseFrames, aliasMap);
                _dataByOriginal[orig] = data;
            }
        }

        private RendererPreviewData BuildRendererPreviewData(
            SkinnedMeshRenderer smr,
            Dictionary<(SkinnedMeshRenderer, JointId), JointConfig> ownership,
            Dictionary<HumanBodyBones, Transform> humanoidBones,
            AvatarKinematicContext kinematicCtx,
            Dictionary<JointId, JointFrame> baseFrames,
            Dictionary<Transform, Transform> aliasMap)
        {
            var data = new RendererPreviewData
            {
                BeforeShadowBones = _pose.MapBonesToShadow(smr.bones),
            };

            Mesh origMesh = smr.sharedMesh;
            int vertCount = origMesh.vertexCount;
            if (vertCount == 0 || origMesh.bindposes == null || origMesh.bindposes.Length == 0)
                return data;

            Vector3[] worldVerts = SkinningEvaluator.EvaluateWorldVerticesCpu(smr);
            var workingBones = new List<Transform>(smr.bones);
            var workingBindposes = new List<Matrix4x4>(origMesh.bindposes);
            BoneWeight[] workingWeights = (BoneWeight[])origMesh.boneWeights.Clone();
            var combinedHelperWeights = new float[vertCount];
            var combinedTiers = new VertexSlotTier[vertCount];
            bool anyModified = false;

            foreach (var jointId in AllJoints)
            {
                if (!ownership.TryGetValue((smr, jointId), out JointConfig cfg))
                    continue;
                if (!baseFrames.TryGetValue(jointId, out JointFrame baseFrame))
                    continue;

                Transform upper = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetUpperHumanoidBone());
                Transform lower = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetLowerHumanoidBone());
                Transform child = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetChildHumanoidBone());
                if (upper == null || lower == null) continue;

                JointFrame frame = baseFrame.WithOffsets(cfg.pivotOffset, cfg.outwardAngleOffset);
                var groups = TwistBoneClassifier.Classify(
                    jointId, upper, lower, child, workingBones.ToArray(), cfg, kinematicCtx,
                    workingWeights, worldVerts, frame, aliasMap);

                if (groups.ProximalBoneIndices.Count == 0 || groups.DistalBoneIndices.Count == 0)
                    continue;
                if (groups.ProximalReferenceBoneIndex < 0 || groups.DistalReferenceBoneIndex < 0)
                    continue;

                int placeholderIdx = workingBones.Count;
                var redParams = WeightRedistributionParams.FromConfig(frame, cfg);
                var res = WeightRedistributor.Redistribute(
                    workingWeights, worldVerts,
                    groups.ProximalBoneIndices, groups.DistalBoneIndices, groups.PrimaryHumanoidBoneIndices,
                    placeholderIdx, in redParams);

                if (res.Stats.AffectedVertices == 0)
                    continue;

                var rigKey = new RigSignatureKey(
                    jointId,
                    groups.ProximalSource.GetInstanceID(),
                    groups.DistalSource.GetInstanceID(),
                    cfg.bendFollowRatio,
                    cfg.pushAt90Deg,
                    cfg.pivotOffset,
                    cfg.outwardAngleOffset);

                Transform shadowHelper = _pose.GetOrCreateShadowHelper(
                    rigKey, frame, groups.ProximalSource, groups.DistalSource,
                    cfg.bendFollowRatio, cfg.pushAt90Deg);
                if (shadowHelper == null) continue;

                int pRef = groups.ProximalReferenceBoneIndex;
                int dRef = groups.DistalReferenceBoneIndex;
                Matrix4x4 sp = workingBones[pRef].localToWorldMatrix * workingBindposes[pRef];
                Matrix4x4 sd = workingBones[dRef].localToWorldMatrix * workingBindposes[dRef];
                Matrix4x4 helperBind = SkinningEvaluator.ComputeHelperBindpose(
                    shadowHelper.localToWorldMatrix, sp, sd, Mathf.Clamp(cfg.bendFollowRatio, 0.1f, 0.9f));

                workingBones.Add(shadowHelper);
                workingBindposes.Add(helperBind);
                workingWeights = res.NewWeights;
                anyModified = true;

                for (int v = 0; v < vertCount; v++)
                {
                    if (res.HelperWeights[v] > combinedHelperWeights[v])
                        combinedHelperWeights[v] = res.HelperWeights[v];
                    if (res.VertexTiers[v] > combinedTiers[v])
                        combinedTiers[v] = res.VertexTiers[v];
                }
            }

            if (!anyModified)
                return data;

            Mesh afterMesh = UnityEngine.Object.Instantiate(origMesh);
            afterMesh.hideFlags = HideFlags.HideAndDontSave;
            afterMesh.bindposes = workingBindposes.ToArray();
            afterMesh.boneWeights = workingWeights;
            _createdMeshes.Add(afterMesh);

            Mesh weightHm = UnityEngine.Object.Instantiate(afterMesh);
            weightHm.hideFlags = HideFlags.HideAndDontSave;
            weightHm.colors = JointKeeperHeatmapDrawer.BuildWeightColors(combinedHelperWeights);
            _createdMeshes.Add(weightHm);

            Mesh diagHm = UnityEngine.Object.Instantiate(afterMesh);
            diagHm.hideFlags = HideFlags.HideAndDontSave;
            diagHm.colors = JointKeeperHeatmapDrawer.BuildDiagnosticColors(combinedTiers);
            _createdMeshes.Add(diagHm);

            int subMeshCount = Mathf.Max(1, origMesh.subMeshCount);
            var hmMats = new Material[subMeshCount];
            for (int i = 0; i < subMeshCount; i++)
                hmMats[i] = _heatmapMaterial;

            data.AfterMesh = afterMesh;
            data.WeightHeatmapMesh = weightHm;
            data.DiagHeatmapMesh = diagHm;
            data.AfterShadowBones = _pose.MapBonesToShadow(workingBones.ToArray());
            data.HeatmapMaterials = hmMats;
            data.HasJointKeeperChanges = true;
            return data;
        }

        private static Dictionary<(SkinnedMeshRenderer, JointId), JointConfig> ResolvePreviewOwnership(
            List<JointKeeperComponent> components,
            Transform avatarRoot)
        {
            var result = new Dictionary<(SkinnedMeshRenderer, JointId), JointConfig>();
            var prio = new Dictionary<(SkinnedMeshRenderer, JointId), (bool isManual, int depth)>();

            for (int i = 0; i < components.Count; i++)
            {
                var comp = components[i];
                bool isManual = comp.targetMode == TargetRendererMode.ManualList;
                var targets = JointKeeperTargetCollector.CollectTargets(comp, avatarRoot);

                foreach (var smr in targets)
                {
                    if (smr == null || smr.sharedMesh == null) continue;
                    int depth = 0;
                    Transform cur = smr.transform;
                    while (cur != null && cur != comp.transform) { cur = cur.parent; depth++; }
                    if (cur != comp.transform) depth = int.MaxValue;

                    foreach (var jointId in AllJoints)
                    {
                        var cfg = comp.ResolveConfig(jointId);
                        if (!cfg.enabled) continue;
                        var key = (smr, jointId);
                        if (!prio.TryGetValue(key, out var existing)
                            || (isManual && !existing.isManual)
                            || (isManual == existing.isManual && depth <= existing.depth))
                        {
                            prio[key] = (isManual, depth);
                            result[key] = cfg;
                        }
                    }
                }
            }
            return result;
        }

        public void OnFrameGroup()
        {
            _pose?.UpdateFrame(JointKeeperPreviewState.BendAngleDeg, JointKeeperPreviewState.TwistAngleDeg);
        }

        public void OnFrame(Renderer original, Renderer proxy)
        {
            var proxySmr = proxy as SkinnedMeshRenderer;
            if (proxySmr == null || !_dataByOriginal.TryGetValue(original, out var data))
                return;

            if (!data.HasJointKeeperChanges || JointKeeperPreviewState.ShowBeforeOnly)
            {
                if (data.BeforeShadowBones != null)
                    proxySmr.bones = data.BeforeShadowBones;
                return;
            }

            PreviewHeatmapMode hm = JointKeeperPreviewState.HeatmapMode;
            if (hm == PreviewHeatmapMode.HelperWeight && data.WeightHeatmapMesh != null)
            {
                proxySmr.sharedMesh = data.WeightHeatmapMesh;
                proxySmr.bones = data.AfterShadowBones;
                proxySmr.sharedMaterials = data.HeatmapMaterials;
            }
            else if (hm == PreviewHeatmapMode.FourBoneDiagnostics && data.DiagHeatmapMesh != null)
            {
                proxySmr.sharedMesh = data.DiagHeatmapMesh;
                proxySmr.bones = data.AfterShadowBones;
                proxySmr.sharedMaterials = data.HeatmapMaterials;
            }
            else
            {
                proxySmr.sharedMesh = data.AfterMesh;
                proxySmr.bones = data.AfterShadowBones;
            }
        }

        public void Dispose()
        {
            _pose?.Dispose();
            for (int i = 0; i < _createdMeshes.Count; i++)
            {
                if (_createdMeshes[i] != null)
                    UnityEngine.Object.DestroyImmediate(_createdMeshes[i]);
            }
            _createdMeshes.Clear();

            if (_heatmapMaterial != null)
            {
                UnityEngine.Object.DestroyImmediate(_heatmapMaterial);
                _heatmapMaterial = null;
            }
            _dataByOriginal.Clear();
        }
    }
}
#endif
