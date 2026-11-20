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
    }
}
