using UnityEngine;
using Dennokoworks.JointKeeper;
#if JOINT_KEEPER_VRCSDK
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;
#endif

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// 前腕ツイスト分散リグの幾何情報（ビルド全体で関節ごとに共通）(FEATURE_ForearmTwist §4.2, §4.6)。
    /// </summary>
    internal readonly struct ForearmTwistGeometry
    {
        /// <summary>J: 肘中心（DistalSource のワールド位置）。</summary>
        public readonly Vector3 ElbowCenter;
        /// <summary>â: 前腕ロール軸（肘→手首向き）。</summary>
        public readonly Vector3 RollAxis;
        /// <summary>ĥ: 直交化前の生のヒンジ軸（WorldUpVector 用）。</summary>
        public readonly Vector3 HingeAxisRaw;
        /// <summary>ĥ⊥: ロール軸に直交化したヒンジ軸（UpAxis 用）。</summary>
        public readonly Vector3 HingeAxisPerp;
        /// <summary>L_L: 前腕長。</summary>
        public readonly float ForearmLength;
        public readonly bool RollAxisMeasured;

        public ForearmTwistGeometry(
            Vector3 elbowCenter,
            Vector3 rollAxis,
            Vector3 hingeAxisRaw,
            Vector3 hingeAxisPerp,
            float forearmLength,
            bool rollAxisMeasured)
        {
            ElbowCenter = elbowCenter;
            RollAxis = rollAxis;
            HingeAxisRaw = hingeAxisRaw;
            HingeAxisPerp = hingeAxisPerp;
            ForearmLength = forearmLength;
            RollAxisMeasured = rollAxisMeasured;
        }

        /// <summary>
        /// ロール軸・ヒンジ軸・前腕長を決める。
        /// ロール軸は実測値（前腕方向から 60° 以内）を優先し、無ければ Hand 方向を使う。
        /// </summary>
        public static bool TryCompute(
            Transform distalSource,
            Transform childHumanoid,
            Vector3? measuredRollAxis,
            Vector3 hingeAxisWorld,
            float fallbackLength,
            out ForearmTwistGeometry geometry,
            out string error)
        {
            geometry = default;
            error = null;
            if (distalSource == null)
            {
                error = "DistalSource が見つかりません。";
                return false;
            }

            Vector3 j = distalSource.position;
            Vector3 handDir = Vector3.zero;
            float length = fallbackLength;
            if (childHumanoid != null)
            {
                Vector3 d = childHumanoid.position - j;
                if (d.sqrMagnitude > 1e-8f)
                {
                    length = d.magnitude;
                    handDir = d / length;
                }
            }

            Vector3 roll = Vector3.zero;
            bool measured = false;
            if (measuredRollAxis.HasValue && measuredRollAxis.Value.sqrMagnitude > 1e-8f)
            {
                Vector3 m = measuredRollAxis.Value.normalized;
                if (handDir.sqrMagnitude > 0.5f && Vector3.Dot(m, handDir) < 0f)
                    m = -m;
                if (handDir.sqrMagnitude < 0.5f || Vector3.Dot(m, handDir) > 0.5f)
                {
                    roll = m;
                    measured = true;
                }
            }
            if (!measured)
                roll = handDir;

            if (roll.sqrMagnitude < 0.5f)
            {
                error = "前腕のロール軸を決定できません（Hand ボーンが見つかりません）。";
                return false;
            }
            if (length < 1e-4f)
            {
                error = "前腕長を決定できません。";
                return false;
            }

            Vector3 hinge = hingeAxisWorld.sqrMagnitude > 1e-8f ? hingeAxisWorld.normalized : Vector3.zero;
            Vector3 perp = hinge - Vector3.Dot(hinge, roll) * roll;
            if (perp.sqrMagnitude < 0.01f)
            {
                error = "ヒンジ軸が前腕ロール軸とほぼ平行なため、曲げ専用の基準ボーンを構成できません。";
                return false;
            }

            geometry = new ForearmTwistGeometry(j, roll, hinge, perp.normalized, length, measured);
            return true;
        }
    }

    /// <summary>
    /// 前腕ツイスト分散ボーン（Aim ターゲット・曲げ専用基準 N・中間ボーン T_j）を生成する (ADR-09, FEATURE_ForearmTwist §4)。
    /// </summary>
    internal static class ForearmTwistRigBuilder
    {
        /// <summary>
        /// 曲げ専用基準ボーン N（DistalSource の子、VRCAimConstraint）と Aim ターゲットを生成する。
        /// </summary>
        public static Transform CreateTwistBase(
            JointId jointId,
            in ForearmTwistGeometry geo,
            Transform proximalSource,
            Transform distalSource,
            out Transform aimTarget)
        {
            // Aim ターゲット: DistalSource の子、ロール軸上 (J + â·L_L)
            var aimGo = new GameObject(VrcConstraintRigBuilder.MakeUniqueChildName(distalSource, JointKeeperGeneratedNames.TwistAimName(jointId)));
            aimTarget = aimGo.transform;
            aimTarget.SetParent(distalSource, worldPositionStays: false);
            aimTarget.position = geo.ElbowCenter + geo.RollAxis * geo.ForearmLength;
            aimTarget.localRotation = Quaternion.identity;
            aimTarget.localScale = Vector3.one;

            // N: DistalSource の子 (localPosition = 0)、レスト回転 = DistalSource と同一
            var baseGo = new GameObject(VrcConstraintRigBuilder.MakeUniqueChildName(distalSource, JointKeeperGeneratedNames.TwistBaseName(jointId)));
            Transform twistBase = baseGo.transform;
            twistBase.SetParent(distalSource, worldPositionStays: false);
            twistBase.localPosition = Vector3.zero;
            twistBase.localRotation = Quaternion.identity;
            twistBase.localScale = Vector3.one;

            ComputeAimAxes(geo, proximalSource, distalSource,
                out Vector3 aimAxisLocal, out Vector3 upAxisLocal, out Vector3 worldUpVectorInProximal);

#if JOINT_KEEPER_VRCSDK
            var aim = baseGo.AddComponent<VRCAimConstraint>();
            aim.GlobalWeight = 1.0f;
            aim.SolveInLocalSpace = false;
            aim.AimAxis = aimAxisLocal;
            aim.UpAxis = upAxisLocal;
            aim.WorldUp = VRCConstraintBase.WorldUpType.ObjectRotationUp;
            aim.WorldUpTransform = proximalSource;
            aim.WorldUpVector = worldUpVectorInProximal;
            aim.RotationAtRest = Vector3.zero;
            aim.RotationOffset = Vector3.zero;
            aim.AffectsRotationX = true;
            aim.AffectsRotationY = true;
            aim.AffectsRotationZ = true;
            aim.Sources.Add(new VRCConstraintSource
            {
                SourceTransform = aimTarget,
                Weight = 1.0f,
                ParentPositionOffset = Vector3.zero,
                ParentRotationOffset = Vector3.zero,
            });
            aim.Locked = true;
            aim.IsActive = true;
            aim.ApplyConfigurationChanges();
#else
            Debug.LogWarning("[JointKeeper] VRChat SDK 3.7.0+ が見つからないため VRCAimConstraint を設定できませんでした。", baseGo);
#endif
            return twistBase;
        }

        /// <summary>
        /// 中間ボーン T_j（N の子、2 ソース VRCRotationConstraint: N ⇔ DistalSource をウェイト (1-k, k) でブレンド）を生成する。
        /// </summary>
        public static Transform CreateTwistKnot(
            JointId jointId,
            int knotIndex,
            in ForearmTwistGeometry geo,
            Transform twistBase,
            Transform distalSource,
            float k,
            float displayAxialRatio)
        {
            var go = new GameObject(VrcConstraintRigBuilder.MakeUniqueChildName(twistBase, JointKeeperGeneratedNames.TwistKnotName(jointId, knotIndex)));
            Transform knot = go.transform;
            knot.SetParent(twistBase, worldPositionStays: false);
            knot.position = geo.ElbowCenter + geo.RollAxis * (Mathf.Clamp01(displayAxialRatio) * geo.ForearmLength);
            knot.localRotation = Quaternion.identity;
            knot.localScale = Vector3.one;

            float kc = Mathf.Clamp01(k);
#if JOINT_KEEPER_VRCSDK
            var rot = go.AddComponent<VRCRotationConstraint>();
            rot.GlobalWeight = 1.0f;
            rot.SolveInLocalSpace = false;
            rot.RotationAtRest = Vector3.zero;
            rot.RotationOffset = Vector3.zero;
            rot.AffectsRotationX = true;
            rot.AffectsRotationY = true;
            rot.AffectsRotationZ = true;
            rot.Sources.Add(new VRCConstraintSource
            {
                SourceTransform = twistBase,
                Weight = 1.0f - kc,
                ParentPositionOffset = Vector3.zero,
                ParentRotationOffset = Vector3.zero,
            });
            rot.Sources.Add(new VRCConstraintSource
            {
                SourceTransform = distalSource,
                Weight = kc,
                ParentPositionOffset = Vector3.zero,
                ParentRotationOffset = Vector3.zero,
            });
            rot.Locked = true;
            rot.IsActive = true;
            rot.ApplyConfigurationChanges();
#else
            Debug.LogWarning("[JointKeeper] VRChat SDK 3.7.0+ が見つからないため VRCRotationConstraint を設定できませんでした。", go);
#endif
            return knot;
        }

        /// <summary>
        /// Aim の局所軸と WorldUpVector を求める（N のレスト回転 = DistalSource の回転なので局所軸は DistalSource 空間）。
        /// </summary>
        internal static void ComputeAimAxes(
            in ForearmTwistGeometry geo,
            Transform proximalSource,
            Transform distalSource,
            out Vector3 aimAxisLocal,
            out Vector3 upAxisLocal,
            out Vector3 worldUpVectorInProximal)
        {
            aimAxisLocal = distalSource.InverseTransformDirection(geo.RollAxis).normalized;
            upAxisLocal = distalSource.InverseTransformDirection(geo.HingeAxisPerp).normalized;
            worldUpVectorInProximal = proximalSource.InverseTransformDirection(geo.HingeAxisRaw).normalized;
        }
    }
}
