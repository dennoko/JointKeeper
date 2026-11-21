using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Animations;
using Dennokoworks.JointKeeper;
using Dennokoworks.JointKeeper.Editor;

namespace Dennokoworks.JointKeeper.Tests
{
    /// <summary>
    /// 曲げ専用基準ボーン N（VRCAimConstraint 等価）と中間ボーン T_j（回転ブレンド）の厳密性 (FEATURE_ForearmTwist §4.4, 不変条件 13)。
    /// </summary>
    public class ForearmTwistRigMathTests
    {
        private const float AngleToleranceDeg = 0.05f;

        private struct Rig
        {
            public Vector3 Elbow;
            public Vector3 RollAxis;
            public Vector3 HingeRaw;
            public Quaternion ProximalRest;
            public Quaternion LowerRest;
            public Vector3 AimLocal;
            public Vector3 UpLocal;
            public Vector3 WorldUpInProximal;
            public float Length;
        }

        /// <param name="hingeTiltDeg">ヒンジ軸をロール軸側へ傾ける角度（A ポーズ・キャリングアングル相当）。</param>
        private static Rig CreateRig(float hingeTiltDeg)
        {
            var r = new Rig
            {
                Elbow = new Vector3(-0.45f, 1.2f, 0f),
                RollAxis = Vector3.left,
                // ボーンのローカル軸が任意でも結果が変わらないことを見るため、非自明なレスト回転にする
                ProximalRest = Quaternion.Euler(12f, -35f, 80f),
                LowerRest = Quaternion.Euler(-20f, 50f, 170f),
                Length = 0.25f,
            };
            r.HingeRaw = (Quaternion.AngleAxis(hingeTiltDeg, Vector3.forward) * Vector3.up).normalized;
            Vector3 perp = (r.HingeRaw - Vector3.Dot(r.HingeRaw, r.RollAxis) * r.RollAxis).normalized;
            r.AimLocal = Quaternion.Inverse(r.LowerRest) * r.RollAxis;
            r.UpLocal = Quaternion.Inverse(r.LowerRest) * perp;
            r.WorldUpInProximal = Quaternion.Inverse(r.ProximalRest) * r.HingeRaw;
            return r;
        }

        /// <summary>上腕は固定、前腕を生のヒンジ軸まわりに曲げ、曲げ後の前腕軸まわりにロールさせた LowerArm 回転。</summary>
        private static Quaternion PoseLower(in Rig r, float bendDeg, float rollDeg, out Vector3 currentRollAxis)
        {
            Quaternion bend = Quaternion.AngleAxis(bendDeg, r.HingeRaw);
            currentRollAxis = bend * r.RollAxis;
            Quaternion roll = Quaternion.AngleAxis(rollDeg, currentRollAxis);
            return roll * bend * r.LowerRest;
        }

        private static Quaternion EvaluateBase(in Rig r, Quaternion lowerRot)
        {
            // Aim ターゲットは LowerArm の子でロール軸上にある
            Vector3 target = r.Elbow + (lowerRot * r.AimLocal) * r.Length;
            Vector3 worldUp = r.ProximalRest * r.WorldUpInProximal;
            Assert.IsTrue(RigMath.EvaluateAimConstraint(r.Elbow, target, r.AimLocal, r.UpLocal, worldUp, out Quaternion q));
            return q;
        }

        [TestCase(0f)]
        [TestCase(5f)]
        public void Rest_BaseEqualsLowerRest(float tilt)
        {
            var r = CreateRig(tilt);
            Quaternion n = EvaluateBase(r, r.LowerRest);
            Assert.Less(Quaternion.Angle(n, r.LowerRest), AngleToleranceDeg);
        }

        [TestCase(0f, 30f)]
        [TestCase(0f, 90f)]
        [TestCase(0f, 150f)]
        [TestCase(5f, 90f)]
        [TestCase(5f, 150f)]
        public void PureBend_BaseFollowsLowerExactly_NoFalseRoll(float tilt, float bend)
        {
            var r = CreateRig(tilt);
            Quaternion lower = PoseLower(r, bend, 0f, out _);
            Quaternion n = EvaluateBase(r, lower);
            Assert.Less(Quaternion.Angle(n, lower), AngleToleranceDeg,
                "生のヒンジ軸を WorldUpVector に使えば、ĥ が â と直交していなくても純粋曲げ時の偽ロールは 0");
        }

        [TestCase(0f, -90f)]
        [TestCase(0f, 60f)]
        [TestCase(90f, 90f)]
        [TestCase(120f, -60f)]
        public void Roll_IsRemovedFromBase(float bend, float roll)
        {
            var r = CreateRig(5f);
            Quaternion lower = PoseLower(r, bend, roll, out _);
            Quaternion bendOnly = PoseLower(r, bend, 0f, out _);
            Quaternion n = EvaluateBase(r, lower);
            Assert.Less(Quaternion.Angle(n, bendOnly), AngleToleranceDeg, "N は LowerArm からロール成分だけを除いた姿勢");
        }

