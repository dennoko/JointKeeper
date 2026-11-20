using System;
using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// 同一関節で補助ボーンを共有できるかを判定する量子化済みリグ同一性キー (ADR-02, §4.4)。
    /// </summary>
    internal readonly struct RigSignatureKey : IEquatable<RigSignatureKey>
    {
        public readonly JointId JointId;
        public readonly int ProximalSourceId;
        public readonly int DistalSourceId;
        public readonly int QuantizedFollowRatio;
        public readonly int QuantizedPushAt90;
        public readonly Vector3Int QuantizedPivot;
        public readonly int QuantizedOutwardAngle;

        public RigSignatureKey(
            JointId jointId,
            int proximalSourceId,
            int distalSourceId,
            float bendFollowRatio,
            float pushAt90Deg,
            Vector3 pivotOffset,
            float outwardAngleOffset)
        {
            JointId = jointId;
            ProximalSourceId = proximalSourceId;
            DistalSourceId = distalSourceId;
            QuantizedFollowRatio = Mathf.RoundToInt(Mathf.Clamp(bendFollowRatio, 0.1f, 0.9f) * 1000f);
            QuantizedPushAt90 = Mathf.RoundToInt(pushAt90Deg * 10000f);
            QuantizedPivot = new Vector3Int(
                Mathf.RoundToInt(pivotOffset.x * 10000f),
                Mathf.RoundToInt(pivotOffset.y * 10000f),
                Mathf.RoundToInt(pivotOffset.z * 10000f));
            QuantizedOutwardAngle = Mathf.RoundToInt(outwardAngleOffset * 100f);
        }

        public bool Equals(RigSignatureKey other)
        {
            return JointId == other.JointId
                && ProximalSourceId == other.ProximalSourceId
                && DistalSourceId == other.DistalSourceId
                && QuantizedFollowRatio == other.QuantizedFollowRatio
                && QuantizedPushAt90 == other.QuantizedPushAt90
                && QuantizedPivot == other.QuantizedPivot
                && QuantizedOutwardAngle == other.QuantizedOutwardAngle;
        }

        public override bool Equals(object obj) => obj is RigSignatureKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)JointId;
                hash = (hash * 397) ^ ProximalSourceId;
                hash = (hash * 397) ^ DistalSourceId;
                hash = (hash * 397) ^ QuantizedFollowRatio;
                hash = (hash * 397) ^ QuantizedPushAt90;
                hash = (hash * 397) ^ QuantizedPivot.GetHashCode();
                hash = (hash * 397) ^ QuantizedOutwardAngle;
                return hash;
            }
        }

        public static bool operator ==(RigSignatureKey left, RigSignatureKey right) => left.Equals(right);
        public static bool operator !=(RigSignatureKey left, RigSignatureKey right) => !left.Equals(right);
    }
}
