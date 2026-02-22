#if PIERCING_NDMF
using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(PiercingTool.Editor.PiercingPlugin))]

namespace PiercingTool.Editor
{
    public class PiercingPlugin : Plugin<PiercingPlugin>
    {
        public override string DisplayName => "ピアス追従ツール";
        public override string QualifiedName => "com.nekoare.piercing-tool";

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                .BeforePlugin("nadena.dev.modular-avatar")
                .Run(PiercingPass.Instance);
        }
    }
}
#endif
