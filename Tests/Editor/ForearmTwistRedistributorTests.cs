using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Dennokoworks.JointKeeper;
using Dennokoworks.JointKeeper.Editor;

namespace Dennokoworks.JointKeeper.Tests
{
    /// <summary>
    /// 前腕ツイスト分散のウェイト振り替え (FEATURE_ForearmTwist §5, 不変条件 11・12)。
    /// 左腕 T ポーズ: 肘 (-0.45, 1.2, 0)、前腕は -X 方向、前腕長 0.25 m。
    /// ボーン: 0 = UpperArm, 1 = LowerArm（剛体遠位）, 2 = PhysBone, 3 = LowerArm_Twist（Constraint 駆動）, 4 = Hand。
    /// ノット: 10 = N, 11 = T_1, 12 = T_2, 1 = LowerArm（f = 1 の最上位）。
    /// </summary>
    public class ForearmTwistRedistributorTests
    {
        private static readonly Vector3 Elbow = new Vector3(-0.45f, 1.2f, 0f);
        private static readonly Vector3 RollAxis = Vector3.left;
        private const float ForearmLength = 0.25f;

        private static ForearmTwistParams CreateParams(
            float follow = 1f, int segments = 3, float snap = 0f,
            float start = 0f, float end = 1f, TwistCurve curve = TwistCurve.Linear)
        {
            return new ForearmTwistParams(
                Elbow, RollAxis, ForearmLength, start, end, curve, 1f,
                RigMath.ComputeTwistKnots(follow, segments), snap);
        }

        private static int[] Knots3Full => new[] { 10, 11, 12, 1 };

        private static Vector3 AlongForearm(float s, float radial = 0.03f)
        {
            return Elbow + RollAxis * (s * ForearmLength) + Vector3.forward * radial;
        }

        private static float WeightOf(BoneWeight bw, int idx)
        {
            float sum = 0f;
            if (bw.boneIndex0 == idx) sum += bw.weight0;
            if (bw.boneIndex1 == idx) sum += bw.weight1;
            if (bw.boneIndex2 == idx) sum += bw.weight2;
            if (bw.boneIndex3 == idx) sum += bw.weight3;
            return sum;
        }

        private static int CountNonZero(BoneWeight bw)
        {
            int n = 0;
            if (bw.weight0 > 1e-6f) n++;
            if (bw.weight1 > 1e-6f) n++;
            if (bw.weight2 > 1e-6f) n++;
            if (bw.weight3 > 1e-6f) n++;
            return n;
        }

        private static void AssertSortedNormalized(BoneWeight bw)
        {
            Assert.GreaterOrEqual(bw.weight0, bw.weight1);
            Assert.GreaterOrEqual(bw.weight1, bw.weight2);
            Assert.GreaterOrEqual(bw.weight2, bw.weight3);
            Assert.GreaterOrEqual(bw.weight3, 0f);
            Assert.AreEqual(1f, bw.weight0 + bw.weight1 + bw.weight2 + bw.weight3, 1e-5f);
        }

        private static HashSet<int> Rigid => new HashSet<int> { 1 };

        [TestCase(0.0f)]
        [TestCase(0.2f)]
        [TestCase(0.5f)]
        [TestCase(0.75f)]
        public void ChainWeightTotal_EqualsOriginalRigidWeight_AndUsesAdjacentKnots(float s)
        {
            var weights = new[]
            {
                new BoneWeight { boneIndex0 = 1, weight0 = 0.7f, boneIndex1 = 0, weight1 = 0.3f },
            };
            var p = CreateParams();
            var res = ForearmTwistRedistributor.Redistribute(weights, new[] { AlongForearm(s) }, Rigid, Knots3Full, in p);

            BoneWeight bw = res.NewWeights[0];
            AssertSortedNormalized(bw);
            Assert.AreEqual(0.3f, WeightOf(bw, 0), 1e-6f, "G_P (UpperArm) は変えない");

            float chain = WeightOf(bw, 10) + WeightOf(bw, 11) + WeightOf(bw, 12) + WeightOf(bw, 1);
            Assert.AreEqual(0.7f, chain, 1e-5f, "不変条件 11: ツイスト総量の保存");

            // 不変条件 12: 参照するノットは隣接 2 本以下
            var used = new List<int>();
            int[] knots = Knots3Full;
            for (int j = 0; j < knots.Length; j++)
                if (WeightOf(bw, knots[j]) > 1e-6f) used.Add(j);
            Assert.LessOrEqual(used.Count, 2);
            if (used.Count == 2) Assert.AreEqual(1, used[1] - used[0]);

            // τ = f·c(s) = s（線形・f=1）
            Assert.AreEqual(s, res.TauValues[0], 1e-4f);
        }

