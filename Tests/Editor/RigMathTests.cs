using NUnit.Framework;
using UnityEngine;
using Dennokoworks.JointKeeper.Editor;

namespace Dennokoworks.JointKeeper.Tests
{
    public class RigMathTests
    {
        [TestCase(0f)]
        [TestCase(60f)]
        [TestCase(90f)]
        [TestCase(120f)]
        [TestCase(150f)]
        public void SymmetricAnchors_ProduceExactOutwardBisectorPush(float bendDeg)
        {
            Vector3 jointCenter = new Vector3(0f, 1.0f, 0f);
            Vector3 limbAxis = Vector3.down;      // +Y ではなく下向き (-Y)
            Vector3 outwardRest = Vector3.forward; // +Z が膝蓋骨方向
            Vector3 hingeAxis = Vector3.Cross(limbAxis, outwardRest).normalized; // (-Y) x (+Z) = -X
            float d90 = 0.004f; // 4 mm at 90 deg
            float alpha = 0.5f;

            RigMath.ComputeSymmetricAnchors(
                jointCenter, limbAxis, alpha, d90,
                out Vector3 anchorA, out Vector3 anchorB, out float w0, out float w1);

            Quaternion helperRestRot = Quaternion.LookRotation(outwardRest, limbAxis);

            // レスト姿勢でソース0 (Upper) とソース1 (Lower) は jointCenter にあり回転 Identity
            Matrix4x4 src0Rest = Matrix4x4.TRS(jointCenter, Quaternion.identity, Vector3.one);
            Matrix4x4 src1Rest = Matrix4x4.TRS(jointCenter, Quaternion.identity, Vector3.one);

            RigMath.ComputeSourceOffsets(src0Rest.inverse, Quaternion.identity, anchorA, helperRestRot, out var pOff0, out var rOff0);
            RigMath.ComputeSourceOffsets(src1Rest.inverse, Quaternion.identity, anchorB, helperRestRot, out var pOff1, out var rOff1);

            // 膝を後方 (-Z = -outwardRest) へ bendDeg 曲げる回転。
            // hingeAxis = Cross(limbAxis, outwardRest) なので、-bendDeg で limbAxis が -outwardRest 方向へ回る。
            Quaternion bendRot = JointKeeperPreviewPose.ComputeFlexionRotation(hingeAxis, bendDeg);
            Matrix4x4 src1Bent = Matrix4x4.TRS(jointCenter, bendRot, Vector3.one);

            // 下腿が解剖学的に後方 (-Z) へ曲がっていることを検証
            if (bendDeg > 1f && bendDeg < 179f)
            {
                Vector3 bentLowerDir = (bendRot * limbAxis).normalized;
                Assert.Greater(Vector3.Dot(bentLowerDir, -outwardRest), 0f, "正の曲げ角は Outward の反対側（内側）へ曲がること");
            }

            RigMath.EvaluateParentConstraint(
                src0Rest, Quaternion.identity, pOff0, rOff0, w0,
                src1Bent, bendRot, pOff1, rOff1, w1,
                out Vector3 pos, out Quaternion rot);

            Vector3 delta = pos - jointCenter;
            float expectedMag = Mathf.Sqrt(2f) * d90 * Mathf.Sin(bendDeg * 0.5f * Mathf.Deg2Rad);
            Assert.AreEqual(expectedMag, delta.magnitude, 1e-5f);

            if (bendDeg > 1f)
            {
                Vector3 expectedBisector = (JointKeeperPreviewPose.ComputeFlexionRotation(hingeAxis, bendDeg * 0.5f) * outwardRest).normalized;
                Assert.Greater(Vector3.Dot(delta.normalized, expectedBisector), 0.9999f);
            }
        }

        [Test]
        public void PureTwistAroundLimbAxis_ProducesZeroPushDisplacement()
        {
            Vector3 jointCenter = Vector3.zero;
            Vector3 limbAxis = Vector3.left;
            Vector3 outwardRest = Vector3.back;
            float d90 = 0.005f;

            RigMath.ComputeSymmetricAnchors(
                jointCenter, limbAxis, 0.5f, d90,
                out Vector3 anchorA, out Vector3 anchorB, out float w0, out float w1);

            Quaternion helperRestRot = Quaternion.LookRotation(outwardRest, limbAxis);
            Matrix4x4 rest = Matrix4x4.identity;
            RigMath.ComputeSourceOffsets(rest, Quaternion.identity, anchorA, helperRestRot, out var pOff0, out var rOff0);
            RigMath.ComputeSourceOffsets(rest, Quaternion.identity, anchorB, helperRestRot, out var pOff1, out var rOff1);

            Quaternion twist90 = Quaternion.AngleAxis(90f, limbAxis);
            Matrix4x4 src1Twisted = Matrix4x4.TRS(jointCenter, twist90, Vector3.one);

            RigMath.EvaluateParentConstraint(
                rest, Quaternion.identity, pOff0, rOff0, w0,
                src1Twisted, twist90, pOff1, rOff1, w1,
                out Vector3 pos, out _);

            Assert.AreEqual(0f, (pos - jointCenter).magnitude, 1e-6f);
        }

