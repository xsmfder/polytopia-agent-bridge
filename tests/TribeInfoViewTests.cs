using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;
using Vector2 = UnityEngine.Vector2;

namespace UnityEngine { public struct Vector2 { } }
public sealed class ViewObject { public object activeInHierarchy = false; }
public sealed class ViewPlayer { public byte Id; public List<byte> knownPlayers = new List<byte> { 2, 3 }; }
public class PlayerInfoPopup
{
    public ViewObject gameObject = new ViewObject(); public object player;
    public string DataMode = "normal", ShowMode = "normal", HideMode = "normal";
    public int DataCalls, Shows, Hides;
    public void SetData(ViewPlayer value)
    { DataCalls++; if (DataMode == "throw") throw new Exception("SetData failed"); player = value; }
    public void Show(Vector2 origin)
    {
        Shows++; if (ShowMode == "throw") throw new Exception("Show failed");
        if (ShowMode != "noop") { gameObject.activeInHierarchy = true; PopupManager.Current = this; }
        if (ShowMode == "effect_throw") throw new Exception("Show effect then failure");
    }
    public void Hide()
    {
        Hides++; if (HideMode == "throw") throw new Exception("Hide failed");
        if (HideMode != "noop") gameObject.activeInHierarchy = false;
        if (HideMode == "effect_throw") throw new Exception("Hide effect then failure");
    }
}
public sealed class AmbiguousView : PlayerInfoPopup
{
    public void Show(object origin) { throw new Exception("wrong Show overload"); }
}
public static class PopupManager
{
    public static object Current; public static PlayerInfoPopup Info;
    public static object GetCurrentPopup() { return Current; }
    public static PlayerInfoPopup GetPlayerInfoPopup() { return Info; }
}
internal static class TribeInfoViewTests
{
    private sealed class Stack { public int Count = 0; }
    private sealed class State
    {
        public int CurrentPlayerIndex { get; set; } public int CurrentTurn = 1;
        public ViewPlayer[] Players { get; set; } public Stack CommandStack = new Stack();
    }
    private sealed class ActionManager { public object GameState; }
    private sealed class Client { public string clientType = "SinglePlayer"; public object GameState, ActionManager; public Guid CurrentGameId = Guid.NewGuid(); }
    private sealed class Manager
    {
        public static Manager instance { get; set; } public static object GameState { get; set; }
        public static ViewPlayer LocalPlayer { get; set; } public Client client;
    }
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly JavaScriptSerializer J = new JavaScriptSerializer();
    private static int passed, failed;
    private static void Set(BridgeMod h, string n, object v) { typeof(BridgeMod).GetField(n,F).SetValue(h,v); }
    private static object Get(BridgeMod h, string n) { return typeof(BridgeMod).GetField(n,F).GetValue(h); }
    private static Dictionary<string,object> View(BridgeMod h, string mode, string id = "request")
    {
        var m=typeof(BridgeMod).GetMethod("TribeInfoPublicRequest",F);
        var raw=m==null ? typeof(BridgeMod).GetMethod("TribeInfoPublic",F).Invoke(h,new object[]{"3",mode})
            : m.Invoke(h,new object[]{"3",mode,id});
        var result=J.Deserialize<Dictionary<string,object>>((string)raw);
        Console.WriteLine("VIEW "+mode+" "+(string)raw);
        return result;
    }
    private static BridgeMod Host(PlayerInfoPopup popup, bool owned = false)
    {
        var h=new BridgeMod(); var me=new ViewPlayer{Id=2}; var gs=new State{Players=new[]{me,new ViewPlayer{Id=3}}};
        var am=new ActionManager{GameState=gs}; Manager.instance=new Manager{client=new Client{GameState=gs,ActionManager=am}};
        Manager.GameState=gs; Manager.LocalPlayer=me; PopupManager.Info=popup; PopupManager.Current=owned ? popup : null;
        Set(h,"_gameManagerType",typeof(Manager)); Set(h,"_gameStateProp",typeof(Manager).GetProperty("GameState"));
        Set(h,"_playersProp",typeof(State).GetProperty("Players")); Set(h,"_currentPlayerIndexProp",typeof(State).GetProperty("CurrentPlayerIndex"));
        typeof(BridgeMod).GetMethod("PrivateSamplingAllowed",F).Invoke(h,new[]{(object)gs});
        Set(h,"_nextCombatSampleTime",long.MaxValue); Set(h,"_combatCaptureAvailable",true);
        Set(h,"_combatFrame",new BridgeMod.CombatFrame{Me="2",Acting="2",Complete=true});
        Set(h,"_readRequestScope",true); Set(h,"_requestPipelineState",gs); Set(h,"_requestPipelineGeneration",(long)Get(h,"_nativeSampleGeneration"));
        Set(h,"_requestSampleState",gs); Set(h,"_requestSampleGeneration",(long)Get(h,"_nativeSampleGeneration"));
        Set(h,"_requestPipeline",new BridgeMod.CombatPipelineView{PropertyProcessing=false,RawProcessing=false,Recapping=false,Simulating=false,
            Paused=false,Pausing=false,Holding=false,Reacting=false,ReactionCount=0,CurrentReaction=false,Queued=false,PendingMine=0,Popup=false,
            LocalTurn=true,StableFrames=2,QuietSeconds=1});
        if(owned) { popup.player=gs.Players[1]; popup.gameObject.activeInHierarchy=true; Set(h,"_tribeInfoOwnedPopup",popup); }
        return h;
    }
    private static void Check(bool v,string s) { if(!v)throw new Exception(s); passed++; Console.WriteLine("PASS "+s); }
    private static void Scenario(string s,Action f)
    { try{f();}catch(Exception ex){failed++;while(ex.InnerException!=null)ex=ex.InnerException;Console.WriteLine("FAIL "+s+": "+ex.Message);} }
    public static int Main()
    {
        System.IO.Directory.CreateDirectory(BridgeMod.WorkDir);
        Scenario("no-op Hide",delegate{
            var p=new PlayerInfoPopup{HideMode="noop"};var h=Host(p,true);var r=View(h,"close","close-1");
            Check((string)r["status"]!="closed","returned Hide does not prove closure");
            Check((string)r["status"]=="closing" && !(bool)r["viewEffectConfirmed"],"active target remains closing");
            View(h,"close","close-1"); View(h,"read"); Check(p.Hides==1,"same close and passive polling never repeat Hide");
            var duplicate=View(h,"close","new-close");Check((string)duplicate["outcome"]=="not_dispatched" && p.Hides==1,"new duplicate request refuses without replay");
            p.gameObject.activeInHierarchy=false;r=View(h,"read");
            Check((string)r["status"]=="closed" && (bool)r["viewEffectConfirmed"],"later actual inactivity confirms original close");
            Check((string)r["viewRequestId"]=="close-1" && (string)r["viewOperation"]=="close","close proof retains original operation identity");
            Check(Get(h,"_tribeInfoOwnedPopup")==null,"confirmed close releases native ownership");
        });
        Scenario("throwing Hide",delegate{
            string before=System.IO.File.Exists(BridgeMod.LogPath)?System.IO.File.ReadAllText(BridgeMod.LogPath):"";
            var p=new PlayerInfoPopup{HideMode="throw"};var h=Host(p,true);Set(h,"_activeRequestId",44L);var r=View(h,"close","close-2");
            Check(!(bool)r["ok"] && (string)r["status"]=="unknown","throwing Hide is not reported closed");
            string trace=System.IO.File.ReadAllText(BridgeMod.LogPath).Substring(before.Length);
            Check(trace.Contains("ui uncertain Exception #44") && trace.Contains("target=tribe_info.Hide") && !trace.Contains("Hide failed"),
                "actual failed information-view callback logs request and source without exception payload");
            View(h,"read");Check(p.Hides==1 && Get(h,"_tribeInfoOwnedPopup")==p,"unknown close retains original reference and attempt guard");
            p.gameObject.activeInHierarchy=false;Check((string)View(h,"read")["status"]=="closed" && p.Hides==1,"unknown Hide can settle by observation only");
        });
        Scenario("effect then throw",delegate{
            var p=new PlayerInfoPopup{HideMode="effect_throw"};var h=Host(p,true);var r=View(h,"close");
            Check((string)r["status"]=="closed" && (bool)r["viewEffectConfirmed"] && !(bool)r["callbackCompleted"],"actual close proves effect despite callback exception");
        });
        Scenario("unreadable active",delegate{
            var p=new PlayerInfoPopup{HideMode="noop"};var h=Host(p,true);p.gameObject.activeInHierarchy=0;var r=View(h,"close");
            Check((string)r["status"]=="closing" && !(bool)r["viewEffectConfirmed"],"non-boolean active state cannot prove closed");
        });
        Scenario("SetData failure",delegate{
            var p=new PlayerInfoPopup{DataMode="throw"};var h=Host(p);var r=View(h,"open");
            Check(!(bool)r["ok"],"SetData failure cannot report opening success");
            Check(p.DataCalls==1 && p.Shows==0 && Get(h,"_tribeInfoOwnedPopup")==null,"failed data population neither Shows nor grants ownership");
        });
        Scenario("prebind callbacks",delegate{
            var p=new AmbiguousView();var h=Host(p);var r=View(h,"open");
            Check((string)r["outcome"]=="not_dispatched" && p.DataCalls==0 && p.Shows==0,"ambiguous Show refuses before even SetData");
        });
        Scenario("rendered open",delegate{
            var p=new PlayerInfoPopup();var h=Host(p);var r=View(h,"open","open-1");
            Check((string)r["status"]=="opening" && !(bool)r["viewEffectConfirmed"],"Show completion is opening, not rendered proof");
            Check(!(bool)View(h,"read")["ok"],"same tick cannot return details");
            Set(h,"_tick",1L);r=View(h,"read");
            Check((bool)r["ok"] && (bool)r["viewEffectConfirmed"] && (string)r["viewRequestId"]=="open-1","later rendered matching target proves original open");
            View(h,"open","new-open");Check(p.DataCalls==1 && p.Shows==1,"repeated open cannot replay callbacks");
            p.player=new ViewPlayer{Id=4};Check(!(bool)View(h,"read")["ok"],"pooled popup retargeting withholds details");
        });
        Scenario("Show effect then throw",delegate{
            var p=new PlayerInfoPopup{ShowMode="effect_throw"};var h=Host(p);var r=View(h,"open","open-2");
            Check(!(bool)r["ok"] && (string)r["status"]=="unknown","throwing Show retains unknown result");
            Set(h,"_tick",1L);r=View(h,"read");
            Check((bool)r["viewEffectConfirmed"] && (string)r["viewRequestId"]=="open-2" && p.Shows==1,"rendered view recovers uncertain Show without replay");
        });
        Scenario("Show throws before effect",delegate{
            var p=new PlayerInfoPopup{ShowMode="throw"};var h=Host(p);var r=View(h,"open","open-3");
            Check(!(bool)r["ok"] && !(bool)r["callbackCompleted"],"Show exception cannot prove callback completion");
            Set(h,"_tick",1L);Check(!(bool)View(h,"read")["ok"],"inactive failed Show grants no readable details");
            View(h,"open","another-open");Check(p.Shows==1,"uncertain Show is never replayed");
        });
        Scenario("manual close and replacement",delegate{
            var p=new PlayerInfoPopup();var h=Host(p);View(h,"open","open-4");
            p.gameObject.activeInHierarchy=false;PopupManager.Current=null;
            var r=View(h,"close","manual-close");
            Check((string)r["status"]=="closed" && (bool)r["viewEffectConfirmed"] && p.Hides==0,"already inactive owned view needs no Hide callback");
            var replacement=new PlayerInfoPopup();replacement.gameObject.activeInHierarchy=true;PopupManager.Current=replacement;
            View(h,"read");Check(replacement.Hides==0,"closed receipt never closes a replacement popup");
        });
        Scenario("manual popup is not owned",delegate{
            var p=new PlayerInfoPopup();var h=Host(p,true);Set(h,"_tribeInfoOwnedPopup",null);var r=View(h,"close");
            Check((string)r["outcome"]=="not_dispatched" && p.Hides==0,"manual view cannot inherit close authorization");
        });
        Scenario("new scope cleanup",delegate{
            var p=new PlayerInfoPopup{HideMode="noop"};var h=Host(p,true);View(h,"close","old-close");
            typeof(BridgeMod).GetMethod("EnsurePlayerRuntimeScope",F).Invoke(h,new object[]{"another-episode","2"});
            Check(Get(h,"_tribeInfoOwnedPopup")==null && Get(h,"_tribeInfoViewRequestId")==null && !(bool)Get(h,"_tribeInfoClosing"),"new episode clears view reference, request and attempt guard");
            Check(p.Hides==1,"scope cleanup never invokes Hide");
        });
        Console.WriteLine("passed="+passed+" failed="+failed);return failed==0 ? 0 : 1;
    }
}
