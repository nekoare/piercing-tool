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

            // ピアスメッシュを取得
            Mesh originalPiercingMesh = GetPiercingMesh(setup);
            if (originalPiercingMesh == null)
                throw new System.InvalidOperationException(
                    "PiercingSetupと同じGameObjectにSkinnedMeshRendererまたはMeshFilterが必要です。");

            // メッシュを複製
            var piercingMesh = Object.Instantiate(originalPiercingMesh);
            piercingMesh.name = originalPiercingMesh.name + "_Piercing";

            var piercingOrigin = ComputePiercingOrigin(piercingMesh);

            // BlendShape転写
            List<string> transferred;
            if (setup.mode == PiercingMode.Single)
            {
                transferred = BlendShapeTransferEngine.TransferBlendShapesSingle(
                    sourceMesh, piercingMesh,
                    setup.referenceVertices.ToArray(),
                    piercingOrigin);
            }
            else
            {
                transferred = BlendShapeTransferEngine.TransferBlendShapesChain(
                    sourceMesh, piercingMesh,
                    setup.pointAVertices.ToArray(),
                    setup.pointBVertices.ToArray(),
                    piercingOrigin);
            }

            // ボーンウェイト・bindpose設定
            if (!setup.skipBoneWeightTransfer)
            {
                TransferBoneWeights(setup, sourceMesh, piercingMesh);
                // ピアスメッシュの座標系に合ったbindposeを計算する
                // bindpose[i] = bone[i].worldToLocal * mesh.localToWorld
                // （ソースのbindposeはソースメッシュの座標系用なのでコピー不可）
                piercingMesh.bindposes = ComputeBindposes(setup);
            }

            // --- デバッグ情報 ---
            Debug.Log($"[PiercingTool] ソースメッシュ: 頂点数={sourceMesh.vertexCount}, BlendShape数={sourceMesh.blendShapeCount}");
            Debug.Log($"[PiercingTool] ピアスメッシュ: 頂点数={piercingMesh.vertexCount}, BlendShape数={piercingMesh.blendShapeCount}");
            Debug.Log($"[PiercingTool] ピアス原点: {piercingOrigin}");
            Debug.Log($"[PiercingTool] ソースTransform: pos={setup.targetRenderer.transform.position}, rot={setup.targetRenderer.transform.rotation.eulerAngles}, scale={setup.targetRenderer.transform.lossyScale}");
            Debug.Log($"[PiercingTool] ピアスTransform: pos={setup.transform.position}, rot={setup.transform.rotation.eulerAngles}, scale={setup.transform.lossyScale}");

            if (setup.mode == PiercingMode.Single)
            {
                var refIndices = setup.referenceVertices;
                Debug.Log($"[PiercingTool] 参照頂点インデックス: [{string.Join(", ", refIndices)}]");
                var sv = sourceMesh.vertices;
                foreach (int idx in refIndices)
                {
                    if (idx < sv.Length)
                        Debug.Log($"[PiercingTool]   頂点#{idx} ベース位置: {sv[idx]}");
                }

                // 最初のBlendShapeのデルタをログ出力
                if (sourceMesh.blendShapeCount > 0)
                {
                    var testDeltas = new Vector3[sourceMesh.vertexCount];
                    var testNormals = new Vector3[sourceMesh.vertexCount];
                    var testTangents = new Vector3[sourceMesh.vertexCount];
                    for (int si = 0; si < Mathf.Min(3, sourceMesh.blendShapeCount); si++)
                    {
                        string name = sourceMesh.GetBlendShapeName(si);
                        sourceMesh.GetBlendShapeFrameVertices(si, 0, testDeltas, testNormals, testTangents);
                        float maxDelta = 0;
                        foreach (int idx in refIndices)
                        {
                            if (idx < testDeltas.Length)
                            {
                                float mag = testDeltas[idx].magnitude;
                                if (mag > maxDelta) maxDelta = mag;
                                Debug.Log($"[PiercingTool]   BlendShape '{name}' 頂点#{idx} デルタ: {testDeltas[idx]} (magnitude: {mag:F6})");
                            }
                        }
                    }
                }
            }

            Debug.Log($"[PiercingTool] {transferred.Count}個のBlendShapeを転写しました: " +
                      string.Join(", ", transferred));
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
            AssetDatabase.SaveAssets();
            Debug.Log($"[PiercingTool] メッシュを保存しました: {path}");
            return path;
        }

        private static Mesh GetPiercingMesh(PiercingSetup setup)
        {
            var smr = setup.GetComponent<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null)
                return smr.sharedMesh;

            var mf = setup.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
                return mf.sharedMesh;

            return null;
        }

        private static void TransferBoneWeights(PiercingSetup setup, Mesh sourceMesh, Mesh piercingMesh)
        {
            if (setup.mode == PiercingMode.Single)
            {
                BlendShapeTransferEngine.TransferBoneWeightsSingle(
                    sourceMesh, piercingMesh,
                    setup.referenceVertices.ToArray());
            }
            else
            {
                var sourceVertices = sourceMesh.vertices;
                var baseA = BlendShapeTransferEngine.ExtractPositions(
                    sourceVertices, setup.pointAVertices.ToArray());
                var baseB = BlendShapeTransferEngine.ExtractPositions(
                    sourceVertices, setup.pointBVertices.ToArray());

                var centroidA = BlendShapeTransferEngine.ComputeCentroid(baseA);
                var centroidB = BlendShapeTransferEngine.ComputeCentroid(baseB);
                var tValues = BlendShapeTransferEngine.ComputeChainTValues(
                    piercingMesh.vertices, centroidA, centroidB);

                BlendShapeTransferEngine.TransferBoneWeightsChain(
                    sourceMesh, piercingMesh,
                    setup.pointAVertices.ToArray(),
                    setup.pointBVertices.ToArray(),
                    tValues);
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

        private static Vector3 ComputePiercingOrigin(Mesh mesh)
        {
            var vertices = mesh.vertices;
            var center = Vector3.zero;
            for (int i = 0; i < vertices.Length; i++)
                center += vertices[i];
            return vertices.Length > 0 ? center / vertices.Length : Vector3.zero;
        }
    }
}
