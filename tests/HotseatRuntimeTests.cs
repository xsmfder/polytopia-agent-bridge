// Managed fixtures exercise the production reader/entrypoint and native-shaped one-shot
// callback. These tests are NOT evidence of a live HotSeatOverlay interaction.
using System;
using System.Reflection;
using PolytopiaAgent;

public class HotSeatOverlay
{
    protected static HotSeatOverlay instance;
    public static bool IsActive { get; set; }
    public static void Install(HotSeatOverlay value) { instance = value; }
    public object player, startButton;
    public Action continueCallback;
    public HotseatRuntimeTests.GameObject gameObject = new HotseatRuntimeTests.GameObject();
    public int Invocations;
    public void ContinueClicked<T>() { throw new Exception("unbound generic cannot replace native continue"); }
    public void ContinueClicked()
    {
        Invocations++;
        Action next = continueCallback; continueCallback = null;
        if (next != null) next();
    }
}
public static class PopupManager
{
    public static bool Showing;
    public static bool get_PopupShowing() { return Showing; }
}

public static class HotseatRuntimeTests
{
    public sealed class GameObject { public bool activeInHierarchy = true; }
    private sealed class Player { public byte Id; }
    private sealed class Button
    {
        internal GameObject Object = new GameObject();
        internal bool Enabled = true;
        public GameObject get_gameObject() { return Object; }
        public bool get_ButtonEnabled() { return Enabled; }
    }
    private sealed class UnreadableButton
    { public GameObject gameObject = new GameObject(); }
    private sealed class State
    {
        public int Turn = 20, MapReads;
        public byte Acting = 2;
        public bool Queued;
        public string Phase = "Started";
        public object Map { get { MapReads++; throw new Exception("concealed map must not be read"); } }
        public int CurrentTurn { get { return Turn; } }
        public byte CurrentPlayer { get { return Acting; } }
        public string CurrentState { get { return Phase; } }
        public bool HasQueuedCommands() { return Queued; }
    }
    private sealed class ActionManager
    {
        public object GameState;
        public bool IsRecap { get; set; }
        public bool IsProcessing { get; set; }
        public bool IsPausedOrPausing { get; set; }
        public bool IsSimulating { get; set; }
    }
    private sealed class Client
    {
        public string clientType = "PassAndPlay";
        public object GameState, ActionManager;
        public Guid CurrentGameId = Guid.NewGuid();
        public bool IsReady { get; set; }
        public bool IsReplay { get; set; }
    }
    private sealed class Manager
    {
        public static Manager instance { get; set; }
        public static object GameState { get; set; }
        public static Player LocalPlayer { get; set; }
        public Client client;
    }
    private static readonly BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static int passed;
    private static object Call(BridgeMod host, string name, params object[] args)
    { return typeof(BridgeMod).GetMethod(name, F).Invoke(host, args); }
    private static void Set(BridgeMod host, string name, object value)
    { typeof(BridgeMod).GetField(name, F).SetValue(host, value); }
    private static BridgeMod.HandoffView View(BridgeMod host)
    { return (BridgeMod.HandoffView)Call(host, "ReadHandoff"); }
    private static string Start(BridgeMod host, string player, string token)
    { return (string)Call(host, "HandoffStart", player, token); }
    private static void Check(bool condition, string description)
    { if (!condition) throw new Exception("FAIL " + description); passed++; Console.WriteLine("PASS " + description); }
    public static int Main()
    {
        try { return Run(); }
        catch (Exception error) { while (error.InnerException != null) error = error.InnerException; Console.WriteLine(error); return 1; }
    }
    private static int Run()
    {
        var host = new BridgeMod(); var state = new State();
        var am = new ActionManager { GameState = state };
        var client = new Client { GameState = state, ActionManager = am, IsReady = true };
        Manager.instance = new Manager { client = client }; Manager.GameState = state;
        Manager.LocalPlayer = new Player { Id = 1 };
        Set(host, "_gameManagerType", typeof(Manager)); Set(host, "_fixedControlledSeat", (byte?)2);
        Set(host, "_gameStateProp", typeof(Manager).GetProperty("GameState"));
        Set(host, "_currentTurnProp", typeof(State).GetProperty("CurrentTurn"));
        Set(host, "_currentStateProp", typeof(State).GetProperty("CurrentState"));
        Set(host, "_mapProp", typeof(State).GetProperty("Map"));
        var button = new Button(); var overlay = new HotSeatOverlay
        { player = new Player { Id = 2 }, startButton = button, continueCallback = delegate { } };
        HotSeatOverlay.Install(overlay); HotSeatOverlay.IsActive = true;
        BridgeMod.HandoffView view = View(host);
        Check(view.Phase == "handoff_overlay" && view.Target == 2 && view.CanStart && !view.Ready,
            "reconnecting at an already visible curtain discovers its displayed target without an EndTurn ticket");
        Check(view.NeedsDecision == null && state.MapReads == 0,
            "curtain metadata does not inspect the map, triggers or another human's pending decisions");
        string token = view.Id;
        Check(!view.Json(true, false, false).Contains("\"outcome\""), "passive handoff observation does not claim write outcome");

        Check(!String.IsNullOrEmpty(token) && View(host).Id == token,
            "handoff token is stable across repeated read-only polling");
        Check(Start(host, "1", token).Contains("UNCONTROLLED_SEAT") && overlay.Invocations == 0,
            "an explicit request for the human seat cannot start its curtain");
        Check(Start(host, "2", "stale").Contains("STALE_HANDOFF") && overlay.Invocations == 0,
            "a stale handoff token cannot invoke a new callback");
        Check(Start(host, "2", "").Contains("STALE_HANDOFF"), "start requires the observed handoff token");
        foreach (var player in new[] { "0", "255", "-1", "true", " 2" })
            Check(Start(host, player, token).Contains("INVALID_PLAYER_ID"), "invalid or sentinel player rejected: " + player);
        string beforeInvoke = Start(host, "2", "stale");
        Check(beforeInvoke.Contains("\"dispatched\":false") && beforeInvoke.Contains("\"outcome\":\"not_dispatched\"")
            && beforeInvoke.Contains("\"callbackAttempted\":false") && overlay.Invocations == 0,
            "actual stale handoff write carries explicit no-callback proof");
        button.Enabled = false;
        Check(!View(host).CanStart && Start(host, "2", token).Contains("HANDOFF_NOT_READY") && overlay.Invocations == 0,
            "disabled native button is not bypassed by calling ContinueClicked");
        button.Enabled = true; button.Object.activeInHierarchy = false;
        Check(View(host).ButtonActive == false && !View(host).CanStart,
            "an enabled button on an inactive GameObject cannot start");
        button.Object.activeInHierarchy = true; overlay.startButton = new UnreadableButton();
        Check(View(host).ButtonEnabled == null && !View(host).CanStart,
            "unreadable button state remains unknown and does not enable the callback");
        overlay.startButton = button; overlay.continueCallback = null;
        Check(View(host).CallbackPresent == false && !View(host).CanStart,
            "missing native continue callback cannot report an executable handoff");
        overlay.continueCallback = delegate { }; overlay.gameObject.activeInHierarchy = false;
        Check(!View(host).CanStart, "inactive curtain GameObject cannot start even with stale IsActive true");
        overlay.gameObject.activeInHierarchy = true;
        Check(!(bool)Call(host, "PrivateSamplingAllowed", state) && state.MapReads == 0,
            "normal private reader is withheld at the dark curtain");
        am.IsSimulating = true;
        Check(!View(host).CanStart && Start(host, "2", token).Contains("HANDOFF_NOT_READY") && overlay.Invocations == 0,
            "simulation cannot invoke an apparently active native handoff callback");
        am.IsSimulating = false; am.GameState = new State();
        Check(!View(host).CanStart, "foreign action-manager state cannot start the main client's curtain");
        am.GameState = state;
        string submitted = Start(host, "2", token);
        Check(submitted.Contains("\"accepted\":true") && submitted.Contains("\"callbackSubmitted\":true")
            && submitted.Contains("\"ready\":false") && overlay.Invocations == 1 && overlay.continueCallback == null,
            "native-shaped ContinueClicked consumes its delegate once and acknowledgement is not map completion");
        Check(Start(host, "2", token).Contains("\"duplicate\":true") && overlay.Invocations == 1,
            "duplicate start on the same handoff cannot execute the callback twice");
        HotSeatOverlay.IsActive = false; Manager.LocalPlayer.Id = 2;
        Check(View(host).Phase == "transition" && !View(host).Ready && state.MapReads == 0,
            "fade-out waits for native GameObject deactivation after IsActive already became false");
        Check(!(bool)Call(host, "PrivateSamplingAllowed", state),
            "direct private observation is also blocked during the same fade-out");
        overlay.gameObject.activeInHierarchy = false; am.IsRecap = true;
        Check(View(host).Phase == "recap" && !View(host).Ready && state.MapReads == 0,
            "visible native recap is reported distinctly without skipping it or scanning the map");
        PopupManager.Showing = true;
        Check(View(host).Phase == "needs_decision" && View(host).NeedsDecision == true && !View(host).Ready
            && View(host).Json(true, false, false).Contains("\"recapping\":true") && state.MapReads == 0,
            "an owned visible recap paused by a popup surfaces that decision with recap evidence instead of waiting forever");
        am.IsRecap = false; PopupManager.Showing = true;
        Check(View(host).Phase == "needs_decision" && View(host).NeedsDecision == true && !View(host).Ready,
            "a real popup is surfaced for decision rather than dismissed by handoff automation");
        PopupManager.Showing = false; am.IsProcessing = true;
        Check(View(host).Phase == "settling" && !View(host).Ready, "native processing prevents map-ready confirmation");
        am.IsProcessing = false; state.Queued = true;
        Check(View(host).Phase == "settling", "queued native commands prevent map-ready confirmation");
        state.Queued = false; am.IsPausedOrPausing = true;
        Check(View(host).Phase == "settling", "paused or pausing native pipeline prevents map-ready confirmation");
        am.IsPausedOrPausing = false; client.ActionManager = new object();
        Check(!View(host).Ready, "missing action-manager state cannot default to ready");
        client.ActionManager = am; state.Acting = 1;
        Check(View(host).Phase == "transition" && !View(host).Ready,
            "old local identity cannot mark the next player's actual turn ready");
        state.Acting = 2;
        Check(View(host).Phase == "map_ready" && View(host).Ready && View(host).Target == 2 && state.MapReads == 0,
            "map readiness requires the actual controlled turn and all native idle gates");

        HotSeatOverlay.IsActive = false; overlay.gameObject.activeInHierarchy = true;
        Check(View(host).Phase == "transition" && !(bool)Call(host, "PrivateSamplingAllowed", state),
            "fresh fade-in is concealed even if an earlier handoff ticket is unavailable");
        HotSeatOverlay.IsActive = true; overlay.player = new Player { Id = 1 }; overlay.continueCallback = delegate { };
        view = View(host);
        Check(view.Phase == "human_turn" && view.Target == 1 && !view.CanStart && !view.Ready,
            "human target returns control without starting or skipping its turn");
        Check(view.Id != token && Start(host, "2", token).Contains("STALE_HANDOFF") && overlay.Invocations == 1,
            "new target invalidates the earlier accepted token");
        overlay.player = new Player { Id = 2 }; state.Turn++;
        view = View(host); token = view.Id;
        overlay.continueCallback = delegate { throw new InvalidOperationException("native callback failure"); };
        Check(Start(host, "2", token).Contains("HANDOFF_INVOCATION_UNCERTAIN") && overlay.Invocations == 2,
            "callback exception reports uncertain submission after native delegate consumption");
        string duplicateFailure = Start(host, "2", token);
        Check(duplicateFailure.Contains("\"outcome\":\"unknown\"") && duplicateFailure.Contains("\"callbackAttempted\":true")
            && !duplicateFailure.Contains("\"dispatched\":false") && !duplicateFailure.Contains("native callback failure"),
            "consumed handoff callback is unknown without false dispatch proof or private exception text");
        Check(duplicateFailure.Contains("HANDOFF_INVOCATION_UNCERTAIN") && duplicateFailure.Contains("\"accepted\":false")
            && duplicateFailure.Contains("\"duplicate\":true") && overlay.Invocations == 2,
            "uncertain native invocation retains uncertainty and is never blindly replayed");
        client.CurrentGameId = Guid.NewGuid(); overlay.continueCallback = delegate { };
        Check(View(host).Id != token && !View(host).Submitted, "a different native game resets stale submission state");
        token = View(host).Id; client.IsReplay = true;
        Check(View(host).Phase == "identity_unknown" && !View(host).CanStart,
            "replay or simulation presentation cannot become a handoff target");
        client.IsReplay = false; state.Phase = "Ended";
        Check(View(host).Phase == "ended" && !View(host).CanStart, "ended match cannot start a leftover curtain callback");
        state.Phase = "Started"; Set(host, "_fixedControlledSeat", (byte?)255);
        Check(View(host).Phase == "configuration_required" && !View(host).CanStart,
            "malformed fixed-seat configuration cannot fall back to another player");
        Set(host, "_fixedControlledSeat", (byte?)2); client.clientType = "Local";
        Check(View(host).Phase == "not_hotseat", "single-player match never enters the hotseat callback path");
        client.clientType = "PassAndPlay"; Manager.instance.client = null;
        string menu = (string)Call(host, "HandoffStatus");
        Check(menu.Contains("\"ok\":true") && menu.Contains("no_active_game"),
            "menu status is a successful public observation with no active game");
        Manager.instance.client = client; HotSeatOverlay.IsActive = false; HotSeatOverlay.Install(null);
        Check((bool)Call(host, "HandoffPresentationInactive"),
            "a provably absent native singleton is an inactive presentation");
        Set(host, "_fixedControlledSeat", (byte?)null);
        Check(!(bool)Call(host, "PrivateSamplingAllowed", state),
            "unconfigured PassAndPlay cannot fall back to the currently displayed human's private view");
        client.clientType = "Local";
        Check((bool)Call(host, "PrivateSamplingAllowed", state),
            "unconfigured single-player retains the existing local observation compatibility");
        Check(state.MapReads == 0, "all managed endpoint and callback tests leave the private map unread");
        Console.WriteLine("TOTAL " + passed + " passed"); return 0;
    }
}
