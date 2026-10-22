using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public enum Screens { Hud, StatsScreen, Other }
public sealed class UIDeepLinkData { }
public sealed class NavObject { public object activeInHierarchy = true; }
public sealed class GameStatsScreen { public NavObject gameObject = new NavObject(); }
public sealed class UIManager
{
    public static UIManager Instance { get; set; }
    public Screens CurrentScreen = Screens.Hud;
    public object Current = new object(); public GameStatsScreen Target = new GameStatsScreen();
    public string ShowMode = "normal", PopMode = "normal"; public bool GetterFails;
    public int Shows, Pops, StackPops; public Action<GameStatsScreen> Populate; public Action Navigation;
    public object GetCurrentScreen() { if(GetterFails) throw new Exception("unreadable screen"); return Current; }
    public object ShowScreen(Screens screen, bool instant, UIDeepLinkData data)
    {
        if(Navigation!=null) Navigation(); Shows++;
        if(ShowMode=="throw") throw new Exception("Show failed");
        if(ShowMode=="noop") return null;
        if(ShowMode=="delayed") return Target;
        FinishShow();
        if(ShowMode=="effect_throw") throw new Exception("Show effect then failure");
        return Target;
    }
    public void FinishShow()
    { Current=Target;CurrentScreen=Screens.StatsScreen;Target.gameObject.activeInHierarchy=true;if(Populate!=null)Populate(Target); }
    public void PopCurrentScreen() { StackPops++; } // Native stack-only method does not hide.
    public void OnBack()
    {
        Pops++; if(PopMode=="throw") throw new Exception("Pop failed");
        if(PopMode!="noop") {Target.gameObject.activeInHierarchy=false;Current=new object();CurrentScreen=Screens.Hud;}
        if(PopMode=="effect_throw") throw new Exception("Pop effect then failure");
    }
}
public static class PopupManager { public static bool PopupShowing { get { return false; } } }
internal static class LeaderboardNavigationTests
{
    private sealed class Player { public byte Id; }
    private sealed class Settings { public string RulesGameMode="Domination"; }
    private sealed class Stack { public int Count; }
    private sealed class State
    { public int CurrentPlayerIndex{get;set;}public Player[] Players{get;set;}public int CurrentTurn=1;public Stack CommandStack=new Stack();public Settings Settings=new Settings(); }
    private sealed class Client { public string clientType="SinglePlayer";public Guid CurrentGameId=Guid.NewGuid();public object GameState; }
    private sealed class Manager
    { public static Manager instance{get;set;}public static object GameState{get;set;}public static Player LocalPlayer{get;set;}public Client client; }
    private const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private static readonly JavaScriptSerializer J=new JavaScriptSerializer();
    private static int passed,failed;
    private static void Set(BridgeMod h,string n,object v){typeof(BridgeMod).GetField(n,F).SetValue(h,v);}
    private static object Get(BridgeMod h,string n){return typeof(BridgeMod).GetField(n,F).GetValue(h);}
    private static object Call(BridgeMod h,string n,params object[] a){return typeof(BridgeMod).GetMethod(n,F).Invoke(h,a);}
    private static Dictionary<string,object> View(BridgeMod h,string mode,string id="request")
    {
        var method=typeof(BridgeMod).GetMethod("LeaderboardPublicRequest",F);
        var raw=method==null ? Call(h,"LeaderboardPublic",mode) : method.Invoke(h,new object[]{mode,id});
        return J.Deserialize<Dictionary<string,object>>((string)raw);
    }
    private static void Rows(BridgeMod h,GameStatsScreen screen)
    {
        Call(h,"BeginLeaderboardCapture",screen);Call(h,"LeaderboardScorePostfix",screen,42);Call(h,"LeaderboardDescriptionPostfix",screen,2);
        Call(h,"LeaderboardRowPostfix","<link=99>Unknown tribe</link>","42","2 cities",true,false);
        Call(h,"CompleteLeaderboardCapture",screen,false);
    }
    private static BridgeMod Host(bool owned=false)
    {
        var h=new BridgeMod();var own=new Player{Id=2};var gs=new State{Players=new[]{own,new Player{Id=3}}};
        Manager.instance=new Manager{client=new Client{GameState=gs}};Manager.GameState=gs;Manager.LocalPlayer=own;
        var ui=new UIManager();UIManager.Instance=ui;ui.Target.gameObject.activeInHierarchy=owned;
        if(owned){ui.Current=ui.Target;ui.CurrentScreen=Screens.StatsScreen;}
        Set(h,"_gameManagerType",typeof(Manager));Set(h,"_gameStateProp",typeof(Manager).GetProperty("GameState"));
        Set(h,"_playersProp",typeof(State).GetProperty("Players"));Set(h,"_currentPlayerIndexProp",typeof(State).GetProperty("CurrentPlayerIndex"));
        Call(h,"PrivateSamplingAllowed",gs);Set(h,"_displayHost",h);Set(h,"_leaderboardHooksInstalled",true);
        Set(h,"_combatCaptureAvailable",true);Set(h,"_combatFrame",new BridgeMod.CombatFrame{Me="2",Acting="2",Complete=true});
        Set(h,"_readRequestScope",true);Set(h,"_requestSampleState",gs);Set(h,"_requestSampleGeneration",(long)Get(h,"_nativeSampleGeneration"));
        Set(h,"_requestPipelineState",gs);Set(h,"_requestPipelineGeneration",(long)Get(h,"_nativeSampleGeneration"));
        Set(h,"_requestPipeline",new BridgeMod.CombatPipelineView{PropertyProcessing=false,RawProcessing=false,Recapping=false,Simulating=false,
            Paused=false,Pausing=false,Holding=false,Reacting=false,ReactionCount=0,CurrentReaction=false,Queued=false,PendingMine=0,Popup=false,
            LocalTurn=true,StableFrames=2,QuietSeconds=1});
        if(owned)Set(h,"_leaderboardOwnedScreen",ui.Target);
        ui.Populate=screen=>Rows(h,screen);
        ui.Navigation=()=>{var m=typeof(BridgeMod).GetMethod("LeaderboardNavigationPrefix",F);if(m!=null)m.Invoke(null,new object[]{ui});};
        return h;
    }
    private static void Check(bool v,string s){if(!v)throw new Exception(s);passed++;Console.WriteLine("PASS "+s);}
    private static void Scenario(string s,Action a)
    {try{a();}catch(Exception ex){failed++;while(ex.InnerException!=null)ex=ex.InnerException;Console.WriteLine("FAIL "+s+": "+ex.Message);}}
    private static object BeginHook(BridgeMod h, object screen)
    {
        var m=typeof(BridgeMod).GetMethod("LeaderboardPopulatePrefix",F);
        if(m.GetParameters().Length==1){m.Invoke(h,new[]{screen});return Get(h,"_leaderboardCapture");}
        object[] args={screen,null};m.Invoke(h,args);return args[1];
    }
    private static object EndHook(BridgeMod h,object screen,Exception error,object state)
    {
        var m=typeof(BridgeMod).GetMethod("LeaderboardPopulateFinalizer",F);
        return m.Invoke(h,m.GetParameters().Length==2?new object[]{screen,error}:new object[]{screen,error,state});
    }
    public static int Main()
    {
        System.IO.Directory.CreateDirectory(BridgeMod.WorkDir);
        Scenario("absent navigation identity",delegate{
            var absent=J.Deserialize<Dictionary<string,object>>((string)Call(null,"LeaderboardUnavailable","not controlled player's turn",""));
            Check(absent["viewRequestId"]==null && (string)absent["outcome"]=="not_dispatched",
                "no dispatched leaderboard navigation has a null ID even when dispatcher supplies empty text");
            var attempt=J.Deserialize<Dictionary<string,object>>((string)Call(null,"LeaderboardUnavailable","not controlled player's turn","real-attempt"));
            Check((string)attempt["viewRequestId"]=="real-attempt",
                "actual unavailable navigation attempt identity is retained");
        });
        Scenario("no-op pop",delegate{
            var h=Host(true);var ui=UIManager.Instance;ui.PopMode="noop";var r=View(h,"close","close-1");
            Check((string)r["status"]=="closing" && !(bool)r["viewEffectConfirmed"],"returned Pop cannot prove close while target remains active");
            Check(ui.StackPops==0,"adapter uses full native OnBack rather than stack-only PopCurrentScreen");
            View(h,"close","close-1");View(h,"read");Check(ui.Pops==1,"original close polling never pops twice");
            Check((string)View(h,"close","new-close")["outcome"]=="not_dispatched" && ui.Pops==1,"new duplicate close cannot pop another page");
            ui.Target.gameObject.activeInHierarchy=false;r=View(h,"read");
            Check((string)r["status"]=="closed" && (bool)r["viewEffectConfirmed"] && (string)r["viewRequestId"]=="close-1","later actual inactivity proves original close");
            Check(Get(h,"_leaderboardOwnedScreen")==null,"confirmed close releases target reference");
        });
        Scenario("pop failures",delegate{
            string before=System.IO.File.Exists(BridgeMod.LogPath)?System.IO.File.ReadAllText(BridgeMod.LogPath):"";
            var h=Host(true);Set(h,"_activeRequestId",45L);var ui=UIManager.Instance;ui.PopMode="throw";var r=View(h,"close","close-2");
            Check(!(bool)r["ok"] && (string)r["status"]=="unknown","throwing Pop remains unknown");
            string trace=System.IO.File.ReadAllText(BridgeMod.LogPath).Substring(before.Length);
            Check(trace.Contains("ui uncertain Exception #45") && trace.Contains("target=leaderboard.OnBack") && !trace.Contains("Pop failed"),
                "actual failed leaderboard callback logs request and source without exception payload");
            View(h,"read");Check(ui.Pops==1 && Get(h,"_leaderboardOwnedScreen")==ui.Target,"unknown close preserves exact target and guard");
            ui.Target.gameObject.activeInHierarchy=false;Check((string)View(h,"read")["status"]=="closed","unknown Pop can settle by passive observation");
            h=Host(true);ui=UIManager.Instance;ui.PopMode="effect_throw";r=View(h,"close");
            Check((string)r["status"]=="closed" && !(bool)r["callbackCompleted"],"actual inactive effect can confirm despite later callback failure");
        });
        Scenario("unreadable page",delegate{
            var h=Host(true);var ui=UIManager.Instance;ui.PopMode="noop";View(h,"close");ui.GetterFails=true;
            Check((string)View(h,"read")["status"]=="closing","failed current-screen getter cannot fabricate closure");
            ui.Target.gameObject.activeInHierarchy=0;
            Check(!(bool)View(h,"read")["viewEffectConfirmed"],"non-boolean activity cannot prove hidden");
        });
        Scenario("delayed open",delegate{
            var h=Host();var ui=UIManager.Instance;ui.ShowMode="delayed";var r=View(h,"open","open-1");
            Check((string)r["status"]=="opening" && !(bool)r["viewEffectConfirmed"],"normal ShowScreen return is not populated view proof");
            Check(Object.ReferenceEquals(Get(h,"_leaderboardOwnedScreen"),ui.Target),"native returned target is retained through delayed presentation");
            Check((string)View(h,"read")["status"]=="opening","missing capture remains pending rather than empty successful rows");
            Set(h,"_tick",1L);ui.FinishShow();r=View(h,"read");
            Check((bool)r["viewEffectConfirmed"] && (string)r["viewRequestId"]=="open-1","current target plus fresh matching native population proves open");
            Check(((System.Collections.ICollection)r["rows"]).Count==1 && (string)r["streamId"]==(string)Get(h,"_combatStream"),"normal captured rows and stream identity retained");
            View(h,"open","another-open");Check(ui.Shows==1,"new open request cannot replay pending or completed ShowScreen");
        });
        Scenario("show after effect exception",delegate{
            var h=Host();var ui=UIManager.Instance;ui.ShowMode="effect_throw";var r=View(h,"open","open-2");
            Check((string)r["status"]=="unknown" && !(bool)r["ok"],"throwing Show does not report successful callback");
            Set(h,"_tick",1L);r=View(h,"read");Check((bool)r["viewEffectConfirmed"] && ui.Shows==1,"matching populated view recovers unknown Show without replay");
        });
        Scenario("failed Show and manual navigation",delegate{
            var h=Host();var ui=UIManager.Instance;ui.ShowMode="throw";View(h,"open","lost-open");
            Check(Get(h,"_leaderboardOwnedScreen")==null,"failed Show without target grants no ownership");
            ui.ShowMode="normal";ui.ShowScreen(Screens.StatsScreen,false,null);Set(h,"_tick",1L);var r=View(h,"read");
            Check(!(bool)r["viewEffectConfirmed"] && r["viewRequestId"]==null,"later manual population cannot recover old open request");
            Check((string)View(h,"close")["outcome"]=="not_dispatched" && ui.Pops==0,"manually opened page cannot inherit close authority");
        });
        Scenario("pooled screen navigation",delegate{
            var h=Host();var ui=UIManager.Instance;View(h,"open","first-open");Set(h,"_tick",1L);
            Check((bool)View(h,"read")["viewEffectConfirmed"],"original pooled screen is initially owned");
            ui.ShowScreen(Screens.StatsScreen,false,null);var r=View(h,"read");
            Check(r["viewRequestId"]==null && !(bool)r["viewEffectConfirmed"],"external navigation retires authorization even for same pooled object");
            Check((string)View(h,"close")["outcome"]=="not_dispatched" && ui.Pops==0,"reused manual screen is not popped under old ownership");
        });
        Scenario("historical capture",delegate{
            var h=Host();View(h,"open","historic-open");Set(h,"_tick",1L);((State)Manager.GameState).CurrentTurn++;
            var r=View(h,"read");Check((string)r["status"]=="historical" && !(bool)r["viewEffectConfirmed"],"historical rows cannot prove a new open");
            Check(((System.Collections.ICollection)r["rows"]).Count==1,"historical player-visible rows stay available");
        });
        Scenario("rows survive confirmed close",delegate{
            var h=Host();var ui=UIManager.Instance;View(h,"open","open-3");Set(h,"_tick",1L);View(h,"read");var r=View(h,"close","close-3");
            Check((string)r["status"]=="closed" && ((System.Collections.ICollection)r["rows"]).Count==1 && r.ContainsKey("snapshotStatus"),"navigation closure does not erase recorded statistics");
            View(h,"read");Check(ui.Pops==1,"confirmed receipt query has no further navigation");
        });
        Scenario("scope reset",delegate{
            var h=Host(true);var ui=UIManager.Instance;ui.PopMode="noop";View(h,"close","old-close");
            Call(h,"EnsurePlayerRuntimeScope","another-game","2");
            Check(Get(h,"_leaderboardOwnedScreen")==null && Get(h,"_leaderboardViewRequestId")==null && !(bool)Get(h,"_leaderboardClosing")
                && (long)Get(h,"_leaderboardOpenOriginRequestId")==0,"new scope clears page, origin, operation and single-attempt guard");
            Check(ui.Pops==1,"cleanup never navigates");
        });
        Scenario("asynchronous origin diagnostics",delegate{
            var h=Host();var ui=UIManager.Instance;ui.ShowMode="delayed";Set(h,"_activeRequestId",71L);
            View(h,"open","origin-open");ui.Current=ui.Target;ui.CurrentScreen=Screens.StatsScreen;
            object capture=BeginHook(h,ui.Target);Check(capture!=null,"native prefix creates its own capture token");
            Set(h,"_activeRequestId",92L);ui.PopMode="noop";View(h,"close","later-close");
            string before=System.IO.File.ReadAllText(BridgeMod.LogPath);
            var error=new Exception("SECRET_ASYNC_NATIVE");object returned=EndHook(h,ui.Target,error,capture);
            string trace=System.IO.File.ReadAllText(BridgeMod.LogPath).Substring(before.Length);
            Check(trace.Contains("originRequest=71")&&trace.Contains("viewRequest=origin-open")&&!trace.Contains("originRequest=92")
                &&!trace.Contains("SECRET_ASYNC"),"later native failure keeps original open request instead of the active close request");
            Check(ReferenceEquals(returned,error)&&Get(h,"_leaderboardSnapshot")==null,"native exception is preserved and failed rows never become a valid snapshot");
        });
        Scenario("capture generation isolation",delegate{
            var h=Host();var ui=UIManager.Instance;ui.ShowMode="delayed";Set(h,"_activeRequestId",73L);View(h,"open","generation-open");
            object older=BeginHook(h,ui.Target),newer=BeginHook(h,ui.Target);
            EndHook(h,ui.Target,new Exception("old native call failed"),older);
            Check(newer!=null&&ReferenceEquals(Get(h,"_leaderboardCapture"),newer),"old finalizer never clears a newer invocation on the same pooled screen");
            EndHook(h,ui.Target,null,newer);
        });
        Scenario("row projection failure trace",delegate{
            var h=Host();var ui=UIManager.Instance;ui.ShowMode="delayed";Set(h,"_activeRequestId",74L);View(h,"open","row-open");
            object capture=BeginHook(h,ui.Target);capture.GetType().GetField("Rows",F).SetValue(capture,null);Set(h,"_activeRequestId",0L);
            string before=System.IO.File.ReadAllText(BridgeMod.LogPath);Call(h,"LeaderboardRowPostfix","label","42","cities",true,false);
            string trace=System.IO.File.ReadAllText(BridgeMod.LogPath).Substring(before.Length);
            Check(trace.Contains("originRequest=74")&&trace.Contains("viewRequest=row-open")&&trace.Contains("stage=row"),
                "actual row projection catch identifies the captured native invocation");
            Check(Get(h,"_leaderboardCapture")==null,"unreadable projection cannot remain a complete capture");
        });
        Scenario("unbound native callback trace",delegate{
            var h=Host();var ui=UIManager.Instance;ui.ShowMode="delayed";Set(h,"_activeRequestId",75L);View(h,"open","new-unpopulated-open");
            string before=System.IO.File.ReadAllText(BridgeMod.LogPath);EndHook(h,ui.Target,new Exception("SECRET_UNBOUND"),null);
            string trace=System.IO.File.ReadAllText(BridgeMod.LogPath).Substring(before.Length);
            Check(trace.Contains("originRequest=?")&&trace.Contains("viewRequest=?")&&!trace.Contains("viewRequest=new-unpopulated-open"),
                "a callback without a prefix token cannot borrow the current view's authorization or origin");
        });
        Console.WriteLine("passed="+passed+" failed="+failed);return failed==0 ? 0 : 1;
    }
}
