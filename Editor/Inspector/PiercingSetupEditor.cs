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
        private SerializedProperty _mergeIntoTarget;
        private SerializedProperty _skipBoneWeightTransfer;
        private SerializedProperty _fixedPiercingRadius;
        private SerializedProperty _perVertexBoneWeights;
        private SerializedProperty _maintainOverallShape;
        private SerializedProperty _surfaceAttachment;
        private SerializedProperty _useMultiGroup;
        private SerializedProperty _positionOffset;
        private SerializedProperty _rotationEuler;

        private VertexPickerTool _pickerTool;

        private struct PickerTarget : System.IEquatable<PickerTarget>
        {
            public enum Kind { None, Single, AnchorTarget, AnchorPiercing, FixedPiercing,
                               GroupSingle, GroupAnchorTarget, GroupAnchorPiercing }
            public Kind kind;
            public int index;
            public int groupIndex;

            public static readonly PickerTarget Single = new PickerTarget { kind = Kind.Single };
            public static readonly PickerTarget Fixed = new PickerTarget { kind = Kind.FixedPiercing };
            public static PickerTarget AnchorTarget(int i)
                => new PickerTarget { kind = Kind.AnchorTarget, index = i };
            public static PickerTarget AnchorPiercing(int i)
                => new PickerTarget { kind = Kind.AnchorPiercing, index = i };
            public static PickerTarget GroupSingleRef(int gi)
                => new PickerTarget { kind = Kind.GroupSingle, groupIndex = gi };
            public static PickerTarget GroupAnchorTgt(int gi, int ai)
                => new PickerTarget { kind = Kind.GroupAnchorTarget, groupIndex = gi, index = ai };
            public static PickerTarget GroupAnchorPrc(int gi, int ai)
                => new PickerTarget { kind = Kind.GroupAnchorPiercing, groupIndex = gi, index = ai };

            public bool Equals(PickerTarget other)
                => kind == other.kind && index == other.index && groupIndex == other.groupIndex;
            public override bool Equals(object obj)
                => obj is PickerTarget other && Equals(other);
            public override int GetHashCode()
                => ((int)kind * 397) ^ (index * 31) ^ groupIndex;
            public static bool operator ==(PickerTarget a, PickerTarget b) => a.Equals(b);
            public static bool operator !=(PickerTarget a, PickerTarget b) => !a.Equals(b);
        }

        private PickerTarget _activePickerTarget;

        /// <summary>
        /// Chain モードで「ピアス側の頂点」トグルの OFF 状態を保持する。
        /// piercingVertices が空でもトグルが戻らないようにするためのエディタ限定状態。
        /// </summary>
        private readonly HashSet<int> _showPiercingForAnchor = new HashSet<int>();
        private int _anchorDeleteRequested = -1;
        private static GUIStyle _darkBoxStyle;
        private static GUIStyle _groupHeaderStyle;
        private static GUIStyle _selectedGroupHeaderStyle;
        private static Texture2D _borderTex;
        private static GUIStyle DarkBoxStyle
        {
            get
            {
                if (_darkBoxStyle == null)
                {
                    _darkBoxStyle = new GUIStyle();
                    _darkBoxStyle.padding = new RectOffset(6, 6, 4, 4);
                    _darkBoxStyle.margin = new RectOffset(2, 2, 2, 2);
                    var bgTex = new Texture2D(1, 1);
                    bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.15f));
                    bgTex.Apply();
                    _darkBoxStyle.normal.background = bgTex;
                }
                return _darkBoxStyle;
            }
        }
        private static GUIStyle GroupHeaderStyle
        {
            get
            {
                if (_groupHeaderStyle == null)
                {
                    _groupHeaderStyle = new GUIStyle();
                    _groupHeaderStyle.padding = new RectOffset(6, 6, 4, 4);
                    _groupHeaderStyle.margin = new RectOffset(2, 2, 1, 1);
                    var bgTex = new Texture2D(1, 1);
                    bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.1f));
                    bgTex.Apply();
                    _groupHeaderStyle.normal.background = bgTex;
                }
                return _groupHeaderStyle;
            }
        }
        private static GUIStyle SelectedGroupHeaderStyle
        {
            get
            {
                if (_selectedGroupHeaderStyle == null)
                {
                    _selectedGroupHeaderStyle = new GUIStyle();
                    _selectedGroupHeaderStyle.padding = new RectOffset(6, 6, 4, 4);
                    _selectedGroupHeaderStyle.margin = new RectOffset(2, 2, 1, 1);
                    var bgTex = new Texture2D(1, 1);
                    bgTex.SetPixel(0, 0, new Color(0.2f, 0.4f, 0.8f, 0.25f));
                    bgTex.Apply();
                    _selectedGroupHeaderStyle.normal.background = bgTex;
                }
                return _selectedGroupHeaderStyle;
            }
        }
        private static Texture2D BorderTex
        {
            get
            {
                if (_borderTex == null)
                {
                    _borderTex = new Texture2D(1, 1);
                    _borderTex.SetPixel(0, 0, new Color(0.5f, 0.5f, 0.5f, 0.5f));
                    _borderTex.Apply();
                }
                return _borderTex;
            }
        }

        /// <summary>
        /// Single モードで referenceVertices が空のとき、現在の位置から自動検出した頂点。
        /// Inspector/SceneView 表示用の一時キャッシュ（コンポーネントには保存しない）。
        /// </summary>
        private int[] _autoDetectedVertices;
        private Dictionary<int, int[]> _groupAutoDetectedVertices;

        // グループドラッグ並び替え用
        private int _dragGroupIndex = -1;
        private int _dragInsertIndex = -1;
        private List<Rect> _groupHeaderRects = new List<Rect>();

        private void OnEnable()
        {
            _mode = serializedObject.FindProperty("mode");
            _targetRenderer = serializedObject.FindProperty("targetRenderer");
            _mergeIntoTarget = serializedObject.FindProperty("mergeIntoTarget");
            _skipBoneWeightTransfer = serializedObject.FindProperty("skipBoneWeightTransfer");
            _fixedPiercingRadius = serializedObject.FindProperty("fixedPiercingRadius");
            _perVertexBoneWeights = serializedObject.FindProperty("perVertexBoneWeights");
            _maintainOverallShape = serializedObject.FindProperty("maintainOverallShape");
            _surfaceAttachment = serializedObject.FindProperty("surfaceAttachment");
            _useMultiGroup = serializedObject.FindProperty("useMultiGroup");
            _positionOffset = serializedObject.FindProperty("positionOffset");
            _rotationEuler = serializedObject.FindProperty("rotationEuler");
            SceneView.duringSceneGui += DrawSceneVisualization;
        }

        private void OnDisable()
        {
            _pickerTool?.Deactivate();
            SceneView.duringSceneGui -= DrawSceneVisualization;
            // デフォルトハンドルを復元
            Tools.hidden = false;
            // プレビューはstatic管理のため、ここではクリーンアップしない
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var setup = (PiercingSetup)target;

            // --- Target Renderer ---
            EditorGUILayout.PropertyField(_targetRenderer, new GUIContent("対象Renderer",
                "顔など，追従させたい対象のGameObjectを割り当ててください"));
            EditorGUILayout.Space();

            // --- マルチモード ---
            using (new EditorGUI.DisabledScope(setup.isPositionSaved))
            {
                EditorGUILayout.PropertyField(_useMultiGroup, new GUIContent("マルチモード",
                    "このメッシュに複数のピアスが含まれている時に使えます。複数のピアスを個別に設定できます。"));
            }

            if (!setup.useMultiGroup)
            {
                // --- 位置調整（非マルチモード、保存前のみ） ---
                if (!setup.isPositionSaved)
                {
                    DrawPositionAdjustmentSection(setup);
                }

                // --- モード ---
                using (new EditorGUI.DisabledScope(setup.isPositionSaved))
                {
                    EditorGUILayout.PropertyField(_mode, new GUIContent("追従モード"));
                }
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
                        var center = GetPiercingMeshWorldCenter(setup);
                        if (setup.positionOffset != Vector3.zero)
                            center += setup.transform.TransformVector(setup.positionOffset);
                        _autoDetectedVertices = FindClosestTriangle(
                            worldVerts, setup.targetRenderer.sharedMesh.triangles,
                            center);
                    }
                }
                else
                {
                    _autoDetectedVertices = null;
                }

                // --- 頂点選択（保存中は読み取り専用） ---
                bool isHybridMode = setup.skipBoneWeightTransfer &&
                                    setup.fixedPiercingVertices.Count > 0;
                if (setup.mode == PiercingMode.Single)
                {
                    // ハイブリッドモードでは参照頂点は自動検出されるため非表示
                    if (!isHybridMode)
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
                        {
                            _perVertexBoneWeights.boolValue = false;
                            _surfaceAttachment.boolValue = false;
                        }

                        EditorGUI.BeginChangeCheck();
                        EditorGUILayout.PropertyField(_surfaceAttachment,
                            new GUIContent("舌ピが浮く場合の調整設定",
                                "舌などの非剛体変形する部位でピアスが浮いたり埋まったりする場合に有効にしてください。\n" +
                                "ピアスを対象メッシュ表面に紐付けて追従させます。"));
                        if (EditorGUI.EndChangeCheck() && _surfaceAttachment.boolValue)
                            _maintainOverallShape.boolValue = false;
                    }

                    EditorGUILayout.Space();
                    DrawMergeAndSkipOptions();
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
                    DrawMergeAndSkipOptions();
                }
                else if (setup.mode == PiercingMode.MultiAnchor)
                {
                    EnsureMinAnchors(setup, 2);
                    DrawMultiAnchorUI(setup);
                }
            }
            else
            {
                _autoDetectedVertices = null;
                UpdateGroupAutoDetectedVertices(setup);
                DrawMultiGroupUI(setup);
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

            // マルチモード時の PhysBone 制限注記
            if (setup.useMultiGroup)
            {
                EditorGUILayout.HelpBox(
                    "マルチモードではPhysBoneで揺れるピアスは利用できません。\n" +
                    "PhysBoneが設定されたピアスはマルチモードをOFFにしてご利用ください。",
                    MessageType.Info);
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

                        string buttonText = isThisPickerActive ? "選択中..." : "頂点を手動で選択";
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
            bool showLabel = true, bool showDeleteButton = false, bool canDelete = false)
        {
            var anchor = setup.anchors[anchorIndex];

            var scope = new EditorGUILayout.VerticalScope(DarkBoxStyle);
            using (scope)
            {
            if (showLabel && showDeleteButton)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
                    using (new EditorGUI.DisabledScope(setup.isPositionSaved || !canDelete))
                    {
                        if (GUILayout.Button("\u00d7", GUILayout.Width(22)))
                            _anchorDeleteRequested = anchorIndex;
                    }
                }
            }
            else if (showLabel)
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);

            // ピアス側指定（Chain モードではオプション、デフォルト表示）
                if (setup.mode == PiercingMode.Chain)
                {
                    bool showPiercing = anchor.piercingVertices.Count > 0 ||
                                        !_showPiercingForAnchor.Contains(anchorIndex);
                    bool newShowPiercing = EditorGUILayout.Toggle(
                        "ピアス側の頂点", showPiercing);

                    if (!newShowPiercing && showPiercing)
                    {
                        _showPiercingForAnchor.Add(anchorIndex);
                        if (anchor.piercingVertices.Count > 0)
                        {
                            Undo.RecordObject(setup, "Clear piercing vertices");
                            anchor.piercingVertices.Clear();
                            EditorUtility.SetDirty(setup);
                        }
                    }
                    else if (newShowPiercing && !showPiercing)
                    {
                        _showPiercingForAnchor.Remove(anchorIndex);
                    }

                    if (newShowPiercing || anchor.piercingVertices.Count > 0)
                    {
                        DrawVertexListForAnchor(
                            "対象頂点", anchor.piercingVertices,
                            PickerTarget.AnchorPiercing(anchorIndex), setup,
                            null, usePiercingMesh: true);
                    }
                }
                else // MultiAnchor — piercing 必須
                {
                    DrawVertexListForAnchor(
                        "対象頂点", anchor.piercingVertices,
                        PickerTarget.AnchorPiercing(anchorIndex), setup,
                        null, usePiercingMesh: true);
                }

                // 参照頂点（つける対象の頂点）
                DrawVertexListForAnchor(
                    "参照頂点(つける対象の頂点)", anchor.targetVertices,
                    PickerTarget.AnchorTarget(anchorIndex), setup,
                    setup.targetRenderer);
            }
            // 枠線を描画
            if (Event.current.type == EventType.Repaint)
            {
                var r = scope.rect;
                GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1), BorderTex);
                GUI.DrawTexture(new Rect(r.x, r.yMax - 1, r.width, 1), BorderTex);
                GUI.DrawTexture(new Rect(r.x, r.y, 1, r.height), BorderTex);
                GUI.DrawTexture(new Rect(r.xMax - 1, r.y, 1, r.height), BorderTex);
            }
        }

        private void DrawVertexListForAnchor(
            string label, List<int> vertices,
            PickerTarget pickerTarget, PiercingSetup setup,
            SkinnedMeshRenderer renderer,
            bool usePiercingMesh = false)
        {
            if (pickerTarget.kind == PickerTarget.Kind.FixedPiercing)
                EditorGUILayout.LabelField(label);
            else
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
                    bool isTargetPicker = pickerTarget.kind == PickerTarget.Kind.AnchorTarget
                                        || pickerTarget.kind == PickerTarget.Kind.GroupAnchorTarget
                                        || pickerTarget.kind == PickerTarget.Kind.GroupSingle;
                    string btnLabel = isTargetPicker ? "手動で再選択" : "選択";
                    string btnText = isActive ? "選択中..." : btnLabel;
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

                        // 対象頂点クリア時は対応アンカーの参照頂点もクリア
                        if (pickerTarget.kind == PickerTarget.Kind.AnchorPiercing)
                        {
                            var anchor = setup.anchors[pickerTarget.index];
                            anchor.targetVertices.Clear();
                        }
                        else if (pickerTarget.kind == PickerTarget.Kind.GroupAnchorPiercing)
                        {
                            var grp = setup.piercingGroups[pickerTarget.groupIndex];
                            if (pickerTarget.index < grp.anchors.Count)
                                grp.anchors[pickerTarget.index].targetVertices.Clear();
                        }

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
                singleSelectMode = usePiercingMesh &&
                    (pickerTarget.kind == PickerTarget.Kind.AnchorPiercing ||
                     pickerTarget.kind == PickerTarget.Kind.GroupAnchorPiercing),
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
                    else if (usePiercingMesh &&
                        pickerTarget.kind == PickerTarget.Kind.GroupAnchorPiercing)
                    {
                        AutoDetectTargetForGroupAnchor(setup, pickerTarget.groupIndex, pickerTarget.index);
                    }

                    // 1点選択モードでは選択後に自動解除
                    if (usePiercingMesh &&
                        (pickerTarget.kind == PickerTarget.Kind.AnchorPiercing ||
                         pickerTarget.kind == PickerTarget.Kind.GroupAnchorPiercing) &&
                        vertices.Count >= 1)
                    {
                        _pickerTool?.Deactivate();
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
                DrawAnchorSection($"Anchor {i + 1}", i, setup, showLabel: true,
                    showDeleteButton: true, canDelete: setup.anchors.Count > 2);

                // 削除が要求された場合
                if (_anchorDeleteRequested == i)
                {
                    _anchorDeleteRequested = -1;
                    Undo.RecordObject(setup, "Remove anchor");
                    setup.anchors.RemoveAt(i);
                    EditorUtility.SetDirty(setup);
                    break;
                }

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
            DrawMergeAndSkipOptions();
        }

        private void DrawMergeAndSkipOptions()
        {
            var setup = (PiercingSetup)target;

            EditorGUI.BeginChangeCheck();
            using (new EditorGUI.DisabledScope(_skipBoneWeightTransfer.boolValue))
            {
                EditorGUILayout.PropertyField(_mergeIntoTarget,
                    new GUIContent("lipsync・MMD用設定(メッシュを統合)",
                        "リップシンクの精度向上・MMDワールドでの口追従に有効\nPhysBoneが含まれるピアスには推奨されません"));
            }
            if (EditorGUI.EndChangeCheck() && _mergeIntoTarget.boolValue)
                _skipBoneWeightTransfer.boolValue = false;

            // マルチモードではボーンウェイト転写スキップ非対応
            bool disableSkip = _mergeIntoTarget.boolValue || setup.useMultiGroup;

            EditorGUI.BeginChangeCheck();
            using (new EditorGUI.DisabledScope(disableSkip))
            {
                EditorGUILayout.PropertyField(_skipBoneWeightTransfer,
                    new GUIContent("ボーンウェイト転写をスキップ(PhysBoneで揺らす場合はチェックを入れてください。)",
                        setup.useMultiGroup
                            ? "マルチモードではこの機能は利用できません"
                            : "PhysBone設定済みの場合にチェック"));
            }
            if (EditorGUI.EndChangeCheck() && _skipBoneWeightTransfer.boolValue)
                _mergeIntoTarget.boolValue = false;

            // マルチモード有効時にスキップがONだった場合は強制OFF
            if (setup.useMultiGroup && _skipBoneWeightTransfer.boolValue)
                _skipBoneWeightTransfer.boolValue = false;

            // ハイブリッドモード: skipBoneWeightTransfer ON + SMR ありの場合に固定範囲ピッカーを表示
            if (_skipBoneWeightTransfer.boolValue)
            {
                var piercingSmr = setup.GetComponent<SkinnedMeshRenderer>();
                if (piercingSmr != null && piercingSmr.sharedMesh != null)
                {
                    EditorGUILayout.Space(4);
                    DrawVertexListForAnchor(
                        "追従させる範囲を指定", setup.fixedPiercingVertices,
                        PickerTarget.Fixed, setup,
                        setup.targetRenderer,
                        usePiercingMesh: true);

                    if (setup.fixedPiercingVertices.Count > 0)
                    {
                        EditorGUILayout.Slider(_fixedPiercingRadius, 0.001f, 0.1f,
                            new GUIContent("固定範囲の半径",
                                "中心頂点からこの半径内の頂点が顔メッシュに追従します"));

                        EditorGUILayout.HelpBox(
                            "範囲内の頂点は対象RendererのBlendShapeに追従します。範囲外の頂点はBone構造が維持されます。\n表情などに沿って動かしたい部分のみ球内に納めてください。",
                            MessageType.Info);
                    }
                }
            }
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

            if (setup.useMultiGroup)
            {
                if (setup.piercingGroups == null || setup.piercingGroups.Count == 0)
                    return false;
                return setup.piercingGroups.All(g => IsGroupReady(g));
            }

            if (setup.mode == PiercingMode.Single)
                return true; // 参照頂点が空の場合は自動選択される
            else // Chain / MultiAnchor
            {
                return setup.anchors != null && setup.anchors.Count >= 2 &&
                       setup.anchors.All(a => a.targetVertices.Count > 0);
            }
        }

        private static bool IsGroupReady(PiercingGroup group)
        {
            if (group == null) return false;
            if (group.mode == PiercingMode.Single)
                return true; // 参照頂点が空でも自動選択される
            else // Chain / MultiAnchor
            {
                return group.anchors != null && group.anchors.Count >= 2 &&
                       group.anchors.All(a => a.targetVertices.Count > 0);
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
            // 参照頂点は referenceVertices に書き込まない。
            // MeshGenerator は referenceVertices が空でも自動検出するため、
            // ここで書き込むと位置解除時に手動選択として残ってしまう。

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
        // マルチグループモード UI
        // =================================================================

        private void DrawMultiGroupUI(PiercingSetup setup)
        {
            EditorGUILayout.Space();

            if (setup.piercingGroups == null)
                setup.piercingGroups = new System.Collections.Generic.List<PiercingGroup>();

            if (setup.piercingGroups.Count == 0)
            {
                EditorGUILayout.HelpBox("アイランドを自動検出してピアスを検出してください。", MessageType.Info);
                using (new EditorGUI.DisabledScope(setup.isPositionSaved))
                {
                    if (GUILayout.Button("アイランドを自動検出"))
                        DetectIslands(setup);
                }
            }
            else
            {
                // 再検出ボタン
                using (new EditorGUI.DisabledScope(setup.isPositionSaved))
                {
                    if (GUILayout.Button("再検出"))
                        DetectIslands(setup);
                }
                EditorGUILayout.Space(4);

                // 全グループをアコーディオン表示
                int groupCount = setup.piercingGroups.Count;
                if (setup.selectedGroupIndex >= groupCount)
                    setup.selectedGroupIndex = groupCount - 1;

                // ヘッダーRect収集（ドラッグ用）
                if (Event.current.type == EventType.Layout)
                    _groupHeaderRects = new List<Rect>(new Rect[groupCount]);

                for (int gi = 0; gi < groupCount; gi++)
                {
                    // ドラッグ中の挿入インジケーター（対象の上に表示）
                    if (_dragGroupIndex >= 0 && _dragInsertIndex == gi && _dragInsertIndex != _dragGroupIndex)
                    {
                        var indicatorRect = EditorGUILayout.GetControlRect(false, 2);
                        EditorGUI.DrawRect(indicatorRect, new Color(0.3f, 0.6f, 1f, 0.8f));
                    }

                    var group = setup.piercingGroups[gi];
                    bool isSelected = gi == setup.selectedGroupIndex;
                    bool isDragSource = gi == _dragGroupIndex;
                    string groupLabel = string.IsNullOrEmpty(group.name)
                        ? $"ピアス {gi + 1}" : group.name;

                    // グループヘッダー
                    var prevAlpha = GUI.color.a;
                    if (isDragSource)
                        GUI.color = new Color(GUI.color.r, GUI.color.g, GUI.color.b, 0.4f);

                    var headerRect = EditorGUILayout.BeginHorizontal(
                        isSelected ? SelectedGroupHeaderStyle : GroupHeaderStyle);
                    {
                        string arrow = isSelected ? "\u25bc" : "\u25b6";
                        string modeLabel = group.mode == PiercingMode.Single ? "シングル"
                            : group.mode == PiercingMode.Chain ? "チェーン" : "複数点指定";
                        GUILayout.Label($"{arrow} {groupLabel}  ({modeLabel}, {group.vertexIndices.Count}頂点)",
                            isSelected ? EditorStyles.boldLabel : EditorStyles.label);
                    }
                    EditorGUILayout.EndHorizontal();

                    if (isDragSource)
                        GUI.color = new Color(GUI.color.r, GUI.color.g, GUI.color.b, prevAlpha);

                    // Rect記録
                    if (gi < _groupHeaderRects.Count)
                        _groupHeaderRects[gi] = headerRect;

                    // マウスイベント処理
                    if (headerRect.Contains(Event.current.mousePosition))
                    {
                        if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
                        {
                            // 左クリック: 展開/折り畳み + ドラッグ開始準備
                            _dragGroupIndex = gi;
                            Event.current.Use();
                        }
                        else if (Event.current.type == EventType.MouseDown && Event.current.button == 1)
                        {
                            // 右クリック: コンテキストメニュー
                            ShowGroupContextMenu(setup, gi);
                            Event.current.Use();
                        }
                    }

                    // 選択中グループの設定を展開
                    if (isSelected)
                        DrawGroupSettings(group, setup);

                    EditorGUILayout.Space(2);
                }

                // ドラッグ中の末尾インジケーター
                if (_dragGroupIndex >= 0 && _dragInsertIndex == groupCount &&
                    _dragInsertIndex != _dragGroupIndex)
                {
                    var indicatorRect = EditorGUILayout.GetControlRect(false, 2);
                    EditorGUI.DrawRect(indicatorRect, new Color(0.3f, 0.6f, 1f, 0.8f));
                }

                // ドラッグ処理
                HandleGroupDrag(setup, groupCount);

                EditorGUILayout.Space();
                DrawMergeAndSkipOptions();
            }
        }

        // =================================================================
        // 位置調整 UI
        // =================================================================

        private void DrawPositionAdjustmentSection(PiercingSetup setup)
        {
            EditorGUILayout.BeginVertical(DarkBoxStyle);

            EditorGUILayout.LabelField("位置調整", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_positionOffset, new GUIContent("位置オフセット"));
            EditorGUILayout.PropertyField(_rotationEuler, new GUIContent("回転"));

            if (setup.positionOffset != Vector3.zero || setup.rotationEuler != Vector3.zero)
            {
                if (GUILayout.Button("リセット", GUILayout.Width(80)))
                {
                    Undo.RecordObject(setup, "Reset piercing offset");
                    setup.positionOffset = Vector3.zero;
                    setup.rotationEuler = Vector3.zero;
                    EditorUtility.SetDirty(setup);
                }
            }

            EditorGUILayout.EndVertical();

            // 枠線を描画
            if (Event.current.type == EventType.Repaint)
            {
                var r = GUILayoutUtility.GetLastRect();
                GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1), BorderTex);
                GUI.DrawTexture(new Rect(r.x, r.y + r.height - 1, r.width, 1), BorderTex);
                GUI.DrawTexture(new Rect(r.x, r.y, 1, r.height), BorderTex);
                GUI.DrawTexture(new Rect(r.x + r.width - 1, r.y, 1, r.height), BorderTex);
            }

            EditorGUILayout.Space();
        }

        private void DrawGroupPositionAdjustment(PiercingGroup group, PiercingSetup setup)
        {
            EditorGUILayout.LabelField("位置調整", EditorStyles.miniLabel);

            EditorGUI.BeginChangeCheck();
            group.positionOffset = EditorGUILayout.Vector3Field("位置オフセット", group.positionOffset);
            group.rotationEuler = EditorGUILayout.Vector3Field("回転", group.rotationEuler);
            if (EditorGUI.EndChangeCheck())
                EditorUtility.SetDirty(setup);

            if (group.positionOffset != Vector3.zero || group.rotationEuler != Vector3.zero)
            {
                if (GUILayout.Button("リセット", GUILayout.Width(80)))
                {
                    Undo.RecordObject(setup, "Reset group offset");
                    group.positionOffset = Vector3.zero;
                    group.rotationEuler = Vector3.zero;
                    EditorUtility.SetDirty(setup);
                }
            }
        }

        private void DrawGroupSettings(PiercingGroup group, PiercingSetup setup)
        {
            var scope = new EditorGUILayout.VerticalScope(DarkBoxStyle);
            using (scope)
            {
                using (new EditorGUI.DisabledScope(setup.isPositionSaved))
                {
                    EditorGUI.BeginChangeCheck();
                    var newMode = (PiercingMode)EditorGUILayout.EnumPopup("追従モード", group.mode);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(setup, "Change group mode");
                        group.mode = newMode;
                        EditorUtility.SetDirty(setup);
                    }
                }

                // 位置調整（保存前のみ）
                if (!setup.isPositionSaved)
                {
                    EditorGUILayout.Space(4);
                    DrawGroupPositionAdjustment(group, setup);
                }

                EditorGUILayout.Space(4);

                if (group.mode == PiercingMode.Single)
                    DrawGroupSingleSettings(group, setup);
                else
                    DrawGroupAnchorSettings(group, setup);
            }
            // 枠線を描画
            if (Event.current.type == EventType.Repaint)
            {
                var r = scope.rect;
                GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1), BorderTex);
                GUI.DrawTexture(new Rect(r.x, r.yMax - 1, r.width, 1), BorderTex);
                GUI.DrawTexture(new Rect(r.x, r.y, 1, r.height), BorderTex);
                GUI.DrawTexture(new Rect(r.xMax - 1, r.y, 1, r.height), BorderTex);
            }
        }

        private void DrawGroupSingleSettings(PiercingGroup group, PiercingSetup setup)
        {
            int gi = setup.piercingGroups.IndexOf(group);

            // 自動検出頂点がある場合は表示
            int[] autoDetected = null;
            bool hasAutoDetected = _groupAutoDetectedVertices != null &&
                _groupAutoDetectedVertices.TryGetValue(gi, out autoDetected) &&
                autoDetected != null;
            // 参照頂点が未指定の場合は自動検出扱い（保存後でもキャッシュがなくても）
            if (group.referenceVertices.Count == 0 && (hasAutoDetected || setup.isPositionSaved))
            {
                EditorGUILayout.LabelField("参照頂点: 自動検出");

                // 手動選択ボタン
                if (!setup.isPositionSaved)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool isActive = _pickerTool != null && _pickerTool.isActive &&
                                        _activePickerTarget.Equals(PickerTarget.GroupSingleRef(gi));
                        string btnText = isActive ? "選択中..." : "手動で再選択";
                        var btnStyle = isActive
                            ? new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold }
                            : GUI.skin.button;
                        if (GUILayout.Button(btnText, btnStyle))
                        {
                            if (isActive)
                                _pickerTool.Deactivate();
                            else
                                StartAnchorPicker(setup, group.referenceVertices,
                                    PickerTarget.GroupSingleRef(gi), false);
                        }
                    }
                }
            }
            else
            {
                // 参照頂点（個数+ピッカーのみ、座標リストは省略）
                EditorGUILayout.LabelField($"参照頂点: {group.referenceVertices.Count}個");
                if (!setup.isPositionSaved)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool isActive = _pickerTool != null && _pickerTool.isActive &&
                                        _activePickerTarget.Equals(PickerTarget.GroupSingleRef(gi));
                        string btnText = isActive ? "選択中..." : "手動で再選択";
                        var btnStyle = isActive
                            ? new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold }
                            : GUI.skin.button;
                        if (GUILayout.Button(btnText, btnStyle))
                        {
                            if (isActive)
                                _pickerTool.Deactivate();
                            else
                                StartAnchorPicker(setup, group.referenceVertices,
                                    PickerTarget.GroupSingleRef(gi), false);
                        }
                        if (GUILayout.Button("クリア", GUILayout.Width(50)))
                        {
                            Undo.RecordObject(setup, "Clear vertices");
                            group.referenceVertices.Clear();
                            EditorUtility.SetDirty(setup);
                        }
                    }
                }
            }

            // surfaceAttachment
            EditorGUI.BeginChangeCheck();
            bool newSurface = EditorGUILayout.Toggle(
                new GUIContent("舌ピが浮く場合の調整設定",
                    "舌などの非剛体変形する部位でピアスが浮いたり埋まったりする場合に有効にしてください。"),
                group.surfaceAttachment);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(setup, "Toggle group surfaceAttachment");
                group.surfaceAttachment = newSurface;
                if (newSurface) group.maintainOverallShape = false;
                EditorUtility.SetDirty(setup);
            }

            // maintainOverallShape
            EditorGUI.BeginChangeCheck();
            bool newMaintain = EditorGUILayout.Toggle(
                new GUIContent("全体の形状を維持する",
                    "ピアス位置に最も近い2頂点を自動選択し、軸方向の回転のみで追従します。"),
                group.maintainOverallShape);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(setup, "Toggle group maintainOverallShape");
                group.maintainOverallShape = newMaintain;
                if (newMaintain) group.surfaceAttachment = false;
                EditorUtility.SetDirty(setup);
            }

        }

        private void DrawGroupAnchorSettings(PiercingGroup group, PiercingSetup setup)
        {
            if (group.anchors == null)
                group.anchors = new System.Collections.Generic.List<AnchorPair>();

            // 最低2アンカーを確保
            while (group.anchors.Count < 2)
                group.anchors.Add(new AnchorPair());

            int gi = setup.piercingGroups.IndexOf(group);

            for (int i = 0; i < group.anchors.Count; i++)
            {
                var anchor = group.anchors[i];
                string anchorLabel = $"Anchor {i + 1}";

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(anchorLabel, EditorStyles.boldLabel);

                // 対象頂点（ピアス側）
                DrawVertexListForAnchor(
                    "対象頂点", anchor.piercingVertices,
                    PickerTarget.GroupAnchorPrc(gi, i), setup,
                    null, usePiercingMesh: true);

                // 参照頂点（ターゲット側）
                DrawVertexListForAnchor(
                    "参照頂点(つける対象の頂点)", anchor.targetVertices,
                    PickerTarget.GroupAnchorTgt(gi, i), setup,
                    setup.targetRenderer);
            }

            // アンカー追加/削除ボタン
            if (!setup.isPositionSaved)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("+ アンカーを追加"))
                    {
                        Undo.RecordObject(setup, "Add group anchor");
                        group.anchors.Add(new AnchorPair());
                        EditorUtility.SetDirty(setup);
                    }
                    using (new EditorGUI.DisabledScope(group.anchors.Count <= 2))
                    {
                        if (GUILayout.Button("- 削除", GUILayout.Width(60)))
                        {
                            Undo.RecordObject(setup, "Remove group anchor");
                            group.anchors.RemoveAt(group.anchors.Count - 1);
                            EditorUtility.SetDirty(setup);
                        }
                    }
                }
            }
        }

        private static void DetectIslands(PiercingSetup setup)
        {
            var mesh = GetPiercingMeshForSetup(setup);
            if (mesh == null)
            {
                EditorUtility.DisplayDialog("エラー",
                    "ピアスメッシュが見つかりません。MeshFilterまたはSkinnedMeshRendererを設定してください。", "OK");
                return;
            }

            var islands = MeshIslandDetector.DetectIslands(mesh.triangles, mesh.vertexCount, mesh.vertices);
            if (islands.Count == 0)
            {
                EditorUtility.DisplayDialog("エラー", "アイランドを検出できませんでした。", "OK");
                return;
            }

            Undo.RecordObject(setup, "Detect islands");
            setup.piercingGroups = new System.Collections.Generic.List<PiercingGroup>();
            for (int i = 0; i < islands.Count; i++)
            {
                var vSet = new HashSet<int>(islands[i]);
                var triList = MeshIslandDetector.ExtractTrianglesForGroup(mesh.triangles, vSet);
                var group = new PiercingGroup
                {
                    name = $"ピアス {i + 1}",
                    vertexIndices = new System.Collections.Generic.List<int>(islands[i]),
                    triangleIndices = triList,
                    mode = PiercingMode.Single,
                };
                setup.piercingGroups.Add(group);
            }
            setup.selectedGroupIndex = 0;
            EditorUtility.SetDirty(setup);
            Debug.Log($"[PiercingTool] {islands.Count}個のアイランドを検出しました。");
        }

        private static Mesh GetPiercingMeshForSetup(PiercingSetup setup)
        {
            var mf = setup.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
                return mf.sharedMesh;
            var smr = setup.GetComponent<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null)
                return smr.sharedMesh;
            return null;
        }

        private void UpdateGroupAutoDetectedVertices(PiercingSetup setup)
        {
            if (!setup.useMultiGroup || setup.piercingGroups == null ||
                setup.piercingGroups.Count == 0 || setup.isPositionSaved ||
                setup.targetRenderer == null || setup.targetRenderer.sharedMesh == null)
            {
                _groupAutoDetectedVertices = null;
                return;
            }

            // BakePiercingWorldVertices でボーン変形後のワールド座標を取得
            var piercingWorldVerts = BakePiercingWorldVertices(setup);
            if (piercingWorldVerts == null)
            {
                _groupAutoDetectedVertices = null;
                return;
            }

            if (_groupAutoDetectedVertices == null)
                _groupAutoDetectedVertices = new Dictionary<int, int[]>();

            var worldVerts = BakeWorldVertices(setup.targetRenderer);
            if (worldVerts == null)
            {
                _groupAutoDetectedVertices = null;
                return;
            }

            var triangles = setup.targetRenderer.sharedMesh.triangles;

            for (int gi = 0; gi < setup.piercingGroups.Count; gi++)
            {
                var group = setup.piercingGroups[gi];
                if (group.mode == PiercingMode.Single && group.referenceVertices.Count == 0)
                {
                    // グループ内頂点のワールド座標重心を計算（オフセット考慮）
                    var groupCenter = ComputeGroupBakedWorldCenter(
                        group.vertexIndices, piercingWorldVerts, setup.transform.position);
                    if (group.positionOffset != Vector3.zero)
                        groupCenter += setup.transform.TransformVector(group.positionOffset);
                    _groupAutoDetectedVertices[gi] = FindClosestTriangle(
                        worldVerts, triangles, groupCenter);
                }
                else
                {
                    _groupAutoDetectedVertices.Remove(gi);
                }
            }
        }

        /// <summary>
        /// BakeMesh 済みワールド頂点からグループの重心を計算する。
        /// </summary>
        private static Vector3 ComputeGroupBakedWorldCenter(
            List<int> vertexIndices, Vector3[] bakedWorldVerts, Vector3 fallback)
        {
            if (vertexIndices == null || vertexIndices.Count == 0)
                return fallback;

            var sum = Vector3.zero;
            int count = 0;
            foreach (int vi in vertexIndices)
            {
                if (vi >= 0 && vi < bakedWorldVerts.Length)
                {
                    sum += bakedWorldVerts[vi];
                    count++;
                }
            }
            return count > 0 ? sum / count : fallback;
        }

        private static Vector3 ComputeGroupWorldCenter(
            PiercingSetup setup, PiercingGroup group, Mesh piercingMesh)
        {
            if (group.vertexIndices == null || group.vertexIndices.Count == 0)
                return setup.transform.position;

            var localVerts = piercingMesh.vertices;
            var sum = Vector3.zero;
            int count = 0;
            foreach (int vi in group.vertexIndices)
            {
                if (vi >= 0 && vi < localVerts.Length)
                {
                    sum += setup.transform.TransformPoint(localVerts[vi]);
                    count++;
                }
            }
            return count > 0 ? sum / count : setup.transform.position;
        }

        private void HandleGroupDrag(PiercingSetup setup, int groupCount)
        {
            var evt = Event.current;

            if (_dragGroupIndex >= 0)
            {
                if (evt.type == EventType.MouseDrag)
                {
                    // ドラッグ中: 挿入位置を計算
                    _dragInsertIndex = groupCount; // デフォルトは末尾
                    for (int i = 0; i < _groupHeaderRects.Count; i++)
                    {
                        if (evt.mousePosition.y < _groupHeaderRects[i].center.y)
                        {
                            _dragInsertIndex = i;
                            break;
                        }
                    }
                    evt.Use();
                    Repaint();
                }
                else if (evt.type == EventType.MouseUp && evt.button == 0)
                {
                    if (_dragInsertIndex >= 0 && _dragInsertIndex != _dragGroupIndex &&
                        _dragInsertIndex != _dragGroupIndex + 1)
                    {
                        // ドロップ: 並び替え実行
                        Undo.RecordObject(setup, "Reorder piercing group");
                        var moving = setup.piercingGroups[_dragGroupIndex];
                        setup.piercingGroups.RemoveAt(_dragGroupIndex);
                        int insertAt = _dragInsertIndex > _dragGroupIndex
                            ? _dragInsertIndex - 1 : _dragInsertIndex;
                        setup.piercingGroups.Insert(insertAt, moving);

                        // 選択インデックスを追従
                        if (setup.selectedGroupIndex == _dragGroupIndex)
                            setup.selectedGroupIndex = insertAt;
                        else if (setup.selectedGroupIndex >= 0)
                        {
                            // 他のグループが選択中の場合、移動に伴うインデックスずれを補正
                            int sel = setup.selectedGroupIndex;
                            if (_dragGroupIndex < sel && insertAt >= sel)
                                setup.selectedGroupIndex--;
                            else if (_dragGroupIndex > sel && insertAt <= sel)
                                setup.selectedGroupIndex++;
                        }

                        EditorUtility.SetDirty(setup);
                    }
                    else if (_dragInsertIndex < 0)
                    {
                        // ドラッグせず離した = クリック → 展開/折り畳み
                        Undo.RecordObject(setup, "Select piercing group");
                        setup.selectedGroupIndex =
                            setup.selectedGroupIndex == _dragGroupIndex ? -1 : _dragGroupIndex;
                        EditorUtility.SetDirty(setup);
                    }

                    _dragGroupIndex = -1;
                    _dragInsertIndex = -1;
                    evt.Use();
                    Repaint();
                }
                else if (evt.type == EventType.Ignore || evt.type == EventType.MouseLeaveWindow)
                {
                    // ドラッグキャンセル
                    _dragGroupIndex = -1;
                    _dragInsertIndex = -1;
                    Repaint();
                }
            }
        }

        private void ShowGroupContextMenu(PiercingSetup setup, int groupIndex)
        {
            var menu = new GenericMenu();
            int count = setup.piercingGroups.Count;

            // この設定を全ピアスに適用
            if (count > 1)
            {
                int gi = groupIndex;
                menu.AddItem(new GUIContent("この設定を全ピアスに適用"), false, () =>
                {
                    Undo.RecordObject(setup, "Apply settings to all groups");
                    var src = setup.piercingGroups[gi];
                    foreach (var g in setup.piercingGroups)
                    {
                        if (g == src) continue;
                        g.surfaceAttachment = src.surfaceAttachment;
                        g.maintainOverallShape = src.maintainOverallShape;
                    }
                    EditorUtility.SetDirty(setup);
                });
                menu.AddSeparator("");
            }

            // 結合メニュー（自分以外の全ピアス）
            for (int i = 0; i < count; i++)
            {
                if (i == groupIndex) continue;
                string targetName = string.IsNullOrEmpty(setup.piercingGroups[i].name)
                    ? $"ピアス {i + 1}" : setup.piercingGroups[i].name;
                int capturedTarget = i;
                menu.AddItem(new GUIContent($"結合/{targetName}"), false,
                    () => MergeGroupInto(setup, groupIndex, capturedTarget));
            }

            menu.ShowAsContext();
        }

        private static void MergeGroupInto(PiercingSetup setup, int sourceIndex, int targetIndex)
        {
            if (sourceIndex < 0 || sourceIndex >= setup.piercingGroups.Count ||
                targetIndex < 0 || targetIndex >= setup.piercingGroups.Count ||
                sourceIndex == targetIndex) return;

            Undo.RecordObject(setup, "Merge piercing group");
            var source = setup.piercingGroups[sourceIndex];
            var target = setup.piercingGroups[targetIndex];

            target.vertexIndices.AddRange(source.vertexIndices);
            target.triangleIndices.AddRange(source.triangleIndices);

            setup.piercingGroups.RemoveAt(sourceIndex);

            // 選択インデックス補正
            int newTargetIdx = sourceIndex < targetIndex ? targetIndex - 1 : targetIndex;
            setup.selectedGroupIndex = newTargetIdx;

            EditorUtility.SetDirty(setup);
            Debug.Log($"[PiercingTool] グループを結合しました。");
        }

        private static void MergeSelectedGroup(PiercingSetup setup)
        {
            int idx = setup.selectedGroupIndex;
            if (idx <= 0 || idx >= setup.piercingGroups.Count) return;

            Undo.RecordObject(setup, "Merge piercing group");
            var prev = setup.piercingGroups[idx - 1];
            var cur = setup.piercingGroups[idx];

            prev.vertexIndices.AddRange(cur.vertexIndices);
            prev.triangleIndices.AddRange(cur.triangleIndices);

            setup.piercingGroups.RemoveAt(idx);
            setup.selectedGroupIndex = Mathf.Clamp(idx - 1, 0, setup.piercingGroups.Count - 1);
            EditorUtility.SetDirty(setup);
            Debug.Log($"[PiercingTool] グループ{idx}を前のグループに統合しました。");
        }
    }
}
