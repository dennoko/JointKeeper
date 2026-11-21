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
            public Mesh ObservedUpstreamMesh;
            public Mesh AfterMesh;
            public Mesh WeightHeatmapMesh;
            public Mesh DiagHeatmapMesh;
            public Mesh TwistHeatmapMesh;
            public Transform[] AfterShadowBones;
            public Transform[] BeforeShadowBones;
            public Material[] HeatmapMaterials;
            public bool HasJointKeeperOwnership;
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
        private readonly Dictionary<(SkinnedMeshRenderer, JointId), (JointConfig cfg, ForearmTwistConfig twist)> _ownership;
        private readonly Dictionary<HumanBodyBones, Transform> _humanoidBones;
        private readonly AvatarKinematicContext _kinematicCtx;
        private readonly Dictionary<JointId, JointFrame> _baseFrames;
        private readonly Dictionary<Transform, Transform> _aliasMap;
        private readonly Dictionary<JointId, Vector3> _measuredAxes;
        private readonly Dictionary<JointId, TwistAxisSample> _twistSamples;
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

            _humanoidBones = HumanoidBoneResolver.ResolveAll(animator, out _);
            if (_humanoidBones.Count == 0) return;

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
            _aliasMap = PreviewBoneResolver.BuildAliasMap(avatarRoot);
            _kinematicCtx = AvatarKinematicContext.Build(avatarRoot, animator, _humanoidBones);
            _measuredAxes = JointKeeperPreviewState.GetCachedMeasuredAxes(avatarRoot);
            _twistSamples = JointKeeperPreviewState.GetCachedTwistSamples(avatarRoot);

            Vector3 avatarForward = JointFrameSolver.ComputeAvatarForward(animator, avatarRoot.transform, out _, _humanoidBones);
            Vector3 avatarUp = avatarRoot.transform.up;

            _baseFrames = new Dictionary<JointId, JointFrame>();
            foreach (var jointId in AllJoints)
            {
                Transform upper = HumanoidBoneResolver.GetBone(_humanoidBones, jointId.GetUpperHumanoidBone());
                Transform lower = HumanoidBoneResolver.GetBone(_humanoidBones, jointId.GetLowerHumanoidBone());
                Transform child = HumanoidBoneResolver.GetBone(_humanoidBones, jointId.GetChildHumanoidBone());
                if (upper == null || lower == null) continue;

                Vector3? childPos = child != null ? child.position : (Vector3?)null;
                Vector3? measured = (_measuredAxes != null && _measuredAxes.TryGetValue(jointId, out var ax)) ? ax : (Vector3?)null;
                _baseFrames[jointId] = JointFrameSolver.Solve(
                    jointId, upper.position, lower.position, childPos, avatarForward, avatarUp, measured);
            }

            _pose = new JointKeeperPreviewPose(avatarRoot, _humanoidBones, _baseFrames, _aliasMap);
            _heatmapMaterial = JointKeeperHeatmapDrawer.CreateHeatmapMaterial();

            // SMR ごとの適用設定を解決
            _ownership = ResolvePreviewOwnership(enabledComps, avatarRoot.transform);

            foreach (var (orig, proxy) in proxyPairs)
            {
                var smr = orig as SkinnedMeshRenderer;
                var proxySmr = proxy as SkinnedMeshRenderer;
                if (smr == null || smr.sharedMesh == null || smr.bones == null || smr.bones.Length == 0)
                    continue;

                var data = BuildRendererPreviewData(smr, proxySmr);
                _dataByOriginal[orig] = data;
            }
        }

        /// <summary>
        /// 上流の NDMF プレビューフィルタ（MA MeshDeleter / ShapeChanger / DennokoMeshEditor 等）が
        /// プロキシ SMR へ反映したメッシュと BlendShape ウェイトを取得する。
        /// ボーン数や頂点ウェイトが不整合な場合のみ元のメッシュへ安全にフォールバックする。
        /// </summary>
        internal static Mesh SelectPreviewSourceMesh(
            SkinnedMeshRenderer origSmr,
            SkinnedMeshRenderer proxySmr,
            out float[] blendShapeWeights)
        {
            blendShapeWeights = null;
            if (origSmr == null) return null;

            Transform[] origBones = origSmr.bones;
            int origBoneCount = origBones != null ? origBones.Length : 0;

            Mesh proxyMesh = proxySmr != null ? proxySmr.sharedMesh : null;
            bool canUseProxyMesh = proxyMesh != null
                && proxyMesh.isReadable
                && proxyMesh.vertexCount > 0
                && proxyMesh.bindposes != null
                && proxyMesh.bindposes.Length == origBoneCount
                && proxyMesh.boneWeights != null
                && proxyMesh.boneWeights.Length == proxyMesh.vertexCount;

            Mesh sourceMesh = canUseProxyMesh ? proxyMesh : origSmr.sharedMesh;
            if (sourceMesh == null) return null;

            SkinnedMeshRenderer bsSource = (canUseProxyMesh && proxySmr != null) ? proxySmr : origSmr;
            int bsCount = sourceMesh.blendShapeCount;
            if (bsCount > 0 && bsSource != null)
            {
                blendShapeWeights = new float[bsCount];
                for (int i = 0; i < bsCount; i++)
                    blendShapeWeights[i] = bsSource.GetBlendShapeWeight(i);
            }

            return sourceMesh;
        }

        private RendererPreviewData BuildRendererPreviewData(
            SkinnedMeshRenderer smr,
            SkinnedMeshRenderer proxySmr)
        {
            bool hasOwnership = HasAnyOwnership(smr);
            Mesh sourceMesh = SelectPreviewSourceMesh(smr, proxySmr, out float[] bsWeights);

            var data = new RendererPreviewData
            {
                ObservedUpstreamMesh = proxySmr != null ? proxySmr.sharedMesh : sourceMesh,
                BeforeShadowBones = _pose.MapBonesToShadow(smr.bones),
                HasJointKeeperOwnership = hasOwnership,
            };

            if (!hasOwnership || sourceMesh == null)
                return data;

            int vertCount = sourceMesh.vertexCount;
            if (vertCount == 0 || sourceMesh.bindposes == null || sourceMesh.bindposes.Length == 0
                || sourceMesh.boneWeights == null || sourceMesh.boneWeights.Length != vertCount)
            {
                return data;
            }

            Transform[] origBones = smr.bones;
            var scaleAdjusterMap = PreviewBoneResolver.CollectScaleAdjusterScales(origBones);
            Vector3[] worldVerts = SkinningEvaluator.EvaluateWorldVerticesCpu(
                sourceMesh, origBones, bsWeights, null, null, scaleAdjusterMap);
            var workingBones = new List<Transform>(origBones);
            var workingBindposes = new List<Matrix4x4>(sourceMesh.bindposes);
            BoneWeight[] workingWeights = (BoneWeight[])sourceMesh.boneWeights.Clone();
            var combinedHelperWeights = new float[vertCount];
            var combinedTiers = new VertexSlotTier[vertCount];
            var combinedTau = new float[vertCount];
            var combinedQuantized = new bool[vertCount];
            for (int v = 0; v < vertCount; v++) combinedTau[v] = -1f;
            bool anyModified = false;

            Transform ResolveAlias(Transform t)
            {
                if (t == null) return null;
                return (_aliasMap != null && _aliasMap.TryGetValue(t, out var mapped)) ? mapped : t;
            }

            Matrix4x4 SkinMatrix(int idx)
            {
                Matrix4x4 world = workingBones[idx].localToWorldMatrix;
                if (scaleAdjusterMap.TryGetValue(workingBones[idx], out Vector3 sc))
                    world = world * Matrix4x4.Scale(sc);
                return world * workingBindposes[idx];
            }

            foreach (var jointId in AllJoints)
            {
                if (!_ownership.TryGetValue((smr, jointId), out var owned))
                    continue;
                if (!_baseFrames.TryGetValue(jointId, out JointFrame baseFrame))
                    continue;

                JointConfig cfg = owned.cfg;
                ForearmTwistConfig twistCfg = owned.twist;

                Transform upper = HumanoidBoneResolver.GetBone(_humanoidBones, jointId.GetUpperHumanoidBone());
                Transform lower = HumanoidBoneResolver.GetBone(_humanoidBones, jointId.GetLowerHumanoidBone());
                Transform child = HumanoidBoneResolver.GetBone(_humanoidBones, jointId.GetChildHumanoidBone());
                if (upper == null || lower == null) continue;

                // 関節 × 機能単位の二重適用防止（ビルドと同じ判定）
                bool elbowOn = cfg.enabled && !JointKeeperGeneratedNames.ReferencesHelperBone(origBones, jointId);
                bool twistOn = jointId.IsElbow() && twistCfg != null && twistCfg.enabled
                    && !JointKeeperGeneratedNames.ReferencesTwistBone(origBones, jointId);
                if (!elbowOn && !twistOn) continue;

                JointFrame frame = baseFrame.WithOffsets(cfg.pivotOffset, cfg.outwardAngleOffset);
                Transform[] bonesSnapshot = workingBones.ToArray();
                var groups = TwistBoneClassifier.Classify(
                    jointId, upper, lower, child, bonesSnapshot, cfg, _kinematicCtx,
                    workingWeights, worldVerts, frame, _aliasMap);

                if (groups.ProximalBoneIndices.Count == 0 || groups.DistalBoneIndices.Count == 0)
                    continue;
                if (groups.ProximalReferenceBoneIndex < 0 || groups.DistalReferenceBoneIndex < 0)
                    continue;

                // 肘補助ボーン（ボーン未生成で計算）
                WeightRedistributionResult res = null;
                if (elbowOn)
                {
                    var redParams = WeightRedistributionParams.FromConfig(frame, cfg);
                    res = WeightRedistributor.Redistribute(
                        workingWeights, worldVerts,
                        groups.ProximalBoneIndices, groups.DistalBoneIndices, groups.PrimaryHumanoidBoneIndices,
                        workingBones.Count, in redParams);
                    if (res.Stats.AffectedVertices == 0)
                        res = null;
                }

                // 前腕ツイスト分散（肘処理後のウェイトに対して計画）
                ForearmTwistPlan twistPlan = null;
                ForearmTwistGeometry twistGeo = default;
                if (twistOn && groups.DistalRigidBoneIndices.Count > 0)
                {
                    BoneWeight[] twistInput = res != null ? res.NewWeights : workingWeights;
                    var diag = ForearmTwistProcessor.Analyze(twistInput, bonesSnapshot, groups, _kinematicCtx, ResolveAlias);
                    bool blocked = diag.HasExistingTwistRig && !twistCfg.applyEvenIfExistingTwistRig;

                    Vector3? measuredRoll = null;
                    if (_twistSamples != null && _twistSamples.TryGetValue(jointId, out var sample) && sample.HasAxis)
                        measuredRoll = sample.Axis;
                    Vector3 hinge = (_measuredAxes != null && _measuredAxes.TryGetValue(jointId, out var h)) ? h : baseFrame.HingeAxis;

                    if (!blocked && diag.RigidVertexCount > 0
                        && ForearmTwistGeometry.TryCompute(groups.DistalSource, child, measuredRoll, hinge, baseFrame.LimbLength, out twistGeo, out _))
                    {
                        var lookup = new Dictionary<Transform, int>();
                        for (int i = 0; i < bonesSnapshot.Length; i++)
                        {
                            Transform r = ResolveAlias(bonesSnapshot[i]);
                            if (r != null && !lookup.ContainsKey(r))
                                lookup[r] = i;
                        }

                        twistPlan = ForearmTwistProcessor.Plan(
                            twistInput, worldVerts, groups, twistCfg, twistGeo,
                            workingBones.Count + (res != null ? 1 : 0),
                            t => (t != null && lookup.TryGetValue(t, out int idx)) ? idx : -1);
                        if (!twistPlan.HasChanges)
                            twistPlan = null;
                    }
                }

                if (res == null && twistPlan == null)
                    continue;

                // 影の N を先に作り、影の肘補助ボーンの遠位ソースにする (ADR-10)
                Transform shadowTwistBase = twistPlan != null
                    ? _pose.GetOrCreateShadowTwistBase(jointId, twistGeo, groups.ProximalSource, groups.DistalSource)
                    : null;
                if (twistPlan != null && shadowTwistBase == null)
                    twistPlan = null;

                if (res != null)
                {
                    bool useBase = shadowTwistBase != null && twistCfg.bendOnlyElbowHelper;
                    var rigKey = new RigSignatureKey(
                        jointId,
                        groups.ProximalSource.GetInstanceID(),
                        useBase ? shadowTwistBase.GetInstanceID() : groups.DistalSource.GetInstanceID(),
                        cfg.bendFollowRatio,
                        cfg.pushAt90Deg,
                        cfg.pivotOffset,
                        cfg.outwardAngleOffset);

                    Transform shadowHelper = _pose.GetOrCreateShadowHelper(
                        rigKey, frame, groups.ProximalSource, groups.DistalSource,
                        cfg.bendFollowRatio, cfg.pushAt90Deg,
                        useBase ? shadowTwistBase : null);

                    if (shadowHelper != null)
                    {
                        Matrix4x4 sp = SkinMatrix(groups.ProximalReferenceBoneIndex);
                        Matrix4x4 sd = SkinMatrix(groups.DistalReferenceBoneIndex);
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
                    else if (twistPlan != null)
                    {
                        // 補助ボーンを作れなかった場合、ツイスト計画は肘処理後のウェイト前提なので破棄
                        twistPlan = null;
                    }
                }

                if (twistPlan != null)
                {
                    Matrix4x4 sRef = SkinMatrix(twistPlan.RigidReferenceBoneIndex);
                    float[] knots = twistPlan.Knots;
                    Transform distalShadow = _pose.GetConstraintShadowBone(groups.DistalSource);
                    ForearmTwistGeometry geo = twistGeo;

                    workingWeights = ForearmTwistProcessor.Commit(
                        twistPlan,
                        j =>
                        {
                            if (j == 0) return shadowTwistBase;
                            if (knots[j] >= 1f - 1e-5f) return distalShadow;
                            return _pose.GetOrCreateShadowTwistKnot(shadowTwistBase, knots[j], geo);
                        },
                        workingBones,
                        workingBindposes,
                        sRef);
                    anyModified = true;

                    for (int v = 0; v < vertCount; v++)
                    {
                        if (twistPlan.Result.TauValues[v] >= 0f)
                            combinedTau[v] = twistPlan.Result.TauValues[v];
                        combinedQuantized[v] |= twistPlan.Result.QuantizedMask[v];
                    }
                }
            }

            if (!anyModified)
                return data;

            Mesh afterMesh = UnityEngine.Object.Instantiate(sourceMesh);
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

            Mesh twistHm = UnityEngine.Object.Instantiate(afterMesh);
            twistHm.hideFlags = HideFlags.HideAndDontSave;
            twistHm.colors = JointKeeperHeatmapDrawer.BuildTwistColors(combinedTau, combinedQuantized);
            _createdMeshes.Add(twistHm);

            int subMeshCount = Mathf.Max(1, sourceMesh.subMeshCount);
            var hmMats = new Material[subMeshCount];
            for (int i = 0; i < subMeshCount; i++)
                hmMats[i] = _heatmapMaterial;

            data.AfterMesh = afterMesh;
            data.WeightHeatmapMesh = weightHm;
            data.DiagHeatmapMesh = diagHm;
            data.TwistHeatmapMesh = twistHm;
            data.AfterShadowBones = _pose.MapBonesToShadow(workingBones.ToArray());
            data.HeatmapMaterials = hmMats;
            data.HasJointKeeperChanges = true;
            return data;
        }

        private bool HasAnyOwnership(SkinnedMeshRenderer smr)
        {
            if (smr == null || _ownership == null) return false;
            for (int i = 0; i < AllJoints.Length; i++)
            {
                if (_ownership.ContainsKey((smr, AllJoints[i])))
                    return true;
            }
            return false;
        }

        private static bool IsOwnGeneratedMesh(RendererPreviewData data, Mesh mesh)
        {
            return mesh != null && (
                mesh == data.AfterMesh
                || mesh == data.WeightHeatmapMesh
                || mesh == data.DiagHeatmapMesh
                || mesh == data.TwistHeatmapMesh);
        }

        private void DisposeRendererMeshes(RendererPreviewData data)
        {
            if (data == null) return;
            DestroyTrackedMesh(data.AfterMesh);
            DestroyTrackedMesh(data.WeightHeatmapMesh);
            DestroyTrackedMesh(data.DiagHeatmapMesh);
            DestroyTrackedMesh(data.TwistHeatmapMesh);
            data.AfterMesh = null;
            data.WeightHeatmapMesh = null;
            data.DiagHeatmapMesh = null;
            data.TwistHeatmapMesh = null;
        }

        private void DestroyTrackedMesh(Mesh mesh)
        {
            if (mesh == null) return;
            _createdMeshes.Remove(mesh);
            UnityEngine.Object.DestroyImmediate(mesh);
        }

        private static Dictionary<(SkinnedMeshRenderer, JointId), (JointConfig cfg, ForearmTwistConfig twist)> ResolvePreviewOwnership(
            List<JointKeeperComponent> components,
            Transform avatarRoot)
        {
            var result = new Dictionary<(SkinnedMeshRenderer, JointId), (JointConfig, ForearmTwistConfig)>();
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
                        var twist = comp.ResolveTwistConfig(jointId);
                        if (!cfg.enabled && !(jointId.IsElbow() && twist.enabled)) continue;
                        var key = (smr, jointId);
                        if (!prio.TryGetValue(key, out var existing)
                            || (isManual && !existing.isManual)
                            || (isManual == existing.isManual && depth <= existing.depth))
                        {
                            prio[key] = (isManual, depth);
                            result[key] = (cfg, twist);
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

            // 上流 NDMF メッシュ編集フィルタ（MA MeshDeleter / DennokoMeshEditor 等）が
            // OnFrame で別のメッシュインスタンスへ差し替えた場合も追従して再構築する。
            Mesh upstreamMesh = proxySmr.sharedMesh;
            if (data.HasJointKeeperOwnership
                && upstreamMesh != null
                && upstreamMesh != data.ObservedUpstreamMesh
                && !IsOwnGeneratedMesh(data, upstreamMesh))
            {
                var origSmr = original as SkinnedMeshRenderer;
                if (origSmr != null && origSmr.bones != null && origSmr.bones.Length > 0)
                {
                    DisposeRendererMeshes(data);
                    data = BuildRendererPreviewData(origSmr, proxySmr);
                    _dataByOriginal[original] = data;
                }
            }

            // ProxyObjectController.OnPreFrame が毎フレーム元の Mesh / Materials / Bones を復元し、
            // 上流フィルタ（MA MeshDeleter / MaterialSetter / ScaleAdjuster 等）が適用された後に OnFrame が呼ばれる。
            // そのため変更なしのときや通常表示では sharedMesh / sharedMaterials を元値で上書きせず、上流プレビューを保持する。
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
            else if (hm == PreviewHeatmapMode.TwistFollow && data.TwistHeatmapMesh != null)
            {
                proxySmr.sharedMesh = data.TwistHeatmapMesh;
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
