using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.JointKeeper
{
    /// <summary>
    /// 1 関節分のリグ・ウェイト再分配設定。
    /// </summary>
    [Serializable]
    public class JointConfig
    {
        public bool enabled = true;

        // ─── リグ設定 ───
        [Range(0.1f, 0.9f)]
        public float bendFollowRatio = 0.50f;

        [Range(-0.01f, 0.02f)]
        public float pushAt90Deg = 0.002f;

        /// <summary>関節ローカルフレーム基準 (x = Axial, y = Outward, z = Hinge) のピボット微調整 (m)。</summary>
        public Vector3 pivotOffset = Vector3.zero;

        /// <summary>肢軸回りの外側ベクトル補正角 (deg)。</summary>
        [Range(-90f, 90f)]
        public float outwardAngleOffset = 0f;

        // ─── ウェイト再分配設定 ───
        [Range(0f, 1f)]
        public float strength = 0.85f;

        [Range(1.0f, 3.0f)]
        public float blendCurvePower = 1.2f;

        [Range(0.001f, 0.25f)]
        public float minGroupWeightThreshold = 0.01f;

        [Range(0.05f, 1.0f)]
        public float axialRadiusScale = 0.35f;

        [Range(0.05f, 1.5f)]
        public float radialRadiusScale = 0.45f;

        [Range(0f, 0.95f)]
        public float innerFalloffRatio = 0.25f;

        [Range(-1f, 1f)]
        public float innerCutoff = -0.15f;

        [Range(-1f, 1f)]
        public float outerFull = 0.35f;

        [Range(0f, 0.5f)]
        public float innerRetention = 0.0f;

        // ─── ツイストボーン・詳細設定 ───
        public bool autoDetectTwistBones = true;
        public List<Transform> extraProximalBones = new List<Transform>();
        public List<Transform> extraDistalBones = new List<Transform>();
        public List<Transform> excludedBones = new List<Transform>();

        public DistalSourceMode distalSourceMode = DistalSourceMode.AutoLowerBone;
        public Transform distalSourceOverride;

        [Range(0f, 0.15f)]
        public float slotSnapThreshold = 0.05f;

        [Range(0f, 0.1f)]
        public float noisePruneThreshold = 0.03f;

        public static JointConfig CreateElbowDefault()
        {
            return new JointConfig();
        }

        public static JointConfig CreateKneeDefault()
        {
            return new JointConfig();
        }

        /// <summary>
        /// 左側設定の数値を右側用にミラーコピーする（ボーン参照リストは呼び出し先を保持）。
        /// pivotOffset.z (Hinge) と outwardAngleOffset のみ符号反転する。
        /// </summary>
        public void CopyMirroredNumericsFrom(JointConfig source)
        {
            if (source == null) return;

            enabled = source.enabled;
            bendFollowRatio = source.bendFollowRatio;
            pushAt90Deg = source.pushAt90Deg;
            pivotOffset = new Vector3(source.pivotOffset.x, source.pivotOffset.y, -source.pivotOffset.z);
            outwardAngleOffset = -source.outwardAngleOffset;

            strength = source.strength;
            blendCurvePower = source.blendCurvePower;
            minGroupWeightThreshold = source.minGroupWeightThreshold;
            axialRadiusScale = source.axialRadiusScale;
            radialRadiusScale = source.radialRadiusScale;
            innerFalloffRatio = source.innerFalloffRatio;
            innerCutoff = source.innerCutoff;
            outerFull = source.outerFull;
            innerRetention = source.innerRetention;

            autoDetectTwistBones = source.autoDetectTwistBones;
            slotSnapThreshold = source.slotSnapThreshold;
            noisePruneThreshold = source.noisePruneThreshold;
        }

        /// <summary>
        /// ビルド・プレビュー用に独立したクローンを作成する。
        /// </summary>
        public JointConfig Clone()
        {
            return new JointConfig
            {
                enabled = enabled,
                bendFollowRatio = bendFollowRatio,
                pushAt90Deg = pushAt90Deg,
                pivotOffset = pivotOffset,
                outwardAngleOffset = outwardAngleOffset,
                strength = strength,
                blendCurvePower = blendCurvePower,
                minGroupWeightThreshold = minGroupWeightThreshold,
                axialRadiusScale = axialRadiusScale,
                radialRadiusScale = radialRadiusScale,
                innerFalloffRatio = innerFalloffRatio,
                innerCutoff = innerCutoff,
                outerFull = outerFull,
                innerRetention = innerRetention,
                autoDetectTwistBones = autoDetectTwistBones,
                extraProximalBones = extraProximalBones != null ? new List<Transform>(extraProximalBones) : new List<Transform>(),
                extraDistalBones = extraDistalBones != null ? new List<Transform>(extraDistalBones) : new List<Transform>(),
                excludedBones = excludedBones != null ? new List<Transform>(excludedBones) : new List<Transform>(),
                distalSourceMode = distalSourceMode,
                distalSourceOverride = distalSourceOverride,
                slotSnapThreshold = slotSnapThreshold,
                noisePruneThreshold = noisePruneThreshold,
            };
        }
    }
}
