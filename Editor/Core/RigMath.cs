using UnityEngine;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// VRCParentConstraint の対称軸オフセット算出および等価評価を行う純粋数学モジュール (§5.4)。
    /// ビルド (VrcConstraintRigBuilder)・プレビュー (JointKeeperPreviewPose)・単体テストで共用する。
    /// </summary>
    internal static class RigMath
    {
        private const float Sqrt2 = 1.4142135623730951f;

        /// <summary>
        /// 追従比率 α と 90°曲げ時押し出し量 d_90 から、2 ソースのウェイトおよび
        /// ワールド空間仮想アンカー A0（近位側）, B0（遠位側）を算出する。
        /// </summary>
        public static void ComputeSymmetricAnchors(
            Vector3 jointCenter,
            Vector3 limbAxis,
            float bendFollowRatio,
            float pushAt90Deg,
            out Vector3 anchorAWorld,
            out Vector3 anchorBWorld,
            out float weight0,
            out float weight1)
        {
            float alpha = Mathf.Clamp(bendFollowRatio, 0.1f, 0.9f);
            weight0 = 1.0f - alpha;
            weight1 = alpha;

            Vector3 axis = limbAxis.sqrMagnitude > 1e-8f ? limbAxis.normalized : Vector3.down;
            float k = Sqrt2 * pushAt90Deg;

            anchorAWorld = jointCenter + axis * (k / (2.0f * weight0));
            anchorBWorld = jointCenter - axis * (k / (2.0f * weight1));
        }

        /// <summary>
        /// 各ソースのローカル空間における ParentPositionOffset / ParentRotationOffset を算出する。
        /// </summary>
        public static void ComputeSourceOffsets(
            Matrix4x4 sourceWorldToLocal,
            Quaternion sourceWorldRotation,
            Vector3 anchorWorld,
            Quaternion helperWorldRotation,
            out Vector3 parentPositionOffset,
            out Vector3 parentRotationOffsetEuler)
        {
            parentPositionOffset = sourceWorldToLocal.MultiplyPoint3x4(anchorWorld);
            Quaternion localRot = Quaternion.Inverse(sourceWorldRotation) * helperWorldRotation;
            parentRotationOffsetEuler = localRot.eulerAngles;
        }

        /// <summary>
        /// 2 ソース VRCParentConstraint と数学的に等価なワールド位置・回転を評価する。
        /// </summary>
        public static void EvaluateParentConstraint(
            Matrix4x4 src0LocalToWorld,
            Quaternion src0WorldRotation,
            Vector3 posOffset0,
            Vector3 rotOffsetEuler0,
            float weight0,
            Matrix4x4 src1LocalToWorld,
            Quaternion src1WorldRotation,
            Vector3 posOffset1,
            Vector3 rotOffsetEuler1,
            float weight1,
            out Vector3 worldPosition,
            out Quaternion worldRotation)
        {
            float totalWeight = weight0 + weight1;
            float n0 = totalWeight > 1e-6f ? (weight0 / totalWeight) : 0.5f;
            float n1 = 1.0f - n0;

            Vector3 p0 = src0LocalToWorld.MultiplyPoint3x4(posOffset0);
            Vector3 p1 = src1LocalToWorld.MultiplyPoint3x4(posOffset1);
            worldPosition = p0 * n0 + p1 * n1;

            Quaternion q0 = src0WorldRotation * Quaternion.Euler(rotOffsetEuler0);
            Quaternion q1 = src1WorldRotation * Quaternion.Euler(rotOffsetEuler1);
            worldRotation = Quaternion.Slerp(q0, q1, n1);
        }

        // ─────────────────────────────────────────────────────────────
        // 前腕ツイスト分散 (FEATURE_ForearmTwist §4)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// VRCAimConstraint（RotationOffset = 0、WorldUp = ObjectRotationUp / Vector）と等価なワールド回転を評価する。
        /// ローカル Aim 軸がターゲット方向を向き、ローカル Up 軸が worldUp の Aim 直交成分を向く回転を返す。
        /// worldUp が Aim 方向とほぼ平行な場合は false を返す（呼び出し側で直前値を維持する）。
        /// </summary>
        public static bool EvaluateAimConstraint(
            Vector3 constrainedPosition,
            Vector3 targetPosition,
            Vector3 aimAxisLocal,
            Vector3 upAxisLocal,
            Vector3 worldUpDirection,
            out Quaternion worldRotation)
        {
            worldRotation = Quaternion.identity;

            Vector3 aimWorld = targetPosition - constrainedPosition;
            if (aimWorld.sqrMagnitude < 1e-12f || aimAxisLocal.sqrMagnitude < 1e-12f)
                return false;
            aimWorld.Normalize();

            if (!TryBuildFrame(aimAxisLocal, upAxisLocal, out Quaternion localFrame))
                return false;
            if (!TryBuildFrame(aimWorld, worldUpDirection, out Quaternion worldFrame))
                return false;

            worldRotation = worldFrame * Quaternion.Inverse(localFrame);
            return true;
        }

        /// <summary>
        /// forward = aim、up = up の aim 直交成分 となる正規直交フレームを作る。
        /// </summary>
        private static bool TryBuildFrame(Vector3 aim, Vector3 up, out Quaternion frame)
        {
            frame = Quaternion.identity;
            Vector3 a = aim.normalized;
            Vector3 u = up - Vector3.Dot(up, a) * a;
            if (u.sqrMagnitude < 1e-8f)
                return false;
            frame = Quaternion.LookRotation(a, u.normalized);
            return true;
        }

        /// <summary>
        /// 2 ソース VRCRotationConstraint（オフセット 0）の等価評価。レスト回転が揃っている前提で
        /// Slerp(N, Distal, k) = Δ_N · R(â, kφ) · T_0 となる (FEATURE_ForearmTwist §4.4)。
        /// </summary>
        public static Quaternion EvaluateTwistKnotRotation(Quaternion baseWorldRotation, Quaternion distalWorldRotation, float k)
        {
            return Quaternion.Slerp(baseWorldRotation, distalWorldRotation, Mathf.Clamp01(k));
        }

        /// <summary>
        /// ノット k_j = f·j/n (j = 0..n) を返す。f ≦ 1e-5 の場合はノット 0（N）のみ。
        /// </summary>
        public static float[] ComputeTwistKnots(float twistFollow, int segments)
        {
            float f = Mathf.Clamp01(twistFollow);
            int n = Mathf.Clamp(segments, 1, 4);
            if (f <= 1e-5f)
                return new[] { 0f };

            var knots = new float[n + 1];
            for (int j = 0; j <= n; j++)
                knots[j] = f * j / n;
            return knots;
        }

        /// <summary>
        /// 分配カーブ c(s) ∈ [0, 1] (§5.2)。
        /// </summary>
        public static float EvaluateTwistCurve(Dennokoworks.JointKeeper.TwistCurve curve, float curvePower, float s)
        {
            s = Mathf.Clamp01(s);
            switch (curve)
            {
                case Dennokoworks.JointKeeper.TwistCurve.Power:
                    return Mathf.Pow(s, Mathf.Max(1f, curvePower));
                case Dennokoworks.JointKeeper.TwistCurve.SmoothStep:
                    return s * s * (3f - 2f * s);
                default:
                    return s;
            }
        }

        /// <summary>
        /// 頂点の前腕軸方向位置 s（クランプ済み）を返す (§5.2)。
        /// </summary>
        public static float ComputeTwistAxialParameter(
            Vector3 vertexWorld,
            Vector3 elbowCenter,
            Vector3 rollAxis,
            float forearmLength,
            float startRatio,
            float endRatio)
        {
            float len = Mathf.Max(forearmLength, 1e-5f);
            float z = Vector3.Dot(vertexWorld - elbowCenter, rollAxis) / len;
            float span = Mathf.Max(endRatio - startRatio, 1e-4f);
            return Mathf.Clamp01((z - startRatio) / span);
        }
    }
}
