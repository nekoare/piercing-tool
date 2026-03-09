using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEditor;

namespace PiercingTool.Editor
{
    /// <summary>
    /// PiercingSetupコンポーネントの設定からBlendShape転写済みメッシュを生成・保存する。
    /// </summary>
    public static class MeshGenerator
    {
        /// <summary>
        /// BlendShape転写済みのピアスメッシュを生成する。
        /// </summary>
        public static Mesh Generate(PiercingSetup setup)
        {
            if (setup.targetRenderer == null)
                throw new System.InvalidOperationException("対象Rendererが設定されていません。");

            var sourceMesh = setup.targetRenderer.sharedMesh;
            if (sourceMesh == null)
                throw new System.InvalidOperationException("対象RendererにMeshが設定されていません。");

            // ピアスメッシュを取得・複製
            Mesh originalPiercingMesh = GetPiercingMesh(setup);
            if (originalPiercingMesh == null)
                throw new System.InvalidOperationException(
                    "PiercingSetupと同じGameObjectにSkinnedMeshRendererまたはMeshFilterが必要です。");

            var piercingMesh = Object.Instantiate(originalPiercingMesh);
            piercingMesh.name = originalPiercingMesh.name + "_Piercing";

            // ピアス側に保存済みBlendShapeがある場合、weightsを頂点に適用してBlendShapeをクリア
            if (setup.savedPiercingBlendShapeWeights != null &&
                setup.savedPiercingBlendShapeWeights.Length > 0 &&
                originalPiercingMesh.blendShapeCount > 0)
            {
                BakePiercingBlendShapes(piercingMesh, setup.savedPiercingBlendShapeWeights);
            }

            bool isHybridMode = setup.skipBoneWeightTransfer &&
                                setup.fixedPiercingVertices.Count > 0;

            // ハイブリッドモード: 中心頂点+半径から固定頂点セットを展開
            List<int> expandedFixedVertices = null;
            if (isHybridMode)
            {
                expandedFixedVertices = ExpandFixedVertices(
                    piercingMesh, setup.fixedPiercingVertices, setup.fixedPiercingRadius);
            }

            // ピアスがSMRでボーン変形されている場合、その変位を頂点に焼き込む
            // （ユーザーがHeadボーン等を動かして配置した位置を反映する）
            // ただし skipBoneWeightTransfer 時はピアスのボーン構造を維持するので焼き込まない
            // （ボーンが実行時に頂点を変形するため、焼き込むと二重変形になる）
            // ハイブリッドモードでは固定頂点のみベイク（顔ボーンに切り替わるため必要）
            if (!setup.skipBoneWeightTransfer)
            {
                var piercingSmr = setup.GetComponent<SkinnedMeshRenderer>();
                if (piercingSmr != null && piercingSmr.sharedMesh != null)
                {
                    BakeBoneDisplacement(piercingSmr, piercingMesh);
                }
            }
            else if (isHybridMode)
            {
                var piercingSmr = setup.GetComponent<SkinnedMeshRenderer>();
                if (piercingSmr != null && piercingSmr.sharedMesh != null)
                {
                    BakeBoneDisplacementPartial(piercingSmr, piercingMesh, expandedFixedVertices);
                }
            }

            // ソースメッシュ→ピアスメッシュの座標変換行列
            // 回転とスケールの違いを補正してBlendShapeデルタを正しく変換する
            var sourceToPiercing = setup.transform.worldToLocalMatrix *
                                   setup.targetRenderer.transform.localToWorldMatrix;

            // --- 負スケール: ワインディング反転 ---
            // MeshRendererはUnityが自動でfront-face判定を反転するが、
            // SkinnedMeshRendererでは自動補正されないため、
            // メッシュデータのワインディングを手動で反転して面カリングを正しくする。
            // 法線・タンジェントは inverse-transpose で正しく変換されるため変更不要。
            // 統合モードでは MeshMerger が piercingToTarget + bindpose を考慮して
            // 一括で判定するため、ここでは反転しない（二重反転防止）。
            if (!setup.mergeIntoTarget && setup.transform.localToWorldMatrix.determinant < 0)
                FlipTriangleWinding(piercingMesh);

            // BlendShape転写
            List<string> transferred;
            int[] resolvedRefIndices = null; // 自動選択を含む解決済み参照頂点（ボーンウェイト用）
            int[][] anchorIndices = null;
            Vector3[] anchorCentroids = null;
            bool isChainOrMulti = setup.mode != PiercingMode.Single;
            if (setup.mode == PiercingMode.Single)
            {
                // ハイブリッドモードでは保存済み参照頂点を無視し、固定頂点の重心から自動検出
                // （referenceVertices はUndo・モード切替用に内部で保持）
                var refIndicesArr = isHybridMode
                    ? new int[0]
                    : setup.referenceVertices.ToArray();

                // ハイブリッドモードでは固定頂点の重心を基準に参照頂点を自動検出
                var piercingWorldPos = isHybridMode
                    ? ComputeFixedVerticesCentroid(setup)
                    : GetPiercingMeshWorldCenter(setup);

                if (setup.maintainOverallShape && refIndicesArr.Length == 0)
                {
                    // 「全体の形状を維持する」（参照頂点未保存時のフォールバック）
                    refIndicesArr = FindClosestTwoVertices(
                        setup.targetRenderer, piercingWorldPos);
                }
                else if (refIndicesArr.Length == 0)
                {
                    // 参照頂点が未指定の場合、最近傍三角面を自動選択
                    refIndicesArr = FindClosestTriangleVertices(
                        setup.targetRenderer, piercingWorldPos);
                }

                resolvedRefIndices = refIndicesArr;

                // 配置時にBlendShapeが有効な場合、ピアス頂点をBASE状態に補正
                // ハイブリッドモードではPhysBone頂点を除外（bindposeとの整合を保つ）
                CorrectPiercingToBasePosition(
                    setup.targetRenderer, sourceMesh, piercingMesh,
                    refIndicesArr, sourceToPiercing,
                    setup.savedBlendShapeWeights,
                    isHybridMode ? new HashSet<int>(expandedFixedVertices) : null);

                transferred = BlendShapeTransferEngine.TransferBlendShapesSingle(
                    sourceMesh, piercingMesh,
                    refIndicesArr, sourceToPiercing);
            }
            else // Chain / MultiAnchor
            {
                anchorIndices = ResolveAnchorIndices(setup);
                if (anchorIndices.Length < 2)
                    throw new System.InvalidOperationException(
                        "Chain/MultiAnchor モードには2つ以上のアンカーが必要です。");

                anchorCentroids = ComputeAnchorCentroids(
                    setup, sourceMesh, piercingMesh, anchorIndices, sourceToPiercing);

                // BASE 補正（全アンカーの target 頂点を統合して重心オフセット計算）
                var allRefIndices = new List<int>();
                foreach (var indices in anchorIndices)
                    allRefIndices.AddRange(indices);
                var blendShapeOffset = ComputeBlendShapeOffsetCentroid(
                    setup.targetRenderer, sourceMesh, allRefIndices.ToArray(),
                    setup.savedBlendShapeWeights);
                if (blendShapeOffset.magnitude > 0.0001f)
                {
                    var piercingOffset = sourceToPiercing.MultiplyVector(blendShapeOffset);
                    var vertices = piercingMesh.vertices;
                    for (int i = 0; i < vertices.Length; i++)
                        vertices[i] -= piercingOffset;
                    piercingMesh.vertices = vertices;
                }

                transferred = BlendShapeTransferEngine.TransferBlendShapesMultiAnchor(
                    sourceMesh, piercingMesh,
                    anchorIndices, anchorCentroids,
                    sourceToPiercing);

                resolvedRefIndices = allRefIndices.ToArray();
            }

            // ハイブリッドモード: PhysBone頂点のBlendShapeデルタをゼロクリア
            if (isHybridMode)
            {
                ZeroOutNonFixedBlendShapeDeltas(piercingMesh, expandedFixedVertices);
            }

            // ボーンウェイト・bindpose設定
            if (isHybridMode)
            {
                // ハイブリッドモード: 固定頂点は顔ウェイト、残りは元のPhysBoneウェイト
                TransferBoneWeightsHybrid(setup, sourceMesh, piercingMesh, resolvedRefIndices, expandedFixedVertices);

                // bindpose: ターゲットボーン分 + ピアスボーン分を結合
                var targetBindposes = ComputeBindposes(setup);
                var piercingSmr = setup.GetComponent<SkinnedMeshRenderer>();
                var piercingBindposes = piercingSmr != null && piercingSmr.sharedMesh != null
                    ? piercingSmr.sharedMesh.bindposes
                    : new Matrix4x4[0];

                var mergedBindposes = new Matrix4x4[targetBindposes.Length + piercingBindposes.Length];
                System.Array.Copy(targetBindposes, 0, mergedBindposes, 0, targetBindposes.Length);
                System.Array.Copy(piercingBindposes, 0, mergedBindposes, targetBindposes.Length,
                    piercingBindposes.Length);
                piercingMesh.bindposes = mergedBindposes;

                NormalizeMeshScale(piercingMesh, setup);
            }
            else if (!setup.skipBoneWeightTransfer)
            {
                TransferBoneWeights(setup, sourceMesh, piercingMesh,
                    resolvedRefIndices, sourceToPiercing,
                    isChainOrMulti ? anchorIndices : null,
                    isChainOrMulti ? anchorCentroids : null);

                // 統合モードでは bindpose/スケール補正は不要
                // （ピアス頂点はターゲット空間に変換されるため）
                if (!setup.mergeIntoTarget)
                {
                    var bindposes = ComputeBindposes(setup);
                    piercingMesh.bindposes = bindposes;

                    NormalizeMeshScale(piercingMesh, setup);
                }
            }

            return piercingMesh;
        }

