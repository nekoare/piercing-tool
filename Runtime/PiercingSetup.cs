using System.Collections.Generic;
using UnityEngine;

namespace PiercingTool
{
    [System.Serializable]
    public class AnchorPair
    {
        [Tooltip("Target メッシュ上の参照頂点")]
        public List<int> targetVertices = new List<int>();

        [Tooltip("ピアスメッシュ上の対応頂点（空なら自動計算）")]
        public List<int> piercingVertices = new List<int>();
    }

    [AddComponentMenu("VRCぴあっさ～")]
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

        /// <summary>
        /// 「位置を保存」時のピアス側 SkinnedMeshRenderer の BlendShape weights。
        /// ビルド時にこの状態をベイクしてピアスメッシュとして使用する。
        /// </summary>
        [HideInInspector]
        public float[] savedPiercingBlendShapeWeights;

        [HideInInspector]
        public bool isPositionSaved;

        /// <summary>
        /// プレビュー適用前の元メッシュ参照。ドメインリロード後の復元に使用。
        /// </summary>
        [HideInInspector]
        public Mesh originalMesh;

        /// <summary>
        /// SMR ピアスのプレビュー中に true。ドメインリロード後の復元に使用。
        /// </summary>
        [HideInInspector]
        public bool isSmrPreviewActive;

        // --- Chain / MultiAnchor 共通 ---
        public List<AnchorPair> anchors = new List<AnchorPair>();

        [Tooltip("lipsync・MMD用設定(メッシュを統合)\nPhysBoneが含まれるピアスには推奨されません")]
        public bool mergeIntoTarget;

        [Tooltip("PhysBone設定済みの場合、ボーンウェイト転写をスキップ")]
        public bool skipBoneWeightTransfer;

        [Tooltip("顔メッシュに追従させる固定範囲の中心頂点（skipBoneWeightTransfer時のハイブリッドモード用）")]
        public List<int> fixedPiercingVertices = new List<int>();

        [Tooltip("固定頂点の範囲半径")]
        public float fixedPiercingRadius = 0.01f;

        [Tooltip("ピアスの各頂点に最寄りの体メッシュ頂点のボーンウェイトを個別適用する")]
        public bool perVertexBoneWeights;

        [Tooltip("最寄りの2頂点を参照点にして軸回転のみで追従し、ピアスの全体的な形状を維持する")]
        public bool maintainOverallShape;

        [Tooltip("舌ピが浮く場合の調整設定")]
        public bool surfaceAttachment;
    }
}
