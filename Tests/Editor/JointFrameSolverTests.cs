using NUnit.Framework;
using UnityEngine;
using Dennokoworks.JointKeeper;
using Dennokoworks.JointKeeper.Editor;

namespace Dennokoworks.JointKeeper.Tests
{
    public class JointFrameSolverTests
    {
        [Test]
        public void StraightTPose_FallsBackToPrior()
        {
            var elbow = JointFrameSolver.Solve(
                JointId.LeftElbow,
                new Vector3(-0.2f, 1.2f, 0f),
                new Vector3(-0.45f, 1.2f, 0f),
                new Vector3(-0.70f, 1.2f, 0f),
                Vector3.forward,
                Vector3.up);

            Assert.AreEqual(JointFrameSource.Prior, elbow.Source);
            Assert.Greater(Vector3.Dot(elbow.Outward, Vector3.back), 0.999f);

            var knee = JointFrameSolver.Solve(
                JointId.LeftKnee,
                new Vector3(-0.1f, 0.9f, 0f),
                new Vector3(-0.1f, 0.5f, 0f),
                new Vector3(-0.1f, 0.1f, 0f),
                Vector3.forward,
                Vector3.up);

            Assert.AreEqual(JointFrameSource.Prior, knee.Source);
            Assert.Greater(Vector3.Dot(knee.Outward, Vector3.forward), 0.999f);
        }

        [Test]
        public void SlightNaturalBend_UsesRestBend()
        {
            // 膝が前方 (+Z) へ 5° 曲がっている
            var knee = JointFrameSolver.Solve(
                JointId.LeftKnee,
                new Vector3(-0.1f, 0.9f, 0f),
                new Vector3(-0.1f, 0.5f, 0.03f),
                new Vector3(-0.1f, 0.1f, 0f),
                Vector3.forward,
                Vector3.up);

            Assert.AreEqual(JointFrameSource.RestBend, knee.Source);
            Assert.Greater(Vector3.Dot(knee.Outward, Vector3.forward), 0.99f);
        }

        [Test]
        public void CarryingAngle15DegLateralBend_DoesNotMisclassifySidewaysAsOutward()
        {
            // 左腕が -X 方向へ伸び、前腕が下方向 (-Y) へ 15° 折れている（キャリングアングル）
            Vector3 upper = new Vector3(-0.2f, 1.2f, 0f);
            Vector3 lower = new Vector3(-0.45f, 1.2f, 0f);
            Vector3 child = lower + Quaternion.Euler(0f, 0f, 15f) * new Vector3(-0.25f, 0f, 0f);

            var elbow = JointFrameSolver.Solve(
                JointId.LeftElbow, upper, lower, child, Vector3.forward, Vector3.up);

            // 横方向曲がりはプライオリ (-Z) と直交するため 0.7 閾値で棄却 → Prior 採用
            Assert.AreEqual(JointFrameSource.Prior, elbow.Source);
            Assert.Greater(Vector3.Dot(elbow.Outward, Vector3.back), 0.95f);
        }

        [Test]
        public void MeasuredHingeAxis_TakesPriorityOverRestBendAndPrior()
        {
            // 斜め 30° 傾いた実測ヒンジ軸
            Vector3 measuredHinge = Quaternion.Euler(30f, 0f, 0f) * Vector3.up;
            var elbow = JointFrameSolver.Solve(
                JointId.LeftElbow,
                new Vector3(-0.2f, 1.2f, 0f),
                new Vector3(-0.45f, 1.2f, 0f),
                new Vector3(-0.70f, 1.2f, 0f),
                Vector3.forward,
                Vector3.up,
                measuredHingeAxis: measuredHinge);

            Assert.AreEqual(JointFrameSource.Measured, elbow.Source);
            // Outward は実測ヒンジ軸と直交し、かつ Prior (-Z) 側を向く
            Assert.AreEqual(0f, Vector3.Dot(elbow.Outward, measuredHinge), 1e-4f);
            Assert.Greater(Vector3.Dot(elbow.Outward, Vector3.back), 0.5f);
        }

        [Test]
        public void MirrorConfig_InvertsHingeOffsetAndOutwardAngle()
        {
            var leftCfg = new JointConfig
            {
                pivotOffset = new Vector3(0.01f, 0.02f, 0.03f),
                outwardAngleOffset = 15f,
                strength = 0.7f,
            };
            var rightCfg = new JointConfig();
            rightCfg.CopyMirroredNumericsFrom(leftCfg);

            Assert.AreEqual(0.01f, rightCfg.pivotOffset.x, 1e-6f);
            Assert.AreEqual(0.02f, rightCfg.pivotOffset.y, 1e-6f);
            Assert.AreEqual(-0.03f, rightCfg.pivotOffset.z, 1e-6f);
            Assert.AreEqual(-15f, rightCfg.outwardAngleOffset, 1e-6f);
            Assert.AreEqual(0.7f, rightCfg.strength, 1e-6f);
        }
    }
}
