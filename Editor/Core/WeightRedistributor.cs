using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// 角モーメント保存型ウェイト再分配および VRChat 最大 4 インフルエンス制限の
    /// 4 段階スロット統合アルゴリズム (§5.5)。Unity シーン非依存の純粋計算クラス。
    /// </summary>
    internal static class WeightRedistributor
    {
        private const float ZeroEpsilon = 1e-6f;
        private const float ActiveEpsilon = 1e-5f;
        private const float NonUnitResidualThreshold = 1e-4f;

        private enum SlotGroup : byte
        {
            Other = 0,
            Proximal = 1,
            Distal = 2,
            Helper = 3,
        }

        private struct SlotEntry
        {
            public int BoneIndex;
            public float Weight;
            public SlotGroup Group;
            public bool IsPrimaryHumanoid;
        }

        /// <summary>
        /// ヒープ確保ゼロの固定 5 スロット作業バッファ。
        /// </summary>
        private struct SlotBuffer5
        {
            public SlotEntry S0, S1, S2, S3, S4;
            public int Count;

            public SlotEntry Get(int i)
            {
                switch (i)
                {
                    case 0: return S0;
                    case 1: return S1;
                    case 2: return S2;
                    case 3: return S3;
                    default: return S4;
                }
            }

            public void Set(int i, SlotEntry entry)
            {
                switch (i)
                {
                    case 0: S0 = entry; break;
                    case 1: S1 = entry; break;
                    case 2: S2 = entry; break;
                    case 3: S3 = entry; break;
                    default: S4 = entry; break;
                }
            }

            public void AddOrMerge(int boneIndex, float weight, SlotGroup group, bool isPrimary)
            {
                if (weight < ZeroEpsilon) return;
                for (int i = 0; i < Count; i++)
                {
                    var e = Get(i);
                    if (e.BoneIndex == boneIndex)
                    {
                        e.Weight += weight;
                        Set(i, e);
                        return;
                    }
                }
                if (Count < 5)
                {
                    Set(Count, new SlotEntry
                    {
                        BoneIndex = boneIndex,
                        Weight = weight,
                        Group = group,
                        IsPrimaryHumanoid = isPrimary,
                    });
                    Count++;
                }
            }

            public int CountActive()
            {
                int n = 0;
                for (int i = 0; i < Count; i++)
                {
                    if (Get(i).Weight > ZeroEpsilon) n++;
                }
                return n;
            }
        }

        public static WeightRedistributionResult Redistribute(
            BoneWeight[] originalWeights,
            Vector3[] worldVertices,
            HashSet<int> proximalBoneIndices,
            HashSet<int> distalBoneIndices,
            HashSet<int> primaryHumanoidBoneIndices,
            int helperBoneIndex,
            in WeightRedistributionParams p)
        {
            int vertCount = originalWeights != null ? originalWeights.Length : 0;
            var result = new WeightRedistributionResult
            {
                NewWeights = new BoneWeight[vertCount],
                ModifiedMask = new bool[vertCount],
                HelperWeights = new float[vertCount],
                VertexTiers = new VertexSlotTier[vertCount],
                Stats = new RedistributionStats { TotalVertices = vertCount },
            };

            if (vertCount == 0 || worldVertices == null || worldVertices.Length != vertCount
                || proximalBoneIndices == null || distalBoneIndices == null
                || proximalBoneIndices.Count == 0 || distalBoneIndices.Count == 0
                || p.Strength <= ZeroEpsilon)
            {
                if (originalWeights != null)
                    System.Array.Copy(originalWeights, result.NewWeights, vertCount);
                return result;
            }

            JointFrame frame = p.Frame;
            float alpha = p.BendFollowRatio;
            float oneMinusAlpha = 1.0f - alpha;
            float invAxialR = 1.0f / p.AxialRadius;
            float invRadialR = 1.0f / p.RadialRadius;

            for (int v = 0; v < vertCount; v++)
            {
                BoneWeight orig = originalWeights[v];

                // Step 1: 既存スロットのサニタイズとグループ集計（元データは改変しない）
                var buf = new SlotBuffer5();
                AddOriginalSlot(ref buf, orig.boneIndex0, orig.weight0, proximalBoneIndices, distalBoneIndices, primaryHumanoidBoneIndices);
                AddOriginalSlot(ref buf, orig.boneIndex1, orig.weight1, proximalBoneIndices, distalBoneIndices, primaryHumanoidBoneIndices);
                AddOriginalSlot(ref buf, orig.boneIndex2, orig.weight2, proximalBoneIndices, distalBoneIndices, primaryHumanoidBoneIndices);
                AddOriginalSlot(ref buf, orig.boneIndex3, orig.weight3, proximalBoneIndices, distalBoneIndices, primaryHumanoidBoneIndices);

                float wP = 0f;
                float wD = 0f;
                float origMinWeight = float.MaxValue;
                for (int i = 0; i < buf.Count; i++)
                {
                    var e = buf.Get(i);
                    if (e.Group == SlotGroup.Proximal) wP += e.Weight;
                    else if (e.Group == SlotGroup.Distal) wD += e.Weight;
                    if (e.Weight < origMinWeight) origMinWeight = e.Weight;
                }

                if (wP < p.MinGroupWeightThreshold || wD < p.MinGroupWeightThreshold)
                {
                    result.NewWeights[v] = orig;
                    continue;
                }

                // Step 2: 空間・非対称方向マスク M(v)
                Vector3 rel = worldVertices[v] - frame.JointCenter;
                float zAxial = Vector3.Dot(rel, frame.LimbAxis);
                Vector3 rPerpVec = rel - frame.LimbAxis * zAxial;
                float rPerp = rPerpVec.magnitude;

                float az = zAxial * invAxialR;
                float ar = rPerp * invRadialR;
                float dNormSqr = az * az + ar * ar;
                if (dNormSqr >= 1.0f)
                {
                    result.NewWeights[v] = orig;
                    continue;
                }

                float dNorm = Mathf.Sqrt(dNormSqr);
                float mDist = 1.0f - SmoothStep(p.InnerFalloffRatio, 1.0f, dNorm);

                float c = rPerp > ActiveEpsilon ? Vector3.Dot(rPerpVec / rPerp, frame.Outward) : 0.0f;
                float mDir = Mathf.Lerp(p.InnerRetention, 1.0f, SmoothStep(p.InnerCutoff, p.OuterFull, c));

                float mask = mDist * mDir;
                if (mask * p.Strength < ActiveEpsilon)
                {
                    result.NewWeights[v] = orig;
                    continue;
                }

                // Step 3: 角モーメント保存型ウェイト算出
                float wJoint = wP + wD;
                float wHMax = Mathf.Min(wP / oneMinusAlpha, wD / alpha);
                float u = Mathf.Clamp01(wHMax / wJoint);
                float uPowered = Mathf.Abs(p.BlendCurvePower - 1.0f) < 1e-5f
                    ? u
                    : Mathf.Pow(u, p.BlendCurvePower);

                float wH = p.Strength * mask * wJoint * uPowered;
                if (wH < ActiveEpsilon)
                {
                    result.NewWeights[v] = orig;
                    continue;
                }

                float deltaWP = oneMinusAlpha * wH;
                float deltaWD = alpha * wH;

                float scaleP = Mathf.Max(0f, 1.0f - deltaWP / wP);
                float scaleD = Mathf.Max(0f, 1.0f - deltaWD / wD);
                if (scaleP < ActiveEpsilon) scaleP = 0f;
                if (scaleD < ActiveEpsilon) scaleD = 0f;

                for (int i = 0; i < buf.Count; i++)
                {
                    var e = buf.Get(i);
                    if (e.Group == SlotGroup.Proximal)
                    {
                        e.Weight *= scaleP;
                        buf.Set(i, e);
                    }
                    else if (e.Group == SlotGroup.Distal)
                    {
                        e.Weight *= scaleD;
                        buf.Set(i, e);
                    }
                    // SlotGroup.Other (PhysBone 等) は無変更
                }

                int helperSlotIdx = buf.Count;
                buf.Set(helperSlotIdx, new SlotEntry
                {
                    BoneIndex = helperBoneIndex,
                    Weight = wH,
                    Group = SlotGroup.Helper,
                    IsPrimaryHumanoid = false,
                });
                buf.Count++;

                // Step 4: VRChat 4 ボーン制限・4 段階スロット統合
                int activeCount = buf.CountActive();
                VertexSlotTier tier;

                if (activeCount <= 4)
                {
                    tier = VertexSlotTier.Case0FreeOrAutoFreed;
                    result.Stats.FreeOrAutoFreedCount++;
                }
                else
                {
                    // activeCount == 5
                    if (TryTier1IntraGroupMerge(ref buf))
                    {
                        tier = VertexSlotTier.Tier1IntraGroup;
                        result.Stats.Tier1IntraGroupCount++;
                    }
                    else if (TryTier2SnapSmallestJointToHelper(ref buf, helperSlotIdx, p.SlotSnapThreshold, wH))
                    {
                        tier = VertexSlotTier.Tier2SnapJoint;
                        result.Stats.Tier2SnapJointCount++;
                    }
                    else if (TryTier3PruneNoiseOther(ref buf, p.NoisePruneThreshold))
                    {
                        tier = VertexSlotTier.Tier3NoisePrune;
                        result.Stats.Tier3NoisePruneCount++;
                    }
                    else if (wH < origMinWeight)
                    {
                        // Tier 4a: 重要ボーン保護優先のため元ウェイトを完全維持
                        result.NewWeights[v] = orig;
                        result.VertexTiers[v] = VertexSlotTier.Tier4Skipped;
                        result.Stats.Tier4SkippedCount++;
                        continue;
                    }
                    else
                    {
                        // Tier 4b: 残留関節ボーンの小さい方を Helper へ吸収（外部ボーン保護）
                        ForceSnapSmallestJointToHelper(ref buf, helperSlotIdx);
                        tier = VertexSlotTier.Tier4Fallback;
                        result.Stats.Tier4FallbackCount++;
                    }
                }

                // Step 5: 総和補正（補助ボーンのみへ残差加算し外部ボーンをビット単位で非改変）＆降順ソート
                float sum = 0f;
                for (int i = 0; i < buf.Count; i++)
                {
                    var e = buf.Get(i);
                    if (e.Weight > ZeroEpsilon) sum += e.Weight;
                }

                float residual = 1.0f - sum;
                if (Mathf.Abs(residual) > NonUnitResidualThreshold && sum > ZeroEpsilon)
                {
                    // 元データ自体の総和が 1 でなかった例外ケース
                    result.Stats.NonUnitInputCount++;
                    float invSum = 1.0f / sum;
                    for (int i = 0; i < buf.Count; i++)
                    {
                        var e = buf.Get(i);
                        if (e.Weight > ZeroEpsilon)
                        {
                            e.Weight *= invSum;
                            buf.Set(i, e);
                        }
                    }
                }
                else
                {
                    var he = buf.Get(helperSlotIdx);
                    he.Weight = Mathf.Max(0f, he.Weight + residual);
                    buf.Set(helperSlotIdx, he);
                }

                float finalHelperWeight = buf.Get(helperSlotIdx).Weight;
                result.NewWeights[v] = PackTop4Sorted(in buf);
                result.ModifiedMask[v] = true;
                result.HelperWeights[v] = finalHelperWeight;
                result.VertexTiers[v] = tier;
                result.Stats.AffectedVertices++;
                if (finalHelperWeight > result.Stats.MaxHelperWeight)
                    result.Stats.MaxHelperWeight = finalHelperWeight;
            }

            return result;
        }

        private static void AddOriginalSlot(
            ref SlotBuffer5 buf,
            int boneIndex,
            float weight,
            HashSet<int> proximal,
            HashSet<int> distal,
            HashSet<int> primaryHumanoid)
        {
            if (weight < ZeroEpsilon) return;
            SlotGroup group = proximal.Contains(boneIndex)
                ? SlotGroup.Proximal
                : (distal.Contains(boneIndex) ? SlotGroup.Distal : SlotGroup.Other);
            bool isPrimary = primaryHumanoid != null && primaryHumanoid.Contains(boneIndex);
            buf.AddOrMerge(boneIndex, weight, group, isPrimary);
        }

        /// <summary>
        /// Tier 1: G_P または G_D 内に 2 本以上の非ゼロボーンがある場合、
        /// 該当グループ内の最小ボーンを同グループ内の最大ボーンへ統合する。
        /// </summary>
        private static bool TryTier1IntraGroupMerge(ref SlotBuffer5 buf)
        {
            int pCount = 0, dCount = 0;
            for (int i = 0; i < buf.Count; i++)
            {
                var e = buf.Get(i);
                if (e.Weight <= ZeroEpsilon) continue;
                if (e.Group == SlotGroup.Proximal) pCount++;
                else if (e.Group == SlotGroup.Distal) dCount++;
            }

            if (pCount < 2 && dCount < 2)
                return false;

            int victimIdx = -1;
            float victimWeight = float.MaxValue;
            bool victimIsPrimary = true;

            for (int i = 0; i < buf.Count; i++)
            {
                var e = buf.Get(i);
                if (e.Weight <= ZeroEpsilon) continue;
                bool eligible = (e.Group == SlotGroup.Proximal && pCount >= 2)
                             || (e.Group == SlotGroup.Distal && dCount >= 2);
                if (!eligible) continue;

                // 最小ウェイト優先。同値なら非 Humanoid（ツイスト/補助）を優先的に統合対象にする
                if (e.Weight < victimWeight - 1e-6f
                    || (Mathf.Abs(e.Weight - victimWeight) <= 1e-6f && victimIsPrimary && !e.IsPrimaryHumanoid))
                {
                    victimIdx = i;
                    victimWeight = e.Weight;
                    victimIsPrimary = e.IsPrimaryHumanoid;
                }
            }

            if (victimIdx < 0) return false;

            SlotEntry victim = buf.Get(victimIdx);
            int hostIdx = -1;
            float hostWeight = -1f;
            for (int i = 0; i < buf.Count; i++)
            {
                if (i == victimIdx) continue;
                var e = buf.Get(i);
                if (e.Weight <= ZeroEpsilon || e.Group != victim.Group) continue;
                if (e.Weight > hostWeight)
                {
                    hostWeight = e.Weight;
                    hostIdx = i;
                }
            }

            if (hostIdx < 0) return false;

            var host = buf.Get(hostIdx);
            host.Weight += victim.Weight;
            buf.Set(hostIdx, host);

            victim.Weight = 0f;
            buf.Set(victimIdx, victim);
            return true;
        }

        /// <summary>
        /// Tier 2: 残留関節ボーン（G_P / G_D）のうち最小ウェイトが τ_snap 以下かつ w_H 未満なら Helper へ吸収。
        /// </summary>
        private static bool TryTier2SnapSmallestJointToHelper(
            ref SlotBuffer5 buf,
            int helperSlotIdx,
            float snapThreshold,
            float wH)
        {
            int minJointIdx = FindSmallestJointSlot(in buf, out float minJointWeight);
            if (minJointIdx < 0 || minJointWeight > snapThreshold || minJointWeight >= wH)
                return false;

            var jointEntry = buf.Get(minJointIdx);
            var helperEntry = buf.Get(helperSlotIdx);
            helperEntry.Weight += jointEntry.Weight;
            jointEntry.Weight = 0f;
            buf.Set(helperSlotIdx, helperEntry);
            buf.Set(minJointIdx, jointEntry);
            return true;
        }

        /// <summary>
        /// Tier 3: 外部ボーン G_O に τ_noise 以下の微小ノイズがあれば削除し、残余外部ボーン（無ければ全体）へ比例再分配。
        /// </summary>
        private static bool TryTier3PruneNoiseOther(ref SlotBuffer5 buf, float noiseThreshold)
        {
            int minOtherIdx = -1;
            float minOtherWeight = float.MaxValue;
            float otherSum = 0f;
            int otherCount = 0;

            for (int i = 0; i < buf.Count; i++)
            {
                var e = buf.Get(i);
                if (e.Weight <= ZeroEpsilon || e.Group != SlotGroup.Other) continue;
                otherCount++;
                otherSum += e.Weight;
                if (e.Weight < minOtherWeight)
                {
                    minOtherWeight = e.Weight;
                    minOtherIdx = i;
                }
            }

            if (minOtherIdx < 0 || minOtherWeight > noiseThreshold)
                return false;

            var victim = buf.Get(minOtherIdx);
            float dropped = victim.Weight;
            victim.Weight = 0f;
            buf.Set(minOtherIdx, victim);

            float remainingOtherSum = otherSum - dropped;
            if (otherCount >= 2 && remainingOtherSum > ZeroEpsilon)
            {
                float scale = (remainingOtherSum + dropped) / remainingOtherSum;
                for (int i = 0; i < buf.Count; i++)
                {
                    var e = buf.Get(i);
                    if (e.Weight > ZeroEpsilon && e.Group == SlotGroup.Other)
                    {
                        e.Weight *= scale;
                        buf.Set(i, e);
                    }
                }
            }
            else
            {
                float totalRem = 0f;
                for (int i = 0; i < buf.Count; i++)
                {
                    var e = buf.Get(i);
                    if (e.Weight > ZeroEpsilon) totalRem += e.Weight;
                }
                if (totalRem > ZeroEpsilon)
                {
                    float scale = (totalRem + dropped) / totalRem;
                    for (int i = 0; i < buf.Count; i++)
                    {
                        var e = buf.Get(i);
                        if (e.Weight > ZeroEpsilon)
                        {
                            e.Weight *= scale;
                            buf.Set(i, e);
                        }
                    }
                }
            }

            return true;
        }

        private static void ForceSnapSmallestJointToHelper(ref SlotBuffer5 buf, int helperSlotIdx)
        {
            int minJointIdx = FindSmallestJointSlot(in buf, out _);
            if (minJointIdx < 0) return;

            var jointEntry = buf.Get(minJointIdx);
            var helperEntry = buf.Get(helperSlotIdx);
            helperEntry.Weight += jointEntry.Weight;
            jointEntry.Weight = 0f;
            buf.Set(helperSlotIdx, helperEntry);
            buf.Set(minJointIdx, jointEntry);
        }

        private static int FindSmallestJointSlot(in SlotBuffer5 buf, out float minWeight)
        {
            int idx = -1;
            minWeight = float.MaxValue;
            for (int i = 0; i < buf.Count; i++)
            {
                var e = buf.Get(i);
                if (e.Weight <= ZeroEpsilon) continue;
                if (e.Group != SlotGroup.Proximal && e.Group != SlotGroup.Distal) continue;
                if (e.Weight < minWeight)
                {
                    minWeight = e.Weight;
                    idx = i;
                }
            }
            return idx;
        }

        private static BoneWeight PackTop4Sorted(in SlotBuffer5 buf)
        {
            // 最大 4 つの非ゼロ要素を抽出し、weight 降順（同値時は boneIndex 昇順）にソート
            SlotEntry a0 = default, a1 = default, a2 = default, a3 = default;
            int n = 0;
            for (int i = 0; i < buf.Count; i++)
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
                if (n == 4) break;
            }

            // 4 要素の固定ソーティングネットワーク（GC ゼロ）
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

        private static void SortPair(ref SlotEntry x, ref SlotEntry y)
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

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            float diff = edge1 - edge0;
            if (Mathf.Abs(diff) < 1e-6f)
                return x >= edge0 ? 1f : 0f;
            float t = Mathf.Clamp01((x - edge0) / diff);
            return t * t * (3f - 2f * t);
        }
    }
}
