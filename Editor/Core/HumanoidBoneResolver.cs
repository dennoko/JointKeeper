using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// Animator.GetBoneTransform が Edit モードや Modular Avatar の RebindHumanoidAvatarPass 直後、
    /// あるいは非アクティブなアバター上で null を返す Unity の仕様を回避し、
    /// Avatar.humanDescription から確実に Humanoid ボーンの Transform を解決する。
    /// </summary>
    internal static class HumanoidBoneResolver
    {
        private static readonly FieldInfo SkeletonBoneParentField =
            typeof(SkeletonBone).GetField("parentName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>
        /// 指定された Animator から全 HumanBodyBones の Transform マップを構築する。
        /// まず Animator.GetBoneTransform を試し、欠損があれば Avatar.humanDescription から補完する。
        /// </summary>
        public static Dictionary<HumanBodyBones, Transform> ResolveAll(Animator animator, out bool usedFallback)
        {
            usedFallback = false;
            var map = new Dictionary<HumanBodyBones, Transform>();
            if (animator == null)
                return map;

            Avatar avatar = animator.avatar;
            bool isHuman = animator.isHuman || (avatar != null && avatar.isValid && avatar.isHuman);
            if (!isHuman)
                return map;

            // 1. 高速パス: Animator.GetBoneTransform
            if (animator.isHuman)
            {
                for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
                {
                    var bone = (HumanBodyBones)i;
                    Transform t = animator.GetBoneTransform(bone);
                    if (t != null)
                        map[bone] = t;
                }
            }

            // 2. コアボーンが取れていれば完了
            bool hasCoreBones = map.ContainsKey(HumanBodyBones.Hips)
                && (map.ContainsKey(HumanBodyBones.LeftUpperLeg) || map.ContainsKey(HumanBodyBones.LeftUpperArm));
            if (hasCoreBones || avatar == null)
                return map;

            // 3. フォールバック: Avatar.humanDescription から階層を直接解決
            usedFallback = true;
            ResolveFromHumanDescription(animator.transform, avatar.humanDescription, map);
            return map;
        }

        public static Transform GetBone(Dictionary<HumanBodyBones, Transform> map, HumanBodyBones bone)
        {
            if (map == null) return null;
            return map.TryGetValue(bone, out Transform t) ? t : null;
        }

        internal static void ResolveFromHumanDescription(
            Transform avatarRoot,
            HumanDescription humanDesc,
            Dictionary<HumanBodyBones, Transform> output)
        {
            if (avatarRoot == null || humanDesc.human == null || humanDesc.human.Length == 0)
                return;

            // skeleton の親子名関係を使って階層パスベースでマッピング（MA HumanoidAvatarDescriptorRebuilder と同方式）
            var byBoneName = new Dictionary<string, Transform>(StringComparer.Ordinal);
            if (humanDesc.skeleton != null && humanDesc.skeleton.Length > 0)
            {
                for (int i = 0; i < humanDesc.skeleton.Length; i++)
                {
                    SkeletonBone sb = humanDesc.skeleton[i];
                    if (string.IsNullOrEmpty(sb.name)) continue;

                    Transform resolved = null;
                    if (i == 0)
                    {
                        resolved = avatarRoot;
                    }
                    else
                    {
                        string parentName = SkeletonBoneParentField?.GetValue(sb) as string;
                        if (!string.IsNullOrWhiteSpace(parentName) && byBoneName.TryGetValue(parentName, out Transform parentT) && parentT != null)
                        {
                            resolved = parentT.Find(sb.name);
                        }
                        if (resolved == null)
                        {
                            resolved = avatarRoot.Find(sb.name);
                        }
                    }

                    if (resolved != null)
                        byBoneName[sb.name] = resolved;
                }
            }

            // 見つからなかったボーン向けの BFS 名前インデックス（親が中間オブジェクトへ移動されたケースにも対応）
            Dictionary<string, Transform> bfsByName = null;

            for (int i = 0; i < humanDesc.human.Length; i++)
            {
                HumanBone hb = humanDesc.human[i];
                if (string.IsNullOrEmpty(hb.humanName) || string.IsNullOrEmpty(hb.boneName))
                    continue;

                string enumName = hb.humanName.Replace(" ", "");
                if (!Enum.TryParse<HumanBodyBones>(enumName, out HumanBodyBones boneEnum))
                    continue;

                if (output.ContainsKey(boneEnum))
                    continue;

                if (!byBoneName.TryGetValue(hb.boneName, out Transform found) || found == null)
                {
                    if (bfsByName == null)
                        bfsByName = BuildBfsNameMap(avatarRoot);
                    bfsByName.TryGetValue(hb.boneName, out found);
                }

                if (found != null)
                    output[boneEnum] = found;
            }
        }

        private static Dictionary<string, Transform> BuildBfsNameMap(Transform root)
        {
            var map = new Dictionary<string, Transform>(StringComparer.Ordinal);
            var queue = new Queue<Transform>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                Transform cur = queue.Dequeue();
                if (!map.ContainsKey(cur.name))
                    map[cur.name] = cur;
                for (int i = 0; i < cur.childCount; i++)
                    queue.Enqueue(cur.GetChild(i));
            }
            return map;
        }
    }
}
