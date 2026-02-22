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

            // ボーンウェイト・bindpose転写
            if (!setup.skipBoneWeightTransfer)
            {
                TransferBoneWeights(setup, sourceMesh, piercingMesh);
                // ソースメッシュのbindposesをコピー（ボーンインデックスが同じなので全部必要）
                piercingMesh.bindposes = sourceMesh.bindposes;
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