        [TestCase(1f / 3f, 90f, 90f)]
        [TestCase(2f / 3f, 120f, -75f)]
        [TestCase(0.5f, 45f, 60f)]
        public void Knot_HasSameBendAsBase_AndFractionalRoll(float k, float bend, float roll)
        {
            var r = CreateRig(5f);
            Quaternion lower = PoseLower(r, bend, roll, out Vector3 axisNow);
            Quaternion n = EvaluateBase(r, lower);
            Quaternion t = RigMath.EvaluateTwistKnotRotation(n, lower, k);

            Quaternion expected = Quaternion.AngleAxis(k * roll, axisNow) * PoseLower(r, bend, 0f, out _);
            Assert.Less(Quaternion.Angle(t, expected), AngleToleranceDeg, "T_j = Δ_N · R(â, kφ) · T_0");
        }

        [Test]
        public void Aim_DegenerateUp_ReturnsFalse()
        {
            Assert.IsFalse(RigMath.EvaluateAimConstraint(
                Vector3.zero, Vector3.left, Vector3.up, Vector3.up, Vector3.left, out _));
        }

        [Test]
        public void TwistCurve_IsMonotonicAndBounded()
        {
            foreach (TwistCurve c in new[] { TwistCurve.Linear, TwistCurve.Power, TwistCurve.SmoothStep })
            {
                float prev = -1f;
                for (int i = 0; i <= 10; i++)
                {
                    float v = RigMath.EvaluateTwistCurve(c, 2f, i / 10f);
                    Assert.GreaterOrEqual(v, prev);
                    Assert.GreaterOrEqual(v, 0f);
                    Assert.LessOrEqual(v, 1f);
                    prev = v;
                }
                Assert.AreEqual(0f, RigMath.EvaluateTwistCurve(c, 2f, 0f), 1e-6f);
                Assert.AreEqual(1f, RigMath.EvaluateTwistCurve(c, 2f, 1f), 1e-6f);
            }
        }
    }

    /// <summary>
    /// 分類器の剛体遠位 / Constraint 駆動遠位の切り分け、ジオメトリ算出、二重適用判定 (FEATURE_ForearmTwist §1.5, §5.1, §8-B2)。
    /// </summary>
    public class ForearmTwistClassificationTests
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

        private GameObject CreateGo(string name, Transform parent = null, Vector3? localPos = null)
        {
            var go = new GameObject(name);
            _created.Add(go);
            if (parent != null) go.transform.SetParent(parent, false);
            if (localPos.HasValue) go.transform.localPosition = localPos.Value;
            return go;
        }

        [Test]
        public void RigidChildren_AreRigidDistal_ConstraintTwist_IsConstraintDriven()
        {
            var root = CreateGo("Avatar");
            var upper = CreateGo("UpperArm_L", root.transform);
            var lower = CreateGo("LowerArm_L", upper.transform, new Vector3(-0.25f, 0f, 0f));
            var hand = CreateGo("Hand_L", lower.transform, new Vector3(-0.25f, 0f, 0f));
            var cuff = CreateGo("Cuff", lower.transform);
            var twist = CreateGo("LowerArm_Twist_L", lower.transform);

            var rotC = twist.AddComponent<RotationConstraint>();
            rotC.AddSource(new ConstraintSource { sourceTransform = hand.transform, weight = 0.5f });
            rotC.rotationAxis = Axis.X;
            rotC.weight = 1f;
            rotC.constraintActive = true;

            var smrBones = new[] { upper.transform, lower.transform, cuff.transform, twist.transform };
            var ctx = AvatarKinematicContext.Build(root, null);
            var groups = TwistBoneClassifier.Classify(
                JointId.LeftElbow, upper.transform, lower.transform, hand.transform,
                smrBones, new JointConfig(), ctx);

            Assert.IsTrue(groups.DistalRigidBoneIndices.Contains(1), "LowerArm は剛体遠位");
            Assert.IsTrue(groups.DistalRigidBoneIndices.Contains(2), "剛体子 Cuff は剛体遠位");
            Assert.IsFalse(groups.DistalRigidBoneIndices.Contains(3));
            Assert.IsTrue(groups.ConstraintDrivenDistalBoneIndices.Contains(3), "Hand ソースのツイストは Constraint 駆動遠位");
        }