        [Test]
        public void ElbowSideVertex_GoesToBendOnlyBase()
        {
            // 肘より上腕側（z < 0）でも G_D ウェイトを持つ頂点は N（τ = 0）に乗る
            var weights = new[] { new BoneWeight { boneIndex0 = 0, weight0 = 0.6f, boneIndex1 = 1, weight1 = 0.4f } };
            var p = CreateParams();
            var res = ForearmTwistRedistributor.Redistribute(weights, new[] { AlongForearm(-0.1f) }, Rigid, Knots3Full, in p);

            Assert.AreEqual(0.4f, WeightOf(res.NewWeights[0], 10), 1e-5f);
            Assert.AreEqual(0f, WeightOf(res.NewWeights[0], 1), 1e-6f);
            Assert.AreEqual(0f, res.TauValues[0], 1e-6f);
        }

        [Test]
        public void WristVertex_WithFullFollow_IsBitwiseUnchanged()
        {
            var orig = new BoneWeight { boneIndex0 = 1, weight0 = 0.8f, boneIndex1 = 4, weight1 = 0.2f };
            var p = CreateParams(follow: 1f);
            var res = ForearmTwistRedistributor.Redistribute(new[] { orig }, new[] { AlongForearm(1.0f) }, Rigid, Knots3Full, in p);

            Assert.IsFalse(res.ModifiedMask[0]);
            Assert.AreEqual(orig, res.NewWeights[0], "f = 1 の手首端は LowerArm のまま（ビット単位で不変）");
        }

        [Test]
        public void ZeroFollow_PutsEverythingOnBase()
        {
            var weights = new[]
            {
                new BoneWeight { boneIndex0 = 1, weight0 = 1f },
                new BoneWeight { boneIndex0 = 1, weight0 = 1f },
            };
            var verts = new[] { AlongForearm(0.3f), AlongForearm(1.0f) };
            var p = CreateParams(follow: 0f);
            Assert.AreEqual(1, p.Knots.Length);

            var res = ForearmTwistRedistributor.Redistribute(weights, verts, Rigid, new[] { 10 }, in p);
            for (int v = 0; v < 2; v++)
            {
                Assert.AreEqual(1f, WeightOf(res.NewWeights[v], 10), 1e-6f);
                Assert.AreEqual(0f, res.TauValues[v], 1e-6f);
            }
        }

        [Test]
        public void ExternalAndConstraintDrivenWeights_AreBitwisePreserved()
        {
            // PhysBone (2) と Constraint 駆動ツイスト (3) は剛体遠位ではないため一切変更しない
            var orig = new BoneWeight
            {
                boneIndex0 = 1, weight0 = 0.5f,
                boneIndex1 = 2, weight1 = 0.3f,
                boneIndex2 = 3, weight2 = 0.2f,
            };
            var p = CreateParams();
            var res = ForearmTwistRedistributor.Redistribute(new[] { orig }, new[] { AlongForearm(0.5f) }, Rigid, Knots3Full, in p);

            BoneWeight bw = res.NewWeights[0];
            Assert.AreEqual(0.3f, WeightOf(bw, 2), 0f);
            Assert.AreEqual(0.2f, WeightOf(bw, 3), 0f);
            AssertSortedNormalized(bw);
        }

        [Test]
        public void FourSlotsFull_QuantizesToNearestKnot_WithoutTouchingExternal()
        {
            var orig = new BoneWeight
            {
                boneIndex0 = 1, weight0 = 0.4f,
                boneIndex1 = 0, weight1 = 0.3f,
                boneIndex2 = 2, weight2 = 0.2f,
                boneIndex3 = 3, weight3 = 0.1f,
            };
            // s = 0.45 → c·n = 1.35 → j = 1, u = 0.35（丸めなしでは 5 本）→ 最寄りの T_1 (11) へ
            var p = CreateParams(snap: 0f);
            var res = ForearmTwistRedistributor.Redistribute(new[] { orig }, new[] { AlongForearm(0.45f) }, Rigid, Knots3Full, in p);

            BoneWeight bw = res.NewWeights[0];
            Assert.LessOrEqual(CountNonZero(bw), 4);
            Assert.AreEqual(0.4f, WeightOf(bw, 11), 1e-5f);
            Assert.AreEqual(0.3f, WeightOf(bw, 0), 0f);
            Assert.AreEqual(0.2f, WeightOf(bw, 2), 0f);
            Assert.AreEqual(0.1f, WeightOf(bw, 3), 0f);
            Assert.AreEqual(1, res.Stats.QuantizedCount);
            Assert.IsTrue(res.QuantizedMask[0]);
            AssertSortedNormalized(bw);
        }

