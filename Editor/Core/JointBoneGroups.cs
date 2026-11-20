using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.JointKeeper.Editor
{
    internal enum BoneGroupKind
    {
        Other = 0,
        Proximal = 1,
        Distal = 2,
        ExistingHelper = 3,
    }

    internal readonly struct ClassifiedBoneInfo
    {
        public readonly Transform Bone;
        public readonly BoneGroupKind Group;
        public readonly bool IsConstraintDriven;
        public readonly string Reason;

        public ClassifiedBoneInfo(Transform bone, BoneGroupKind group, bool isConstraintDriven, string reason)
        {
            Bone = bone;
            Group = group;
            IsConstraintDriven = isConstraintDriven;
            Reason = reason;
        }
    }

    /// <summary>
    /// 1 関節分のボーン分類結果および Constraint ソース・基準 Bindpose 情報 (§5.1)。
    /// </summary>
    internal sealed class JointBoneGroups
    {
        public Transform UpperHumanoid;
        public Transform LowerHumanoid;
        public Transform ChildHumanoid;
        public Transform ProximalSource;
        public Transform DistalSource;
        public bool ProximalSourceIsConstraintDriven;
        public bool DistalSourceIsConstraintDriven;

        public readonly HashSet<int> ProximalBoneIndices = new HashSet<int>();
        public readonly HashSet<int> DistalBoneIndices = new HashSet<int>();
        public readonly HashSet<int> PrimaryHumanoidBoneIndices = new HashSet<int>();

        public int ProximalReferenceBoneIndex = -1;
        public int DistalReferenceBoneIndex = -1;

        public readonly HashSet<Transform> ExistingJointHelpers = new HashSet<Transform>();
        public readonly List<ClassifiedBoneInfo> ClassifiedBones = new List<ClassifiedBoneInfo>();
        public readonly List<string> DiagnosticWarnings = new List<string>();
    }
}
