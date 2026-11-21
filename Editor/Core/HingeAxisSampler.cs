using System;
using System.Collections.Generic;
using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// 前腕ロール軸の実測結果 (FEATURE_ForearmTwist §4.6)。
    /// </summary>
    internal readonly struct TwistAxisSample
    {
        /// <summary>ワールド空間のロール軸（肘→手首向き）。HasAxis == false の場合は Vector3.zero。</summary>
        public readonly Vector3 Axis;
        public readonly bool HasAxis;
        /// <summary>ひねりマッスル 1.0 あたりの LowerArm ロール角 (deg)。0 に近いアバターでは前腕ツイスト分散の効果がない。</summary>
        public readonly float DegreesPerMuscleUnit;
        /// <summary>Avatar.humanDescription.lowerArmTwist（取得失敗時は -1）。</summary>
        public readonly float LowerArmTwistSetting;

        public TwistAxisSample(Vector3 axis, bool hasAxis, float degreesPerMuscleUnit, float lowerArmTwistSetting)
        {
            Axis = axis;
            HasAxis = hasAxis;
            DegreesPerMuscleUnit = degreesPerMuscleUnit;
            LowerArmTwistSetting = lowerArmTwistSetting;
        }
    }

    /// <summary>
    /// Humanoid マッスル空間から肘・膝の実際のヒンジ回転軸を実測する (ADR-06)。
    /// ビルド時のアバタークローンまたは一時クローン上で実行し、終了時に全 Transform の local TRS を復元する。
    /// </summary>
    internal static class HingeAxisSampler
    {
        private readonly struct TransformSnapshot
        {
            public readonly Transform Transform;
            public readonly Vector3 LocalPosition;
            public readonly Quaternion LocalRotation;
            public readonly Vector3 LocalScale;

            public TransformSnapshot(Transform t)
            {
                Transform = t;
                LocalPosition = t.localPosition;
                LocalRotation = t.localRotation;
                LocalScale = t.localScale;
            }

            public void Restore()
            {
                if (Transform == null) return;
                Transform.localPosition = LocalPosition;
                Transform.localRotation = LocalRotation;
                Transform.localScale = LocalScale;
            }
        }

        private static int FindMuscleIndex(string muscleName)
        {
            if (string.IsNullOrEmpty(muscleName)) return -1;
            string[] names = HumanTrait.MuscleName;
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], muscleName, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// 指定した全関節の実測ヒンジ軸をまとめて取得する。
        /// 測定できない関節は辞書に含めない（フォールバックへ委ねる）。
        /// </summary>
        public static Dictionary<JointId, Vector3> SampleAll(
            Animator animator,
            Dictionary<HumanBodyBones, Transform> humanoidBones = null)
        {
            return SampleAll(animator, humanoidBones, out _);
        }

        /// <summary>
        /// ヒンジ軸に加え、前腕ツイスト分散用のロール軸と「ひねりマッスル 1.0 あたりのロール角」も同時に実測する
        /// (FEATURE_ForearmTwist §4.6)。Transform の退避・復元は 1 回にまとめる。
        /// </summary>
        public static Dictionary<JointId, Vector3> SampleAll(
            Animator animator,
            Dictionary<HumanBodyBones, Transform> humanoidBones,
            out Dictionary<JointId, TwistAxisSample> twistSamples)
        {
            var result = new Dictionary<JointId, Vector3>();
            twistSamples = new Dictionary<JointId, TwistAxisSample>();
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
                return result;

            if (humanoidBones == null)
                humanoidBones = HumanoidBoneResolver.ResolveAll(animator, out _);

            var allTransforms = animator.transform.GetComponentsInChildren<Transform>(true);
            var snapshots = new TransformSnapshot[allTransforms.Length];
            for (int i = 0; i < allTransforms.Length; i++)
                snapshots[i] = new TransformSnapshot(allTransforms[i]);

            HumanPoseHandler handler = null;
            try
            {
                handler = new HumanPoseHandler(animator.avatar, animator.transform);
                var basePose = new HumanPose();
                handler.GetHumanPose(ref basePose);
                if (basePose.muscles == null || basePose.muscles.Length == 0)
                    return result;

                var joints = new[] { JointId.LeftElbow, JointId.RightElbow, JointId.LeftKnee, JointId.RightKnee };
                foreach (var jointId in joints)
                {
                    if (TrySampleSingle(humanoidBones, handler, basePose, jointId, out var axis))
                    {
                        result[jointId] = axis;
                    }
                }

                var twistJoints = new[] { JointId.LeftElbow, JointId.RightElbow };
                float lowerArmTwist = TryGetLowerArmTwistSetting(animator.avatar);
                foreach (var jointId in twistJoints)
                {
                    if (TrySampleTwist(humanoidBones, handler, basePose, jointId, lowerArmTwist, out var sample))
                        twistSamples[jointId] = sample;
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[JointKeeper] HingeAxisSampler fallback: {ex.Message}");
            }
            finally
            {
                handler?.Dispose();
                for (int i = 0; i < snapshots.Length; i++)
                    snapshots[i].Restore();
            }

            return result;
        }

        private static bool TrySampleSingle(
            Dictionary<HumanBodyBones, Transform> humanoidBones,
            HumanPoseHandler handler,
            HumanPose basePose,
            JointId jointId,
            out Vector3 axis)
        {
            axis = Vector3.zero;
            int muscleIdx = FindMuscleIndex(jointId.GetMuscleName());
            if (muscleIdx < 0 || muscleIdx >= basePose.muscles.Length)
                return false;

            Transform upper = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetUpperHumanoidBone());
            Transform lower = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetLowerHumanoidBone());
            if (upper == null || lower == null)
                return false;

            Vector3 limbDir = lower.position - upper.position;
            if (limbDir.sqrMagnitude < 1e-8f)
                return false;
            limbDir.Normalize();

            var poseA = ClonePose(basePose);
            var poseB = ClonePose(basePose);

            float m0 = basePose.muscles[muscleIdx];
            float m1 = (m0 - 0.2f >= -1.0f) ? (m0 - 0.2f) : (m0 + 0.2f);
            poseA.muscles[muscleIdx] = m0;
            poseB.muscles[muscleIdx] = m1;

            handler.SetHumanPose(ref poseA);
            Quaternion qA = lower.rotation;

            handler.SetHumanPose(ref poseB);
            Quaternion qB = lower.rotation;

            Quaternion deltaQ = qB * Quaternion.Inverse(qA);
            deltaQ.ToAngleAxis(out float angleDeg, out Vector3 rotAxis);

            if (float.IsNaN(angleDeg) || float.IsInfinity(angleDeg))
                return false;
            if (angleDeg > 180f)
                angleDeg = 360f - angleDeg;
            if (angleDeg < 1.0f || rotAxis.sqrMagnitude < 1e-6f)
                return false;

            rotAxis.Normalize();
            if (Mathf.Abs(Vector3.Dot(rotAxis, limbDir)) > 0.5f)
                return false;

            axis = rotAxis;
            return true;
        }

        private static bool TrySampleTwist(
            Dictionary<HumanBodyBones, Transform> humanoidBones,
            HumanPoseHandler handler,
            HumanPose basePose,
            JointId jointId,
            float lowerArmTwistSetting,
            out TwistAxisSample sample)
        {
            sample = default;
            int muscleIdx = FindMuscleIndex(jointId.GetTwistMuscleName());
            if (muscleIdx < 0 || muscleIdx >= basePose.muscles.Length)
                return false;

            Transform lower = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetLowerHumanoidBone());
            Transform child = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetChildHumanoidBone());
            if (lower == null || child == null)
                return false;

            Vector3 forearmDir = child.position - lower.position;
            if (forearmDir.sqrMagnitude < 1e-8f)
                return false;
            forearmDir.Normalize();

            var poseA = ClonePose(basePose);
            var poseB = ClonePose(basePose);

            const float delta = 0.2f;
            float m0 = basePose.muscles[muscleIdx];
            float m1 = (m0 - delta >= -1.0f) ? (m0 - delta) : (m0 + delta);
            poseA.muscles[muscleIdx] = m0;
            poseB.muscles[muscleIdx] = m1;

            handler.SetHumanPose(ref poseA);
            Quaternion qA = lower.rotation;
            handler.SetHumanPose(ref poseB);
            Quaternion qB = lower.rotation;

            Quaternion deltaQ = qB * Quaternion.Inverse(qA);
            deltaQ.ToAngleAxis(out float angleDeg, out Vector3 rotAxis);
            if (float.IsNaN(angleDeg) || float.IsInfinity(angleDeg))
                return false;
            if (angleDeg > 180f)
                angleDeg = 360f - angleDeg;

            bool hasAxis = angleDeg >= 0.5f && rotAxis.sqrMagnitude > 1e-6f;
            if (hasAxis)
            {
                rotAxis.Normalize();
                // ロール軸は前腕方向に近いはず。大きく外れる場合は測定失敗（Hand 方向へフォールバック）
                if (Mathf.Abs(Vector3.Dot(rotAxis, forearmDir)) < 0.5f)
                    hasAxis = false;
                else if (Vector3.Dot(rotAxis, forearmDir) < 0f)
                    rotAxis = -rotAxis; // 符号を前腕方向（肘→手首）に揃える
            }

            sample = new TwistAxisSample(
                hasAxis ? rotAxis : Vector3.zero,
                hasAxis,
                angleDeg / delta,
                lowerArmTwistSetting);
            return true;
        }

        private static float TryGetLowerArmTwistSetting(Avatar avatar)
        {
            try
            {
                return avatar != null ? avatar.humanDescription.lowerArmTwist : -1f;
            }
            catch (Exception)
            {
                return -1f;
            }
        }

        private static HumanPose ClonePose(HumanPose src)
        {
            var dst = new HumanPose
            {
                bodyPosition = src.bodyPosition,
                bodyRotation = src.bodyRotation,
                muscles = src.muscles != null ? (float[])src.muscles.Clone() : null,
            };
            return dst;
        }
    }
}