        [Test]
        public void SnapThreshold_CollapsesNearKnotVerticesToSingleBone()
        {
            var weights = new[] { new BoneWeight { boneIndex0 = 1, weight0 = 1f } };
            // c·n = 1.05 → u = 0.05 ≦ 0.15 → T_1 のみ
            var p = CreateParams(snap: 0.15f);
            var res = ForearmTwistRedistributor.Redistribute(weights, new[] { AlongForearm(0.35f) }, Rigid, Knots3Full, in p);

            Assert.AreEqual(1f, WeightOf(res.NewWeights[0], 11), 1e-5f);
            Assert.AreEqual(1, CountNonZero(res.NewWeights[0]));
            Assert.AreEqual(1, res.Stats.SnapSavedCount);
        }

        [Test]
        public void InvertedRatios_AreGuarded_NoNaN()
        {
            var weights = new[] { new BoneWeight { boneIndex0 = 1, weight0 = 1f } };
            var p = CreateParams(start: 0.5f, end: 0.3f);
            var res = ForearmTwistRedistributor.Redistribute(weights, new[] { AlongForearm(0.4f) }, Rigid, Knots3Full, in p);

            BoneWeight bw = res.NewWeights[0];
            Assert.IsFalse(float.IsNaN(bw.weight0));
            AssertSortedNormalized(bw);
        }

        [Test]
        public void SeamDuplicates_ProduceIdenticalWeights()
        {
            var w = new BoneWeight { boneIndex0 = 1, weight0 = 0.65f, boneIndex1 = 0, weight1 = 0.35f };
            Vector3 pos = AlongForearm(0.42f);
            var p = CreateParams();
            var res = ForearmTwistRedistributor.Redistribute(new[] { w, w }, new[] { pos, pos }, Rigid, Knots3Full, in p);
            Assert.AreEqual(res.NewWeights[0], res.NewWeights[1]);
        }

        [Test]
        public void UnweightedVertex_IsUntouched()
        {
            var orig = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            var p = CreateParams();
            var res = ForearmTwistRedistributor.Redistribute(new[] { orig }, new[] { AlongForearm(0.5f) }, Rigid, Knots3Full, in p);
            Assert.IsFalse(res.ModifiedMask[0]);
            Assert.AreEqual(orig, res.NewWeights[0]);
            Assert.AreEqual(-1f, res.TauValues[0]);
        }

        [Test]
        public void UsedKnots_AndRemap_CompactPlaceholders()
        {
            // s = 0.1 → ノット 0 と 1 のみ使用。プレースホルダー 10, 11, 12 → 最終 20, 21（12 は未使用）
            var weights = new[] { new BoneWeight { boneIndex0 = 1, weight0 = 1f } };
            var p = CreateParams(snap: 0f);
            var res = ForearmTwistRedistributor.Redistribute(weights, new[] { AlongForearm(0.1f) }, Rigid, Knots3Full, in p);

            Assert.IsTrue(res.UsedKnots[0]);
            Assert.IsTrue(res.UsedKnots[1]);
            Assert.IsFalse(res.UsedKnots[2]);

            ForearmTwistRedistributor.RemapPlaceholderIndices(res.NewWeights, res.ModifiedMask, 10, new[] { 20, 21, -1 });
            BoneWeight bw = res.NewWeights[0];
            Assert.AreEqual(1f, WeightOf(bw, 20) + WeightOf(bw, 21), 1e-5f);
            Assert.AreEqual(0f, WeightOf(bw, 10) + WeightOf(bw, 11), 1e-6f);
        }

        [Test]
        public void Knots_AreUniformInFollow()
        {
            CollectionAssert.AreEqual(new[] { 0f, 1f / 3f, 2f / 3f, 1f }, RigMath.ComputeTwistKnots(1f, 3));
            float[] half = RigMath.ComputeTwistKnots(0.5f, 2);
            Assert.AreEqual(3, half.Length);
            Assert.AreEqual(0.25f, half[1], 1e-6f);
            Assert.AreEqual(0.5f, half[2], 1e-6f);
            Assert.AreEqual(1, RigMath.ComputeTwistKnots(0f, 3).Length);
        }
    }
}
