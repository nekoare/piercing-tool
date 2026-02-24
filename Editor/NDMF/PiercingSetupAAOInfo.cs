#if PIERCING_AAO
using Anatawa12.AvatarOptimizer.API;

namespace PiercingTool.Editor
{
    [ComponentInformation(typeof(PiercingSetup))]
    internal class PiercingSetupAAOInfo : ComponentInformation<PiercingSetup>
    {
        protected override void CollectDependency(
            PiercingSetup component, ComponentDependencyCollector collector)
        {
            // PiercingSetupはNDMFビルド時に処理・破棄されるエディタ専用コンポーネント。
            // ランタイム依存関係なし。
        }
    }
}
#endif