        /// <summary>メッシュをアセットとして保存する。</summary>
        public static string SaveMeshAsset(Mesh mesh, string basePath = null)
        {
            if (string.IsNullOrEmpty(basePath))
                basePath = "Assets";

            string path = EditorUtility.SaveFilePanelInProject(
                "ピアスメッシュを保存",
                mesh.name, "asset",
                "生成されたピアスメッシュの保存先を選択してください",
                basePath);

            if (string.IsNullOrEmpty(path))
                return null;

            AssetDatabase.CreateAsset(mesh, path);
            Debug.Log($"[PiercingTool] メッシュを保存しました: {path}");
            return path;
        }

        private static Mesh GetPiercingMesh(PiercingSetup setup)
        {
            // MeshFilterを優先（元メッシュが残っている場合はそちらを使う）
            var mf = setup.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
                return mf.sharedMesh;

            var smr = setup.GetComponent<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null)
                return smr.sharedMesh;

            return null;
        }

        /// <summary>
        /// 保存済みBlendShape weightsをピアスメッシュの頂点に焼き込み、
        /// BlendShapeデータをクリアする。BakeMeshと異なりボーン変形を含まない。
        /// </summary>
        internal static void BakePiercingBlendShapes(Mesh piercingMesh, float[] savedWeights)
        {
            int blendShapeCount = piercingMesh.blendShapeCount;
            if (blendShapeCount == 0) return;

            var vertices = piercingMesh.vertices;
            int vertexCount = vertices.Length;
            var deltaVertices = new Vector3[vertexCount];
            var deltaNormals = new Vector3[vertexCount];
            var deltaTangents = new Vector3[vertexCount];

            int applyCount = Mathf.Min(savedWeights.Length, blendShapeCount);
            for (int si = 0; si < applyCount; si++)
            {
                float weight = savedWeights[si];
                if (Mathf.Approximately(weight, 0f)) continue;

                int frameCount = piercingMesh.GetBlendShapeFrameCount(si);
                if (frameCount == 0) continue;

                float frameWeight = piercingMesh.GetBlendShapeFrameWeight(si, frameCount - 1);
                if (Mathf.Approximately(frameWeight, 0f)) continue;

                piercingMesh.GetBlendShapeFrameVertices(
                    si, frameCount - 1, deltaVertices, deltaNormals, deltaTangents);

                float ratio = weight / frameWeight;
                for (int vi = 0; vi < vertexCount; vi++)
                    vertices[vi] += deltaVertices[vi] * ratio;
            }

            piercingMesh.vertices = vertices;
            piercingMesh.ClearBlendShapes();
            piercingMesh.RecalculateBounds();
        }