        [Test]
        public void PivotOffsetNonZero_ContinuousAcrossPushZero()
        {
            Vector3 rawCenter = Vector3.zero;
            Vector3 jointCenter = new Vector3(0f, 0.01f, 0.01f); // pivotOffset != 0
            Vector3 limbAxis = Vector3.down;
            Quaternion bend90 = Quaternion.AngleAxis(90f, Vector3.left);

            Vector3 EvalAtPush(float push)
            {
                RigMath.ComputeSymmetricAnchors(jointCenter, limbAxis, 0.5f, push, out var a, out var b, out var w0, out var w1);
                Matrix4x4 rest = Matrix4x4.TRS(rawCenter, Quaternion.identity, Vector3.one);
                RigMath.ComputeSourceOffsets(rest.inverse, Quaternion.identity, a, Quaternion.identity, out var p0, out var r0);
                RigMath.ComputeSourceOffsets(rest.inverse, Quaternion.identity, b, Quaternion.identity, out var p1, out var r1);
                Matrix4x4 bent = Matrix4x4.TRS(rawCenter, bend90, Vector3.one);
                RigMath.EvaluateParentConstraint(rest, Quaternion.identity, p0, r0, w0, bent, bend90, p1, r1, w1, out var pos, out _);
                return pos;
            }

            Vector3 pZero = EvalAtPush(0f);
            Vector3 pEps = EvalAtPush(1e-5f);
            Assert.Less((pEps - pZero).magnitude, 2e-5f);
        }

        [Test]
        public void PreviewFlexionRotation_BendsElbowsForwardAndKneesBackward()
        {
            // 左肘（T ポーズ -X, 外側 -Z）: 90° 曲げで前腕が前方 (+Z) へ曲がること
            var leftElbow = JointFrameSolver.Solve(
                JointId.LeftElbow,
                new Vector3(-0.2f, 1.2f, 0f),
                new Vector3(-0.45f, 1.2f, 0f),
                new Vector3(-0.70f, 1.2f, 0f),
                Vector3.forward,
                Vector3.up);
            Vector3 leftForearmBent = JointKeeperPreviewPose.ComputeFlexionRotation(leftElbow.HingeAxis, 90f) * leftElbow.LimbAxis;
            Assert.Greater(Vector3.Dot(leftForearmBent.normalized, Vector3.forward), 0.999f);

            // 右肘（T ポーズ +X, 外側 -Z）: 90° 曲げで前腕が前方 (+Z) へ曲がること
            var rightElbow = JointFrameSolver.Solve(
                JointId.RightElbow,
                new Vector3(0.2f, 1.2f, 0f),
                new Vector3(0.45f, 1.2f, 0f),
                new Vector3(0.70f, 1.2f, 0f),
                Vector3.forward,
                Vector3.up);
            Vector3 rightForearmBent = JointKeeperPreviewPose.ComputeFlexionRotation(rightElbow.HingeAxis, 90f) * rightElbow.LimbAxis;
            Assert.Greater(Vector3.Dot(rightForearmBent.normalized, Vector3.forward), 0.999f);

            // 膝（下向き -Y, 外側 +Z）: 90° 曲げで下腿が後方 (-Z) へ曲がること
            var leftKnee = JointFrameSolver.Solve(
                JointId.LeftKnee,
                new Vector3(-0.1f, 0.9f, 0f),
                new Vector3(-0.1f, 0.5f, 0f),
                new Vector3(-0.1f, 0.1f, 0f),
                Vector3.forward,
                Vector3.up);
            Vector3 leftShinBent = JointKeeperPreviewPose.ComputeFlexionRotation(leftKnee.HingeAxis, 90f) * leftKnee.LimbAxis;
            Assert.Greater(Vector3.Dot(leftShinBent.normalized, Vector3.back), 0.999f);
        }
    }
}
