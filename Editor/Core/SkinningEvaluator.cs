using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// CPU スキニングによる頂点ワールド座標評価（正本）およびブレンド Bindpose 逆算 (ADR-07, §5.3)。
    /// </summary>
    internal static class SkinningEvaluator
    {
        /// <summary>
        /// SkinnedMeshRenderer の現在の BlendShape・ボーン姿勢から全頂点のワールド座標を CPU で厳密算出する。
        /// 非アクティブなトグル衣装でも同一の結果を返す。
        /// </summary>
        public static Vector3[] EvaluateWorldVerticesCpu(
            SkinnedMeshRenderer smr,
            Dictionary<Transform, Vector3> extraBoneScales = null)
        {
            if (smr == null || smr.sharedMesh == null)
                return System.Array.Empty<Vector3>();

            Mesh mesh = smr.sharedMesh;
            int bsCount = mesh.blendShapeCount;
            float[] weights = bsCount > 0 ? new float[bsCount] : null;
            for (int i = 0; i < bsCount; i++)
                weights[i] = smr.GetBlendShapeWeight(i);

            return EvaluateWorldVerticesCpu(mesh, smr.bones, weights, null, null, extraBoneScales);
        }

        /// <summary>
        /// Mesh・bones・blendShapeWeights から CPU スキニングでワールド座標を算出する。
        /// </summary>
        public static Vector3[] EvaluateWorldVerticesCpu(
            Mesh mesh,
            Transform[] bones,
            float[] blendShapeWeights = null,
            BoneWeight[] boneWeightsOverride = null,
            Matrix4x4[] bindposesOverride = null,
            Dictionary<Transform, Vector3> extraBoneScales = null)
        {
            if (mesh == null)
                return System.Array.Empty<Vector3>();

            Vector3[] baseVerts = mesh.vertices;
            int vertCount = baseVerts.Length;
            if (vertCount == 0)
                return System.Array.Empty<Vector3>();

            // 1. BlendShape 変位適用（マルチフレーム補間対応）
            Vector3[] shaped = new Vector3[vertCount];
            System.Array.Copy(baseVerts, shaped, vertCount);

            int bsCount = mesh.blendShapeCount;
            if (blendShapeWeights != null && bsCount > 0)
            {
                Vector3[] deltaA = null;
                Vector3[] deltaB = null;
                int count = Mathf.Min(bsCount, blendShapeWeights.Length);

                for (int s = 0; s < count; s++)
                {
                    float w = blendShapeWeights[s];
                    if (Mathf.Abs(w) < 1e-6f) continue;

                    int frameCount = mesh.GetBlendShapeFrameCount(s);
                    if (frameCount <= 0) continue;

                    if (deltaA == null) deltaA = new Vector3[vertCount];

                    if (frameCount == 1)
                    {
                        float fw = mesh.GetBlendShapeFrameWeight(s, 0);
                        if (Mathf.Abs(fw) < 1e-6f) continue;
                        mesh.GetBlendShapeFrameVertices(s, 0, deltaA, null, null);
                        float t = w / fw;
                        for (int v = 0; v < vertCount; v++)
                            shaped[v] += deltaA[v] * t;
                    }
                    else
                    {
                        ApplyMultiFrameBlendShape(mesh, s, frameCount, w, shaped, deltaA, ref deltaB);
                    }
                }
            }

            // 2. スキニング行列事前計算
            Matrix4x4[] bindposes = bindposesOverride ?? mesh.bindposes;
            BoneWeight[] boneWeights = boneWeightsOverride ?? mesh.boneWeights;

            if (bones == null || bindposes == null || boneWeights == null
                || boneWeights.Length != vertCount)
            {
                return shaped;
            }

            int boneCount = Mathf.Min(bones.Length, bindposes.Length);
            var skinMatrices = new Matrix4x4[boneCount];
            var validBone = new bool[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                if (bones[b] != null)
                {
                    Matrix4x4 boneWorld = bones[b].localToWorldMatrix;
                    if (extraBoneScales != null && extraBoneScales.TryGetValue(bones[b], out Vector3 extraScale))
                        boneWorld = boneWorld * Matrix4x4.Scale(extraScale);
                    skinMatrices[b] = boneWorld * bindposes[b];
                    validBone[b] = true;
                }
            }

            // 3. 4-bone LBS 評価
            var result = new Vector3[vertCount];
            for (int v = 0; v < vertCount; v++)
            {
                BoneWeight bw = boneWeights[v];
                Vector3 sv = shaped[v];
                Vector3 acc = Vector3.zero;
                float weightSum = 0f;

                Accumulate(bw.boneIndex0, bw.weight0, sv, boneCount, validBone, skinMatrices, ref acc, ref weightSum);
                Accumulate(bw.boneIndex1, bw.weight1, sv, boneCount, validBone, skinMatrices, ref acc, ref weightSum);
                Accumulate(bw.boneIndex2, bw.weight2, sv, boneCount, validBone, skinMatrices, ref acc, ref weightSum);
                Accumulate(bw.boneIndex3, bw.weight3, sv, boneCount, validBone, skinMatrices, ref acc, ref weightSum);

                if (weightSum > 1e-6f)
                    result[v] = Mathf.Abs(weightSum - 1f) > 1e-5f ? (acc / weightSum) : acc;
                else
                    result[v] = sv;
            }

            return result;
        }

        private static void Accumulate(
            int idx,
            float w,
            Vector3 sv,
            int boneCount,
            bool[] validBone,
            Matrix4x4[] skinMatrices,
            ref Vector3 acc,
            ref float weightSum)
        {
            if (w <= 0f || idx < 0 || idx >= boneCount || !validBone[idx])
                return;
            acc += skinMatrices[idx].MultiplyPoint3x4(sv) * w;
            weightSum += w;
        }

        private static void ApplyMultiFrameBlendShape(
            Mesh mesh,
            int shapeIndex,
            int frameCount,
            float w,
            Vector3[] shaped,
            Vector3[] deltaA,
            ref Vector3[] deltaB)
        {
            int vertCount = shaped.Length;
            float firstW = mesh.GetBlendShapeFrameWeight(shapeIndex, 0);
            if (w <= firstW)
            {
                if (Mathf.Abs(firstW) < 1e-6f) return;
                mesh.GetBlendShapeFrameVertices(shapeIndex, 0, deltaA, null, null);
                float t = w / firstW;
                for (int v = 0; v < vertCount; v++)
                    shaped[v] += deltaA[v] * t;
                return;
            }

            for (int f = 1; f < frameCount; f++)
            {
                float prevW = mesh.GetBlendShapeFrameWeight(shapeIndex, f - 1);
                float currW = mesh.GetBlendShapeFrameWeight(shapeIndex, f);
                if (w <= currW || f == frameCount - 1)
                {
                    if (deltaB == null) deltaB = new Vector3[vertCount];
                    mesh.GetBlendShapeFrameVertices(shapeIndex, f - 1, deltaA, null, null);
                    mesh.GetBlendShapeFrameVertices(shapeIndex, f, deltaB, null, null);
                    float denom = currW - prevW;
                    float t = Mathf.Abs(denom) > 1e-6f ? (w - prevW) / denom : 1f;
                    for (int v = 0; v < vertCount; v++)
                        shaped[v] += Vector3.LerpUnclamped(deltaA[v], deltaB[v], t);
                    return;
                }
            }
        }

        /// <summary>
        /// クロスチェック用 BakeMesh 評価（デバッグ・統合テスト用）。
        /// </summary>
        public static Vector3[] EvaluateWorldVerticesBakeMesh(SkinnedMeshRenderer smr)
        {
            var baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                smr.BakeMesh(baked, useScale: true);
                Vector3[] verts = baked.vertices;
                Matrix4x4 trs = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                for (int i = 0; i < verts.Length; i++)
                    verts[i] = trs.MultiplyPoint3x4(verts[i]);
                return verts;
            }
            finally
            {
                Object.DestroyImmediate(baked);
            }
        }

        /// <summary>
        /// 近位/遠位スキニング行列の線形補間から補助ボーンの Bindpose を逆算する (ADR-07)。
        /// S_H = (1 - α) S_P + α S_D となる M_bind_H = B_H^{-1} * S_H を返す。
        /// </summary>
        public static Matrix4x4 ComputeHelperBindpose(
            Matrix4x4 helperLocalToWorld,
            Matrix4x4 proximalSkinMatrix,
            Matrix4x4 distalSkinMatrix,
            float alpha)
        {
            Matrix4x4 blended = MatrixLerp(proximalSkinMatrix, distalSkinMatrix, alpha);
            return helperLocalToWorld.inverse * blended;
        }

        public static Matrix4x4 MatrixLerp(Matrix4x4 a, Matrix4x4 b, float t)
        {
            float omt = 1.0f - t;
            var m = new Matrix4x4();
            for (int i = 0; i < 16; i++)
                m[i] = a[i] * omt + b[i] * t;
            return m;
        }

        /// <summary>
        /// 変更された頂点における新旧レスト姿勢ワールド座標の最大ズレ (m) を計測する (§4.2 5f)。
        /// </summary>
        public static float MeasureMaxRestPoseDrift(
            Vector3[] oldWorldVerts,
            Vector3[] newWorldVerts,
            bool[] modifiedMask)
        {
            if (oldWorldVerts == null || newWorldVerts == null || modifiedMask == null)
                return 0f;

            int len = Mathf.Min(oldWorldVerts.Length, Mathf.Min(newWorldVerts.Length, modifiedMask.Length));
            float maxSqr = 0f;
            for (int i = 0; i < len; i++)
            {
                if (!modifiedMask[i]) continue;
                float sqr = (newWorldVerts[i] - oldWorldVerts[i]).sqrMagnitude;
                if (sqr > maxSqr) maxSqr = sqr;
            }
            return Mathf.Sqrt(maxSqr);
        }
    }
}
