using System.Collections.Generic;
using UnityEngine;

namespace PiercingTool
{
    public static class MeshIslandDetector
    {
        /// <summary>
        /// メッシュの三角形情報から非連結アイランドを検出する。
        /// 戻り値: 各アイランドに属する頂点インデックスのリスト。
        /// </summary>
        public static List<List<int>> DetectIslands(int[] triangles, int vertexCount)
        {
            var parent = new int[vertexCount];
            var rank = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                parent[i] = i;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                Union(parent, rank, triangles[i], triangles[i + 1]);
                Union(parent, rank, triangles[i + 1], triangles[i + 2]);
            }

            // 三角形に含まれる頂点のみ収集
            var used = new HashSet<int>();
            for (int i = 0; i < triangles.Length; i++)
                used.Add(triangles[i]);

            var groups = new Dictionary<int, List<int>>();
            foreach (int v in used)
            {
                int root = Find(parent, v);
                if (!groups.ContainsKey(root))
                    groups[root] = new List<int>();
                groups[root].Add(v);
            }

            return new List<List<int>>(groups.Values);
        }

        /// <summary>
        /// メッシュの三角形情報から非連結アイランドを検出し、
        /// 空間的に近いアイランドを自動統合する。
        /// </summary>
        public static List<List<int>> DetectIslands(int[] triangles, int vertexCount, Vector3[] vertices)
        {
            var islands = DetectIslands(triangles, vertexCount);
            if (islands.Count <= 1) return islands;
            return MergeNearbyIslands(islands, vertices, triangles);
        }

        /// <summary>
        /// 頂点間の最短距離が閾値以内のアイランド同士を統合する。
        /// 閾値はメッシュの平均エッジ長 × 2 で自動決定。
        /// </summary>
        public static List<List<int>> MergeNearbyIslands(
            List<List<int>> islands, Vector3[] vertices, int[] triangles)
        {
            int n = islands.Count;
            if (n <= 1) return islands;

            // 平均エッジ長を計算して閾値を決定
            float threshold = ComputeAverageEdgeLength(triangles, vertices) * 2f;
            float thresholdSq = threshold * threshold;

            // 各アイランドの AABB を事前計算（粗いフィルタ用）
            var mins = new Vector3[n];
            var maxs = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                foreach (int vi in islands[i])
                {
                    if (vi < 0 || vi >= vertices.Length) continue;
                    min = Vector3.Min(min, vertices[vi]);
                    max = Vector3.Max(max, vertices[vi]);
                }
                mins[i] = min;
                maxs[i] = max;
            }

            // Union-Find で近接アイランド同士を統合
            var parent = new int[n];
            var rank = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    // AABB が閾値以上離れていたらスキップ（高速フィルタ）
                    if (mins[i].x - threshold > maxs[j].x || maxs[i].x + threshold < mins[j].x ||
                        mins[i].y - threshold > maxs[j].y || maxs[i].y + threshold < mins[j].y ||
                        mins[i].z - threshold > maxs[j].z || maxs[i].z + threshold < mins[j].z)
                        continue;

                    // 頂点間の最短距離を計算
                    if (MinDistanceSqBetween(islands[i], islands[j], vertices) <= thresholdSq)
                        Union(parent, rank, i, j);
                }
            }

            // 統合結果をまとめる
            var merged = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(parent, i);
                if (!merged.ContainsKey(root))
                    merged[root] = new List<int>();
                merged[root].AddRange(islands[i]);
            }

            return new List<List<int>>(merged.Values);
        }

        /// <summary>
        /// メッシュの平均エッジ長を計算する。
        /// </summary>
        private static float ComputeAverageEdgeLength(int[] triangles, Vector3[] vertices)
        {
            if (triangles.Length == 0) return 0f;
            float totalLen = 0f;
            int edgeCount = 0;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                if (a >= vertices.Length || b >= vertices.Length || c >= vertices.Length) continue;
                totalLen += Vector3.Distance(vertices[a], vertices[b]);
                totalLen += Vector3.Distance(vertices[b], vertices[c]);
                totalLen += Vector3.Distance(vertices[c], vertices[a]);
                edgeCount += 3;
            }
            return edgeCount > 0 ? totalLen / edgeCount : 0f;
        }

        /// <summary>
        /// 2つのアイランド間の最短頂点間距離の二乗を返す。
        /// </summary>
        private static float MinDistanceSqBetween(
            List<int> islandA, List<int> islandB, Vector3[] vertices)
        {
            float minSq = float.MaxValue;
            foreach (int a in islandA)
            {
                if (a < 0 || a >= vertices.Length) continue;
                var va = vertices[a];
                foreach (int b in islandB)
                {
                    if (b < 0 || b >= vertices.Length) continue;
                    float sq = (va - vertices[b]).sqrMagnitude;
                    if (sq < minSq)
                    {
                        minSq = sq;
                        if (sq == 0f) return 0f; // 早期終了
                    }
                }
            }
            return minSq;
        }

        /// <summary>
        /// 頂点インデックスリストに属する三角形を抽出する。
        /// 戻り値: 元メッシュの triangles 配列でのインデックス列（3つずつで1三角形）。
        /// </summary>
        public static List<int> ExtractTrianglesForGroup(
            int[] triangles, HashSet<int> vertexSet)
        {
            var result = new List<int>();
            for (int i = 0; i < triangles.Length; i += 3)
            {
                if (vertexSet.Contains(triangles[i]) &&
                    vertexSet.Contains(triangles[i + 1]) &&
                    vertexSet.Contains(triangles[i + 2]))
                {
                    result.Add(triangles[i]);
                    result.Add(triangles[i + 1]);
                    result.Add(triangles[i + 2]);
                }
            }
            return result;
        }

        private static int Find(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        private static void Union(int[] parent, int[] rank, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra == rb) return;
            if (rank[ra] < rank[rb]) { int t = ra; ra = rb; rb = t; }
            parent[rb] = ra;
            if (rank[ra] == rank[rb]) rank[ra]++;
        }
    }
}
