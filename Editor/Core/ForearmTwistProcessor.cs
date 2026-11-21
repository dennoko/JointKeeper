using System;
using System.Collections.Generic;
using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// SMR 単位の前腕ツイスト診断結果 (FEATURE_ForearmTwist §1.3, §8-B2, §8-B3)。
    /// </summary>
    internal sealed class ForearmTwistSmrDiagnostics
    {
        /// <summary>Constraint 駆動の遠位ボーン（既存の前腕ツイストリグ）に非ゼロウェイトを持つ頂点数。</summary>
        public int ExistingRigVertexCount;
        public string ExistingRigBoneName;
        /// <summary>Hand 以降（ChildHumanoid 配下）に非ゼロウェイトを持つ頂点数。</summary>
        public int HandWeightVertexCount;
        /// <summary>前腕配下（Hand 以降を除く）の PhysBone に非ゼロウェイトを持つ頂点数。</summary>
        public int ForearmPhysBoneVertexCount;
        public readonly List<string> ForearmPhysBoneNames = new List<string>();
        /// <summary>振り替え対象（剛体遠位ウェイトあり）の頂点数。</summary>
        public int RigidVertexCount;

        public bool HasExistingTwistRig => ExistingRigVertexCount > 0;
    }

    /// <summary>
    /// 前腕ツイスト分散の計画（ウェイト計算済み・ボーン未生成）。
    /// </summary>
    internal sealed class ForearmTwistPlan
    {
        public ForearmTwistResult Result;
        public float[] Knots;
        /// <summary>ノット j のボーンインデックス（プレースホルダーまたは既存の DistalSource インデックス）。</summary>
        public int[] KnotBoneIndices;
        public bool[] KnotIsPlaceholder;
        public int PlaceholderBase;
        public int PlaceholderCount;
        /// <summary>チェーンボーンの Bindpose 算出に使う剛体遠位の基準ボーンインデックス。</summary>
        public int RigidReferenceBoneIndex;

        public bool HasChanges => Result != null && Result.Stats.AffectedVertices > 0;
    }

    /// <summary>
    /// NDMF ビルドとプレビューで共通の前腕ツイスト処理（診断・計画・ボーン確定）(FEATURE_ForearmTwist §5, §8-B6)。
    /// ボーン生成だけを呼び出し側のデリゲートに任せ、計算は完全に同一にする（WYSIWYG）。
    /// </summary>
    internal static class ForearmTwistProcessor
    {
        private const float WeightEpsilon = 1e-4f;

        public static ForearmTwistSmrDiagnostics Analyze(
            BoneWeight[] weights,
            Transform[] smrBones,
            JointBoneGroups groups,
            AvatarKinematicContext ctx,
            Func<Transform, Transform> resolve)
        {
            var diag = new ForearmTwistSmrDiagnostics();
            if (weights == null || smrBones == null || groups == null)
                return diag;

            resolve = resolve ?? (t => t);
            int boneCount = smrBones.Length;
            var isHand = new bool[boneCount];
            var isForearmPhysBone = new bool[boneCount];
            var isExistingRig = new bool[boneCount];
            var isRigid = new bool[boneCount];

            for (int i = 0; i < boneCount; i++)
            {
                Transform raw = smrBones[i];
                if (raw == null) continue;

                isExistingRig[i] = groups.ConstraintDrivenDistalBoneIndices.Contains(i);
                isRigid[i] = groups.DistalRigidBoneIndices.Contains(i);

                bool underHand = groups.ChildHumanoid != null && IsUnder(raw, groups.ChildHumanoid, resolve);
                isHand[i] = underHand;

                if (!underHand && ctx != null && groups.LowerHumanoid != null
                    && (ctx.PhysBoneSet.Contains(raw) || ctx.PhysBoneSet.Contains(resolve(raw)))
                    && IsUnder(raw, groups.LowerHumanoid, resolve))
                {
                    isForearmPhysBone[i] = true;
                }
            }

            var pbNames = new HashSet<string>();
            for (int v = 0; v < weights.Length; v++)
            {
                BoneWeight bw = weights[v];
                bool hand = false, pb = false, rig = false, rigid = false;
                Check(bw.boneIndex0, bw.weight0, isHand, isForearmPhysBone, isExistingRig, isRigid, ref hand, ref pb, ref rig, ref rigid);
                Check(bw.boneIndex1, bw.weight1, isHand, isForearmPhysBone, isExistingRig, isRigid, ref hand, ref pb, ref rig, ref rigid);
                Check(bw.boneIndex2, bw.weight2, isHand, isForearmPhysBone, isExistingRig, isRigid, ref hand, ref pb, ref rig, ref rigid);
                Check(bw.boneIndex3, bw.weight3, isHand, isForearmPhysBone, isExistingRig, isRigid, ref hand, ref pb, ref rig, ref rigid);

                if (hand) diag.HandWeightVertexCount++;
                if (pb)
                {
                    diag.ForearmPhysBoneVertexCount++;
                    CollectNames(bw, isForearmPhysBone, smrBones, pbNames);
                }
                if (rig)
                {
                    diag.ExistingRigVertexCount++;
                    if (diag.ExistingRigBoneName == null)
                        diag.ExistingRigBoneName = FindFirstName(bw, isExistingRig, smrBones);
                }
                if (rigid) diag.RigidVertexCount++;
            }

            diag.ForearmPhysBoneNames.AddRange(pbNames);
            diag.ForearmPhysBoneNames.Sort(StringComparer.Ordinal);
            return diag;
        }

        /// <summary>
        /// ウェイト計算だけを行い、ボーン生成は行わない計画を作る。
        /// </summary>
        /// <param name="indexOfResolvedBone">解決済み Transform → smr.bones インデックス（無ければ -1）。</param>
        public static ForearmTwistPlan Plan(
            BoneWeight[] inputWeights,
            Vector3[] worldVertices,
            JointBoneGroups groups,
            ForearmTwistConfig config,
            in ForearmTwistGeometry geometry,
            int placeholderBase,
            Func<Transform, int> indexOfResolvedBone)
        {
            var plan = new ForearmTwistPlan
            {
                Knots = RigMath.ComputeTwistKnots(config.twistFollow, config.segments),
                PlaceholderBase = placeholderBase,
                RigidReferenceBoneIndex = SelectRigidReference(groups, inputWeights),
            };

            int knotCount = plan.Knots.Length;
            plan.KnotBoneIndices = new int[knotCount];
            plan.KnotIsPlaceholder = new bool[knotCount];

            int next = 0;
            for (int j = 0; j < knotCount; j++)
            {
                bool isTopFull = j == knotCount - 1 && j > 0 && plan.Knots[j] >= 1f - 1e-5f;
                int existing = isTopFull && indexOfResolvedBone != null ? indexOfResolvedBone(groups.DistalSource) : -1;
                if (existing >= 0 && groups.DistalRigidBoneIndices.Contains(existing))
                {
                    plan.KnotBoneIndices[j] = existing;
                }
                else
                {
                    plan.KnotBoneIndices[j] = placeholderBase + next;
                    plan.KnotIsPlaceholder[j] = true;
                    next++;
                }
            }
            plan.PlaceholderCount = next;

            if (plan.RigidReferenceBoneIndex < 0)
            {
                plan.Result = new ForearmTwistResult
                {
                    NewWeights = (BoneWeight[])inputWeights.Clone(),
                    ModifiedMask = new bool[inputWeights.Length],
                    TauValues = new float[inputWeights.Length],
                    UsedKnots = new bool[knotCount],
                    QuantizedMask = new bool[inputWeights.Length],
                };
                return plan;
            }

            var p = ForearmTwistParams.FromConfig(config, geometry.ElbowCenter, geometry.RollAxis, geometry.ForearmLength);
            plan.Result = ForearmTwistRedistributor.Redistribute(
                inputWeights, worldVertices, groups.DistalRigidBoneIndices, plan.KnotBoneIndices, in p);
            return plan;
        }

        /// <summary>
        /// 使用されたプレースホルダーノットだけボーンを確定し、ウェイトのインデックスを付け替える。
        /// </summary>
        /// <param name="getKnotBone">ノット j のボーン（j = 0 は N、それ以外は T_j または DistalSource）を取得・生成する。</param>
        /// <param name="rigidReferenceSkinMatrix">S_D^ref（剛体遠位基準ボーンのスキニング行列）。</param>
        /// <returns>付け替え後のウェイト。</returns>
        public static BoneWeight[] Commit(
            ForearmTwistPlan plan,
            Func<int, Transform> getKnotBone,
            List<Transform> workingBones,
            List<Matrix4x4> workingBindposes,
            Matrix4x4 rigidReferenceSkinMatrix)
        {
            BoneWeight[] weights = plan.Result.NewWeights;
            if (!plan.HasChanges || plan.PlaceholderCount == 0)
                return weights;

            var remap = new int[plan.PlaceholderCount];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;

            for (int j = 0; j < plan.Knots.Length; j++)
            {
                if (!plan.KnotIsPlaceholder[j] || !plan.Result.UsedKnots[j])
                    continue;

                Transform bone = getKnotBone(j);
                if (bone == null)
                    continue;

                int local = plan.KnotBoneIndices[j] - plan.PlaceholderBase;
                remap[local] = workingBones.Count;
                workingBones.Add(bone);
                workingBindposes.Add(bone.localToWorldMatrix.inverse * rigidReferenceSkinMatrix);
            }

            ForearmTwistRedistributor.RemapPlaceholderIndices(weights, plan.Result.ModifiedMask, plan.PlaceholderBase, remap);
            return weights;
        }

        private static int SelectRigidReference(JointBoneGroups groups, BoneWeight[] weights)
        {
            if (groups.DistalRigidBoneIndices.Count == 0)
                return -1;
            if (groups.DistalRigidBoneIndices.Contains(groups.DistalReferenceBoneIndex))
                return groups.DistalReferenceBoneIndex;

            var totals = new Dictionary<int, float>();
            foreach (int idx in groups.DistalRigidBoneIndices)
                totals[idx] = 0f;

            if (weights != null)
            {
                for (int v = 0; v < weights.Length; v++)
                {
                    var bw = weights[v];
                    Accumulate(totals, bw.boneIndex0, bw.weight0);
                    Accumulate(totals, bw.boneIndex1, bw.weight1);
                    Accumulate(totals, bw.boneIndex2, bw.weight2);
                    Accumulate(totals, bw.boneIndex3, bw.weight3);
                }
            }

            int best = -1;
            float bestW = -1f;
            foreach (var kv in totals)
            {
                if (kv.Value > bestW || (Mathf.Approximately(kv.Value, bestW) && kv.Key < best))
                {
                    bestW = kv.Value;
                    best = kv.Key;
                }
            }
            return best;
        }

        private static void Accumulate(Dictionary<int, float> totals, int idx, float w)
        {
            if (w > 0f && totals.TryGetValue(idx, out float cur))
                totals[idx] = cur + w;
        }

        private static bool IsUnder(Transform t, Transform ancestor, Func<Transform, Transform> resolve)
        {
            Transform cur = t;
            while (cur != null)
            {
                if (cur == ancestor || resolve(cur) == ancestor) return true;
                cur = cur.parent;
            }
            return false;
        }

        private static void Check(
            int idx, float w,
            bool[] isHand, bool[] isPb, bool[] isRig, bool[] isRigid,
            ref bool hand, ref bool pb, ref bool rig, ref bool rigid)
        {
            if (w <= WeightEpsilon || idx < 0 || idx >= isHand.Length) return;
            hand |= isHand[idx];
            pb |= isPb[idx];
            rig |= isRig[idx];
            rigid |= isRigid[idx];
        }

        private static void CollectNames(BoneWeight bw, bool[] flags, Transform[] bones, HashSet<string> names)
        {
            AddName(bw.boneIndex0, bw.weight0, flags, bones, names);
            AddName(bw.boneIndex1, bw.weight1, flags, bones, names);
            AddName(bw.boneIndex2, bw.weight2, flags, bones, names);
            AddName(bw.boneIndex3, bw.weight3, flags, bones, names);
        }

        private static void AddName(int idx, float w, bool[] flags, Transform[] bones, HashSet<string> names)
        {
            if (w <= WeightEpsilon || idx < 0 || idx >= flags.Length || !flags[idx] || bones[idx] == null) return;
            if (names.Count < 8) names.Add(bones[idx].name);
        }

        private static string FindFirstName(BoneWeight bw, bool[] flags, Transform[] bones)
        {
            int[] idx = { bw.boneIndex0, bw.boneIndex1, bw.boneIndex2, bw.boneIndex3 };
            float[] w = { bw.weight0, bw.weight1, bw.weight2, bw.weight3 };
            for (int i = 0; i < 4; i++)
            {
                if (w[i] > WeightEpsilon && idx[i] >= 0 && idx[i] < flags.Length && flags[idx[i]] && bones[idx[i]] != null)
                    return bones[idx[i]].name;
            }
            return null;
        }
    }
}
