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
            SceneView.duringSceneGui += DrawSceneVisualization;
        }

        private void OnDisable()
        {
            _pickerTool?.Deactivate();
            SceneView.duringSceneGui -= DrawSceneVisualization;
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

            // --- 位置を保存ボタン ---
            using (new EditorGUI.DisabledScope(!IsReadyToGenerate(setup)))
            {
                string buttonLabel = setup.isPositionSaved ? "位置を保存（保存済み）" : "位置を保存";
                if (GUILayout.Button(buttonLabel, GUILayout.Height(30)))
                {
                    try
                    {
                        SavePosition(setup);
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
                return true; // 参照頂点が空の場合は自動選択される
            else
                return setup.pointAVertices.Count > 0 && setup.pointBVertices.Count > 0;
        }

        private void SavePosition(PiercingSetup setup)
        {
            if (setup.targetRenderer == null)
                throw new System.InvalidOperationException("対象Rendererが設定されていません。");

            Undo.RecordObject(setup, "Save piercing position");

            // 参照頂点が未指定の場合、現在のBlendShape状態で自動選択して保存
            // （ビルド時のBlendShape状態に依存しないようにするため）
            if (setup.mode == PiercingMode.Single && setup.referenceVertices.Count == 0)
            {
                var autoSelected = MeshGenerator.FindClosestTriangleVertices(
                    setup.targetRenderer, setup.transform.position);
                setup.referenceVertices.AddRange(autoSelected);
                Debug.Log($"[PiercingTool] 参照頂点を自動選択しました: {string.Join(", ", autoSelected)}");
            }

            // BlendShape weightsスナップショットを保存
            var smr = setup.targetRenderer;
            int count = smr.sharedMesh != null ? smr.sharedMesh.blendShapeCount : 0;
            setup.savedBlendShapeWeights = new float[count];
            for (int i = 0; i < count; i++)
                setup.savedBlendShapeWeights[i] = smr.GetBlendShapeWeight(i);

            setup.isPositionSaved = true;
            EditorUtility.SetDirty(setup);

            Debug.Log($"[PiercingTool] 位置を保存しました（BlendShape weights: {count}個）。");
        }

        // =================================================================
        // SceneView 参照頂点の可視化
        // =================================================================

        private static readonly Color ColorGood = new Color(0f, 1f, 0.5f, 1f);
        private static readonly Color ColorDegenerate = new Color(1f, 0.4f, 0f, 1f);
        private static readonly Color ColorAutoSelect = new Color(1f, 0.85f, 0f, 1f);
        private static readonly Color ColorPointA = new Color(0f, 0.8f, 1f, 1f);
        private static readonly Color ColorPointB = new Color(1f, 0.5f, 0.8f, 1f);
        private static readonly Color ColorNormal = new Color(0.3f, 0.5f, 1f, 0.9f);

        private void DrawSceneVisualization(SceneView sceneView)
        {
            var setup = target as PiercingSetup;
            if (setup == null || setup.targetRenderer == null) return;

            var worldVertices = BakeWorldVertices(setup.targetRenderer);
            if (worldVertices == null) return;

            if (setup.mode == PiercingMode.Single)
            {
                if (setup.referenceVertices.Count > 0)
                {
                    DrawVertexGroup(setup.referenceVertices, worldVertices, sceneView);
                }
                else
                {
                    // 自動選択のプレビュー
                    var triangles = setup.targetRenderer.sharedMesh.triangles;
                    var autoIndices = FindClosestTriangle(
                        worldVertices, triangles, setup.transform.position);
                    DrawVertexGroup(
                        new List<int>(autoIndices), worldVertices, sceneView,
                        ColorAutoSelect, "auto");
                }
            }
            else
            {
                DrawVertexGroup(setup.pointAVertices, worldVertices, sceneView, ColorPointA, "A");
                DrawVertexGroup(setup.pointBVertices, worldVertices, sceneView, ColorPointB, "B");
            }
        }

        private static Vector3[] BakeWorldVertices(SkinnedMeshRenderer renderer)
        {
            if (renderer == null || renderer.sharedMesh == null) return null;

            var bakedMesh = new Mesh();
            renderer.BakeMesh(bakedMesh);
            var localVerts = bakedMesh.vertices;
            var worldVerts = new Vector3[localVerts.Length];
            var transform = renderer.transform;
            for (int i = 0; i < localVerts.Length; i++)
                worldVerts[i] = transform.TransformPoint(localVerts[i]);
            Object.DestroyImmediate(bakedMesh);
            return worldVerts;
        }

        /// <summary>
        /// ワールド座標の頂点配列から、targetPosに最も近い三角面の3頂点インデックスを返す。
        /// </summary>
        private static int[] FindClosestTriangle(Vector3[] worldVertices, int[] triangles, Vector3 targetPos)
        {
            // 最も近い頂点を見つける
            float minDistSq = float.MaxValue;
            int closestVertex = 0;
            for (int i = 0; i < worldVertices.Length; i++)
            {
                float distSq = (worldVertices[i] - targetPos).sqrMagnitude;
                if (distSq < minDistSq)
                {
                    minDistSq = distSq;
                    closestVertex = i;
                }
            }

            // その頂点を共有する三角面から、法線がターゲット方向を向いている最良の面を選ぶ
            float bestScore = float.NegativeInfinity;
            int bestTriStart = -1;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int i0 = triangles[i], i1 = triangles[i + 1], i2 = triangles[i + 2];
                if (i0 != closestVertex && i1 != closestVertex && i2 != closestVertex)
                    continue;

                Vector3 v0 = worldVertices[i0], v1 = worldVertices[i1], v2 = worldVertices[i2];
                Vector3 normal = Vector3.Cross(v1 - v0, v2 - v0).normalized;
                Vector3 centroid = (v0 + v1 + v2) / 3f;
                Vector3 toTarget = (targetPos - centroid).normalized;
                float score = Vector3.Dot(normal, toTarget);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestTriStart = i;
                }
            }

            if (bestTriStart < 0)
                return new int[] { closestVertex };

            return new int[] { triangles[bestTriStart], triangles[bestTriStart + 1], triangles[bestTriStart + 2] };
        }

        private static void DrawVertexGroup(
            List<int> vertices, Vector3[] worldVertices, SceneView sceneView,
            Color? baseColor = null, string label = null)
        {
            if (vertices.Count == 0) return;

            // ワールド座標を取得
            var positions = new List<Vector3>();
            var indices = new List<int>();
            for (int i = 0; i < vertices.Count; i++)
            {
                int vi = vertices[i];
                if (vi >= 0 && vi < worldVertices.Length)
                {
                    positions.Add(worldVertices[vi]);
                    indices.Add(vi);
                }
            }
            if (positions.Count == 0) return;

            var cameraForward = sceneView.camera.transform.forward;
            bool isDegenerate = false;
            Color color = baseColor ?? ColorGood;

            // 三角形の品質チェック＋描画
            if (positions.Count >= 3)
            {
                var edge1 = positions[1] - positions[0];
                var edge2 = positions[2] - positions[0];
                var normal = Vector3.Cross(edge1, edge2);
                float area = normal.magnitude * 0.5f;
                float minEdge = Mathf.Min(
                    edge1.magnitude,
                    edge2.magnitude,
                    (positions[2] - positions[1]).magnitude);

                isDegenerate = area < 0.00001f || minEdge < 0.0001f;

                if (baseColor == null)
                    color = isDegenerate ? ColorDegenerate : ColorGood;

                // 半透明の三角形面
                var faceColor = new Color(color.r, color.g, color.b, 0.15f);
                Handles.color = faceColor;
                Handles.DrawAAConvexPolygon(positions[0], positions[1], positions[2]);

                // エッジライン
                var lineColor = new Color(color.r, color.g, color.b, 0.8f);
                Handles.color = lineColor;
                Handles.DrawLine(positions[0], positions[1]);
                Handles.DrawLine(positions[1], positions[2]);
                Handles.DrawLine(positions[2], positions[0]);

                // 法線矢印
                if (!isDegenerate)
                {
                    var centroid = (positions[0] + positions[1] + positions[2]) / 3f;
                    float arrowLen = HandleUtility.GetHandleSize(centroid) * 0.12f;
                    Handles.color = ColorNormal;
                    Handles.DrawLine(centroid, centroid + normal.normalized * arrowLen);
                }
            }
            else if (positions.Count == 2)
            {
                var lineColor = new Color(color.r, color.g, color.b, 0.8f);
                Handles.color = lineColor;
                Handles.DrawLine(positions[0], positions[1]);
            }

            // 頂点ドット + インデックスラベル
            var dotColor = new Color(color.r, color.g, color.b, 1f);
            Handles.color = dotColor;
            var labelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = dotColor },
                fontSize = 10
            };

            for (int i = 0; i < positions.Count; i++)
            {
                float size = HandleUtility.GetHandleSize(positions[i]) * 0.012f;
                Handles.DrawSolidDisc(positions[i], cameraForward, size);

                string indexLabel = label != null
                    ? $"{label}:{indices[i]}"
                    : $"#{indices[i]}";
                Handles.Label(
                    positions[i] + sceneView.camera.transform.up * size * 4,
                    indexLabel, labelStyle);
            }
        }

        /// <summary>
        /// 頂点群の三角形品質を評価する（ベースメッシュ座標で判定）。
        /// </summary>
        private static VertexQuality EvaluateVertexQuality(
            Vector3[] sourceVertices, List<int> vertices)
        {
            if (vertices.Count < 2) return VertexQuality.Ok;

            // 最短辺の長さチェック
            float minDist = float.MaxValue;
            for (int i = 0; i < vertices.Count; i++)
            {
                for (int j = i + 1; j < vertices.Count; j++)
                {
                    int vi = vertices[i], vj = vertices[j];
                    if (vi < sourceVertices.Length && vj < sourceVertices.Length)
                    {
                        float dist = Vector3.Distance(sourceVertices[vi], sourceVertices[vj]);
                        minDist = Mathf.Min(minDist, dist);
                    }
                }
            }
            if (minDist < 0.001f)
                return VertexQuality.TooClose;

            // 共線性チェック（3頂点以上）
            if (vertices.Count >= 3)
            {
                int i0 = vertices[0], i1 = vertices[1], i2 = vertices[2];
                if (i0 < sourceVertices.Length && i1 < sourceVertices.Length && i2 < sourceVertices.Length)
                {
                    var edge1 = sourceVertices[i1] - sourceVertices[i0];
                    var edge2 = sourceVertices[i2] - sourceVertices[i0];
                    float area = Vector3.Cross(edge1, edge2).magnitude * 0.5f;
                    if (area < 0.000001f)
                        return VertexQuality.Collinear;
                }
            }

            return VertexQuality.Ok;
        }

        private enum VertexQuality { Ok, TooClose, Collinear }

        // =================================================================
        // バリデーション
        // =================================================================

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

            if (setup.mode == PiercingMode.Single)
            {
                if (setup.referenceVertices.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "参照頂点が未指定のため、ピアス位置から最も近い三角面が自動選択されます。\n" +
                        "意図と異なる場合は「頂点を選択」で手動指定してください。",
                        MessageType.Info);
                }
                else if (setup.referenceVertices.Count < 3)
                {
                    EditorGUILayout.HelpBox(
                        "参照頂点が3つ未満のため、回転追従が制限されます。\n" +
                        "1頂点: 位置のみ / 2頂点: 軸回転のみ / 3頂点以上: 完全な回転追従",
                        MessageType.Info);
                }
            }

            // 頂点品質チェック
            if (setup.targetRenderer != null && setup.targetRenderer.sharedMesh != null)
            {
                var sourceVerts = setup.targetRenderer.sharedMesh.vertices;

                if (setup.mode == PiercingMode.Single && setup.referenceVertices.Count >= 2)
                {
                    var quality = EvaluateVertexQuality(sourceVerts, setup.referenceVertices);
                    DrawVertexQualityWarning(quality);
                }
                else if (setup.mode == PiercingMode.Chain)
                {
                    if (setup.pointAVertices.Count >= 2)
                    {
                        var qa = EvaluateVertexQuality(sourceVerts, setup.pointAVertices);
                        if (qa != VertexQuality.Ok)
                        {
                            EditorGUILayout.HelpBox(
                                $"Point A: {GetQualityMessage(qa)}", MessageType.Warning);
                        }
                    }
                    if (setup.pointBVertices.Count >= 2)
                    {
                        var qb = EvaluateVertexQuality(sourceVerts, setup.pointBVertices);
                        if (qb != VertexQuality.Ok)
                        {
                            EditorGUILayout.HelpBox(
                                $"Point B: {GetQualityMessage(qb)}", MessageType.Warning);
                        }
                    }
                }
            }
        }

        private static void DrawVertexQualityWarning(VertexQuality quality)
        {
            if (quality == VertexQuality.Ok) return;
            EditorGUILayout.HelpBox(GetQualityMessage(quality), MessageType.Warning);
        }

        private static string GetQualityMessage(VertexQuality quality)
        {
            switch (quality)
            {
                case VertexQuality.TooClose:
                    return "参照頂点が近すぎます。回転追従が不安定になります。\n" +
                           "ピアスを付ける面の上で、三角形を作るように離れた頂点を選んでください。";
                case VertexQuality.Collinear:
                    return "参照頂点が直線上に並んでいます。\n" +
                           "三角形を作るように3点目を別の方向に選んでください。";
                default:
                    return string.Empty;
            }
        }
    }
}
