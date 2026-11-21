using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// JointKeeper が生成するボーン名の規約と、二重適用検出 (FEATURE_ForearmTwist §1.5)。
    /// 「関節 × 機能」単位で判定するため、肘補助ボーンと前腕ツイストボーンは別々に検出する。
    /// </summary>
    internal static class JointKeeperGeneratedNames
    {
        public static string HelperBaseName(JointId jointId) => $"JK_Helper_{jointId}";
        public static string TwistBaseName(JointId jointId) => $"JK_TwistBase_{jointId}";
        public static string TwistAimName(JointId jointId) => $"JK_TwistAim_{jointId}";
        public static string TwistKnotName(JointId jointId, int knotIndex) => $"JK_Twist_{jointId}_{knotIndex}";
        private static string TwistKnotPrefix(JointId jointId) => $"JK_Twist_{jointId}_";

        /// <summary>SMR がすでに当該関節の肘・膝補助ボーンを参照しているか（Bake 済み等）。</summary>
        public static bool ReferencesHelperBone(Transform[] bones, JointId jointId)
        {
            return ContainsPrefix(bones, HelperBaseName(jointId));
        }

        /// <summary>SMR がすでに当該関節の前腕ツイストボーンを参照しているか（Bake 済み等）。</summary>
        public static bool ReferencesTwistBone(Transform[] bones, JointId jointId)
        {
            return ContainsPrefix(bones, TwistBaseName(jointId)) || ContainsPrefix(bones, TwistKnotPrefix(jointId));
        }

        private static bool ContainsPrefix(Transform[] bones, string prefix)
        {
            if (bones == null) return false;
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones[i];
                if (b != null && b.name.StartsWith(prefix, System.StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
