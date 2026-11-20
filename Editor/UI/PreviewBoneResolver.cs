using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// Edit モード（MA Merge Armature / Scale Adjuster 実行前）において、
    /// 衣装ボーンとアバターボーンの対応付けおよび Scale Adjuster 値を解決する (§7.3・破壊的ベイク共用)。
    /// Modular Avatar へのアセンブリ依存を持たずにリフレクションで読み取る。
    /// </summary>
    internal static class PreviewBoneResolver
    {
        private const string MergeArmatureTypeName = "nadena.dev.modular_avatar.core.ModularAvatarMergeArmature";
        private const string ScaleAdjusterTypeName = "nadena.dev.modular_avatar.core.ModularAvatarScaleAdjuster";

        internal sealed class MergeArmatureMapping
        {
            public Transform OutfitArmatureRoot;
            public Transform AvatarTargetRoot;
            public readonly Dictionary<Transform, Transform> OutfitToAvatar = new Dictionary<Transform, Transform>();
            public readonly Dictionary<Transform, Transform> AvatarToOutfit = new Dictionary<Transform, Transform>();
        }

        /// <summary>
        /// アバター配下の全 ModularAvatarMergeArmature を解析し、
        /// 「衣装側 Transform → 対応先アバター側 Transform」のエイリアスマップを返す。
        /// </summary>
        public static Dictionary<Transform, Transform> BuildAliasMap(GameObject avatarRoot)
        {
            var aliasMap = new Dictionary<Transform, Transform>();
            var mappings = BuildMergeArmatureMappings(avatarRoot);
            for (int i = 0; i < mappings.Count; i++)
            {
                foreach (var kv in mappings[i].OutfitToAvatar)
                    aliasMap[kv.Key] = kv.Value;
            }
            return aliasMap;
        }

        /// <summary>
        /// ModularAvatarMergeArmature ごとの双方向マッピング一覧を構築する。
        /// 破壊的ベイク時に衣装側の Armature ボーンを直接特定するために使用する。
        /// </summary>
        public static List<MergeArmatureMapping> BuildMergeArmatureMappings(GameObject avatarRoot)
        {
            var result = new List<MergeArmatureMapping>();
            if (avatarRoot == null) return result;

            var allComponents = avatarRoot.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < allComponents.Length; i++)
            {
                Component c = allComponents[i];
                if (c == null) continue;
                Type t = c.GetType();
                if (!string.Equals(t.FullName, MergeArmatureTypeName, StringComparison.Ordinal))
                    continue;

                var mapping = TryBuildMapping(c, t);
                if (mapping != null && mapping.OutfitToAvatar.Count > 0)
                    result.Add(mapping);
            }

            return result;
        }

        /// <summary>
        /// 指定 SMR のスキニングボーンがどの MergeArmature 配下に属するか判定し、
        /// 該当すれば衣装側の (upper, lower, child) を返す。該当しなければアバター側をそのまま返す。
        /// </summary>
        public static bool TryResolveOutfitJointBones(
            Transform[] smrBones,
            List<MergeArmatureMapping> mappings,
            Transform avatarUpper,
            Transform avatarLower,
            Transform avatarChild,
            out Transform targetUpper,
            out Transform targetLower,
            out Transform targetChild)
        {
            targetUpper = avatarUpper;
            targetLower = avatarLower;
            targetChild = avatarChild;

            if (smrBones == null || smrBones.Length == 0 || mappings == null || mappings.Count == 0
                || avatarUpper == null || avatarLower == null)
            {
                return false;
            }

            MergeArmatureMapping bestMapping = null;
            int bestHits = 0;

            for (int m = 0; m < mappings.Count; m++)
            {
                var mapping = mappings[m];
                if (!mapping.AvatarToOutfit.ContainsKey(avatarUpper) || !mapping.AvatarToOutfit.ContainsKey(avatarLower))
                    continue;

                int hits = 0;
                for (int b = 0; b < smrBones.Length; b++)
                {
                    Transform bone = smrBones[b];
                    if (bone == null) continue;
                    if (mapping.OutfitToAvatar.ContainsKey(bone) || bone.IsChildOf(mapping.OutfitArmatureRoot))
                        hits++;
                }

                if (hits > bestHits)
                {
                    bestHits = hits;
                    bestMapping = mapping;
                }
            }

            if (bestMapping == null || bestHits == 0)
                return false;

            targetUpper = bestMapping.AvatarToOutfit[avatarUpper];
            targetLower = bestMapping.AvatarToOutfit[avatarLower];
            targetChild = (avatarChild != null && bestMapping.AvatarToOutfit.TryGetValue(avatarChild, out var oc))
                ? oc
                : null;
            return targetUpper != null && targetLower != null;
        }

        /// <summary>
        /// 指定ボーン配列に ModularAvatarScaleAdjuster が付与されていれば、その非 (1,1,1) スケールを収集する。
        /// </summary>
        public static Dictionary<Transform, Vector3> CollectScaleAdjusterScales(Transform[] bones)
        {
            var result = new Dictionary<Transform, Vector3>();
            if (bones == null) return result;

            for (int i = 0; i < bones.Length; i++)
            {
                Transform b = bones[i];
                if (b == null || result.ContainsKey(b)) continue;

                if (TryGetScaleAdjusterScale(b, out Vector3 scale)
                    && (scale - Vector3.one).sqrMagnitude > 1e-8f)
                {
                    result[b] = scale;
                }
            }
            return result;
        }

        public static bool TryGetScaleAdjusterScale(Transform bone, out Vector3 scale)
        {
            scale = Vector3.one;
            if (bone == null) return false;

            var comps = bone.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                Component c = comps[i];
                if (c == null) continue;
                Type t = c.GetType();
                if (!string.Equals(t.FullName, ScaleAdjusterTypeName, StringComparison.Ordinal))
                    continue;

                object val = GetFieldOrProperty(c, t, "Scale") ?? GetFieldOrProperty(c, t, "m_Scale");
                if (val is Vector3 v)
                {
                    scale = v;
                    return true;
                }
            }
            return false;
        }

        private static MergeArmatureMapping TryBuildMapping(Component comp, Type type)
        {
            object mergeTargetRef = GetFieldOrProperty(comp, type, "mergeTarget");
            GameObject targetGo = ResolveAvatarObjectReference(mergeTargetRef, comp);
            if (targetGo == null) return null;

            string prefix = GetFieldOrProperty(comp, type, "prefix") as string ?? string.Empty;
            string suffix = GetFieldOrProperty(comp, type, "suffix") as string ?? string.Empty;

            var mapping = new MergeArmatureMapping
            {
                OutfitArmatureRoot = comp.transform,
                AvatarTargetRoot = targetGo.transform,
            };

            MapRecursive(comp.transform, targetGo.transform, prefix, suffix, mapping);
            return mapping;
        }

        private static void MapRecursive(
            Transform outfitNode,
            Transform avatarNode,
            string prefix,
            string suffix,
            MergeArmatureMapping mapping)
        {
            if (outfitNode == null || avatarNode == null) return;
            mapping.OutfitToAvatar[outfitNode] = avatarNode;
            if (!mapping.AvatarToOutfit.ContainsKey(avatarNode))
                mapping.AvatarToOutfit[avatarNode] = outfitNode;

            for (int i = 0; i < outfitNode.childCount; i++)
            {
                Transform outfitChild = outfitNode.GetChild(i);
                string baseName = StripPrefixSuffix(outfitChild.name, prefix, suffix);
                Transform avatarChild = avatarNode.Find(baseName);
                if (avatarChild != null)
                {
                    MapRecursive(outfitChild, avatarChild, prefix, suffix, mapping);
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
