using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public sealed class PublicDisplayObject { public bool activeInHierarchy = true; }
public sealed class PublicDisplayText
{
    public PublicDisplayObject gameObject = new PublicDisplayObject(); public string Text;
    public string ParsedOverride; public bool ThrowText; public int ParsedCalls, TextReads;
    public string text { get { TextReads++; if (ThrowText) throw new Exception("text read failed"); return Text; } }
    public string GetParsedText() { ParsedCalls++; return ParsedOverride ?? Text; }
}
public sealed class DiplomacyNotification
{
    public PublicDisplayObject gameObject = new PublicDisplayObject(); public string state = "Showing";
    public PublicDisplayText header = new PublicDisplayText { Text = "Diplomacy" };
    public PublicDisplayText description = new PublicDisplayText { Text = "<link=999>Unknown tribe</link> made peace" };
    public object player { get { throw new Exception("hidden identity read"); } }
}
public sealed class GameStatsScreen { public PublicDisplayObject gameObject = new PublicDisplayObject(); }
public sealed class UIManager
{
    public static UIManager Instance { get; set; }
    public string CurrentScreen = "StatsScreen";
    public GameStatsScreen Screen = new GameStatsScreen(); public int Pops;
    public object GetCurrentScreen() { return Screen; }
    public void PopCurrentScreen() { }
    public void OnBack() { Pops++; Screen.gameObject.activeInHierarchy = false; }
}
public sealed class HotSeatOverlay
{
    public static bool IsActive { get; set; }
    public static HotSeatOverlay instance = new HotSeatOverlay();
    public PublicDisplayObject gameObject = new PublicDisplayObject { activeInHierarchy = false };
}
internal static class PublicDisplayTests
{
    private sealed class Player
    {
        public byte Id;
        public object score { get { throw new Exception("raw opponent score"); } }
        public object tribe { get { throw new Exception("hidden tribe"); } }
    }
    private sealed class Settings { public string RulesGameMode = "Domination"; }
    private sealed class Stack { public int Count = 1; }
    private sealed class State
    {
        public int CurrentTurn = 9;
        public int CurrentPlayerIndex { get; set; }
        public Settings Settings = new Settings(); public Stack CommandStack = new Stack();
        public bool Concealed; public int PrivateReads;
        public Player[] Players { get { PrivateReads++; if (Concealed) throw new Exception("curtain roster read"); return new[] { new Player { Id = 2 }, new Player { Id = 3 } }; } }
        public object Map { get { throw new Exception("map not needed"); } }
    }
    private sealed class ActionManager { public object GameState; public bool IsSimulating { get { return false; } } }
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
    private static readonly BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static int passed;
    private static object Call(BridgeMod h, string name, params object[] args)
    { return typeof(BridgeMod).GetMethod(name, F).Invoke(h, args); }
    private static void Set(BridgeMod h, string name, object value) { typeof(BridgeMod).GetField(name, F).SetValue(h, value); }
    private static Dictionary<string, object> Parse(object json) { return Json.Deserialize<Dictionary<string, object>>((string)json); }
    private static Dictionary<string, object>[] Rows(Dictionary<string, object> value, string name)
    { return Json.Deserialize<Dictionary<string, object>[]>(Json.Serialize(value[name])); }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static void Row(BridgeMod h, object screen, int? score, int? count, bool alive)
    {
        if (score.HasValue) Call(h, "LeaderboardScorePostfix", screen, score.Value);
        if (count.HasValue) Call(h, "LeaderboardDescriptionPostfix", screen, count.Value);
        Call(h, "LeaderboardRowPostfix", "<link=317>Unknown tribe</link>", "1 234", "<b>2 cities</b>", alive, false);
    }
    public static int Main()
    {
        try { return Run(); }
        catch (Exception error) { while (error.InnerException != null) error = error.InnerException; Console.WriteLine(error); return 1; }
    }
    private static int Run()
    {
        var h = new BridgeMod(); var gs = new State(); var am = new ActionManager { GameState = gs };
        var client = new Client { GameState = gs, ActionManager = am };
        Manager.instance = new Manager { client = client }; Manager.GameState = gs; Manager.LocalPlayer = new Player { Id = 2 };
        Set(h, "_gameManagerType", typeof(Manager)); Set(h, "_gameStateProp", typeof(Manager).GetProperty("GameState"));
        Set(h, "_playersProp", typeof(State).GetProperty("Players")); Set(h, "_currentPlayerIndexProp", typeof(State).GetProperty("CurrentPlayerIndex"));
        Set(h, "_displayHost", h); Set(h, "_leaderboardHooksInstalled", true); Set(h, "_notificationHookInstalled", true);
        UIManager.Instance = new UIManager(); var screen = UIManager.Instance.Screen;
        Check(Parse(Call(h, "LeaderboardPublic", "read"))["ok"].Equals(false), "read before native population is unavailable");
        Call(h, "BeginLeaderboardCapture", screen); Row(h, screen, 1234, 2, true); Row(h, screen, null, null, true);
        Call(h, "CompleteLeaderboardCapture", screen, false);
        var snapshot = Parse(Call(h, "LeaderboardPublic", "read")); var rows = Rows(snapshot, "rows");
        Check(rows.Length == 2 && (string)snapshot["observedTurn"] == "9", "completed native population is a dated readable snapshot");
        Check((int)rows[0]["score"] == 1234 && (string)rows[0]["scoreText"] == "1 234", "cached native scalar preserves score without locale parsing");
        Check((string)rows[0]["label"] == "Unknown tribe" && !rows[0].ContainsKey("playerId"), "anonymous rendered label never maps to native identity");
        Check((int)rows[0]["holdingCount"] == 2 && (string)rows[0]["holdingKind"] == "cities", "alive ordinary-mode count is provided");
        Check(rows[1]["score"] == null && rows[1]["holdingCount"] == null, "missing row callbacks cannot borrow prior row scalars");
        Check((int)rows[1]["position"] == 2 && UIManager.Instance.Pops == 0, "reading preserves native order without navigating");
        gs.CurrentTurn++;
        Check((string)Parse(Call(h, "LeaderboardPublic", "read"))["status"] == "historical", "old statistics stay historical after a turn change");
        gs.Settings.RulesGameMode = "Might";
        Call(h, "BeginLeaderboardCapture", screen); Row(h, screen, 42, 7, true); Call(h, "CompleteLeaderboardCapture", screen, false);
        Check(Rows(Parse(Call(h, "LeaderboardPublic", "read")), "rows")[0]["holdingCount"] == null,
              "Might count argument is not exposed when its description branch ignores it");
        gs.Settings.RulesGameMode = "Domination";
        Call(h, "BeginLeaderboardCapture", screen); Row(h, screen, 42, 7, false); Call(h, "CompleteLeaderboardCapture", screen, false);
        Check(Rows(Parse(Call(h, "LeaderboardPublic", "read")), "rows")[0]["holdingCount"] == null, "dead description does not disclose unused count");
        Call(h, "BeginLeaderboardCapture", screen); Row(h, screen, 1, 1, true); Call(h, "CompleteLeaderboardCapture", screen, true);
        Check(Parse(Call(h, "LeaderboardPublic", "read"))["ok"].Equals(false), "failed refresh invalidates previous snapshot");
        Check(Parse(Call(h, "LeaderboardPublic", "close"))["ok"].Equals(false) && UIManager.Instance.Pops == 0, "interface does not close an unowned screen");
        Set(h, "_leaderboardOwnedScreen", screen);
        Check((string)Parse(Call(h, "LeaderboardPublic", "close"))["status"] == "closed" && UIManager.Instance.Pops == 1, "owned screen uses native pop once");

        var notice = new DiplomacyNotification(); Call(h, "ObserveDiplomacyNotification", notice);
        var history = Parse(Call(h, "NotificationsPublic", 0L, null, 100)); var events = Rows(history, "events"); string stream = (string)history["streamId"];
        Check(Parse(Call(h, "NotificationsPublic", 0L, "", 100))["ok"].Equals(true), "missing JSON stream becomes empty text and starts a fresh cursor");
        Check(events.Length == 1 && (string)events[0]["message"] == "Unknown tribe made peace", "actual shown diplomacy text is retained without hidden link identity");
        Check((int)events[0]["observedTurn"] == gs.CurrentTurn && !events[0].ContainsKey("player") && !events[0].ContainsKey("currentRelation"), "notice is dated historical text without hidden parties or current-relation inference");
        Call(h, "ObserveDiplomacyNotification", notice);
        Check(Rows(Parse(Call(h, "NotificationsPublic", 0L, stream, 100)), "events").Length == 1, "same notification duplicate within one frame is suppressed");
        notice.gameObject.activeInHierarchy = false; Set(h, "_tick", 1L); Call(h, "ObserveDiplomacyNotification", notice);
        notice.gameObject.activeInHierarchy = true; notice.state = "Hidden"; Call(h, "ObserveDiplomacyNotification", notice);
        Check(Rows(Parse(Call(h, "NotificationsPublic", 0L, stream, 100)), "events").Length == 1, "inactive or hidden notification cannot enter history");
        notice.state = "Showing";
        Set(h, "_fixedControlledSeat", (byte?)2); client.clientType = "PassAndPlay"; HotSeatOverlay.IsActive = true; gs.Concealed = true;
        int before = gs.PrivateReads; Call(h, "BeginLeaderboardCapture", screen); Call(h, "ObserveDiplomacyNotification", notice);
        Check(Parse(Call(h, "NotificationsPublic", 0L, stream, 100))["ok"].Equals(false) && gs.PrivateReads == before, "hotseat curtain rejects both domains before private roster sampling");
        gs.Concealed = false; HotSeatOverlay.IsActive = false; Set(h, "_fixedControlledSeat", (byte?)null); client.clientType = "SinglePlayer";
        Check(Rows(Parse(Call(h, "NotificationsPublic", 1L, stream, 100)), "events").Length == 0, "cursor reads only newer observed notices");
        for (int i = 2; i <= 132; i++) { Set(h, "_tick", (long)i); Call(h, "ObserveDiplomacyNotification", notice); }
        history = Parse(Call(h, "NotificationsPublic", 0L, stream, 5));
        Check((bool)history["historyGap"] && (bool)history["hasMore"] && Rows(history, "events").Length == 5, "bounded journal reports eviction gap and pagination");
        client.CurrentGameId = Guid.NewGuid(); gs = new State(); Manager.GameState = gs; client.GameState = gs; am.GameState = gs;
        history = Parse(Call(h, "NotificationsPublic", 0L, null, 100));
        Check((string)history["streamId"] != stream && Rows(history, "events").Length == 0, "new native game resets notification scope and stream");
        Check(Parse(Call(h, "NotificationsPublic", 0L, stream, 100))["ok"].Equals(false), "old stream cannot read new-game cursor");
        var fresh = new DiplomacyNotification();
        fresh.header.Text = "<link=123>Unknown tribe</link>"; fresh.header.ParsedOverride = "Previous header";
        fresh.description.Text = "Newly shown treaty"; fresh.description.ParsedOverride = "";
        Set(h, "_tick", 200L); Call(h, "ObserveDiplomacyNotification", fresh);
        history = Parse(Call(h, "NotificationsPublic", 0L, null, 100)); events = Rows(history, "events");
        Check(events.Length == 1 && (string)events[0]["message"] == "Newly shown treaty",
            "shown notification reads current label instead of empty parsed mesh cache");
        Check((string)events[0]["title"] == "Unknown tribe" && !new JavaScriptSerializer().Serialize(events[0]).Contains("Previous header"),
            "current label replaces stale parsed header and strips private rich-text links");
        Check(fresh.header.ParsedCalls == 0 && fresh.description.ParsedCalls == 0
            && fresh.header.TextReads == 1 && fresh.description.TextReads == 1,
            "shown labels use one native text read without parsing or UI layout mutation");
        var blank = new DiplomacyNotification(); blank.header.Text = " "; blank.description.Text = "";
        Set(h, "_tick", 201L); Call(h, "ObserveDiplomacyNotification", blank);
        history = Parse(Call(h, "NotificationsPublic", 0L, null, 100));
        Check(Rows(history, "events").Length == 1 && Convert.ToInt64(history["nextEventId"]) == 1,
            "fully empty shown labels never become a fabricated blank diplomacy event");
        Check(!(bool)history["captureComplete"] && Convert.ToInt64(history["unreadableNoticeCount"]) == 1,
            "lost shown text is reported separately from an empty complete notice history");
        Call(h, "ObserveDiplomacyNotification", blank);
        Check(Convert.ToInt64(Parse(Call(h, "NotificationsPublic", 0L, null, 100))["unreadableNoticeCount"]) == 1,
            "same-frame duplicate unreadable display is counted once");
        blank.gameObject.activeInHierarchy = false; Set(h, "_tick", 202L);
        Call(h, "ObserveDiplomacyNotification", blank);
        Check(Convert.ToInt64(Parse(Call(h, "NotificationsPublic", 0L, null, 100))["unreadableNoticeCount"]) == 1,
            "hidden notification does not disclose or increment missing-text metadata");
        var broken = new DiplomacyNotification(); broken.header.ThrowText = broken.description.ThrowText = true;
        broken.header.ParsedOverride = "Old hidden title"; broken.description.ParsedOverride = "Old hidden body";
        Set(h, "_tick", 203L); Call(h, "ObserveDiplomacyNotification", broken);
        history = Parse(Call(h, "NotificationsPublic", 0L, null, 100));
        Check(Rows(history, "events").Length == 1 && Convert.ToInt64(history["unreadableNoticeCount"]) == 2,
            "failed current text does not fall back to an unrelated old parsed message");
        client.CurrentGameId = Guid.NewGuid(); gs = new State(); Manager.GameState = gs; client.GameState = gs; am.GameState = gs;
        history = Parse(Call(h, "NotificationsPublic", 0L, null, 100));
        Check((bool)history["captureComplete"] && Convert.ToInt64(history["unreadableNoticeCount"]) == 0,
            "new controlled game clears missing-text scope alongside the public history");
        Console.WriteLine("PASS total " + passed); return 0;
    }
}