        /// <summary>
        /// SMR のボーン変形による頂点変位をピアスメッシュに焼き込む。
        /// BlendShape の影響を除外するため、一時的にウェイトをゼロにして BakeMesh する。
        /// </summary>
        private static void BakeBoneDisplacement(SkinnedMeshRenderer smr, Mesh piercingMesh)
        {
            var sharedMesh = smr.sharedMesh;
            int bsCount = sharedMesh.blendShapeCount;

            // BlendShape ウェイトを一時的にゼロにしてボーン変形のみを取得
            var savedWeights = new float[bsCount];
            for (int i = 0; i < bsCount; i++)
            {
                savedWeights[i] = smr.GetBlendShapeWeight(i);
                smr.SetBlendShapeWeight(i, 0f);
            }

            var bakedMesh = new Mesh();
            smr.BakeMesh(bakedMesh);
            var bakedVerts = bakedMesh.vertices;
            Object.DestroyImmediate(bakedMesh);

            // ウェイトを復元
            for (int i = 0; i < bsCount; i++)
                smr.SetBlendShapeWeight(i, savedWeights[i]);

            // ボーン変位 = BakeMesh(weights=0) - sharedMesh.vertices
            var bindVerts = sharedMesh.vertices;
            var verts = piercingMesh.vertices;
            bool hasDisplacement = false;
            for (int i = 0; i < verts.Length && i < bakedVerts.Length; i++)
            {
                var disp = bakedVerts[i] - bindVerts[i];
                if (disp.sqrMagnitude > 0.000001f)
                {
                    verts[i] += disp;
                    hasDisplacement = true;
                }
            }

            if (hasDisplacement)
            {
                piercingMesh.vertices = verts;
                piercingMesh.RecalculateBounds();
            }
        }

        /// <summary>
        /// SMR のボーン変形による頂点変位を、指定された頂点のみに焼き込む。
        /// ハイブリッドモード用: 固定頂点は顔ボーンに切り替わるため変位のベイクが必要だが、
        /// PhysBone 頂点は元のボーンが実行時に変形するためベイクすると二重変形になる。
        /// </summary>
        private static void BakeBoneDisplacementPartial(
            SkinnedMeshRenderer smr, Mesh piercingMesh, List<int> targetVertices)
        {
            var sharedMesh = smr.sharedMesh;
            int bsCount = sharedMesh.blendShapeCount;

            // BlendShape ウェイトを一時的にゼロにしてボーン変形のみを取得
            var savedWeights = new float[bsCount];
            for (int i = 0; i < bsCount; i++)
            {
                savedWeights[i] = smr.GetBlendShapeWeight(i);
                smr.SetBlendShapeWeight(i, 0f);
            }

            var bakedMesh = new Mesh();
            smr.BakeMesh(bakedMesh);
            var bakedVerts = bakedMesh.vertices;
            Object.DestroyImmediate(bakedMesh);

            // ウェイトを復元
            for (int i = 0; i < bsCount; i++)
                smr.SetBlendShapeWeight(i, savedWeights[i]);

            // 指定頂点のみにボーン変位を適用
            var bindVerts = sharedMesh.vertices;
            var verts = piercingMesh.vertices;
            var targetSet = new HashSet<int>(targetVertices);
            bool hasDisplacement = false;

            for (int i = 0; i < verts.Length && i < bakedVerts.Length; i++)
            {
                if (!targetSet.Contains(i)) continue;

                var disp = bakedVerts[i] - bindVerts[i];
                if (disp.sqrMagnitude > 0.000001f)
                {
                    verts[i] += disp;
                    hasDisplacement = true;
                }
            }

            if (hasDisplacement)
            {
                piercingMesh.vertices = verts;
                piercingMesh.RecalculateBounds();
            }
        }

