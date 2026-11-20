using UnityEditor;
using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// Phase 2 (Step 3): Scene ビュー上の関節フレームギズモ・楕円体 ROI・
    /// ピボットおよび外側方向オフセットのインタラクティブハンドル (§7.2)。
    /// </summary>
    internal static class JointKeeperSceneHandles
    {
        private static readonly Color OuterArcColor = new Color(0.25f, 0.85f, 1.0f, 0.85f);
        private static readonly Color InnerArcColor = new Color(0.45f, 0.55f, 0.65f, 0.30f);
        private static readonly Color FalloffColor = new Color(0.35f, 1.0f, 0.55f, 0.50f);
        private static readonly Color OutwardArrowColor = new Color(1.0f, 0.55f, 0.15f, 0.95f);
        private static readonly Color HingeLineColor = new Color(0.95f, 0.30f, 0.45f, 0.75f);

        private static readonly JointId[] AllJoints =
        {
            JointId.LeftElbow,
            JointId.RightElbow,
            JointId.LeftKnee,
            JointId.RightKnee,
        };

        public static void DrawSceneGUI(JointKeeperComponent comp, Animator animator, Transform avatarRoot, bool showHandles)
        {
            if (comp == null || animator == null || !animator.isHuman)
                return;

            Vector3 avatarForward = JointFrameSolver.ComputeAvatarForward(animator, avatarRoot, out _);
            Vector3 avatarUp = avatarRoot != null ? avatarRoot.up : Vector3.up;

            foreach (var jointId in AllJoints)
            {
                JointConfig resolved = comp.ResolveConfig(jointId);
                if (!resolved.enabled) continue;

                Transform upper = animator.GetBoneTransform(jointId.GetUpperHumanoidBone());
                Transform lower = animator.GetBoneTransform(jointId.GetLowerHumanoidBone());
                Transform child = animator.GetBoneTransform(jointId.GetChildHumanoidBone());
                if (upper == null || lower == null) continue;

                Vector3? childPos = child != null ? child.position : (Vector3?)null;
                JointFrame baseFrame = JointFrameSolver.Solve(
                    jointId, upper.position, lower.position, childPos, avatarForward, avatarUp);
                JointFrame frame = baseFrame.WithOffsets(resolved.pivotOffset, resolved.outwardAngleOffset);

                DrawJointGizmos(frame, resolved);

                // 左右リンク中は左側のみ編集ハンドルを出し、右側は自動ミラー表示とする
                bool isEditableSide = jointId.IsElbow()
                    ? (comp.unlinkElbowLeftRight || jointId == JointId.LeftElbow)
                    : (comp.unlinkKneeLeftRight || jointId == JointId.LeftKnee);

                if (showHandles && isEditableSide)
                {
                    DrawInteractiveHandles(comp, jointId, baseFrame, frame);
                }
            }
        }

        private static void DrawJointGizmos(in JointFrame frame, JointConfig cfg)
        {
            float axialR = Mathf.Max(cfg.axialRadiusScale * frame.LimbLength, 1e-4f);
            float radialR = Mathf.Max(cfg.radialRadiusScale * frame.LimbLength, 1e-4f);
            float innerRatio = Mathf.Clamp(cfg.innerFalloffRatio, 0f, 0.95f);

            // 1. 関節中心マーカー
            Handles.color = OutwardArrowColor;
            float dotSize = HandleUtility.GetHandleSize(frame.JointCenter) * 0.045f;
            Handles.SphereHandleCap(0, frame.JointCenter, Quaternion.identity, dotSize, EventType.Repaint);

            // 2. 外側ベクトル矢印（肘頭 / 膝蓋骨方向）
            float arrowLen = Mathf.Max(radialR * 0.9f, 0.03f);
            Handles.DrawLine(frame.JointCenter, frame.JointCenter + frame.Outward * arrowLen);
            Handles.ConeHandleCap(
                0,
                frame.JointCenter + frame.Outward * arrowLen,
                Quaternion.LookRotation(frame.Outward, frame.LimbAxis),
                dotSize * 1.4f,
                EventType.Repaint);

            // 3. ヒンジ軸ライン
            Handles.color = HingeLineColor;
            float hingeLen = Mathf.Max(radialR * 0.7f, 0.025f);
            Handles.DrawLine(
                frame.JointCenter - frame.HingeAxis * hingeLen,
                frame.JointCenter + frame.HingeAxis * hingeLen);

            // 4. 楕円体 ROI ワイヤーフレーム（赤道面：外側/内側で色分け）
            DrawDirectionalEquatorialCircle(frame, radialR, cfg.innerCutoff);

            // 5. 楕円体 ROI（軸方向 2 断面）
            Handles.color = OuterArcColor;
            DrawEllipse(frame.JointCenter, frame.LimbAxis * axialR, frame.Outward * radialR, 32);
            Handles.color = InnerArcColor;
            DrawEllipse(frame.JointCenter, frame.LimbAxis * axialR, frame.HingeAxis * radialR, 32);

            // 6. 減衰開始境界（InnerFalloffRatio）
            if (innerRatio > 0.05f)
            {
                Handles.color = FalloffColor;
                DrawEllipse(frame.JointCenter, frame.Outward * (radialR * innerRatio), frame.HingeAxis * (radialR * innerRatio), 24);
            }
        }

        private static void DrawDirectionalEquatorialCircle(in JointFrame frame, float radius, float innerCutoff)
        {
            const int segments = 36;
            Vector3 prev = frame.JointCenter + frame.Outward * radius;
            for (int i = 1; i <= segments; i++)
            {
                float rad = (i / (float)segments) * Mathf.PI * 2f;
                Vector3 dir = frame.Outward * Mathf.Cos(rad) + frame.HingeAxis * Mathf.Sin(rad);
                Vector3 next = frame.JointCenter + dir * radius;
                float c = Vector3.Dot(dir, frame.Outward);
                Handles.color = c >= innerCutoff ? OuterArcColor : InnerArcColor;
                Handles.DrawLine(prev, next);
                prev = next;
            }
        }

        private static void DrawEllipse(Vector3 center, Vector3 axisA, Vector3 axisB, int segments)
        {
            Vector3 prev = center + axisA;
            for (int i = 1; i <= segments; i++)
            {
                float rad = (i / (float)segments) * Mathf.PI * 2f;
                Vector3 next = center + axisA * Mathf.Cos(rad) + axisB * Mathf.Sin(rad);
                Handles.DrawLine(prev, next);
                prev = next;
            }
        }

        private static void DrawInteractiveHandles(
            JointKeeperComponent comp,
            JointId jointId,
            in JointFrame baseFrame,
            in JointFrame currentFrame)
        {
            JointConfig targetCfg = GetDirectConfig(comp, jointId);
            if (targetCfg == null) return;

            // ピボット位置ハンドル
            EditorGUI.BeginChangeCheck();
            Vector3 newWorldPos = Handles.PositionHandle(currentFrame.JointCenter, currentFrame.FrameRotation);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(comp, "Move JointKeeper Pivot Offset");
                Vector3 delta = newWorldPos - baseFrame.RawJointCenter;
                // (x = Axial, y = Outward, z = Hinge)
                targetCfg.pivotOffset = new Vector3(
                    Vector3.Dot(delta, currentFrame.LimbAxis),
                    Vector3.Dot(delta, currentFrame.Outward),
                    Vector3.Dot(delta, currentFrame.HingeAxis));
                EditorUtility.SetDirty(comp);
            }

            // 外側方向回転ディスク（LimbAxis 回り）
            EditorGUI.BeginChangeCheck();
            float discRadius = HandleUtility.GetHandleSize(currentFrame.JointCenter) * 0.45f;
            Handles.color = OutwardArrowColor;
            Quaternion newRot = Handles.Disc(
                currentFrame.FrameRotation,
                currentFrame.JointCenter,
                currentFrame.LimbAxis,
                discRadius,
                cutoffPlane: false,
                snap: 1f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(comp, "Rotate JointKeeper Outward Angle");
                Vector3 newOutward = newRot * Vector3.forward;
                float signedAngle = Vector3.SignedAngle(baseFrame.Outward, newOutward, baseFrame.LimbAxis);
                targetCfg.outwardAngleOffset = Mathf.Clamp(signedAngle, -90f, 90f);
                EditorUtility.SetDirty(comp);
            }
        }

        private static JointConfig GetDirectConfig(JointKeeperComponent comp, JointId jointId)
        {
            switch (jointId)
            {
                case JointId.LeftElbow: return comp.leftElbow;
                case JointId.RightElbow: return comp.rightElbow;
                case JointId.LeftKnee: return comp.leftKnee;
                case JointId.RightKnee: return comp.rightKnee;
                default: return null;
            }
        }
    }
}
