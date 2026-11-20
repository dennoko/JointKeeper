using UnityEngine;
using Dennokoworks.JointKeeper;
#if JOINT_KEEPER_VRCSDK
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;
#endif

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// 補助ボーン GameObject および VRCParentConstraint を生成する統一ビルダー (ADR-01, §5.4)。
    /// </summary>
    internal static class VrcConstraintRigBuilder
    {
        public static Transform CreateHelperBone(
            JointId jointId,
            JointFrame frame,
            Transform proximalSource,
            Transform distalSource,
            float bendFollowRatio,
            float pushAt90Deg,
            int variantIndex)
        {
            string baseName = variantIndex <= 1
                ? $"JK_Helper_{jointId}"
                : $"JK_Helper_{jointId}_{variantIndex}";
            string uniqueName = MakeUniqueChildName(proximalSource, baseName);

            var helperGo = new GameObject(uniqueName);
            Transform helper = helperGo.transform;
            helper.SetParent(proximalSource, worldPositionStays: false);
            helper.position = frame.JointCenter;
            helper.rotation = frame.FrameRotation;
            helper.localScale = Vector3.one;

            RigMath.ComputeSymmetricAnchors(
                frame.JointCenter,
                frame.LimbAxis,
                bendFollowRatio,
                pushAt90Deg,
                out Vector3 anchorAWorld,
                out Vector3 anchorBWorld,
                out float w0,
                out float w1);

            RigMath.ComputeSourceOffsets(
                proximalSource.worldToLocalMatrix,
                proximalSource.rotation,
                anchorAWorld,
                helper.rotation,
                out Vector3 posOffset0,
                out Vector3 rotOffset0);

            RigMath.ComputeSourceOffsets(
                distalSource.worldToLocalMatrix,
                distalSource.rotation,
                anchorBWorld,
                helper.rotation,
                out Vector3 posOffset1,
                out Vector3 rotOffset1);

#if JOINT_KEEPER_VRCSDK
            var constraint = helperGo.AddComponent<VRCParentConstraint>();
            constraint.GlobalWeight = 1.0f;
            constraint.SolveInLocalSpace = false;
            constraint.PositionAtRest = helper.localPosition;
            constraint.RotationAtRest = helper.localRotation.eulerAngles;

            // ADR-01 (rev.2): 位置・回転とも常に全軸有効（pivotOffset != 0 時の連続性保証）
            constraint.AffectsPositionX = true;
            constraint.AffectsPositionY = true;
            constraint.AffectsPositionZ = true;
            constraint.AffectsRotationX = true;
            constraint.AffectsRotationY = true;
            constraint.AffectsRotationZ = true;

            var src0 = new VRCConstraintSource
            {
                SourceTransform = proximalSource,
                Weight = w0,
                ParentPositionOffset = posOffset0,
                ParentRotationOffset = rotOffset0,
            };
            var src1 = new VRCConstraintSource
            {
                SourceTransform = distalSource,
                Weight = w1,
                ParentPositionOffset = posOffset1,
                ParentRotationOffset = rotOffset1,
            };

            constraint.Sources.Add(src0);
            constraint.Sources.Add(src1);
            constraint.Locked = true;
            constraint.IsActive = true;
            constraint.ApplyConfigurationChanges();
#else
            Debug.LogWarning("[JointKeeper] VRChat SDK 3.7.0+ が見つからないため VRCParentConstraint を設定できませんでした。", helperGo);
#endif

            return helper;
        }

        private static string MakeUniqueChildName(Transform parent, string desiredName)
        {
            if (parent == null || parent.Find(desiredName) == null)
                return desiredName;

            int suffix = 2;
            while (parent.Find($"{desiredName}_{suffix}") != null)
                suffix++;
            return $"{desiredName}_{suffix}";
        }
    }
}