        private static void TransferBoneWeights(
            PiercingSetup setup, Mesh sourceMesh, Mesh piercingMesh,
            int[] resolvedRefIndices, Matrix4x4 sourceToPiercing,
            int[][] cachedAnchorIndices = null, Vector3[] cachedAnchorCentroids = null)
        {
            if (setup.mode == PiercingMode.Single && setup.perVertexBoneWeights)
            {
                // 頂点ごとのウェイト転写: 各ピアス頂点にソースメッシュの最寄り面から補間
                var piercingToSource = sourceToPiercing.inverse;
                BlendShapeTransferEngine.TransferBoneWeightsPerVertex(
                    sourceMesh, piercingMesh, piercingToSource);
            }
            else if (setup.mode == PiercingMode.Single)
            {
                var refIndices = resolvedRefIndices ?? setup.referenceVertices.ToArray();

                // 3頂点の場合、ピアス取付位置のバリセントリック座標で補間
                float[] baryWeights = null;
                if (refIndices.Length == 3)
                {
                    var deformedRefPos = ComputeDeformedRefPositions(
                        setup.targetRenderer, sourceMesh, refIndices,
                        setup.savedBlendShapeWeights);
                    var attachLocal = setup.targetRenderer.transform
                        .InverseTransformPoint(setup.transform.position);
                    var bary = BlendShapeTransferEngine.ComputeBarycentricCoords(
                        attachLocal, deformedRefPos[0], deformedRefPos[1], deformedRefPos[2]);
                    baryWeights = new float[] { bary.x, bary.y, bary.z };
                }

                BlendShapeTransferEngine.TransferBoneWeightsSingle(
                    sourceMesh, piercingMesh,
                    refIndices, baryWeights);
            }
            else // Chain / MultiAnchor
            {
                var anchorIndices = cachedAnchorIndices ?? ResolveAnchorIndices(setup);
                if (anchorIndices.Length < 2) return;

                var anchorCentroids = cachedAnchorCentroids ?? ComputeAnchorCentroids(
                    setup, sourceMesh, piercingMesh, anchorIndices, sourceToPiercing);

                var segmentData = BlendShapeTransferEngine.ComputeSegmentTValues(
                    piercingMesh.vertices, anchorCentroids);

                BlendShapeTransferEngine.TransferBoneWeightsMultiAnchor(
                    sourceMesh, piercingMesh, anchorIndices, segmentData);
            }
        }

        /// <summary>
        /// ハイブリッドボーンウェイト転写:
        /// fixedPiercingVertices → 顔ボーンウェイト（ターゲットボーン範囲）
        /// それ以外 → 元のピアスボーンウェイト（インデックスをターゲットボーン数分オフセット）
        /// </summary>
        private static void TransferBoneWeightsHybrid(
            PiercingSetup setup, Mesh sourceMesh, Mesh piercingMesh,
            int[] resolvedRefIndices, List<int> expandedFixedVertices)
        {
            int targetBoneCount = setup.targetRenderer.bones.Length;
            var fixedSet = new HashSet<int>(expandedFixedVertices);

            // 固定頂点用: 顔ボーンウェイトを計算
            var refIndices = resolvedRefIndices ?? setup.referenceVertices.ToArray();
            float[] baryWeights = null;
            if (refIndices.Length == 3)
            {
                var deformedRefPos = ComputeDeformedRefPositions(
                    setup.targetRenderer, sourceMesh, refIndices,
                    setup.savedBlendShapeWeights);
                var attachLocal = setup.targetRenderer.transform
                    .InverseTransformPoint(setup.transform.position);
                var bary = BlendShapeTransferEngine.ComputeBarycentricCoords(
                    attachLocal, deformedRefPos[0], deformedRefPos[1], deformedRefPos[2]);
                baryWeights = new float[] { bary.x, bary.y, bary.z };
            }

            Dictionary<int, float> faceWeighted;
            if (baryWeights != null && baryWeights.Length == refIndices.Length)
                faceWeighted = BlendShapeTransferEngine.ComputeWeightedBoneWeights(
                    sourceMesh, refIndices, baryWeights);
            else
                faceWeighted = BlendShapeTransferEngine.ComputeAverageBoneWeights(
                    sourceMesh, refIndices);
            var faceSorted = BlendShapeTransferEngine.NormalizeAndSort(faceWeighted);

            // 元のピアスボーンウェイトを読み取り（managed にコピー）
            var origBpvNative = piercingMesh.GetBonesPerVertex();
            var origBpvData = origBpvNative.IsCreated ? origBpvNative.ToArray() : new byte[0];
            var origWeightsNative = piercingMesh.GetAllBoneWeights();
            var origWeightsData = origWeightsNative.IsCreated
                ? origWeightsNative.ToArray() : new BoneWeight1[0];

            // ハイブリッドウェイト構築
            int piercingVertexCount = piercingMesh.vertexCount;
            var bonesPerVertex = new byte[piercingVertexCount];
            var allWeightsList = new List<BoneWeight1>();

            int origWeightIdx = 0;
            for (int vi = 0; vi < piercingVertexCount; vi++)
            {
                int origCount = vi < origBpvData.Length ? origBpvData[vi] : 0;

                if (fixedSet.Contains(vi))
                {
                    // 固定頂点: 顔ボーンウェイト（インデックスはターゲットボーン範囲 0~N-1）
                    byte boneCount = (byte)Mathf.Min(faceSorted.Count, 4);
                    bonesPerVertex[vi] = boneCount;
                    for (int i = 0; i < boneCount; i++)
                        allWeightsList.Add(faceSorted[i]);
                }
                else
                {
                    // PhysBone頂点: 元のボーンウェイト（インデックスにtargetBoneCountを加算）
                    if (origCount > 0)
                    {
                        bonesPerVertex[vi] = (byte)origCount;
                        for (int i = 0; i < origCount; i++)
                        {
                            var w = origWeightsData[origWeightIdx + i];
                            w.boneIndex += targetBoneCount;
                            allWeightsList.Add(w);
                        }
                    }
                    else
                    {
                        // ウェイトがない場合のフォールバック
                        bonesPerVertex[vi] = 1;
                        allWeightsList.Add(new BoneWeight1 { boneIndex = 0, weight = 1f });
                    }
                }

                origWeightIdx += origCount;
            }

            // メッシュに設定
            var bpvNative = new NativeArray<byte>(bonesPerVertex, Allocator.Temp);
            var weightsNative = new NativeArray<BoneWeight1>(
                allWeightsList.ToArray(), Allocator.Temp);
            try
            {
                piercingMesh.SetBoneWeights(bpvNative, weightsNative);
            }
            finally
            {
                bpvNative.Dispose();
                weightsNative.Dispose();
            }
        }

