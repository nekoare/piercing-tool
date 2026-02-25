using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace PiercingTool.Editor
{
    /// <summary>
    /// 参照頂点のBlendShapeデルタから剛体変換（位置＋回転）を計算し、
    /// ピアスメッシュにBlendShapeとして転写するエンジン。
    /// 全参照頂点から最適回転を算出するため、頂点選択に依存しないロバストな結果を得られる。
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
        /// 3頂点以上の場合、全頂点から最適回転を算出する（Davenport q-method）。
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
            {
                var deformedPositions = new Vector3[count];
                for (int i = 0; i < count; i++)
                    deformedPositions[i] = basePositions[i] + deltaPositions[i];
                rotationDelta = ComputeOptimalRotation(
                    basePositions, baseCentroid, deformedPositions, shapeCentroid);
            }
            else if (count == 2)
                rotationDelta = ComputeRotationFromAxis(basePositions, deltaPositions);
            else
                rotationDelta = Quaternion.identity;

            return new RigidDelta { position = positionDelta, rotation = rotationDelta };
        }

        /// <summary>
        /// 剛体デルタをソースメッシュローカル空間からピアスメッシュローカル空間に変換する。
        /// sourceToPiercingSpace = piercingTransform.worldToLocalMatrix * sourceTransform.localToWorldMatrix
        /// </summary>
        public static RigidDelta TransformRigidDelta(RigidDelta delta, Matrix4x4 sourceToPiercingSpace)
        {
            // 位置デルタ: ベクトルとして変換（回転＋スケール適用）
            Vector3 transformedPosition = sourceToPiercingSpace.MultiplyVector(delta.position);

            // 回転デルタ: 座標変換の回転成分で共役をとる
            Quaternion coordRotation = sourceToPiercingSpace.rotation;
            Quaternion transformedRotation = coordRotation * delta.rotation * Quaternion.Inverse(coordRotation);

            return new RigidDelta { position = transformedPosition, rotation = transformedRotation };
        }

        /// <summary>
        /// N個の対応点ペアから最適な回転を計算する（Davenport q-method）。
        /// 相互共分散行列から4x4対称行列を構築し、べき乗法で最大固有ベクトル（=最適回転四元数）を求める。
        /// 全頂点を使用するため、特定の3頂点の選び方に依存しない安定した結果が得られる。
        /// </summary>
        private static Quaternion ComputeOptimalRotation(
            Vector3[] basePositions, Vector3 baseCentroid,
            Vector3[] deformedPositions, Vector3 deformedCentroid)
        {
            int count = basePositions.Length;
            if (count < 2) return Quaternion.identity;

            // 相互共分散行列 H = Σ(q_i ⊗ p_i^T)
            // p_i = base_i - baseCentroid (参照フレーム), q_i = deformed_i - deformedCentroid (観測フレーム)
            // Davenport法では H_{ij} = Σ q_i * p_j（観測×参照^T）が正しい定義
            float Sxx = 0, Sxy = 0, Sxz = 0;
            float Syx = 0, Syy = 0, Syz = 0;
            float Szx = 0, Szy = 0, Szz = 0;

            for (int i = 0; i < count; i++)
            {
                Vector3 p = basePositions[i] - baseCentroid;
                Vector3 q = deformedPositions[i] - deformedCentroid;
                Sxx += q.x * p.x; Sxy += q.x * p.y; Sxz += q.x * p.z;
                Syx += q.y * p.x; Syy += q.y * p.y; Syz += q.y * p.z;
                Szx += q.z * p.x; Szy += q.z * p.y; Szz += q.z * p.z;
            }

            // Davenport 4x4対称行列 N
            // 最大固有ベクトル = 最適回転の四元数 (w, x, y, z)
            float[] N = {
                Sxx + Syy + Szz,  Syz - Szy,        Szx - Sxz,        Sxy - Syx,
                Syz - Szy,        Sxx - Syy - Szz,  Sxy + Syx,        Szx + Sxz,
                Szx - Sxz,        Sxy + Syx,       -Sxx + Syy - Szz,  Syz + Szy,
                Sxy - Syx,        Szx + Sxz,        Syz + Szy,       -Sxx - Syy + Szz
            };

            // べき乗法で最大固有ベクトルを求める
            float[] v = { 1f, 0f, 0f, 0f };
            for (int iter = 0; iter < 30; iter++)
            {
                float[] nv = { 0f, 0f, 0f, 0f };
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 4; c++)
                        nv[r] += N[r * 4 + c] * v[c];

                float mag = Mathf.Sqrt(nv[0] * nv[0] + nv[1] * nv[1] + nv[2] * nv[2] + nv[3] * nv[3]);
                if (mag < 1e-10f) return Quaternion.identity;

                v[0] = nv[0] / mag; v[1] = nv[1] / mag; v[2] = nv[2] / mag; v[3] = nv[3] / mag;
            }

            // v = (w, x, y, z) → Unity Quaternion(x, y, z, w)
            return new Quaternion(v[1], v[2], v[3], v[0]).normalized;
        }

        /// <summary>
        /// 三角面のbase→deformed間のフレーム回転を計算する。
        /// 三角面の辺ベクトルと法線から直交座標系を構築し、その変化から回転を求める。
        /// Davenport法と異なり、3頂点が密集していても面が退化していなければ安定した結果が得られる。
        /// </summary>
        public static Quaternion ComputeTriangleFrameRotation(
            Vector3 v0Base, Vector3 v1Base, Vector3 v2Base,
            Vector3 v0Def, Vector3 v1Def, Vector3 v2Def)
        {
            // Base frame
            Vector3 baseEdge1 = v1Base - v0Base;
            Vector3 baseEdge2 = v2Base - v0Base;
            Vector3 baseNormal = Vector3.Cross(baseEdge1, baseEdge2);

            if (baseNormal.sqrMagnitude < 1e-12f)
                return Quaternion.identity;

            baseNormal.Normalize();
            Vector3 baseE1 = baseEdge1.normalized;
            Vector3 baseE2 = Vector3.Cross(baseNormal, baseE1);

            // Deformed frame
            Vector3 defEdge1 = v1Def - v0Def;
            Vector3 defEdge2 = v2Def - v0Def;
            Vector3 defNormal = Vector3.Cross(defEdge1, defEdge2);

            if (defNormal.sqrMagnitude < 1e-12f)
                return Quaternion.identity;

            defNormal.Normalize();
            Vector3 defE1 = defEdge1.normalized;
            Vector3 defE2 = Vector3.Cross(defNormal, defE1);

            // Frame rotation: base → deformed
            Quaternion baseRot = Quaternion.LookRotation(baseNormal, baseE2);
            Quaternion defRot = Quaternion.LookRotation(defNormal, defE2);

            return defRot * Quaternion.Inverse(baseRot);
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
        /// ピアス空間で直接剛体変換を計算することで、座標変換の回転共役を回避し、
        /// スケール差がある場合でも正確な結果を得る。
        /// </summary>
        public static List<string> TransferBlendShapesSingle(
            Mesh sourceMesh,
            Mesh piercingMesh,
            int[] referenceIndices,
            Matrix4x4 sourceToPiercingSpace,
            float deltaThreshold = 0.0001f)
        {
            var transferredNames = new List<string>();

            int sourceVertexCount = sourceMesh.vertexCount;
            int piercingVertexCount = piercingMesh.vertexCount;
            var piercingVertices = piercingMesh.vertices;
            var sourceVertices = sourceMesh.vertices;

            // 参照頂点のベース位置をピアス空間に変換（ループ外で1回だけ）
            var basePositionsSrc = ExtractPositions(sourceVertices, referenceIndices);
            var basePositionsPiercing = new Vector3[referenceIndices.Length];
            for (int i = 0; i < referenceIndices.Length; i++)
                basePositionsPiercing[i] = sourceToPiercingSpace.MultiplyPoint3x4(basePositionsSrc[i]);

            // 回転ピボット = ピアス空間でのベース重心（自然に正しい位置）
            var rotationPivot = ComputeCentroid(basePositionsPiercing);

            int blendShapeCount = sourceMesh.blendShapeCount;
            var srcDeltaVertices = new Vector3[sourceVertexCount];
            var srcDeltaNormals = new Vector3[sourceVertexCount];
            var srcDeltaTangents = new Vector3[sourceVertexCount];
            var deformedPiercing = new Vector3[referenceIndices.Length];
            var deltasPiercing = new Vector3[referenceIndices.Length];

            for (int si = 0; si < blendShapeCount; si++)
            {
                string shapeName = sourceMesh.GetBlendShapeName(si);
                int frameCount = sourceMesh.GetBlendShapeFrameCount(si);

                sourceMesh.GetBlendShapeFrameVertices(si, frameCount - 1,
                    srcDeltaVertices, srcDeltaNormals, srcDeltaTangents);

                // 変形後位置をピアス空間に変換し、ピアス空間でのデルタを計算
                for (int i = 0; i < referenceIndices.Length; i++)
                {
                    int idx = referenceIndices[i];
                    var deformedSrc = sourceVertices[idx] + srcDeltaVertices[idx];
                    deformedPiercing[i] = sourceToPiercingSpace.MultiplyPoint3x4(deformedSrc);
                    deltasPiercing[i] = deformedPiercing[i] - basePositionsPiercing[i];
                }

                // 回転を計算
                Quaternion rotation;
                if (referenceIndices.Length == 3)
                {
                    // 三角面フレーム回転: 面の辺・法線の変化から直接回転を計算
                    // Davenport法と異なり密集頂点でも安定し、局所的な面の傾き変化を正確に捉える
                    rotation = ComputeTriangleFrameRotation(
                        basePositionsPiercing[0], basePositionsPiercing[1], basePositionsPiercing[2],
                        deformedPiercing[0], deformedPiercing[1], deformedPiercing[2]);
                }
                else
                {
                    // 4頂点以上: Davenport法、1-2頂点: 既存のフォールバック
                    rotation = ComputeRigidDelta(basePositionsPiercing, deltasPiercing).rotation;
                }

                // 並進: 参照頂点の重心デルタ
                var translation = ComputeCentroid(deltasPiercing);

                // 閾値チェック
                if (translation.magnitude < deltaThreshold &&
                    Quaternion.Angle(rotation, Quaternion.identity) < 0.01f)
                    continue;

                // ピアスメッシュの全頂点に剛体変換を適用
                var piercingDeltas = new Vector3[piercingVertexCount];
                var piercingNormalDeltas = new Vector3[piercingVertexCount];
                var piercingTangentDeltas = new Vector3[piercingVertexCount];

                for (int vi = 0; vi < piercingVertexCount; vi++)
                {
                    var localPos = piercingVertices[vi] - rotationPivot;
                    piercingDeltas[vi] = rotation * localPos - localPos + translation;
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
        /// N 個のアンカー重心によるポリラインに対し、各ピアス頂点の
        /// (所属セグメントインデックス, セグメント内ローカルt) を計算する。
        /// </summary>
        public static (int segmentIndex, float localT)[] ComputeSegmentTValues(
            Vector3[] piercingVertices,
            Vector3[] anchorCentroids)
        {
            int anchorCount = anchorCentroids.Length;
            int vertexCount = piercingVertices.Length;
            var result = new (int, float)[vertexCount];

            if (anchorCount < 2)
            {
                for (int i = 0; i < vertexCount; i++)
                    result[i] = (0, 0f);
                return result;
            }

            for (int vi = 0; vi < vertexCount; vi++)
            {
                var p = piercingVertices[vi];
                float bestDistSq = float.MaxValue;
                int bestSeg = 0;
                float bestT = 0f;

                for (int seg = 0; seg < anchorCount - 1; seg++)
                {
                    var a = anchorCentroids[seg];
                    var b = anchorCentroids[seg + 1];
                    var ab = b - a;
                    float abSqrMag = ab.sqrMagnitude;

                    float t = abSqrMag < 1e-8f ? 0f
                        : Mathf.Clamp01(Vector3.Dot(p - a, ab) / abSqrMag);

                    var projected = a + ab * t;
                    float distSq = (p - projected).sqrMagnitude;

                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestSeg = seg;
                        bestT = t;
                    }
                }

                result[vi] = (bestSeg, bestT);
            }

            return result;
        }

        /// <summary>
        /// Chainモード: 2つの参照頂点群のBlendShapeデルタを補間してピアスメッシュに転写する。
        /// </summary>
        public static List<string> TransferBlendShapesChain(
            Mesh sourceMesh,
            Mesh piercingMesh,
            int[] pointAIndices,
            int[] pointBIndices,
            Matrix4x4 sourceToPiercingSpace,
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

            // セントロイドをピアス空間に変換してt値を計算
            var piercingCentroidA = sourceToPiercingSpace.MultiplyPoint3x4(centroidA);
            var piercingCentroidB = sourceToPiercingSpace.MultiplyPoint3x4(centroidB);
            var tValues = ComputeChainTValues(piercingVertices, piercingCentroidA, piercingCentroidB);

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

                // 閾値チェック（ソース空間で判定）
                bool aSignificant = rigidA.position.magnitude >= deltaThreshold ||
                                    Quaternion.Angle(rigidA.rotation, Quaternion.identity) >= 0.01f;
                bool bSignificant = rigidB.position.magnitude >= deltaThreshold ||
                                    Quaternion.Angle(rigidB.rotation, Quaternion.identity) >= 0.01f;
                if (!aSignificant && !bSignificant)
                    continue;

                // ソース空間→ピアス空間に変換
                var transformedA = TransformRigidDelta(rigidA, sourceToPiercingSpace);
                var transformedB = TransformRigidDelta(rigidB, sourceToPiercingSpace);

                var piercingDeltas = new Vector3[piercingVertexCount];
                var piercingNormalDeltas = new Vector3[piercingVertexCount];
                var piercingTangentDeltas = new Vector3[piercingVertexCount];

                for (int vi = 0; vi < piercingVertexCount; vi++)
                {
                    float t = tValues[vi];
                    var pos = Vector3.Lerp(transformedA.position, transformedB.position, t);
                    var rot = Quaternion.Slerp(transformedA.rotation, transformedB.rotation, t);

                    // t値に応じて回転ピボットも補間
                    var pivot = Vector3.Lerp(piercingCentroidA, piercingCentroidB, t);
                    var localPos = piercingVertices[vi] - pivot;
                    piercingDeltas[vi] = rot * localPos - localPos + pos;
                }

                float frameWeight = sourceMesh.GetBlendShapeFrameWeight(si, frameCount - 1);
                piercingMesh.AddBlendShapeFrame(shapeName, frameWeight,
                    piercingDeltas, piercingNormalDeltas, piercingTangentDeltas);

                transferredNames.Add(shapeName);
            }

            return transferredNames;
        }

        /// <summary>
        /// N点アンカー版: 各アンカーの剛体デルタをセグメント補間してピアスメッシュに転写する。
        /// </summary>
        public static List<string> TransferBlendShapesMultiAnchor(
            Mesh sourceMesh,
            Mesh piercingMesh,
            int[][] anchorIndices,
            Vector3[] anchorCentroids,
            Matrix4x4 sourceToPiercingSpace,
            float deltaThreshold = 0.0001f)
        {
            var transferredNames = new List<string>();
            int anchorCount = anchorIndices.Length;
            int sourceVertexCount = sourceMesh.vertexCount;
            int piercingVertexCount = piercingMesh.vertexCount;
            var piercingVertices = piercingMesh.vertices;
            var sourceVertices = sourceMesh.vertices;

            // 各アンカーの参照頂点ベース位置をピアス空間に変換
            var anchorBasePiercing = new Vector3[anchorCount][];
            for (int a = 0; a < anchorCount; a++)
            {
                var baseSrc = ExtractPositions(sourceVertices, anchorIndices[a]);
                anchorBasePiercing[a] = new Vector3[baseSrc.Length];
                for (int i = 0; i < baseSrc.Length; i++)
                    anchorBasePiercing[a][i] = sourceToPiercingSpace.MultiplyPoint3x4(baseSrc[i]);
            }

            var segmentData = ComputeSegmentTValues(piercingVertices, anchorCentroids);

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

                // 各アンカーの剛体デルタを計算（ピアス空間）
                var anchorRotations = new Quaternion[anchorCount];
                var anchorTranslations = new Vector3[anchorCount];
                bool anySignificant = false;

                for (int a = 0; a < anchorCount; a++)
                {
                    var deformedPiercing = new Vector3[anchorIndices[a].Length];
                    var deltasPiercing = new Vector3[anchorIndices[a].Length];

                    for (int i = 0; i < anchorIndices[a].Length; i++)
                    {
                        int idx = anchorIndices[a][i];
                        var deformedSrc = sourceVertices[idx] + srcDeltaVertices[idx];
                        deformedPiercing[i] = sourceToPiercingSpace.MultiplyPoint3x4(deformedSrc);
                        deltasPiercing[i] = deformedPiercing[i] - anchorBasePiercing[a][i];
                    }

                    Quaternion rotation;
                    if (anchorIndices[a].Length == 3)
                    {
                        rotation = ComputeTriangleFrameRotation(
                            anchorBasePiercing[a][0], anchorBasePiercing[a][1], anchorBasePiercing[a][2],
                            deformedPiercing[0], deformedPiercing[1], deformedPiercing[2]);
                    }
                    else
                    {
                        rotation = ComputeRigidDelta(anchorBasePiercing[a], deltasPiercing).rotation;
                    }

                    var translation = ComputeCentroid(deltasPiercing);
                    anchorRotations[a] = rotation;
                    anchorTranslations[a] = translation;

                    if (translation.magnitude >= deltaThreshold ||
                        Quaternion.Angle(rotation, Quaternion.identity) >= 0.01f)
                        anySignificant = true;
                }

                if (!anySignificant) continue;

                var piercingDeltas = new Vector3[piercingVertexCount];
                var piercingNormalDeltas = new Vector3[piercingVertexCount];
                var piercingTangentDeltas = new Vector3[piercingVertexCount];

                for (int vi = 0; vi < piercingVertexCount; vi++)
                {
                    var (seg, t) = segmentData[vi];
                    int a0 = seg;
                    int a1 = Mathf.Min(seg + 1, anchorCount - 1);

                    var pos = Vector3.Lerp(anchorTranslations[a0], anchorTranslations[a1], t);
                    var rot = Quaternion.Slerp(anchorRotations[a0], anchorRotations[a1], t);
                    var pivot = Vector3.Lerp(anchorCentroids[a0], anchorCentroids[a1], t);

                    var localPos = piercingVertices[vi] - pivot;
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
        /// Singleモード: 参照頂点のボーンウェイトを補間してピアス全頂点に適用する。
        /// referenceWeightsが指定された場合はバリセントリック補間、未指定の場合は単純平均を使用する。
        /// </summary>
        public static void TransferBoneWeightsSingle(
            Mesh sourceMesh, Mesh piercingMesh, int[] referenceIndices,
            float[] referenceWeights = null)
        {
            Dictionary<int, float> weighted;
            if (referenceWeights != null && referenceWeights.Length == referenceIndices.Length)
                weighted = ComputeWeightedBoneWeights(sourceMesh, referenceIndices, referenceWeights);
            else
                weighted = ComputeAverageBoneWeights(sourceMesh, referenceIndices);

            var sorted = NormalizeAndSort(weighted);

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

        /// <summary>
        /// N点アンカー版: セグメント補間でボーンウェイトを適用する。
        /// </summary>
        public static void TransferBoneWeightsMultiAnchor(
            Mesh sourceMesh, Mesh piercingMesh,
            int[][] anchorIndices,
            (int segmentIndex, float localT)[] segmentData)
        {
            int anchorCount = anchorIndices.Length;

            var anchorWeights = new Dictionary<int, float>[anchorCount];
            for (int a = 0; a < anchorCount; a++)
                anchorWeights[a] = ComputeAverageBoneWeights(sourceMesh, anchorIndices[a]);

            var allBoneIndices = new HashSet<int>();
            foreach (var w in anchorWeights)
                foreach (var kvp in w)
                    allBoneIndices.Add(kvp.Key);

            int piercingVertexCount = piercingMesh.vertexCount;
            var bonesPerVertex = new byte[piercingVertexCount];
            var allWeightsList = new List<BoneWeight1>();

            for (int vi = 0; vi < piercingVertexCount; vi++)
            {
                var (seg, t) = segmentData[vi];
                int a0 = seg;
                int a1 = Mathf.Min(seg + 1, anchorCount - 1);

                var interpolated = new List<BoneWeight1>();
                float totalWeight = 0;

                foreach (int boneIdx in allBoneIndices)
                {
                    anchorWeights[a0].TryGetValue(boneIdx, out float w0);
                    anchorWeights[a1].TryGetValue(boneIdx, out float w1);
                    float w = Mathf.Lerp(w0, w1, t);
                    if (w > 0.001f)
                    {
                        interpolated.Add(new BoneWeight1 { boneIndex = boneIdx, weight = w });
                        totalWeight += w;
                    }
                }

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
        // 頂点ごとのボーンウェイト転写
        // =====================================================================

        /// <summary>
        /// ピアスの各頂点に対し、ソースメッシュ上の最寄り三角面からバリセントリック補間した
        /// ボーンウェイトを個別に適用する。体表面の変形に追従するため、
        /// 変形が大きい部位（へそ・胸など）に有効。
        /// </summary>
        public static void TransferBoneWeightsPerVertex(
            Mesh sourceMesh, Mesh piercingMesh, Matrix4x4 piercingToSourceSpace)
        {
            var sourceVertices = sourceMesh.vertices;
            var sourceTriangles = sourceMesh.triangles;
            var piercingVertices = piercingMesh.vertices;
            int piercingVertexCount = piercingMesh.vertexCount;

            var bonesPerVertex = new byte[piercingVertexCount];
            var allWeightsList = new List<BoneWeight1>();

            for (int vi = 0; vi < piercingVertexCount; vi++)
            {
                // ピアス頂点をソースメッシュ空間に変換
                var posInSource = piercingToSourceSpace.MultiplyPoint3x4(piercingVertices[vi]);

                // 最寄りの三角面を検索
                int bestTriStart = FindNearestTriangle(
                    posInSource, sourceVertices, sourceTriangles);

                // バリセントリック座標からウェイトを補間
                Dictionary<int, float> weighted;
                if (bestTriStart >= 0)
                {
                    int i0 = sourceTriangles[bestTriStart];
                    int i1 = sourceTriangles[bestTriStart + 1];
                    int i2 = sourceTriangles[bestTriStart + 2];
                    var bary = ComputeBarycentricCoords(
                        posInSource, sourceVertices[i0], sourceVertices[i1], sourceVertices[i2]);
                    weighted = ComputeWeightedBoneWeights(
                        sourceMesh, new[] { i0, i1, i2 },
                        new[] { bary.x, bary.y, bary.z });
                }
                else
                {
                    // フォールバック: 最寄り頂点のウェイトをそのまま使用
                    int nearest = FindNearestVertex(posInSource, sourceVertices);
                    weighted = ComputeWeightedBoneWeights(
                        sourceMesh, new[] { nearest }, new[] { 1f });
                }

                var sorted = NormalizeAndSort(weighted);
                bonesPerVertex[vi] = (byte)sorted.Count;
                allWeightsList.AddRange(sorted);
            }

            var bonesPerVertexNative = new NativeArray<byte>(bonesPerVertex, Allocator.Temp);
            var allWeightsNative = new NativeArray<BoneWeight1>(allWeightsList.ToArray(), Allocator.Temp);
            piercingMesh.SetBoneWeights(bonesPerVertexNative, allWeightsNative);
            bonesPerVertexNative.Dispose();
            allWeightsNative.Dispose();
        }

        /// <summary>
        /// 指定点に最も近い三角面のインデックス（triangles配列内の開始位置）を返す。
        /// 見つからない場合は-1。
        /// </summary>
        private static int FindNearestTriangle(
            Vector3 point, Vector3[] vertices, int[] triangles)
        {
            float bestDistSq = float.MaxValue;
            int bestTriStart = -1;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 v0 = vertices[triangles[i]];
                Vector3 v1 = vertices[triangles[i + 1]];
                Vector3 v2 = vertices[triangles[i + 2]];

                // 三角面の重心までの距離で近似（高速化のため）
                Vector3 centroid = (v0 + v1 + v2) / 3f;
                float distSq = (point - centroid).sqrMagnitude;

                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestTriStart = i;
                }
            }

            return bestTriStart;
        }

        /// <summary>最寄りの頂点インデックスを返す。</summary>
        private static int FindNearestVertex(Vector3 point, Vector3[] vertices)
        {
            float bestDistSq = float.MaxValue;
            int best = 0;

            for (int i = 0; i < vertices.Length; i++)
            {
                float distSq = (point - vertices[i]).sqrMagnitude;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = i;
                }
            }

            return best;
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

        /// <summary>
        /// 参照頂点のボーンウェイトを指定の重みで加重平均する（バリセントリック補間用）。
        /// </summary>
        private static Dictionary<int, float> ComputeWeightedBoneWeights(
            Mesh mesh, int[] indices, float[] vertexWeights)
        {
            var sourceWeights = mesh.GetAllBoneWeights();
            var sourceBonesPerVertex = mesh.GetBonesPerVertex();
            var weightMap = new Dictionary<int, float>();

            for (int j = 0; j < indices.Length; j++)
            {
                int vi = indices[j];
                float vertWeight = vertexWeights[j];

                int offset = 0;
                for (int i = 0; i < vi; i++)
                    offset += sourceBonesPerVertex[i];

                int count = sourceBonesPerVertex[vi];
                for (int i = 0; i < count; i++)
                {
                    var bw = sourceWeights[offset + i];
                    if (!weightMap.ContainsKey(bw.boneIndex))
                        weightMap[bw.boneIndex] = 0;
                    weightMap[bw.boneIndex] += bw.weight * vertWeight;
                }
            }

            return weightMap;
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

        /// <summary>
        /// 点pの三角形(v0,v1,v2)上のバリセントリック座標を計算する。
        /// pは三角形平面に射影される。退化三角形の場合は等分(1/3,1/3,1/3)を返す。
        /// 返り値は (v0の重み, v1の重み, v2の重み) で合計1.0。
        /// </summary>
        public static Vector3 ComputeBarycentricCoords(
            Vector3 p, Vector3 v0, Vector3 v1, Vector3 v2)
        {
            Vector3 e0 = v1 - v0;
            Vector3 e1 = v2 - v0;
            Vector3 ep = p - v0;

            float d00 = Vector3.Dot(e0, e0);
            float d01 = Vector3.Dot(e0, e1);
            float d11 = Vector3.Dot(e1, e1);
            float dp0 = Vector3.Dot(ep, e0);
            float dp1 = Vector3.Dot(ep, e1);

            float denom = d00 * d11 - d01 * d01;
            if (Mathf.Abs(denom) < 1e-10f)
                return new Vector3(1f / 3f, 1f / 3f, 1f / 3f);

            float v = (d11 * dp0 - d01 * dp1) / denom;
            float w = (d00 * dp1 - d01 * dp0) / denom;
            float u = 1f - v - w;

            // 三角形外にはみ出た場合はクランプして正規化
            u = Mathf.Max(0f, u);
            v = Mathf.Max(0f, v);
            w = Mathf.Max(0f, w);
            float sum = u + v + w;
            return new Vector3(u / sum, v / sum, w / sum);
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
