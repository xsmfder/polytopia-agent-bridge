// Drive production cleanup and public close endpoints with pooled native-shaped UI.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public sealed class StatisticsObject { public bool activeInHierarchy = true; }
public sealed class GameStatsScreen { public StatisticsObject gameObject = new StatisticsObject(); }
public sealed class UIManager
{
    public static UIManager Instance { get; set; }
    public GameStatsScreen Screen = new GameStatsScreen(); public int Pops;
    public object GetCurrentScreen() { return Screen; }
    public void PopCurrentScreen() { }
    public void OnBack() { Pops++; Screen.gameObject.activeInHierarchy = false; }
}
public sealed class PlayerInfoPopup
{
    public object player; public int Hides;
    public void Hide() { Hides++; }
}
public static class PopupManager
{
    public static object Current;
    public static object GetCurrentPopup() { return Current; }
}
internal static class LifecycleRuntimeTests
{
    private sealed class Player
    { public byte Id; public List<byte> knownPlayers = new List<byte> { 1, 2, 3 }; }
    private sealed class State
    {
        public int CurrentPlayerIndex { get; set; }
        public Player[] Players { get; set; }
    }
    private sealed class ActionManager
    {
        public object GameState; public bool Simulating;
        public bool IsSimulating { get { return Simulating; } }
    }
    private sealed class Client
    {
        public string clientType = "SinglePlayer"; public object GameState, ActionManager;
        public Guid CurrentGameId = Guid.NewGuid();
        public bool IsReady { get { return true; } } public bool IsReplay { get { return false; } }
    }
    private sealed class Manager
    {
        public static Manager instance { get; set; } public static object GameState { get; set; }
        public static Player LocalPlayer { get; set; } public Client client;
    }
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly JavaScriptSerializer J = new JavaScriptSerializer();
    private static int passed;
    private static object Get(object h, string name) { return h.GetType().GetField(name, F).GetValue(h); }
    private static void Set(object h, string name, object value) { h.GetType().GetField(name, F).SetValue(h, value); }
    private static object Call(BridgeMod h, string name, params object[] args) { return typeof(BridgeMod).GetMethod(name, F).Invoke(h, args); }
    private static Dictionary<string, object> Parse(object value) { return J.Deserialize<Dictionary<string, object>>((string)value); }
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    private static BridgeMod.CombatFrame Frame(string episode, string me = "2", bool complete = true)
    { return new BridgeMod.CombatFrame { Episode = episode, Me = me, Turn = "3", Acting = me, Phase = "Playing", Complete = complete }; }
    private static int Count(BridgeMod h, string name) { return ((ICollection)Get(h, name)).Count; }
    private static void Scope(BridgeMod h, string episode, string me = "2") { Call(h, "EnsurePlayerRuntimeScope", episode, me); }
    private static object SeedOutcome(BridgeMod h, string episode, byte me)
    {
        object state = new object();
        Set(h, "_publicResultState", state); Set(h, "_publicResultEpisode", episode);
        Set(h, "_publicResultWinner", (byte?)me); Set(h, "_publicResultWon", (bool?)true);
        Set(h, "_publicResultControlled", (byte?)me);
        return state;
    }
    private static void Alive(BridgeMod h, byte player, string episode)
    {
        var row = Activator.CreateInstance(typeof(BridgeMod).GetNestedType("PublicAliveObservation", F), true);
        Set(row, "Episode", episode); Set(row, "Alive", true);
        ((IDictionary)Get(h, "_publicAlive"))[player] = row;
    }
    private static void Seed(BridgeMod h, string episode)
    {
        var proof = new BridgeMod.PublicDiplomacyObservation { Episode = episode, NativeAction = new object(), NativeCommand = new object() };
        ((IList)Get(h, "_publicDiplomacyActive")).Add(proof);
        ((IDictionary)Get(h, "_publicDiplomacyProof"))[1L] = proof;
        ((HashSet<string>)Get(h, "_publicPeaceReceipts")).Add(episode + ":2:3");
        ((IDictionary)Get(h, "_publicResponseExpectations"))[1L] = "expected";
        ((IDictionary)Get(h, "_peaceResponseDisplays"))[1L] = new BridgeMod.PeaceResponseDisplay { Popup = PopupManager.Current, Attempted = true };
        var capture = Activator.CreateInstance(typeof(BridgeMod).GetNestedType("LeaderboardCapture", F), true);
        Set(h, "_leaderboardCapture", capture); Set(h, "_leaderboardSnapshot", capture);
        Set(h, "_leaderboardOwnedScreen", UIManager.Instance.Screen);
        Set(h, "_tribeInfoOwnedPopup", PopupManager.Current); Set(h, "_tribeInfoStamp", "stamp"); Set(h, "_tribeInfoOpenedTick", 7L);
        Set(h, "_notificationScope", episode + ":2"); Set(h, "_notificationId", 1L);
        ((IList)Get(h, "_publicNotifications")).Add(new KeyValuePair<long, string>(1, "{}"));
        Set(h, "_lastNotification", new object()); Set(h, "_lastNotificationTick", 7L);
        Set(h, "_requestSampleState", new object()); Set(h, "_requestPipelineState", new object());
        Set(h, "_requestSampleGeneration", 10L); Set(h, "_requestPipelineGeneration", 10L);
        ((IDictionary)Get(h, "_combatTickets"))[9L] = new BridgeMod.CombatTicket { Id = 9, Stream = (string)Get(h, "_combatStream"), Before = Frame(episode), Accepted = true };
    }
    public static int Main()
    { try { Run(); Console.WriteLine("ALL PASS " + passed); return 0; } catch (Exception e) { Console.WriteLine(e); return 1; } }
    private static void Run()
    {
        var h = new BridgeMod(); UIManager.Instance = new UIManager();
        var popup = new PlayerInfoPopup(); PopupManager.Current = popup;
        Call(h, "CRObserve", Frame("first"), null); Seed(h, "first");
        object originalResultState = SeedOutcome(h, "first", 2); Alive(h, 2, "first");
        ((IDictionary)Get(h, "_nativeFinalResults"))["archived"] = "frozen-final"; Set(h, "_nativeFinalLatestEpisode", "archived");
        string stream = (string)Get(h, "_combatStream"), notificationStream = (string)Get(h, "_notificationStream");
        Scope(h, "first");
        Check(Count(h, "_publicDiplomacyProof") == 1 && Get(h, "_tribeInfoOwnedPopup") == popup, "same scope preserves live proof and owned popup");
        Check(Get(h, "_publicResultState") == originalResultState && Count(h, "_publicAlive") == 1, "same scope preserves native final and alive evidence");
        foreach (var me in new[] { "0", "255", "bad", "" }) Scope(h, "other", me);
        Scope(h, null); Scope(h, "");
        Check(Get(h, "_publicResultState") == originalResultState, "invalid scope cannot release active terminal reference");
        Check((string)Get(h, "_playerRuntimeEpisode") == "first" && Count(h, "_publicDiplomacyProof") == 1, "invalid scope cannot erase valid proof");
        Call(h, "CRObserve", null, null); Call(h, "CRObserve", Frame("other", "2", false), null);
        Check(Count(h, "_combatTickets") == 1 && Count(h, "_publicDiplomacyProof") == 1, "null or incomplete frame preserves unresolved context");
        long generation = (long)Get(h, "_nativeSampleGeneration"); Scope(h, "second");
        Check(Get(h, "_publicResultState") == null && Get(h, "_publicResultEpisode") == null,
            "new episode releases the old native terminal state reference");
        Check(Get(h, "_publicResultWinner") == null && Get(h, "_publicResultWon") == null
            && Get(h, "_publicResultControlled") == null, "old terminal metadata is released with its reference");
        Check(Count(h, "_publicAlive") == 0, "new episode prunes stale alive observations");
        foreach (var name in new[] { "_publicDiplomacyActive", "_publicDiplomacyProof", "_publicResponseExpectations", "_peaceResponseDisplays" })
            Check(Count(h, name) == 0, "new episode releases " + name);
        Check(((HashSet<string>)Get(h, "_publicPeaceReceipts")).Count == 0, "new episode removes old incoming peace evidence");
        Check(Get(h, "_leaderboardCapture") == null && Get(h, "_leaderboardSnapshot") == null && Get(h, "_leaderboardOwnedScreen") == null, "new episode releases statistics capture snapshot and ownership");
        Check(Get(h, "_tribeInfoOwnedPopup") == null && Get(h, "_tribeInfoStamp") == null && (long)Get(h, "_tribeInfoOpenedTick") == 0, "new episode releases diplomacy view ownership");
        Check(Count(h, "_publicNotifications") == 0 && (long)Get(h, "_notificationId") == 0 && Get(h, "_lastNotification") == null && Get(h, "_notificationScope") == null, "new episode clears notification history and dedup reference");
        Check((string)Get(h, "_notificationStream") != notificationStream, "new scope invalidates old notification cursor stream");
        Check(Get(h, "_requestSampleState") == null && Get(h, "_requestPipelineState") == null && (long)Get(h, "_requestSampleGeneration") == -1 && (long)Get(h, "_requestPipelineGeneration") == -1 && (long)Get(h, "_nativeSampleGeneration") > generation, "new scope releases and invalidates per-request caches");
        Check(UIManager.Instance.Pops == 0 && popup.Hides == 0, "cleanup never invokes native UI callbacks");
        Check(Count(h, "_combatTickets") == 1 && (string)Get(h, "_combatStream") == stream, "metadata change alone leaves old combat receipt until complete new frame");
        Check((string)((IDictionary)Get(h, "_nativeFinalResults"))["archived"] == "frozen-final" && (string)Get(h, "_nativeFinalLatestEpisode") == "archived", "frozen final archive survives scope cleanup");
        Call(h, "CRObserve", Frame("second"), null);
        Check(Count(h, "_combatTickets") == 0 && (string)Get(h, "_combatStream") != stream, "complete new frame atomically resets old combat tickets and stream");
        Seed(h, "second"); Scope(h, "second", "3");
        Check(Count(h, "_publicDiplomacyProof") == 0 && Get(h, "_leaderboardOwnedScreen") == null, "same episode different controlled seat clears transient private data");

        // Exercise the actual public close paths; UI instances are reused across games.
        h = new BridgeMod(); var own = new Player { Id = 2 }; var other = new Player { Id = 3 };
        var gs = new State { Players = new[] { own, other } }; var am = new ActionManager { GameState = gs };
        var client = new Client { GameState = gs, ActionManager = am }; Manager.instance = new Manager { client = client };
        Manager.GameState = gs; Manager.LocalPlayer = own; popup.player = other;
        Set(h, "_gameManagerType", typeof(Manager)); Set(h, "_gameStateProp", typeof(Manager).GetProperty("GameState"));
        Set(h, "_playersProp", typeof(State).GetProperty("Players")); Set(h, "_currentPlayerIndexProp", typeof(State).GetProperty("CurrentPlayerIndex"));
        Check((bool)Call(h, "PrivateSamplingAllowed", gs), "visible own game establishes scope before frame capture");
        string episode = (string)Get(h, "_playerRuntimeEpisode"); Seed(h, episode);
        gs = new State { Players = new[] { own, other } }; Manager.GameState = gs; client.GameState = gs; am.GameState = gs;
        Check(!(bool)Parse(Call(h, "LeaderboardPublic", "close"))["ok"] && UIManager.Instance.Pops == 0, "reused statistics instance cannot carry close authorization into new game");
        Check(!(bool)Parse(Call(h, "TribeInfoPublic", "3", "close"))["ok"] && popup.Hides == 0, "reused tribe popup cannot carry close authorization into new game");
        Set(h, "_leaderboardOwnedScreen", UIManager.Instance.Screen);
        Check((bool)Parse(Call(h, "LeaderboardPublic", "close"))["ok"] && UIManager.Instance.Pops == 1, "current scope retains legitimate statistics close");
        Set(h, "_tribeInfoOwnedPopup", popup);
        Check((bool)Parse(Call(h, "TribeInfoPublic", "3", "close"))["ok"] && popup.Hides == 1, "current scope retains legitimate diplomacy popup close");

        // Human turns and fake/replayed states must not rotate the configured player's scope.
        Set(h, "_fixedControlledSeat", (byte?)2); Call(h, "PrivateSamplingAllowed", gs);
        episode = (string)Get(h, "_playerRuntimeEpisode"); Seed(h, episode);
        Manager.LocalPlayer = new Player { Id = 1 };
        Check(!(bool)Call(h, "PrivateSamplingAllowed", gs) && Count(h, "_combatTickets") == 1 && Count(h, "_publicDiplomacyProof") == 1, "human turn pauses without clearing controlled player receipts");
        Manager.LocalPlayer = own; am.Simulating = true;
        Check(!(bool)Call(h, "PrivateSamplingAllowed", gs) && Count(h, "_publicDiplomacyProof") == 1, "simulation suppression preserves current proof without creating a new scope");
        am.Simulating = false;
        Check(!(bool)Call(h, "PrivateSamplingAllowed", new State()) && Count(h, "_peaceResponseDisplays") == 1, "fake state cannot trigger cleanup or reset popup attempt guards");
        Check(!(bool)Call(h, "PrivateSamplingAllowed", (object)null) && Count(h, "_publicDiplomacyProof") == 1, "temporary no state preserves valid pending receipts");
        Check((bool)Call(h, "PrivateSamplingAllowed", gs) && Count(h, "_publicDiplomacyProof") == 1, "return to same controlled game retains existing proof");

        // A native callback may have established evidence for the next scope already.
        h = new BridgeMod(); Scope(h, "prior");
        object currentResultState = SeedOutcome(h, "current", 2);
        Alive(h, 1, "prior"); Alive(h, 2, "current");
        Scope(h, "current");
        Check(Get(h, "_publicResultState") == currentResultState && (bool)Get(h, "_publicResultWon"),
            "scope cleanup preserves an already captured current native final");
        Check(Count(h, "_publicAlive") == 1 && ((IDictionary)Get(h, "_publicAlive")).Contains((byte)2),
            "pruning preserves current-episode alive evidence without keeping old rows");
        Scope(h, "current", "3");
        Check(Get(h, "_publicResultState") == null && Count(h, "_publicAlive") == 0,
            "controlled-seat change releases terminal and contact evidence from the other seat");
        SeedOutcome(h, "stale-host", 3); Alive(h, 3, "stale-host");
        h = new BridgeMod(); Scope(h, "fresh-host");
        Check(Get(h, "_publicResultState") == null && Count(h, "_publicAlive") == 0,
            "first valid scope also prunes stale static evidence from a previous host");
    }
}
