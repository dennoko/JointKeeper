using NUnit.Framework;
using UnityEngine;
using Dennokoworks.JointKeeper.Editor;

namespace Dennokoworks.JointKeeper.Tests
{
    public class SkinningEvaluatorTests
    {
        [Test]
        public void BlendedBindpose_ZeroDriftEvenWhenDistalRotatedFromBindpose()
        {
            // Upper は bind pose のまま (S_P = I)、Lower は 10° 回転済み (S_D = R(10°))
            Matrix4x4 sp = Matrix4x4.identity;
            Matrix4x4 sd = Matrix4x4.Rotate(Quaternion.Euler(10f, 0f, 0f));
            Matrix4x4 helperWorld = Matrix4x4.TRS(new Vector3(0f, 1f, 0f), Quaternion.Euler(5f, 0f, 0f), Vector3.one);
            float alpha = 0.5f;

            Matrix4x4 helperBind = SkinningEvaluator.ComputeHelperBindpose(helperWorld, sp, sd, alpha);
            Matrix4x4 sh = helperWorld * helperBind;

            // 50/50 頂点が Helper 100% になったときの位置差が 0 であること (ADR-07)
            Vector3 v = new Vector3(0.02f, 0.95f, 0.05f);
            Vector3 origPos = 0.5f * sp.MultiplyPoint3x4(v) + 0.5f * sd.MultiplyPoint3x4(v);
            Vector3 newPos = sh.MultiplyPoint3x4(v);

            Assert.Less((newPos - origPos).magnitude, 1e-5f);
        }

        [Test]
        public void CpuEvaluation_MatchesBakeMesh_WithBlendShapeAndScale()
        {
            var root = new GameObject("Root");
            var boneA = new GameObject("BoneA");
            boneA.transform.SetParent(root.transform, false);
            boneA.transform.position = new Vector3(0.3f, 0.5f, -0.2f);
            boneA.transform.rotation = Quaternion.Euler(15f, 30f, -10f);
            boneA.transform.localScale = new Vector3(1.1f, 0.95f, 1.05f);

            var smrGo = new GameObject("Smr");
            smrGo.transform.SetParent(root.transform, false);
            smrGo.transform.position = new Vector3(1f, 2f, 3f);
            smrGo.transform.rotation = Quaternion.Euler(45f, 0f, 0f);

            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(0.1f, 0.2f, 0.05f),
                new Vector3(-0.05f, 0.1f, 0.15f),
            };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.AddBlendShapeFrame("Inflate", 100f, new[]
            {
                new Vector3(0.01f, 0f, 0f),
                new Vector3(0f, 0.02f, 0f),
                new Vector3(0f, 0f, 0.03f),
            }, null, null);
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.boneWeights = new[]
            {
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
            };

            var smr = smrGo.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = new[] { boneA.transform };
            smr.SetBlendShapeWeight(0, 50f);

            try
            {
                Vector3[] cpuVerts = SkinningEvaluator.EvaluateWorldVerticesCpu(smr);
                Vector3[] bakeVerts = SkinningEvaluator.EvaluateWorldVerticesBakeMesh(smr);

                Assert.AreEqual(bakeVerts.Length, cpuVerts.Length);
                for (int i = 0; i < cpuVerts.Length; i++)
                {
                    Assert.Less((cpuVerts[i] - bakeVerts[i]).magnitude, 1e-4f);
                }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(root);
            }
        }
    }
}
