using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.JointKeeper
{
    /// <summary>
    /// NDMF ビルド時に肘・膝の補助ボーン生成とウェイト再計算を非破壊で適用するコンポーネント。
    /// 衣装メッシュ・衣装プレハブ・アバタールートのいずれにもアタッチ可能。
    /// </summary>
    [AddComponentMenu("dennokoworks/Joint Keeper")]
    [DisallowMultipleComponent]
    public sealed class JointKeeperComponent : MonoBehaviour
#if JOINT_KEEPER_VRCSDK
        , VRC.SDKBase.IEditorOnly
#endif
    {
        public TargetRendererMode targetMode = TargetRendererMode.AutoDetect;
        public List<SkinnedMeshRenderer> targetRenderers = new List<SkinnedMeshRenderer>();
        public List<SkinnedMeshRenderer> excludedRenderers = new List<SkinnedMeshRenderer>();

        public bool unlinkElbowLeftRight = false;
        public JointConfig leftElbow = JointConfig.CreateElbowDefault();
        public JointConfig rightElbow = JointConfig.CreateElbowDefault();

        /// <summary>前腕ツイスト分散（肘専用）。左右リンクは unlinkElbowLeftRight に従う。</summary>
        public ForearmTwistConfig leftForearmTwist = new ForearmTwistConfig();
        public ForearmTwistConfig rightForearmTwist = new ForearmTwistConfig();

        public bool unlinkKneeLeftRight = false;
        public JointConfig leftKnee = JointConfig.CreateKneeDefault();
        public JointConfig rightKnee = JointConfig.CreateKneeDefault();

        [HideInInspector]
        public JointBakeRecord bakeRecord = new JointBakeRecord();

        /// <summary>
        /// 破壊的ベイクで既にメッシュ差し替え済みかどうか。
        /// true の間は NDMF ビルド・プレビューの二重適用を防止する。
        /// </summary>
        public bool IsBaked => bakeRecord != null && bakeRecord.isBaked;

        /// <summary>
        /// 左右リンク設定を考慮した解決済み JointConfig を返す。
        /// 右側がリンク中の場合は left の数値をミラー適用し、右側のボーン参照リストを保持する。
        /// </summary>
        public JointConfig ResolveConfig(JointId jointId)
        {
            switch (jointId)
            {
                case JointId.LeftElbow:
                    return (leftElbow ?? JointConfig.CreateElbowDefault()).Clone();

                case JointId.RightElbow:
                {
                    var right = (rightElbow ?? JointConfig.CreateElbowDefault()).Clone();
                    if (!unlinkElbowLeftRight && leftElbow != null)
                        right.CopyMirroredNumericsFrom(leftElbow);
                    return right;
                }

                case JointId.LeftKnee:
                    return (leftKnee ?? JointConfig.CreateKneeDefault()).Clone();

                case JointId.RightKnee:
                {
                    var right = (rightKnee ?? JointConfig.CreateKneeDefault()).Clone();
                    if (!unlinkKneeLeftRight && leftKnee != null)
                        right.CopyMirroredNumericsFrom(leftKnee);
                    return right;
                }

                default:
                    return new JointConfig();
            }
        }

        /// <summary>
        /// 左右リンク設定を考慮した解決済み ForearmTwistConfig を返す（肘以外は常に無効）。
        /// 全項目がスカラーで左右対称なため、リンク時は符号反転なしで left の値をそのまま使う。
        /// </summary>
        public ForearmTwistConfig ResolveTwistConfig(JointId jointId)
        {
            switch (jointId)
            {
                case JointId.LeftElbow:
                    return (leftForearmTwist ?? new ForearmTwistConfig()).Clone();

                case JointId.RightElbow:
                {
                    if (!unlinkElbowLeftRight && leftForearmTwist != null)
                        return leftForearmTwist.Clone();
                    return (rightForearmTwist ?? new ForearmTwistConfig()).Clone();
                }

                default:
                    return new ForearmTwistConfig { enabled = false };
            }
        }
    }
}
