using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Dennokoworks.JointKeeper;
using Dennokoworks.JointKeeper.Editor;

namespace Dennokoworks.JointKeeper.Tests
{
    public class JointKeeperBakerTests
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
        public void ResolveOutputDirectory_PrefersOriginalMeshFolderThenSceneThenFallback()
        {
            Assert.AreEqual(
                "Assets/Outfit/FBX",
                JointKeeperBaker.ResolveOutputDirectoryFromPaths("Assets/Outfit/FBX/Denim.fbx", "Assets/Scenes/Main.unity"));

            Assert.AreEqual(
                "Assets/Scenes",
                JointKeeperBaker.ResolveOutputDirectoryFromPaths("Packages/com.example/Mesh.fbx", "Assets/Scenes/Main.unity"));

            Assert.AreEqual(
                "Assets",
                JointKeeperBaker.ResolveOutputDirectoryFromPaths(null, "Assets/data.unity"));

            Assert.AreEqual(
                "Assets/JointKeeper_Baked",
                JointKeeperBaker.ResolveOutputDirectoryFromPaths(null, ""));
        }

        [Test]
        public void SanitizeAssetFileName_StripsSuffixAndInvalidChars()
        {
            Assert.AreEqual("Denim_Wide_Cargo", JointKeeperBaker.SanitizeAssetFileName("Denim:Wide/Cargo_JointKeeper"));
            Assert.AreEqual("Pants", JointKeeperBaker.SanitizeAssetFileName("Pants (JointKeeper)"));
            Assert.AreEqual("BakedMesh", JointKeeperBaker.SanitizeAssetFileName("   "));
        }