        /// <summary>
        /// ピアスメッシュの座標系に合ったbindposeを計算する。
        /// bindpose[i] = bone[i].worldToLocal * piercingTransform.localToWorld
        /// これにより「ピアスのローカル頂点 → ワールド → ボーンローカル」の変換が正しく行われる。
        /// </summary>
        private static Matrix4x4[] ComputeBindposes(PiercingSetup setup)
        {
            var bones = setup.targetRenderer.bones;
            var piercingLocalToWorld = setup.transform.localToWorldMatrix;
            var bindposes = new Matrix4x4[bones.Length];

            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != null)
                    bindposes[i] = bones[i].worldToLocalMatrix * piercingLocalToWorld;
                else
                    bindposes[i] = Matrix4x4.identity;
            }

            return bindposes;
        }

        /// <summary>
        /// 配置時のBlendShape状態を考慮し、ピアスメッシュの頂点位置をBASE状態（weight=0）に補正する。
        /// 参照三角面のフレーム回転+重心移動の逆変換をピアス空間で直接適用する。
        ///
        /// 法線・タンジェントは回転しない。BlendShapeの法線デルタは常にゼロ（剛体追従のため）で、
        /// BASE補正の回転はBlendShapeで打ち消されない。法線を回転させると全状態で
        /// ワールド法線がMeshRendererと一致しなくなるため、位置のみ補正する。
        /// </summary>
        private static void CorrectPiercingToBasePosition(
            SkinnedMeshRenderer renderer, Mesh sourceMesh, Mesh piercingMesh,
            int[] referenceIndices, Matrix4x4 sourceToPiercing,
            float[] overrideWeights = null,
            HashSet<int> affectedVertices = null)
        {
            // ソース空間で変形前/後の位置を取得
            var baseRefPosSrc = BlendShapeTransferEngine.ExtractPositions(
                sourceMesh.vertices, referenceIndices);
            var deformedRefPosSrc = ComputeDeformedRefPositions(
                renderer, sourceMesh, referenceIndices, overrideWeights);

            // ピアス空間に変換して回転を計算（非一様スケールの往復変換を回避）
            var basePiercing = new Vector3[referenceIndices.Length];
            var deformedPiercing = new Vector3[referenceIndices.Length];
            for (int i = 0; i < referenceIndices.Length; i++)
            {
                basePiercing[i] = sourceToPiercing.MultiplyPoint3x4(baseRefPosSrc[i]);
                deformedPiercing[i] = sourceToPiercing.MultiplyPoint3x4(deformedRefPosSrc[i]);
            }

            Quaternion rotation;
            if (referenceIndices.Length == 3)
            {
                rotation = BlendShapeTransferEngine.ComputeTriangleFrameRotation(
                    basePiercing[0], basePiercing[1], basePiercing[2],
                    deformedPiercing[0], deformedPiercing[1], deformedPiercing[2]);
            }
            else
            {
                var deltas = new Vector3[referenceIndices.Length];
                for (int i = 0; i < deltas.Length; i++)
                    deltas[i] = deformedPiercing[i] - basePiercing[i];
                rotation = BlendShapeTransferEngine.ComputeRigidDelta(
                    basePiercing, deltas).rotation;
            }

            var baseCentroid = BlendShapeTransferEngine.ComputeCentroid(basePiercing);
            var deformedCentroid = BlendShapeTransferEngine.ComputeCentroid(deformedPiercing);

            // 有意な変形がなければ補正不要
            float offsetMag = (deformedCentroid - baseCentroid).magnitude;
            float rotAngle = Quaternion.Angle(rotation, Quaternion.identity);
            if (offsetMag < 0.0001f && rotAngle < 0.01f)
                return;

            // ピアス空間で直接逆変換を適用（頂点位置のみ）
            // affectedVertices が指定されている場合、その頂点のみ補正
            // （ハイブリッドモード: PhysBone頂点はbindposeとの整合を保つため補正しない）
            var invRotation = Quaternion.Inverse(rotation);
            var vertices = piercingMesh.vertices;

            for (int i = 0; i < vertices.Length; i++)
            {
                if (affectedVertices != null && !affectedVertices.Contains(i)) continue;
                vertices[i] = invRotation * (vertices[i] - deformedCentroid) + baseCentroid;
            }

            piercingMesh.vertices = vertices;
        }

        /// <summary>
        /// 現在のBlendShape状態における参照頂点群の変形後位置を計算する（ソースメッシュローカル空間）。
        /// base位置 + 全アクティブBlendShapeのウェイト付きデルタ。
        /// </summary>
        public static Vector3[] ComputeDeformedRefPositions(
            SkinnedMeshRenderer renderer, Mesh sourceMesh, int[] referenceIndices,
            float[] overrideWeights = null)
        {
            var sourceVertices = sourceMesh.vertices;
            int vertexCount = sourceMesh.vertexCount;
            int blendShapeCount = sourceMesh.blendShapeCount;

            // base位置から開始
            var deformed = new Vector3[referenceIndices.Length];
            for (int i = 0; i < referenceIndices.Length; i++)
            {
                int idx = referenceIndices[i];
                deformed[i] = idx < vertexCount ? sourceVertices[idx] : Vector3.zero;
            }

            // アクティブなBlendShapeのデルタを加算
            var deltaVertices = new Vector3[vertexCount];
            var deltaNormals = new Vector3[vertexCount];
            var deltaTangents = new Vector3[vertexCount];

            for (int si = 0; si < blendShapeCount; si++)
            {
                float weight = overrideWeights != null && si < overrideWeights.Length
                    ? overrideWeights[si]
                    : renderer.GetBlendShapeWeight(si);
                if (Mathf.Abs(weight) < 0.01f) continue;

                int frameCount = sourceMesh.GetBlendShapeFrameCount(si);
                float frameWeight = sourceMesh.GetBlendShapeFrameWeight(si, frameCount - 1);
                if (Mathf.Abs(frameWeight) < 0.01f) continue;

                sourceMesh.GetBlendShapeFrameVertices(si, frameCount - 1,
                    deltaVertices, deltaNormals, deltaTangents);

                float normalizedWeight = weight / frameWeight;

                for (int i = 0; i < referenceIndices.Length; i++)
                {
                    int idx = referenceIndices[i];
                    if (idx < vertexCount)
                        deformed[i] += deltaVertices[idx] * normalizedWeight;
                }
            }

            return deformed;
        }

        /// <summary>
        /// 現在のBlendShape状態における参照頂点群の位置オフセット重心を計算する（ソースメッシュローカル空間）。
        /// 剛体変換方式のフォールバック用。
        /// </summary>
        private static Vector3 ComputeBlendShapeOffsetCentroid(
            SkinnedMeshRenderer renderer, Mesh sourceMesh, int[] referenceIndices,
            float[] overrideWeights = null)
        {
            var sourceVertices = sourceMesh.vertices;
            var deformed = ComputeDeformedRefPositions(renderer, sourceMesh, referenceIndices, overrideWeights);

            var offset = Vector3.zero;
            for (int i = 0; i < referenceIndices.Length; i++)
            {
                int idx = referenceIndices[i];
                Vector3 basePos = idx < sourceVertices.Length ? sourceVertices[idx] : Vector3.zero;
                offset += deformed[i] - basePos;
            }
            return referenceIndices.Length > 0 ? offset / referenceIndices.Length : Vector3.zero;
        }

        /// <summary>
        /// ピアスメッシュのバウンディングボックス中心をワールド座標で返す。
        /// transform.position（原点）ではなく実際のメッシュ位置を返す。
        /// SMR の場合は BakeMesh でボーン変形後の位置を取得する。
        /// </summary>
        private static Vector3 GetPiercingMeshWorldCenter(PiercingSetup setup)
        {
            var mf = setup.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
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
        /// 固定頂点（fixedPiercingVertices）の重心をワールド座標で返す。
        /// ハイブリッドモードで参照頂点の自動検出基準として使用する。
        /// </summary>
        private static Vector3 ComputeFixedVerticesCentroid(PiercingSetup setup)
        {
            Vector3[] verts = null;
            var mf = setup.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                verts = mf.sharedMesh.vertices;
            }
            else
            {
                var smr = setup.GetComponent<SkinnedMeshRenderer>();
                if (smr != null && smr.sharedMesh != null)
                {
                    // SMRはボーン変形後の実際の頂点位置を使用
                    // （バインドポーズ位置だとボーン移動分ずれて参照頂点の検出が狂う）
                    var bakedMesh = new Mesh();
                    smr.BakeMesh(bakedMesh);
                    verts = bakedMesh.vertices;
                    Object.DestroyImmediate(bakedMesh);
                }
            }

            if (verts == null || setup.fixedPiercingVertices.Count == 0)
                return setup.transform.position;

            var sum = Vector3.zero;
            int count = 0;
            foreach (int vi in setup.fixedPiercingVertices)
            {
                if (vi >= 0 && vi < verts.Length)
                {
                    sum += verts[vi];
                    count++;
                }
            }

            if (count == 0)
                return setup.transform.position;

            var localCentroid = sum / count;
            return setup.transform.TransformPoint(localCentroid);
        }

        /// <summary>
        /// ピアスのワールド位置に最も近いソースメッシュ上の三角面の3頂点インデックスを返す。
        /// BakeMeshで現在のBlendShape/ボーン状態を反映した変形後メッシュから最近傍を検索する。
        /// </summary>
        public static int[] FindClosestTriangleVertices(SkinnedMeshRenderer renderer, Vector3 piercingWorldPos)
        {
            var bakedMesh = new Mesh();
            renderer.BakeMesh(bakedMesh);
            var vertices = bakedMesh.vertices;
            var triangles = bakedMesh.triangles;
            Object.DestroyImmediate(bakedMesh);

            var localPos = renderer.transform.InverseTransformPoint(piercingWorldPos);
            return PiercingUtility.FindClosestTriangleIndices(vertices, triangles, localPos);
        }

        /// <summary>
        /// ターゲットメッシュ上でピアス位置に最も近い2頂点のインデックスを返す。
        /// 「全体の形状を維持する」オプション用。
        /// </summary>
        public static int[] FindClosestTwoVertices(SkinnedMeshRenderer renderer, Vector3 piercingWorldPos)
        {
            var bakedMesh = new Mesh();
            renderer.BakeMesh(bakedMesh);

            var vertices = bakedMesh.vertices;
            var localPos = renderer.transform.InverseTransformPoint(piercingWorldPos);

            int closest0 = 0, closest1 = 1;
            float dist0 = float.MaxValue, dist1 = float.MaxValue;

            for (int i = 0; i < vertices.Length; i++)
            {
                float distSq = (vertices[i] - localPos).sqrMagnitude;
                if (distSq < dist0)
                {
                    closest1 = closest0;
                    dist1 = dist0;
                    closest0 = i;
                    dist0 = distSq;
                }
                else if (distSq < dist1)
                {
                    closest1 = i;
                    dist1 = distSq;
                }
            }

            Object.DestroyImmediate(bakedMesh);
            return new int[] { closest0, closest1 };
        }

        /// <summary>
        /// メッシュの全サブメッシュの三角形ワインディングを反転する。
        /// 各三角面の頂点インデックス0と1を入れ替えることでCW⇔CCWを切り替える。
        /// </summary>
        private static void FlipTriangleWinding(Mesh mesh)
        {
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var indices = mesh.GetTriangles(sub);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int tmp = indices[i];
                    indices[i] = indices[i + 1];
                    indices[i + 1] = tmp;
                }
                mesh.SetTriangles(indices, sub);
            }
        }

        /// <summary>
        /// 非一様スケールのピアスに対し、スケール成分をメッシュデータにベイクし
        /// bindposeから除去する。
        ///
        /// GPUスキニングでは法線にボーン行列が直接乗算される（inverse-transposeではない）。
        /// ボーン行列に非一様スケールが含まれると、法線の変換結果がMeshRendererと異なる。
        /// スケールをメッシュにベイクすることで実効ボーン行列が回転のみとなり、
        /// 直接乗算でも正しい法線が得られる。
        ///
        /// 一様スケールでは直接乗算とinverse-transposeが正規化後に一致するため、
        /// 補正は不要でスキップされる。
        /// </summary>
        private static void NormalizeMeshScale(Mesh mesh, PiercingSetup setup)
        {
            // localToWorldMatrixの各列ベクトルの長さ = 各軸のスケール絶対値
            var ltw = setup.transform.localToWorldMatrix;
            float sx = new Vector3(ltw.m00, ltw.m10, ltw.m20).magnitude;
            float sy = new Vector3(ltw.m01, ltw.m11, ltw.m21).magnitude;
            float sz = new Vector3(ltw.m02, ltw.m12, ltw.m22).magnitude;

            // ゼロスケールガード
            if (sx < 1e-7f || sy < 1e-7f || sz < 1e-7f) return;

            // 一様スケールなら補正不要
            float avg = (sx + sy + sz) / 3f;
            float maxDev = Mathf.Max(
                Mathf.Abs(sx - avg),
                Mathf.Abs(sy - avg),
                Mathf.Abs(sz - avg));
            if (maxDev / avg < 0.01f)
                return;

            var scale = new Vector3(sx, sy, sz);
            var invScale = new Vector3(1f / sx, 1f / sy, 1f / sz);

            // --- 頂点: スケールをベイク ---
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = Vector3.Scale(vertices[i], scale);
            mesh.vertices = vertices;

            // --- 法線: inverse-scaleで変換（inverse-transposeに相当） ---
            var normals = mesh.normals;
            if (normals != null && normals.Length > 0)
            {
                for (int i = 0; i < normals.Length; i++)
                    normals[i] = Vector3.Scale(normals[i], invScale).normalized;
                mesh.normals = normals;
            }

            // --- タンジェント方向: スケールで変換（wは保持） ---
            var tangents = mesh.tangents;
            if (tangents != null && tangents.Length > 0)
            {
                for (int i = 0; i < tangents.Length; i++)
                {
                    var dir = Vector3.Scale(
                        new Vector3(tangents[i].x, tangents[i].y, tangents[i].z),
                        scale);
                    tangents[i] = new Vector4(dir.x, dir.y, dir.z, tangents[i].w);
                }
                mesh.tangents = tangents;
            }

            // --- BlendShape位置デルタ: スケールで変換 ---
            int bsCount = mesh.blendShapeCount;
            int vtxCount = mesh.vertexCount;
            if (bsCount > 0)
            {
                var frames = new List<(string name, float weight, Vector3[] dv, Vector3[] dn, Vector3[] dt)>();
                var tmpDV = new Vector3[vtxCount];
                var tmpDN = new Vector3[vtxCount];
                var tmpDT = new Vector3[vtxCount];

                for (int si = 0; si < bsCount; si++)
                {
                    string name = mesh.GetBlendShapeName(si);
                    int fCount = mesh.GetBlendShapeFrameCount(si);
                    for (int fi = 0; fi < fCount; fi++)
                    {
                        float w = mesh.GetBlendShapeFrameWeight(si, fi);
                        mesh.GetBlendShapeFrameVertices(si, fi, tmpDV, tmpDN, tmpDT);

                        var scaledDV = new Vector3[vtxCount];
                        for (int i = 0; i < vtxCount; i++)
                            scaledDV[i] = Vector3.Scale(tmpDV[i], scale);

                        frames.Add((name, w, scaledDV,
                            (Vector3[])tmpDN.Clone(), (Vector3[])tmpDT.Clone()));
                    }
                }

                mesh.ClearBlendShapes();
                foreach (var (name, w, dv, dn, dt) in frames)
                    mesh.AddBlendShapeFrame(name, w, dv, dn, dt);
            }

            // --- bindpose: inverse-scaleを右から乗算 ---
            // gpuBone = bone.ltw × bindpose × Scale(invScale)
            //         = ltw × Scale(invScale)
            //         = R × S × S⁻¹ × sign = R × sign（回転+符号のみ）
            var bindposes = mesh.bindposes;
            if (bindposes != null && bindposes.Length > 0)
            {
                var invScaleMatrix = Matrix4x4.Scale(invScale);
                for (int i = 0; i < bindposes.Length; i++)
                    bindposes[i] = bindposes[i] * invScaleMatrix;
                mesh.bindposes = bindposes;
            }

            mesh.RecalculateBounds();
        }

        /// <summary>
        /// 中心頂点 + 半径から、範囲内の全頂点インデックスを返す。
        /// ピアスメッシュのローカル空間で距離計算する。
        /// </summary>
        public static List<int> ExpandFixedVertices(
            Mesh piercingMesh, List<int> centerVertices, float radius)
        {
            var vertices = piercingMesh.vertices;
            var result = new HashSet<int>();
            float radiusSq = radius * radius;

            foreach (int ci in centerVertices)
            {
                if (ci < 0 || ci >= vertices.Length) continue;
                var center = vertices[ci];
                result.Add(ci); // 中心頂点自体は常に含む

                for (int i = 0; i < vertices.Length; i++)
                {
                    if ((vertices[i] - center).sqrMagnitude <= radiusSq)
                        result.Add(i);
                }
            }

            return new List<int>(result);
        }

        /// <summary>
        /// fixedPiercingVertices に含まれない頂点の BlendShape デルタをゼロにする。
        /// PhysBone で制御される頂点に BlendShape の影響が及ばないようにする。
        /// </summary>
        private static void ZeroOutNonFixedBlendShapeDeltas(Mesh mesh, List<int> fixedVertices)
        {
            int bsCount = mesh.blendShapeCount;
            if (bsCount == 0) return;

            int vtxCount = mesh.vertexCount;
            var fixedSet = new HashSet<int>(fixedVertices);

            // 全BlendShapeデータを読み取り
            var frames = new List<(string name, float weight, Vector3[] dv, Vector3[] dn, Vector3[] dt)>();
            var tmpDV = new Vector3[vtxCount];
            var tmpDN = new Vector3[vtxCount];
            var tmpDT = new Vector3[vtxCount];

            for (int si = 0; si < bsCount; si++)
            {
                string name = mesh.GetBlendShapeName(si);
                int fCount = mesh.GetBlendShapeFrameCount(si);
                for (int fi = 0; fi < fCount; fi++)
                {
                    float w = mesh.GetBlendShapeFrameWeight(si, fi);
                    mesh.GetBlendShapeFrameVertices(si, fi, tmpDV, tmpDN, tmpDT);

                    var dv = (Vector3[])tmpDV.Clone();
                    var dn = (Vector3[])tmpDN.Clone();
                    var dt = (Vector3[])tmpDT.Clone();

                    // 固定頂点以外のデルタをゼロクリア
                    for (int i = 0; i < vtxCount; i++)
                    {
                        if (!fixedSet.Contains(i))
                        {
                            dv[i] = Vector3.zero;
                            dn[i] = Vector3.zero;
                            dt[i] = Vector3.zero;
                        }
                    }

                    frames.Add((name, w, dv, dn, dt));
                }
            }

            mesh.ClearBlendShapes();
            foreach (var (name, w, dv, dn, dt) in frames)
                mesh.AddBlendShapeFrame(name, w, dv, dn, dt);
        }

        /// <summary>
        /// PiercingSetup の anchors から処理用の配列を構築する。
        /// </summary>
        private static int[][] ResolveAnchorIndices(PiercingSetup setup)
        {
            if (setup.anchors == null || setup.anchors.Count < 2)
                return new int[0][];

            var result = new int[setup.anchors.Count][];
            for (int i = 0; i < setup.anchors.Count; i++)
                result[i] = setup.anchors[i].targetVertices.ToArray();
            return result;
        }

        /// <summary>
        /// 各アンカーのピアス空間での重心を計算する。
        /// piercingVertices が指定されていればその重心、なければ target 頂点の重心を変換。
        /// </summary>
        private static Vector3[] ComputeAnchorCentroids(
            PiercingSetup setup, Mesh sourceMesh, Mesh piercingMesh,
            int[][] anchorIndices, Matrix4x4 sourceToPiercing)
        {
            var sourceVertices = sourceMesh.vertices;
            var piercingVertices = piercingMesh.vertices;
            int count = anchorIndices.Length;
            var centroids = new Vector3[count];

            for (int i = 0; i < count; i++)
            {
                bool hasPiercingSide = setup.anchors != null &&
                                       i < setup.anchors.Count &&
                                       setup.anchors[i].piercingVertices.Count > 0;

                if (hasPiercingSide)
                {
                    var pVerts = setup.anchors[i].piercingVertices;
                    var sum = Vector3.zero;
                    int validCount = 0;
                    foreach (int vi in pVerts)
                    {
                        if (vi >= 0 && vi < piercingVertices.Length)
                        {
                            sum += piercingVertices[vi];
                            validCount++;
                        }
                    }
                    centroids[i] = validCount > 0 ? sum / validCount : Vector3.zero;
                }
                else
                {
                    var basePos = BlendShapeTransferEngine.ExtractPositions(
                        sourceVertices, anchorIndices[i]);
                    var centroid = BlendShapeTransferEngine.ComputeCentroid(basePos);
                    centroids[i] = sourceToPiercing.MultiplyPoint3x4(centroid);
                }
            }

            return centroids;
        }

    }
}
