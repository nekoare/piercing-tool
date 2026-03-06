using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace PiercingTool.Editor
{
    /// <summary>
    /// ピアスメッシュをターゲット SkinnedMeshRenderer のメッシュに統合する。
    /// </summary>
    public static class MeshMerger
    {
        /// <summary>
        /// ピアスメッシュをターゲットメッシュに統合する。
        /// ターゲットの sharedMesh と sharedMaterials を直接変更する。
        /// </summary>
        public static void Merge(
            SkinnedMeshRenderer targetSmr,
            Mesh piercingMesh,
            Matrix4x4 piercingToTarget,
            Material[] piercingMaterials)
        {
            var targetMesh = targetSmr.sharedMesh;
            int nTarget = targetMesh.vertexCount;
            int nPiercing = piercingMesh.vertexCount;
            int nTotal = nTarget + nPiercing;

            var normalMatrix = ComputeNormalMatrix(piercingToTarget);
            bool flipWinding = piercingToTarget.determinant < 0;

            // ==============================================================
            // 1. 全データを先に読み取る（mesh.vertices 変更で他属性がクリアされるため）
            // ==============================================================

            // 頂点属性
            var targetPositions = targetMesh.vertices;
            var targetNormals = targetMesh.normals;
            var targetTangents = targetMesh.tangents;
            var targetColors = targetMesh.colors;

            // UV (0-7)
            var targetUVs = new List<Vector4>[8];
            for (int ch = 0; ch < 8; ch++)
            {
                targetUVs[ch] = new List<Vector4>();
                targetMesh.GetUVs(ch, targetUVs[ch]);
            }

            // サブメッシュ
            int targetSubCount = targetMesh.subMeshCount;
            var targetTris = new int[targetSubCount][];
            for (int s = 0; s < targetSubCount; s++)
                targetTris[s] = targetMesh.GetTriangles(s);

            // ボーンウェイト（NativeArray は mesh.Clear() で解放されるため managed にコピー）
            var targetBpvNative = targetMesh.GetBonesPerVertex();
            var targetBpvData = targetBpvNative.IsCreated ? targetBpvNative.ToArray() : new byte[0];
            var targetWeightsNative = targetMesh.GetAllBoneWeights();
            var targetWeightsData = targetWeightsNative.IsCreated
                ? targetWeightsNative.ToArray() : new BoneWeight1[0];

            // BlendShape
            var targetShapes = ReadBlendShapes(targetMesh, nTarget);

            // ピアスの BlendShape
            var piercingShapes = ReadBlendShapes(piercingMesh, nPiercing);

            // ピアスの UV
            var piercingUVs = new List<Vector4>[8];
            for (int ch = 0; ch < 8; ch++)
            {
                piercingUVs[ch] = new List<Vector4>();
                piercingMesh.GetUVs(ch, piercingUVs[ch]);
            }

            // ピアスのボーンウェイト（同様に managed にコピー）
            var piercingBpvNative = piercingMesh.GetBonesPerVertex();
            var piercingBpvData = piercingBpvNative.IsCreated ? piercingBpvNative.ToArray() : new byte[0];
            var piercingWeightsNative = piercingMesh.GetAllBoneWeights();
            var piercingWeightsData = piercingWeightsNative.IsCreated
                ? piercingWeightsNative.ToArray() : new BoneWeight1[0];

            // ピアスのサブメッシュ
            int piercingSubCount = piercingMesh.subMeshCount;
            var piercingTris = new int[piercingSubCount][];
            for (int s = 0; s < piercingSubCount; s++)
                piercingTris[s] = piercingMesh.GetTriangles(s);

            // ==============================================================
            // 2. メッシュをクリアして再構築
            // ==============================================================
            targetMesh.Clear();
            targetMesh.indexFormat = IndexFormat.UInt32;

            // --- 頂点 ---
            var positions = new Vector3[nTotal];
            System.Array.Copy(targetPositions, positions, nTarget);
            for (int i = 0; i < nPiercing; i++)
                positions[nTarget + i] = piercingToTarget.MultiplyPoint3x4(piercingMesh.vertices[i]);
            targetMesh.vertices = positions;

            // --- 法線 ---
            var piercingNormals = piercingMesh.normals;
            if (targetNormals != null && targetNormals.Length > 0)
            {
                var normals = new Vector3[nTotal];
                System.Array.Copy(targetNormals, normals, nTarget);
                if (piercingNormals != null && piercingNormals.Length > 0)
                    for (int i = 0; i < nPiercing; i++)
                        normals[nTarget + i] = normalMatrix.MultiplyVector(piercingNormals[i]).normalized;
                targetMesh.normals = normals;
            }

            // --- タンジェント ---
            var piercingTangents = piercingMesh.tangents;
            if (targetTangents != null && targetTangents.Length > 0)
            {
                var tangents = new Vector4[nTotal];
                System.Array.Copy(targetTangents, tangents, targetTangents.Length);
                if (piercingTangents != null && piercingTangents.Length > 0)
                    for (int i = 0; i < piercingTangents.Length; i++)
                    {
                        var dir = new Vector3(piercingTangents[i].x, piercingTangents[i].y, piercingTangents[i].z);
                        dir = piercingToTarget.MultiplyVector(dir).normalized;
                        float w = piercingTangents[i].w;
                        if (flipWinding) w = -w;
                        tangents[nTarget + i] = new Vector4(dir.x, dir.y, dir.z, w);
                    }
                targetMesh.tangents = tangents;
            }

            // --- UV ---
            for (int ch = 0; ch < 8; ch++)
            {
                bool hasTarget = targetUVs[ch].Count > 0;
                bool hasPiercing = piercingUVs[ch].Count > 0;
                if (!hasTarget && !hasPiercing) continue;

                var merged = new List<Vector4>(nTotal);
                if (hasTarget)
                    merged.AddRange(targetUVs[ch]);
                else
                    for (int i = 0; i < nTarget; i++)
                        merged.Add(Vector4.zero);

                if (hasPiercing)
                    merged.AddRange(piercingUVs[ch]);
                else
                    for (int i = 0; i < nPiercing; i++)
                        merged.Add(Vector4.zero);

                targetMesh.SetUVs(ch, merged);
            }

            // --- 頂点カラー ---
            {
                bool hasTarget = targetColors != null && targetColors.Length > 0;
                var piercingColors = piercingMesh.colors;
                bool hasPiercing = piercingColors != null && piercingColors.Length > 0;

                if (hasTarget || hasPiercing)
                {
                    var colors = new Color[nTotal];
                    if (hasTarget)
                        System.Array.Copy(targetColors, colors, targetColors.Length);
                    else
                        for (int i = 0; i < nTarget; i++)
                            colors[i] = Color.white;

                    if (hasPiercing)
                        System.Array.Copy(piercingColors, 0, colors, nTarget, piercingColors.Length);
                    else
                        for (int i = 0; i < nPiercing; i++)
                            colors[nTarget + i] = Color.white;

                    targetMesh.colors = colors;
                }
            }

            // --- サブメッシュ ---
            targetMesh.subMeshCount = targetSubCount + piercingSubCount;
            for (int s = 0; s < targetSubCount; s++)
                targetMesh.SetTriangles(targetTris[s], s);
            for (int s = 0; s < piercingSubCount; s++)
            {
                var tris = piercingTris[s];
                for (int i = 0; i < tris.Length; i++)
                    tris[i] += nTarget;
                if (flipWinding)
                    for (int i = 0; i < tris.Length; i += 3)
                    {
                        int tmp = tris[i];
                        tris[i] = tris[i + 1];
                        tris[i + 1] = tmp;
                    }
                targetMesh.SetTriangles(tris, targetSubCount + s);
            }

            // --- ボーンウェイト ---
            if (targetBpvData.Length > 0 || piercingBpvData.Length > 0)
            {
                var mergedBpv = new byte[nTotal];
                for (int i = 0; i < targetBpvData.Length; i++)
                    mergedBpv[i] = targetBpvData[i];
                for (int i = 0; i < piercingBpvData.Length; i++)
                    mergedBpv[nTarget + i] = piercingBpvData[i];

                var mergedWeights = new BoneWeight1[targetWeightsData.Length + piercingWeightsData.Length];
                for (int i = 0; i < targetWeightsData.Length; i++)
                    mergedWeights[i] = targetWeightsData[i];
                for (int i = 0; i < piercingWeightsData.Length; i++)
                    mergedWeights[targetWeightsData.Length + i] = piercingWeightsData[i];

                // bindpose はターゲットのボーンから再計算
                targetMesh.bindposes = targetSmr.bones.Length > 0
                    ? ComputeTargetBindposes(targetSmr)
                    : new Matrix4x4[0];

                var bpvNative = new NativeArray<byte>(mergedBpv, Allocator.Temp);
                var weightsNative = new NativeArray<BoneWeight1>(mergedWeights, Allocator.Temp);
                targetMesh.SetBoneWeights(bpvNative, weightsNative);
                bpvNative.Dispose();
                weightsNative.Dispose();
            }

            // --- BlendShape ---
            MergeBlendShapes(targetMesh, targetShapes, piercingShapes,
                piercingToTarget, nTarget, nPiercing);

            // --- マテリアル追加 ---
            var materials = new List<Material>(targetSmr.sharedMaterials);
            materials.AddRange(piercingMaterials);
            targetSmr.sharedMaterials = materials.ToArray();

            targetMesh.RecalculateBounds();
        }

        // =====================================================================
        // ヘルパー
        // =====================================================================

        private static Matrix4x4 ComputeNormalMatrix(Matrix4x4 m)
        {
            var inv = m.inverse;
            var result = Matrix4x4.identity;
            result.m00 = inv.m00; result.m01 = inv.m10; result.m02 = inv.m20;
            result.m10 = inv.m01; result.m11 = inv.m11; result.m12 = inv.m21;
            result.m20 = inv.m02; result.m21 = inv.m12; result.m22 = inv.m22;
            result.m03 = 0; result.m13 = 0; result.m23 = 0;
            result.m30 = 0; result.m31 = 0; result.m32 = 0; result.m33 = 1;
            return result;
        }

        private static Matrix4x4[] ComputeTargetBindposes(SkinnedMeshRenderer smr)
        {
            var bones = smr.bones;
            var ltw = smr.transform.localToWorldMatrix;
            var bindposes = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++)
                bindposes[i] = bones[i] != null
                    ? bones[i].worldToLocalMatrix * ltw
                    : Matrix4x4.identity;
            return bindposes;
        }

        // =====================================================================
        // BlendShape 読み取り・結合
        // =====================================================================

        private struct BlendShapeFrame
        {
            public float weight;
            public Vector3[] dv, dn, dt;
        }

        private struct BlendShapeData
        {
            public string name;
            public List<BlendShapeFrame> frames;
        }

        private static List<BlendShapeData> ReadBlendShapes(Mesh mesh, int vertexCount)
        {
            var result = new List<BlendShapeData>(mesh.blendShapeCount);
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string name = mesh.GetBlendShapeName(i);
                int frameCount = mesh.GetBlendShapeFrameCount(i);
                var frames = new List<BlendShapeFrame>(frameCount);
                for (int f = 0; f < frameCount; f++)
                {
                    float weight = mesh.GetBlendShapeFrameWeight(i, f);
                    var dv = new Vector3[vertexCount];
                    var dn = new Vector3[vertexCount];
                    var dt = new Vector3[vertexCount];
                    mesh.GetBlendShapeFrameVertices(i, f, dv, dn, dt);
                    frames.Add(new BlendShapeFrame { weight = weight, dv = dv, dn = dn, dt = dt });
                }
                result.Add(new BlendShapeData { name = name, frames = frames });
            }
            return result;
        }

        private static void MergeBlendShapes(
            Mesh targetMesh,
            List<BlendShapeData> targetShapes,
            List<BlendShapeData> piercingShapes,
            Matrix4x4 piercingToTarget,
            int nTarget, int nPiercing)
        {
            int nTotal = nTarget + nPiercing;

            // ターゲットの BlendShape を辞書化
            var targetDict = new Dictionary<string, BlendShapeData>();
            foreach (var s in targetShapes)
                targetDict[s.name] = s;

            // ピアスの BlendShape をデルタ変換して辞書化
            var piercingDict = new Dictionary<string, BlendShapeData>();
            foreach (var s in piercingShapes)
            {
                var transformed = s;
                transformed.frames = new List<BlendShapeFrame>(s.frames.Count);
                foreach (var frame in s.frames)
                {
                    var dv = new Vector3[nPiercing];
                    for (int i = 0; i < nPiercing; i++)
                        dv[i] = piercingToTarget.MultiplyVector(frame.dv[i]);
                    transformed.frames.Add(new BlendShapeFrame
                    {
                        weight = frame.weight,
                        dv = dv, dn = frame.dn, dt = frame.dt
                    });
                }
                piercingDict[s.name] = transformed;
            }

            // 名前の順序を維持（ターゲット順 → ピアスのみ）
            var allNames = new List<string>();
            var nameSet = new HashSet<string>();
            foreach (var s in targetShapes)
                if (nameSet.Add(s.name))
                    allNames.Add(s.name);
            foreach (var s in piercingShapes)
                if (nameSet.Add(s.name))
                    allNames.Add(s.name);

            // 結合
            foreach (string name in allNames)
            {
                bool hasT = targetDict.TryGetValue(name, out var tData);
                bool hasP = piercingDict.TryGetValue(name, out var pData);

                int frameCount = hasT ? tData.frames.Count :
                                 hasP ? pData.frames.Count : 0;

                for (int f = 0; f < frameCount; f++)
                {
                    float weight = hasT ? tData.frames[f].weight : pData.frames[f].weight;

                    var dv = new Vector3[nTotal];
                    var dn = new Vector3[nTotal];
                    var dt = new Vector3[nTotal];

                    if (hasT && f < tData.frames.Count)
                    {
                        System.Array.Copy(tData.frames[f].dv, 0, dv, 0, nTarget);
                        System.Array.Copy(tData.frames[f].dn, 0, dn, 0, nTarget);
                        System.Array.Copy(tData.frames[f].dt, 0, dt, 0, nTarget);
                    }

                    if (hasP && f < pData.frames.Count)
                    {
                        System.Array.Copy(pData.frames[f].dv, 0, dv, nTarget, nPiercing);
                        System.Array.Copy(pData.frames[f].dn, 0, dn, nTarget, nPiercing);
                        System.Array.Copy(pData.frames[f].dt, 0, dt, nTarget, nPiercing);
                    }

                    targetMesh.AddBlendShapeFrame(name, weight, dv, dn, dt);
                }
            }
        }
    }
}
