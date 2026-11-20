using System;
using System.Collections.Generic;
using UnityEngine;
using Dennokoworks.JointKeeper;
#if JOINT_KEEPER_NDMF
using nadena.dev.ndmf.preview;
#endif

namespace Dennokoworks.JointKeeper.Editor
{
    internal enum PreviewHeatmapMode
    {
        Off = 0,
        HelperWeight = 1,
        FourBoneDiagnostics = 2,
    }

    /// <summary>
    /// Phase 2 プレビューの Inspector 操作状態および実測ヒンジ軸キャッシュ。
    /// Enabled / StructureVersion のみ NDMF PublishedValue として公開し、
    /// 曲げ角・ツイスト角・ヒートマップ切替は毎フレーム OnFrame で即時反映（ノード再構築なし）。
    /// </summary>
    internal static class JointKeeperPreviewState
    {
#if JOINT_KEEPER_NDMF
        public static readonly PublishedValue<bool> EnabledPublished =
            new PublishedValue<bool>(false, debugName: "JointKeeper/PreviewEnabled");
        public static readonly PublishedValue<int> StructureVersionPublished =
            new PublishedValue<int>(0, debugName: "JointKeeper/StructureVersion");
#endif

        public static bool Enabled { get; private set; }
        public static float BendAngleDeg { get; private set; }
        public static float TwistAngleDeg { get; private set; }
        public static bool ShowBeforeOnly { get; private set; }
        public static PreviewHeatmapMode HeatmapMode { get; private set; }
        public static int Version { get; private set; }

        public static event Action Changed;

        private static readonly Dictionary<int, Dictionary<JointId, Vector3>> MeasuredAxesCache =
            new Dictionary<int, Dictionary<JointId, Vector3>>();

        public static void SetEnabled(bool value)
        {
            if (Enabled == value) return;
            Enabled = value;
#if JOINT_KEEPER_NDMF
            EnabledPublished.Value = value;
#endif
            NotifyChanged();
        }

        public static void SetBendAngle(float deg)
        {
            deg = Mathf.Clamp(deg, 0f, 150f);
            if (Mathf.Approximately(BendAngleDeg, deg)) return;
            BendAngleDeg = deg;
            NotifyChanged();
        }

        public static void SetTwistAngle(float deg)
        {
            deg = Mathf.Clamp(deg, -90f, 90f);
            if (Mathf.Approximately(TwistAngleDeg, deg)) return;
            TwistAngleDeg = deg;
            NotifyChanged();
        }

        public static void SetShowBeforeOnly(bool value)
        {
            if (ShowBeforeOnly == value) return;
            ShowBeforeOnly = value;
            NotifyChanged();
        }

        public static void SetHeatmapMode(PreviewHeatmapMode mode)
        {
            if (HeatmapMode == mode) return;
            HeatmapMode = mode;
            NotifyChanged();
        }

        public static void ResetPose()
        {
            BendAngleDeg = 0f;
            TwistAngleDeg = 0f;
            ShowBeforeOnly = false;
            NotifyChanged();
        }

        public static Dictionary<JointId, Vector3> GetCachedMeasuredAxes(GameObject avatarRoot)
        {
            if (avatarRoot == null) return null;
            return MeasuredAxesCache.TryGetValue(avatarRoot.GetInstanceID(), out var dict) ? dict : null;
        }

        /// <summary>
        /// 元シーンを変更せず HideAndDontSave クローン上で HingeAxisSampler を実行して結果をキャッシュする (§7.3)。
        /// </summary>
        public static Dictionary<JointId, Vector3> ResampleMeasuredAxesSafe(GameObject avatarRoot)
        {
            if (avatarRoot == null) return null;

            GameObject clone = UnityEngine.Object.Instantiate(avatarRoot);
            SetHideFlagsRecursive(clone, HideFlags.HideAndDontSave);
            try
            {
                var anim = clone.GetComponent<Animator>();
                if (anim == null) anim = clone.GetComponentInChildren<Animator>(true);
                var sampled = HingeAxisSampler.SampleAll(anim);
                MeasuredAxesCache[avatarRoot.GetInstanceID()] = sampled;
#if JOINT_KEEPER_NDMF
                StructureVersionPublished.Value = StructureVersionPublished.Value + 1;
#endif
                NotifyChanged();
                return sampled;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clone);
            }
        }

        private static void SetHideFlagsRecursive(GameObject go, HideFlags flags)
        {
            if (go == null) return;
            go.hideFlags = flags;
            Transform t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                SetHideFlagsRecursive(t.GetChild(i).gameObject, flags);
        }

        private static void NotifyChanged()
        {
            Version++;
            Changed?.Invoke();
        }
    }
}
