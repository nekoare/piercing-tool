using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace PiercingTool.Editor
{
    [CustomEditor(typeof(PiercingSetup))]
    [InitializeOnLoad]
    public partial class PiercingSetupEditor : UnityEditor.Editor
    {
        private SerializedProperty _mode;
        private SerializedProperty _targetRenderer;
        private SerializedProperty _skipBoneWeightTransfer;
        private SerializedProperty _perVertexBoneWeights;
        private SerializedProperty _maintainOverallShape;

        private VertexPickerTool _pickerTool;

        private struct PickerTarget : System.IEquatable<PickerTarget>
        {
            public enum Kind { None, Single, AnchorTarget, AnchorPiercing }
            public Kind kind;
            public int index;

            public static readonly PickerTarget Single = new PickerTarget { kind = Kind.Single };
            public static PickerTarget AnchorTarget(int i)
                => new PickerTarget { kind = Kind.AnchorTarget, index = i };
            public static PickerTarget AnchorPiercing(int i)
                => new PickerTarget { kind = Kind.AnchorPiercing, index = i };

            public bool Equals(PickerTarget other)
                => kind == other.kind && index == other.index;
            public override bool Equals(object obj)
                => obj is PickerTarget other && Equals(other);
            public override int GetHashCode()
                => ((int)kind * 397) ^ index;
            public static bool operator ==(PickerTarget a, PickerTarget b) => a.Equals(b);
            public static bool operator !=(PickerTarget a, PickerTarget b) => !a.Equals(b);
        }

        private PickerTarget _activePickerTarget;

        /// <summary>
        /// Chain モードで「ピアス側も指定する」トグルの ON 状態を保持する。
        /// piercingVertices が空でもトグルが戻らないようにするためのエディタ限定状態。
        /// </summary>
        private readonly HashSet<int> _showPiercingForAnchor = new HashSet<int>();

        /// <summary>
        /// Single モードで referenceVertices が空のとき、現在の位置から自動検出した頂点。
        /// Inspector/SceneView 表示用の一時キャッシュ（コンポーネントには保存しない）。
        /// </summary>
        private int[] _autoDetectedVertices;

        private void OnEnable()
        {
            _mode = serializedObject.FindProperty("mode");
            _targetRenderer = serializedObject.FindProperty("targetRenderer");
            _skipBoneWeightTransfer = serializedObject.FindProperty("skipBoneWeightTransfer");
            _perVertexBoneWeights = serializedObject.FindProperty("perVertexBoneWeights");
            _maintainOverallShape = serializedObject.FindProperty("maintainOverallShape");
            SceneView.duringSceneGui += DrawSceneVisualization;
        }

        private void OnDisable()
        {
            _pickerTool?.Deactivate();
            SceneView.duringSceneGui -= DrawSceneVisualization;
            // プレビューはstatic管理のため、ここではクリーンアップしない
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

            // --- 自動検出キャッシュ更新（Single + 未保存 + 手動選択なし） ---
            if (setup.mode == PiercingMode.Single &&
                !setup.isPositionSaved &&
                setup.referenceVertices.Count == 0 &&
                setup.targetRenderer != null &&
                setup.targetRenderer.sharedMesh != null)
            {
                var worldVerts = BakeWorldVertices(setup.targetRenderer);
                if (worldVerts != null)
                {
                    _autoDetectedVertices = FindClosestTriangle(
                        worldVerts, setup.targetRenderer.sharedMesh.triangles,
                        GetPiercingMeshWorldCenter(setup));
                }
            }
            else
            {
                _autoDetectedVertices = null;
            }

            // --- 頂点選択（保存中は読み取り専用） ---
            if (setup.mode == PiercingMode.Single)
            {
                DrawVertexSection("参照頂点", setup.referenceVertices, PickerTarget.Single, setup);
                EditorGUILayout.Space();
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(_perVertexBoneWeights,
                    new GUIContent("体表面に追従",
                        "ピアスの各頂点に最寄りの体メッシュのボーンウェイトを個別適用します。\n" +
                        "体勢による位置ずれや埋まりを軽減しますが、ピアスが多少変形します。"));
                if (EditorGUI.EndChangeCheck() && _perVertexBoneWeights.boolValue)
                    _maintainOverallShape.boolValue = false;

                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(_maintainOverallShape,
                    new GUIContent("全体の形状を維持する",
                        "ピアス位置に最も近い2頂点を自動選択し、軸方向の回転のみで追従します。\n" +
                        "ピアスの形状変化を抑えたい場合に有効です。"));
                if (EditorGUI.EndChangeCheck() && _maintainOverallShape.boolValue)
                    _perVertexBoneWeights.boolValue = false;
            }
            else if (setup.mode == PiercingMode.Chain)
            {
                EnsureMinAnchors(setup, 2);

                for (int i = 0; i < setup.anchors.Count && i < 2; i++)
                {
                    string anchorLabel = i == 0 ? "Anchor A" : "Anchor B";
                    DrawAnchorSection(anchorLabel, i, setup);
                }

                EditorGUILayout.Space();
                EditorGUILayout.PropertyField(_skipBoneWeightTransfer,
                    new GUIContent("ボーンウェイト転写をスキップ",
                        "PhysBone設定済みの場合にチェック"));
            }
            else if (setup.mode == PiercingMode.MultiAnchor)
            {
                EnsureMinAnchors(setup, 2);
                DrawMultiAnchorUI(setup);
            }

            EditorGUILayout.Space(10);

            // --- 位置を保存 / 解除ボタン ---
            if (setup.isPositionSaved)
            {
                // Toggle(true, ..., "Button") で押下状態の見た目にする
                if (!GUILayout.Toggle(true, "保存を解除", "Button", GUILayout.Height(30)))
                    UnsavePosition(setup);
            }
            else
            {
                using (new EditorGUI.DisabledScope(!IsReadyToGenerate(setup)))
                {
                    if (GUILayout.Button("位置を保存", GUILayout.Height(30)))
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
                // 手動選択がある場合はそれを表示、なければ自動検出を表示
                bool showingAuto = vertices.Count == 0 && _autoDetectedVertices != null &&
                                   pickerTarget == PickerTarget.Single;

                if (showingAuto)
                {
                    EditorGUILayout.LabelField("選択数: 自動検出");

                    // 自動検出頂点をグレーで表示
                    if (setup.targetRenderer != null && setup.targetRenderer.sharedMesh != null)
                    {
                        var sourceVertices = setup.targetRenderer.sharedMesh.vertices;
                        var prevColor = GUI.color;
                        GUI.color = new Color(1f, 1f, 1f, 0.5f);
                        foreach (int vi in _autoDetectedVertices)
                        {
                            string posStr = vi < sourceVertices.Length
                                ? sourceVertices[vi].ToString("F3")
                                : "(invalid)";
                            EditorGUILayout.LabelField($"  #{vi}  {posStr}");
                        }
                        GUI.color = prevColor;
                    }
                }
                else
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
                                if (!setup.isPositionSaved &&
                                    GUILayout.Button("\u00d7", GUILayout.Width(22)))
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
                }

                // ピッカーボタン（保存中は非表示）
                if (!setup.isPositionSaved)
                {
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

                    // 自動選択をやり直すボタン（Single + 手動選択がある場合のみ）
                    if (pickerTarget == PickerTarget.Single && vertices.Count > 0 &&
                        setup.targetRenderer != null && setup.targetRenderer.sharedMesh != null)
                    {
                        if (GUILayout.Button("現在の位置で自動選択をやり直す"))
                        {
                            var worldVerts = BakeWorldVertices(setup.targetRenderer);
                            if (worldVerts != null)
                            {
                                var auto = FindClosestTriangle(
                                    worldVerts, setup.targetRenderer.sharedMesh.triangles,
                                    GetPiercingMeshWorldCenter(setup));
                                Undo.RecordObject(setup, "Re-detect vertices");
                                vertices.Clear();
                                vertices.AddRange(auto);
                                EditorUtility.SetDirty(setup);
                            }
                        }
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

        private void DrawAnchorSection(string label, int anchorIndex, PiercingSetup setup,
            bool showLabel = true)
        {
            var anchor = setup.anchors[anchorIndex];

            if (showLabel)
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                // Target 頂点
                DrawVertexListForAnchor(
                    "Target頂点", anchor.targetVertices,
                    PickerTarget.AnchorTarget(anchorIndex), setup,
                    setup.targetRenderer);

                // ピアス側指定（Chain モードではオプション）
                if (setup.mode == PiercingMode.Chain)
                {
                    bool showPiercing = anchor.piercingVertices.Count > 0 ||
                                        _showPiercingForAnchor.Contains(anchorIndex);
                    bool newShowPiercing = EditorGUILayout.Toggle(
                        "ピアス側も指定する", showPiercing);

                    if (newShowPiercing && !showPiercing)
                    {
                        _showPiercingForAnchor.Add(anchorIndex);
                    }
                    else if (!newShowPiercing && showPiercing)
                    {
                        _showPiercingForAnchor.Remove(anchorIndex);
                        if (anchor.piercingVertices.Count > 0)
                        {
                            Undo.RecordObject(setup, "Clear piercing vertices");
                            anchor.piercingVertices.Clear();
                            EditorUtility.SetDirty(setup);
                        }
                    }

                    if (newShowPiercing || anchor.piercingVertices.Count > 0)
                    {
                        DrawVertexListForAnchor(
                            "Piercing頂点", anchor.piercingVertices,
                            PickerTarget.AnchorPiercing(anchorIndex), setup,
                            null, usePiercingMesh: true);
                    }
                }
                else // MultiAnchor — piercing 必須
                {
                    DrawVertexListForAnchor(
                        "Piercing頂点", anchor.piercingVertices,
                        PickerTarget.AnchorPiercing(anchorIndex), setup,
                        null, usePiercingMesh: true);
                }
            }
        }

        private void DrawVertexListForAnchor(
            string label, List<int> vertices,
            PickerTarget pickerTarget, PiercingSetup setup,
            SkinnedMeshRenderer renderer,
            bool usePiercingMesh = false)
        {
            EditorGUILayout.LabelField($"{label}: {vertices.Count}個");

            // 頂点リスト表示
            if (vertices.Count > 0)
            {
                Vector3[] sourceVerts = null;
                if (usePiercingMesh)
                {
                    var mf = setup.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                        sourceVerts = mf.sharedMesh.vertices;
                    else
                    {
                        var smr = setup.GetComponent<SkinnedMeshRenderer>();
                        if (smr != null && smr.sharedMesh != null)
                            sourceVerts = smr.sharedMesh.vertices;
                    }
                }
                else if (renderer != null && renderer.sharedMesh != null)
                {
                    sourceVerts = renderer.sharedMesh.vertices;
                }

                if (sourceVerts != null)
                {
                    int removeIndex = -1;
                    for (int i = 0; i < vertices.Count; i++)
                    {
                        int vi = vertices[i];
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            string posStr = vi < sourceVerts.Length
                                ? sourceVerts[vi].ToString("F3") : "(invalid)";
                            EditorGUILayout.LabelField($"  #{vi}  {posStr}");
                            if (!setup.isPositionSaved &&
                                GUILayout.Button("\u00d7", GUILayout.Width(22)))
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
            }

            // ピッカーボタン
            if (!setup.isPositionSaved)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool isActive = _pickerTool != null &&
                                    _pickerTool.isActive &&
                                    _activePickerTarget == pickerTarget;
                    string btnText = isActive ? "選択中..." : "選択";
                    var btnStyle = isActive
                        ? new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold }
                        : GUI.skin.button;

                    if (GUILayout.Button(btnText, btnStyle))
                    {
                        if (isActive)
                            _pickerTool.Deactivate();
                        else
                            StartAnchorPicker(setup, vertices, pickerTarget, usePiercingMesh);
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

        private void StartAnchorPicker(
            PiercingSetup setup, List<int> vertices,
            PickerTarget pickerTarget, bool usePiercingMesh)
        {
            _pickerTool?.Deactivate();

            _activePickerTarget = pickerTarget;
            _pickerTool = new VertexPickerTool
            {
                selectedVertices = vertices,
                singleSelectMode = usePiercingMesh,
                onSelectionChanged = () =>
                {
                    Undo.RecordObject(setup, "Select vertex");
                    EditorUtility.SetDirty(setup);

                    // ピアス側ピッカーの場合、対応アンカーの Target 頂点を自動検出
                    if (usePiercingMesh &&
                        pickerTarget.kind == PickerTarget.Kind.AnchorPiercing)
                    {
                        int anchorIdx = pickerTarget.index;
                        AutoDetectTargetForAnchor(setup, anchorIdx);
                    }

                    Repaint();
                }
            };

            if (usePiercingMesh)
            {
                // ピアスメッシュ: MeshFilter を優先、なければ SkinnedMeshRenderer
                var mf = setup.GetComponent<MeshFilter>();
                if (mf != null)
                {
                    _pickerTool.meshFilter = mf;
                }
                else
                {
                    var smr = setup.GetComponent<SkinnedMeshRenderer>();
                    if (smr != null)
                        _pickerTool.targetRenderer = smr;
                }
            }
            else
            {
                if (setup.targetRenderer == null)
                {
                    EditorUtility.DisplayDialog("エラー", "対象Rendererを設定してください。", "OK");
                    return;
                }
                _pickerTool.targetRenderer = setup.targetRenderer;
            }

            _pickerTool.Activate();
        }

        private static void EnsureMinAnchors(PiercingSetup setup, int minCount)
        {
            if (setup.anchors == null)
                setup.anchors = new List<AnchorPair>();
            if (setup.anchors.Count < minCount)
            {
                Undo.RecordObject(setup, "Initialize anchors");
                while (setup.anchors.Count < minCount)
                    setup.anchors.Add(new AnchorPair());
                EditorUtility.SetDirty(setup);
            }
        }

        private void DrawMultiAnchorUI(PiercingSetup setup)
        {
            for (int i = 0; i < setup.anchors.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"Anchor {i + 1}", EditorStyles.boldLabel);

                    // 削除ボタン（2個未満にはできない）
                    using (new EditorGUI.DisabledScope(
                        setup.isPositionSaved || setup.anchors.Count <= 2))
                    {
                        if (GUILayout.Button("\u00d7", GUILayout.Width(22)))
                        {
                            Undo.RecordObject(setup, "Remove anchor");
                            setup.anchors.RemoveAt(i);
                            EditorUtility.SetDirty(setup);
                            break;
                        }
                    }
                }

                DrawAnchorSection($"Anchor {i + 1}", i, setup, showLabel: false);
                EditorGUILayout.Space();
            }

            // アンカー追加ボタン
            if (!setup.isPositionSaved)
            {
                if (GUILayout.Button("+ アンカーを追加"))
                {
                    Undo.RecordObject(setup, "Add anchor");
                    setup.anchors.Add(new AnchorPair());
                    EditorUtility.SetDirty(setup);
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_skipBoneWeightTransfer,
                new GUIContent("ボーンウェイト転写をスキップ",
                    "PhysBone設定済みの場合にチェック"));
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
            else // Chain / MultiAnchor
            {
                return setup.anchors != null && setup.anchors.Count >= 2 &&
                       setup.anchors.All(a => a.targetVertices.Count > 0);
            }
        }

        private void UnsavePosition(PiercingSetup setup)
        {
            Undo.RecordObject(setup, "Unsave piercing position");

            // プレビューをクリーンアップ
            int id = setup.GetInstanceID();
            if (s_previews.TryGetValue(id, out var state))
            {
                CleanupPreviewState(setup, state);
                s_previews.Remove(id);
                s_originalMeshes.Remove(id);
            }

            setup.isPositionSaved = false;
            setup.savedBlendShapeWeights = null;
            setup.savedPiercingBlendShapeWeights = null;
            setup.isSmrPreviewActive = false;
            setup.originalMesh = null;
            EditorUtility.SetDirty(setup);

            Debug.Log("[PiercingTool] 位置保存を解除しました。");
        }

        private void SavePosition(PiercingSetup setup)
        {
            if (setup.targetRenderer == null)
                throw new System.InvalidOperationException("対象Rendererが設定されていません。");

            Undo.RecordObject(setup, "Save piercing position");

            // 参照頂点が未指定の場合、現在のBlendShape状態で自動選択して保存
            // （ビルド時のBlendShape状態に依存しないようにするため）
            if (setup.mode == PiercingMode.Single)
            {
                if (setup.maintainOverallShape)
                {
                    // 「全体の形状を維持する」: 2頂点を自動選択して保存
                    setup.referenceVertices.Clear();
                    var autoSelected = MeshGenerator.FindClosestTwoVertices(
                        setup.targetRenderer, GetPiercingMeshWorldCenter(setup));
                    setup.referenceVertices.AddRange(autoSelected);
                    Debug.Log($"[PiercingTool] 2頂点を自動選択しました（形状維持）: {string.Join(", ", autoSelected)}");
                }
                else if (setup.referenceVertices.Count == 0)
                {
                    var autoSelected = MeshGenerator.FindClosestTriangleVertices(
                        setup.targetRenderer, GetPiercingMeshWorldCenter(setup));
                    setup.referenceVertices.AddRange(autoSelected);
                    Debug.Log($"[PiercingTool] 参照頂点を自動選択しました: {string.Join(", ", autoSelected)}");
                }
            }

            // ターゲットの BlendShape weightsスナップショットを保存
            var smr = setup.targetRenderer;
            int count = smr.sharedMesh != null ? smr.sharedMesh.blendShapeCount : 0;
            setup.savedBlendShapeWeights = new float[count];
            for (int i = 0; i < count; i++)
                setup.savedBlendShapeWeights[i] = smr.GetBlendShapeWeight(i);

            // ピアス側の BlendShape weights を保存（SMR の場合のみ）
            var piercingSmr = setup.GetComponent<SkinnedMeshRenderer>();
            if (piercingSmr != null && piercingSmr.sharedMesh != null &&
                piercingSmr.sharedMesh.blendShapeCount > 0)
            {
                int pCount = piercingSmr.sharedMesh.blendShapeCount;
                setup.savedPiercingBlendShapeWeights = new float[pCount];
                for (int i = 0; i < pCount; i++)
                    setup.savedPiercingBlendShapeWeights[i] = piercingSmr.GetBlendShapeWeight(i);
            }
            else
            {
                setup.savedPiercingBlendShapeWeights = null;
            }

            // プレビュー適用前の元メッシュを保持（ドメインリロード後の復元用）
            var mf = setup.GetComponent<MeshFilter>();
            if (mf != null)
                setup.originalMesh = mf.sharedMesh;

            setup.isPositionSaved = true;
            EditorUtility.SetDirty(setup);

            // プレビューを登録（static管理）
            RegisterPreview(setup);

            Debug.Log($"[PiercingTool] 位置を保存しました（BlendShape weights: {count}個）。");
        }
    }
}
