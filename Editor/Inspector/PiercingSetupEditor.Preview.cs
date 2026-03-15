using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace PiercingTool.Editor
{
    public partial class PiercingSetupEditor
    {
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
            public Vector3[] boneDisplacement; // ボーン変形による変位（SMR時のみ非null）

            // 参照頂点のキャッシュ（初回検出後に固定、毎フレーム再検出を防止）
            public Dictionary<int, int[]> groupRefVerticesCache;
            public int[] singleRefVerticesCache;
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
        /// </summary>
        private static void OnUndoRedo()
        {
            var setups = Object.FindObjectsOfType<PiercingSetup>();
            foreach (var setup in setups)
            {
                int id = setup.GetInstanceID();

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

                if (s_previews.ContainsKey(id)) continue;

                var mf = setup.GetComponent<MeshFilter>();
                if (mf != null && (mf.hideFlags & HideFlags.DontSave) == 0)
                {
                    if (s_originalMeshes.TryGetValue(id, out var originalMesh) &&
                        originalMesh != null && mf.sharedMesh != originalMesh)
                    {
                        mf.sharedMesh = originalMesh;
                    }
                }
                else
                {
                    var piercingSmr = setup.GetComponent<SkinnedMeshRenderer>();
                    if (piercingSmr != null)
                        piercingSmr.enabled = true;
                    CleanupOrphanedTempComponents(setup);
                }

                RegisterPreview(setup);
            }
        }

        /// <summary>
        /// ドメインリロード後に isPositionSaved な PiercingSetup のプレビューを自動再登録する。
        /// </summary>
        private static void RestorePreviewsAfterReload()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            var setups = Object.FindObjectsOfType<PiercingSetup>();
            foreach (var setup in setups)
            {
                if (!setup.isPositionSaved) continue;
                if (s_previews.ContainsKey(setup.GetInstanceID())) continue;

                if (setup.isSmrPreviewActive)
                {
                    var piercingSmr = setup.GetComponent<SkinnedMeshRenderer>();
                    if (piercingSmr != null)
                        piercingSmr.enabled = true;
                    CleanupOrphanedTempComponents(setup);
                }
                else
                {
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
        /// Play Mode遷移前にプレビューを全解除。復帰時に遅延復元。
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
            var snapshot = new List<KeyValuePair<int, PreviewState>>(s_previews);
            s_previews.Clear();
            foreach (var kvp in snapshot)
            {
                var setup = EditorUtility.InstanceIDToObject(kvp.Key) as PiercingSetup;
                CleanupPreviewState(setup, kvp.Value);
            }
        }

        /// <summary>
        /// プレビューメッシュに対応する元のメッシュを検索する。
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

        // -----------------------------------------------------------------
        // プレビュー登録・更新・クリーンアップ
        // -----------------------------------------------------------------

        private static void RegisterPreview(PiercingSetup setup)
        {
            int id = setup.GetInstanceID();

            if (s_previews.TryGetValue(id, out var oldState))
                CleanupPreviewState(setup, oldState);

            var state = new PreviewState();

            var piercingSmr = setup.GetComponent<SkinnedMeshRenderer>();
            state.piercingSmr = piercingSmr;

            if (piercingSmr != null && piercingSmr.sharedMesh != null &&
                piercingSmr.sharedMesh.blendShapeCount > 0)
            {
                int count = piercingSmr.sharedMesh.blendShapeCount;
                state.originalPiercingWeights = new float[count];
                for (int i = 0; i < count; i++)
                    state.originalPiercingWeights[i] = piercingSmr.GetBlendShapeWeight(i);

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

            // 保存時の状態で参照頂点を検出してキャッシュ
            // （プレビュー追従中に毎フレーム再検出するとガタつくため）
            if (!setup.useMultiGroup && setup.mode == PiercingMode.Single &&
                setup.referenceVertices.Count == 0)
            {
                var renderer = setup.targetRenderer;
                if (renderer != null)
                {
                    var piercingWorldPos = GetPiercingMeshWorldCenter(setup);
                    state.singleRefVerticesCache = setup.maintainOverallShape
                        ? MeshGenerator.FindClosestTwoVerticesAtState(
                            renderer, piercingWorldPos, setup.savedBlendShapeWeights)
                        : MeshGenerator.FindClosestTriangleVerticesAtState(
                            renderer, piercingWorldPos, setup.savedBlendShapeWeights);
                }
            }

            // マルチグループの参照頂点も保存時に検出
            if (setup.useMultiGroup && setup.piercingGroups.Count > 0)
            {
                var renderer = setup.targetRenderer;
                if (renderer != null)
                {
                    // SMR の場合はスキニング済み頂点で重心を計算（sharedMesh はバインドポーズで位置がずれる）
                    Vector3[] skinnedWorldVerts = null;
                    var piercingSmr2 = setup.GetComponent<SkinnedMeshRenderer>();
                    if (piercingSmr2 != null && piercingSmr2.sharedMesh != null)
                    {
                        var localVerts = MeshGenerator.ComputeSkinnedVerticesLocal(piercingSmr2, setup.transform);
                        skinnedWorldVerts = new Vector3[localVerts.Length];
                        for (int i = 0; i < localVerts.Length; i++)
                            skinnedWorldVerts[i] = setup.transform.TransformPoint(localVerts[i]);
                    }

                    state.groupRefVerticesCache = new Dictionary<int, int[]>();
                    for (int gi = 0; gi < setup.piercingGroups.Count; gi++)
                    {
                        var group = setup.piercingGroups[gi];
                        if (group.referenceVertices != null && group.referenceVertices.Count > 0)
                            continue;
                        if (group.mode != PiercingMode.Single) continue;

                        // グループ重心をワールド座標で計算
                        Vector3 groupCenter;
                        if (skinnedWorldVerts != null)
                        {
                            // SMR: スキニング済みワールド頂点から重心計算
                            var sum = Vector3.zero;
                            int cnt = 0;
                            foreach (int vi in group.vertexIndices)
                            {
                                if (vi >= 0 && vi < skinnedWorldVerts.Length)
                                {
                                    sum += skinnedWorldVerts[vi];
                                    cnt++;
                                }
                            }
                            groupCenter = cnt > 0 ? sum / cnt : setup.transform.position;
                        }
                        else
                        {
                            // MF: sharedMesh から重心計算
                            var piercingMesh = GetPiercingMeshForSetup(setup);
                            groupCenter = piercingMesh != null
                                ? ComputeGroupWorldCenter(setup, group, piercingMesh)
                                : setup.transform.position;
                        }

                        // 位置調整オフセットを重心に適用（オフセット後の位置で参照頂点を検出）
                        if (group.positionOffset != Vector3.zero || group.rotationEuler != Vector3.zero)
                        {
                            groupCenter += setup.transform.TransformVector(group.positionOffset);
                        }

                        var refVerts = group.maintainOverallShape
                            ? MeshGenerator.FindClosestTwoVerticesAtState(
                                renderer, groupCenter, setup.savedBlendShapeWeights)
                            : MeshGenerator.FindClosestTriangleVerticesAtState(
                                renderer, groupCenter, setup.savedBlendShapeWeights);
                        state.groupRefVerticesCache[gi] = refVerts;
                    }
                }
            }

            s_previews[id] = state;
        }

        private static void StaticUpdatePreviews()
        {
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

            if (setup.useMultiGroup && setup.piercingGroups.Count > 0)
            {
                return UpdateMultiGroupPreview(setup, state);
            }

            if (setup.mode == PiercingMode.Single)
            {
                // referenceVertices が空でも、位置保存済みなら自動検出で追従可能
                if (setup.referenceVertices.Count == 0 && !setup.maintainOverallShape
                    && !setup.isPositionSaved)
                    return false;
            }
            else
            {
                if (setup.anchors == null || setup.anchors.Count < 2) return false;
                if (!setup.anchors.All(a => a.targetVertices.Count > 0)) return false;
            }

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

            if (state.previewMesh == null)
            {
                if (!InitializePreviewMesh(setup, state))
                    return false;

                if (setup.mode != PiercingMode.Single)
                    InitSegmentDataForPreview(setup, sourceMesh, state);
            }

            if (setup.mode == PiercingMode.Single)
            {
                if (setup.surfaceAttachment && setup.referenceVertices.Count == 3)
                    ApplySurfacePreview(setup, sourceMesh, state);
                else
                    ApplyRigidPreview(setup, sourceMesh, state);
            }
            else
                ApplySegmentPreview(setup, sourceMesh, state);

            return true;
        }

        /// <summary>
        /// MF パス用: previewMesh に位置調整オフセットを直接適用する。
        /// </summary>
        private static void ApplyOffsetToPreviewMesh(PiercingSetup setup, Mesh previewMesh)
        {
            if (setup.useMultiGroup)
            {
                foreach (var group in setup.piercingGroups)
                {
                    MeshGenerator.ApplyPositionOffset(
                        previewMesh,
                        group.positionOffset,
                        group.rotationEuler,
                        new HashSet<int>(group.vertexIndices));
                }
            }
            else
            {
                MeshGenerator.ApplyPositionOffset(
                    previewMesh, setup.positionOffset, setup.rotationEuler);
            }
        }

        /// <summary>
        /// SMR パス用: originalVertices と bakedVerts の両方に同じオフセットを適用する。
        /// ピボットと回転は bakedVerts（ボーン変形後＝実際に見える位置）から計算し、
        /// 同じ変位を両配列に適用することで boneDisplacement を保持する。
        /// </summary>
        private static void ApplyOffsetToVertexArrays(
            PiercingSetup setup, Vector3[] originalVertices, Vector3[] bakedVerts)
        {
            if (setup.useMultiGroup)
            {
                foreach (var group in setup.piercingGroups)
                {
                    if (group.positionOffset == Vector3.zero && group.rotationEuler == Vector3.zero)
                        continue;
                    var rotation = Quaternion.Euler(group.rotationEuler);
                    var vertexSet = new HashSet<int>(group.vertexIndices);
                    // ボーン変形後の頂点からピボットを計算（実際の見た目と一致させる）
                    var pivot = MeshGenerator.ComputeGroupCentroid(bakedVerts, vertexSet);
                    foreach (int i in vertexSet)
                    {
                        if (i < 0 || i >= originalVertices.Length) continue;
                        // bakedVerts（見える位置）を基準に回転オフセットを計算
                        var delta = rotation * (bakedVerts[i] - pivot) + pivot
                                    + group.positionOffset - bakedVerts[i];
                        originalVertices[i] += delta;
                        if (i < bakedVerts.Length)
                            bakedVerts[i] += delta;
                    }
                }
            }
            else
            {
                if (setup.positionOffset == Vector3.zero && setup.rotationEuler == Vector3.zero)
                    return;
                var rotation = Quaternion.Euler(setup.rotationEuler);
                // ボーン変形後の頂点からピボットを計算
                var pivot = Vector3.zero;
                for (int i = 0; i < bakedVerts.Length; i++)
                    pivot += bakedVerts[i];
                if (bakedVerts.Length > 0) pivot /= bakedVerts.Length;

                for (int i = 0; i < originalVertices.Length; i++)
                {
                    var delta = rotation * (bakedVerts[i] - pivot) + pivot
                                + setup.positionOffset - bakedVerts[i];
                    originalVertices[i] += delta;
                    if (i < bakedVerts.Length)
                        bakedVerts[i] += delta;
                }
            }
        }

        private static bool InitializePreviewMesh(PiercingSetup setup, PreviewState state)
        {
            var mf = setup.GetComponent<MeshFilter>();
            var piercingSmr = setup.GetComponent<SkinnedMeshRenderer>();

            if (mf != null && (mf.hideFlags & HideFlags.DontSave) == 0)
            {
                // === MF ベース ===
                state.originalSharedMesh = mf.sharedMesh;
                if (state.originalSharedMesh == null) return false;
                s_originalMeshes[setup.GetInstanceID()] = state.originalSharedMesh;
                state.meshFilter = mf;
                state.previewMesh = Object.Instantiate(state.originalSharedMesh);
                state.previewMesh.name = state.originalSharedMesh.name + "_Preview";
                state.previewMesh.hideFlags = HideFlags.HideAndDontSave;
                // 位置調整オフセットを焼き込み
                ApplyOffsetToPreviewMesh(setup, state.previewMesh);
                state.originalVertices = state.previewMesh.vertices;
                mf.sharedMesh = state.previewMesh;
                state.isSmrPiercing = false;
            }
            else if (piercingSmr != null && piercingSmr.sharedMesh != null)
            {
                // === SMR ベース ===
                state.isSmrPiercing = true;
                var sourceMesh = piercingSmr.sharedMesh;
                state.originalSharedMesh = sourceMesh;
                state.previewMesh = Object.Instantiate(sourceMesh);
                state.previewMesh.name = sourceMesh.name + "_Preview";
                state.previewMesh.hideFlags = HideFlags.HideAndDontSave;

                if (setup.savedPiercingBlendShapeWeights != null &&
                    setup.savedPiercingBlendShapeWeights.Length > 0 &&
                    state.previewMesh.blendShapeCount > 0)
                {
                    MeshGenerator.BakePiercingBlendShapes(
                        state.previewMesh, setup.savedPiercingBlendShapeWeights);
                }

                state.originalVertices = state.previewMesh.vertices;

                // ボーン変形後の頂点を手動スキニングで取得（BakeMesh は一部ボーン構成でずれるため）
                var bakedVerts = MeshGenerator.ComputeSkinnedVerticesLocal(piercingSmr, setup.transform);

                // 位置調整オフセットを originalVertices と bakedVerts の両方に適用
                // （差分=boneDisplacement は変わらないため、追従計算に影響しない）
                ApplyOffsetToVertexArrays(setup, state.originalVertices, bakedVerts);

                state.boneDisplacement = new Vector3[state.originalVertices.Length];
                bool hasDisplacement = false;
                for (int i = 0; i < state.originalVertices.Length; i++)
                {
                    state.boneDisplacement[i] = bakedVerts[i] - state.originalVertices[i];
                    if (state.boneDisplacement[i].sqrMagnitude > 0.000001f)
                        hasDisplacement = true;
                }
                // ボーン変位がない場合は null にして後続の処理をスキップ可能にする
                if (!hasDisplacement)
                    state.boneDisplacement = null;

                // プレビュー初期メッシュにもボーン変位を反映
                if (state.boneDisplacement != null)
                {
                    state.previewMesh.vertices = bakedVerts;
                    state.previewMesh.RecalculateBounds();
                }

                piercingSmr.enabled = false;

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

            if (setup.referenceVertices.Count > 0)
            {
                refIndicesArr = setup.referenceVertices.ToArray();
            }
            else
            {
                // 参照頂点未指定: キャッシュから取得、なければ初回検出して固定
                if (state.singleRefVerticesCache != null)
                {
                    refIndicesArr = state.singleRefVerticesCache;
                }
                else
                {
                    var piercingWorldPos = GetPiercingMeshWorldCenter(setup);
                    refIndicesArr = setup.maintainOverallShape
                        ? MeshGenerator.FindClosestTwoVerticesAtState(
                            renderer, piercingWorldPos, setup.savedBlendShapeWeights)
                        : MeshGenerator.FindClosestTriangleVerticesAtState(
                            renderer, piercingWorldPos, setup.savedBlendShapeWeights);
                    state.singleRefVerticesCache = refIndicesArr;
                }
            }

            var sourceToPiercing = setup.transform.worldToLocalMatrix *
                                   renderer.transform.localToWorldMatrix;

            var savedRefPosSrc = MeshGenerator.ComputeDeformedRefPositions(
                renderer, sourceMesh, refIndicesArr, setup.savedBlendShapeWeights);
            var currentRefPosSrc = MeshGenerator.ComputeDeformedRefPositions(
                renderer, sourceMesh, refIndicesArr, null);

            var savedPiercing = new Vector3[refIndicesArr.Length];
            var currentPiercing = new Vector3[refIndicesArr.Length];
            for (int i = 0; i < refIndicesArr.Length; i++)
            {
                savedPiercing[i] = sourceToPiercing.MultiplyPoint3x4(savedRefPosSrc[i]);
                currentPiercing[i] = sourceToPiercing.MultiplyPoint3x4(currentRefPosSrc[i]);
            }

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

            // ピアスの重心を計算（オフセット込み）
            var piercingCentroid = Vector3.zero;
            for (int i = 0; i < state.originalVertices.Length; i++)
            {
                piercingCentroid += state.originalVertices[i];
                if (state.boneDisplacement != null)
                    piercingCentroid += state.boneDisplacement[i];
            }
            if (state.originalVertices.Length > 0)
                piercingCentroid /= state.originalVertices.Length;

            // 回転はピアスの重心を中心に適用し、平行移動は参照三角形の移動量のみ適用。
            var refTranslation = currentCentroid - savedCentroid;
            var vertices = new Vector3[state.originalVertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                var v = state.originalVertices[i];
                if (state.boneDisplacement != null)
                    v += state.boneDisplacement[i];
                vertices[i] = rotation * (v - piercingCentroid) + piercingCentroid + refTranslation;
            }

            state.previewMesh.vertices = vertices;
            state.previewMesh.RecalculateBounds();
        }

        /// <summary>
        /// 表面アタッチメント方式のプレビュー。
        /// バリセントリック座標＋法線オフセットでアタッチメントポイントを計算し、
        /// 表面追従する並進を適用する。
        /// </summary>
        private static void ApplySurfacePreview(PiercingSetup setup, Mesh sourceMesh, PreviewState state)
        {
            var renderer = setup.targetRenderer;
            var refIndicesArr = setup.referenceVertices.ToArray();

            var sourceToPiercing = setup.transform.worldToLocalMatrix *
                                   renderer.transform.localToWorldMatrix;

            var savedRefPosSrc = MeshGenerator.ComputeDeformedRefPositions(
                renderer, sourceMesh, refIndicesArr, setup.savedBlendShapeWeights);
            var currentRefPosSrc = MeshGenerator.ComputeDeformedRefPositions(
                renderer, sourceMesh, refIndicesArr, null);

            var savedPiercing = new Vector3[3];
            var currentPiercing = new Vector3[3];
            for (int i = 0; i < 3; i++)
            {
                savedPiercing[i] = sourceToPiercing.MultiplyPoint3x4(savedRefPosSrc[i]);
                currentPiercing[i] = sourceToPiercing.MultiplyPoint3x4(currentRefPosSrc[i]);
            }

            // ピアス重心からバリセントリック座標と法線オフセットを計算
            var piercingCentroid = Vector3.zero;
            for (int i = 0; i < state.originalVertices.Length; i++)
            {
                var v = state.originalVertices[i];
                if (state.boneDisplacement != null)
                    v += state.boneDisplacement[i];
                piercingCentroid += v;
            }
            piercingCentroid /= state.originalVertices.Length;

            var bary = BlendShapeTransferEngine.ComputeBarycentricCoords(
                piercingCentroid,
                savedPiercing[0], savedPiercing[1], savedPiercing[2]);

            var savedSurface = bary.x * savedPiercing[0] + bary.y * savedPiercing[1] + bary.z * savedPiercing[2];
            var savedNormal = Vector3.Cross(
                savedPiercing[1] - savedPiercing[0], savedPiercing[2] - savedPiercing[0]);
            if (savedNormal.sqrMagnitude < 1e-12f) savedNormal = Vector3.up;
            else savedNormal.Normalize();
            float normalOffset = Vector3.Dot(piercingCentroid - savedSurface, savedNormal);
            var savedAttach = savedSurface + normalOffset * savedNormal;

            // 現在のアタッチメントポイント
            var currentSurface = bary.x * currentPiercing[0] + bary.y * currentPiercing[1] + bary.z * currentPiercing[2];
            var currentNormal = Vector3.Cross(
                currentPiercing[1] - currentPiercing[0], currentPiercing[2] - currentPiercing[0]);
            if (currentNormal.sqrMagnitude < 1e-12f) currentNormal = savedNormal;
            else currentNormal.Normalize();
            var currentAttach = currentSurface + normalOffset * currentNormal;

            // 三角面フレーム回転
            var rotation = BlendShapeTransferEngine.ComputeTriangleFrameRotation(
                savedPiercing[0], savedPiercing[1], savedPiercing[2],
                currentPiercing[0], currentPiercing[1], currentPiercing[2]);

            // 回転はピアスの重心を中心に適用し、平行移動はアタッチメントポイントの移動量のみ適用
            var attachTranslation = currentAttach - savedAttach;
            var vertices = new Vector3[state.originalVertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                var v = state.originalVertices[i];
                if (state.boneDisplacement != null)
                    v += state.boneDisplacement[i];
                vertices[i] = rotation * (v - piercingCentroid) + piercingCentroid + attachTranslation;
            }

            state.previewMesh.vertices = vertices;
            state.previewMesh.RecalculateBounds();
        }

        private static void InitSegmentDataForPreview(
            PiercingSetup setup, Mesh sourceMesh, PreviewState state)
        {
            var renderer = setup.targetRenderer;
            var sourceToPiercing = setup.transform.worldToLocalMatrix *
                                   renderer.transform.localToWorldMatrix;

            int anchorCount = setup.anchors.Count;
            state.anchorIndices = new int[anchorCount][];
            for (int i = 0; i < anchorCount; i++)
                state.anchorIndices[i] = setup.anchors[i].targetVertices.ToArray();

            var savedSourceVerts = sourceMesh.vertices;
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
                            var pos = piercingVertices[vi];
                            if (state.boneDisplacement != null)
                                pos += state.boneDisplacement[vi];
                            sum += pos;
                            count++;
                        }
                    }
                    state.anchorCentroids[a] = count > 0 ? sum / count : Vector3.zero;
                }
                else
                {
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

            if (state.boneDisplacement != null)
            {
                var displaced = new Vector3[state.originalVertices.Length];
                for (int i = 0; i < displaced.Length; i++)
                    displaced[i] = state.originalVertices[i] + state.boneDisplacement[i];
                state.segmentData = BlendShapeTransferEngine.ComputeSegmentTValues(
                    displaced, state.anchorCentroids);
            }
            else
            {
                state.segmentData = BlendShapeTransferEngine.ComputeSegmentTValues(
                    state.originalVertices, state.anchorCentroids);
            }
        }

        private static void ApplySegmentPreview(
            PiercingSetup setup, Mesh sourceMesh, PreviewState state)
        {
            if (state.anchorIndices == null || state.segmentData == null) return;

            var renderer = setup.targetRenderer;
            var sourceToPiercing = setup.transform.worldToLocalMatrix *
                                   renderer.transform.localToWorldMatrix;

            int anchorCount = state.anchorIndices.Length;
            var savedDeltas = new (Vector3 translation, Quaternion rotation)[anchorCount];

            for (int a = 0; a < anchorCount; a++)
            {
                var indices = state.anchorIndices[a];

                var savedPos = MeshGenerator.ComputeDeformedRefPositions(
                    renderer, sourceMesh, indices, setup.savedBlendShapeWeights);
                var savedPiercing = new Vector3[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                    savedPiercing[i] = sourceToPiercing.MultiplyPoint3x4(savedPos[i]);

                var currentPos = MeshGenerator.ComputeDeformedRefPositions(
                    renderer, sourceMesh, indices, null);
                var currentPiercing = new Vector3[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                    currentPiercing[i] = sourceToPiercing.MultiplyPoint3x4(currentPos[i]);

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
                savedDeltas[a] = (currentCentroid - savedCentroid, rotation);
            }

            var vertices = new Vector3[state.originalVertices.Length];
            for (int vi = 0; vi < vertices.Length; vi++)
            {
                var (seg, t) = state.segmentData[vi];
                int a0 = seg;
                int a1 = Mathf.Min(seg + 1, anchorCount - 1);

                var rot = Quaternion.Slerp(savedDeltas[a0].rotation, savedDeltas[a1].rotation, t);
                var trans = Vector3.Lerp(savedDeltas[a0].translation, savedDeltas[a1].translation, t);
                var pivot = Vector3.Lerp(state.anchorCentroids[a0], state.anchorCentroids[a1], t);

                var v = state.originalVertices[vi];
                if (state.boneDisplacement != null)
                    v += state.boneDisplacement[vi];
                var localPos = v - pivot;
                vertices[vi] = rot * localPos + pivot + trans;
            }

            state.previewMesh.vertices = vertices;
            state.previewMesh.RecalculateBounds();
        }

        // =================================================================
        // マルチグループ プレビュー
        // =================================================================

        private static bool UpdateMultiGroupPreview(PiercingSetup setup, PreviewState state)
        {
            var renderer = setup.targetRenderer;
            var sourceMesh = renderer.sharedMesh;
            if (sourceMesh == null) return false;

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

            if (state.previewMesh == null)
            {
                if (!InitializePreviewMesh(setup, state))
                    return false;
            }

            // originalVertices をベースに作業配列を作成し、boneDisplacement を適用
            var vertices = new Vector3[state.originalVertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = state.originalVertices[i];
                if (state.boneDisplacement != null)
                    vertices[i] += state.boneDisplacement[i];
            }

            // 各グループにそれぞれのモードで変形を適用
            foreach (var group in setup.piercingGroups)
            {
                if (group.vertexIndices == null || group.vertexIndices.Count == 0) continue;

                if (group.mode == PiercingMode.Single)
                    ApplyGroupRigidPreview(setup, group, state, vertices);
                else
                    ApplyGroupSegmentPreview(setup, group, state, vertices);
            }

            state.previewMesh.vertices = vertices;
            state.previewMesh.RecalculateBounds();
            return true;
        }

        private static void ApplyGroupRigidPreview(
            PiercingSetup setup, PiercingGroup group, PreviewState state, Vector3[] vertices)
        {
            var renderer = setup.targetRenderer;
            var sourceMesh = renderer.sharedMesh;

            // グループ重心（ボーン変形適用済み originalVertices から計算）
            var groupCentroidLocal = ComputeGroupVertexCentroid(group, state.originalVertices, state.boneDisplacement);
            var groupCentroidWorld = setup.transform.TransformPoint(groupCentroidLocal);

            // グループのインデックスを取得（キャッシュキー用）
            int groupIndex = setup.piercingGroups.IndexOf(group);

            // 参照頂点インデックスの決定（キャッシュして毎フレーム再検出しない）
            int[] refIndicesArr;
            if (group.referenceVertices != null && group.referenceVertices.Count > 0)
            {
                refIndicesArr = group.referenceVertices.ToArray();
            }
            else
            {
                // キャッシュから取得、なければ初回検出してキャッシュ
                if (state.groupRefVerticesCache == null)
                    state.groupRefVerticesCache = new Dictionary<int, int[]>();

                if (!state.groupRefVerticesCache.TryGetValue(groupIndex, out refIndicesArr))
                {
                    // 初回のみ: 保存時のBlendShape状態で検出して固定
                    refIndicesArr = group.maintainOverallShape
                        ? MeshGenerator.FindClosestTwoVerticesAtState(
                            renderer, groupCentroidWorld, setup.savedBlendShapeWeights)
                        : MeshGenerator.FindClosestTriangleVerticesAtState(
                            renderer, groupCentroidWorld, setup.savedBlendShapeWeights);
                    state.groupRefVerticesCache[groupIndex] = refIndicesArr;
                }
            }

            if (refIndicesArr == null || refIndicesArr.Length == 0)
            {
                Debug.LogWarning($"[PiercingTool] ApplyGroupRigidPreview: refIndicesArr empty for group {groupIndex}");
                return;
            }

            var sourceToPiercing = setup.transform.worldToLocalMatrix *
                                   renderer.transform.localToWorldMatrix;

            var savedRefPosSrc = MeshGenerator.ComputeDeformedRefPositions(
                renderer, sourceMesh, refIndicesArr, setup.savedBlendShapeWeights);
            var currentRefPosSrc = MeshGenerator.ComputeDeformedRefPositions(
                renderer, sourceMesh, refIndicesArr, null);

            var savedPiercing = new Vector3[refIndicesArr.Length];
            var currentPiercing = new Vector3[refIndicesArr.Length];
            for (int i = 0; i < refIndicesArr.Length; i++)
            {
                savedPiercing[i] = sourceToPiercing.MultiplyPoint3x4(savedRefPosSrc[i]);
                currentPiercing[i] = sourceToPiercing.MultiplyPoint3x4(currentRefPosSrc[i]);
            }

            Quaternion rotation;
            Vector3 savedCentroid;
            Vector3 currentCentroid;

            if (group.surfaceAttachment && refIndicesArr.Length == 3)
            {
                // バリセントリック座標による表面アタッチメント
                var bary = BlendShapeTransferEngine.ComputeBarycentricCoords(
                    groupCentroidLocal,
                    savedPiercing[0], savedPiercing[1], savedPiercing[2]);

                var savedSurface = bary.x * savedPiercing[0] + bary.y * savedPiercing[1] + bary.z * savedPiercing[2];
                var savedNormal = Vector3.Cross(
                    savedPiercing[1] - savedPiercing[0], savedPiercing[2] - savedPiercing[0]);
                if (savedNormal.sqrMagnitude < 1e-12f) savedNormal = Vector3.up;
                else savedNormal.Normalize();
                float normalOffset = Vector3.Dot(groupCentroidLocal - savedSurface, savedNormal);
                savedCentroid = savedSurface + normalOffset * savedNormal;

                var currentSurface = bary.x * currentPiercing[0] + bary.y * currentPiercing[1] + bary.z * currentPiercing[2];
                var currentNormal = Vector3.Cross(
                    currentPiercing[1] - currentPiercing[0], currentPiercing[2] - currentPiercing[0]);
                if (currentNormal.sqrMagnitude < 1e-12f) currentNormal = savedNormal;
                else currentNormal.Normalize();
                currentCentroid = currentSurface + normalOffset * currentNormal;

                rotation = BlendShapeTransferEngine.ComputeTriangleFrameRotation(
                    savedPiercing[0], savedPiercing[1], savedPiercing[2],
                    currentPiercing[0], currentPiercing[1], currentPiercing[2]);
            }
            else if (group.maintainOverallShape && refIndicesArr.Length == 2)
            {
                // 2頂点軸回転
                var deltas = new Vector3[refIndicesArr.Length];
                for (int i = 0; i < deltas.Length; i++)
                    deltas[i] = currentPiercing[i] - savedPiercing[i];
                rotation = BlendShapeTransferEngine.ComputeRigidDelta(savedPiercing, deltas).rotation;

                savedCentroid = BlendShapeTransferEngine.ComputeCentroid(savedPiercing);
                currentCentroid = BlendShapeTransferEngine.ComputeCentroid(currentPiercing);
            }
            else if (refIndicesArr.Length == 3)
            {
                // 三角面フレーム回転
                rotation = BlendShapeTransferEngine.ComputeTriangleFrameRotation(
                    savedPiercing[0], savedPiercing[1], savedPiercing[2],
                    currentPiercing[0], currentPiercing[1], currentPiercing[2]);

                savedCentroid = BlendShapeTransferEngine.ComputeCentroid(savedPiercing);
                currentCentroid = BlendShapeTransferEngine.ComputeCentroid(currentPiercing);
            }
            else
            {
                // フォールバック: RigidDelta
                var deltas = new Vector3[refIndicesArr.Length];
                for (int i = 0; i < deltas.Length; i++)
                    deltas[i] = currentPiercing[i] - savedPiercing[i];
                rotation = BlendShapeTransferEngine.ComputeRigidDelta(savedPiercing, deltas).rotation;

                savedCentroid = BlendShapeTransferEngine.ComputeCentroid(savedPiercing);
                currentCentroid = BlendShapeTransferEngine.ComputeCentroid(currentPiercing);
            }

            // グループに属する頂点にのみ変換を適用（vertices はすでに boneDisplacement 適用済み）
            var refTranslation = currentCentroid - savedCentroid;
            foreach (int vi in group.vertexIndices)
            {
                if (vi < 0 || vi >= vertices.Length) continue;
                vertices[vi] = rotation * (vertices[vi] - groupCentroidLocal) + groupCentroidLocal + refTranslation;
            }
        }

        private static void ApplyGroupSegmentPreview(
            PiercingSetup setup, PiercingGroup group, PreviewState state, Vector3[] vertices)
        {
            if (group.anchors == null || group.anchors.Count < 2) return;
            if (!group.anchors.All(a => a.targetVertices.Count > 0)) return;

            var renderer = setup.targetRenderer;
            var sourceMesh = renderer.sharedMesh;
            var sourceToPiercing = setup.transform.worldToLocalMatrix *
                                   renderer.transform.localToWorldMatrix;

            int anchorCount = group.anchors.Count;
            var anchorIndices = new int[anchorCount][];
            for (int i = 0; i < anchorCount; i++)
                anchorIndices[i] = group.anchors[i].targetVertices.ToArray();

            // 保存時のアンカー重心を計算（グループの piercingVertices または target から）
            var savedAnchorCentroids = new Vector3[anchorCount];
            {
                var savedSourceVerts = sourceMesh.vertices;
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

                for (int a = 0; a < anchorCount; a++)
                {
                    bool hasPiercingSide = group.anchors[a].piercingVertices.Count > 0;
                    if (hasPiercingSide)
                    {
                        var pVerts = group.anchors[a].piercingVertices;
                        var sum = Vector3.zero;
                        int count = 0;
                        foreach (int vi in pVerts)
                        {
                            if (vi < state.originalVertices.Length)
                            {
                                var pos = state.originalVertices[vi];
                                if (state.boneDisplacement != null)
                                    pos += state.boneDisplacement[vi];
                                sum += pos;
                                count++;
                            }
                        }
                        savedAnchorCentroids[a] = count > 0 ? sum / count : Vector3.zero;
                    }
                    else
                    {
                        var sum = Vector3.zero;
                        foreach (int vi in anchorIndices[a])
                        {
                            if (vi < savedSourceVerts.Length)
                                sum += sourceToPiercing.MultiplyPoint3x4(savedSourceVerts[vi]);
                        }
                        savedAnchorCentroids[a] = anchorIndices[a].Length > 0
                            ? sum / anchorIndices[a].Length : Vector3.zero;
                    }
                }
            }

            // グループに属する頂点のセグメントデータをオンザフライで計算
            // vertices はすでに boneDisplacement 適用済み
            var groupVerts = group.vertexIndices;
            var groupPositions = new Vector3[groupVerts.Count];
            for (int i = 0; i < groupVerts.Count; i++)
            {
                int vi = groupVerts[i];
                groupPositions[i] = (vi >= 0 && vi < vertices.Length) ? vertices[vi] : Vector3.zero;
            }
            var segmentData = BlendShapeTransferEngine.ComputeSegmentTValues(
                groupPositions, savedAnchorCentroids);

            // 現在のアンカーデルタを計算
            var savedDeltas = new (Vector3 translation, Quaternion rotation)[anchorCount];
            for (int a = 0; a < anchorCount; a++)
            {
                var indices = anchorIndices[a];

                var savedPos = MeshGenerator.ComputeDeformedRefPositions(
                    renderer, sourceMesh, indices, setup.savedBlendShapeWeights);
                var savedPiercing = new Vector3[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                    savedPiercing[i] = sourceToPiercing.MultiplyPoint3x4(savedPos[i]);

                var currentPos = MeshGenerator.ComputeDeformedRefPositions(
                    renderer, sourceMesh, indices, null);
                var currentPiercing = new Vector3[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                    currentPiercing[i] = sourceToPiercing.MultiplyPoint3x4(currentPos[i]);

                Quaternion rot;
                if (indices.Length == 3)
                {
                    rot = BlendShapeTransferEngine.ComputeTriangleFrameRotation(
                        savedPiercing[0], savedPiercing[1], savedPiercing[2],
                        currentPiercing[0], currentPiercing[1], currentPiercing[2]);
                }
                else
                {
                    var deltas = new Vector3[indices.Length];
                    for (int i = 0; i < deltas.Length; i++)
                        deltas[i] = currentPiercing[i] - savedPiercing[i];
                    rot = BlendShapeTransferEngine.ComputeRigidDelta(savedPiercing, deltas).rotation;
                }

                var savedCentroid = BlendShapeTransferEngine.ComputeCentroid(savedPiercing);
                var currentCentroid = BlendShapeTransferEngine.ComputeCentroid(currentPiercing);
                savedDeltas[a] = (currentCentroid - savedCentroid, rot);
            }

            // グループ頂点に変換を適用
            for (int gi = 0; gi < groupVerts.Count; gi++)
            {
                int vi = groupVerts[gi];
                if (vi < 0 || vi >= vertices.Length) continue;

                var (seg, t) = segmentData[gi];
                int a0 = seg;
                int a1 = Mathf.Min(seg + 1, anchorCount - 1);

                var rot = Quaternion.Slerp(savedDeltas[a0].rotation, savedDeltas[a1].rotation, t);
                var trans = Vector3.Lerp(savedDeltas[a0].translation, savedDeltas[a1].translation, t);
                var pivot = Vector3.Lerp(savedAnchorCentroids[a0], savedAnchorCentroids[a1], t);

                var localPos = vertices[vi] - pivot;
                vertices[vi] = rot * localPos + pivot + trans;
            }
        }

        /// <summary>
        /// グループ重心をワールド座標で返す。
        /// </summary>
        private static Vector3 GetGroupPiercingWorldCenter(
            PiercingSetup setup, PiercingGroup group, PreviewState state)
        {
            var localCentroid = ComputeGroupVertexCentroid(group, state.originalVertices, state.boneDisplacement);
            return setup.transform.TransformPoint(localCentroid);
        }

        /// <summary>
        /// グループの vertexIndices からローカル重心を計算する。
        /// </summary>
        private static Vector3 ComputeGroupVertexCentroid(
            PiercingGroup group, Vector3[] originalVertices, Vector3[] boneDisplacement)
        {
            var sum = Vector3.zero;
            int count = 0;
            foreach (int vi in group.vertexIndices)
            {
                if (vi < 0 || vi >= originalVertices.Length) continue;
                var pos = originalVertices[vi];
                if (boneDisplacement != null)
                    pos += boneDisplacement[vi];
                sum += pos;
                count++;
            }
            return count > 0 ? sum / count : Vector3.zero;
        }

        private static void CleanupPreviewState(PiercingSetup setup, PreviewState state)
        {
            if (state.previewMesh != null)
            {
                if (state.isSmrPiercing)
                {
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
                    if (state.meshFilter != null && state.originalSharedMesh != null)
                        state.meshFilter.sharedMesh = state.originalSharedMesh;
                }

                Object.DestroyImmediate(state.previewMesh);
            }

            if (state.piercingSmr != null && state.originalPiercingWeights != null)
            {
                int count = Mathf.Min(
                    state.originalPiercingWeights.Length,
                    state.piercingSmr.sharedMesh != null
                        ? state.piercingSmr.sharedMesh.blendShapeCount : 0);
                for (int i = 0; i < count; i++)
                    state.piercingSmr.SetBlendShapeWeight(i, state.originalPiercingWeights[i]);
            }

            state.groupRefVerticesCache = null;
            state.singleRefVerticesCache = null;
        }

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

        private static void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
        {
            var states = new List<PreviewState>(s_previews.Values);
            foreach (var state in states)
            {
                try
                {
                    if (state.isSmrPiercing)
                    {
                        if (state.piercingSmr != null)
                            state.piercingSmr.enabled = true;
                    }
                    else
                    {
                        if (state.meshFilter != null && state.originalSharedMesh != null)
                            state.meshFilter.sharedMesh = state.originalSharedMesh;
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[PiercingTool] シーン保存前のプレビュー復元に失敗: {e.Message}");
                }
            }
        }

        private static void OnSceneSaved(UnityEngine.SceneManagement.Scene scene)
        {
            var states = new List<PreviewState>(s_previews.Values);
            foreach (var state in states)
            {
                try
                {
                    if (state.isSmrPiercing)
                    {
                        if (state.piercingSmr != null)
                            state.piercingSmr.enabled = false;
                    }
                    else
                    {
                        if (state.meshFilter != null && state.previewMesh != null)
                            state.meshFilter.sharedMesh = state.previewMesh;
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[PiercingTool] シーン保存後のプレビュー復帰に失敗: {e.Message}");
                }
            }
        }
    }
}
