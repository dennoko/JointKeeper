using System.Collections.Generic;
using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    internal readonly struct ForearmTwistParams
    {
        /// <summary>J: 肘中心（DistalSource のワールド位置）。</summary>
        public readonly Vector3 ElbowCenter;
        /// <summary>â: 前腕ロール軸（肘→手首向き単位ベクトル）。</summary>
        public readonly Vector3 RollAxis;
        /// <summary>L_L: 前腕長。</summary>
        public readonly float ForearmLength;
        public readonly float StartRatio;
        public readonly float EndRatio;
        public readonly TwistCurve Curve;
        public readonly float CurvePower;
        /// <summary>k_j（ノットごとのロール追従率）。長さ 1 なら N のみ。</summary>
        public readonly float[] Knots;
        public readonly float SnapThreshold;

        public ForearmTwistParams(
            Vector3 elbowCenter,
            Vector3 rollAxis,
            float forearmLength,
            float startRatio,
            float endRatio,
            TwistCurve curve,
            float curvePower,
            float[] knots,
            float snapThreshold)
        {
            ElbowCenter = elbowCenter;
            RollAxis = rollAxis.sqrMagnitude > 1e-12f ? rollAxis.normalized : Vector3.down;
            ForearmLength = Mathf.Max(forearmLength, 1e-4f);
            StartRatio = startRatio;
            EndRatio = Mathf.Max(endRatio, startRatio + 1e-4f);
            Curve = curve;
            CurvePower = Mathf.Max(1f, curvePower);
            Knots = (knots != null && knots.Length > 0) ? knots : new[] { 0f };
            SnapThreshold = Mathf.Clamp(snapThreshold, 0f, 0.5f);
        }

        public static ForearmTwistParams FromConfig(
            ForearmTwistConfig config,
            Vector3 elbowCenter,
            Vector3 rollAxis,
            float forearmLength)
        {
            return new ForearmTwistParams(
                elbowCenter,
                rollAxis,
                forearmLength,
                config.startRatio,
                config.EffectiveEndRatio,
                config.curve,
                config.curvePower,
                RigMath.ComputeTwistKnots(config.twistFollow, config.segments),
                config.twistSnapThreshold);
        }
    }

    internal struct ForearmTwistStats
    {
        public int TotalVertices;
        public int AffectedVertices;
        /// <summary>4 ボーン超過のため最寄りノットへ丸めた頂点数 (§5.4)。</summary>
        public int QuantizedCount;
        /// <summary>twistSnapThreshold により単一ボーンへまとめた頂点数。</summary>
        public int SnapSavedCount;
    }

    internal sealed class ForearmTwistResult
    {
        public BoneWeight[] NewWeights;
        public bool[] ModifiedMask;
        /// <summary>頂点ごとのロール追従率 τ（未処理頂点は -1）。ヒートマップ用。</summary>
        public float[] TauValues;
        /// <summary>ノットごとに、変更頂点から 1 度でも参照されたか（遅延生成用）。</summary>
        public bool[] UsedKnots;
        /// <summary>4 ボーン制限のため最寄りノットへ丸めた頂点（ヒートマップ診断用）。</summary>
        public bool[] QuantizedMask;
        public ForearmTwistStats Stats;
    }

    /// <summary>
    /// 前腕ツイスト分散のウェイト振り替え (FEATURE_ForearmTwist §5)。Unity シーン非依存の純粋計算。
    /// 剛体遠位ボーン群 G_D^rigid のウェイト W_R だけを、隣接 2 ノット（N, T_1, …, top）へ線形補間で振り替える。
    /// それ以外のスロット（G_P・肘補助・PhysBone・Constraint 駆動遠位）はビット単位で保持する。
    /// </summary>
    internal static class ForearmTwistRedistributor
    {
        private const float ZeroEpsilon = 1e-6f;
        private const float ActiveEpsilon = 1e-5f;
        private const float EndpointEpsilon = 1e-5f;

        private struct Slot
        {
            public int BoneIndex;
            public float Weight;
            public bool IsChain;
        }

        private struct SlotBuffer6
        {
            public Slot S0, S1, S2, S3, S4, S5;
            public int Count;

            public Slot Get(int i)
            {
                switch (i)
                {
                    case 0: return S0;
                    case 1: return S1;
                    case 2: return S2;
                    case 3: return S3;
                    case 4: return S4;
                    default: return S5;
                }
            }

            public void Set(int i, Slot s)
            {
                switch (i)
                {
                    case 0: S0 = s; break;
                    case 1: S1 = s; break;
                    case 2: S2 = s; break;
                    case 3: S3 = s; break;
                    case 4: S4 = s; break;
                    default: S5 = s; break;
                }
            }

            public void AddOrMerge(int boneIndex, float weight, bool isChain)
            {
                if (weight <= ZeroEpsilon) return;
                for (int i = 0; i < Count; i++)
                {
                    var e = Get(i);
                    if (e.BoneIndex == boneIndex)
                    {
                        e.Weight += weight;
                        e.IsChain |= isChain;
                        Set(i, e);
                        return;
                    }
                }
                if (Count < 6)
                {
                    Set(Count, new Slot { BoneIndex = boneIndex, Weight = weight, IsChain = isChain });
                    Count++;
                }
            }
        }

        /// <param name="originalWeights">入力ウェイト（肘処理後のもの）。</param>
        /// <param name="worldVertices">レスト姿勢ワールド頂点。</param>
        /// <param name="rigidDistalBoneIndices">G_D^rigid（DistalSource と、その Constraint を介さない剛体子孫）。</param>
        /// <param name="knotBoneIndices">ノット j に割り当てるボーンインデックス（p.Knots と同じ長さ）。</param>
        public static ForearmTwistResult Redistribute(
            BoneWeight[] originalWeights,
            Vector3[] worldVertices,
            HashSet<int> rigidDistalBoneIndices,
            int[] knotBoneIndices,
            in ForearmTwistParams p)
        {
            int vertCount = originalWeights != null ? originalWeights.Length : 0;
            int knotCount = p.Knots.Length;
            var result = new ForearmTwistResult
            {
                NewWeights = new BoneWeight[vertCount],
                ModifiedMask = new bool[vertCount],
                TauValues = new float[vertCount],
                UsedKnots = new bool[knotCount],
                QuantizedMask = new bool[vertCount],
                Stats = new ForearmTwistStats { TotalVertices = vertCount },
            };
            for (int v = 0; v < vertCount; v++)
                result.TauValues[v] = -1f;

            if (vertCount == 0
                || worldVertices == null || worldVertices.Length != vertCount
                || rigidDistalBoneIndices == null || rigidDistalBoneIndices.Count == 0
                || knotBoneIndices == null || knotBoneIndices.Length != knotCount)
            {
                if (originalWeights != null)
                    System.Array.Copy(originalWeights, result.NewWeights, vertCount);
                return result;
            }

            int segments = knotCount - 1;

            for (int v = 0; v < vertCount; v++)
            {
                BoneWeight orig = originalWeights[v];

                // 1. 剛体遠位ウェイト W_R の集計と、それ以外のスロットの保持
                var buf = new SlotBuffer6();
                float wR = 0f;
                int rigidSlotCount = 0;
                int singleRigidIndex = -1;
                float origSum = 0f;
                CollectSlot(ref buf, orig.boneIndex0, orig.weight0, rigidDistalBoneIndices, ref wR, ref rigidSlotCount, ref singleRigidIndex, ref origSum);
                CollectSlot(ref buf, orig.boneIndex1, orig.weight1, rigidDistalBoneIndices, ref wR, ref rigidSlotCount, ref singleRigidIndex, ref origSum);
                CollectSlot(ref buf, orig.boneIndex2, orig.weight2, rigidDistalBoneIndices, ref wR, ref rigidSlotCount, ref singleRigidIndex, ref origSum);
                CollectSlot(ref buf, orig.boneIndex3, orig.weight3, rigidDistalBoneIndices, ref wR, ref rigidSlotCount, ref singleRigidIndex, ref origSum);

                if (wR < ActiveEpsilon)
                {
                    result.NewWeights[v] = orig;
                    continue;
                }

                // 2. τ の算出とノット区間の決定
                int j;
                float u;
                if (segments <= 0)
                {
                    j = 0;
                    u = 0f;
                }
                else
                {
                    float s = RigMath.ComputeTwistAxialParameter(
                        worldVertices[v], p.ElbowCenter, p.RollAxis, p.ForearmLength, p.StartRatio, p.EndRatio);
                    float c = RigMath.EvaluateTwistCurve(p.Curve, p.CurvePower, s);
                    float x = c * segments;
                    j = Mathf.Min(Mathf.FloorToInt(x), segments - 1);
                    if (j < 0) j = 0;
                    u = Mathf.Clamp01(x - j);

                    if (u < EndpointEpsilon) u = 0f;
                    else if (u > 1f - EndpointEpsilon) u = 1f;
                    else if (p.SnapThreshold > 0f && (u <= p.SnapThreshold || u >= 1f - p.SnapThreshold))
                    {
                        u = u < 0.5f ? 0f : 1f;
                        result.Stats.SnapSavedCount++;
                    }
                }

                // 3. チェーンスロットの追加（最大 2 本の隣接ノット）
                int baseCount = buf.Count;
                AddChain(ref buf, knotBoneIndices, j, u, wR, segments);

                // 4. 4 ボーン制限: 超過時は最寄りノットへ丸めて 1 本にする（外部ボーンは不変）
                if (buf.Count > 4 && u > 0f && u < 1f)
                {
                    buf.Count = baseCount;
                    u = u < 0.5f ? 0f : 1f;
                    AddChain(ref buf, knotBoneIndices, j, u, wR, segments);
                    result.Stats.QuantizedCount++;
                    result.QuantizedMask[v] = true;
                }
                if (buf.Count > 4)
                {
                    // 理論上起きない（剛体スロット ≥ 1 本を 1 本に置換）。安全側で元データを維持
                    result.NewWeights[v] = orig;
                    continue;
                }

                int lowKnot = j;
                int highKnot = segments > 0 ? j + 1 : 0;
                int usedKnotForSingle = u >= 1f ? highKnot : lowKnot;

                // 元が単一剛体ボーンで、割当先も同じボーンなら何も変えない（ビット単位で保持）
                if (rigidSlotCount == 1 && (u <= 0f || u >= 1f)
                    && knotBoneIndices[usedKnotForSingle] == singleRigidIndex)
                {
                    result.NewWeights[v] = orig;
                    continue;
                }

                // 5. 総和補正（浮動小数点残差のみチェーンスロットの最大要素へ加算）
                float newSum = 0f;
                int largestChain = -1;
                float largestChainWeight = -1f;
                for (int i = 0; i < buf.Count; i++)
                {
                    var e = buf.Get(i);
                    newSum += e.Weight;
                    if (e.IsChain && e.Weight > largestChainWeight)
                    {
                        largestChainWeight = e.Weight;
                        largestChain = i;
                    }
                }
                float residual = origSum - newSum;
                if (largestChain >= 0 && Mathf.Abs(residual) > 0f)
                {
                    var e = buf.Get(largestChain);
                    e.Weight = Mathf.Max(0f, e.Weight + residual);
                    buf.Set(largestChain, e);
                }

                if (u < 1f) result.UsedKnots[lowKnot] = true;
                if (u > 0f) result.UsedKnots[highKnot] = true;

                float kLow = p.Knots[lowKnot];
                float kHigh = p.Knots[highKnot];
                result.TauValues[v] = kLow + (kHigh - kLow) * u;
                result.NewWeights[v] = PackTop4Sorted(in buf);
                result.ModifiedMask[v] = true;
                result.Stats.AffectedVertices++;
            }

            return result;
        }

        private static void CollectSlot(
            ref SlotBuffer6 buf,
            int boneIndex,
            float weight,
            HashSet<int> rigidSet,
            ref float wR,
            ref int rigidSlotCount,
            ref int singleRigidIndex,
            ref float origSum)
        {
            if (weight <= ZeroEpsilon) return;
            origSum += weight;
            if (rigidSet.Contains(boneIndex))
            {
                wR += weight;
                rigidSlotCount++;
                singleRigidIndex = boneIndex;
                return;
            }
            buf.AddOrMerge(boneIndex, weight, isChain: false);
        }

        private static void AddChain(ref SlotBuffer6 buf, int[] knotBoneIndices, int j, float u, float wR, int segments)
        {
            if (segments <= 0)
            {
                buf.AddOrMerge(knotBoneIndices[0], wR, isChain: true);
                return;
            }

            if (u <= 0f)
            {
                buf.AddOrMerge(knotBoneIndices[j], wR, isChain: true);
            }
            else if (u >= 1f)
            {
                buf.AddOrMerge(knotBoneIndices[j + 1], wR, isChain: true);
            }
            else
            {
                buf.AddOrMerge(knotBoneIndices[j], (1f - u) * wR, isChain: true);
                buf.AddOrMerge(knotBoneIndices[j + 1], u * wR, isChain: true);
            }
        }

        private static BoneWeight PackTop4Sorted(in SlotBuffer6 buf)
        {
            Slot a0 = default, a1 = default, a2 = default, a3 = default;
            int n = 0;
            for (int i = 0; i < buf.Count && n < 4; i++)
            {
                var e = buf.Get(i);
                if (e.Weight <= ZeroEpsilon) continue;
                switch (n)
                {
                    case 0: a0 = e; break;
                    case 1: a1 = e; break;
                    case 2: a2 = e; break;
                    case 3: a3 = e; break;
                }
                n++;
            }

            SortPair(ref a0, ref a1);
            SortPair(ref a2, ref a3);
            SortPair(ref a0, ref a2);
            SortPair(ref a1, ref a3);
            SortPair(ref a1, ref a2);

            return new BoneWeight
            {
                boneIndex0 = a0.Weight > ZeroEpsilon ? a0.BoneIndex : 0,
                weight0 = a0.Weight > ZeroEpsilon ? a0.Weight : 0f,
                boneIndex1 = a1.Weight > ZeroEpsilon ? a1.BoneIndex : 0,
                weight1 = a1.Weight > ZeroEpsilon ? a1.Weight : 0f,
                boneIndex2 = a2.Weight > ZeroEpsilon ? a2.BoneIndex : 0,
                weight2 = a2.Weight > ZeroEpsilon ? a2.Weight : 0f,
                boneIndex3 = a3.Weight > ZeroEpsilon ? a3.BoneIndex : 0,
                weight3 = a3.Weight > ZeroEpsilon ? a3.Weight : 0f,
            };
        }

        private static void SortPair(ref Slot x, ref Slot y)
        {
            bool swap = (y.Weight > x.Weight)
                     || (y.Weight == x.Weight && y.Weight > ZeroEpsilon && y.BoneIndex < x.BoneIndex);
            if (swap)
            {
                var tmp = x;
                x = y;
                y = tmp;
            }
        }

        /// <summary>
        /// プレースホルダーのボーンインデックスを最終インデックスへ付け替える（未使用ノットを詰めるため）。
        /// remap[placeholder - placeholderBase] が最終インデックス（-1 は未使用＝出現しない前提）。
        /// </summary>
        public static void RemapPlaceholderIndices(BoneWeight[] weights, bool[] modifiedMask, int placeholderBase, int[] remap)
        {
            if (weights == null || remap == null) return;
            for (int v = 0; v < weights.Length; v++)
            {
                if (modifiedMask != null && !modifiedMask[v]) continue;
                var bw = weights[v];
                bw.boneIndex0 = Remap(bw.boneIndex0, bw.weight0, placeholderBase, remap);
                bw.boneIndex1 = Remap(bw.boneIndex1, bw.weight1, placeholderBase, remap);
                bw.boneIndex2 = Remap(bw.boneIndex2, bw.weight2, placeholderBase, remap);
                bw.boneIndex3 = Remap(bw.boneIndex3, bw.weight3, placeholderBase, remap);
                weights[v] = bw;
            }
        }

        private static int Remap(int index, float weight, int placeholderBase, int[] remap)
        {
            int local = index - placeholderBase;
            if (local < 0 || local >= remap.Length)
                return index;
            if (weight <= ZeroEpsilon)
                return 0;
            return remap[local] >= 0 ? remap[local] : index;
        }
    }
}
