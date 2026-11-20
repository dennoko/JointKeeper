using System;
using System.Collections.Generic;
using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// Phase 2 プレビュー用の非保存影ボーン階層 (§7.3)。
    /// 元アバターのボーンを一切回転させず、毎フレーム影階層のみを同期・曲げ・ツイスト・RigMath 評価する。
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
            public Vector3 HingeAxisWorldAtRest;
            public Vector3 LimbAxisWorldAtRest;
        }

        private GameObject _shadowRoot;
        private readonly Dictionary<Transform, Transform> _origToShadow = new Dictionary<Transform, Transform>();
        private readonly List<(Transform orig, Transform shadow)> _syncPairs = new List<(Transform, Transform)>();
        private readonly Dictionary<RigSignatureKey, ShadowHelperEntry> _helpersByKey =
            new Dictionary<RigSignatureKey, ShadowHelperEntry>();
        private readonly List<JointBendInfo> _bendInfos = new List<JointBendInfo>();
        private readonly Dictionary<Transform, Transform> _outfitAliasMap;

        public JointKeeperPreviewPose(
            GameObject avatarRoot,
            Animator animator,
            Dictionary<JointId, JointFrame> baseFrames,
            Dictionary<Transform, Transform> outfitAliasMap)
        {
            _outfitAliasMap = outfitAliasMap ?? new Dictionary<Transform, Transform>();
            if (avatarRoot == null) return;

            _shadowRoot = new GameObject($"__JK_ShadowPose_{avatarRoot.name}")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            _shadowRoot.transform.SetPositionAndRotation(avatarRoot.transform.position, avatarRoot.transform.rotation);
            _shadowRoot.transform.localScale = avatarRoot.transform.localScale;
            _origToShadow[avatarRoot.transform] = _shadowRoot.transform;
            _syncPairs.Add((avatarRoot.transform, _shadowRoot.transform));

            CloneHierarchy(avatarRoot.transform, _shadowRoot.transform);

            if (animator != null && animator.isHuman && baseFrames != null)
            {
                foreach (var kv in baseFrames)
                {
                    Transform lowerOrig = animator.GetBoneTransform(kv.Key.GetLowerHumanoidBone());
                    if (lowerOrig == null || !_origToShadow.TryGetValue(lowerOrig, out Transform lowerShadow))
                        continue;

                    _bendInfos.Add(new JointBendInfo
                    {
                        JointId = kv.Key,
                        LowerShadow = lowerShadow,
                        HingeAxisWorldAtRest = kv.Value.HingeAxis,
                        LimbAxisWorldAtRest = kv.Value.LimbAxis,
                    });
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

            Transform proxShadow = ResolveShadowBone(proximalSourceOrig);
            Transform distShadow = ResolveShadowBone(distalSourceOrig);
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

        public Transform ResolveShadowBone(Transform origBone)
        {
            if (origBone == null) return null;
            if (_outfitAliasMap.TryGetValue(origBone, out Transform mapped))
                origBone = mapped;
            return _origToShadow.TryGetValue(origBone, out Transform shadow) ? shadow : origBone;
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
                    mapped[i] = ResolveShadowBone(b);
            }
            return mapped;
        }

        /// <summary>
        /// 毎フレーム 1 回（OnFrameGroup）呼び出し、元ボーンの TRS 同期 → 曲げ/ツイスト適用 → RigMath 評価を行う。
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

            // 2. 各関節へ曲げ角・前腕ツイスト角を適用
            for (int i = 0; i < _bendInfos.Count; i++)
            {
                var info = _bendInfos[i];
                if (info.LowerShadow == null) continue;

                // ヒンジ軸回りの曲げ回転（Outward の反対側＝内側へ曲げる）
                if (Mathf.Abs(bendAngleDeg) > 1e-3f)
                {
                    Quaternion bendWorld = Quaternion.AngleAxis(bendAngleDeg, info.HingeAxisWorldAtRest);
                    info.LowerShadow.rotation = bendWorld * info.LowerShadow.rotation;
                }

                // 肘のみ前腕ツイスト角を適用
                if (info.JointId.IsElbow() && Mathf.Abs(twistAngleDeg) > 1e-3f)
                {
                    Vector3 currentAxis = (Quaternion.AngleAxis(bendAngleDeg, info.HingeAxisWorldAtRest) * info.LimbAxisWorldAtRest).normalized;
                    Quaternion twistWorld = Quaternion.AngleAxis(twistAngleDeg, currentAxis);
                    info.LowerShadow.rotation = twistWorld * info.LowerShadow.rotation;
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

                CloneHierarchy(origChild, shadowChild);
            }
        }

        public void Dispose()
        {
            if (_shadowRoot != null)
            {
                UnityEngine.Object.DestroyImmediate(_shadowRoot);
                _shadowRoot = null;
            }
            _origToShadow.Clear();
            _syncPairs.Clear();
            _helpersByKey.Clear();
            _bendInfos.Clear();
        }
    }
}
