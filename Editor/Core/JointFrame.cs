using UnityEngine;

namespace Dennokoworks.JointKeeper.Editor
{
    internal enum JointFrameSource
    {
        Measured = 0,
        RestBend = 1,
        Prior = 2,
    }

    internal readonly struct JointFrame
    {
        public readonly Vector3 JointCenter;
        public readonly Vector3 RawJointCenter;
        public readonly Vector3 LimbAxis;
        public readonly Vector3 Outward;
        public readonly Vector3 HingeAxis;
        public readonly float LimbLength;
        public readonly Quaternion FrameRotation;
        public readonly JointFrameSource Source;

        public JointFrame(
            Vector3 jointCenter,
            Vector3 rawJointCenter,
            Vector3 limbAxis,
            Vector3 outward,
            Vector3 hingeAxis,
            float limbLength,
            Quaternion frameRotation,
            JointFrameSource source)
        {
            JointCenter = jointCenter;
            RawJointCenter = rawJointCenter;
            LimbAxis = limbAxis;
            Outward = outward;
            HingeAxis = hingeAxis;
            LimbLength = limbLength;
            FrameRotation = frameRotation;
            Source = source;
        }

        /// <summary>
        /// ベースフレームへ per-config の outwardAngleOffset と pivotOffset を適用した新フレームを返す。
        /// pivotOffset は (x = Axial, y = Outward, z = Hinge)。
        /// </summary>
        public JointFrame WithOffsets(Vector3 pivotOffset, float outwardAngleOffsetDeg)
        {
            Vector3 outward = Outward;
            if (Mathf.Abs(outwardAngleOffsetDeg) > 1e-4f)
            {
                outward = (Quaternion.AngleAxis(outwardAngleOffsetDeg, LimbAxis) * outward).normalized;
            }

            Vector3 hinge = Vector3.Cross(LimbAxis, outward);
            if (hinge.sqrMagnitude < 1e-8f)
                hinge = HingeAxis;
            else
                hinge.Normalize();

            Quaternion rot = Quaternion.LookRotation(outward, LimbAxis);
            Vector3 center = RawJointCenter
                + LimbAxis * pivotOffset.x
                + outward * pivotOffset.y
                + hinge * pivotOffset.z;

            return new JointFrame(
                center,
                RawJointCenter,
                LimbAxis,
                outward,
                hinge,
                LimbLength,
                rot,
                Source);
        }
    }
}
