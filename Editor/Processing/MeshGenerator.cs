using System.Collections.Generic;
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

            // ソースメッシュ→ピアスメッシュの座標変換行列
            // 回転とスケールの違いを補正してBlendShapeデルタを正しく変換する
            var sourceToPiercing = setup.transform.worldToLocalMatrix *
                                   setup.targetRenderer.transform.localToWorldMatrix;

            // --- 負スケール: ワインディング反転 ---
            // MeshRendererはUnityが自動でfront-face判定を反転するが、
            // SkinnedMeshRendererでは自動補正されないため、
            // メッシュデータのワインディングを手動で反転して面カリングを正しくする。
            // 法線・タンジェントは inverse-transpose で正しく変換されるため変更不要。
            if (setup.transform.localToWorldMatrix.determinant < 0)
                FlipTriangleWinding(piercingMesh);

            // BlendShape転写
            List<string> transferred;
            int[] resolvedRefIndices = null; // 自動選択を含む解決済み参照頂点（ボーンウェイト用）
            if (setup.mode == PiercingMode.Single)
            {
                var refIndicesArr = setup.referenceVertices.ToArray();

                // 参照頂点が未指定の場合、ピアス位置から最近傍三角面を自動選択
                if (refIndicesArr.Length == 0)
                {
                    refIndicesArr = FindClosestTriangleVertices(
                        setup.targetRenderer, setup.transform.position);
                }

                resolvedRefIndices = refIndicesArr;

                // 配置時にBlendShapeが有効な場合、ピアス頂点をBASE状態に補正
                CorrectPiercingToBasePosition(
                    setup.targetRenderer, sourceMesh, piercingMesh,
                    refIndicesArr, sourceToPiercing,
                    setup.savedBlendShapeWeights);

                transferred = BlendShapeTransferEngine.TransferBlendShapesSingle(
                    sourceMesh, piercingMesh,
                    refIndicesArr, sourceToPiercing);
            }
            else // Chain / MultiAnchor
            {
                var anchorIndices = ResolveAnchorIndices(setup);
                if (anchorIndices.Length < 2)
                    throw new System.InvalidOperationException(
                        "Chain/MultiAnchor モードには2つ以上のアンカーが必要です。");

                var anchorCentroids = ComputeAnchorCentroids(
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

            // ボーンウェイト・bindpose設定
            if (!setup.skipBoneWeightTransfer)
            {
                TransferBoneWeights(setup, sourceMesh, piercingMesh,
                    resolvedRefIndices, sourceToPiercing);

                // ピアスメッシュの座標系に合ったbindposeを計算する
                // bindpose[i] = bone[i].worldToLocal * mesh.localToWorld
                // （ソースのbindposeはソースメッシュの座標系用なのでコピー不可）
                var bindposes = ComputeBindposes(setup);
                piercingMesh.bindposes = bindposes;

                // 非一様スケール補正: スケールをメッシュにベイクしてボーン行列から除去
                NormalizeMeshScale(piercingMesh, setup);
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

        private static void TransferBoneWeights(
            PiercingSetup setup, Mesh sourceMesh, Mesh piercingMesh,
            int[] resolvedRefIndices, Matrix4x4 sourceToPiercing)
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
                var anchorIndices = ResolveAnchorIndices(setup);
                if (anchorIndices.Length < 2) return;

                var anchorCentroids = ComputeAnchorCentroids(
                    setup, sourceMesh, piercingMesh, anchorIndices, sourceToPiercing);

                var segmentData = BlendShapeTransferEngine.ComputeSegmentTValues(
                    piercingMesh.vertices, anchorCentroids);

                BlendShapeTransferEngine.TransferBoneWeightsMultiAnchor(
                    sourceMesh, piercingMesh, anchorIndices, segmentData);
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
            float[] overrideWeights = null)
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
            var invRotation = Quaternion.Inverse(rotation);
            var vertices = piercingMesh.vertices;

            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = invRotation * (vertices[i] - deformedCentroid) + baseCentroid;

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
        /// ピアスのワールド位置に最も近いソースメッシュ上の三角面の3頂点インデックスを返す。
        /// BakeMeshで現在のBlendShape/ボーン状態を反映した変形後メッシュから最近傍を検索する。
        /// </summary>
        public static int[] FindClosestTriangleVertices(SkinnedMeshRenderer renderer, Vector3 piercingWorldPos)
        {
            var bakedMesh = new Mesh();
            renderer.BakeMesh(bakedMesh);

            var vertices = bakedMesh.vertices;
            var triangles = bakedMesh.triangles;

            // ピアスのワールド位置をBakeMeshの座標系（レンダラーのローカル空間）に変換
            var localPos = renderer.transform.InverseTransformPoint(piercingWorldPos);

            // 最も近い頂点を見つける
            float minDistSq = float.MaxValue;
            int closestVertex = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                float distSq = (vertices[i] - localPos).sqrMagnitude;
                if (distSq < minDistSq)
                {
                    minDistSq = distSq;
                    closestVertex = i;
                }
            }

            // closestVertexを共有する三角面から、法線がピアス方向を向いている最良の面を選ぶ
            float bestScore = float.NegativeInfinity;
            int bestTriStart = -1;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int i0 = triangles[i], i1 = triangles[i + 1], i2 = triangles[i + 2];
                if (i0 != closestVertex && i1 != closestVertex && i2 != closestVertex)
                    continue;

                Vector3 v0 = vertices[i0], v1 = vertices[i1], v2 = vertices[i2];
                Vector3 normal = Vector3.Cross(v1 - v0, v2 - v0).normalized;
                Vector3 centroid = (v0 + v1 + v2) / 3f;
                Vector3 toTarget = (localPos - centroid).normalized;
                float score = Vector3.Dot(normal, toTarget);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestTriStart = i;
                }
            }

            Object.DestroyImmediate(bakedMesh);

            if (bestTriStart < 0)
                return new int[] { closestVertex };

            return new int[] { triangles[bestTriStart], triangles[bestTriStart + 1], triangles[bestTriStart + 2] };
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
