using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace PiercingTool.Editor
{
    public partial class PiercingSetupEditor
    {
        // =================================================================
        // SceneView ビジュアライゼーション
        // =================================================================

        private static readonly Color ColorGood = new Color(0f, 1f, 0.5f, 1f);
        private static readonly Color ColorDegenerate = new Color(1f, 0.4f, 0f, 1f);
        private static readonly Color ColorAutoSelect = new Color(1f, 0.85f, 0f, 1f);
        private static readonly Color ColorNormal = new Color(0.3f, 0.5f, 1f, 0.9f);

        private static readonly Color[] AnchorColors = new Color[]
        {
            new Color(0f, 0.8f, 1f, 1f),   // 青
            new Color(1f, 0.5f, 0.8f, 1f), // ピンク
            new Color(0.3f, 1f, 0.3f, 1f), // 緑
            new Color(1f, 0.8f, 0f, 1f),   // 黄
            new Color(0.8f, 0.4f, 1f, 1f), // 紫
            new Color(1f, 0.5f, 0.2f, 1f), // オレンジ
            new Color(0f, 1f, 0.8f, 1f),   // シアン
            new Color(1f, 0.3f, 0.3f, 1f), // 赤
        };

        // BakeWorldVertices キャッシュ（同一フレーム内の重複BakeMeshを防止）
        private static int s_bakeCache_rendererId;
        private static int s_bakeCache_frame = -1;
        private static Vector3[] s_bakeCache_result;

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
                else if (_autoDetectedVertices != null)
                {
                    DrawVertexGroup(
                        new List<int>(_autoDetectedVertices), worldVertices, sceneView,
                        ColorAutoSelect, "auto");
                }
            }
            else // Chain / MultiAnchor
            {
                var anchors = setup.anchors;
                if (anchors == null || anchors.Count == 0) return;

                // 各アンカーの target 頂点を色分け表示
                for (int i = 0; i < anchors.Count; i++)
                {
                    var color = AnchorColors[i % AnchorColors.Length];
                    string label = setup.mode == PiercingMode.Chain
                        ? (i == 0 ? "A" : "B")
                        : $"{i + 1}";
                    DrawVertexGroup(anchors[i].targetVertices, worldVertices, sceneView, color, label);
                }

                // アンカー間をラインで接続（target 側の重心同士）
                if (anchors.Count >= 2)
                {
                    var lineColor = new Color(1f, 1f, 1f, 0.4f);
                    Handles.color = lineColor;
                    for (int i = 0; i < anchors.Count - 1; i++)
                    {
                        var c0 = ComputeVertexGroupCentroid(anchors[i].targetVertices, worldVertices);
                        var c1 = ComputeVertexGroupCentroid(anchors[i + 1].targetVertices, worldVertices);
                        if (c0.HasValue && c1.HasValue)
                            Handles.DrawDottedLine(c0.Value, c1.Value, 4f);
                    }
                }
            }
        }

        private static Vector3[] BakeWorldVertices(SkinnedMeshRenderer renderer)
        {
            if (renderer == null || renderer.sharedMesh == null) return null;

            int id = renderer.GetInstanceID();
            int frame = UnityEngine.Time.frameCount;
            if (frame == s_bakeCache_frame && id == s_bakeCache_rendererId
                && s_bakeCache_result != null)
                return s_bakeCache_result;

            var bakedMesh = new Mesh();
            renderer.BakeMesh(bakedMesh);
            var localVerts = bakedMesh.vertices;
            var worldVerts = new Vector3[localVerts.Length];
            var transform = renderer.transform;
            for (int i = 0; i < localVerts.Length; i++)
                worldVerts[i] = transform.TransformPoint(localVerts[i]);
            Object.DestroyImmediate(bakedMesh);

            s_bakeCache_rendererId = id;
            s_bakeCache_frame = frame;
            s_bakeCache_result = worldVerts;
            return worldVerts;
        }

        /// <summary>
        /// ワールド座標の頂点配列から、targetPosに最も近い三角面の3頂点インデックスを返す。
        /// </summary>
        private static int[] FindClosestTriangle(Vector3[] worldVertices, int[] triangles, Vector3 targetPos)
            => PiercingUtility.FindClosestTriangleIndices(worldVertices, triangles, targetPos);

        /// <summary>
        /// ピアスメッシュのバウンディングボックス中心をワールド座標で返す。
        /// transform.position（原点）ではなく実際のメッシュ位置を返す。
        /// </summary>
        private static Vector3 GetPiercingMeshWorldCenter(PiercingSetup setup)
        {
            var mf = setup.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && (mf.hideFlags & HideFlags.DontSave) == 0)
                return setup.transform.TransformPoint(mf.sharedMesh.bounds.center);

            var smr = setup.GetComponent<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null)
                return setup.transform.TransformPoint(smr.sharedMesh.bounds.center);

            return setup.transform.position;
        }

        /// <summary>
        /// ピアス側頂点のワールド重心を計算し、ターゲットメッシュ上の最近傍三角面を自動検出して
        /// 対応アンカーの targetVertices を上書きする。
        /// </summary>
        private static void AutoDetectTargetForAnchor(PiercingSetup setup, int anchorIndex)
        {
            if (anchorIndex < 0 || anchorIndex >= setup.anchors.Count) return;
            var anchor = setup.anchors[anchorIndex];
            if (anchor.piercingVertices.Count == 0) return;
            if (setup.targetRenderer == null || setup.targetRenderer.sharedMesh == null) return;

            // ピアス側メッシュの頂点をワールド座標で取得
            Vector3[] piercingVerts = null;
            var transform = setup.transform;
            var mf = setup.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                piercingVerts = mf.sharedMesh.vertices;
            }
            else
            {
                var smr = setup.GetComponent<SkinnedMeshRenderer>();
                if (smr != null && smr.sharedMesh != null)
                {
                    var bakedMesh = new Mesh();
                    smr.BakeMesh(bakedMesh);
                    piercingVerts = bakedMesh.vertices;
                    Object.DestroyImmediate(bakedMesh);
                }
            }
            if (piercingVerts == null) return;

            // ピアス頂点のワールド重心を計算
            var sum = Vector3.zero;
            int count = 0;
            foreach (int vi in anchor.piercingVertices)
            {
                if (vi >= 0 && vi < piercingVerts.Length)
                {
                    sum += transform.TransformPoint(piercingVerts[vi]);
                    count++;
                }
            }
            if (count == 0) return;
            var centroid = sum / count;

            // ターゲットメッシュから最近傍三角面を検出
            var worldVerts = BakeWorldVertices(setup.targetRenderer);
            if (worldVerts == null) return;

            var detected = FindClosestTriangle(
                worldVerts, setup.targetRenderer.sharedMesh.triangles, centroid);

            Undo.RecordObject(setup, "Auto-detect target vertices");
            anchor.targetVertices.Clear();
            anchor.targetVertices.AddRange(detected);
            EditorUtility.SetDirty(setup);
        }

        private static Vector3? ComputeVertexGroupCentroid(List<int> vertices, Vector3[] worldVertices)
        {
            if (vertices.Count == 0) return null;
            var sum = Vector3.zero;
            int count = 0;
            foreach (int vi in vertices)
            {
                if (vi >= 0 && vi < worldVertices.Length)
                {
                    sum += worldVertices[vi];
                    count++;
                }
            }
            return count > 0 ? sum / count : (Vector3?)null;
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
    }
}
