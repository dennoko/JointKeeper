using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// Edit モード（MA Merge Armature 実行前）において、衣装ボーンとアバターボーンの対応付けを解決する (§7.3)。
    /// Modular Avatar へのアセンブリ依存を持たずにリフレクションで ModularAvatarMergeArmature を読み取る。
    /// </summary>
    internal static class PreviewBoneResolver
    {
        private const string MergeArmatureTypeName = "nadena.dev.modular_avatar.core.ModularAvatarMergeArmature";

        /// <summary>
        /// アバター配下の全 ModularAvatarMergeArmature を解析し、
        /// 「衣装側 Transform → 対応先アバター側 Transform」のエイリアスマップを返す。
        /// </summary>
        public static Dictionary<Transform, Transform> BuildAliasMap(GameObject avatarRoot)
        {
            var aliasMap = new Dictionary<Transform, Transform>();
            if (avatarRoot == null) return aliasMap;

            var allComponents = avatarRoot.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < allComponents.Length; i++)
            {
                Component c = allComponents[i];
                if (c == null) continue;
                Type t = c.GetType();
                if (!string.Equals(t.FullName, MergeArmatureTypeName, StringComparison.Ordinal))
                    continue;

                TryResolveMergeArmature(c, t, aliasMap);
            }

            return aliasMap;
        }

        private static void TryResolveMergeArmature(Component comp, Type type, Dictionary<Transform, Transform> aliasMap)
        {
            // mergeTarget (AvatarObjectReference) / prefix (string) / suffix (string)
            object mergeTargetRef = GetFieldOrProperty(comp, type, "mergeTarget");
            GameObject targetGo = ResolveAvatarObjectReference(mergeTargetRef, comp);
            if (targetGo == null) return;

            string prefix = GetFieldOrProperty(comp, type, "prefix") as string ?? string.Empty;
            string suffix = GetFieldOrProperty(comp, type, "suffix") as string ?? string.Empty;

            MapRecursive(comp.transform, targetGo.transform, prefix, suffix, aliasMap);
        }

        private static void MapRecursive(
            Transform outfitNode,
            Transform avatarNode,
            string prefix,
            string suffix,
            Dictionary<Transform, Transform> aliasMap)
        {
            if (outfitNode == null || avatarNode == null) return;
            aliasMap[outfitNode] = avatarNode;

            for (int i = 0; i < outfitNode.childCount; i++)
            {
                Transform outfitChild = outfitNode.GetChild(i);
                string baseName = StripPrefixSuffix(outfitChild.name, prefix, suffix);
                Transform avatarChild = avatarNode.Find(baseName);
                if (avatarChild != null)
                {
                    MapRecursive(outfitChild, avatarChild, prefix, suffix, aliasMap);
                }
            }
        }

        private static string StripPrefixSuffix(string name, string prefix, string suffix)
        {
            string s = name;
            if (!string.IsNullOrEmpty(prefix) && s.StartsWith(prefix, StringComparison.Ordinal))
                s = s.Substring(prefix.Length);
            if (!string.IsNullOrEmpty(suffix) && s.EndsWith(suffix, StringComparison.Ordinal))
                s = s.Substring(0, s.Length - suffix.Length);
            return s;
        }

        private static object GetFieldOrProperty(object obj, Type type, string name)
        {
            var f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(obj);
            var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null) return p.GetValue(obj);
            return null;
        }

        private static GameObject ResolveAvatarObjectReference(object aor, Component context)
        {
            if (aor == null) return null;
            if (aor is GameObject go) return go;

            Type aorType = aor.GetType();
            var getMethod = aorType.GetMethod("Get", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Component) }, null);
            if (getMethod != null)
            {
                return getMethod.Invoke(aor, new object[] { context }) as GameObject;
            }
            return null;
        }
    }
}