        [Test]
        public void ExtraBoneScales_MatchesExplicitScaleProxyTransform()
        {
            var root = new GameObject("Root"); _created.Add(root);
            var bone = new GameObject("UpperLeg_L"); _created.Add(bone);
            bone.transform.SetParent(root.transform, false);
            bone.transform.position = new Vector3(0.1f, 0.8f, 0f);

            var scaleProxy = new GameObject("ScaleProxy"); _created.Add(scaleProxy);
            scaleProxy.transform.SetParent(bone.transform, false);
            scaleProxy.transform.localPosition = Vector3.zero;
            scaleProxy.transform.localRotation = Quaternion.identity;
            scaleProxy.transform.localScale = new Vector3(1.0f, 1.07f, 1.0f);

            var mesh = new Mesh(); _created.Add(mesh);
            mesh.vertices = new[] { new Vector3(0.1f, 0.4f, 0.05f), new Vector3(0.1f, 0.6f, -0.02f) };
            mesh.bindposes = new[] { bone.transform.worldToLocalMatrix };
            mesh.boneWeights = new[]
            {
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
            };

            var expected = SkinningEvaluator.EvaluateWorldVerticesCpu(mesh, new[] { scaleProxy.transform });
            var extraScales = new Dictionary<Transform, Vector3> { [bone.transform] = new Vector3(1.0f, 1.07f, 1.0f) };
            var actual = SkinningEvaluator.EvaluateWorldVerticesCpu(mesh, new[] { bone.transform }, null, null, null, extraScales);

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Less(Vector3.Distance(expected[i], actual[i]), 1e-6f);
            }
        }

        [Test]
        public void ResolveOutfitJointBones_SelectsOutfitArmatureMatchingSmrBones()
        {
            var avatar = new GameObject("Avatar"); _created.Add(avatar);
            var avArm = new GameObject("Armature"); avArm.transform.SetParent(avatar.transform, false);
            var avUpper = new GameObject("UpperLeg_L"); avUpper.transform.SetParent(avArm.transform, false);
            var avLower = new GameObject("LowerLeg_L"); avLower.transform.SetParent(avUpper.transform, false);
            var avFoot = new GameObject("Foot_L"); avFoot.transform.SetParent(avLower.transform, false);

            var outfit = new GameObject("Outfit"); outfit.transform.SetParent(avatar.transform, false);
            var outArm = new GameObject("Armature.1"); outArm.transform.SetParent(outfit.transform, false);
            var outUpper = new GameObject("UpperLeg_L"); outUpper.transform.SetParent(outArm.transform, false);
            var outLower = new GameObject("LowerLeg_L"); outLower.transform.SetParent(outUpper.transform, false);
            var outFoot = new GameObject("Foot_L"); outFoot.transform.SetParent(outLower.transform, false);

            var mapping = new PreviewBoneResolver.MergeArmatureMapping
            {
                OutfitArmatureRoot = outArm.transform,
                AvatarTargetRoot = avArm.transform,
            };
            mapping.OutfitToAvatar[outUpper.transform] = avUpper.transform;
            mapping.OutfitToAvatar[outLower.transform] = avLower.transform;
            mapping.OutfitToAvatar[outFoot.transform] = avFoot.transform;
            mapping.AvatarToOutfit[avUpper.transform] = outUpper.transform;
            mapping.AvatarToOutfit[avLower.transform] = outLower.transform;
            mapping.AvatarToOutfit[avFoot.transform] = outFoot.transform;

            var mappings = new List<PreviewBoneResolver.MergeArmatureMapping> { mapping };

            bool foundOutfit = PreviewBoneResolver.TryResolveOutfitJointBones(
                new[] { outUpper.transform, outLower.transform },
                mappings,
                avUpper.transform, avLower.transform, avFoot.transform,
                out Transform tU, out Transform tL, out Transform tC);

            Assert.IsTrue(foundOutfit);
            Assert.AreEqual(outUpper.transform, tU);
            Assert.AreEqual(outLower.transform, tL);
            Assert.AreEqual(outFoot.transform, tC);

            // アバター本体側の SMR ならアバター側ボーンをそのまま返すこと
            bool foundAvatar = PreviewBoneResolver.TryResolveOutfitJointBones(
                new[] { avUpper.transform, avLower.transform },
                mappings,
                avUpper.transform, avLower.transform, avFoot.transform,
                out Transform aU, out Transform aL, out Transform aC);

            Assert.IsFalse(foundAvatar);
            Assert.AreEqual(avUpper.transform, aU);
            Assert.AreEqual(avLower.transform, aL);
            Assert.AreEqual(avFoot.transform, aC);
        }

        [Test]
        public void BuildState_TwistBaseAndKnots_TrackCreatedAimTargetAndShareAcrossCalls()
        {
            var root = new GameObject("ArmRoot"); _created.Add(root);
            var upper = new GameObject("UpperArm_L"); upper.transform.SetParent(root.transform, false);
            upper.transform.position = new Vector3(-0.15f, 1.25f, 0f);
            var lower = new GameObject("LowerArm_L"); lower.transform.SetParent(upper.transform, false);
            lower.transform.position = new Vector3(-0.40f, 1.25f, 0f);
            var hand = new GameObject("Hand_L"); hand.transform.SetParent(lower.transform, false);
            hand.transform.position = new Vector3(-0.65f, 1.25f, 0f);

            var frame = JointFrameSolver.Solve(
                JointId.LeftElbow, upper.transform.position, lower.transform.position, hand.transform.position,
                Vector3.forward, Vector3.up);
            Assert.IsTrue(ForearmTwistGeometry.TryCompute(
                lower.transform, hand.transform, null, frame.HingeAxis, frame.LimbLength,
                out var geo, out _));

            var state = new JointKeeperBuildState();
            Transform base1 = state.GetOrCreateTwistBase(
                JointId.LeftElbow, geo, upper.transform, lower.transform,
                out Transform aim1, out bool baseCreated1);

            Assert.IsTrue(baseCreated1);
            Assert.IsNotNull(base1);
            Assert.IsNotNull(aim1);
            Assert.AreEqual(lower.transform, base1.parent);
            Assert.AreEqual(lower.transform, aim1.parent);

            Transform base2 = state.GetOrCreateTwistBase(
                JointId.LeftElbow, geo, upper.transform, lower.transform,
                out Transform aim2, out bool baseCreated2);

            Assert.IsFalse(baseCreated2);
            Assert.AreEqual(base1, base2);
            Assert.IsNull(aim2);

            Transform knot1 = state.GetOrCreateTwistKnot(
                JointId.LeftElbow, geo, base1, lower.transform, 0.5f, out bool knotCreated1);
            Transform knot2 = state.GetOrCreateTwistKnot(
                JointId.LeftElbow, geo, base1, lower.transform, 0.5f, out bool knotCreated2);

            Assert.IsTrue(knotCreated1);
            Assert.IsFalse(knotCreated2);
            Assert.AreEqual(knot1, knot2);
            Assert.AreEqual(base1, knot1.parent);
        }
    }
}
