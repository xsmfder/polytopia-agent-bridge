using System;
using System.IO;
using System.Threading;
using HarmonyLib;

namespace PolytopiaAgent
{
    // Shared by both loader entry points. No loader or Unity dependency in the core.
    public partial class BridgeMod
    {
        internal const string BridgeVersion=BridgeRelease.Version;
        private static bool _processSeatCaptured;
        private static byte? _processSeat;
        internal static byte? PreserveProcessSeat(byte? configured)
        {
            if(configured==0 || configured==255)throw new InvalidOperationException("Invalid controlled seat");
            if(!_processSeatCaptured){_processSeat=configured;_processSeatCaptured=true;}
            else if(configured.HasValue && configured!=_processSeat)
                throw new InvalidOperationException("Controlled seat cannot change within the same process");
            return _processSeat;
        }
        private Semaphore _runtimeLease;
        private bool _runtimeRunning;
        private string _hostLoader, _hostVersion;
        internal HarmonyLib.Harmony HarmonyInstance { get; private set; }

        internal static Semaphore AcquireRuntimeLease(int pid)
        {
            var lease=new Semaphore(1,1,"Local\\PolytopiaAgentBridge.Host."+pid);
            if(lease.WaitOne(0))return lease;
            lease.Dispose();return null;
        }
        private void BeginRuntimeHost(string loader,string version)
        {
            if(_runtimeRunning)throw new InvalidOperationException("Bridge host is already running");
            _runtimeLease=AcquireRuntimeLease(OwnPid);
            if(_runtimeLease==null)throw new InvalidOperationException("Another Bridge host already owns this process");
            _hostLoader=loader;_hostVersion=version;
            HarmonyInstance=new HarmonyLib.Harmony("local-agent.polytopia.bridge."+OwnPid);
            _runtimeRunning=true;
        }
        private string HostReadyDocument(string document)
        {
            return document.Substring(0,document.Length-1)+",\"loader\":"+Quote(_hostLoader)
                +",\"loaderVersion\":"+Quote(_hostVersion)+",\"bridgeVersion\":"+Quote(BridgeVersion)+"}";
        }
        internal bool RuntimeHasPendingWork()
        {
            if(_combatHookDepth>0)return true;
            foreach(var ticket in _combatTickets.Values)
                if(ticket.Dispatched && ticket.Accepted!=false && ticket.FinalFrame==null)return true;
            return _gameOperation!=null && _gameOperation.Status!="confirmed" && _gameOperation.Status!="rejected";
        }
        public bool StopRuntime(bool processQuitting=false)
        {
            if(!_runtimeRunning)return true;
            if(!processQuitting && (RuntimeHasPendingWork() || _readPreparationState==1 || _readPreparationState==2))
            {Log("Bridge unload refused: pending operation or preparation; settle the original ticket first");return false;}
            _runtimeRunning=false;
            try
            {
                // Withdraw only this instance's marker. Keep historical queues and unresolved receipts.
                string ready=Path.Combine(WorkDir,"bridge-ready.json");
                if(File.Exists(ready))
                {
                    var row=BridgeJsonCodec.Object(File.ReadAllText(ready));object started;
                    if(TransportOwnsReadyFile() && row.TryGetValue("started",out started)
                        && object.Equals(started,_startupStarted))File.Delete(ready);
                }
                if(HarmonyInstance!=null)HarmonyInstance.UnpatchSelf();
                if(_publicMarkerHooks!=null)_publicMarkerHooks.Detach();
                if(object.ReferenceEquals(_combatHookHost,this))_combatHookHost=null;
                if(object.ReferenceEquals(_performanceHost,this))_performanceHost=null;
                if(object.ReferenceEquals(_displayHost,this))_displayHost=null;
                _publicResultState=null;_publicResultEpisode=null;_publicResultControlled=null;
                _publicAlive.Clear();_publicDiplomacyActive.Clear();_publicDiplomacyProof.Clear();
                _leaderboardCapture=_leaderboardSnapshot=null;ClearLeaderboardView();ClearTribeInfoView();
                _requestSampleState=_requestPipelineState=null;
                _combatFrame=null;_combatCauseStack.Clear();_cymantiActivePasses.Clear();_polarisActivePasses.Clear();
                InvalidateNativeSamples();ClearReflectionPlans();
                if(_readPreparationState!=1 && _readPreparationState!=2)
                {_readPreparationState=0;_readPreparedMethods=0;_readPreparationFailures=0;_readPreparationTicks=0;}
                Log("Bridge host stopped: "+_hostLoader);
            }
            finally
            {
                if(_runtimeLease!=null){_runtimeLease.Release();_runtimeLease.Dispose();_runtimeLease=null;}
            }
            return true;
        }
    }
}
