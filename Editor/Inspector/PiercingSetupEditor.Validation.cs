using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace PiercingTool.Editor
{
    public partial class PiercingSetupEditor
    {
        // =================================================================
        // バリデーションメッセージ
        // =================================================================

        private void DrawValidationMessages(PiercingSetup setup)
        {
            if (setup.targetRenderer == null)
            {
                EditorGUILayout.HelpBox("対象Rendererを設定してください。", MessageType.Info);
                return;
            }

            var smr = setup.GetComponent<SkinnedMeshRenderer>();
            var mf = setup.GetComponent<MeshFilter>();
            if (smr == null && mf == null)
            {
                EditorGUILayout.HelpBox(
                    "このGameObjectにSkinnedMeshRendererまたはMeshFilterが必要です。\n" +
                    "ピアスのメッシュをこのGameObjectに設定してください。",
                    MessageType.Warning);
            }

            if (setup.mode == PiercingMode.Single)
            {
                if (setup.referenceVertices.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "参照頂点が未指定のため、ピアス位置から最も近い三角面が自動選択されます。\n" +
                        "意図と異なる場合は「頂点を選択」で手動指定してください。",
                        MessageType.Info);
                }
                else if (setup.referenceVertices.Count < 3)
                {
                    EditorGUILayout.HelpBox(
                        "参照頂点が3つ未満のため、回転追従が制限されます。\n" +
                        "1頂点: 位置のみ / 2頂点: 軸回転のみ / 3頂点以上: 完全な回転追従",
                        MessageType.Info);
                }
            }

            // メッシュ統合のガイダンス
            if (setup.mergeIntoTarget)
            {
                EditorGUILayout.HelpBox(
                    "ピアスメッシュがターゲットに統合されます。\n" +
                    "リップシンク・MMDワールドでの口の追従が改善されます。",
                    MessageType.Info);
            }

            // ボーンウェイト転写のガイダンス
            if (setup.skipBoneWeightTransfer)
            {
                EditorGUILayout.HelpBox(
                    "ボーンウェイト転写をスキップしているため、ピアスのボーン構造は維持されます。\n" +
                    "アバターの部位に追従させるには MA Merge Armature または MA Bone Proxy を設定してください。",
                    MessageType.Info);
            }
            else if (!setup.mergeIntoTarget)
            {
                EditorGUILayout.HelpBox(
                    "PhysBoneによる揺れものがある場合は「ボーンウェイト転写をスキップ」をONにし、\n" +
                    "MA Merge Armature または MA Bone Proxy を設定してください。",
                    MessageType.Info);
            }

            // 頂点品質チェック
            if (setup.targetRenderer != null && setup.targetRenderer.sharedMesh != null)
            {
                var sourceVerts = setup.targetRenderer.sharedMesh.vertices;

                if (setup.mode == PiercingMode.Single && setup.referenceVertices.Count >= 2)
                {
                    var quality = EvaluateVertexQuality(sourceVerts, setup.referenceVertices);
                    DrawVertexQualityWarning(quality);
                }
                else if (setup.mode == PiercingMode.Chain || setup.mode == PiercingMode.MultiAnchor)
                {
                    if (setup.anchors != null)
                    {
                        for (int i = 0; i < setup.anchors.Count; i++)
                        {
                            var av = setup.anchors[i].targetVertices;
                            if (av.Count >= 2)
                            {
                                var qa = EvaluateVertexQuality(sourceVerts, av);
                                if (qa != VertexQuality.Ok)
                                {
                                    string anchorLabel = setup.mode == PiercingMode.Chain
                                        ? (i == 0 ? "Anchor A" : "Anchor B")
                                        : $"Anchor {i + 1}";
                                    EditorGUILayout.HelpBox(
                                        $"{anchorLabel}: {GetQualityMessage(qa)}", MessageType.Warning);
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void DrawVertexQualityWarning(VertexQuality quality)
        {
            if (quality == VertexQuality.Ok) return;
            EditorGUILayout.HelpBox(GetQualityMessage(quality), MessageType.Warning);
        }

        private static string GetQualityMessage(VertexQuality quality)
        {
            switch (quality)
            {
                case VertexQuality.TooClose:
                    return "参照頂点が近すぎます。回転追従が不安定になります。\n" +
                           "ピアスを付ける面の上で、三角形を作るように離れた頂点を選んでください。";
                case VertexQuality.Collinear:
                    return "参照頂点が直線上に並んでいます。\n" +
                           "三角形を作るように3点目を別の方向に選んでください。";
                default:
                    return string.Empty;
            }
        }

    }
}
