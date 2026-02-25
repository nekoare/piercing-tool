using System.Collections.Generic;
using UnityEngine;

namespace PiercingTool
{
    [AddComponentMenu("Piercing Tool/Piercing Setup")]
    [DisallowMultipleComponent]
    public class PiercingSetup : MonoBehaviour
#if PIERCING_VRCSDK
        , VRC.SDKBase.IEditorOnly
#endif
    {
        public PiercingMode mode = PiercingMode.Single;

        [Tooltip("BlendShapeの解析対象SkinnedMeshRenderer")]
        public SkinnedMeshRenderer targetRenderer;

        // --- Single mode ---
        [Tooltip("参照頂点のインデックスリスト")]
        public List<int> referenceVertices = new List<int>();

        // --- 位置保存（NDMF用） ---
        [HideInInspector]
        public float[] savedBlendShapeWeights;

        [HideInInspector]
        public bool isPositionSaved;

        /// <summary>
        /// プレビュー適用前の元メッシュ参照。ドメインリロード後の復元に使用。
        /// </summary>
        [HideInInspector]
        public Mesh originalMesh;

        // --- Chain mode ---
        [Tooltip("チェーンのPoint A参照頂点")]
        public List<int> pointAVertices = new List<int>();

        [Tooltip("チェーンのPoint B参照頂点")]
        public List<int> pointBVertices = new List<int>();

        [Tooltip("PhysBone設定済みの場合、ボーンウェイト転写をスキップ")]
        public bool skipBoneWeightTransfer;

        [Tooltip("ピアスの各頂点に最寄りの体メッシュ頂点のボーンウェイトを個別適用する")]
        public bool perVertexBoneWeights;
    }
}
