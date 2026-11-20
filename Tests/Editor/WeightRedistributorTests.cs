using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Dennokoworks.JointKeeper;
using Dennokoworks.JointKeeper.Editor;

namespace Dennokoworks.JointKeeper.Tests
{
    public class WeightRedistributorTests
    {
        private static JointFrame CreateStandardElbowFrame()
        {
            return JointFrameSolver.Solve(
                JointId.LeftElbow,
                upperPos: new Vector3(-0.2f, 1.2f, 0f),
                lowerPos: new Vector3(-0.45f, 1.2f, 0f),
                childPos: new Vector3(-0.70f, 1.2f, 0f),
                avatarForward: Vector3.forward,
                avatarUp: Vector3.up);
        }

        private static WeightRedistributionParams CreateParams(
            JointFrame frame,
            float alpha = 0.5f,
            float strength = 1.0f,
            float gamma = 1.0f,
            float snapThreshold = 0.05f,
            float noiseThreshold = 0.03f)
        {
            return new WeightRedistributionParams(
                frame: frame,
                bendFollowRatio: alpha,
                strength: strength,
                blendCurvePower: gamma,
                minGroupWeightThreshold: 0.01f,
                axialRadius: 0.1f,
                radialRadius: 0.12f,
                innerFalloffRatio: 0.5f,
                innerCutoff: -0.15f,
                outerFull: 0.35f,
                innerRetention: 0.0f,
                slotSnapThreshold: snapThreshold,
                noisePruneThreshold: noiseThreshold);
        }

        private static float GetWeightForBone(BoneWeight bw, int boneIdx)
        {
            float sum = 0f;
            if (bw.boneIndex0 == boneIdx) sum += bw.weight0;
            if (bw.boneIndex1 == boneIdx) sum += bw.weight1;
            if (bw.boneIndex2 == boneIdx) sum += bw.weight2;
            if (bw.boneIndex3 == boneIdx) sum += bw.weight3;
            return sum;
        }

        private static void AssertValidSortedNormalized(BoneWeight bw)
        {
            Assert.GreaterOrEqual(bw.weight0, 0f);
            Assert.GreaterOrEqual(bw.weight1, 0f);
            Assert.GreaterOrEqual(bw.weight2, 0f);
            Assert.GreaterOrEqual(bw.weight3, 0f);
            Assert.GreaterOrEqual(bw.weight0, bw.weight1);
            Assert.GreaterOrEqual(bw.weight1, bw.weight2);
            Assert.GreaterOrEqual(bw.weight2, bw.weight3);
            float sum = bw.weight0 + bw.weight1 + bw.weight2 + bw.weight3;
            Assert.AreEqual(1.0f, sum, 1e-5f);
        }

        [TestCase(0.50f, 0.50f, 0.5f, 1.0f)]
        [TestCase(0.75f, 0.25f, 0.5f, 1.0f)]
        [TestCase(0.90f, 0.10f, 0.5f, 1.5f)]
        [TestCase(0.60f, 0.40f, 0.4f, 2.0f)]
        public void AngularMomentumConserved_AndNormalized(float wP, float wD, float alpha, float gamma)
        {
            var frame = CreateStandardElbowFrame();
            var p = CreateParams(frame, alpha: alpha, strength: 0.85f, gamma: gamma);

            // 外側 (肘頭側: -Z) かつ中心付近
            Vector3 outerPos = frame.JointCenter + frame.Outward * 0.03f;
            var orig = new[] { new BoneWeight { boneIndex0 = 0, weight0 = wP, boneIndex1 = 1, weight1 = wD } };
            var verts = new[] { outerPos };

            var res = WeightRedistributor.Redistribute(
                orig, verts,
                new HashSet<int> { 0 },
                new HashSet<int> { 1 },
                new HashSet<int> { 0, 1 },
                helperBoneIndex: 10,
                in p);

            Assert.AreEqual(1, res.Stats.AffectedVertices);
            BoneWeight nw = res.NewWeights[0];
            AssertValidSortedNormalized(nw);

            float newD = GetWeightForBone(nw, 1);
            float newH = GetWeightForBone(nw, 10);
            Assert.Greater(newH, 0f);
            // 角モーメント保存: W'_D + alpha * w_H == W_D
            Assert.AreEqual(wD, newD + alpha * newH, 1e-5f);
        }

