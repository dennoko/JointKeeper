using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    internal enum VertexSlotTier : byte
    {
        Unmodified = 0,
        Case0FreeOrAutoFreed = 1,
        Tier1IntraGroup = 2,
        Tier2SnapJoint = 3,
        Tier3NoisePrune = 4,
        Tier4Skipped = 5,
        Tier4Fallback = 6,
    }

    internal readonly struct WeightRedistributionParams
    {
        public readonly JointFrame Frame;
        public readonly float BendFollowRatio;
        public readonly float Strength;
        public readonly float BlendCurvePower;
        public readonly float MinGroupWeightThreshold;
        public readonly float AxialRadius;
        public readonly float RadialRadius;
        public readonly float InnerFalloffRatio;
        public readonly float InnerCutoff;
        public readonly float OuterFull;
        public readonly float InnerRetention;
        public readonly float SlotSnapThreshold;
        public readonly float NoisePruneThreshold;

        public WeightRedistributionParams(
            JointFrame frame,
            float bendFollowRatio,
            float strength,
            float blendCurvePower,
            float minGroupWeightThreshold,
            float axialRadius,
            float radialRadius,
            float innerFalloffRatio,
            float innerCutoff,
            float outerFull,
            float innerRetention,
            float slotSnapThreshold,
            float noisePruneThreshold)
        {
            Frame = frame;
            BendFollowRatio = Mathf.Clamp(bendFollowRatio, 0.1f, 0.9f);
            Strength = Mathf.Clamp01(strength);
            BlendCurvePower = Mathf.Max(blendCurvePower, 1.0f);
            MinGroupWeightThreshold = Mathf.Max(minGroupWeightThreshold, 1e-5f);
            AxialRadius = Mathf.Max(axialRadius, 1e-4f);
            RadialRadius = Mathf.Max(radialRadius, 1e-4f);
            InnerFalloffRatio = Mathf.Clamp(innerFalloffRatio, 0f, 0.95f);
            InnerCutoff = innerCutoff;
            OuterFull = outerFull;
            InnerRetention = Mathf.Clamp(innerRetention, 0f, 0.5f);
            SlotSnapThreshold = Mathf.Max(slotSnapThreshold, 0f);
            NoisePruneThreshold = Mathf.Max(noisePruneThreshold, 0f);
        }

        public static WeightRedistributionParams FromConfig(JointFrame frame, JointConfig config)
        {
            return new WeightRedistributionParams(
                frame: frame,
                bendFollowRatio: config.bendFollowRatio,
                strength: config.strength,
                blendCurvePower: config.blendCurvePower,
                minGroupWeightThreshold: config.minGroupWeightThreshold,
                axialRadius: config.axialRadiusScale * frame.LimbLength,
                radialRadius: config.radialRadiusScale * frame.LimbLength,
                innerFalloffRatio: config.innerFalloffRatio,
                innerCutoff: config.innerCutoff,
                outerFull: config.outerFull,
                innerRetention: config.innerRetention,
                slotSnapThreshold: config.slotSnapThreshold,
                noisePruneThreshold: config.noisePruneThreshold);
        }
    }

    internal struct RedistributionStats
    {
        public int TotalVertices;
        public int AffectedVertices;
        public int FreeOrAutoFreedCount;
        public int Tier1IntraGroupCount;
        public int Tier2SnapJointCount;
        public int Tier3NoisePruneCount;
        public int Tier4SkippedCount;
        public int Tier4FallbackCount;
        public int NonUnitInputCount;
        public float MaxHelperWeight;
    }

    internal sealed class WeightRedistributionResult
    {
        public BoneWeight[] NewWeights;
        public bool[] ModifiedMask;
        public float[] HelperWeights;
        public VertexSlotTier[] VertexTiers;
        public RedistributionStats Stats;
    }
}
