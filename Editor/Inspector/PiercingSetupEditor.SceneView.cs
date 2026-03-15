using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
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
        private static readonly Color ColorFixedCenter = new Color(1f, 0.3f, 0.3f, 1f);
        private static readonly Color ColorFixedRange = new Color(1f, 0.7f, 0.3f, 0.6f);
        private static readonly Color ColorFixedSphere = new Color(0f, 0f, 0f, 0.35f);

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

        // ピアスメッシュ用キャッシュ
        private static int s_piercingBakeCache_id;
        private static int s_piercingBakeCache_frame = -1;
        private static Vector3[] s_piercingBakeCache_result;


        private void DrawSceneVisualization(SceneView sceneView)
        {
            var setup = target as PiercingSetup;
            if (setup == null || setup.targetRenderer == null) return;

            var worldVertices = BakeWorldVertices(setup.targetRenderer);
            if (worldVertices == null) return;

            // マルチグループモード
            if (setup.useMultiGroup && setup.piercingGroups.Count > 0)
            {
                DrawMultiGroupScene(setup, sceneView, worldVertices);
                // マルチモードのオーバーレイは DrawMultiGroupScene 内で処理済み
                DrawAdjustmentHandles(setup);
                return;
            }

            // ハイブリッドモード: 固定範囲の球ギズモ + 範囲内頂点ハイライト
            bool isHybridMode = setup.skipBoneWeightTransfer &&
                                setup.fixedPiercingVertices.Count > 0;
            if (isHybridMode)
            {
                DrawFixedVertexSpheres(setup, sceneView);
            }

            if (setup.mode == PiercingMode.Single)
            {
                // ハイブリッドモードでは参照頂点の表示をスキップ
                if (!isHybridMode && setup.referenceVertices.Count > 0)
                {
                    DrawVertexGroup(setup.referenceVertices, worldVertices, sceneView);
                }
                else if (!isHybridMode && _autoDetectedVertices != null)
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

            // 位置調整オーバーレイとハンドル
            DrawAdjustmentOverlay(setup);
            DrawAdjustmentHandles(setup);

        }

        /// <summary>
        /// マルチグループモードの SceneView 描画。
        /// 選択中グループのメッシュ面をハイライト、他グループは薄く表示。
        /// 各グループの重心にラベルを表示し、クリックでグループを選択可能。
        /// </summary>
        private void DrawMultiGroupScene(
            PiercingSetup setup, SceneView sceneView, Vector3[] worldVertices)
        {
            var piercingWorldVerts = BakePiercingWorldVertices(setup);
            if (piercingWorldVerts == null) return;

            int selectedIdx = setup.selectedGroupIndex;
            if (selectedIdx < 0 || selectedIdx >= setup.piercingGroups.Count) return;

            // 頂点ソースにオフセットが焼き込み済みか判定
            bool offsetAlreadyBaked = false;
            if (setup.isPositionSaved)
            {
                var mf = setup.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null &&
                    (mf.hideFlags & HideFlags.DontSave) == 0)
                    offsetAlreadyBaked = true;
            }

            // 各グループの重心を事前計算（ラベル表示 + クリック選択用）
            var groupCenters = new Vector3[setup.piercingGroups.Count];
            for (int gi = 0; gi < setup.piercingGroups.Count; gi++)
            {
                var g = setup.piercingGroups[gi];
                var center = ComputeGroupBakedWorldCenter(
                    g.vertexIndices, piercingWorldVerts, setup.transform.position);

                bool gHasOffset = g.positionOffset != Vector3.zero ||
                                  g.rotationEuler != Vector3.zero;
                if (gHasOffset && !offsetAlreadyBaked)
                {
                    var parentRot = setup.transform.rotation;
                    var rot = parentRot * Quaternion.Euler(g.rotationEuler) * Quaternion.Inverse(parentRot);
                    var pivot = center;
                    center = rot * (center - pivot) + pivot +
                             setup.transform.TransformVector(g.positionOffset);
                }

                groupCenters[gi] = center;
            }

            // --- クリックでグループ選択 ---
            HandleGroupClickSelection(setup, groupCenters, sceneView);

            // --- 選択グループのメッシュ面をハイライト ---
            {
                var group = setup.piercingGroups[selectedIdx];
                var groupColor = AnchorColors[selectedIdx % AnchorColors.Length];

                bool hasOffset = group.positionOffset != Vector3.zero ||
                                 group.rotationEuler != Vector3.zero;
                Vector3 pivot = Vector3.zero, worldOffset = Vector3.zero;
                Quaternion rotation = Quaternion.identity;

                if (hasOffset && !offsetAlreadyBaked)
                {
                    pivot = ComputeGroupBakedWorldCenter(
                        group.vertexIndices, piercingWorldVerts, setup.transform.position);
                    var parentRot = setup.transform.rotation;
                    rotation = parentRot * Quaternion.Euler(group.rotationEuler) * Quaternion.Inverse(parentRot);
                    worldOffset = setup.transform.TransformVector(group.positionOffset);
                }

                var faceColor = new Color(groupColor.r, groupColor.g, groupColor.b, 0.3f);
                var edgeColor = new Color(groupColor.r, groupColor.g, groupColor.b, 0.6f);

                var prevZTest = Handles.zTest;
                Handles.zTest = CompareFunction.LessEqual;

                var tris = group.triangleIndices;
                for (int ti = 0; ti + 2 < tris.Count; ti += 3)
                {
                    int a = tris[ti], b = tris[ti + 1], c = tris[ti + 2];
                    if (a < 0 || a >= piercingWorldVerts.Length ||
                        b < 0 || b >= piercingWorldVerts.Length ||
                        c < 0 || c >= piercingWorldVerts.Length) continue;

                    var pa = piercingWorldVerts[a];
                    var pb = piercingWorldVerts[b];
                    var pc = piercingWorldVerts[c];

                    if (hasOffset && !offsetAlreadyBaked)
                    {
                        pa = rotation * (pa - pivot) + pivot + worldOffset;
                        pb = rotation * (pb - pivot) + pivot + worldOffset;
                        pc = rotation * (pc - pivot) + pivot + worldOffset;
                    }

                    Handles.color = faceColor;
                    Handles.DrawAAConvexPolygon(pa, pb, pc);

                    Handles.color = edgeColor;
                    Handles.DrawLine(pa, pb);
                    Handles.DrawLine(pb, pc);
                    Handles.DrawLine(pc, pa);
                }

                Handles.zTest = prevZTest;

                // 選択グループの参照頂点/アンカー情報
                if (group.mode == PiercingMode.Single)
                {
                    if (group.referenceVertices.Count > 0)
                    {
                        DrawVertexGroup(group.referenceVertices, worldVertices, sceneView,
                            groupColor, $"G{selectedIdx + 1}");
                    }
                    else if (_groupAutoDetectedVertices != null &&
                             _groupAutoDetectedVertices.TryGetValue(selectedIdx, out var autoVerts) &&
                             autoVerts != null)
                    {
                        DrawVertexGroup(new List<int>(autoVerts), worldVertices, sceneView,
                            ColorAutoSelect, "auto");
                    }
                }
                else // Chain / MultiAnchor
                {
                    if (group.anchors != null)
                    {
                        for (int ai = 0; ai < group.anchors.Count; ai++)
                        {
                            var anchorColor = AnchorColors[(selectedIdx + ai + 1) % AnchorColors.Length];
                            string label = group.mode == PiercingMode.Chain
                                ? (ai == 0 ? "A" : "B")
                                : $"{ai + 1}";
                            DrawVertexGroup(group.anchors[ai].targetVertices, worldVertices,
                                sceneView, anchorColor, $"G{selectedIdx + 1}:{label}");
                        }

                        if (group.anchors.Count >= 2)
                        {
                            Handles.color = new Color(1f, 1f, 1f, 0.4f);
                            for (int ai = 0; ai < group.anchors.Count - 1; ai++)
                            {
                                var c0 = ComputeVertexGroupCentroid(
                                    group.anchors[ai].targetVertices, worldVertices);
                                var c1 = ComputeVertexGroupCentroid(
                                    group.anchors[ai + 1].targetVertices, worldVertices);
                                if (c0.HasValue && c1.HasValue)
                                    Handles.DrawDottedLine(c0.Value, c1.Value, 4f);
                            }
                        }
                    }
                }
            }

            // --- 各グループのラベル表示 ---
            DrawGroupLabels(setup, groupCenters, selectedIdx, sceneView);
        }

        /// <summary>
        /// SceneView 上のクリックで最も近いグループを選択する。
        /// </summary>
        private void HandleGroupClickSelection(
            PiercingSetup setup, Vector3[] groupCenters, SceneView sceneView)
        {
            if (setup.piercingGroups.Count <= 1) return;

            var e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 0) return;
            if (e.alt) return; // Alt+クリック（カメラ操作）は無視

            // ハンドル操作中（Move/Rotateドラッグ等）はスルー
            if (GUIUtility.hotControl != 0) return;

            // ピッカーツールがアクティブな場合はスルー
            if (_pickerTool != null && _pickerTool.isActive) return;

            var mousePos = e.mousePosition;
            float bestDistSq = float.MaxValue;
            int bestGroup = -1;
            float maxDistSq = 60f * 60f; // 60px 以内

            for (int gi = 0; gi < groupCenters.Length; gi++)
            {
                var screenPos = HandleUtility.WorldToGUIPoint(groupCenters[gi]);
                float distSq = (screenPos - mousePos).sqrMagnitude;
                if (distSq < bestDistSq && distSq < maxDistSq)
                {
                    bestDistSq = distSq;
                    bestGroup = gi;
                }
            }

            if (bestGroup >= 0 && bestGroup != setup.selectedGroupIndex)
            {
                Undo.RecordObject(setup, "Select piercing group");
                setup.selectedGroupIndex = bestGroup;
                EditorUtility.SetDirty(setup);
                e.Use();
                Repaint();
                SceneView.RepaintAll();
            }
        }

        /// <summary>
        /// 各グループの重心位置にグループ名ラベルを表示する。
        /// 選択中グループは強調表示。
        /// </summary>
        private static GUIStyle s_groupLabelStyle;

        private static void DrawGroupLabels(
            PiercingSetup setup, Vector3[] groupCenters, int selectedIdx, SceneView sceneView)
        {
            if (s_groupLabelStyle == null)
            {
                s_groupLabelStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    alignment = TextAnchor.MiddleCenter
                };
            }

            var cam = sceneView.camera;
            for (int gi = 0; gi < setup.piercingGroups.Count; gi++)
            {
                var group = setup.piercingGroups[gi];
                var groupColor = AnchorColors[gi % AnchorColors.Length];
                bool isSelected = (gi == selectedIdx);

                string labelText = string.IsNullOrEmpty(group.name)
                    ? $"Group {gi + 1}"
                    : group.name;

                s_groupLabelStyle.fontSize = isSelected ? 13 : 11;
                s_groupLabelStyle.normal.textColor = isSelected
                    ? groupColor
                    : new Color(groupColor.r, groupColor.g, groupColor.b, 0.7f);

                // ラベルをグループ重心の少し上に表示
                var labelPos = groupCenters[gi] +
                    cam.transform.up * HandleUtility.GetHandleSize(groupCenters[gi]) * 0.15f;
                Handles.Label(labelPos, labelText, s_groupLabelStyle);

                // 非選択グループにはクリック可能な小さいドットを表示
                if (!isSelected)
                {
                    Handles.color = new Color(groupColor.r, groupColor.g, groupColor.b, 0.5f);
                    float dotSize = HandleUtility.GetHandleSize(groupCenters[gi]) * 0.02f;
                    Handles.DrawSolidDisc(groupCenters[gi], cam.transform.forward, dotSize);
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
        /// ピアスメッシュのワールド頂点座標を取得する（キャッシュ付き）。
        /// SMR の場合は BakeMesh でボーン変形後の位置を返す。
        /// </summary>
        private static Vector3[] BakePiercingWorldVertices(PiercingSetup setup)
        {
            int id = setup.GetInstanceID();
            int frame = UnityEngine.Time.frameCount;
            if (frame == s_piercingBakeCache_frame && id == s_piercingBakeCache_id
                && s_piercingBakeCache_result != null)
                return s_piercingBakeCache_result;

            Vector3[] localVerts = null;
            var tr = setup.transform;

            var mf = setup.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && (mf.hideFlags & HideFlags.DontSave) == 0)
            {
                localVerts = mf.sharedMesh.vertices;
            }
            else
            {
                var smr = setup.GetComponent<SkinnedMeshRenderer>();
                if (smr != null && smr.sharedMesh != null)
                {
                    // BakeMesh は一部のボーン構成で実際のレンダリング位置と異なる結果を返すため、
                    // 手動スキニング計算でワールド座標を求め、SMRローカル空間に戻す
                    localVerts = MeshGenerator.ComputeSkinnedVerticesLocal(smr, tr);
                }
            }

            if (localVerts == null) return null;

            var worldVerts = new Vector3[localVerts.Length];
            for (int i = 0; i < localVerts.Length; i++)
                worldVerts[i] = tr.TransformPoint(localVerts[i]);

            s_piercingBakeCache_id = id;
            s_piercingBakeCache_frame = frame;
            s_piercingBakeCache_result = worldVerts;
            return worldVerts;
        }

        /// <summary>
        /// ハイブリッドモードの固定範囲を可視化する。
        /// 各中心頂点に球ギズモ、範囲内頂点にドットを描画。
        /// </summary>
        private static void DrawFixedVertexSpheres(PiercingSetup setup, SceneView sceneView)
        {
            var piercingWorldVerts = BakePiercingWorldVertices(setup);
            if (piercingWorldVerts == null) return;

            // ピアスメッシュのローカル頂点（距離計算用）
            Mesh piercingMesh = null;
            var mf = setup.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && (mf.hideFlags & HideFlags.DontSave) == 0)
                piercingMesh = mf.sharedMesh;
            else
            {
                var smr = setup.GetComponent<SkinnedMeshRenderer>();
                if (smr != null && smr.sharedMesh != null)
                    piercingMesh = smr.sharedMesh;
            }
            if (piercingMesh == null) return;

            // 展開済み固定頂点セットを計算
            var expanded = MeshGenerator.ExpandFixedVertices(
                piercingMesh, setup.fixedPiercingVertices, setup.fixedPiercingRadius);
            var centerSet = new HashSet<int>(setup.fixedPiercingVertices);

            var cam = sceneView.camera;
            var camForward = cam.transform.forward;

            // 各中心頂点に球ギズモを描画
            foreach (int ci in setup.fixedPiercingVertices)
            {
                if (ci < 0 || ci >= piercingWorldVerts.Length) continue;
                var centerWorld = piercingWorldVerts[ci];

                // ローカル空間の半径をワールド空間に変換（平均スケール）
                var ltw = setup.transform.localToWorldMatrix;
                float sx = new Vector3(ltw.m00, ltw.m10, ltw.m20).magnitude;
                float sy = new Vector3(ltw.m01, ltw.m11, ltw.m21).magnitude;
                float sz = new Vector3(ltw.m02, ltw.m12, ltw.m22).magnitude;
                float worldRadius = setup.fixedPiercingRadius * (sx + sy + sz) / 3f;

                // 3軸のワイヤーディスクで球を表現
                Handles.color = ColorFixedSphere;
                Handles.DrawWireDisc(centerWorld, Vector3.up, worldRadius, 2f);
                Handles.DrawWireDisc(centerWorld, Vector3.right, worldRadius, 2f);
                Handles.DrawWireDisc(centerWorld, Vector3.forward, worldRadius, 2f);

                // 中心頂点ドット
                Handles.color = ColorFixedCenter;
                float dotSize = HandleUtility.GetHandleSize(centerWorld) * 0.015f;
                Handles.DrawSolidDisc(centerWorld, camForward, dotSize);
            }

            // 範囲内頂点をドットで表示（中心頂点以外）
            Handles.color = ColorFixedRange;
            foreach (int vi in expanded)
            {
                if (centerSet.Contains(vi)) continue;
                if (vi < 0 || vi >= piercingWorldVerts.Length) continue;
                var pos = piercingWorldVerts[vi];
                float dotSize = HandleUtility.GetHandleSize(pos) * 0.008f;
                Handles.DrawSolidDisc(pos, camForward, dotSize);
            }
        }

        /// <summary>
        /// ワールド座標の頂点配列から、targetPosに最も近い三角面の3頂点インデックスを返す。
        /// </summary>
        private static int[] FindClosestTriangle(Vector3[] worldVertices, int[] triangles, Vector3 targetPos)
            => PiercingUtility.FindClosestTriangleIndices(worldVertices, triangles, targetPos);

        /// <summary>
        /// ピアスメッシュのバウンディングボックス中心をワールド座標で返す。
        /// transform.position（原点）ではなく実際のメッシュ位置を返す。
        /// SMR の場合は BakeMesh でボーン変形後の位置を取得する。
        /// </summary>
        private static Vector3 GetPiercingMeshWorldCenter(PiercingSetup setup)
        {
            var mf = setup.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && (mf.hideFlags & HideFlags.DontSave) == 0)
                return setup.transform.TransformPoint(mf.sharedMesh.bounds.center);

            var smr = setup.GetComponent<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null)
            {
                var bakedMesh = new Mesh();
                smr.BakeMesh(bakedMesh);
                var center = setup.transform.TransformPoint(bakedMesh.bounds.center);
                Object.DestroyImmediate(bakedMesh);
                return center;
            }

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

        /// <summary>
        /// マルチグループモード用: グループのアンカーのピアス側頂点から
        /// ターゲットメッシュ上の最近傍三角面を自動検出する。
        /// </summary>
        private static void AutoDetectTargetForGroupAnchor(
            PiercingSetup setup, int groupIndex, int anchorIndex)
        {
            if (groupIndex < 0 || groupIndex >= setup.piercingGroups.Count) return;
            var group = setup.piercingGroups[groupIndex];
            if (anchorIndex < 0 || anchorIndex >= group.anchors.Count) return;
            var anchor = group.anchors[anchorIndex];
            if (anchor.piercingVertices.Count == 0) return;
            if (setup.targetRenderer == null || setup.targetRenderer.sharedMesh == null) return;

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

            var worldVerts = BakeWorldVertices(setup.targetRenderer);
            if (worldVerts == null) return;

            var detected = FindClosestTriangle(
                worldVerts, setup.targetRenderer.sharedMesh.triangles, centroid);

            Undo.RecordObject(setup, "Auto-detect group anchor target");
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

        // =================================================================
        // 位置調整: SceneView オーバーレイ & ハンドル
        // =================================================================

        private static bool HasAnyOffset(PiercingSetup setup)
        {
            if (setup.useMultiGroup)
            {
                foreach (var group in setup.piercingGroups)
                {
                    if (group.positionOffset != Vector3.zero || group.rotationEuler != Vector3.zero)
                        return true;
                }
                return false;
            }
            return setup.positionOffset != Vector3.zero || setup.rotationEuler != Vector3.zero;
        }

        /// <summary>
        /// 「位置を保存」前: オフセット適用後の位置にメッシュをオーバーレイ描画する（非マルチモード用）。
        /// マルチモードは DrawMultiGroupScene 内で直接オフセット適用する。
        /// 保存後はプレビューメッシュに焼き込み済みのため描画不要。
        /// </summary>
        private void DrawAdjustmentOverlay(PiercingSetup setup)
        {
            if (setup.isPositionSaved) return;
            if (setup.useMultiGroup) return; // マルチモードは DrawMultiGroupScene で処理
            if (setup.positionOffset == Vector3.zero && setup.rotationEuler == Vector3.zero) return;

            var piercingWorldVerts = BakePiercingWorldVertices(setup);
            if (piercingWorldVerts == null) return;

            var mesh = GetPiercingMeshForOverlay(setup);
            if (mesh == null) return;

            var triangles = mesh.triangles;
            var pivot = ComputePiercingWorldCenter(piercingWorldVerts);
            // ローカル回転をワールド空間の等価回転に変換
            var parentRot = setup.transform.rotation;
            var rotation = parentRot * Quaternion.Euler(setup.rotationEuler) * Quaternion.Inverse(parentRot);
            var worldOffset = setup.transform.TransformVector(setup.positionOffset);

            DrawOffsetTrianglesAll(triangles, piercingWorldVerts, pivot, rotation, worldOffset);
        }

        private static Vector3 ComputePiercingWorldCenter(Vector3[] worldVerts)
        {
            var sum = Vector3.zero;
            for (int i = 0; i < worldVerts.Length; i++)
                sum += worldVerts[i];
            return worldVerts.Length > 0 ? sum / worldVerts.Length : Vector3.zero;
        }

        // オーバーレイ描画色
        private static readonly Color OverlayFaceColor = new Color(0.3f, 0.8f, 1f, 0.25f);
        private static readonly Color OverlayEdgeColor = new Color(0.3f, 0.8f, 1f, 0.6f);

        private static void DrawOffsetTrianglesAll(
            int[] triangles, Vector3[] worldVerts,
            Vector3 pivot, Quaternion rotation, Vector3 worldOffset)
        {
            var prevZTest = Handles.zTest;
            Handles.zTest = CompareFunction.LessEqual;

            for (int ti = 0; ti + 2 < triangles.Length; ti += 3)
            {
                int a = triangles[ti], b = triangles[ti + 1], c = triangles[ti + 2];
                if (a < 0 || a >= worldVerts.Length ||
                    b < 0 || b >= worldVerts.Length ||
                    c < 0 || c >= worldVerts.Length) continue;

                var pa = rotation * (worldVerts[a] - pivot) + pivot + worldOffset;
                var pb = rotation * (worldVerts[b] - pivot) + pivot + worldOffset;
                var pc = rotation * (worldVerts[c] - pivot) + pivot + worldOffset;

                Handles.color = OverlayFaceColor;
                Handles.DrawAAConvexPolygon(pa, pb, pc);
                Handles.color = OverlayEdgeColor;
                Handles.DrawLine(pa, pb);
                Handles.DrawLine(pb, pc);
                Handles.DrawLine(pc, pa);
            }

            Handles.zTest = prevZTest;
        }

        private static Mesh GetPiercingMeshForOverlay(PiercingSetup setup)
        {
            var mf = setup.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && (mf.hideFlags & HideFlags.DontSave) == 0)
                return mf.sharedMesh;
            var smr = setup.GetComponent<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null)
                return smr.sharedMesh;
            return null;
        }

        /// <summary>
        /// 「位置を保存」前: SceneView に位置・回転ハンドルを表示する。
        /// Move/Rotate ツール連動。
        /// </summary>
        private void DrawAdjustmentHandles(PiercingSetup setup)
        {
            if (setup.isPositionSaved)
            {
                Tools.hidden = false;
                return;
            }

            var piercingWorldVerts = BakePiercingWorldVertices(setup);
            if (piercingWorldVerts == null)
            {
                Tools.hidden = false;
                return;
            }

            // 位置調整可能な間はデフォルトのTransformハンドルを非表示にする
            Tools.hidden = true;

            if (setup.useMultiGroup)
            {
                int selectedIdx = setup.selectedGroupIndex;
                if (selectedIdx < 0 || selectedIdx >= setup.piercingGroups.Count)
                {
                    Tools.hidden = false;
                    return;
                }
                var group = setup.piercingGroups[selectedIdx];
                DrawGroupAdjustmentHandle(setup, group, piercingWorldVerts);
            }
            else
            {
                DrawSingleAdjustmentHandle(setup, piercingWorldVerts);
            }
        }

        private void DrawSingleAdjustmentHandle(PiercingSetup setup, Vector3[] piercingWorldVerts)
        {
            var pivot = ComputePiercingWorldCenter(piercingWorldVerts);
            var worldOffset = setup.transform.TransformVector(setup.positionOffset);
            var handlePos = pivot + worldOffset;

            EditorGUI.BeginChangeCheck();

            if (Tools.current == Tool.Move || Tools.current == Tool.Transform)
            {
                var newPos = Handles.PositionHandle(handlePos, Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(setup, "Adjust piercing position");
                    var worldDelta = newPos - handlePos;
                    setup.positionOffset += setup.transform.InverseTransformVector(worldDelta);
                    EditorUtility.SetDirty(setup);
                    SceneView.RepaintAll();
                }
            }
            else if (Tools.current == Tool.Rotate)
            {
                // 現在の累積回転をハンドルに渡す（差分が正しく計算されるように）
                var currentWorldRot = setup.transform.rotation * Quaternion.Euler(setup.rotationEuler);
                var newRot = Handles.RotationHandle(currentWorldRot, handlePos);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(setup, "Adjust piercing rotation");
                    setup.rotationEuler = (Quaternion.Inverse(setup.transform.rotation) * newRot).eulerAngles;
                    EditorUtility.SetDirty(setup);
                    SceneView.RepaintAll();
                }
            }
            else
            {
                Handles.color = new Color(1f, 0.8f, 0f, 0.8f);
                Handles.SphereHandleCap(0, handlePos, Quaternion.identity,
                    HandleUtility.GetHandleSize(handlePos) * 0.08f, EventType.Repaint);
            }
        }

        private void DrawGroupAdjustmentHandle(
            PiercingSetup setup, PiercingGroup group, Vector3[] piercingWorldVerts)
        {
            var pivot = ComputeGroupBakedWorldCenter(
                group.vertexIndices, piercingWorldVerts, setup.transform.position);
            var worldOffset = setup.transform.TransformVector(group.positionOffset);
            var handlePos = pivot + worldOffset;

            EditorGUI.BeginChangeCheck();

            if (Tools.current == Tool.Move || Tools.current == Tool.Transform)
            {
                var newPos = Handles.PositionHandle(handlePos, Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(setup, "Adjust piercing group position");
                    var worldDelta = newPos - handlePos;
                    group.positionOffset += setup.transform.InverseTransformVector(worldDelta);
                    EditorUtility.SetDirty(setup);
                    SceneView.RepaintAll();
                }
            }
            else if (Tools.current == Tool.Rotate)
            {
                // 現在の累積回転をハンドルに渡す（差分が正しく計算されるように）
                var currentWorldRot = setup.transform.rotation * Quaternion.Euler(group.rotationEuler);
                var newRot = Handles.RotationHandle(currentWorldRot, handlePos);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(setup, "Adjust piercing group rotation");
                    group.rotationEuler = (Quaternion.Inverse(setup.transform.rotation) * newRot).eulerAngles;
                    EditorUtility.SetDirty(setup);
                    SceneView.RepaintAll();
                }
            }
            else
            {
                Handles.color = new Color(1f, 0.8f, 0f, 0.8f);
                Handles.SphereHandleCap(0, handlePos, Quaternion.identity,
                    HandleUtility.GetHandleSize(handlePos) * 0.08f, EventType.Repaint);
            }
        }
    }
}
