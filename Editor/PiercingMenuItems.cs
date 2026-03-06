using UnityEngine;
using UnityEditor;

namespace PiercingTool.Editor
{
    internal static class PiercingMenuItems
    {
        [MenuItem("GameObject/VRCぴあっさ～追加", false, 10)]
        private static void AddPiercingSetup(MenuCommand menuCommand)
        {
            var go = menuCommand.context as GameObject;
            if (go == null) return;

            if (go.GetComponent<PiercingSetup>() != null)
            {
                Debug.LogWarning("[PiercingTool] このGameObjectには既にVRCぴあっさ～があります。");
                return;
            }

            Undo.AddComponent<PiercingSetup>(go);
        }

        [MenuItem("GameObject/VRCぴあっさ～追加", true)]
        private static bool ValidateAddPiercingSetup()
        {
            return Selection.activeGameObject != null;
        }
    }
}
