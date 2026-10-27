#if BRIDGE_POLYMOD
using System;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
namespace PolytopiaAgent
{
    public sealed class AgentBridgeScript : PolyMod.Api.PolyScript
    {
        internal static AgentBridgeScript ActiveScript;
        private BridgeMod runtime;
        private BridgeUpdateLoop loop;
        private GameObject updateObject;
        public override void Load()
        {
            if(runtime!=null)throw new InvalidOperationException("Bridge script already loaded");
            var candidate=new BridgeMod();
            try
            {
                candidate.InitializeRuntime("PolyMod",typeof(PolyMod.Api.PolyScript).Assembly.GetName().Version.ToString());
                if(!ClassInjector.IsTypeRegisteredInIl2Cpp(typeof(BridgeUpdateLoop)))
                    ClassInjector.RegisterTypeInIl2Cpp<BridgeUpdateLoop>();
                updateObject=new GameObject("PolytopiaAgentBridge");
                UnityEngine.Object.DontDestroyOnLoad(updateObject);
                loop=updateObject.AddComponent<BridgeUpdateLoop>();
                loop.Host=candidate;
                runtime=candidate;ActiveScript=this;
                Logger.LogInfo("PolytopiaAgentBridge "+BridgeRelease.Version+" started via PolyMod "+typeof(PolyMod.Api.PolyScript).Assembly.GetName().Version);
            }
            catch
            {
                candidate.StopRuntime(true);
                if(updateObject!=null)UnityEngine.Object.Destroy(updateObject);
                if(loop!=null)loop.Host=null;
                throw;
            }
        }
        public override void Unload()
        {
            if(runtime==null)return;
            if(!runtime.StopRuntime())throw new InvalidOperationException("Bridge has an unsettled operation; unload was refused");
            if(loop!=null)loop.Host=null;
            if(object.ReferenceEquals(ActiveScript,this))ActiveScript=null;
            if(updateObject!=null)UnityEngine.Object.Destroy(updateObject);
            updateObject=null;runtime=null;loop=null;
        }
    }
    public sealed class BridgeUpdateLoop : MonoBehaviour
    {
        internal BridgeMod Host;
        public BridgeUpdateLoop(IntPtr pointer):base(pointer){}
        private void Update(){if(Host!=null)Host.TickRuntime();}
        private void OnApplicationQuit(){if(Host!=null){Host.StopRuntime(true);Host=null;}}
    }
}
#endif
