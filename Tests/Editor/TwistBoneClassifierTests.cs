using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Animations;
using Dennokoworks.JointKeeper;
using Dennokoworks.JointKeeper.Editor;

namespace Dennokoworks.JointKeeper.Tests
{
    public class TwistBoneClassifierTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();

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

        private GameObject CreateGo(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            _created.Add(go);
            if (parent != null) go.transform.SetParent(parent, false);
            return go;
        }

        [Test]
        public void RigidChildWithArbitraryName_AndHandTwist_ClassifiedAsDistal()
        {
            var root = CreateGo("Avatar");
            var upper = CreateGo("UpperArm_L", root.transform);
            var lower = CreateGo("LowerArm_L", upper.transform);
            var hand = CreateGo("Hand_L", lower.transform);
            var cuff = CreateGo("Cuff_Decoration", lower.transform); // 名前ヒント無関係な剛体子ボーン
            var twist = CreateGo("ForearmTwist_L", lower.transform);

            var rotC = twist.AddComponent<RotationConstraint>();
            rotC.AddSource(new ConstraintSource { sourceTransform = hand.transform, weight = 0.5f });
            rotC.rotationAxis = Axis.Y;
            rotC.weight = 1f;
            rotC.constraintActive = true;

            var smrBones = new[] { upper.transform, lower.transform, cuff.transform, twist.transform, hand.transform };
            var ctx = AvatarKinematicContext.Build(root, null);

            var groups = TwistBoneClassifier.Classify(
                JointId.LeftElbow, upper.transform, lower.transform, hand.transform,
                smrBones, new JointConfig(), ctx);

            Assert.IsTrue(groups.ProximalBoneIndices.Contains(0));
            Assert.IsTrue(groups.DistalBoneIndices.Contains(1));
            Assert.IsTrue(groups.DistalBoneIndices.Contains(2), "名前ヒント無関係な剛体子ボーン Cuff は G_D に入る");
            Assert.IsTrue(groups.DistalBoneIndices.Contains(3), "Hand ソースのツイストは G_D に入る");
            Assert.IsFalse(groups.DistalBoneIndices.Contains(4), "Hand 自身は除外");
        }

        [Test]
        public void SerialUpperArmTwist_SetsProximalSourceToUpperTwist()
        {
            var root = CreateGo("Avatar");
            var upper = CreateGo("UpperArm_L", root.transform);
            var upperTwist = CreateGo("UpperArm_Twist_L", upper.transform);
            var lower = CreateGo("LowerArm_L", upperTwist.transform);
            var hand = CreateGo("Hand_L", lower.transform);

            var smrBones = new[] { upper.transform, upperTwist.transform, lower.transform };
            var ctx = AvatarKinematicContext.Build(root, null);

            var groups = TwistBoneClassifier.Classify(
                JointId.LeftElbow, upper.transform, lower.transform, hand.transform,
                smrBones, new JointConfig(), ctx);

            Assert.IsTrue(groups.ProximalBoneIndices.Contains(0));
            Assert.IsTrue(groups.ProximalBoneIndices.Contains(1));
            Assert.AreEqual(upperTwist.transform, groups.ProximalSource);
        }

        [Test]
        public void OneAxisUpperTwistReferencingLower_NotMisclassifiedAsExistingHelper()
        {
            var root = CreateGo("Avatar");
            var upper = CreateGo("UpperArm_L", root.transform);
            var lower = CreateGo("LowerArm_L", upper.transform);
            var hand = CreateGo("Hand_L", lower.transform);
            var upperRoll = CreateGo("UpperArm_Roll_L", upper.transform);

            var rotC = upperRoll.AddComponent<RotationConstraint>();
            rotC.AddSource(new ConstraintSource { sourceTransform = lower.transform, weight = 0.5f });
            rotC.rotationAxis = Axis.X; // 1 軸のみ
            rotC.weight = 1f;
            rotC.constraintActive = true;

            var smrBones = new[] { upper.transform, lower.transform, upperRoll.transform };
            var ctx = AvatarKinematicContext.Build(root, null);

            var groups = TwistBoneClassifier.Classify(
                JointId.LeftElbow, upper.transform, lower.transform, hand.transform,
                smrBones, new JointConfig(), ctx);

            Assert.IsTrue(groups.ProximalBoneIndices.Contains(2));
            Assert.AreEqual(0, groups.ExistingJointHelpers.Count);
        }

        [Test]
        public void ExistingJointHelperWithMultiAxisConstraint_DetectedAndExcluded()
        {
            var root = CreateGo("Avatar");
            var upper = CreateGo("UpperArm_L", root.transform);
            var lower = CreateGo("LowerArm_L", upper.transform);
            var hand = CreateGo("Hand_L", lower.transform);
            var existingHelper = CreateGo("Elbow_Helper_L", upper.transform);

            var parentC = existingHelper.AddComponent<ParentConstraint>();
            parentC.AddSource(new ConstraintSource { sourceTransform = upper.transform, weight = 0.5f });
            parentC.AddSource(new ConstraintSource { sourceTransform = lower.transform, weight = 0.5f });
            parentC.rotationAxis = Axis.X | Axis.Y | Axis.Z;
            parentC.translationAxis = Axis.X | Axis.Y | Axis.Z;
            parentC.weight = 1f;
            parentC.constraintActive = true;

            var smrBones = new[] { upper.transform, lower.transform, existingHelper.transform };
            var ctx = AvatarKinematicContext.Build(root, null);

            var groups = TwistBoneClassifier.Classify(
                JointId.LeftElbow, upper.transform, lower.transform, hand.transform,
                smrBones, new JointConfig(), ctx);

            Assert.IsTrue(groups.ExistingJointHelpers.Contains(existingHelper.transform));
            Assert.IsFalse(groups.ProximalBoneIndices.Contains(2));
            Assert.IsFalse(groups.DistalBoneIndices.Contains(2));
            Assert.Greater(groups.DiagnosticWarnings.Count, 0);
        }

