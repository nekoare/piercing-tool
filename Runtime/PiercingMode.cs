using UnityEngine;

namespace PiercingTool
{
    public enum PiercingMode
    {
        [InspectorName("シングル")]
        Single,
        [InspectorName("チェーン(2点)")]
        Chain,
        [InspectorName("複数点指定(3点～)")]
        MultiAnchor
    }
}
