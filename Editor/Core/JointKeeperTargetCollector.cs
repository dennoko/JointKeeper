using System.Collections.Generic;
using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// JointKeeperComponent の対象 SkinnedMeshRenderer を収集する共通ヘルパー。
    /// NDMF ビルド・プレビュー・破壊的ベイク・Inspector のすべてで共用する（NDMF 非依存）。
    /// </summary>
    internal static class JointKeeperTargetCollector
    {
        public static List<SkinnedMeshRenderer> CollectTargets(
            JointKeeperComponent comp,
            Transform avatarRoot,
            bool logOutOfAvatarWarning = false)
        {
            var result = new List<SkinnedMeshRenderer>();
            if (comp == null) return result;

            if (comp.targetMode == TargetRendererMode.ManualList)
            {
                if (comp.targetRenderers == null) return result;
                var seen = new HashSet<SkinnedMeshRenderer>();
                for (int i = 0; i < comp.targetRenderers.Count; i++)
                {
                    var smr = comp.targetRenderers[i];
                    if (smr == null || !seen.Add(smr)) continue;
                    if (avatarRoot != null && !smr.transform.IsChildOf(avatarRoot))
                    {
                        if (logOutOfAvatarWarning)
                        {
                            Debug.LogWarning($"[JointKeeper] アバター外の SkinnedMeshRenderer '{smr.name}' が指定されているため無視しました。", comp);
                        }
                        continue;
                    }
                    result.Add(smr);
                }
                return result;
            }

            var excluded = new HashSet<SkinnedMeshRenderer>();
            if (comp.excludedRenderers != null)
            {
                for (int i = 0; i < comp.excludedRenderers.Count; i++)
                {
                    if (comp.excludedRenderers[i] != null)
                        excluded.Add(comp.excludedRenderers[i]);
                }
            }

            var found = comp.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < found.Length; i++)
            {
                if (found[i] != null && !excluded.Contains(found[i]))
                    result.Add(found[i]);
            }
            return result;
        }
    }
}
