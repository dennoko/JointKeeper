using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// ヒエラルキー右クリックメニュー: dennokoworks → JointKeeper (§7.1)
    /// およびコンポーネント新規追加時の自動折りたたみ処理。
    /// 基本は「コンポーネントを付けるだけ」の運用を想定し、追加直後はコンパクトな折りたたみ状態にする。
    /// </summary>
    [InitializeOnLoad]
    internal static class JointKeeperMenu
    {
        private const string MenuPath = "GameObject/dennokoworks/JointKeeper";

        static JointKeeperMenu()
        {
            ObjectFactory.componentWasAdded -= OnComponentWasAdded;
            ObjectFactory.componentWasAdded += OnComponentWasAdded;
        }

        private static void OnComponentWasAdded(Component comp)
        {
            if (comp is JointKeeperComponent jk)
            {
                CollapseInspectorDeferred(jk);
            }
        }

        internal static void CollapseInspectorDeferred(JointKeeperComponent comp)
        {
            if (comp == null) return;
            ApplyCollapsedState(comp);

            // Inspector の ActiveEditorTracker 再構築後にも確実に折りたたみ状態を反映する
            EditorApplication.delayCall += () =>
            {
                if (comp != null)
                    ApplyCollapsedState(comp);
            };
        }

        private static void ApplyCollapsedState(JointKeeperComponent comp)
        {
            if (comp == null) return;
            InternalEditorUtility.SetIsInspectorExpanded(comp, false);

            var tracker = ActiveEditorTracker.sharedTracker;
            if (tracker != null)
            {
                var editors = tracker.activeEditors;
                bool matched = false;
                for (int i = 0; i < editors.Length; i++)
                {
                    if (editors[i] != null && editors[i].target == comp)
                    {
                        tracker.SetVisible(i, 0);
                        matched = true;
                    }
                }
                if (matched)
                {
                    InternalEditorUtility.RepaintAllViews();
                }
                else
                {
                    tracker.ForceRebuild();
                }
            }
        }

        [MenuItem(MenuPath, false, 10)]
        private static void AddJointKeeperComponent(MenuCommand command)
        {
            // 複数選択時、Unity は選択数だけ MenuItem を呼ぶため command.context 単位で処理する
            GameObject target = command.context as GameObject;
            if (target == null)
                target = Selection.activeGameObject;

            if (target == null)
                return;

            if (target.GetComponent<JointKeeperComponent>() != null)
                return;

            var added = Undo.AddComponent<JointKeeperComponent>(target);
            CollapseInspectorDeferred(added);
            Selection.activeGameObject = target;
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateAddJointKeeperComponent()
        {
            GameObject[] selected = Selection.gameObjects;
            if (selected == null || selected.Length == 0)
                return false;

            for (int i = 0; i < selected.Length; i++)
            {
                if (selected[i] != null && selected[i].GetComponent<JointKeeperComponent>() == null)
                    return true;
            }
            return false;
        }
    }
}
