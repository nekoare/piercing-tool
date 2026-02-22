using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace PiercingTool.Editor
{
    /// <summary>
    /// 参照頂点のBlendShapeデルタから剛体変換（位置＋回転）を計算し、
    /// ピアスメッシュにBlendShapeとして転写するエンジン。
    /// </summary>
    public static class BlendShapeTransferEngine
    {
        /// <summary>剛体変換デルタ</summary>
        public struct RigidDelta
        {
            public Vector3 position;
            public Quaternion rotation;
        }

        // =====================================================================
        // デルタ計算
        // =====================================================================

        /// <summary>
        /// 参照頂点群のBlendShapeデルタから剛体変換を計算する。
        /// </summary>
        public static RigidDelta ComputeRigidDelta(
            Vector3[] basePositions,
            Vector3[] deltaPositions)
        {
            int count = basePositions.Length;
            if (count == 0)
                return new RigidDelta { position = Vector3.zero, rotation = Quaternion.identity };

            // 重心計算
            var baseCentroid = Vector3.zero;
            var shapeCentroid = Vector3.zero;
            for (int i = 0; i < count; i++)
            {
                baseCentroid += basePositions[i];
                shapeCentroid += basePositions[i] + deltaPositions[i];
            }
            baseCentroid /= count;
            shapeCentroid /= count;

            var positionDelta = shapeCentroid - baseCentroid;

            // 回転計算
            Quaternion rotationDelta;
            if (count >= 3)
                rotationDelta = ComputeRotationFromPlane(basePositions, deltaPositions);
            else if (count == 2)
                rotationDelta = ComputeRotationFromAxis(basePositions, deltaPositions);
            else
                rotationDelta = Quaternion.identity;

            return new RigidDelta { position = positionDelta, rotation = rotationDelta };
        }

        /// <summary>3頂点以上: 法線＋接線から回転を算出</summary>
        private static Quaternion ComputeRotationFromPlane(
            Vector3[] basePositions, Vector3[] deltaPositions)
        {
            var baseEdge1 = basePositions[1] - basePositions[0];
            var baseEdge2 = basePositions[2] - basePositions[0];
            var baseNormal = Vector3.Cross(baseEdge1, baseEdge2).normalized;
            var baseTangent = baseEdge1.normalized;

            if (baseNormal.sqrMagnitude < 1e-6f || baseTangent.sqrMagnitude < 1e-6f)
                return Quaternion.identity;

            var shapeP0 = basePositions[0] + deltaPositions[0];
            var shapeP1 = basePositions[1] + deltaPositions[1];
            var shapeP2 = basePositions[2] + deltaPositions[2];
            var shapeEdge1 = shapeP1 - shapeP0;
            var shapeEdge2 = shapeP2 - shapeP0;
            var shapeNormal = Vector3.Cross(shapeEdge1, shapeEdge2).normalized;
            var shapeTangent = shapeEdge1.normalized;

            if (shapeNormal.sqrMagnitude < 1e-6f || shapeTangent.sqrMagnitude < 1e-6f)
                return Quaternion.identity;

            var baseRotation = Quaternion.LookRotation(baseNormal, baseTangent);
            var shapeRotation = Quaternion.LookRotation(shapeNormal, shapeTangent);

            return shapeRotation * Quaternion.Inverse(baseRotation);
        }

        /// <summary>2頂点: 軸方向の回転のみ</summary>
        private static Quaternion ComputeRotationFromAxis(
            Vector3[] basePositions, Vector3[] deltaPositions)
        {
            var baseAxis = (basePositions[1] - basePositions[0]).normalized;
            var shapeAxis = ((basePositions[1] + deltaPositions[1]) -
                             (basePositions[0] + deltaPositions[0])).normalized;

            if (baseAxis.sqrMagnitude < 1e-6f || shapeAxis.sqrMagnitude < 1e-6f)
                return Quaternion.identity;

            return Quaternion.FromToRotation(baseAxis, shapeAxis);
        }

        // =====================================================================
        // Singleモード BlendShape転写
        // =====================================================================

        /// <summary>
        /// Singleモード: 参照頂点のBlendShapeデルタをピアスメッシュに剛体変換として転写する。
        /// </summary>
        public static List<string> TransferBlendShapesSingle(
            Mesh sourceMesh,
            Mesh piercingMesh,
            int[] referenceIndices,
            Vector3 piercingOrigin,
            float deltaThreshold = 0.0001f)
        {
            var transferredNames = new List<string>();

            int sourceVertexCount = sourceMesh.vertexCount;
            int piercingVertexCount = piercingMesh.vertexCount;
            var piercingVertices = piercingMesh.vertices;
            var sourceVertices = sourceMesh.vertices;

            // 参照頂点のベース位置を抽出
            var basePositions = ExtractPositions(sourceVertices, referenceIndices);

            int blendShapeCount = sourceMesh.blendShapeCount;
            var srcDeltaVertices = new Vector3[sourceVertexCount];
            var srcDeltaNormals = new Vector3[sourceVertexCount];
            var srcDeltaTangents = new Vector3[sourceVertexCount];

            for (int si = 0; si < blendShapeCount; si++)
            {
                string shapeName = sourceMesh.GetBlendShapeName(si);
                int frameCount = sourceMesh.GetBlendShapeFrameCount(si);

                sourceMesh.GetBlendShapeFrameVertices(si, frameCount - 1,
                    srcDeltaVertices, srcDeltaNormals, srcDeltaTangents);

                // 参照頂点のデルタを抽出
                var refDeltas = ExtractPositions(srcDeltaVertices, referenceIndices);

                // 剛体変換デルタを計算
                var rigid = ComputeRigidDelta(basePositions, refDeltas);

                // 閾値チェック
                if (rigid.position.magnitude < deltaThreshold &&
                    Quaternion.Angle(rigid.rotation, Quaternion.identity) < 0.01f)
                    continue;

                // ピアスメッシュの全頂点に剛体変換を適用
                var piercingDeltas = new Vector3[piercingVertexCount];
                var piercingNormalDeltas = new Vector3[piercingVertexCount];
                var piercingTangentDeltas = new Vector3[piercingVertexCount];

                for (int vi = 0; vi < piercingVertexCount; vi++)
                {
                    var localPos = piercingVertices[vi] - piercingOrigin;
                    piercingDeltas[vi] = rigid.rotation * localPos - localPos + rigid.position;
                }

                float frameWeight = sourceMesh.GetBlendShapeFrameWeight(si, frameCount - 1);
                piercingMesh.AddBlendShapeFrame(shapeName, frameWeight,
                    piercingDeltas, piercingNormalDeltas, piercingTangentDeltas);

                transferredNames.Add(shapeName);
            }

            return transferredNames;
        }

        // =====================================================================
        // Chainモード BlendShape転写
        // =====================================================================

        /// <summary>
        /// チェーンメッシュの各頂点のt値（Point A=0, Point B=1）を計算する。
        /// </summary>
        public static float[] ComputeChainTValues(
            Vector3[] piercingVertices,
            Vector3 pointACentroid,
            Vector3 pointBCentroid)
        {
            var axis = pointBCentroid - pointACentroid;
            float axisSqrMag = axis.sqrMagnitude;
            var tValues = new float[piercingVertices.Length];

            for (int i = 0; i < piercingVertices.Length; i++)
            {
                if (axisSqrMag < 1e-8f)
                {
                    tValues[i] = 0.5f;
                }
                else
                {
                    float t = Vector3.Dot(piercingVertices[i] - pointACentroid, axis) / axisSqrMag;
                    tValues[i] = Mathf.Clamp01(t);
                }
            }

            return tValues;
        }

        /// <summary>
        /// Chainモード: 2つの参照頂点群のBlendShapeデルタを補間してピアスメッシュに転写する。
        /// </summary>
        public static List<string> TransferBlendShapesChain(
            Mesh sourceMesh,
            Mesh piercingMesh,
            int[] pointAIndices,
            int[] pointBIndices,
            Vector3 piercingOrigin,
            float deltaThreshold = 0.0001f)
        {
            var transferredNames = new List<string>();

            int sourceVertexCount = sourceMesh.vertexCount;
            int piercingVertexCount = piercingMesh.vertexCount;
            var piercingVertices = piercingMesh.vertices;
            var sourceVertices = sourceMesh.vertices;

            var baseA = ExtractPositions(sourceVertices, pointAIndices);
            var baseB = ExtractPositions(sourceVertices, pointBIndices);
            var centroidA = ComputeCentroid(baseA);
            var centroidB = ComputeCentroid(baseB);

            var tValues = ComputeChainTValues(piercingVertices, centroidA, centroidB);

            int blendShapeCount = sourceMesh.blendShapeCount;
            var srcDeltaVertices = new Vector3[sourceVertexCount];
            var srcDeltaNormals = new Vector3[sourceVertexCount];
            var srcDeltaTangents = new Vector3[sourceVertexCount];

            for (int si = 0; si < blendShapeCount; si++)
            {
                string shapeName = sourceMesh.GetBlendShapeName(si);
                int frameCount = sourceMesh.GetBlendShapeFrameCount(si);

                sourceMesh.GetBlendShapeFrameVertices(si, frameCount - 1,
                    srcDeltaVertices, srcDeltaNormals, srcDeltaTangents);

                var deltasA = ExtractPositions(srcDeltaVertices, pointAIndices);
                var deltasB = ExtractPositions(srcDeltaVertices, pointBIndices);
                var rigidA = ComputeRigidDelta(baseA, deltasA);
                var rigidB = ComputeRigidDelta(baseB, deltasB);

                // 閾値チェック
                bool aSignificant = rigidA.position.magnitude >= deltaThreshold ||
                                    Quaternion.Angle(rigidA.rotation, Quaternion.identity) >= 0.01f;
                bool bSignificant = rigidB.position.magnitude >= deltaThreshold ||
                                    Quaternion.Angle(rigidB.rotation, Quaternion.identity) >= 0.01f;
                if (!aSignificant && !bSignificant)
                    continue;

                var piercingDeltas = new Vector3[piercingVertexCount];
                var piercingNormalDeltas = new Vector3[piercingVertexCount];
                var piercingTangentDeltas = new Vector3[piercingVertexCount];

                for (int vi = 0; vi < piercingVertexCount; vi++)
                {
                    float t = tValues[vi];
                    var pos = Vector3.Lerp(rigidA.position, rigidB.position, t);
                    var rot = Quaternion.Slerp(rigidA.rotation, rigidB.rotation, t);

                    var localPos = piercingVertices[vi] - piercingOrigin;
                    piercingDeltas[vi] = rot * localPos - localPos + pos;
                }

                float frameWeight = sourceMesh.GetBlendShapeFrameWeight(si, frameCount - 1);
                piercingMesh.AddBlendShapeFrame(shapeName, frameWeight,
                    piercingDeltas, piercingNormalDeltas, piercingTangentDeltas);

                transferredNames.Add(shapeName);
            }

            return transferredNames;
        }

        // =====================================================================
        // ボーンウェイト転写
        // =====================================================================

        /// <summary>
        /// Singleモード: 参照頂点のボーンウェイトを加重平均してピアス全頂点に適用する。
        /// </summary>
        public static void TransferBoneWeightsSingle(
            Mesh sourceMesh, Mesh piercingMesh, int[] referenceIndices)
        {
            var averaged = ComputeAverageBoneWeights(sourceMesh, referenceIndices);
            var sorted = NormalizeAndSort(averaged);

            int piercingVertexCount = piercingMesh.vertexCount;
            byte boneCount = (byte)Mathf.Min(sorted.Count, 4);
            var bonesPerVertex = new byte[piercingVertexCount];
            var allWeights = new BoneWeight1[piercingVertexCount * boneCount];

            for (int vi = 0; vi < piercingVertexCount; vi++)
            {
                bonesPerVertex[vi] = boneCount;
                for (int i = 0; i < boneCount; i++)
                    allWeights[vi * boneCount + i] = sorted[i];
            }

            var bonesPerVertexNative = new NativeArray<byte>(bonesPerVertex, Allocator.Temp);
            var allWeightsNative = new NativeArray<BoneWeight1>(allWeights, Allocator.Temp);
            piercingMesh.SetBoneWeights(bonesPerVertexNative, allWeightsNative);
            bonesPerVertexNative.Dispose();
            allWeightsNative.Dispose();
        }

        /// <summary>
        /// Chainモード: t値に基づいてPoint AとPoint Bのウェイトを補間して適用する。
        /// </summary>
        public static void TransferBoneWeightsChain(
            Mesh sourceMesh, Mesh piercingMesh,
            int[] pointAIndices, int[] pointBIndices,
            float[] tValues)
        {
            var weightsA = ComputeAverageBoneWeights(sourceMesh, pointAIndices);
            var weightsB = ComputeAverageBoneWeights(sourceMesh, pointBIndices);

            var allBoneIndices = new HashSet<int>();
            foreach (var kvp in weightsA) allBoneIndices.Add(kvp.Key);
            foreach (var kvp in weightsB) allBoneIndices.Add(kvp.Key);

            int piercingVertexCount = piercingMesh.vertexCount;
            var bonesPerVertex = new byte[piercingVertexCount];
            var allWeightsList = new List<BoneWeight1>();

            for (int vi = 0; vi < piercingVertexCount; vi++)
            {
                float t = tValues[vi];
                var interpolated = new List<BoneWeight1>();
                float totalWeight = 0;

                foreach (int boneIdx in allBoneIndices)
                {
                    weightsA.TryGetValue(boneIdx, out float wA);
                    weightsB.TryGetValue(boneIdx, out float wB);
                    float w = Mathf.Lerp(wA, wB, t);
                    if (w > 0.001f)
                    {
                        interpolated.Add(new BoneWeight1 { boneIndex = boneIdx, weight = w });
                        totalWeight += w;
                    }
                }

                // 正規化・ソート・上位4つに制限
                for (int i = 0; i < interpolated.Count; i++)
                {
                    var bw = interpolated[i];
                    bw.weight /= totalWeight;
                    interpolated[i] = bw;
                }
                interpolated.Sort((a, b) => b.weight.CompareTo(a.weight));
                if (interpolated.Count > 4)
                    interpolated.RemoveRange(4, interpolated.Count - 4);

                bonesPerVertex[vi] = (byte)interpolated.Count;
                allWeightsList.AddRange(interpolated);
            }

            var bonesPerVertexNative = new NativeArray<byte>(bonesPerVertex, Allocator.Temp);
            var allWeightsNative = new NativeArray<BoneWeight1>(allWeightsList.ToArray(), Allocator.Temp);
            piercingMesh.SetBoneWeights(bonesPerVertexNative, allWeightsNative);
            bonesPerVertexNative.Dispose();
            allWeightsNative.Dispose();
        }

        // =====================================================================
        // ユーティリティ
        // =====================================================================

        public static Vector3[] ExtractPositions(Vector3[] allPositions, int[] indices)
        {
            var result = new Vector3[indices.Length];
            for (int i = 0; i < indices.Length; i++)
                result[i] = allPositions[indices[i]];
            return result;
        }

        public static Vector3 ComputeCentroid(Vector3[] positions)
        {
            var sum = Vector3.zero;
            for (int i = 0; i < positions.Length; i++)
                sum += positions[i];
            return positions.Length > 0 ? sum / positions.Length : Vector3.zero;
        }

        private static Dictionary<int, float> ComputeAverageBoneWeights(Mesh mesh, int[] indices)
        {
            var sourceWeights = mesh.GetAllBoneWeights();
            var sourceBonesPerVertex = mesh.GetBonesPerVertex();
            var weightMap = new Dictionary<int, float>();

            foreach (int vi in indices)
            {
                int offset = 0;
                for (int i = 0; i < vi; i++)
                    offset += sourceBonesPerVertex[i];

                int count = sourceBonesPerVertex[vi];
                for (int i = 0; i < count; i++)
                {
                    var bw = sourceWeights[offset + i];
                    if (!weightMap.ContainsKey(bw.boneIndex))
                        weightMap[bw.boneIndex] = 0;
                    weightMap[bw.boneIndex] += bw.weight;
                }
            }

            foreach (int key in new List<int>(weightMap.Keys))
                weightMap[key] /= indices.Length;

            return weightMap;
        }

        private static List<BoneWeight1> NormalizeAndSort(Dictionary<int, float> weightMap)
        {
            float totalWeight = 0;
            var result = new List<BoneWeight1>();

            foreach (var kvp in weightMap)
            {
                if (kvp.Value > 0.001f)
                {
                    result.Add(new BoneWeight1 { boneIndex = kvp.Key, weight = kvp.Value });
                    totalWeight += kvp.Value;
                }
            }

            for (int i = 0; i < result.Count; i++)
            {
                var bw = result[i];
                bw.weight /= totalWeight;
                result[i] = bw;
            }

            result.Sort((a, b) => b.weight.CompareTo(a.weight));

            if (result.Count > 4)
                result.RemoveRange(4, result.Count - 4);

            return result;
        }
    }
}
