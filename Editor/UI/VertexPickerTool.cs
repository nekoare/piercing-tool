using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;

namespace PiercingTool.Editor
{
    /// <summary>
    /// Scene Viewでメッシュの頂点を選択するエディタツール。
    /// ピッキング: HandleUtility.WorldToGUIPoint によるスクリーン距離比較。
    /// オクルージョン: BakeMeshから作ったMeshColliderでRaycast判定。
    /// 表示: ZTest付きGL.QUADSドットで見えている頂点のみ表示。
    /// </summary>
    public class VertexPickerTool
    {
        public SkinnedMeshRenderer targetRenderer;
        public List<int> selectedVertices;
        public Action onSelectionChanged;

        public bool isActive { get; private set; }

        private Vector3[] _cachedWorldVertices;

        // オクルージョン用MeshCollider
        private GameObject _colliderGO;
        private MeshCollider _meshCollider;
        private Mesh _colliderMesh;
        private bool[] _occlusionMask;
        private bool _occlusionDirty = true;

        // 表示用
        private Material _dotMaterial;
        private int _hoveredVertex = -1;
        private Vector2 _lastMousePosition;

        private static readonly Color DotColor = new Color(0f, 1f, 1f, 0.8f);
        private static readonly Color SelectedColor = new Color(0f, 1f, 0.5f, 1f);
        private static readonly Color HoveredColor = new Color(1f, 1f, 0f, 1f);
        private const float PickDistanceSqr = 20f * 20f;
        private const float DotScale = 0.015f;

        public void Activate()
        {
            if (targetRenderer == null || selectedVertices == null) return;
            isActive = true;
            UpdateCachedVertices();
            SetupCollider();
            SceneView.duringSceneGui += OnSceneGUI;
            SceneView.RepaintAll();
        }

        public void Deactivate()
        {
            isActive = false;
            SceneView.duringSceneGui -= OnSceneGUI;
            Cleanup();
            SceneView.RepaintAll();
        }

        private void Cleanup()
        {
            if (_dotMaterial != null)
            {
                UnityEngine.Object.DestroyImmediate(_dotMaterial);
                _dotMaterial = null;
            }
            CleanupCollider();
        }

        private void SetupCollider()
        {
            CleanupCollider();

            _colliderMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            targetRenderer.BakeMesh(_colliderMesh);

            _colliderGO = new GameObject("__PiercingTool_ZTest__")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _colliderGO.transform.SetParent(targetRenderer.transform, false);

            _meshCollider = _colliderGO.AddComponent<MeshCollider>();
            _meshCollider.hideFlags = HideFlags.HideAndDontSave;
            _meshCollider.convex = false;
            _meshCollider.cookingOptions = MeshColliderCookingOptions.None;
            _meshCollider.sharedMesh = _colliderMesh;

            _occlusionDirty = true;
        }

        private void CleanupCollider()
        {
            if (_colliderGO != null)
            {
                UnityEngine.Object.DestroyImmediate(_colliderGO);
                _colliderGO = null;
                _meshCollider = null;
            }
            if (_colliderMesh != null)
            {
                UnityEngine.Object.DestroyImmediate(_colliderMesh);
                _colliderMesh = null;
            }
            _occlusionMask = null;
        }

        private void UpdateCachedVertices()
        {
            if (targetRenderer == null) return;
            var bakedMesh = new Mesh();
            targetRenderer.BakeMesh(bakedMesh);

            var localVertices = bakedMesh.vertices;
            var rendererTransform = targetRenderer.transform;
            _cachedWorldVertices = new Vector3[localVertices.Length];
            for (int i = 0; i < localVertices.Length; i++)
                _cachedWorldVertices[i] = rendererTransform.TransformPoint(localVertices[i]);

            UnityEngine.Object.DestroyImmediate(bakedMesh);
        }

        private void EnsureMaterials()
        {
            if (_dotMaterial == null)
            {
                _dotMaterial = new Material(Shader.Find("Hidden/Internal-Colored"))
                    { hideFlags = HideFlags.HideAndDontSave };
                _dotMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                _dotMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                _dotMaterial.SetInt("_ZWrite", 0);
                _dotMaterial.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            }
        }

