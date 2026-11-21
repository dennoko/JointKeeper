using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// 段階的開示（Progressive Disclosure）を採用した JointKeeperComponent のカスタム Inspector (§7.1)。
    /// DennokoMeshEditor に倣い、上部にバージョンチェックバーと言語切替（JA/EN）を配置し、
    /// すべての設定項目にマウスオーバーツールチップを提供する。
    /// </summary>
    [CustomEditor(typeof(JointKeeperComponent))]
    internal sealed class JointKeeperEditor : UnityEditor.Editor
    {
        private bool _showAdvanced;
        private bool _showElbowAdvanced;
        private bool _showKneeAdvanced;
        private bool _showTwistDiagnostics;
        private bool _showSceneHandles;
        private bool _showBakedAssets;

        private SerializedProperty _targetMode;
        private SerializedProperty _targetRenderers;
        private SerializedProperty _excludedRenderers;
        private SerializedProperty _unlinkElbow;
        private SerializedProperty _leftElbow;
        private SerializedProperty _rightElbow;
        private SerializedProperty _unlinkKnee;
        private SerializedProperty _leftKnee;
        private SerializedProperty _rightKnee;
        private SerializedProperty _leftTwist;
        private SerializedProperty _rightTwist;

        /// <summary>前腕ツイスト診断のキャッシュ（メッシュ全頂点を走査するため Inspector 表示ごとには再計算しない）。</summary>
        private readonly Dictionary<(int smrId, int meshId, JointId jointId), ForearmTwistSmrDiagnostics> _twistDiagCache =
            new Dictionary<(int, int, JointId), ForearmTwistSmrDiagnostics>();

        private DennokoVersionChecker.Result _versionResult;
        private static GUIStyle _versionLinkStyle;

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
            _leftTwist = serializedObject.FindProperty("leftForearmTwist");
            _rightTwist = serializedObject.FindProperty("rightForearmTwist");
            _twistDiagCache.Clear();

            JointKeeperLocalization.OnLanguageChanged += OnLanguageChanged;

            ReloadVersionResult();
            JointKeeperVersion.StartCheckBackgroundTask();
        }

        private void OnDisable()
        {
            JointKeeperLocalization.OnLanguageChanged -= OnLanguageChanged;
        }

        private void OnLanguageChanged()
        {
            Repaint();
        }

        /// <summary>取得完了時に JointKeeperVersion から呼ばれる。</summary>
        internal void ReloadVersionResult()
        {
            _versionResult = JointKeeperVersion.LoadResultFromSessionState();
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawVersionBar();
            EditorGUILayout.Space(4f);

            var comp = (JointKeeperComponent)target;
            Animator avatarAnimator = FindAvatarAnimator(comp.transform);
            Transform avatarRoot = avatarAnimator != null ? avatarAnimator.transform : comp.transform.root;

            DrawStatusAndPlacementGuides(comp, avatarAnimator, avatarRoot);

            if (comp.IsBaked)
            {
                EditorGUILayout.Space(4f);
                if (DrawBakedStatusBox(comp))
                {
                    return;
                }
            }

            EditorGUILayout.Space(4f);
            DrawSimpleJointControls(
                "joint.elbows", "joint.elbows_tooltip",
                _leftElbow, _rightElbow, _unlinkElbow);
            EditorGUILayout.Space(2f);
            DrawSimpleTwistControls(comp, avatarAnimator, avatarRoot);
            EditorGUILayout.Space(2f);
            DrawSimpleJointControls(
                "joint.knees", "joint.knees_tooltip",
                _leftKnee, _rightKnee, _unlinkKnee);
            EditorGUILayout.Space(4f);

            if (!comp.IsBaked)
            {
                DrawPreviewControls(avatarRoot);
                EditorGUILayout.Space(4f);
                DrawBakeSection(comp);
            }

            EditorGUILayout.Space(6f);
            _showAdvanced = EditorGUILayout.Foldout(
                _showAdvanced,
                JointKeeperLocalization.Content("advanced.foldout", "advanced.foldout_tooltip"),
                true,
                EditorStyles.foldoutHeader);

            if (_showAdvanced)
            {
                EditorGUI.indentLevel++;
                DrawAdvancedTargetSettings();
                EditorGUILayout.Space(4f);
                DrawAdvancedJointSection("advanced.elbow_section", ref _showElbowAdvanced, _leftElbow, _rightElbow, _unlinkElbow, comp, isElbow: true);
                EditorGUILayout.Space(2f);
                DrawAdvancedJointSection("advanced.knee_section", ref _showKneeAdvanced, _leftKnee, _rightKnee, _unlinkKnee, comp, isElbow: false);
                EditorGUILayout.Space(4f);

                _showSceneHandles = EditorGUILayout.ToggleLeft(
                    JointKeeperLocalization.Content("advanced.show_scene_handles", "advanced.show_scene_handles_tooltip"),
                    _showSceneHandles);

                if (avatarRoot != null
                    && GUILayout.Button(JointKeeperLocalization.Content("advanced.resample_axes", "advanced.resample_axes_tooltip")))
                {
                    JointKeeperPreviewState.ResampleMeasuredAxesSafe(avatarRoot.gameObject);
                    SceneView.RepaintAll();
                }

                DrawTwistDiagnostics(comp, avatarAnimator, avatarRoot);
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// 一番上の 1 行（DennokoMeshEditor 準拠）。
        /// 左に現在バージョンと更新状態、右端に再確認ボタン（↻）と言語切替ボタン（EN/JA）。
        /// </summary>
        private void DrawVersionBar()
        {
            if (_versionLinkStyle == null)
            {
                _versionLinkStyle = new GUIStyle(EditorStyles.miniLabel);
            }

            var prevColor = GUI.contentColor;

            EditorGUILayout.BeginHorizontal();

            GUI.contentColor = new Color(0.68f, 0.68f, 0.68f);
            GUILayout.Label($"v{_versionResult.LocalVersion}", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
            GUI.contentColor = prevColor;

            switch (_versionResult.State)
            {
                case DennokoVersionChecker.State.UpdateAvailable:
                {
                    var tooltip = string.IsNullOrEmpty(_versionResult.Message)
                        ? JointKeeperLocalization.Tr("version.update_tooltip")
                        : _versionResult.Message;

                    GUI.contentColor = new Color(0.35f, 0.8f, 0.4f);
                    var clicked = GUILayout.Button(
                        new GUIContent(JointKeeperLocalization.Format("version.update_available", _versionResult.LatestVersion), tooltip),
                        _versionLinkStyle,
                        GUILayout.ExpandWidth(false));
                    GUI.contentColor = prevColor;

                    EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
                    if (clicked)
                    {
                        JointKeeperVersion.OpenUpdatePage(_versionResult.Url);
                    }
                    break;
                }

                case DennokoVersionChecker.State.Error:
                    GUI.contentColor = new Color(1f, 0.72f, 0.3f);
                    GUILayout.Label(
                        JointKeeperLocalization.Content("version.error", "version.error_tooltip"),
                        EditorStyles.miniLabel,
                        GUILayout.ExpandWidth(false));
                    GUI.contentColor = prevColor;
                    break;

                case DennokoVersionChecker.State.Checking:
                    GUI.contentColor = new Color(0.55f, 0.55f, 0.55f);
                    GUILayout.Label(
                        JointKeeperLocalization.Tr("version.checking"),
                        EditorStyles.miniLabel,
                        GUILayout.ExpandWidth(false));
                    GUI.contentColor = prevColor;
                    break;

                default: // UpToDate
                    break;
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button(
                new GUIContent("↻", JointKeeperLocalization.Tr("version.recheck_tooltip")),
                EditorStyles.miniButton,
                GUILayout.Width(22f)))
            {
                JointKeeperVersion.ForceRecheck();
                ReloadVersionResult();
            }

            if (GUILayout.Button(
                new GUIContent(JointKeeperLocalization.ButtonText, JointKeeperLocalization.ButtonTooltip),
                EditorStyles.miniButton,
                GUILayout.Width(28f)))
            {
                JointKeeperLocalization.ToggleLanguage();
            }

            EditorGUILayout.EndHorizontal();

            var separator = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(separator, new Color(0f, 0f, 0f, 0.2f));
        }

        private static void DrawPreviewControls(Transform avatarRoot)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            bool nextEnabled = EditorGUILayout.ToggleLeft(
                JointKeeperLocalization.Content("preview.toggle", "preview.toggle_tooltip"),
                JointKeeperPreviewState.Enabled,
                EditorStyles.boldLabel);
            if (nextEnabled != JointKeeperPreviewState.Enabled)
            {
                JointKeeperPreviewState.SetEnabled(nextEnabled);
                SceneView.RepaintAll();
            }

            if (JointKeeperPreviewState.Enabled
                && GUILayout.Button(
                    JointKeeperLocalization.Content("preview.reset", "preview.reset_tooltip"),
                    GUILayout.Width(64f)))
            {
                JointKeeperPreviewState.ResetPose();
                SceneView.RepaintAll();
            }

            EditorGUILayout.EndHorizontal();

            if (JointKeeperPreviewState.Enabled)
            {
                EditorGUI.indentLevel++;

                float nextBend = EditorGUILayout.Slider(
                    JointKeeperLocalization.Content("preview.bend_angle", "preview.bend_angle_tooltip"),
                    JointKeeperPreviewState.BendAngleDeg,
                    0f,
                    150f);
                if (!Mathf.Approximately(nextBend, JointKeeperPreviewState.BendAngleDeg))
                {
                    JointKeeperPreviewState.SetBendAngle(nextBend);
                    SceneView.RepaintAll();
                }

                float nextTwist = EditorGUILayout.Slider(
                    JointKeeperLocalization.Content("preview.twist_angle", "preview.twist_angle_tooltip"),
                    JointKeeperPreviewState.TwistAngleDeg,
                    -90f,
                    90f);
                if (!Mathf.Approximately(nextTwist, JointKeeperPreviewState.TwistAngleDeg))
                {
                    JointKeeperPreviewState.SetTwistAngle(nextTwist);
                    SceneView.RepaintAll();
                }

                bool nextBefore = EditorGUILayout.Toggle(
                    JointKeeperLocalization.Content("preview.show_before", "preview.show_before_tooltip"),
                    JointKeeperPreviewState.ShowBeforeOnly);
                if (nextBefore != JointKeeperPreviewState.ShowBeforeOnly)
                {
                    JointKeeperPreviewState.SetShowBeforeOnly(nextBefore);
                    SceneView.RepaintAll();
                }

                var nextHm = (PreviewHeatmapMode)EditorGUILayout.EnumPopup(
                    JointKeeperLocalization.Content("preview.heatmap", "preview.heatmap_tooltip"),
                    JointKeeperPreviewState.HeatmapMode);
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
            if (comp == null || (!comp.enabled && !comp.IsBaked)) return;

            Animator avatarAnimator = FindAvatarAnimator(comp.transform);
            Transform avatarRoot = avatarAnimator != null ? avatarAnimator.transform : comp.transform.root;
            JointKeeperSceneHandles.DrawSceneGUI(comp, avatarAnimator, avatarRoot, _showSceneHandles);
        }

        private bool DrawBakedStatusBox(JointKeeperComponent comp)
        {
            var rec = comp.bakeRecord;
            int meshCount = rec?.renderers != null ? rec.renderers.Count : 0;
            int helperCount = rec?.createdHelperBones != null ? rec.createdHelperBones.Count : 0;
            int totalVerts = 0;
            if (rec?.renderers != null)
            {
                for (int i = 0; i < rec.renderers.Count; i++)
                    totalVerts += rec.renderers[i] != null ? rec.renderers[i].affectedVertexCount : 0;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(JointKeeperLocalization.Tr("bake.baked_title"), EditorStyles.boldLabel);

            string info = JointKeeperLocalization.Format("bake.baked_summary", meshCount, helperCount, totalVerts);
            if (rec != null && rec.bakedWithScaleAdjuster)
            {
                info += JointKeeperLocalization.Tr("bake.baked_scale_adjuster_note");
            }
            EditorGUILayout.HelpBox(info, MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(JointKeeperLocalization.Content("bake.btn_rebake", "bake.btn_rebake_tooltip")))
            {
                serializedObject.ApplyModifiedProperties();
                var res = JointKeeperBaker.BakeComponent(comp);
                if (!res.Success)
                    EditorUtility.DisplayDialog(JointKeeperLocalization.Tr("bake.dialog_rebake_title"), res.Message, "OK");
                serializedObject.Update();
                SceneView.RepaintAll();
            }
            if (GUILayout.Button(JointKeeperLocalization.Content("bake.btn_revert", "bake.btn_revert_tooltip")))
            {
                serializedObject.ApplyModifiedProperties();
                JointKeeperBaker.RevertComponent(comp, deleteGeneratedAssets: true);
                serializedObject.Update();
                SceneView.RepaintAll();
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button(JointKeeperLocalization.Content("bake.btn_finalize", "bake.btn_finalize_tooltip")))
            {
                bool ok = EditorUtility.DisplayDialog(
                    JointKeeperLocalization.Tr("bake.dialog_finalize_title"),
                    JointKeeperLocalization.Tr("bake.dialog_finalize_body"),
                    JointKeeperLocalization.Tr("bake.dialog_finalize_ok"),
                    JointKeeperLocalization.Tr("bake.dialog_cancel"));
                if (ok)
                {
                    JointKeeperBaker.FinalizeAndRemoveComponent(comp);
                    return true;
                }
            }

            _showBakedAssets = EditorGUILayout.Foldout(
                _showBakedAssets,
                JointKeeperLocalization.Content("bake.generated_list", "bake.generated_list_tooltip"),
                true);
            if (_showBakedAssets && rec != null)
            {
                EditorGUI.indentLevel++;
                using (new EditorGUI.DisabledScope(true))
                {
                    if (rec.renderers != null)
                    {
                        for (int i = 0; i < rec.renderers.Count; i++)
                        {
                            var r = rec.renderers[i];
                            if (r == null) continue;
                            EditorGUILayout.ObjectField(
                                r.renderer != null ? r.renderer.name : $"Mesh {i}",
                                r.bakedMesh,
                                typeof(Mesh),
                                allowSceneObjects: false);
                        }
                    }
                    if (rec.createdHelperBones != null)
                    {
                        for (int i = 0; i < rec.createdHelperBones.Count; i++)
                        {
                            var hb = rec.createdHelperBones[i];
                            if (hb == null) continue;
                            EditorGUILayout.ObjectField(
                                JointKeeperLocalization.Format("bake.helper_item", i + 1),
                                hb,
                                typeof(GameObject),
                                allowSceneObjects: true);
                        }
                    }
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
            return false;
        }

        private void DrawBakeSection(JointKeeperComponent comp)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(JointKeeperLocalization.Tr("bake.section_title"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                JointKeeperLocalization.Tr("bake.section_desc"),
                EditorStyles.wordWrappedMiniLabel);

            if (GUILayout.Button(JointKeeperLocalization.Content("bake.btn_bake", "bake.btn_bake_tooltip")))
            {
                serializedObject.ApplyModifiedProperties();
                var res = JointKeeperBaker.BakeComponent(comp);
                if (!res.Success)
                {
                    EditorUtility.DisplayDialog(JointKeeperLocalization.Tr("bake.dialog_title"), res.Message, "OK");
                }
                serializedObject.Update();
                SceneView.RepaintAll();
            }
            EditorGUILayout.EndVertical();
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
                EditorGUILayout.HelpBox(JointKeeperLocalization.Tr("status.no_animator"), MessageType.Warning);
                return;
            }

            if (comp.IsBaked)
                return;

            var targets = JointKeeperTargetCollector.CollectTargets(comp, avatarRoot);
            if (targets.Count == 0)
            {
                EditorGUILayout.HelpBox(JointKeeperLocalization.Tr("status.no_targets"), MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    JointKeeperLocalization.Format("status.target_summary", targets.Count),
                    MessageType.Info);

                if (comp.transform == avatarRoot && comp.targetMode == TargetRendererMode.AutoDetect)
                {
                    EditorGUILayout.HelpBox(JointKeeperLocalization.Tr("status.root_placement_note"), MessageType.None);
                }
            }
        }

        private static void DrawSimpleJointControls(
            string labelKey,
            string tooltipKey,
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
                enabledProp.boolValue = EditorGUILayout.ToggleLeft(
                    JointKeeperLocalization.Content(labelKey, tooltipKey),
                    enabledProp.boolValue,
                    EditorStyles.boldLabel);
                if (enabledProp.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.Slider(
                        leftProp.FindPropertyRelative("strength"),
                        0f, 1f,
                        JointKeeperLocalization.Content("joint.strength", "joint.strength_tooltip"));
                    EditorGUILayout.Slider(
                        leftProp.FindPropertyRelative("pushAt90Deg"),
                        -0.01f, 0.02f,
                        JointKeeperLocalization.Content("joint.push90", "joint.push90_tooltip"));
                    EditorGUI.indentLevel--;
                }
            }
            else
            {
                EditorGUILayout.LabelField(
                    new GUIContent(
                        JointKeeperLocalization.Format("joint.unlinked_suffix", JointKeeperLocalization.Tr(labelKey)),
                        JointKeeperLocalization.Tr(tooltipKey)),
                    EditorStyles.boldLabel);
                EditorGUI.indentLevel++;

                enabledProp.boolValue = EditorGUILayout.ToggleLeft(
                    JointKeeperLocalization.Content("joint.left", "joint.left_tooltip"),
                    enabledProp.boolValue);
                if (enabledProp.boolValue)
                {
                    EditorGUILayout.Slider(
                        leftProp.FindPropertyRelative("strength"),
                        0f, 1f,
                        JointKeeperLocalization.Content("joint.left_strength", "joint.strength_tooltip"));
                    EditorGUILayout.Slider(
                        leftProp.FindPropertyRelative("pushAt90Deg"),
                        -0.01f, 0.02f,
                        JointKeeperLocalization.Content("joint.left_push90", "joint.push90_tooltip"));
                }

                rightEnabledProp.boolValue = EditorGUILayout.ToggleLeft(
                    JointKeeperLocalization.Content("joint.right", "joint.right_tooltip"),
                    rightEnabledProp.boolValue);
                if (rightEnabledProp.boolValue)
                {
                    EditorGUILayout.Slider(
                        rightProp.FindPropertyRelative("strength"),
                        0f, 1f,
                        JointKeeperLocalization.Content("joint.right_strength", "joint.strength_tooltip"));
                    EditorGUILayout.Slider(
                        rightProp.FindPropertyRelative("pushAt90Deg"),
                        -0.01f, 0.02f,
                        JointKeeperLocalization.Content("joint.right_push90", "joint.push90_tooltip"));
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawAdvancedTargetSettings()
        {
            EditorGUILayout.LabelField(JointKeeperLocalization.Tr("advanced.target_header"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(
                _targetMode,
                JointKeeperLocalization.Content("advanced.target_mode", "advanced.target_mode_tooltip"));

            if ((TargetRendererMode)_targetMode.enumValueIndex == TargetRendererMode.AutoDetect)
            {
                EditorGUILayout.PropertyField(
                    _excludedRenderers,
                    JointKeeperLocalization.Content("advanced.excluded_renderers", "advanced.excluded_renderers_tooltip"),
                    true);
            }
            else
            {
                EditorGUILayout.PropertyField(
                    _targetRenderers,
                    JointKeeperLocalization.Content("advanced.target_renderers", "advanced.target_renderers_tooltip"),
                    true);
            }
        }

        private void DrawAdvancedJointSection(
            string titleKey,
            ref bool foldout,
            SerializedProperty leftProp,
            SerializedProperty rightProp,
            SerializedProperty unlinkProp,
            JointKeeperComponent comp,
            bool isElbow)
        {
            foldout = EditorGUILayout.Foldout(foldout, JointKeeperLocalization.Tr(titleKey), true);
            if (!foldout) return;

            EditorGUI.indentLevel++;
            bool prevUnlink = unlinkProp.boolValue;
            bool nextUnlink = EditorGUILayout.Toggle(
                JointKeeperLocalization.Content("advanced.unlink_lr", "advanced.unlink_lr_tooltip"),
                prevUnlink);
            if (nextUnlink != prevUnlink)
            {
                unlinkProp.boolValue = nextUnlink;
                if (nextUnlink)
                {
                    serializedObject.ApplyModifiedProperties();
                    Undo.RecordObject(comp, "Unlink JointKeeper Left/Right");
                    if (isElbow)
                    {
                        comp.rightElbow.CopyMirroredNumericsFrom(comp.leftElbow);
                        if (comp.rightForearmTwist == null) comp.rightForearmTwist = new ForearmTwistConfig();
                        comp.rightForearmTwist.CopyNumericsFrom(comp.leftForearmTwist);
                    }
                    else comp.rightKnee.CopyMirroredNumericsFrom(comp.leftKnee);
                    EditorUtility.SetDirty(comp);
                    serializedObject.Update();
                }
            }

            if (!unlinkProp.boolValue)
            {
                DrawDetailedJointFields(leftProp, JointKeeperLocalization.Tr("advanced.lr_shared_header"));
            }
            else
            {
                DrawDetailedJointFields(leftProp, JointKeeperLocalization.Tr("joint.left"));
                EditorGUILayout.Space(2f);
                DrawDetailedJointFields(rightProp, JointKeeperLocalization.Tr("joint.right"));
            }

            if (isElbow)
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField(JointKeeperLocalization.Tr("twist.advanced_header"), EditorStyles.boldLabel);
                if (!unlinkProp.boolValue)
                {
                    DrawDetailedTwistFields(_leftTwist, JointKeeperLocalization.Tr("advanced.lr_shared_header"));
                }
                else
                {
                    DrawDetailedTwistFields(_leftTwist, JointKeeperLocalization.Tr("joint.left"));
                    EditorGUILayout.Space(2f);
                    DrawDetailedTwistFields(_rightTwist, JointKeeperLocalization.Tr("joint.right"));
                }
            }
            EditorGUI.indentLevel--;
        }

        private static void DrawDetailedJointFields(SerializedProperty prop, string header)
        {
            EditorGUILayout.LabelField(header, EditorStyles.miniBoldLabel);

            EditorGUILayout.Slider(
                prop.FindPropertyRelative("bendFollowRatio"), 0.1f, 0.9f,
                JointKeeperLocalization.Content("advanced.bend_follow_ratio", "advanced.bend_follow_ratio_tooltip"));
            EditorGUILayout.Slider(
                prop.FindPropertyRelative("blendCurvePower"), 1.0f, 3.0f,
                JointKeeperLocalization.Content("advanced.blend_curve_power", "advanced.blend_curve_power_tooltip"));
            EditorGUILayout.PropertyField(
                prop.FindPropertyRelative("pivotOffset"),
                JointKeeperLocalization.Content("advanced.pivot_offset", "advanced.pivot_offset_tooltip"));
            EditorGUILayout.Slider(
                prop.FindPropertyRelative("outwardAngleOffset"), -90f, 90f,
                JointKeeperLocalization.Content("advanced.outward_angle", "advanced.outward_angle_tooltip"));

            EditorGUILayout.Slider(
                prop.FindPropertyRelative("axialRadiusScale"), 0.05f, 1.0f,
                JointKeeperLocalization.Content("advanced.axial_radius", "advanced.axial_radius_tooltip"));
            EditorGUILayout.Slider(
                prop.FindPropertyRelative("radialRadiusScale"), 0.05f, 1.5f,
                JointKeeperLocalization.Content("advanced.radial_radius", "advanced.radial_radius_tooltip"));
            EditorGUILayout.Slider(
                prop.FindPropertyRelative("innerFalloffRatio"), 0f, 0.95f,
                JointKeeperLocalization.Content("advanced.inner_falloff", "advanced.inner_falloff_tooltip"));
            EditorGUILayout.Slider(
                prop.FindPropertyRelative("innerCutoff"), -1f, 1f,
                JointKeeperLocalization.Content("advanced.inner_cutoff", "advanced.inner_cutoff_tooltip"));
            EditorGUILayout.Slider(
                prop.FindPropertyRelative("outerFull"), -1f, 1f,
                JointKeeperLocalization.Content("advanced.outer_full", "advanced.outer_full_tooltip"));
            EditorGUILayout.Slider(
                prop.FindPropertyRelative("innerRetention"), 0f, 0.5f,
                JointKeeperLocalization.Content("advanced.inner_retention", "advanced.inner_retention_tooltip"));

            EditorGUILayout.PropertyField(
                prop.FindPropertyRelative("autoDetectTwistBones"),
                JointKeeperLocalization.Content("advanced.auto_twist", "advanced.auto_twist_tooltip"));
            EditorGUILayout.PropertyField(
                prop.FindPropertyRelative("distalSourceMode"),
                JointKeeperLocalization.Content("advanced.distal_source_mode", "advanced.distal_source_mode_tooltip"));
            if ((DistalSourceMode)prop.FindPropertyRelative("distalSourceMode").enumValueIndex == DistalSourceMode.CustomOverride)
            {
                EditorGUILayout.PropertyField(
                    prop.FindPropertyRelative("distalSourceOverride"),
                    JointKeeperLocalization.Content("advanced.distal_source_override", "advanced.distal_source_override_tooltip"));
            }

            EditorGUILayout.PropertyField(
                prop.FindPropertyRelative("extraProximalBones"),
                JointKeeperLocalization.Content("advanced.extra_proximal", "advanced.extra_proximal_tooltip"),
                true);
            EditorGUILayout.PropertyField(
                prop.FindPropertyRelative("extraDistalBones"),
                JointKeeperLocalization.Content("advanced.extra_distal", "advanced.extra_distal_tooltip"),
                true);
            EditorGUILayout.PropertyField(
                prop.FindPropertyRelative("excludedBones"),
                JointKeeperLocalization.Content("advanced.excluded_bones", "advanced.excluded_bones_tooltip"),
                true);

            EditorGUILayout.Slider(
                prop.FindPropertyRelative("minGroupWeightThreshold"), 0.001f, 0.25f,
                JointKeeperLocalization.Content("advanced.min_group_weight", "advanced.min_group_weight_tooltip"));
            EditorGUILayout.Slider(
                prop.FindPropertyRelative("slotSnapThreshold"), 0f, 0.15f,
                JointKeeperLocalization.Content("advanced.slot_snap", "advanced.slot_snap_tooltip"));
            EditorGUILayout.Slider(
                prop.FindPropertyRelative("noisePruneThreshold"), 0f, 0.1f,
                JointKeeperLocalization.Content("advanced.noise_prune", "advanced.noise_prune_tooltip"));
        }

        private void DrawTwistDiagnostics(
            JointKeeperComponent comp,
            Animator avatarAnimator,
            Transform avatarRoot)
        {
            _showTwistDiagnostics = EditorGUILayout.Foldout(
                _showTwistDiagnostics,
                JointKeeperLocalization.Content("diag.foldout", "diag.foldout_tooltip"),
                true);
            if (!_showTwistDiagnostics) return;

            if (avatarAnimator == null)
            {
                EditorGUILayout.HelpBox(JointKeeperLocalization.Tr("diag.no_animator"), MessageType.Info);
                return;
            }

            var humanoidBones = HumanoidBoneResolver.ResolveAll(avatarAnimator, out _);
            if (humanoidBones.Count == 0)
            {
                EditorGUILayout.HelpBox(JointKeeperLocalization.Tr("diag.no_humanoid_bones"), MessageType.Info);
                return;
            }

            var ctx = AvatarKinematicContext.Build(avatarRoot.gameObject, avatarAnimator, humanoidBones);
            var aliasMap = PreviewBoneResolver.BuildAliasMap(avatarRoot.gameObject);
            var targets = JointKeeperTargetCollector.CollectTargets(comp, avatarRoot);
            if (targets.Count == 0)
            {
                EditorGUILayout.LabelField(JointKeeperLocalization.Tr("diag.no_targets"));
                return;
            }

            SkinnedMeshRenderer sampleSmr = targets[0];
            EditorGUILayout.LabelField(
                JointKeeperLocalization.Format("diag.sample_header", sampleSmr.name),
                EditorStyles.miniBoldLabel);

            var twistSamples = JointKeeperPreviewState.GetCachedTwistSamples(avatarRoot.gameObject);
            var joints = new[] { JointId.LeftElbow, JointId.RightElbow, JointId.LeftKnee, JointId.RightKnee };
            foreach (var jointId in joints)
            {
                JointConfig cfg = comp.ResolveConfig(jointId);
                bool twistEnabled = jointId.IsElbow() && comp.ResolveTwistConfig(jointId).enabled;
                if (!cfg.enabled && !twistEnabled) continue;

                if (twistEnabled)
                    DrawTwistAxisDiagnostics(jointId, twistSamples);

                Transform upper = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetUpperHumanoidBone());
                Transform lower = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetLowerHumanoidBone());
                Transform child = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetChildHumanoidBone());
                if (upper == null || lower == null) continue;

                var groups = TwistBoneClassifier.Classify(
                    jointId, upper, lower, child, sampleSmr.bones, cfg, ctx,
                    boneAliasMap: aliasMap);

                string pSrc = groups.ProximalSource != null ? groups.ProximalSource.name : "None";
                string dSrc = groups.DistalSource != null ? groups.DistalSource.name : "None";
                string depthNote = (groups.ProximalSourceIsConstraintDriven || groups.DistalSourceIsConstraintDriven)
                    ? JointKeeperLocalization.Tr("diag.source_constrained")
                    : string.Empty;

                EditorGUILayout.LabelField(
                    $"{jointId}: G_P={groups.ProximalBoneIndices.Count}, G_D={groups.DistalBoneIndices.Count} | Src=({pSrc}, {dSrc}){depthNote}",
                    EditorStyles.miniLabel);

                foreach (var warn in groups.DiagnosticWarnings)
                    EditorGUILayout.HelpBox(warn, MessageType.Warning);
            }
        }

        /// <summary>
        /// 通常表示の前腕ツイスト分散コントロール（トグル＋ひねり追従率）と推奨・警告表示 (FEATURE_ForearmTwist §1.3, §6.2)。
        /// </summary>
        private void DrawSimpleTwistControls(JointKeeperComponent comp, Animator avatarAnimator, Transform avatarRoot)
        {
            if (_leftTwist == null || _rightTwist == null) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var leftEnabled = _leftTwist.FindPropertyRelative("enabled");
            var rightEnabled = _rightTwist.FindPropertyRelative("enabled");
            bool isUnlinked = _unlinkElbow.boolValue;
            bool anyEnabled;

            if (!isUnlinked)
            {
                leftEnabled.boolValue = EditorGUILayout.ToggleLeft(
                    JointKeeperLocalization.Content("twist.title", "twist.title_tooltip"),
                    leftEnabled.boolValue,
                    EditorStyles.boldLabel);
                anyEnabled = leftEnabled.boolValue;
                if (leftEnabled.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.Slider(
                        _leftTwist.FindPropertyRelative("twistFollow"), 0f, 1f,
                        JointKeeperLocalization.Content("twist.follow", "twist.follow_tooltip"));
                    EditorGUI.indentLevel--;
                }
            }
            else
            {
                EditorGUILayout.LabelField(
                    new GUIContent(
                        JointKeeperLocalization.Format("joint.unlinked_suffix", JointKeeperLocalization.Tr("twist.title")),
                        JointKeeperLocalization.Tr("twist.title_tooltip")),
                    EditorStyles.boldLabel);
                EditorGUI.indentLevel++;

                leftEnabled.boolValue = EditorGUILayout.ToggleLeft(
                    JointKeeperLocalization.Content("joint.left", "joint.left_tooltip"),
                    leftEnabled.boolValue);
                if (leftEnabled.boolValue)
                {
                    EditorGUILayout.Slider(
                        _leftTwist.FindPropertyRelative("twistFollow"), 0f, 1f,
                        JointKeeperLocalization.Content("twist.left_follow", "twist.follow_tooltip"));
                }

                rightEnabled.boolValue = EditorGUILayout.ToggleLeft(
                    JointKeeperLocalization.Content("joint.right", "joint.right_tooltip"),
                    rightEnabled.boolValue);
                if (rightEnabled.boolValue)
                {
                    EditorGUILayout.Slider(
                        _rightTwist.FindPropertyRelative("twistFollow"), 0f, 1f,
                        JointKeeperLocalization.Content("twist.right_follow", "twist.follow_tooltip"));
                }
                anyEnabled = leftEnabled.boolValue || rightEnabled.boolValue;

                EditorGUI.indentLevel--;
            }

            if (anyEnabled)
            {
                EditorGUILayout.LabelField(JointKeeperLocalization.Tr("twist.recommend_note"), EditorStyles.wordWrappedMiniLabel);
                serializedObject.ApplyModifiedProperties();
                DrawTwistWarnings(comp, avatarAnimator, avatarRoot);
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// 対象メッシュごとの前腕ツイスト診断（Hand ウェイト・既存ツイストリグ・前腕 PhysBone）を警告表示する。
        /// </summary>
        private void DrawTwistWarnings(JointKeeperComponent comp, Animator avatarAnimator, Transform avatarRoot)
        {
            if (avatarAnimator == null || avatarRoot == null || comp.IsBaked) return;

            var handMeshes = new List<string>();
            var rigMeshes = new List<string>();
            var rigBones = new HashSet<string>();
            var pbMeshes = new List<string>();
            var pbBones = new HashSet<string>();

            Dictionary<HumanBodyBones, Transform> humanoidBones = null;
            AvatarKinematicContext ctx = null;
            Dictionary<Transform, Transform> aliasMap = null;

            var targets = JointKeeperTargetCollector.CollectTargets(comp, avatarRoot);
            foreach (var jointId in new[] { JointId.LeftElbow, JointId.RightElbow })
            {
                var twistCfg = comp.ResolveTwistConfig(jointId);
                if (!twistCfg.enabled) continue;
                JointConfig cfg = comp.ResolveConfig(jointId);

                foreach (var smr in targets)
                {
                    if (smr == null || smr.sharedMesh == null || smr.bones == null || smr.bones.Length == 0) continue;
                    var key = (smr.GetInstanceID(), smr.sharedMesh.GetInstanceID(), jointId);
                    if (!_twistDiagCache.TryGetValue(key, out var diag))
                    {
                        if (humanoidBones == null)
                        {
                            humanoidBones = HumanoidBoneResolver.ResolveAll(avatarAnimator, out _);
                            ctx = AvatarKinematicContext.Build(avatarRoot.gameObject, avatarAnimator, humanoidBones);
                            aliasMap = PreviewBoneResolver.BuildAliasMap(avatarRoot.gameObject);
                        }
                        diag = AnalyzeTwistForRenderer(smr, jointId, cfg, humanoidBones, ctx, aliasMap);
                        _twistDiagCache[key] = diag;
                    }
                    if (diag == null) continue;

                    if (diag.HandWeightVertexCount > 0 && twistCfg.twistFollow < 1f - 1e-4f && !handMeshes.Contains(smr.name))
                        handMeshes.Add(smr.name);
                    if (diag.HasExistingTwistRig && !twistCfg.applyEvenIfExistingTwistRig && !rigMeshes.Contains(smr.name))
                    {
                        rigMeshes.Add(smr.name);
                        if (diag.ExistingRigBoneName != null) rigBones.Add(diag.ExistingRigBoneName);
                    }
                    if (diag.ForearmPhysBoneVertexCount > 0 && !pbMeshes.Contains(smr.name))
                    {
                        pbMeshes.Add(smr.name);
                        foreach (var n in diag.ForearmPhysBoneNames) pbBones.Add(n);
                    }
                }
            }

            if (handMeshes.Count > 0)
                EditorGUILayout.HelpBox(JointKeeperLocalization.Format("twist.warn_hand_weights", string.Join(", ", handMeshes)), MessageType.Warning);
            if (rigMeshes.Count > 0)
                EditorGUILayout.HelpBox(JointKeeperLocalization.Format("twist.warn_existing_rig", string.Join(", ", rigMeshes), string.Join(", ", rigBones)), MessageType.Warning);
            if (pbMeshes.Count > 0)
                EditorGUILayout.HelpBox(JointKeeperLocalization.Format("twist.warn_physbone", string.Join(", ", pbMeshes), string.Join(", ", pbBones)), MessageType.Info);
        }

        private static ForearmTwistSmrDiagnostics AnalyzeTwistForRenderer(
            SkinnedMeshRenderer smr,
            JointId jointId,
            JointConfig cfg,
            Dictionary<HumanBodyBones, Transform> humanoidBones,
            AvatarKinematicContext ctx,
            Dictionary<Transform, Transform> aliasMap)
        {
            Transform upper = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetUpperHumanoidBone());
            Transform lower = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetLowerHumanoidBone());
            Transform child = HumanoidBoneResolver.GetBone(humanoidBones, jointId.GetChildHumanoidBone());
            if (upper == null || lower == null) return null;

            var groups = TwistBoneClassifier.Classify(
                jointId, upper, lower, child, smr.bones, cfg, ctx, boneAliasMap: aliasMap);
            if (groups.DistalBoneIndices.Count == 0) return null;

            BoneWeight[] weights = smr.sharedMesh.boneWeights;
            return ForearmTwistProcessor.Analyze(
                weights, smr.bones, groups, ctx,
                t => (t != null && aliasMap != null && aliasMap.TryGetValue(t, out var m)) ? m : t);
        }

        private static void DrawDetailedTwistFields(SerializedProperty prop, string header)
        {
            if (prop == null) return;
            EditorGUILayout.LabelField(header, EditorStyles.miniBoldLabel);

            EditorGUILayout.PropertyField(
                prop.FindPropertyRelative("enabled"),
                JointKeeperLocalization.Content("twist.title", "twist.title_tooltip"));
            EditorGUILayout.Slider(
                prop.FindPropertyRelative("twistFollow"), 0f, 1f,
                JointKeeperLocalization.Content("twist.follow", "twist.follow_tooltip"));
            EditorGUILayout.IntSlider(
                prop.FindPropertyRelative("segments"), 1, 4,
                JointKeeperLocalization.Content("twist.segments", "twist.segments_tooltip"));

            var startProp = prop.FindPropertyRelative("startRatio");
            var endProp = prop.FindPropertyRelative("endRatio");
            EditorGUILayout.Slider(startProp, -0.3f, 0.5f,
                JointKeeperLocalization.Content("twist.start_ratio", "twist.start_ratio_tooltip"));
            EditorGUILayout.Slider(endProp, 0.3f, 1.2f,
                JointKeeperLocalization.Content("twist.end_ratio", "twist.end_ratio_tooltip"));
            if (endProp.floatValue < startProp.floatValue + ForearmTwistConfig.MinRatioSpan)
                endProp.floatValue = startProp.floatValue + ForearmTwistConfig.MinRatioSpan;

            var curveProp = prop.FindPropertyRelative("curve");
            EditorGUILayout.PropertyField(curveProp,
                JointKeeperLocalization.Content("twist.curve", "twist.curve_tooltip"));
            if ((TwistCurve)curveProp.enumValueIndex == TwistCurve.Power)
            {
                EditorGUILayout.Slider(
                    prop.FindPropertyRelative("curvePower"), 1f, 3f,
                    JointKeeperLocalization.Content("twist.curve_power", "twist.curve_power_tooltip"));
            }

            EditorGUILayout.PropertyField(
                prop.FindPropertyRelative("bendOnlyElbowHelper"),
                JointKeeperLocalization.Content("twist.bend_only_helper", "twist.bend_only_helper_tooltip"));
            EditorGUILayout.PropertyField(
                prop.FindPropertyRelative("applyEvenIfExistingTwistRig"),
                JointKeeperLocalization.Content("twist.apply_existing", "twist.apply_existing_tooltip"));
            EditorGUILayout.Slider(
                prop.FindPropertyRelative("twistSnapThreshold"), 0f, 0.5f,
                JointKeeperLocalization.Content("twist.snap", "twist.snap_tooltip"));
        }

        private static void DrawTwistAxisDiagnostics(JointId jointId, Dictionary<JointId, TwistAxisSample> twistSamples)
        {
            bool has = twistSamples != null && twistSamples.TryGetValue(jointId, out var sample);
            TwistAxisSample s = has ? twistSamples[jointId] : default;

            string axis = has && s.HasAxis
                ? JointKeeperLocalization.Tr("twist.diag_axis_measured")
                : JointKeeperLocalization.Tr("twist.diag_axis_fallback");
            string roll = has
                ? JointKeeperLocalization.Format("twist.diag_roll_value", s.DegreesPerMuscleUnit)
                : JointKeeperLocalization.Tr("twist.diag_roll_none");
            string lat = has && s.LowerArmTwistSetting >= 0f ? s.LowerArmTwistSetting.ToString("F2") : "-";

            EditorGUILayout.LabelField(
                JointKeeperLocalization.Format("twist.diag_line", jointId, axis, roll, lat),
                EditorStyles.wordWrappedMiniLabel);

            if (has && s.DegreesPerMuscleUnit < 1f)
                EditorGUILayout.HelpBox(JointKeeperLocalization.Tr("twist.diag_roll_zero_warn"), MessageType.Warning);
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
    }
}