        [Test]
        public void InnerCreaseAndOutOfRoi_AreBitForBitUnchanged()
        {
            var frame = CreateStandardElbowFrame();
            var p = CreateParams(frame, strength: 1.0f);

            var bwInner = new BoneWeight { boneIndex0 = 0, weight0 = 0.5f, boneIndex1 = 1, weight1 = 0.5f };
            var bwFar = new BoneWeight { boneIndex0 = 0, weight0 = 0.5f, boneIndex1 = 1, weight1 = 0.5f };

            var orig = new[] { bwInner, bwFar };
            var verts = new[]
            {
                frame.JointCenter - frame.Outward * 0.03f, // 内側折り目 (c = -1)
                frame.JointCenter + frame.LimbAxis * 0.5f, // ROI 外
            };

            var res = WeightRedistributor.Redistribute(
                orig, verts,
                new HashSet<int> { 0 },
                new HashSet<int> { 1 },
                new HashSet<int> { 0, 1 },
                helperBoneIndex: 10,
                in p);

            Assert.AreEqual(0, res.Stats.AffectedVertices);
            Assert.AreEqual(bwInner, res.NewWeights[0]);
            Assert.AreEqual(bwFar, res.NewWeights[1]);
        }

        [Test]
        public void FourBoneLimit_Tier1_IntraGroupTwistMerge_PreservesAngularMomentum()
        {
            var frame = CreateStandardElbowFrame();
            // strength = 0.5 なので片側グループが 0 にならず 5 ボーン衝突が発生する
            var p = CreateParams(frame, alpha: 0.5f, strength: 0.5f, gamma: 1.0f);

            // 0=Upper, 2=UpperTwist (G_P), 1=Lower, 3=LowerTwist (G_D)
            var orig = new[]
            {
                new BoneWeight
                {
                    boneIndex0 = 0, weight0 = 0.35f,
                    boneIndex1 = 1, weight1 = 0.35f,
                    boneIndex2 = 2, weight2 = 0.15f,
                    boneIndex3 = 3, weight3 = 0.15f,
                }
            };
            var verts = new[] { frame.JointCenter + frame.Outward * 0.03f };

            var res = WeightRedistributor.Redistribute(
                orig, verts,
                new HashSet<int> { 0, 2 },
                new HashSet<int> { 1, 3 },
                new HashSet<int> { 0, 1 },
                helperBoneIndex: 10,
                in p);

            Assert.AreEqual(1, res.Stats.Tier1IntraGroupCount);
            Assert.AreEqual(VertexSlotTier.Tier1IntraGroup, res.VertexTiers[0]);
            BoneWeight nw = res.NewWeights[0];
            AssertValidSortedNormalized(nw);

            float newD = GetWeightForBone(nw, 1) + GetWeightForBone(nw, 3);
            float newH = GetWeightForBone(nw, 10);
            // 元 W_D = 0.50 → 角モーメント完全保存
            Assert.AreEqual(0.50f, newD + 0.5f * newH, 1e-5f);
        }

        [Test]
        public void FourBoneLimit_Tier2_SnapsTinyJointToHelper_PreservesPhysBonesBitForBit()
        {
            var frame = CreateStandardElbowFrame();
            var p = CreateParams(frame, alpha: 0.5f, strength: 0.85f, gamma: 1.0f, snapThreshold: 0.05f);

            // 0=Upper(0.30), 1=Lower(0.20), 20=PB1(0.30), 21=PB2(0.20)
            // wHMax = 0.40, wH = 0.34 → deltaP = 0.17 (W'_P=0.13), deltaD = 0.17 (W'_D=0.03 <= 0.05) → Tier 2!
            var orig = new[]
            {
                new BoneWeight
                {
                    boneIndex0 = 0,  weight0 = 0.30f,
                    boneIndex1 = 20, weight1 = 0.30f,
                    boneIndex2 = 1,  weight2 = 0.20f,
                    boneIndex3 = 21, weight3 = 0.20f,
                }
            };
            var verts = new[] { frame.JointCenter + frame.Outward * 0.03f };

            var res = WeightRedistributor.Redistribute(
                orig, verts,
                new HashSet<int> { 0 },
                new HashSet<int> { 1 },
                new HashSet<int> { 0, 1 },
                helperBoneIndex: 10,
                in p);

            Assert.AreEqual(1, res.Stats.Tier2SnapJointCount);
            AssertValidSortedNormalized(res.NewWeights[0]);
            // PhysBone ウェイトがビット単位で不変であること (Invariant 4)
            Assert.AreEqual(0.30f, GetWeightForBone(res.NewWeights[0], 20));
            Assert.AreEqual(0.20f, GetWeightForBone(res.NewWeights[0], 21));
        }

