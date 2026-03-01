using System.Collections.Generic;
using UnityEngine;

namespace PiercingTool.Editor
{
    internal static class PiercingUtility
    {
        /// <summary>
        /// root からの相対パスを返す（MA の referencePath 用）。
        /// </summary>
        public static string GetRelativePath(Transform target, Transform root)
        {
            var parts = new List<string>();
            var current = target;
            while (current != null && current != root)
            {
                parts.Insert(0, current.name);
                current = current.parent;
            }
            return string.Join("/", parts);
        }

        /// <summary>
        /// 頂点配列と三角面から、targetPos に最も近い三角面の3頂点インデックスを返す。
        /// 最近傍頂点を共有する三角面のうち、法線が targetPos 方向を向いているものを優先する。
        /// </summary>
        public static int[] FindClosestTriangleIndices(
            Vector3[] vertices, int[] triangles, Vector3 targetPos)
        {
            // 最近傍頂点
            float minDistSq = float.MaxValue;
            int closestVertex = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                float distSq = (vertices[i] - targetPos).sqrMagnitude;
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

                Vector3 v0 = vertices[i0], v1 = vertices[i1], v2 = vertices[i2];
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

            return new int[] {
                triangles[bestTriStart],
                triangles[bestTriStart + 1],
                triangles[bestTriStart + 2]
            };
        }
    }
}
