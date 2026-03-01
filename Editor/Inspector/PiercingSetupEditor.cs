using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace PiercingTool.Editor
{
    [CustomEditor(typeof(PiercingSetup))]
    [InitializeOnLoad]
    public class PiercingSetupEditor : UnityEditor.Editor
    {
        private SerializedProperty _mode;
        private SerializedProperty _targetRenderer;
        private SerializedProperty _skipBoneWeightTransfer;
        private SerializedProperty _perVertexBoneWeights;
        private SerializedProperty _maintainOverallShape;

        private VertexPickerTool _pickerTool;

        private const int MaxAnchorCount = 8;

        private enum PickerTarget
        {
            Single,
            AnchorTarget0, AnchorTarget1, AnchorTarget2, AnchorTarget3,
            AnchorTarget4, AnchorTarget5, AnchorTarget6, AnchorTarget7,
            AnchorPiercing0, AnchorPiercing1, AnchorPiercing2, AnchorPiercing3,
            AnchorPiercing4, AnchorPiercing5, AnchorPiercing6, AnchorPiercing7,
        }

        private static PickerTarget AnchorTargetPicker(int index)
            => (PickerTarget)((int)PickerTarget.AnchorTarget0 + index);

        private static PickerTarget AnchorPiercingPicker(int index)
            => (PickerTarget)((int)PickerTarget.AnchorPiercing0 + index);

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

        // =================================================================
        // Static BlendShape追従プレビュー
        // Inspector選択に依存せず、位置保存後は常に動作する
        // =================================================================

        private class PreviewState
        {
            public MeshFilter meshFilter;
            public Mesh previewMesh;
            public Vector3[] originalVertices;
            public float[] lastWeights;
            public Mesh originalSharedMesh;

            // Chain/MultiAnchor 用: プレビュー初期化時に計算し、毎フレーム再利用
            public int[][] anchorIndices;
            public Vector3[] anchorCentroids;
            public (int segmentIndex, float localT)[] segmentData;

            // ピアス側 SMR の BlendShape weights 復元用
            public SkinnedMeshRenderer piercingSmr;
            public float[] originalPiercingWeights;

            // SMR ピアスのプレビュー用
            public bool isSmrPiercing;
            public MeshRenderer tempMeshRenderer;
        }

        private static readonly Dictionary<int, PreviewState> s_previews =
            new Dictionary<int, PreviewState>();

        // Undo復元用: クリーンアップ後も元メッシュ参照を保持
        private static readonly Dictionary<int, Mesh> s_originalMeshes =
            new Dictionary<int, Mesh>();

        // ドメインリロード後の初回更新で自動復元を行うためのフラグ
        private static bool s_needsRestore = true;

        static PiercingSetupEditor()
        {
            s_needsRestore = true;
            EditorApplication.update += StaticUpdatePreviews;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        /// <summary>
        /// Undo/Redo後に、プレビュー状態を復元する。
        /// コンポーネント削除→Ctrl+Z の場合、UndoがMeshFilterを破棄済みプレビューメッシュに
        /// 戻すためMissingになるか、またはUndoが復元したプレビューメッシュがそのまま残る。
        /// いずれの場合も s_originalMeshes から元メッシュを復元してからプレビューを再登録する。
        /// </summary>
        private static void OnUndoRedo()
        {
            var setups = Object.FindObjectsOfType<PiercingSetup>();
            foreach (var setup in setups)
            {
                int id = setup.GetInstanceID();

                // isPositionSaved が Undo で false に戻った場合、
                // SMR プレビュー状態が残っていたらクリーンアップ
                if (!setup.isPositionSaved)
                {
                    if (setup.isSmrPreviewActive)
                    {
                        var smr = setup.GetComponent<SkinnedMeshRenderer>();
                        if (smr != null) smr.enabled = true;
                        CleanupOrphanedTempComponents(setup);
                        setup.isSmrPreviewActive = false;
                        EditorUtility.SetDirty(setup);
                    }
                    continue;
                }

                // 既にプレビュー管理中なら何もしない
                if (s_previews.ContainsKey(id)) continue;

                var mf = setup.GetComponent<MeshFilter>();
                if (mf != null && (mf.hideFlags & HideFlags.DontSave) == 0)
                {
                    // MF ベース: 元メッシュを復元
                    if (s_originalMeshes.TryGetValue(id, out var originalMesh) &&
                        originalMesh != null && mf.sharedMesh != originalMesh)
                    {
                        mf.sharedMesh = originalMesh;
                    }
                }
                else
                {
                    // SMR ベース: SMR を再有効化してから再登録
                    var piercingSmr = setup.GetComponent<SkinnedMeshRenderer>();
                    if (piercingSmr != null)
                        piercingSmr.enabled = true;
                    CleanupOrphanedTempComponents(setup);
                }

                RegisterPreview(setup);
            }
        }

        /// <summary>
        /// ドメインリロード後（Play→Edit復帰、Unity再起動、スクリプト再コンパイル）に
        /// isPositionSaved な PiercingSetup のプレビューを自動再登録する。
        /// </summary>
        private static void RestorePreviewsAfterReload()
        {
            // Play Mode中はプレビューを復元しない
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            var setups = Object.FindObjectsOfType<PiercingSetup>();
            foreach (var setup in setups)
            {
                if (!setup.isPositionSaved) continue;
                if (s_previews.ContainsKey(setup.GetInstanceID())) continue;

                if (setup.isSmrPreviewActive)
                {
                    // SMR ピアス: リロードで HideAndDontSave な MF/MR は消えている。
                    // SMR を再有効化してから再登録（UpdatePreview で再作成される）
                    var piercingSmr = setup.GetComponent<SkinnedMeshRenderer>();
                    if (piercingSmr != null)
                        piercingSmr.enabled = true;
                    CleanupOrphanedTempComponents(setup);
                }
                else
                {
                    // MF ベース: シリアライズ済みの originalMesh から復元
                    var mf = setup.GetComponent<MeshFilter>();
                    if (mf != null && setup.originalMesh != null &&
                        mf.sharedMesh != setup.originalMesh)
                    {
                        mf.sharedMesh = setup.originalMesh;
                    }
                }

                RegisterPreview(setup);
            }
        }

        /// <summary>
        /// Play Mode遷移前にプレビューを全解除（NDMFがクローンする前に元メッシュを復元する）。
        /// Play Mode→Edit復帰時にプレビューを遅延復元する。
        /// </summary>
        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
                CleanupAllPreviews();
            else if (change == PlayModeStateChange.EnteredEditMode)
                EditorApplication.delayCall += RestorePreviewsAfterReload;
        }

        /// <summary>
        /// 全プレビューを解除し、元のメッシュを復元する。
        /// </summary>
        public static void CleanupAllPreviews()
        {
            foreach (var kvp in s_previews)
            {
                var setup = EditorUtility.InstanceIDToObject(kvp.Key) as PiercingSetup;
                CleanupPreviewState(setup, kvp.Value);
            }
            s_previews.Clear();
        }

        /// <summary>
        /// プレビューメッシュに対応する元のメッシュを検索する。
        /// NDMFビルドでクローンされたオブジェクト上のプレビューメッシュを復元するために使用。
        /// </summary>
        public static Mesh FindOriginalMesh(Mesh possiblePreviewMesh)
        {
            if (possiblePreviewMesh == null) return null;

            foreach (var state in s_previews.Values)
            {
                if (state.previewMesh == possiblePreviewMesh)
                    return state.originalSharedMesh;
            }

            return null;
        }

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
                    AnchorTargetPicker(anchorIndex), setup,
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
                            AnchorPiercingPicker(anchorIndex), setup,
                            null, usePiercingMesh: true);
                    }
                }
                else // MultiAnchor — piercing 必須
                {
                    DrawVertexListForAnchor(
                        "Piercing頂点", anchor.piercingVertices,
                        AnchorPiercingPicker(anchorIndex), setup,
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
                        pickerTarget >= PickerTarget.AnchorPiercing0 &&
                        pickerTarget <= PickerTarget.AnchorPiercing7)
                    {
                        int anchorIdx = (int)pickerTarget - (int)PickerTarget.AnchorPiercing0;
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

            // アンカー追加ボタン（PickerTarget enum の上限に合わせて最大8個）
            if (!setup.isPositionSaved)
            {
                using (new EditorGUI.DisabledScope(setup.anchors.Count >= MaxAnchorCount))
                {
                    if (GUILayout.Button("+ アンカーを追加"))
                    {
                        Undo.RecordObject(setup, "Add anchor");
                        setup.anchors.Add(new AnchorPair());
                        EditorUtility.SetDirty(setup);
                    }
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
            if (setup.mode == PiercingMode.Single && setup.referenceVertices.Count == 0)
            {
                var autoSelected = MeshGenerator.FindClosestTriangleVertices(
                    setup.targetRenderer, GetPiercingMeshWorldCenter(setup));
                setup.referenceVertices.AddRange(autoSelected);
                Debug.Log($"[PiercingTool] 参照頂点を自動選択しました: {string.Join(", ", autoSelected)}");
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

        // =================================================================
        // SceneView 参照頂点の可視化
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

        // =================================================================
        // BlendShape追従プレビュー（static管理）
        // =================================================================

        private static void RegisterPreview(PiercingSetup setup)
        {
            int id = setup.GetInstanceID();

            // 既存の状態があればクリーンアップ
            if (s_previews.TryGetValue(id, out var oldState))
                CleanupPreviewState(setup, oldState);

            var state = new PreviewState();

            // ピアス側 SMR の参照を常に保存（CleanupPreviewState での復元に必要）
            var piercingSmr = setup.GetComponent<SkinnedMeshRenderer>();
            state.piercingSmr = piercingSmr;

            // ピアス側 SMR の BlendShape weights を保存済み値に固定
            if (piercingSmr != null && piercingSmr.sharedMesh != null &&
                piercingSmr.sharedMesh.blendShapeCount > 0)
            {
                int count = piercingSmr.sharedMesh.blendShapeCount;
                state.originalPiercingWeights = new float[count];
                for (int i = 0; i < count; i++)
                    state.originalPiercingWeights[i] = piercingSmr.GetBlendShapeWeight(i);

                // 保存済み weights があればそれを適用、なければ 0（BASE ポーズ）
                if (setup.savedPiercingBlendShapeWeights != null)
                {
                    int applyCount = Mathf.Min(setup.savedPiercingBlendShapeWeights.Length, count);
                    for (int i = 0; i < applyCount; i++)
                        piercingSmr.SetBlendShapeWeight(i, setup.savedPiercingBlendShapeWeights[i]);
                }
                else
                {
                    for (int i = 0; i < count; i++)
                        piercingSmr.SetBlendShapeWeight(i, 0f);
                }
            }

            s_previews[id] = state;
        }

        private static void StaticUpdatePreviews()
        {
            // ドメインリロード後の初回: isPositionSaved な Setup を自動再登録
            if (s_needsRestore)
            {
                s_needsRestore = false;
                RestorePreviewsAfterReload();
            }

            if (s_previews.Count == 0) return;

            var toRemove = new List<int>();
            bool anyUpdated = false;

            foreach (var kvp in s_previews)
            {
                var setup = EditorUtility.InstanceIDToObject(kvp.Key) as PiercingSetup;

                // 破棄済み or 位置未保存 → クリーンアップ対象
                if (setup == null || !setup.isPositionSaved)
                {
                    toRemove.Add(kvp.Key);
                    continue;
                }

                if (UpdatePreviewForSetup(setup, kvp.Value))
                    anyUpdated = true;
            }

            foreach (var id in toRemove)
            {
                if (s_previews.TryGetValue(id, out var state))
                {
                    var setup = EditorUtility.InstanceIDToObject(id) as PiercingSetup;
                    CleanupPreviewState(setup, state);
                    s_previews.Remove(id);
                }
            }

            if (anyUpdated)
                SceneView.RepaintAll();
        }

        private static bool UpdatePreviewForSetup(PiercingSetup setup, PreviewState state)
        {
            // ピアス側 SMR の BlendShape weights を保存済み値に維持
            if (state.piercingSmr != null && state.originalPiercingWeights != null &&
                setup.savedPiercingBlendShapeWeights != null)
            {
                int count = Mathf.Min(
                    setup.savedPiercingBlendShapeWeights.Length,
                    state.piercingSmr.sharedMesh != null
                        ? state.piercingSmr.sharedMesh.blendShapeCount : 0);
                for (int i = 0; i < count; i++)
                    state.piercingSmr.SetBlendShapeWeight(i, setup.savedPiercingBlendShapeWeights[i]);
            }

            if (setup.targetRenderer == null) return false;

            var renderer = setup.targetRenderer;
            var sourceMesh = renderer.sharedMesh;
            if (sourceMesh == null) return false;

            // モード別の事前チェック
            if (setup.mode == PiercingMode.Single)
            {
                if (setup.referenceVertices.Count == 0 && !setup.maintainOverallShape)
                    return false;
            }
            else // Chain / MultiAnchor
            {
                if (setup.anchors == null || setup.anchors.Count < 2) return false;
                if (!setup.anchors.All(a => a.targetVertices.Count > 0)) return false;
            }

            // BlendShape weight 変更検知
            int blendShapeCount = sourceMesh.blendShapeCount;
            bool weightsChanged = false;

            if (state.lastWeights == null || state.lastWeights.Length != blendShapeCount)
            {
                state.lastWeights = new float[blendShapeCount];
                weightsChanged = true;
            }

            for (int i = 0; i < blendShapeCount; i++)
            {
                float w = renderer.GetBlendShapeWeight(i);
                if (Mathf.Abs(w - state.lastWeights[i]) > 0.01f)
                    weightsChanged = true;
                state.lastWeights[i] = w;
            }

            if (!weightsChanged) return false;

            // プレビューメッシュの初期化
            if (state.previewMesh == null)
            {
                if (!InitializePreviewMesh(setup, state))
                    return false;

                // Chain/MultiAnchor: セグメントデータを初期化（1回のみ）
                if (setup.mode != PiercingMode.Single)
                {
                    InitSegmentDataForPreview(setup, sourceMesh, state);
                }
            }

            // 剛体変換を計算して頂点に適用
            if (setup.mode == PiercingMode.Single)
                ApplyRigidPreview(setup, sourceMesh, state);
            else
                ApplySegmentPreview(setup, sourceMesh, state);

            return true;
        }

        /// <summary>
        /// プレビューメッシュを初期化する。MF ピアスは既存メッシュを複製、
        /// SMR ピアスは SMR を無効化して一時 MF+MR を追加する。
        /// </summary>
        private static bool InitializePreviewMesh(PiercingSetup setup, PreviewState state)
        {
            var mf = setup.GetComponent<MeshFilter>();
            var piercingSmr = setup.GetComponent<SkinnedMeshRenderer>();

            if (mf != null && (mf.hideFlags & HideFlags.DontSave) == 0)
            {
                // === MF ベース（既存フロー） ===
                state.originalSharedMesh = mf.sharedMesh;
                if (state.originalSharedMesh == null) return false;
                s_originalMeshes[setup.GetInstanceID()] = state.originalSharedMesh;
                state.meshFilter = mf;
                state.previewMesh = Object.Instantiate(state.originalSharedMesh);
                state.previewMesh.name = state.originalSharedMesh.name + "_Preview";
                state.previewMesh.hideFlags = HideFlags.HideAndDontSave;
                state.originalVertices = state.previewMesh.vertices;
                mf.sharedMesh = state.previewMesh;
                state.isSmrPiercing = false;
            }
            else if (piercingSmr != null && piercingSmr.sharedMesh != null)
            {
                // === SMR ベース（新規フロー） ===
                state.isSmrPiercing = true;

                // SMR の sharedMesh を元にプレビューメッシュを作成
                var sourceMesh = piercingSmr.sharedMesh;
                state.originalSharedMesh = sourceMesh;
                state.previewMesh = Object.Instantiate(sourceMesh);
                state.previewMesh.name = sourceMesh.name + "_Preview";
                state.previewMesh.hideFlags = HideFlags.HideAndDontSave;

                // 保存済みピアス BlendShape を頂点にベイク
                if (setup.savedPiercingBlendShapeWeights != null &&
                    setup.savedPiercingBlendShapeWeights.Length > 0 &&
                    state.previewMesh.blendShapeCount > 0)
                {
                    MeshGenerator.BakePiercingBlendShapes(
                        state.previewMesh, setup.savedPiercingBlendShapeWeights);
                }

                state.originalVertices = state.previewMesh.vertices;

                // SMR を無効化
                piercingSmr.enabled = false;

                // 一時的な MeshFilter + MeshRenderer を追加
                var tempMf = setup.gameObject.AddComponent<MeshFilter>();
                tempMf.hideFlags = HideFlags.HideAndDontSave;
                tempMf.sharedMesh = state.previewMesh;

                var tempMr = setup.gameObject.AddComponent<MeshRenderer>();
                tempMr.hideFlags = HideFlags.HideAndDontSave;
                tempMr.sharedMaterials = piercingSmr.sharedMaterials;

                state.meshFilter = tempMf;
                state.tempMeshRenderer = tempMr;

                setup.isSmrPreviewActive = true;
                EditorUtility.SetDirty(setup);
            }
            else
            {
                return false;
            }

            return true;
        }

        private static void ApplyRigidPreview(PiercingSetup setup, Mesh sourceMesh, PreviewState state)
        {
            var renderer = setup.targetRenderer;
            int[] refIndicesArr;

            if (setup.maintainOverallShape)
            {
                var piercingWorldPos = GetPiercingMeshWorldCenter(setup);
                refIndicesArr = MeshGenerator.FindClosestTwoVertices(renderer, piercingWorldPos);
            }
            else
            {
                refIndicesArr = setup.referenceVertices.ToArray();
            }

            // ソース→ピアス座標変換行列
            var sourceToPiercing = setup.transform.worldToLocalMatrix *
                                   renderer.transform.localToWorldMatrix;

            // 保存時の参照頂点位置（ソース空間）
            var savedRefPosSrc = MeshGenerator.ComputeDeformedRefPositions(
                renderer, sourceMesh, refIndicesArr, setup.savedBlendShapeWeights);

            // 現在の参照頂点位置（ソース空間）
            var currentRefPosSrc = MeshGenerator.ComputeDeformedRefPositions(
                renderer, sourceMesh, refIndicesArr, null);

            // ピアス空間に変換
            var savedPiercing = new Vector3[refIndicesArr.Length];
            var currentPiercing = new Vector3[refIndicesArr.Length];
            for (int i = 0; i < refIndicesArr.Length; i++)
            {
                savedPiercing[i] = sourceToPiercing.MultiplyPoint3x4(savedRefPosSrc[i]);
                currentPiercing[i] = sourceToPiercing.MultiplyPoint3x4(currentRefPosSrc[i]);
            }

            // 回転を計算（saved → current）
            Quaternion rotation;
            if (refIndicesArr.Length == 3)
            {
                rotation = BlendShapeTransferEngine.ComputeTriangleFrameRotation(
                    savedPiercing[0], savedPiercing[1], savedPiercing[2],
                    currentPiercing[0], currentPiercing[1], currentPiercing[2]);
            }
            else
            {
                var deltas = new Vector3[refIndicesArr.Length];
                for (int i = 0; i < deltas.Length; i++)
                    deltas[i] = currentPiercing[i] - savedPiercing[i];
                rotation = BlendShapeTransferEngine.ComputeRigidDelta(
                    savedPiercing, deltas).rotation;
            }

            var savedCentroid = BlendShapeTransferEngine.ComputeCentroid(savedPiercing);
            var currentCentroid = BlendShapeTransferEngine.ComputeCentroid(currentPiercing);

            // 順変換をピアス頂点に適用（saved → current）
            var vertices = new Vector3[state.originalVertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = rotation * (state.originalVertices[i] - savedCentroid) + currentCentroid;

            state.previewMesh.vertices = vertices;
            state.previewMesh.RecalculateBounds();
        }

        /// <summary>
        /// Chain/MultiAnchor プレビュー用: セグメントデータを初期化する。
        /// プレビューメッシュ作成時に1回だけ呼ばれる。
        /// </summary>
        private static void InitSegmentDataForPreview(
            PiercingSetup setup, Mesh sourceMesh, PreviewState state)
        {
            var renderer = setup.targetRenderer;
            var sourceToPiercing = setup.transform.worldToLocalMatrix *
                                   renderer.transform.localToWorldMatrix;

            // anchorIndices を構築
            int anchorCount = setup.anchors.Count;
            state.anchorIndices = new int[anchorCount][];
            for (int i = 0; i < anchorCount; i++)
                state.anchorIndices[i] = setup.anchors[i].targetVertices.ToArray();

            // アンカー重心をピアス空間で計算（保存時のBlendShape状態で）
            var savedSourceVerts = sourceMesh.vertices;
            // 保存時のBlendShapeによる変位を加算
            if (setup.savedBlendShapeWeights != null)
            {
                var deformedVerts = new Vector3[savedSourceVerts.Length];
                System.Array.Copy(savedSourceVerts, deformedVerts, savedSourceVerts.Length);
                var deltaV = new Vector3[savedSourceVerts.Length];
                var deltaN = new Vector3[savedSourceVerts.Length];
                var deltaT = new Vector3[savedSourceVerts.Length];
                for (int si = 0; si < sourceMesh.blendShapeCount; si++)
                {
                    float w = si < setup.savedBlendShapeWeights.Length
                        ? setup.savedBlendShapeWeights[si] : 0f;
                    if (Mathf.Abs(w) < 0.01f) continue;
                    int fc = sourceMesh.GetBlendShapeFrameCount(si);
                    float fw = sourceMesh.GetBlendShapeFrameWeight(si, fc - 1);
                    sourceMesh.GetBlendShapeFrameVertices(si, fc - 1, deltaV, deltaN, deltaT);
                    float scale = fw != 0f ? w / fw : 0f;
                    for (int vi = 0; vi < deformedVerts.Length; vi++)
                        deformedVerts[vi] += deltaV[vi] * scale;
                }
                savedSourceVerts = deformedVerts;
            }

            state.anchorCentroids = new Vector3[anchorCount];
            for (int a = 0; a < anchorCount; a++)
            {
                bool hasPiercingSide = a < setup.anchors.Count &&
                                       setup.anchors[a].piercingVertices.Count > 0;
                if (hasPiercingSide)
                {
                    var pVerts = setup.anchors[a].piercingVertices;
                    var piercingVertices = state.originalVertices;
                    var sum = Vector3.zero;
                    int count = 0;
                    foreach (int vi in pVerts)
                    {
                        if (vi < piercingVertices.Length)
                        {
                            sum += piercingVertices[vi];
                            count++;
                        }
                    }
                    state.anchorCentroids[a] = count > 0 ? sum / count : Vector3.zero;
                }
                else
                {
                    // target 頂点の保存時位置をピアス空間に変換して重心を計算
                    var sum = Vector3.zero;
                    foreach (int vi in state.anchorIndices[a])
                    {
                        if (vi < savedSourceVerts.Length)
                            sum += sourceToPiercing.MultiplyPoint3x4(savedSourceVerts[vi]);
                    }
                    state.anchorCentroids[a] = state.anchorIndices[a].Length > 0
                        ? sum / state.anchorIndices[a].Length : Vector3.zero;
                }
            }

            // 各ピアス頂点のセグメント割り当てを計算
            state.segmentData = BlendShapeTransferEngine.ComputeSegmentTValues(
                state.originalVertices, state.anchorCentroids);
        }

        /// <summary>
        /// Chain/MultiAnchor プレビュー: 各アンカーの剛体デルタをセグメント補間して適用する。
        /// </summary>
        private static void ApplySegmentPreview(
            PiercingSetup setup, Mesh sourceMesh, PreviewState state)
        {
            if (state.anchorIndices == null || state.segmentData == null) return;

            var renderer = setup.targetRenderer;
            var sourceToPiercing = setup.transform.worldToLocalMatrix *
                                   renderer.transform.localToWorldMatrix;

            int anchorCount = state.anchorIndices.Length;
            var sourceVertices = sourceMesh.vertices;

            // 各アンカーの saved/current 位置をピアス空間で計算
            var savedDeltas = new (Vector3 translation, Quaternion rotation)[anchorCount];

            for (int a = 0; a < anchorCount; a++)
            {
                var indices = state.anchorIndices[a];

                // 保存時位置（ピアス空間）
                var savedPos = MeshGenerator.ComputeDeformedRefPositions(
                    renderer, sourceMesh, indices, setup.savedBlendShapeWeights);
                var savedPiercing = new Vector3[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                    savedPiercing[i] = sourceToPiercing.MultiplyPoint3x4(savedPos[i]);

                // 現在位置（ピアス空間）
                var currentPos = MeshGenerator.ComputeDeformedRefPositions(
                    renderer, sourceMesh, indices, null);
                var currentPiercing = new Vector3[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                    currentPiercing[i] = sourceToPiercing.MultiplyPoint3x4(currentPos[i]);

                // 剛体デルタ（回転 + 平行移動）
                Quaternion rotation;
                if (indices.Length == 3)
                {
                    rotation = BlendShapeTransferEngine.ComputeTriangleFrameRotation(
                        savedPiercing[0], savedPiercing[1], savedPiercing[2],
                        currentPiercing[0], currentPiercing[1], currentPiercing[2]);
                }
                else
                {
                    var deltas = new Vector3[indices.Length];
                    for (int i = 0; i < deltas.Length; i++)
                        deltas[i] = currentPiercing[i] - savedPiercing[i];
                    rotation = BlendShapeTransferEngine.ComputeRigidDelta(
                        savedPiercing, deltas).rotation;
                }

                var savedCentroid = BlendShapeTransferEngine.ComputeCentroid(savedPiercing);
                var currentCentroid = BlendShapeTransferEngine.ComputeCentroid(currentPiercing);
                var translation = currentCentroid - savedCentroid;

                savedDeltas[a] = (translation, rotation);
            }

            // 各ピアス頂点にセグメント補間で適用
            var vertices = new Vector3[state.originalVertices.Length];
            for (int vi = 0; vi < vertices.Length; vi++)
            {
                var (seg, t) = state.segmentData[vi];
                int a0 = seg;
                int a1 = Mathf.Min(seg + 1, anchorCount - 1);

                var rot = Quaternion.Slerp(savedDeltas[a0].rotation, savedDeltas[a1].rotation, t);
                var trans = Vector3.Lerp(savedDeltas[a0].translation, savedDeltas[a1].translation, t);
                var pivot = Vector3.Lerp(state.anchorCentroids[a0], state.anchorCentroids[a1], t);

                var localPos = state.originalVertices[vi] - pivot;
                vertices[vi] = rot * localPos + pivot + trans;
            }

            state.previewMesh.vertices = vertices;
            state.previewMesh.RecalculateBounds();
        }

        private static void CleanupPreviewState(PiercingSetup setup, PreviewState state)
        {
            if (state.previewMesh != null)
            {
                if (state.isSmrPiercing)
                {
                    // SMR ピアス: 一時コンポーネントを破棄し、SMR を再有効化
                    if (state.meshFilter != null)
                        Object.DestroyImmediate(state.meshFilter);
                    if (state.tempMeshRenderer != null)
                        Object.DestroyImmediate(state.tempMeshRenderer);
                    if (state.piercingSmr != null)
                        state.piercingSmr.enabled = true;
                    if (setup != null)
                    {
                        setup.isSmrPreviewActive = false;
                        EditorUtility.SetDirty(setup);
                    }
                }
                else
                {
                    // MF ベース: 元メッシュを復元
                    if (state.meshFilter != null && state.originalSharedMesh != null)
                        state.meshFilter.sharedMesh = state.originalSharedMesh;
                }

                Object.DestroyImmediate(state.previewMesh);
            }

            // ピアス側 SMR の BlendShape weights を復元
            if (state.piercingSmr != null && state.originalPiercingWeights != null)
            {
                int count = Mathf.Min(
                    state.originalPiercingWeights.Length,
                    state.piercingSmr.sharedMesh != null
                        ? state.piercingSmr.sharedMesh.blendShapeCount : 0);
                for (int i = 0; i < count; i++)
                    state.piercingSmr.SetBlendShapeWeight(i, state.originalPiercingWeights[i]);
            }
        }

        /// <summary>
        /// HideAndDontSave な孤立 MeshFilter/MeshRenderer を削除する。
        /// ドメインリロードや Undo で PreviewState が失われた場合のクリーンアップ用。
        /// </summary>
        private static void CleanupOrphanedTempComponents(PiercingSetup setup)
        {
            foreach (var mf in setup.GetComponents<MeshFilter>())
            {
                if ((mf.hideFlags & HideFlags.DontSave) != 0)
                    Object.DestroyImmediate(mf);
            }
            foreach (var mr in setup.GetComponents<MeshRenderer>())
            {
                if ((mr.hideFlags & HideFlags.DontSave) != 0)
                    Object.DestroyImmediate(mr);
            }
        }

        /// <summary>
        /// シーン保存前に元のメッシュを復元（プレビューメッシュがシリアライズされるのを防ぐ）。
        /// </summary>
        private static void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
        {
            foreach (var kvp in s_previews)
            {
                var state = kvp.Value;
                if (state.isSmrPiercing)
                {
                    // SMR を一時的に再有効化（disabled 状態がシリアライズされるのを防ぐ）
                    if (state.piercingSmr != null)
                        state.piercingSmr.enabled = true;
                }
                else
                {
                    if (state.meshFilter != null && state.originalSharedMesh != null)
                        state.meshFilter.sharedMesh = state.originalSharedMesh;
                }
            }
        }

        /// <summary>
        /// シーン保存後にプレビューメッシュを再適用。
        /// </summary>
        private static void OnSceneSaved(UnityEngine.SceneManagement.Scene scene)
        {
            foreach (var kvp in s_previews)
            {
                var state = kvp.Value;
                if (state.isSmrPiercing)
                {
                    // SMR を再無効化
                    if (state.piercingSmr != null)
                        state.piercingSmr.enabled = false;
                }
                else
                {
                    if (state.meshFilter != null && state.previewMesh != null)
                        state.meshFilter.sharedMesh = state.previewMesh;
                }
            }
        }

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
                else if (setup.mode == PiercingMode.Chain || setup.mode == PiercingMode.MultiAnchor)
                {
                    if (setup.anchors != null)
                    {
                        for (int i = 0; i < setup.anchors.Count; i++)
                        {
                            var av = setup.anchors[i].targetVertices;
                            if (av.Count >= 2)
                            {
                                var qa = EvaluateVertexQuality(sourceVerts, av);
                                if (qa != VertexQuality.Ok)
                                {
                                    string anchorLabel = setup.mode == PiercingMode.Chain
                                        ? (i == 0 ? "Anchor A" : "Anchor B")
                                        : $"Anchor {i + 1}";
                                    EditorGUILayout.HelpBox(
                                        $"{anchorLabel}: {GetQualityMessage(qa)}", MessageType.Warning);
                                }
                            }
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
