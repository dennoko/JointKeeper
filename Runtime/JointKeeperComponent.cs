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

        public bool unlinkKneeLeftRight = false;
        public JointConfig leftKnee = JointConfig.CreateKneeDefault();
        public JointConfig rightKnee = JointConfig.CreateKneeDefault();

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
    }
}