        [Test]
        public void LowerArmAbsentFromSmrBones_DistalRefFallsBackToTwistBone()
        {
            var root = CreateGo("Avatar");
            var upper = CreateGo("UpperArm_L", root.transform);
            var lower = CreateGo("LowerArm_L", upper.transform);
            var hand = CreateGo("Hand_L", lower.transform);
            var twist = CreateGo("LowerArm_Twist_L", lower.transform);

            // LowerArm 自体は smr.bones に含まれない
            var smrBones = new[] { upper.transform, twist.transform };
            var ctx = AvatarKinematicContext.Build(root, null);

            var groups = TwistBoneClassifier.Classify(
                JointId.LeftElbow, upper.transform, lower.transform, hand.transform,
                smrBones, new JointConfig(), ctx);

            Assert.AreEqual(0, groups.ProximalReferenceBoneIndex);
            Assert.AreEqual(1, groups.DistalReferenceBoneIndex);
        }

        [Test]
        public void DormantPoseWrapperConstraint_DoesNotExcludeChildBones_AndPromotesSourcesToWrapper()
        {
            // AvatarPoseSystem + MA ScaleAdjuster + 素体既存膝補助ボーンの組み合わせを再現
            var root = CreateGo("Avatar");
            var worldFix = CreateGo("WorldFix", root.transform);
            var fixUpper = CreateGo("Fix_UpperLeg_L", worldFix.transform);
            var fixLower = CreateGo("Fix_LowerLeg_L", worldFix.transform);

            var upperClone = CreateGo("UpperLeg_L", root.transform);
            var upperConst = CreateGo("UpperLeg_L_Const", upperClone.transform);
            var upperOrig = CreateGo("UpperLeg_L", upperConst.transform);
            var lowerLegKnee = CreateGo("LowerLegKnee_L", upperOrig.transform);
            var upperScaleProxy = CreateGo("ScaleProxy", upperOrig.transform);

            var lowerClone = CreateGo("LowerLeg_L", upperClone.transform);
            var lowerConst = CreateGo("LowerLeg_L_Const", lowerClone.transform);
            var lowerOrig = CreateGo("LowerLeg_L", lowerConst.transform);
            var lowerScaleProxy = CreateGo("ScaleProxy", lowerOrig.transform);

            var footClone = CreateGo("Foot_L", lowerClone.transform);

            // 休止中ポーズラッパー (weight = 0 で外部 WorldFix を参照)
            var pcU = upperConst.AddComponent<ParentConstraint>();
            pcU.AddSource(new ConstraintSource { sourceTransform = fixUpper.transform, weight = 1f });
            pcU.weight = 0f;
            pcU.constraintActive = true;

            var pcL = lowerConst.AddComponent<ParentConstraint>();
            pcL.AddSource(new ConstraintSource { sourceTransform = fixLower.transform, weight = 1f });
            pcL.weight = 0f;
            pcL.constraintActive = true;

            // 素体既存の 3 軸膝補助ボーン (active, weight = 0.5)
            var kneeRc = lowerLegKnee.AddComponent<RotationConstraint>();
            kneeRc.AddSource(new ConstraintSource { sourceTransform = lowerOrig.transform, weight = 1f });
            kneeRc.rotationAxis = Axis.X | Axis.Y | Axis.Z;
            kneeRc.weight = 0.5f;
            kneeRc.constraintActive = true;

            var smrBones = new[] { upperScaleProxy.transform, lowerScaleProxy.transform, lowerLegKnee.transform };
            var ctx = AvatarKinematicContext.Build(root, null);

            var groups = TwistBoneClassifier.Classify(
                JointId.LeftKnee, upperClone.transform, lowerClone.transform, footClone.transform,
                smrBones, new JointConfig(), ctx);

            Assert.IsTrue(groups.ProximalBoneIndices.Contains(0), "休止中ラッパー配下の ScaleProxy が G_P に分類されること");
            Assert.IsTrue(groups.DistalBoneIndices.Contains(1), "休止中ラッパー配下の ScaleProxy が G_D に分類されること");
            Assert.IsTrue(groups.ExistingJointHelpers.Contains(lowerLegKnee.transform), "素体既存膝ボーンは除外対象として検出されること");
            Assert.IsFalse(groups.ProximalBoneIndices.Contains(2));
            Assert.IsFalse(groups.DistalBoneIndices.Contains(2));
            Assert.AreEqual(upperConst.transform, groups.ProximalSource, "ProximalSource がポーズラッパーへ昇格されること");
            Assert.AreEqual(lowerConst.transform, groups.DistalSource, "DistalSource がポーズラッパーへ昇格されること");
        }
    }
}
