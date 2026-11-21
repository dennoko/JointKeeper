namespace Dennokoworks.JointKeeper
{
    public enum JointId
    {
        LeftElbow = 0,
        RightElbow = 1,
        LeftKnee = 2,
        RightKnee = 3,
    }

    public enum TargetRendererMode
    {
        /// <summary>コンポーネント配下（アバタールート配置時は全衣装）の SMR を自動対象化（除外リスト対応）。</summary>
        AutoDetect = 0,
        /// <summary>targetRenderers リストに明示指定された SMR のみを対象化。</summary>
        ManualList = 1,
    }

    public enum DistalSourceMode
    {
        /// <summary>Humanoid の LowerArm / LowerLeg を自動使用。</summary>
        AutoLowerBone = 0,
        /// <summary>カウンターツイスト等の特殊リグ向けに手動指定した Transform を使用。</summary>
        CustomOverride = 1,
    }

    public static class JointIdExtensions
    {
        public static bool IsElbow(this JointId id)
        {
            return id == JointId.LeftElbow || id == JointId.RightElbow;
        }

        public static bool IsKnee(this JointId id)
        {
            return id == JointId.LeftKnee || id == JointId.RightKnee;
        }

        public static bool IsRight(this JointId id)
        {
            return id == JointId.RightElbow || id == JointId.RightKnee;
        }

        public static UnityEngine.HumanBodyBones GetUpperHumanoidBone(this JointId id)
        {
            switch (id)
            {
                case JointId.LeftElbow: return UnityEngine.HumanBodyBones.LeftUpperArm;
                case JointId.RightElbow: return UnityEngine.HumanBodyBones.RightUpperArm;
                case JointId.LeftKnee: return UnityEngine.HumanBodyBones.LeftUpperLeg;
                case JointId.RightKnee: return UnityEngine.HumanBodyBones.RightUpperLeg;
                default: return UnityEngine.HumanBodyBones.LastBone;
            }
        }

        public static UnityEngine.HumanBodyBones GetLowerHumanoidBone(this JointId id)
        {
            switch (id)
            {
                case JointId.LeftElbow: return UnityEngine.HumanBodyBones.LeftLowerArm;
                case JointId.RightElbow: return UnityEngine.HumanBodyBones.RightLowerArm;
                case JointId.LeftKnee: return UnityEngine.HumanBodyBones.LeftLowerLeg;
                case JointId.RightKnee: return UnityEngine.HumanBodyBones.RightLowerLeg;
                default: return UnityEngine.HumanBodyBones.LastBone;
            }
        }

        public static UnityEngine.HumanBodyBones GetChildHumanoidBone(this JointId id)
        {
            switch (id)
            {
                case JointId.LeftElbow: return UnityEngine.HumanBodyBones.LeftHand;
                case JointId.RightElbow: return UnityEngine.HumanBodyBones.RightHand;
                case JointId.LeftKnee: return UnityEngine.HumanBodyBones.LeftFoot;
                case JointId.RightKnee: return UnityEngine.HumanBodyBones.RightFoot;
                default: return UnityEngine.HumanBodyBones.LastBone;
            }
        }

        /// <summary>遠位ボーンのロール（ひねり）マッスル名 (FEATURE_ForearmTwist §4.6)。</summary>
        public static string GetTwistMuscleName(this JointId id)
        {
            switch (id)
            {
                case JointId.LeftElbow: return "Left Forearm Twist In-Out";
                case JointId.RightElbow: return "Right Forearm Twist In-Out";
                case JointId.LeftKnee: return "Left Lower Leg Twist In-Out";
                case JointId.RightKnee: return "Right Lower Leg Twist In-Out";
                default: return string.Empty;
            }
        }

        public static string GetMuscleName(this JointId id)
        {
            switch (id)
            {
                case JointId.LeftElbow: return "Left Forearm Stretch";
                case JointId.RightElbow: return "Right Forearm Stretch";
                case JointId.LeftKnee: return "Left Lower Leg Stretch";
                case JointId.RightKnee: return "Right Lower Leg Stretch";
                default: return string.Empty;
            }
        }
    }
}