        [Test]
        public void FourBoneLimit_Tier3_PrunesTinyExternalNoise()
        {
            var frame = CreateStandardElbowFrame();
            var p = CreateParams(frame, alpha: 0.5f, strength: 0.5f, gamma: 1.0f, snapThreshold: 0.05f, noiseThreshold: 0.03f);

            // 0=Upper(0.35), 1=Lower(0.35), 20=PB1(0.28), 21=NoiseChest(0.02 <= 0.03)
            var orig = new[]
            {
                new BoneWeight
                {
                    boneIndex0 = 0,  weight0 = 0.35f,
                    boneIndex1 = 1,  weight1 = 0.35f,
                    boneIndex2 = 20, weight2 = 0.28f,
                    boneIndex3 = 21, weight3 = 0.02f,
                }
            };
            var verts = new[] { frame.JointCenter + frame.Outward * 0.03f };

            var res = WeightRedistributor.Redistribute(
                orig, verts,
                new HashSet<int> { 0 },
                new HashSet<int> { 1 },
                new HashSet<int> { 0, 1 },
                helperBoneIndex: 10,
                in p);

            Assert.AreEqual(1, res.Stats.Tier3NoisePruneCount);
            AssertValidSortedNormalized(res.NewWeights[0]);
            Assert.AreEqual(0f, GetWeightForBone(res.NewWeights[0], 21));
            Assert.AreEqual(0.30f, GetWeightForBone(res.NewWeights[0], 20), 1e-5f);
        }

        [Test]
        public void FourBoneLimit_Tier4Skip_ProtectsImportantExternalBones()
        {
            var frame = CreateStandardElbowFrame();
            // strength = 0.2 → wH = 0.06 < origMinWeight (0.15) → Tier 4 Skip
            var p = CreateParams(frame, alpha: 0.5f, strength: 0.2f, gamma: 1.0f, snapThreshold: 0.05f, noiseThreshold: 0.03f);

            var origBw = new BoneWeight
            {
                boneIndex0 = 20, weight0 = 0.35f,
                boneIndex1 = 21, weight1 = 0.35f,
                boneIndex2 = 0,  weight2 = 0.15f,
                boneIndex3 = 1,  weight3 = 0.15f,
            };
            var verts = new[] { frame.JointCenter + frame.Outward * 0.03f };

            var res = WeightRedistributor.Redistribute(
                new[] { origBw }, verts,
                new HashSet<int> { 0 },
                new HashSet<int> { 1 },
                new HashSet<int> { 0, 1 },
                helperBoneIndex: 10,
                in p);

            Assert.AreEqual(1, res.Stats.Tier4SkippedCount);
            Assert.AreEqual(0, res.Stats.AffectedVertices);
            Assert.AreEqual(origBw, res.NewWeights[0]);
        }

        [Test]
        public void UvSeamSplitVertices_ProduceIdenticalWeights()
        {
            var frame = CreateStandardElbowFrame();
            var p = CreateParams(frame, strength: 0.85f, gamma: 1.2f);

            Vector3 pos = frame.JointCenter + frame.Outward * 0.025f + frame.LimbAxis * 0.01f;
            var bw = new BoneWeight { boneIndex0 = 0, weight0 = 0.62f, boneIndex1 = 1, weight1 = 0.38f };

            var res = WeightRedistributor.Redistribute(
                new[] { bw, bw },
                new[] { pos, pos },
                new HashSet<int> { 0 },
                new HashSet<int> { 1 },
                new HashSet<int> { 0, 1 },
                helperBoneIndex: 10,
                in p);

            Assert.AreEqual(res.NewWeights[0], res.NewWeights[1]);
        }

        [Test]
        public void NonUnitInputWeights_IncrementNonUnitInputCount()
        {
            var frame = CreateStandardElbowFrame();
            var p = CreateParams(frame, strength: 0.85f);

            // 元データ総和が 0.80 (非正規化入力)
            var bw = new BoneWeight { boneIndex0 = 0, weight0 = 0.40f, boneIndex1 = 1, weight1 = 0.40f };
            var verts = new[] { frame.JointCenter + frame.Outward * 0.03f };

            var res = WeightRedistributor.Redistribute(
                new[] { bw }, verts,
                new HashSet<int> { 0 },
                new HashSet<int> { 1 },
                new HashSet<int> { 0, 1 },
                helperBoneIndex: 10,
                in p);

            Assert.AreEqual(1, res.Stats.NonUnitInputCount);
            AssertValidSortedNormalized(res.NewWeights[0]);
        }
    }
}
