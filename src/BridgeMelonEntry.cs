#if !BRIDGE_POLYMOD
using MelonLoader;
[assembly: MelonInfo(typeof(PolytopiaAgent.MelonBridgeEntry), "PolytopiaAgentBridge", PolytopiaAgent.BridgeRelease.Version, "local-agent")]
[assembly: MelonGame(null,null)]
namespace PolytopiaAgent
{
    public sealed class MelonBridgeEntry : MelonMod
    {
        private BridgeMod runtime;
        public override void OnInitializeMelon()
        {
            runtime=new BridgeMod();
            runtime.InitializeRuntime("MelonLoader",typeof(MelonMod).Assembly.GetName().Version.ToString());
        }
        public override void OnUpdate(){if(runtime!=null)runtime.TickRuntime();}
        public override void OnDeinitializeMelon(){if(runtime!=null)runtime.StopRuntime(true);}
    }
}
#endif
