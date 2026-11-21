using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Dennokoworks.JointKeeper;
using Dennokoworks.JointKeeper.Editor;

namespace Dennokoworks.JointKeeper.Tests
{
    public class JointKeeperPreviewPoseTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null)
                    Object.DestroyImmediate(_created[i]);
            }
            _created.Clear();
        }

        [Test]
        public void Blender100xOutfitArmature_PreservesScaleAndFollowsBendWithoutPinching()
        {
            // アバター本体 (Armature scale = 1)
            var avatar = new GameObject("Avatar"); _created.Add(avatar);
            var avArm = new GameObject("Armature"); avArm.transform.SetParent(avatar.transform, false);
            var avChest = new GameObject("Chest"); avChest.transform.SetParent(avArm.transform, false);
            avChest.transform.position = new Vector3(0f, 1.1f, 0f);
            var avUpper = new GameObject("UpperArm_L"); avUpper.transform.SetParent(avChest.transform, false);
            avUpper.transform.position = new Vector3(-0.15f, 1.25f, 0f);
            var avLower = new GameObject("LowerArm_L"); avLower.transform.SetParent(avUpper.transform, false);
            avLower.transform.position = new Vector3(-0.40f, 1.25f, 0f);
            var avHand = new GameObject("Hand_L"); avHand.transform.SetParent(avLower.transform, false);
            avHand.transform.position = new Vector3(-0.65f, 1.25f, 0f);

            // Blender 100x スケール出力の MA 衣装 (Armature scale = 100)
            var outfit = new GameObject("Outfit100x"); outfit.transform.SetParent(avatar.transform, false);
            var outArm = new GameObject("Armature"); outArm.transform.SetParent(outfit.transform, false);
            outArm.transform.localScale = new Vector3(100f, 100f, 100f);

            var outChest = new GameObject("Chest"); outChest.transform.SetParent(outArm.transform, false);
            outChest.transform.position = avChest.transform.position;
            var outUpper = new GameObject("UpperArm_L"); outUpper.transform.SetParent(outChest.transform, false);
            outUpper.transform.position = avUpper.transform.position;
            var outLower = new GameObject("LowerArm_L"); outLower.transform.SetParent(outUpper.transform, false);
            outLower.transform.position = avLower.transform.position;
            var outHand = new GameObject("Hand_L"); outHand.transform.SetParent(outLower.transform, false);
            outHand.transform.position = avHand.transform.position;

            var humanoidBones = new Dictionary<HumanBodyBones, Transform>
            {
                [HumanBodyBones.Chest] = avChest.transform,
                [HumanBodyBones.LeftUpperArm] = avUpper.transform,
                [HumanBodyBones.LeftLowerArm] = avLower.transform,
                [HumanBodyBones.LeftHand] = avHand.transform,
            };

            var aliasMap = new Dictionary<Transform, Transform>
            {
                [outArm.transform] = avArm.transform,
                [outChest.transform] = avChest.transform,
                [outUpper.transform] = avUpper.transform,
                [outLower.transform] = avLower.transform,
                [outHand.transform] = avHand.transform,
            };

            var elbowFrame = JointFrameSolver.Solve(
                JointId.LeftElbow,
                avUpper.transform.position,
                avLower.transform.position,
                avHand.transform.position,
                Vector3.forward,
                Vector3.up);
            var baseFrames = new Dictionary<JointId, JointFrame>
            {
                [JointId.LeftElbow] = elbowFrame,
            };

            using (var pose = new JointKeeperPreviewPose(avatar, humanoidBones, baseFrames, aliasMap))
            {
                var origBones = new[] { outChest.transform, outUpper.transform, outLower.transform, outHand.transform };
                var shadowBones = pose.MapBonesToShadow(origBones);

                // 0°: 100x スケールが完全に維持され、1/100 縮小（メッシュ絞り込み）が起きないこと
                pose.UpdateFrame(0f, 0f);
                Assert.AreEqual(100f, shadowBones[0].lossyScale.x, 1e-3f, "Chest 影ボーンの 100x スケールが保持されること");
                Assert.AreEqual(100f, shadowBones[2].lossyScale.x, 1e-3f, "LowerArm 影ボーンの 100x スケールが保持されること");

                // 90° 曲げ: スケールを維持したまま、衣装側の手首もアバター側と同じく前方 (+Z) へ曲がること
                pose.UpdateFrame(90f, 0f);
                Assert.AreEqual(100f, shadowBones[2].lossyScale.x, 1e-3f, "曲げ後も 100x スケールが保持されること");

                Vector3 bentForearmDir = (shadowBones[3].position - shadowBones[2].position).normalized;
                Assert.Greater(Vector3.Dot(bentForearmDir, Vector3.forward), 0.999f, "衣装側前腕も前方 (+Z) へ 90° 追従して曲がること");
                Assert.Less(Vector3.Distance(shadowBones[2].position, avLower.transform.position), 1e-4f);
            }
        }

        [Test]
        public void OutfitWithRotatedLocalAxesAndGloveOnlyDescendant_PreservesRestOrientationAndFollowsBend()
        {
            var avatar = new GameObject("Avatar"); _created.Add(avatar);
            var avArm = new GameObject("Armature"); avArm.transform.SetParent(avatar.transform, false);
            var avUpper = new GameObject("UpperArm_L"); avUpper.transform.SetParent(avArm.transform, false);
            avUpper.transform.position = new Vector3(-0.15f, 1.25f, 0f);
            var avLower = new GameObject("LowerArm_L"); avLower.transform.SetParent(avUpper.transform, false);
            avLower.transform.position = new Vector3(-0.40f, 1.25f, 0f);
            var avHand = new GameObject("Hand_L"); avHand.transform.SetParent(avLower.transform, false);
            avHand.transform.position = new Vector3(-0.65f, 1.25f, 0f);

            // ローカル回転が異なる手袋衣装（Hand_L のみ mergeTarget 配下にある想定）
            var glove = new GameObject("Glove"); glove.transform.SetParent(avatar.transform, false);
            var gloveArm = new GameObject("Armature"); gloveArm.transform.SetParent(glove.transform, false);
            gloveArm.transform.localScale = new Vector3(100f, 100f, 100f);
            var gloveHand = new GameObject("Hand_L"); gloveHand.transform.SetParent(gloveArm.transform, false);
            gloveHand.transform.position = avHand.transform.position;
            gloveHand.transform.rotation = Quaternion.Euler(30f, 90f, -45f);

            var humanoidBones = new Dictionary<HumanBodyBones, Transform>
            {
                [HumanBodyBones.LeftUpperArm] = avUpper.transform,
                [HumanBodyBones.LeftLowerArm] = avLower.transform,
                [HumanBodyBones.LeftHand] = avHand.transform,
            };

            var aliasMap = new Dictionary<Transform, Transform>
            {
                [gloveHand.transform] = avHand.transform,
            };

            var elbowFrame = JointFrameSolver.Solve(
                JointId.LeftElbow,
                avUpper.transform.position,
                avLower.transform.position,
                avHand.transform.position,
                Vector3.forward,
                Vector3.up);
            var baseFrames = new Dictionary<JointId, JointFrame>
            {
                [JointId.LeftElbow] = elbowFrame,
            };

            using (var pose = new JointKeeperPreviewPose(avatar, humanoidBones, baseFrames, aliasMap))
            {
                var shadowHand = pose.ResolveSmrShadowBone(gloveHand.transform);
                var avShadowHand = pose.ResolveSmrShadowBone(avHand.transform);

                // 0°: 衣装側固有の回転・スケールがそのまま保たれること
                pose.UpdateFrame(0f, 0f);
                Assert.AreEqual(100f, shadowHand.lossyScale.x, 1e-3f);
                Assert.Less(Quaternion.Angle(shadowHand.rotation, gloveHand.transform.rotation), 1e-3f);

                // 90° 曲げ: LowerArm_L を持たない手袋も Hand_L のワールド位置・回転差分に追従すること
                pose.UpdateFrame(90f, 0f);
                Assert.AreEqual(100f, shadowHand.lossyScale.x, 1e-3f);
                Assert.Less(Vector3.Distance(shadowHand.position, avShadowHand.position), 1e-4f);
            }
        }
    }
}
