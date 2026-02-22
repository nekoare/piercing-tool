using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace PiercingTool.Editor
{
    /// <summary>
    /// Scene Viewでメッシュの頂点を選択するエディタツール。
    /// </summary>
    public class VertexPickerTool
    {
        public SkinnedMeshRenderer targetRenderer;
        public List<int> selectedVertices;
        public Action onSelectionChanged;

        public bool isActive { get; private set; }

        private Vector3[] _cachedWorldVertices;
        private int _hoveredVertex = -1;
        private Vector2 _lastMousePosition;

        private static readonly Color SelectedColor = new Color(0f, 1f, 0.5f, 1f);
        private static readonly Color HoveredColor = new Color(1f, 1f, 0f, 1f);
        private const float PickDistancePixels = 20f;

        public void Activate()
        {
            if (targetRenderer == null || selectedVertices == null) return;
            isActive = true;
            UpdateCachedVertices();
            SceneView.duringSceneGui += OnSceneGUI;
            SceneView.RepaintAll();
        }

        public void Deactivate()
        {
            isActive = false;
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.RepaintAll();
        }

        private void UpdateCachedVertices()
        {
            if (targetRenderer == null) return;
            var bakedMesh = new Mesh();
            targetRenderer.BakeMesh(bakedMesh);

            var localVertices = bakedMesh.vertices;
            var transform = targetRenderer.transform;
            _cachedWorldVertices = new Vector3[localVertices.Length];
            for (int i = 0; i < localVertices.Length; i++)
                _cachedWorldVertices[i] = transform.TransformPoint(localVertices[i]);

            UnityEngine.Object.DestroyImmediate(bakedMesh);
        }

        private void OnSceneGUI(SceneView sceneView)
        {
            if (!isActive || _cachedWorldVertices == null) return;

            var e = Event.current;
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            HandleUtility.AddDefaultControl(controlId);

            // マウス位置が変わったら最近傍頂点を更新
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
            {
                _lastMousePosition = e.mousePosition;
                _hoveredVertex = FindNearestVertex(_lastMousePosition);
                sceneView.Repaint();
            }

            // クリック処理
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (_hoveredVertex >= 0)
                {
                    if (e.shift)
                    {
                        selectedVertices.Remove(_hoveredVertex);
                    }
                    else
                    {
                        if (!selectedVertices.Contains(_hoveredVertex))
                            selectedVertices.Add(_hoveredVertex);
                    }

                    onSelectionChanged?.Invoke();
                    e.Use();
                }
            }

            // Escapeで終了
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                Deactivate();
                e.Use();
                return;
            }

            // 頂点描画
            DrawVertices(sceneView.camera);

            // HUDラベル表示
            Handles.BeginGUI();
            var rect = new Rect(10, 10, 320, 60);
            EditorGUI.DrawRect(rect, new Color(0, 0, 0, 0.7f));
            var style = new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = Color.white },
                fontSize = 12
            };
            GUI.Label(new Rect(15, 14, 310, 20),
                $"頂点選択モード — 選択数: {selectedVertices.Count}", style);
            GUI.Label(new Rect(15, 34, 310, 20),
                "クリック: 追加 / Shift+クリック: 解除 / Esc: 終了", style);
            Handles.EndGUI();
        }

        private int FindNearestVertex(Vector2 mousePos)
        {
            float minDist = PickDistancePixels;
            int nearest = -1;

            for (int i = 0; i < _cachedWorldVertices.Length; i++)
            {
                var screenPos = HandleUtility.WorldToGUIPoint(_cachedWorldVertices[i]);
                float dist = Vector2.Distance(mousePos, screenPos);
                if (dist < minDist)
                {
                    minDist = dist;
                    nearest = i;
                }
            }

            return nearest;
        }

        private void DrawVertices(Camera camera)
        {
            var cameraForward = camera.transform.forward;

            // 選択済み頂点
            Handles.color = SelectedColor;
            foreach (int vi in selectedVertices)
            {
                if (vi >= 0 && vi < _cachedWorldVertices.Length)
                {
                    float size = HandleUtility.GetHandleSize(_cachedWorldVertices[vi]) * 0.02f;
                    Handles.DrawSolidDisc(_cachedWorldVertices[vi], cameraForward, size);
                }
            }

            // ホバー頂点
            if (_hoveredVertex >= 0 && _hoveredVertex < _cachedWorldVertices.Length)
            {
                Handles.color = HoveredColor;
                float size = HandleUtility.GetHandleSize(_cachedWorldVertices[_hoveredVertex]) * 0.02f;
                Handles.DrawWireDisc(_cachedWorldVertices[_hoveredVertex], cameraForward, size * 1.5f);

                // 頂点インデックス表示
                var labelStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    normal = { textColor = HoveredColor },
                    fontSize = 11
                };
                Handles.Label(
                    _cachedWorldVertices[_hoveredVertex] + camera.transform.up * size * 3,
                    $"#{_hoveredVertex}", labelStyle);
            }
        }
    }
}
