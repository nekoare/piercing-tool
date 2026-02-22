using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace PiercingTool.Editor
{
    [CustomEditor(typeof(PiercingSetup))]
    public class PiercingSetupEditor : UnityEditor.Editor
    {
        private SerializedProperty _mode;
        private SerializedProperty _targetRenderer;
        private SerializedProperty _skipBoneWeightTransfer;

        private VertexPickerTool _pickerTool;

        private enum PickerTarget { Single, PointA, PointB }
        private PickerTarget _activePickerTarget;

        private void OnEnable()
        {
            _mode = serializedObject.FindProperty("mode");
            _targetRenderer = serializedObject.FindProperty("targetRenderer");
            _skipBoneWeightTransfer = serializedObject.FindProperty("skipBoneWeightTransfer");
        }

        private void OnDisable()
        {
            _pickerTool?.Deactivate();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var setup = (PiercingSetup)target;

            // --- モード ---
            EditorGUILayout.PropertyField(_mode, new GUIContent("モード"));
            EditorGUILayout.Space();

            // --- Target Renderer ---
            EditorGUILayout.PropertyField(_targetRenderer, new GUIContent("対象Renderer"));
            EditorGUILayout.Space();

            // --- 頂点選択 ---
            if (setup.mode == PiercingMode.Single)
            {
                DrawVertexSection("参照頂点", setup.referenceVertices, PickerTarget.Single, setup);
            }
            else
            {
                DrawVertexSection("Point A", setup.pointAVertices, PickerTarget.PointA, setup);
                EditorGUILayout.Space();
                DrawVertexSection("Point B", setup.pointBVertices, PickerTarget.PointB, setup);
                EditorGUILayout.Space();
                EditorGUILayout.PropertyField(_skipBoneWeightTransfer,
                    new GUIContent("ボーンウェイト転写をスキップ",
                        "PhysBone設定済みの場合にチェック"));
            }

            EditorGUILayout.Space(10);

            // --- Generate Mesh ボタン ---
            using (new EditorGUI.DisabledScope(!IsReadyToGenerate(setup)))
            {
                if (GUILayout.Button("メッシュを生成", GUILayout.Height(30)))
                {
                    try
                    {
                        var mesh = MeshGenerator.Generate(setup);
                        var path = MeshGenerator.SaveMeshAsset(mesh);
                        if (path != null)
                        {
                            ApplyGeneratedMesh(setup, mesh);
                        }
                    }
                    catch (System.Exception e)
                    {
                        EditorUtility.DisplayDialog("エラー", e.Message, "OK");
                        Debug.LogException(e);
                    }
                }
            }

            // --- バリデーションメッセージ ---
            DrawValidationMessages(setup);

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawVertexSection(string label, List<int> vertices,
            PickerTarget pickerTarget, PiercingSetup setup)
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField($"選択数: {vertices.Count}");

                // 選択頂点リスト
                if (vertices.Count > 0 && setup.targetRenderer != null &&
                    setup.targetRenderer.sharedMesh != null)
                {
                    var sourceVertices = setup.targetRenderer.sharedMesh.vertices;
                    int removeIndex = -1;

                    for (int i = 0; i < vertices.Count; i++)
                    {
                        int vi = vertices[i];
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            string posStr = vi < sourceVertices.Length
                                ? sourceVertices[vi].ToString("F3")
                                : "(invalid)";
                            EditorGUILayout.LabelField($"  #{vi}  {posStr}");
                            if (GUILayout.Button("\u00d7", GUILayout.Width(22)))
                                removeIndex = i;
                        }
                    }

                    if (removeIndex >= 0)
                    {
                        Undo.RecordObject(setup, "Remove vertex");
                        vertices.RemoveAt(removeIndex);
                        EditorUtility.SetDirty(setup);
                    }
                }

                // ピッカーボタン
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool isThisPickerActive = _pickerTool != null &&
                                              _pickerTool.isActive &&
                                              _activePickerTarget == pickerTarget;

                    string buttonText = isThisPickerActive ? "選択中..." : "頂点を選択";
                    var buttonStyle = isThisPickerActive
                        ? new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold }
                        : GUI.skin.button;

                    if (GUILayout.Button(buttonText, buttonStyle))
                    {
                        if (isThisPickerActive)
                        {
                            _pickerTool.Deactivate();
                        }
                        else
                        {
                            StartPicker(setup, vertices, pickerTarget);
                        }
                    }

                    if (GUILayout.Button("クリア", GUILayout.Width(50)))
                    {
                        Undo.RecordObject(setup, "Clear vertices");
                        vertices.Clear();
                        EditorUtility.SetDirty(setup);
                    }
                }
            }
        }

        private void StartPicker(PiercingSetup setup, List<int> vertices, PickerTarget pickerTarget)
        {
            _pickerTool?.Deactivate();

            if (setup.targetRenderer == null)
            {
                EditorUtility.DisplayDialog("エラー", "対象Rendererを設定してください。", "OK");
                return;
            }

            _activePickerTarget = pickerTarget;
            _pickerTool = new VertexPickerTool
            {
                targetRenderer = setup.targetRenderer,
                selectedVertices = vertices,
                onSelectionChanged = () =>
                {
                    Undo.RecordObject(setup, "Select vertex");
                    EditorUtility.SetDirty(setup);
                    Repaint();
                }
            };
            _pickerTool.Activate();
        }

        private bool IsReadyToGenerate(PiercingSetup setup)
        {
            if (setup.targetRenderer == null) return false;
            if (setup.targetRenderer.sharedMesh == null) return false;

            var smr = setup.GetComponent<SkinnedMeshRenderer>();
            var mf = setup.GetComponent<MeshFilter>();
            if (smr == null && mf == null) return false;
            if (smr != null && smr.sharedMesh == null) return false;
            if (mf != null && mf.sharedMesh == null) return false;

            if (setup.mode == PiercingMode.Single)
                return setup.referenceVertices.Count > 0;
            else
                return setup.pointAVertices.Count > 0 && setup.pointBVertices.Count > 0;
        }

        /// <summary>
        /// 生成されたメッシュをSkinnedMeshRendererに適用し、bones/rootBoneも自動設定する。
        /// </summary>
        private void ApplyGeneratedMesh(PiercingSetup setup, Mesh mesh)
        {
            var smr = setup.GetComponent<SkinnedMeshRenderer>();
            if (smr != null)
            {
                Undo.RecordObject(smr, "Apply generated piercing mesh");
                smr.sharedMesh = mesh;

                if (!setup.skipBoneWeightTransfer)
                {
                    smr.bones = setup.targetRenderer.bones;
                    smr.rootBone = setup.targetRenderer.rootBone;
                }

                EditorUtility.SetDirty(smr);
                Debug.Log("[PiercingTool] SkinnedMeshRendererにメッシュ・bones・rootBoneを自動設定しました。");
            }
        }

        private void DrawValidationMessages(PiercingSetup setup)
        {
            if (setup.targetRenderer == null)
            {
                EditorGUILayout.HelpBox("対象Rendererを設定してください。", MessageType.Info);
                return;
            }

            var smr = setup.GetComponent<SkinnedMeshRenderer>();
            var mf = setup.GetComponent<MeshFilter>();
            if (smr == null && mf == null)
            {
                EditorGUILayout.HelpBox(
                    "このGameObjectにSkinnedMeshRendererまたはMeshFilterが必要です。\n" +
                    "ピアスのメッシュをこのGameObjectに設定してください。",
                    MessageType.Warning);
            }

            if (setup.mode == PiercingMode.Single && setup.referenceVertices.Count > 0 &&
                setup.referenceVertices.Count < 3)
            {
                EditorGUILayout.HelpBox(
                    "参照頂点が3つ未満のため、回転追従が制限されます。\n" +
                    "1頂点: 位置のみ / 2頂点: 軸回転のみ / 3頂点以上: 完全な回転追従",
                    MessageType.Info);
            }
        }
    }
}
