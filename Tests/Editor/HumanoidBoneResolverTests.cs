using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Dennokoworks.JointKeeper.Editor;

namespace Dennokoworks.JointKeeper.Tests
{
    public class HumanoidBoneResolverTests
    {
        [Test]
        public void ResolveFromHumanDescription_FindsBonesEvenWhenAvatarInactive()
        {
            var root = new GameObject("Avatar");
            var arm = new GameObject("Armature"); arm.transform.SetParent(root.transform, false);
            var hips = new GameObject("Hips"); hips.transform.SetParent(arm.transform, false);
            var upperL = new GameObject("UpperLeg_L"); upperL.transform.SetParent(hips.transform, false);
            var lowerL = new GameObject("LowerLeg_L"); lowerL.transform.SetParent(upperL.transform, false);
            var footL = new GameObject("Foot_L"); footL.transform.SetParent(lowerL.transform, false);

            // 非アクティブ状態でも humanDescription 経由で確実に解決できること
            root.SetActive(false);

            var desc = new HumanDescription
            {
                skeleton = new[]
                {
                    new SkeletonBone { name = "Avatar" },
                    new SkeletonBone { name = "Armature" },
                    new SkeletonBone { name = "Hips" },
                    new SkeletonBone { name = "UpperLeg_L" },
                    new SkeletonBone { name = "LowerLeg_L" },
                    new SkeletonBone { name = "Foot_L" },
                },
                human = new[]
                {
                    new HumanBone { humanName = "Hips", boneName = "Hips" },
                    new HumanBone { humanName = "LeftUpperLeg", boneName = "UpperLeg_L" },
                    new HumanBone { humanName = "LeftLowerLeg", boneName = "LowerLeg_L" },
                    new HumanBone { humanName = "LeftFoot", boneName = "Foot_L" },
                },
            };

            try
            {
                var map = new Dictionary<HumanBodyBones, Transform>();
                HumanoidBoneResolver.ResolveFromHumanDescription(root.transform, desc, map);

                Assert.AreEqual(hips.transform, HumanoidBoneResolver.GetBone(map, HumanBodyBones.Hips));
                Assert.AreEqual(upperL.transform, HumanoidBoneResolver.GetBone(map, HumanBodyBones.LeftUpperLeg));
                Assert.AreEqual(lowerL.transform, HumanoidBoneResolver.GetBone(map, HumanBodyBones.LeftLowerLeg));
                Assert.AreEqual(footL.transform, HumanoidBoneResolver.GetBone(map, HumanBodyBones.LeftFoot));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
