using UnityEngine;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// Phase 2 ヒートマップ（補助ウェイト分布 / 4 ボーン統合診断）の頂点カラーおよび非保存マテリアル生成 (§7.3)。
    /// </summary>
    internal static class JointKeeperHeatmapDrawer
    {
        private static readonly Color ColUnmodified = new Color(0.22f, 0.24f, 0.28f, 1f);
        private static readonly Color ColCase0 = new Color(0.20f, 0.85f, 0.35f, 1f);
        private static readonly Color ColTier1 = new Color(0.15f, 0.85f, 0.95f, 1f);
        private static readonly Color ColTier23 = new Color(0.98f, 0.82f, 0.15f, 1f);
        private static readonly Color ColTier4 = new Color(0.95f, 0.20f, 0.75f, 1f);

        public static Material CreateHeatmapMaterial()
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");

            var mat = new Material(shader)
            {
                name = "JointKeeper_Heatmap_Preview",
                hideFlags = HideFlags.HideAndDontSave,
            };
            mat.SetInt("_ZWrite", 1);
            mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Back);
            return mat;
        }

        public static Color[] BuildWeightColors(float[] helperWeights)
        {
            int n = helperWeights != null ? helperWeights.Length : 0;
            var colors = new Color[n];
            for (int i = 0; i < n; i++)
            {
                colors[i] = EvaluateWeightGradient(helperWeights[i]);
            }
            return colors;
        }

        /// <summary>
        /// 前腕ツイスト追従率 τ（-1 = 未処理）の頂点カラー。0 = 青（曲げのみ）→ 1 = 赤（元のロール）。
        /// 4 ボーン制限で丸めた頂点はマゼンタで示す。
        /// </summary>
        public static Color[] BuildTwistColors(float[] tau, bool[] quantized)
        {
            int n = tau != null ? tau.Length : 0;
            var colors = new Color[n];
            for (int i = 0; i < n; i++)
            {
                if (tau[i] < 0f)
                    colors[i] = ColUnmodified;
                else if (quantized != null && i < quantized.Length && quantized[i])
                    colors[i] = ColTier4;
                else
                    colors[i] = Color.Lerp(new Color(0.10f, 0.35f, 0.95f, 1f), new Color(0.95f, 0.20f, 0.15f, 1f), Mathf.Clamp01(tau[i]));
            }
            return colors;
        }

        public static Color[] BuildDiagnosticColors(VertexSlotTier[] tiers)
        {
            int n = tiers != null ? tiers.Length : 0;
            var colors = new Color[n];
            for (int i = 0; i < n; i++)
            {
                switch (tiers[i])
                {
                    case VertexSlotTier.Case0FreeOrAutoFreed: colors[i] = ColCase0; break;
                    case VertexSlotTier.Tier1IntraGroup: colors[i] = ColTier1; break;
                    case VertexSlotTier.Tier2SnapJoint:
                    case VertexSlotTier.Tier3NoisePrune: colors[i] = ColTier23; break;
                    case VertexSlotTier.Tier4Skipped:
                    case VertexSlotTier.Tier4Fallback: colors[i] = ColTier4; break;
                    default: colors[i] = ColUnmodified; break;
                }
            }
            return colors;
        }

        private static Color EvaluateWeightGradient(float w)
        {
            w = Mathf.Clamp01(w);
            if (w <= 1e-4f)
                return ColUnmodified;
            if (w < 0.5f)
                return Color.Lerp(new Color(0.10f, 0.35f, 0.95f, 1f), new Color(0.15f, 0.90f, 0.35f, 1f), w * 2f);
            return Color.Lerp(new Color(0.15f, 0.90f, 0.35f, 1f), new Color(0.95f, 0.20f, 0.15f, 1f), (w - 0.5f) * 2f);
        }
    }
}