        [Test]
        public void ExistingTwistRig_IsDetectedOnlyWhenWeighted()
        {
            var root = CreateGo("Avatar");
            var upper = CreateGo("UpperArm_L", root.transform);
            var lower = CreateGo("LowerArm_L", upper.transform, new Vector3(-0.25f, 0f, 0f));
            var hand = CreateGo("Hand_L", lower.transform, new Vector3(-0.25f, 0f, 0f));
            var twist = CreateGo("LowerArm_Twist_L", lower.transform);
            var rotC = twist.AddComponent<RotationConstraint>();
            rotC.AddSource(new ConstraintSource { sourceTransform = hand.transform, weight = 0.5f });
            rotC.rotationAxis = Axis.X;
            rotC.weight = 1f;
            rotC.constraintActive = true;

            var smrBones = new[] { upper.transform, lower.transform, twist.transform, hand.transform };
            var ctx = AvatarKinematicContext.Build(root, null);
            var groups = TwistBoneClassifier.Classify(
                JointId.LeftElbow, upper.transform, lower.transform, hand.transform,
                smrBones, new JointConfig(), ctx);

            // 素体相当: ツイストボーンにウェイトあり → 検出
            var bodyWeights = new[]
            {
                new BoneWeight { boneIndex0 = 1, weight0 = 0.5f, boneIndex1 = 2, weight1 = 0.5f },
                new BoneWeight { boneIndex0 = 3, weight0 = 1f },
            };
            var bodyDiag = ForearmTwistProcessor.Analyze(bodyWeights, smrBones, groups, ctx, null);
            Assert.IsTrue(bodyDiag.HasExistingTwistRig);
            Assert.AreEqual("LowerArm_Twist_L", bodyDiag.ExistingRigBoneName);
            Assert.AreEqual(1, bodyDiag.HandWeightVertexCount);

            // 他アバター向け衣装相当: LowerArm のみ → 検出しない（誤スキップしない）
            var outfitWeights = new[] { new BoneWeight { boneIndex0 = 1, weight0 = 0.7f, boneIndex1 = 0, weight1 = 0.3f } };
            var outfitDiag = ForearmTwistProcessor.Analyze(outfitWeights, smrBones, groups, ctx, null);
            Assert.IsFalse(outfitDiag.HasExistingTwistRig);
            Assert.AreEqual(0, outfitDiag.HandWeightVertexCount);
            Assert.AreEqual(1, outfitDiag.RigidVertexCount);
        }

        [Test]
        public void Geometry_UsesHandDirection_AndRejectsParallelHinge()
        {
            var root = CreateGo("Avatar");
            var lower = CreateGo("LowerArm_L", root.transform, new Vector3(-0.45f, 1.2f, 0f));
            var hand = CreateGo("Hand_L", lower.transform, new Vector3(-0.25f, 0f, 0f));

            Assert.IsTrue(ForearmTwistGeometry.TryCompute(
                lower.transform, hand.transform, null, Vector3.up, 0.2f, out var geo, out _));
            Assert.Less(Vector3.Angle(geo.RollAxis, Vector3.left), 1e-3f);
            Assert.AreEqual(0.25f, geo.ForearmLength, 1e-5f);
            Assert.IsFalse(geo.RollAxisMeasured);

            Assert.IsFalse(ForearmTwistGeometry.TryCompute(
                lower.transform, hand.transform, null, Vector3.left, 0.2f, out _, out string error));
            Assert.IsNotNull(error);
        }

        [Test]
        public void GeneratedNames_DetectPerJointAndPerFeature()
        {
            var root = CreateGo("Avatar");
            var helper = CreateGo(JointKeeperGeneratedNames.HelperBaseName(JointId.LeftElbow), root.transform);
            var knot = CreateGo(JointKeeperGeneratedNames.TwistKnotName(JointId.RightElbow, 1), root.transform);
            var bones = new[] { root.transform, helper.transform, knot.transform };

            Assert.IsTrue(JointKeeperGeneratedNames.ReferencesHelperBone(bones, JointId.LeftElbow));
            Assert.IsFalse(JointKeeperGeneratedNames.ReferencesTwistBone(bones, JointId.LeftElbow), "肘補助ボーンがあっても左前腕ツイストは妨げない");
            Assert.IsFalse(JointKeeperGeneratedNames.ReferencesHelperBone(bones, JointId.LeftKnee), "膝は妨げない");
            Assert.IsTrue(JointKeeperGeneratedNames.ReferencesTwistBone(bones, JointId.RightElbow));
            Assert.IsFalse(JointKeeperGeneratedNames.ReferencesHelperBone(bones, JointId.RightElbow));
        }

        [Test]
        public void ResolveTwistConfig_LinksRightToLeft_AndKneesAreDisabled()
        {
            var go = CreateGo("Comp");
            var comp = go.AddComponent<JointKeeperComponent>();
            comp.leftForearmTwist.enabled = true;
            comp.leftForearmTwist.twistFollow = 0.6f;
            comp.rightForearmTwist.enabled = false;

            Assert.IsTrue(comp.ResolveTwistConfig(JointId.RightElbow).enabled);
            Assert.AreEqual(0.6f, comp.ResolveTwistConfig(JointId.RightElbow).twistFollow, 1e-6f);
            Assert.IsFalse(comp.ResolveTwistConfig(JointId.LeftKnee).enabled);

            comp.unlinkElbowLeftRight = true;
            Assert.IsFalse(comp.ResolveTwistConfig(JointId.RightElbow).enabled);
        }
    }
}