        /// <summary>
        /// カメラから頂点へのレイがメッシュに遮られるかを判定し、
        /// 各頂点のオクルージョンマスクを更新する。
        /// </summary>
        private void UpdateOcclusionMask(Camera camera)
        {
            if (!_occlusionDirty || _meshCollider == null) return;

            int n = _cachedWorldVertices.Length;
            if (_occlusionMask == null || _occlusionMask.Length != n)
                _occlusionMask = new bool[n];

            bool prevHitBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var wp = _cachedWorldVertices[i];
                    var sp = HandleUtility.WorldToGUIPoint(wp);
                    var ray = HandleUtility.GUIPointToWorldRay(sp);

                    // レイ上での頂点までの距離
                    float t = Vector3.Dot(wp - ray.origin, ray.direction);
                    if (t <= 0f)
                    {
                        _occlusionMask[i] = false;
                        continue;
                    }

                    // 頂点自身のトライアングルに当たらないようにε分手前まで判定
                    float eps = Mathf.Max(HandleUtility.GetHandleSize(wp) * 0.005f, 0.0005f);
                    float maxDist = Mathf.Max(0f, t - eps);

                    _occlusionMask[i] = _meshCollider.Raycast(ray, out _, maxDist);
                }
            }
            finally
            {
                Physics.queriesHitBackfaces = prevHitBackfaces;
            }

            _occlusionDirty = false;
        }

        private void OnSceneGUI(SceneView sceneView)
        {
            if (!isActive || _cachedWorldVertices == null) return;

            var e = Event.current;
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            HandleUtility.AddDefaultControl(controlId);

            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
            {
                _lastMousePosition = e.mousePosition;
                _occlusionDirty = true;
                sceneView.Repaint();
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (_hoveredVertex >= 0)
                {
                    if (e.shift)
                        selectedVertices.Remove(_hoveredVertex);
                    else if (!selectedVertices.Contains(_hoveredVertex))
                        selectedVertices.Add(_hoveredVertex);
                    onSelectionChanged?.Invoke();
                    e.Use();
                }
            }

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                Deactivate();
                e.Use();
                return;
            }

            if (e.type == EventType.Repaint)
            {
                EnsureMaterials();

                // オクルージョン判定を更新
                UpdateOcclusionMask(sceneView.camera);

                // スクリーン距離でホバー頂点を判定
                _hoveredVertex = PickNearestVertex(sceneView.camera);

                // 表示用ドット描画（シーンRTに直接、ZTest付き）
                DrawVertexDots(sceneView.camera);
                DrawHighlights(sceneView.camera);
            }

            // HUD
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

        /// <summary>
        /// HandleUtility.WorldToGUIPointでスクリーン座標に変換し、
        /// マウスに最も近い非遮蔽頂点を見つける。
        /// </summary>
        private int PickNearestVertex(Camera camera)
        {
            float bestDist = PickDistanceSqr;
            int bestIndex = -1;

            for (int i = 0; i < _cachedWorldVertices.Length; i++)
            {
                // オクルージョンされた頂点はスキップ
                if (_occlusionMask != null && i < _occlusionMask.Length && _occlusionMask[i])
                    continue;

                Vector2 sp = HandleUtility.WorldToGUIPoint(_cachedWorldVertices[i]);
                float dist = (sp - _lastMousePosition).sqrMagnitude;

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private void DrawVertexDots(Camera camera)
        {
            _dotMaterial.SetPass(0);

            var camRight = camera.transform.right;
            var camUp = camera.transform.up;

            GL.Begin(GL.QUADS);
            GL.Color(DotColor);

            for (int i = 0; i < _cachedWorldVertices.Length; i++)
            {
                var pos = _cachedWorldVertices[i];
                float size = HandleUtility.GetHandleSize(pos) * DotScale;

                GL.Vertex(pos - camRight * size - camUp * size);
                GL.Vertex(pos + camRight * size - camUp * size);
                GL.Vertex(pos + camRight * size + camUp * size);
                GL.Vertex(pos - camRight * size + camUp * size);
            }

            GL.End();
        }

        private void DrawHighlights(Camera camera)
        {
            var cameraForward = camera.transform.forward;

            Handles.color = SelectedColor;
            foreach (int vi in selectedVertices)
            {
                if (vi >= 0 && vi < _cachedWorldVertices.Length)
                {
                    float size = HandleUtility.GetHandleSize(_cachedWorldVertices[vi]) * 0.02f;
                    Handles.DrawSolidDisc(_cachedWorldVertices[vi], cameraForward, size);
                }
            }

            if (_hoveredVertex >= 0 && _hoveredVertex < _cachedWorldVertices.Length)
            {
                Handles.color = HoveredColor;
                float size = HandleUtility.GetHandleSize(_cachedWorldVertices[_hoveredVertex]) * 0.02f;
                Handles.DrawWireDisc(_cachedWorldVertices[_hoveredVertex], cameraForward, size * 1.5f);

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
