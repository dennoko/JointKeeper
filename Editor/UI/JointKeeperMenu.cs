using UnityEditor;
using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// ヒエラルキー右クリックメニュー: dennokoworks → JointKeeper (§7.1)。
    /// 選択中のオブジェクト（複数選択対応）に JointKeeperComponent を付与する。
    /// </summary>
    internal static class JointKeeperMenu
    {
        private const string MenuPath = "GameObject/dennokoworks/JointKeeper";

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

            Undo.AddComponent<JointKeeperComponent>(target);
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
