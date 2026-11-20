using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// 段階的開示（Progressive Disclosure）を採用した JointKeeperComponent のカスタム Inspector (§7.1)。
    /// 初期表示は「対象サマリー・部位トグル・強度/押し出し」のみとし、詳細項目は折りたたみ内に格納する。
    /// </summary>
    [CustomEditor(typeof(JointKeeperComponent))]
    internal sealed class JointKeeperEditor : UnityEditor.Editor
    {
        private bool _showAdvanced;
        private bool _showElbowAdvanced;
        private bool _showKneeAdvanced;
        private bool _showTwistDiagnostics;
        private bool _showSceneHandles;

        private SerializedProperty _targetMode;
        private SerializedProperty _targetRenderers;
        private SerializedProperty _excludedRenderers;
        private SerializedProperty _unlinkElbow;
        private SerializedProperty _leftElbow;
        private SerializedProperty _rightElbow;
        private SerializedProperty _unlinkKnee;
        private SerializedProperty _leftKnee;
        private SerializedProperty _rightKnee;

        private void OnEnable()
        {
            _targetMode = serializedObject.FindProperty("targetMode");
            _targetRenderers = serializedObject.FindProperty("targetRenderers");
            _excludedRenderers = serializedObject.FindProperty("excludedRenderers");
            _unlinkElbow = serializedObject.FindProperty("unlinkElbowLeftRight");
            _leftElbow = serializedObject.FindProperty("leftElbow");
            _rightElbow = serializedObject.FindProperty("rightElbow");
            _unlinkKnee = serializedObject.FindProperty("unlinkKneeLeftRight");
            _leftKnee = serializedObject.FindProperty("leftKnee");
            _rightKnee = serializedObject.FindProperty("rightKnee");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var comp = (JointKeeperComponent)target;
            Animator avatarAnimator = FindAvatarAnimator(comp.transform);
            Transform avatarRoot = avatarAnimator != null ? avatarAnimator.transform : comp.transform.root;

            DrawStatusAndPlacementGuides(comp, avatarAnimator, avatarRoot);

            EditorGUILayout.Space(4f);
            DrawSimpleJointControls("肘 (Elbows)", _leftElbow, _rightElbow, _unlinkElbow);
            EditorGUILayout.Space(2f);
            DrawSimpleJointControls("膝 (Knees)", _leftKnee, _rightKnee, _unlinkKnee);
            EditorGUILayout.Space(4f);
            DrawPreviewControls(avatarRoot);

            EditorGUILayout.Space(6f);
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "詳細設定 (Advanced)", true, EditorStyles.foldoutHeader);
            if (_showAdvanced)
            {
                EditorGUI.indentLevel++;
                DrawAdvancedTargetSettings();
                EditorGUILayout.Space(4f);
                DrawAdvancedJointSection("肘 (Elbows) 詳細", ref _showElbowAdvanced, _leftElbow, _rightElbow, _unlinkElbow, comp, isElbow: true);
                EditorGUILayout.Space(2f);
                DrawAdvancedJointSection("膝 (Knees) 詳細", ref _showKneeAdvanced, _leftKnee, _rightKnee, _unlinkKnee, comp, isElbow: false);
                EditorGUILayout.Space(4f);
                _showSceneHandles = EditorGUILayout.ToggleLeft("Scene ビュー操作ハンドルを表示 (Pivot / Outward)", _showSceneHandles);
                if (avatarRoot != null && GUILayout.Button("関節フレーム再計算（マッスル軸実測）"))
                {
                    JointKeeperPreviewState.ResampleMeasuredAxesSafe(avatarRoot.gameObject);
                    SceneView.RepaintAll();
                }
                DrawTwistDiagnostics(comp, avatarAnimator, avatarRoot);
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();
        }

        private static void DrawPreviewControls(Transform avatarRoot)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            bool nextEnabled = EditorGUILayout.ToggleLeft("Edit モード曲げプレビュー (NDMF Preview)", JointKeeperPreviewState.Enabled, EditorStyles.boldLabel);
            if (nextEnabled != JointKeeperPreviewState.Enabled)
            {
                JointKeeperPreviewState.SetEnabled(nextEnabled);
                SceneView.RepaintAll();
            }
            if (JointKeeperPreviewState.Enabled && GUILayout.Button("リセット", GUILayout.Width(64f)))
            {
                JointKeeperPreviewState.ResetPose();
                SceneView.RepaintAll();
            }
            EditorGUILayout.EndHorizontal();

            if (JointKeeperPreviewState.Enabled)
            {
                EditorGUI.indentLevel++;
                float nextBend = EditorGUILayout.Slider("曲げ角 (Bend °)", JointKeeperPreviewState.BendAngleDeg, 0f, 150f);
                if (!Mathf.Approximately(nextBend, JointKeeperPreviewState.BendAngleDeg))
                {
                    JointKeeperPreviewState.SetBendAngle(nextBend);
                    SceneView.RepaintAll();
                }

                float nextTwist = EditorGUILayout.Slider("前腕ツイスト (Twist °)", JointKeeperPreviewState.TwistAngleDeg, -90f, 90f);
                if (!Mathf.Approximately(nextTwist, JointKeeperPreviewState.TwistAngleDeg))
                {
                    JointKeeperPreviewState.SetTwistAngle(nextTwist);
                    SceneView.RepaintAll();
                }

                bool nextBefore = EditorGUILayout.Toggle("適用前と比較 (Before 表示)", JointKeeperPreviewState.ShowBeforeOnly);
                if (nextBefore != JointKeeperPreviewState.ShowBeforeOnly)
                {
                    JointKeeperPreviewState.SetShowBeforeOnly(nextBefore);
                    SceneView.RepaintAll();
                }

                var nextHm = (PreviewHeatmapMode)EditorGUILayout.EnumPopup("ヒートマップ表示", JointKeeperPreviewState.HeatmapMode);
                if (nextHm != JointKeeperPreviewState.HeatmapMode)
                {
                    JointKeeperPreviewState.SetHeatmapMode(nextHm);
                    SceneView.RepaintAll();
                }
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndVertical();
        }

        private void OnSceneGUI()
        {
            var comp = (JointKeeperComponent)target;
            if (comp == null || !comp.enabled) return;

            Animator avatarAnimator = FindAvatarAnimator(comp.transform);
            Transform avatarRoot = avatarAnimator != null ? avatarAnimator.transform : comp.transform.root;
            JointKeeperSceneHandles.DrawSceneGUI(comp, avatarAnimator, avatarRoot, _showSceneHandles);
        }

        private static void DrawStatusAndPlacementGuides(
            JointKeeperComponent comp,
            Animator avatarAnimator,
            Transform avatarRoot)
        {
            bool isHumanoid = avatarAnimator != null
                && (avatarAnimator.isHuman || (avatarAnimator.avatar != null && avatarAnimator.avatar.isValid && avatarAnimator.avatar.isHuman));
            if (!isHumanoid)
            {
                EditorGUILayout.HelpBox(
                    "親階層に Humanoid Animator が見つかりません。VRChat アバター配下の衣装オブジェクトにアタッチしてください。",
                    MessageType.Warning);
                return;
            }

            var targets = CollectTargetRenderersPreview(comp, avatarRoot);
            if (targets.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "配下に SkinnedMeshRenderer が見つかりません。衣装のルートオブジェクトまたは衣装メッシュに直接アタッチしてください。",
                    MessageType.Warning);
            }
            else
            {
                string summary = $"対象メッシュ: {targets.Count} 件（ビルド時に肘・膝の境界頂点があるメッシュのみ自動適用されます）";
                EditorGUILayout.HelpBox(summary, MessageType.Info);

                if (comp.transform == avatarRoot && comp.targetMode == TargetRendererMode.AutoDetect)
                {
                    EditorGUILayout.HelpBox(
                        "アバタールートに配置されているため素体メッシュも対象に含まれます。素体を除外したい場合は「詳細設定 → 除外リスト」へ追加してください。",
                        MessageType.None);
                }
            }
        }

        private static void DrawSimpleJointControls(
            string label,
            SerializedProperty leftProp,
            SerializedProperty rightProp,
            SerializedProperty unlinkProp)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var enabledProp = leftProp.FindPropertyRelative("enabled");
            var rightEnabledProp = rightProp.FindPropertyRelative("enabled");
            bool isUnlinked = unlinkProp.boolValue;

            if (!isUnlinked)
            {
                enabledProp.boolValue = EditorGUILayout.ToggleLeft(label, enabledProp.boolValue, EditorStyles.boldLabel);
                if (enabledProp.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.Slider(leftProp.FindPropertyRelative("strength"), 0f, 1f, new GUIContent("強度 (Strength)"));
                    EditorGUILayout.Slider(leftProp.FindPropertyRelative("pushAt90Deg"), -0.01f, 0.02f, new GUIContent("90° 押し出し量 (m)"));
                    EditorGUI.indentLevel--;
                }
            }
            else
            {
                EditorGUILayout.LabelField($"{label}（左右個別設定中）", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                enabledProp.boolValue = EditorGUILayout.ToggleLeft("左 (Left)", enabledProp.boolValue);
                if (enabledProp.boolValue)
                {
                    EditorGUILayout.Slider(leftProp.FindPropertyRelative("strength"), 0f, 1f, new GUIContent("左 強度"));
                    EditorGUILayout.Slider(leftProp.FindPropertyRelative("pushAt90Deg"), -0.01f, 0.02f, new GUIContent("左 90° 押し出し量 (m)"));
                }
                rightEnabledProp.boolValue = EditorGUILayout.ToggleLeft("右 (Right)", rightEnabledProp.boolValue);
                if (rightEnabledProp.boolValue)
                {
                    EditorGUILayout.Slider(rightProp.FindPropertyRelative("strength"), 0f, 1f, new GUIContent("右 強度"));
                    EditorGUILayout.Slider(rightProp.FindPropertyRelative("pushAt90Deg"), -0.01f, 0.02f, new GUIContent("右 90° 押し出し量 (m)"));
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawAdvancedTargetSettings()
        {
            EditorGUILayout.LabelField("対象メッシュ設定", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_targetMode, new GUIContent("検出モード"));
            if ((TargetRendererMode)_targetMode.enumValueIndex == TargetRendererMode.AutoDetect)
            {
                EditorGUILayout.PropertyField(_excludedRenderers, new GUIContent("除外メッシュ (Excluded)"), true);
            }
            else
            {
                EditorGUILayout.PropertyField(_targetRenderers, new GUIContent("対象メッシュ (Manual)"), true);
            }
        }

        private void DrawAdvancedJointSection(
            string title,
            ref bool foldout,
            SerializedProperty leftProp,
            SerializedProperty rightProp,
            SerializedProperty unlinkProp,
            JointKeeperComponent comp,
            bool isElbow)
        {
            foldout = EditorGUILayout.Foldout(foldout, title, true);
            if (!foldout) return;

            EditorGUI.indentLevel++;
            bool prevUnlink = unlinkProp.boolValue;
            bool nextUnlink = EditorGUILayout.Toggle(new GUIContent("左右個別に調整 (Unlink L/R)"), prevUnlink);
            if (nextUnlink != prevUnlink)
            {
                unlinkProp.boolValue = nextUnlink;
                if (nextUnlink)
                {
                    // リンク解除時に左側の数値設定をミラーコピーして右側へ反映 (§3.3)
                    serializedObject.ApplyModifiedProperties();
                    Undo.RecordObject(comp, "Unlink JointKeeper Left/Right");
                    if (isElbow) comp.rightElbow.CopyMirroredNumericsFrom(comp.leftElbow);
                    else comp.rightKnee.CopyMirroredNumericsFrom(comp.leftKnee);
                    EditorUtility.SetDirty(comp);
                    serializedObject.Update();
                }
            }

            if (!unlinkProp.boolValue)
            {
                DrawDetailedJointFields(leftProp, "左右共通（左基準ミラー）");
            }
            else
            {
                DrawDetailedJointFields(leftProp, "左 (Left)");
                EditorGUILayout.Space(2f);
                DrawDetailedJointFields(rightProp, "右 (Right)");
            }
            EditorGUI.indentLevel--;
        }

        private static void DrawDetailedJointFields(SerializedProperty prop, string header)
        {
            EditorGUILayout.LabelField(header, EditorStyles.miniBoldLabel);
            EditorGUILayout.Slider(prop.FindPropertyRelative("bendFollowRatio"), 0.1f, 0.9f, new GUIContent("追従率 (Bend Follow Ratio)"));
            EditorGUILayout.Slider(prop.FindPropertyRelative("blendCurvePower"), 1.0f, 3.0f, new GUIContent("分配カーブ指数 (Gamma)"));
            EditorGUILayout.PropertyField(prop.FindPropertyRelative("pivotOffset"), new GUIContent("ピボット調整 (Axial, Out, Hinge)"));
            EditorGUILayout.Slider(prop.FindPropertyRelative("outwardAngleOffset"), -90f, 90f, new GUIContent("外側方向オフセット (deg)"));

            EditorGUILayout.Slider(prop.FindPropertyRelative("axialRadiusScale"), 0.05f, 1.0f, new GUIContent("軸方向影響半径 (肢長比)"));
            EditorGUILayout.Slider(prop.FindPropertyRelative("radialRadiusScale"), 0.05f, 1.5f, new GUIContent("径方向影響半径 (肢長比)"));
            EditorGUILayout.Slider(prop.FindPropertyRelative("innerFalloffRatio"), 0f, 0.95f, new GUIContent("減衰開始比"));
            EditorGUILayout.Slider(prop.FindPropertyRelative("innerCutoff"), -1f, 1f, new GUIContent("内側カットオフ"));
            EditorGUILayout.Slider(prop.FindPropertyRelative("outerFull"), -1f, 1f, new GUIContent("外側フル開始"));
            EditorGUILayout.Slider(prop.FindPropertyRelative("innerRetention"), 0f, 0.5f, new GUIContent("内側最小維持率"));

            EditorGUILayout.PropertyField(prop.FindPropertyRelative("autoDetectTwistBones"), new GUIContent("ツイストボーン自動分類"));
            EditorGUILayout.PropertyField(prop.FindPropertyRelative("distalSourceMode"), new GUIContent("遠位ソースモード"));
            if ((DistalSourceMode)prop.FindPropertyRelative("distalSourceMode").enumValueIndex == DistalSourceMode.CustomOverride)
            {
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("distalSourceOverride"), new GUIContent("遠位ソース上書き"));
            }
            EditorGUILayout.PropertyField(prop.FindPropertyRelative("extraProximalBones"), new GUIContent("手動追加: 近位ボーン (G_P)"), true);
            EditorGUILayout.PropertyField(prop.FindPropertyRelative("extraDistalBones"), new GUIContent("手動追加: 遠位ボーン (G_D)"), true);
            EditorGUILayout.PropertyField(prop.FindPropertyRelative("excludedBones"), new GUIContent("手動除外ボーン"), true);

            EditorGUILayout.Slider(prop.FindPropertyRelative("minGroupWeightThreshold"), 0.001f, 0.25f, new GUIContent("境界判定閾値"));
            EditorGUILayout.Slider(prop.FindPropertyRelative("slotSnapThreshold"), 0f, 0.15f, new GUIContent("4ボーン Tier2 吸収閾値"));
            EditorGUILayout.Slider(prop.FindPropertyRelative("noisePruneThreshold"), 0f, 0.1f, new GUIContent("4ボーン Tier3 ノイズ閾値"));
        }

        private void DrawTwistDiagnostics(
            JointKeeperComponent comp,
            Animator avatarAnimator,
            Transform avatarRoot)
        {
            _showTwistDiagnostics = EditorGUILayout.Foldout(_showTwistDiagnostics, "ツイストボーン検出・診断", true);
            if (!_showTwistDiagnostics) return;

            if (avatarAnimator == null)
            {
                EditorGUILayout.HelpBox("Humanoid Animator が見つかりません。", MessageType.Info);
                return;
            }

            var humanoidBones = HumanoidBoneResolver.ResolveAll(avatarAnimator, out _);
            if (humanoidBones.Count == 0)
            {
                EditorGUILayout.HelpBox("Humanoid ボーンを解決できませんでした。", MessageType.Info);
                return;
            }

            var ctx = AvatarKinematicContext.Build(avatarRoot.gameObject, avatarAnimator, humanoidBones);
            var targets = CollectTargetRenderersPreview(comp, avatarRoot);
            if (targets.Count == 0)
            {
                EditorGUILayout.LabelField("対象メッシュがありません。");
                return;
            }

            // 代表として先頭 SMR の分類結果を表示
            SkinnedMeshRenderer sampleSmr = targets[0];
            EditorGUILayout.LabelField($"診断サンプル: {sampleSmr.name}", EditorStyles.miniBoldLabel);

            var joints = new[] { JointId.LeftElbow, JointId.RightElbow, JointId.LeftKnee, JointId.RightKnee };
            foreach (var jointId in joints)
            {
                JointConfig cfg = comp.ResolveConfig(jointId);
                if (!cfg.enabled) continue;

                Transform upper = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetUpperHumanoidBone());
                Transform lower = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetLowerHumanoidBone());
                Transform child = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetChildHumanoidBone());
                if (upper == null || lower == null) continue;

                var groups = TwistBoneClassifier.Classify(
                    jointId, upper, lower, child, sampleSmr.bones, cfg, ctx);

                string pSrc = groups.ProximalSource != null ? groups.ProximalSource.name : "None";
                string dSrc = groups.DistalSource != null ? groups.DistalSource.name : "None";
                string depthNote = (groups.ProximalSourceIsConstraintDriven || groups.DistalSourceIsConstraintDriven)
                    ? " [Source Constraint 駆動]"
                    : string.Empty;

                EditorGUILayout.LabelField(
                    $"{jointId}: G_P={groups.ProximalBoneIndices.Count}, G_D={groups.DistalBoneIndices.Count} | Src=({pSrc}, {dSrc}){depthNote}",
                    EditorStyles.miniLabel);

                foreach (var warn in groups.DiagnosticWarnings)
                    EditorGUILayout.HelpBox(warn, MessageType.Warning);
            }
        }

        private static Animator FindAvatarAnimator(Transform t)
        {
            Transform cur = t;
            Animator found = null;
            while (cur != null)
            {
                var anim = cur.GetComponent<Animator>();
                if (anim != null && (anim.isHuman || (anim.avatar != null && anim.avatar.isValid && anim.avatar.isHuman)))
                    found = anim;
                cur = cur.parent;
            }
            return found;
        }

        private static List<SkinnedMeshRenderer> CollectTargetRenderersPreview(
            JointKeeperComponent comp,
            Transform avatarRoot)
        {
            var result = new List<SkinnedMeshRenderer>();
            if (comp == null) return result;

            if (comp.targetMode == TargetRendererMode.ManualList)
            {
                if (comp.targetRenderers != null)
                {
                    foreach (var smr in comp.targetRenderers)
                    {
                        if (smr != null && (avatarRoot == null || smr.transform.IsChildOf(avatarRoot)))
                            result.Add(smr);
                    }
                }
                return result;
            }

            var excluded = new HashSet<SkinnedMeshRenderer>();
            if (comp.excludedRenderers != null)
            {
                foreach (var ex in comp.excludedRenderers)
                {
                    if (ex != null) excluded.Add(ex);
                }
            }

            var found = comp.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < found.Length; i++)
            {
                if (found[i] != null && !excluded.Contains(found[i]))
                    result.Add(found[i]);
            }
            return result;
        }
    }
}
