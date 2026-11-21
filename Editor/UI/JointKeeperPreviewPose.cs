using System;
using System.Collections.Generic;
using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// Phase 2 プレビュー用の非保存影ボーン階層 (§7.3)。
    /// 元アバターのボーンを一切回転させず、毎フレーム影階層のみを同期・曲げ・ツイスト・RigMath 評価する。
    /// MA Merge Armature 衣装（Blender 100x スケールや異なるローカル回転を持つ衣装を含む）の
    /// bindpose 不整合によるメッシュ絞り込みを防ぐため、衣装 SMR には衣装側の影ボーンをそのまま割り当て、
    /// アバター側の曲げ・ツイスト差分だけを剛体ワールド変換として衣装側影ボーンへ伝播する。
    /// </summary>
    internal sealed class JointKeeperPreviewPose : IDisposable
    {
        private sealed class ShadowHelperEntry
        {
            public Transform HelperShadow;
            public Transform ProximalShadow;
            public Transform DistalShadow;
            public Vector3 PosOffset0;
            public Vector3 RotOffset0;
            public Vector3 PosOffset1;
            public Vector3 RotOffset1;
            public float Weight0;
            public float Weight1;
        }

        private sealed class JointBendInfo
        {
            public JointId JointId;
            public Transform LowerShadow;
            public Vector3 HingeAxisInParentLocal;
            public Vector3 LimbAxisInParentLocal;
        }

        private sealed class OutfitFollowerEntry
        {
            public Transform OutfitShadow;
            public Transform AvatarShadow;
        }

        private GameObject _shadowRoot;
        private readonly Dictionary<Transform, Transform> _origToShadow = new Dictionary<Transform, Transform>();
        private readonly Dictionary<Transform, Transform> _smrBoneToShadow = new Dictionary<Transform, Transform>();
        private readonly List<(Transform orig, Transform shadow)> _syncPairs = new List<(Transform, Transform)>();
        private readonly Dictionary<RigSignatureKey, ShadowHelperEntry> _helpersByKey =
            new Dictionary<RigSignatureKey, ShadowHelperEntry>();
        private readonly List<JointBendInfo> _bendInfos = new List<JointBendInfo>();
        private readonly List<OutfitFollowerEntry> _outfitFollowers = new List<OutfitFollowerEntry>();

        public JointKeeperPreviewPose(
            GameObject avatarRoot,
            Dictionary<HumanBodyBones, Transform> humanoidBones,
            Dictionary<JointId, JointFrame> baseFrames,
            Dictionary<Transform, Transform> outfitAliasMap)
        {
            var aliasMap = outfitAliasMap ?? new Dictionary<Transform, Transform>();
            if (avatarRoot == null) return;

            _shadowRoot = new GameObject($"__JK_ShadowPose_{avatarRoot.name}")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            _shadowRoot.transform.SetPositionAndRotation(avatarRoot.transform.position, avatarRoot.transform.rotation);
            _shadowRoot.transform.localScale = avatarRoot.transform.localScale;
            _origToShadow[avatarRoot.transform] = _shadowRoot.transform;
            _smrBoneToShadow[avatarRoot.transform] = _shadowRoot.transform;
            _syncPairs.Add((avatarRoot.transform, _shadowRoot.transform));

            CloneHierarchy(avatarRoot.transform, _shadowRoot.transform);

            var bentAvatarBones = new HashSet<Transform>();
            if (humanoidBones != null && baseFrames != null)
            {
                foreach (var kv in baseFrames)
                {
                    Transform lowerOrig = HumanoidBoneResolver.GetBone(humanoidBones, kv.Key.GetLowerHumanoidBone());
                    if (lowerOrig == null || !_origToShadow.TryGetValue(lowerOrig, out Transform lowerShadow))
                        continue;

                    bentAvatarBones.Add(lowerOrig);

                    Transform refParent = lowerShadow.parent != null ? lowerShadow.parent : _shadowRoot.transform;
                    _bendInfos.Add(new JointBendInfo
                    {
                        JointId = kv.Key,
                        LowerShadow = lowerShadow,
                        HingeAxisInParentLocal = refParent.InverseTransformDirection(kv.Value.HingeAxis).normalized,
                        LimbAxisInParentLocal = refParent.InverseTransformDirection(kv.Value.LimbAxis).normalized,
                    });
                }
            }

            // MA Merge Armature 衣装のうち、曲げ対象のアバター下肢（またはその子孫）に対応する衣装側影ボーンを登録。
            // 親が既に追従対象なら子孫は階層連動するため重複適用しない（手袋・靴下等、下肢を持たず Hand/Foot から始まる衣装にも対応）。
            if (bentAvatarBones.Count > 0 && aliasMap.Count > 0)
            {
                var affectedOutfitOrigs = new HashSet<Transform>();
                foreach (var kv in aliasMap)
                {
                    if (kv.Key == null || kv.Value == null) continue;
                    if (IsSelfOrDescendantOfAny(kv.Value, bentAvatarBones))
                        affectedOutfitOrigs.Add(kv.Key);
                }

                foreach (var outfitOrig in affectedOutfitOrigs)
                {
                    if (HasAncestorInSet(outfitOrig, affectedOutfitOrigs))
                        continue;

                    Transform avatarOrig = aliasMap[outfitOrig];
                    if (_origToShadow.TryGetValue(outfitOrig, out Transform outfitShadow)
                        && _origToShadow.TryGetValue(avatarOrig, out Transform avatarShadow)
                        && outfitShadow != avatarShadow)
                    {
                        _outfitFollowers.Add(new OutfitFollowerEntry
                        {
                            OutfitShadow = outfitShadow,
                            AvatarShadow = avatarShadow,
                        });
                    }
                }
            }
        }

        public Transform GetOrCreateShadowHelper(
            RigSignatureKey key,
            JointFrame frame,
            Transform proximalSourceOrig,
            Transform distalSourceOrig,
            float bendFollowRatio,
            float pushAt90Deg)
        {
            if (_helpersByKey.TryGetValue(key, out var existing))
                return existing.HelperShadow;

            Transform proxShadow = ResolveConstraintShadowBone(proximalSourceOrig);
            Transform distShadow = ResolveConstraintShadowBone(distalSourceOrig);
            if (proxShadow == null || distShadow == null)
                return null;

            var helperGo = new GameObject($"JK_ShadowHelper_{key.JointId}")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            Transform helper = helperGo.transform;
            helper.SetParent(proxShadow, worldPositionStays: false);
            helper.position = frame.JointCenter;
            helper.rotation = frame.FrameRotation;
            helper.localScale = Vector3.one;

            RigMath.ComputeSymmetricAnchors(
                frame.JointCenter,
                frame.LimbAxis,
                bendFollowRatio,
                pushAt90Deg,
                out Vector3 anchorA,
                out Vector3 anchorB,
                out float w0,
                out float w1);

            RigMath.ComputeSourceOffsets(
                proximalSourceOrig.worldToLocalMatrix,
                proximalSourceOrig.rotation,
                anchorA,
                helper.rotation,
                out Vector3 posOff0,
                out Vector3 rotOff0);

            RigMath.ComputeSourceOffsets(
                distalSourceOrig.worldToLocalMatrix,
                distalSourceOrig.rotation,
                anchorB,
                helper.rotation,
                out Vector3 posOff1,
                out Vector3 rotOff1);

            var entry = new ShadowHelperEntry
            {
                HelperShadow = helper,
                ProximalShadow = proxShadow,
                DistalShadow = distShadow,
                PosOffset0 = posOff0,
                RotOffset0 = rotOff0,
                PosOffset1 = posOff1,
                RotOffset1 = rotOff1,
                Weight0 = w0,
                Weight1 = w1,
            };
            _helpersByKey[key] = entry;
            return helper;
        }

        /// <summary>
        /// Constraint ソース用の影ボーンを返す（ScaleProxy ではなく本体ボーンの影を返す）。
        /// </summary>
        private Transform ResolveConstraintShadowBone(Transform origBone)
        {
            if (origBone == null) return null;
            return _origToShadow.TryGetValue(origBone, out Transform shadow) ? shadow : origBone;
        }

        /// <summary>
        /// SMR.bones 用の影ボーンを返す。衣装ボーンをアバターボーンへ無理に置換せず、
        /// 衣装側自身の影ボーン（MA Scale Adjuster があればその ScaleProxy 影）を返すことで
        /// 100x スケールやローカル軸差による bindpose 破綻を完全に防ぐ。
        /// </summary>
        public Transform ResolveSmrShadowBone(Transform origBone)
        {
            if (origBone == null) return null;
            return _smrBoneToShadow.TryGetValue(origBone, out Transform shadow) ? shadow : origBone;
        }

        public Transform[] MapBonesToShadow(Transform[] workingBones)
        {
            var mapped = new Transform[workingBones.Length];
            for (int i = 0; i < workingBones.Length; i++)
            {
                Transform b = workingBones[i];
                if (b != null && b.hideFlags == HideFlags.HideAndDontSave)
                    mapped[i] = b; // 既に影補助ボーン
                else
                    mapped[i] = ResolveSmrShadowBone(b);
            }
            return mapped;
        }

        /// <summary>
        /// 毎フレーム 1 回（OnFrameGroup）呼び出し、元ボーンの TRS 同期 → 曲げ/ツイスト適用 →
        /// 衣装側影ボーンへの剛体差分伝播 → RigMath 評価を行う。
        /// </summary>
        public void UpdateFrame(float bendAngleDeg, float twistAngleDeg)
        {
            if (_shadowRoot == null) return;

            // 1. 全影ボーンを元ボーンの現在 TRS へ同期
            for (int i = 0; i < _syncPairs.Count; i++)
            {
                var (orig, shadow) = _syncPairs[i];
                if (orig == null || shadow == null) continue;
                if (i == 0)
                {
                    shadow.SetPositionAndRotation(orig.position, orig.rotation);
                    shadow.localScale = orig.localScale;
                }
                else
                {
                    shadow.localPosition = orig.localPosition;
                    shadow.localRotation = orig.localRotation;
                    shadow.localScale = orig.localScale;
                }
            }

            // 1b. 衣装追従用のレスト姿勢（曲げ適用前）のワールド位置・回転を記録
            int followerCount = _outfitFollowers.Count;
            Vector3[] avRestPos = null;
            Quaternion[] avRestRot = null;
            Vector3[] outRestPos = null;
            Quaternion[] outRestRot = null;

            bool hasPoseChange = Mathf.Abs(bendAngleDeg) > 1e-3f || Mathf.Abs(twistAngleDeg) > 1e-3f;
            if (hasPoseChange && followerCount > 0)
            {
                avRestPos = new Vector3[followerCount];
                avRestRot = new Quaternion[followerCount];
                outRestPos = new Vector3[followerCount];
                outRestRot = new Quaternion[followerCount];
                for (int i = 0; i < followerCount; i++)
                {
                    var f = _outfitFollowers[i];
                    if (f.AvatarShadow == null || f.OutfitShadow == null) continue;
                    avRestPos[i] = f.AvatarShadow.position;
                    avRestRot[i] = f.AvatarShadow.rotation;
                    outRestPos[i] = f.OutfitShadow.position;
                    outRestRot[i] = f.OutfitShadow.rotation;
                }
            }

            // 2. 各関節へ曲げ角・前腕ツイスト角を適用
            for (int i = 0; i < _bendInfos.Count; i++)
            {
                var info = _bendInfos[i];
                if (info.LowerShadow == null) continue;

                Transform refParent = info.LowerShadow.parent != null ? info.LowerShadow.parent : _shadowRoot.transform;
                Vector3 hingeWorld = refParent.TransformDirection(info.HingeAxisInParentLocal).normalized;
                Vector3 limbWorld = refParent.TransformDirection(info.LimbAxisInParentLocal).normalized;

                // HingeAxis = Cross(LimbAxis, Outward) なので、Unity の Quaternion.AngleAxis(+θ, H) * L は
                // H × L = +Outward（逆反り方向）へ回る。したがって内側へ自然に曲げるには -bendAngleDeg を使用する。
                Quaternion bendWorld = Quaternion.identity;
                if (Mathf.Abs(bendAngleDeg) > 1e-3f)
                {
                    bendWorld = ComputeFlexionRotation(hingeWorld, bendAngleDeg);
                    info.LowerShadow.rotation = bendWorld * info.LowerShadow.rotation;
                }

                // 肘のみ前腕ツイスト角を適用
                if (info.JointId.IsElbow() && Mathf.Abs(twistAngleDeg) > 1e-3f)
                {
                    Vector3 currentAxis = (bendWorld * limbWorld).normalized;
                    Quaternion twistWorld = Quaternion.AngleAxis(twistAngleDeg, currentAxis);
                    info.LowerShadow.rotation = twistWorld * info.LowerShadow.rotation;
                }
            }

            // 2b. アバター側影ボーンの曲げ/ツイスト剛体差分を衣装側影ボーンへ適用（localScale は一切変更しない）
            if (hasPoseChange && followerCount > 0)
            {
                for (int i = 0; i < followerCount; i++)
                {
                    var f = _outfitFollowers[i];
                    if (f.AvatarShadow == null || f.OutfitShadow == null) continue;

                    Quaternion deltaRot = f.AvatarShadow.rotation * Quaternion.Inverse(avRestRot[i]);
                    Vector3 newPos = f.AvatarShadow.position + deltaRot * (outRestPos[i] - avRestPos[i]);
                    Quaternion newRot = deltaRot * outRestRot[i];
                    f.OutfitShadow.SetPositionAndRotation(newPos, newRot);
                }
            }

            // 3. 影補助ボーンを VRCParentConstraint と同一の RigMath 式で更新
            foreach (var kv in _helpersByKey)
            {
                var e = kv.Value;
                if (e.HelperShadow == null || e.ProximalShadow == null || e.DistalShadow == null) continue;

                RigMath.EvaluateParentConstraint(
                    e.ProximalShadow.localToWorldMatrix,
                    e.ProximalShadow.rotation,
                    e.PosOffset0,
                    e.RotOffset0,
                    e.Weight0,
                    e.DistalShadow.localToWorldMatrix,
                    e.DistalShadow.rotation,
                    e.PosOffset1,
                    e.RotOffset1,
                    e.Weight1,
                    out Vector3 worldPos,
                    out Quaternion worldRot);

                e.HelperShadow.SetPositionAndRotation(worldPos, worldRot);
            }
        }

        /// <summary>
        /// JointFrame.HingeAxis (= Cross(LimbAxis, Outward)) を基準に、
        /// 下肢（LimbAxis）を外側（Outward）と反対の解剖学的屈曲方向（-Outward）へ曲げる回転を返す。
        /// </summary>
        internal static Quaternion ComputeFlexionRotation(Vector3 hingeAxisWorld, float bendAngleDeg)
        {
            return Quaternion.AngleAxis(-bendAngleDeg, hingeAxisWorld);
        }

        private void CloneHierarchy(Transform origParent, Transform shadowParent)
        {
            for (int i = 0; i < origParent.childCount; i++)
            {
                Transform origChild = origParent.GetChild(i);
                var go = new GameObject(origChild.name)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                Transform shadowChild = go.transform;
                shadowChild.SetParent(shadowParent, false);
                shadowChild.localPosition = origChild.localPosition;
                shadowChild.localRotation = origChild.localRotation;
                shadowChild.localScale = origChild.localScale;

                _origToShadow[origChild] = shadowChild;
                _syncPairs.Add((origChild, shadowChild));

                // MA Scale Adjuster があるボーンは ScaleAdjusterPass と同一の ScaleProxy を影階層にも用意する
                if (PreviewBoneResolver.TryGetScaleAdjusterScale(origChild, out Vector3 saScale)
                    && (saScale - Vector3.one).sqrMagnitude > 1e-8f)
                {
                    var proxyGo = new GameObject($"{origChild.name}__JK_ScaleProxy")
                    {
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                    Transform proxyT = proxyGo.transform;
                    proxyT.SetParent(shadowChild, false);
                    proxyT.localPosition = Vector3.zero;
                    proxyT.localRotation = Quaternion.identity;
                    proxyT.localScale = saScale;
                    _smrBoneToShadow[origChild] = proxyT;
                }
                else
                {
                    _smrBoneToShadow[origChild] = shadowChild;
                }

                CloneHierarchy(origChild, shadowChild);
            }
        }

        private static bool IsSelfOrDescendantOfAny(Transform t, HashSet<Transform> roots)
        {
            Transform cur = t;
            while (cur != null)
            {
                if (roots.Contains(cur)) return true;
                cur = cur.parent;
            }
            return false;
        }

        private static bool HasAncestorInSet(Transform t, HashSet<Transform> set)
        {
            Transform cur = t != null ? t.parent : null;
            while (cur != null)
            {
                if (set.Contains(cur)) return true;
                cur = cur.parent;
            }
            return false;
        }

        public void Dispose()
        {
            if (_shadowRoot != null)
            {
                UnityEngine.Object.DestroyImmediate(_shadowRoot);
                _shadowRoot = null;
            }
            _origToShadow.Clear();
            _smrBoneToShadow.Clear();
            _syncPairs.Clear();
            _helpersByKey.Clear();
            _bendInfos.Clear();
            _outfitFollowers.Clear();
        }
    }
}
