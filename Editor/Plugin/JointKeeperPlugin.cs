#if JOINT_KEEPER_NDMF
using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(Dennokoworks.JointKeeper.Editor.JointKeeperPlugin))]

namespace Dennokoworks.JointKeeper.Editor
{
    internal sealed class JointKeeperPlugin : Plugin<JointKeeperPlugin>
    {
        public override string QualifiedName => "dennokoworks.joint-keeper";
        public override string DisplayName => "JointKeeper";

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run(JointKeeperPass.Instance)
                .PreviewingWith(new JointKeeperRenderFilter());
        }
    }
}
#endif
