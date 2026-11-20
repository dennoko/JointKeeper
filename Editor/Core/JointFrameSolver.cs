using System.Collections.Generic;
using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// Humanoid ボーン位置と実測ヒンジ軸から、ボーンローカル軸非依存の正規直交関節フレームを構築する (§5.2)。
    /// </summary>
    internal static class JointFrameSolver
    {
        private const float RestBendSinThreshold = 0.0348995f; // sin(2 deg)
        private const float RestBendPriorDotThreshold = 0.7f;  // within ~45 deg of prior

        /// <summary>
        /// アバター基準前方ベクトル F_av を Humanoid 骨格から算出する。
        /// 取得不可・異常時は avatarRoot.forward へフォールバック。
        /// </summary>
        public static Vector3 ComputeAvatarForward(
            Animator animator,
            Transform avatarRoot,
            out string warning,
            Dictionary<HumanBodyBones, Transform> humanoidBones = null)
        {
            warning = null;
            Vector3 rootForward = avatarRoot != null ? avatarRoot.forward.normalized : Vector3.forward;
            if (animator == null && humanoidBones == null)
                return rootForward;

            if (humanoidBones == null)
                humanoidBones = HumanoidBoneResolver.ResolveAll(animator, out _);

            Transform leftLeg = HumanoidBoneResolver.GetBone(humanoidBones, HumanBodyBones.LeftUpperLeg);
            Transform rightLeg = HumanoidBoneResolver.GetBone(humanoidBones, HumanBodyBones.RightUpperLeg);
            Transform hips = HumanoidBoneResolver.GetBone(humanoidBones, HumanBodyBones.Hips);
            Transform spine = HumanoidBoneResolver.GetBone(humanoidBones, HumanBodyBones.Spine);
            if (spine == null)
                spine = HumanoidBoneResolver.GetBone(humanoidBones, HumanBodyBones.Chest);

            if (leftLeg == null || rightLeg == null || hips == null || spine == null)
                return rootForward;

            Vector3 rAv = rightLeg.position - leftLeg.position;
            Vector3 uAv = spine.position - hips.position;
            // Unity 左手座標系: Cross(Right(+X), Up(+Y)) = Forward(+Z)
            Vector3 cross = Vector3.Cross(rAv, uAv);
            if (cross.sqrMagnitude < 1e-8f)
                return rootForward;

            Vector3 fAv = cross.normalized;
            if (Vector3.Dot(fAv, rootForward) < 0f)
            {
                warning = "Humanoid 骨格の前方向がアバタールート forward と反転しています。avatarRoot.forward にフォールバックします。";
                return rootForward;
            }

            return fAv;
        }

        /// <summary>
        /// 関節フレームを純粋計算で構築する（単体テスト可能）。
        /// </summary>
        public static JointFrame Solve(
            JointId jointId,
            Vector3 upperPos,
            Vector3 lowerPos,
            Vector3? childPos,
            Vector3 avatarForward,
            Vector3 avatarUp,
            Vector3? measuredHingeAxis = null,
            Vector3 pivotOffset = default,
            float outwardAngleOffsetDeg = 0f)
        {
            Vector3 vU = lowerPos - upperPos;
            float lenU = vU.magnitude;
            Vector3 uU = lenU > 1e-6f ? (vU / lenU) : Vector3.down;

            Vector3 uL = uU;
            float limbLength = Mathf.Max(lenU, 1e-4f);
            if (childPos.HasValue)
            {
                Vector3 vL = childPos.Value - lowerPos;
                float lenL = vL.magnitude;
                if (lenL > 1e-4f)
                {
                    uL = vL / lenL;
                    limbLength = Mathf.Max(Mathf.Min(lenU, lenL), 1e-4f);
                }
            }

            Vector3 sumAxis = uU + uL;
            Vector3 limbAxis = sumAxis.sqrMagnitude > 1e-8f ? sumAxis.normalized : uU;

            // 解剖学的プライオリ: 肘は -F_av（後方）、膝は +F_av（前方）
            Vector3 rawPrior = jointId.IsElbow() ? -avatarForward : avatarForward;
            Vector3 projPrior = rawPrior - Vector3.Dot(rawPrior, limbAxis) * limbAxis;
            if (projPrior.sqrMagnitude < 1e-6f)
            {
                projPrior = avatarUp - Vector3.Dot(avatarUp, limbAxis) * limbAxis;
                if (projPrior.sqrMagnitude < 1e-6f)
                    projPrior = Vector3.Cross(limbAxis, Vector3.right);
            }
            Vector3 nPrior = projPrior.normalized;

            Vector3 nBase;
            JointFrameSource source;

            // 第1優先: 実測ヒンジ軸 (ADR-06)
            if (measuredHingeAxis.HasValue && measuredHingeAxis.Value.sqrMagnitude > 1e-6f)
            {
                Vector3 hProj = measuredHingeAxis.Value - Vector3.Dot(measuredHingeAxis.Value, limbAxis) * limbAxis;
                if (hProj.sqrMagnitude > 1e-6f)
                {
                    Vector3 hNorm = hProj.normalized;
                    Vector3 candidate = Vector3.Cross(hNorm, limbAxis).normalized;
                    if (Vector3.Dot(candidate, nPrior) < 0f)
                        candidate = -candidate;
                    nBase = candidate;
                    source = JointFrameSource.Measured;
                }
                else
                {
                    nBase = SolveFallbackOutward(uU, uL, limbAxis, nPrior, out source);
                }
            }
            else
            {
                nBase = SolveFallbackOutward(uU, uL, limbAxis, nPrior, out source);
            }

            Vector3 hingeBase = Vector3.Cross(limbAxis, nBase);
            if (hingeBase.sqrMagnitude < 1e-8f)
                hingeBase = Vector3.right;
            else
                hingeBase.Normalize();

            var baseFrame = new JointFrame(
                lowerPos,
                lowerPos,
                limbAxis,
                nBase,
                hingeBase,
                limbLength,
                Quaternion.LookRotation(nBase, limbAxis),
                source);

            if (pivotOffset != Vector3.zero || Mathf.Abs(outwardAngleOffsetDeg) > 1e-4f)
                return baseFrame.WithOffsets(pivotOffset, outwardAngleOffsetDeg);

            return baseFrame;
        }

        private static Vector3 SolveFallbackOutward(
            Vector3 uU,
            Vector3 uL,
            Vector3 limbAxis,
            Vector3 nPrior,
            out JointFrameSource source)
        {
            // 第2優先: レスト曲がりベクトル b = uU - uL
            Vector3 b = uU - uL;
            float bMag = b.magnitude;
            if (bMag > RestBendSinThreshold)
            {
                Vector3 bNorm = b / bMag;
                if (Vector3.Dot(bNorm, nPrior) > RestBendPriorDotThreshold)
                {
                    Vector3 bOrtho = bNorm - Vector3.Dot(bNorm, limbAxis) * limbAxis;
                    if (bOrtho.sqrMagnitude > 1e-6f)
                    {
                        source = JointFrameSource.RestBend;
                        return bOrtho.normalized;
                    }
                }
            }

            // 第3優先: 解剖学的プライオリ
            source = JointFrameSource.Prior;
            return nPrior;
        }
    }
}
