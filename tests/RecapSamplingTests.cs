// Production entrypoint regressions for main-client history reconstruction. No game calls.
using System;
using System.Collections;
using System.Reflection;
using PolytopiaAgent;

internal static class RecapSamplingTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Player { public byte Id = 2; }
    private sealed class State
    {
        public int MapReads, Turn = 15;
        public object Map { get { MapReads++; return null; } }
        public int CurrentTurn { get { return Turn; } }
        public string CurrentState { get { return "Started"; } }
    }
    private sealed class ActionManager
    {
        public object GameState;
        public object Simulating = false;
        public bool IsSimulating { get { return (bool)Simulating; } }
        public bool IsRecap { get; set; }
    }
    private sealed class Client
    {
        public object GameState, ActionManager;
        public object Ready = true, Replay = false;
        public bool IsReady { get { return (bool)Ready; } }
        public bool IsReplay { get { return (bool)Replay; } }
    }
    private sealed class Manager
    {
        public static Manager instance { get; set; }
        public Client client;
        public static object GameState { get; set; }
        public static Player LocalPlayer { get; set; }
    }
    private sealed class ActionProbe
    {
        public int Reads;
        public byte PlayerId { get { Reads++; return 2; } }
        public byte SenderId { get { Reads++; return 1; } }
    }
    private static int passed;
    private static object Call(BridgeMod host, string name, params object[] args)
    { return typeof(BridgeMod).GetMethod(name, F).Invoke(host, args); }
    private static void Set(BridgeMod host, string name, object value)
    { typeof(BridgeMod).GetField(name, F).SetValue(host, value); }
    private static object Get(BridgeMod host, string name)
    { return typeof(BridgeMod).GetField(name, F).GetValue(host); }
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
    public static int Main()
    {
        try { return Run(); }
        catch (Exception error)
        { while (error.InnerException != null) error = error.InnerException; Console.WriteLine(error); return 1; }
    }
    private static int Run()
    {
        var host = new BridgeMod(); var state = new State();
        var am = new ActionManager { GameState = state }; var client = new Client { GameState = state, ActionManager = am };
        Manager.instance = new Manager { client = client }; Manager.GameState = state; Manager.LocalPlayer = new Player();
        Set(host, "_fixedControlledSeat", (byte?)2); Set(host, "_gameManagerType", typeof(Manager));
        Set(host, "_gameStateProp", typeof(Manager).GetProperty("GameState"));
        Set(host, "_mapProp", typeof(State).GetProperty("Map"));
        Set(host, "_currentTurnProp", typeof(State).GetProperty("CurrentTurn"));
        Set(host, "_currentStateProp", typeof(State).GetProperty("CurrentState"));
        Set(host, "_combatHookHost", host); Set(host, "_publicRuntimeOwner", host);
        Check((bool)Call(host, "PrivateSamplingAllowed", state), "normal ready main client remains observable during action processing");
        var trusted = new BridgeMod.CombatFrame { Episode = "same-match", Me = "2", Turn = "14", Acting = "2", Phase = "Started", Complete = true };
        trusted.Units.Add("18", new BridgeMod.CombatUnitView { Id = "18", Owner = "1", Type = "Warrior", Coord = "(7,4)", Health = 90 });
        Call(host, "CRObserve", trusted, null);
        var ticket = new BridgeMod.CombatTicket { Id = 1, Stream = (string)Get(host, "_combatStream"), FinalFrame = trusted, FinalEvent = 204 };
        ((IDictionary)Get(host, "_combatTickets")).Add(1L, ticket);
        Set(host, "_combatEventId", 204L); Set(host, "_combatCaptureAvailable", true);
        string stream = (string)Get(host, "_combatStream"); long version = (long)Get(host, "_combatVersion");
        am.Simulating = true;
        for (int turn = 0; turn <= 15; turn++)
        { state.Turn = turn; Call(host, "CRCapture", state); Call(host, "CombatRuntimeTick", state); }
        Check(state.MapReads == 0, "main displayed history reconstruction performs no map reads across Turn 0 to 15");
        Check(Object.ReferenceEquals(Get(host, "_combatFrame"), trusted) && (long)Get(host, "_combatEventId") == 204
            && (long)Get(host, "_combatVersion") == version && (string)Get(host, "_combatStream") == stream,
            "reconstruction preserves trusted frame stream version and cursor without replayed events");
        Check(Object.ReferenceEquals(((IDictionary)Get(host, "_combatTickets"))[1L], ticket) && ticket.FinalFrame == trusted && ticket.FinalEvent == 204,
            "frozen receipt is untouched by history reconstruction");
        var probe = new ActionProbe(); var prefix = new object[] { probe, state, null, null };
        Call(host, "CombatActionPrefix", prefix);
        Check(prefix[3] == null && probe.Reads == 0 && state.MapReads == 0, "simulated native prefix exits before action or map fields are read");
        Call(host, "PublicAlivePostfix", probe, state, true); Call(host, "PublicMatchEndedPrefix", true, null, (byte)1);
        Check(probe.Reads == 0 && Get(host, "_publicResultState") == null, "simulated alive and match result cannot enter public observations");
        var passType = typeof(BridgeMod).GetNestedType("CombatHookPass", F);
        object pass = Activator.CreateInstance(passType, true);
        passType.GetField("State", F).SetValue(pass, state); passType.GetField("Action", F).SetValue(pass, probe);
        passType.GetField("CandidateKind", F).SetValue(pass, "ReceiveDiplomacyMessageAction");
        var causes = (IList)Get(host, "_combatCauseStack"); causes.Add(new BridgeMod.CombatCause()); Set(host, "_combatHookDepth", 1);
        var active = (IList)Get(host, "_publicDiplomacyActive");
        active.Add(new BridgeMod.PublicDiplomacyObservation { NativeAction = probe, Me = 2, Other = 1, Kind = "ReceiveDiplomacyMessageAction" });
        object unrelated = new object(); active.Add(new BridgeMod.PublicDiplomacyObservation { NativeAction = unrelated });
        Call(host, "CombatCloseHook", pass, false);
        Check((int)Get(host, "_combatHookDepth") == 0 && causes.Count == 0 && (bool)passType.GetField("Closed", F).GetValue(pass),
            "prefix to simulation transition still balances native hook cleanup");
        Check(probe.Reads == 0 && state.MapReads == 0 && active.Count == 1
            && Object.ReferenceEquals(((BridgeMod.PublicDiplomacyObservation)active[0]).NativeAction, unrelated),
            "suppressed postfix discards only its pending diplomacy proof without completing historical receipts");
        Check(!(bool)Get(host, "_combatCaptureAvailable") && (int)Get(host, "_combatStableFrames") == 0,
            "suppressed hook cannot contribute a successful stable settlement sample");
        Call(host, "CombatCloseHook", pass, false);
        Check(active.Count == 1 && (int)Get(host, "_combatHookDepth") == 0, "postfix finalizer pair closes once");
        am.Simulating = null;
        Check(!(bool)Call(host, "PrivateSamplingAllowed", state), "unknown simulation flag is withheld");
        am.Simulating = false; client.Ready = false;
        Check(!(bool)Call(host, "PrivateSamplingAllowed", state), "not ready main client is withheld");
        client.Ready = null;
        Check(!(bool)Call(host, "PrivateSamplingAllowed", state), "unknown readiness is withheld");
        client.Ready = true; client.Replay = true;
        Check(!(bool)Call(host, "PrivateSamplingAllowed", state), "full replay is withheld independently of recap");
        client.Replay = null;
        Check(!(bool)Call(host, "PrivateSamplingAllowed", state), "unknown replay flag is withheld");
        client.Replay = false; am.GameState = new State();
        Check(!(bool)Call(host, "PrivateSamplingAllowed", state), "wrong main action manager state is withheld");
        am.GameState = state; client.ActionManager = null;
        Check(!(bool)Call(host, "PrivateSamplingAllowed", state), "missing main action manager is withheld");
        client.ActionManager = am; am.IsRecap = true;
        Check((bool)Call(host, "PrivateSamplingAllowed", state), "visible player recap remains allowed when not simulating");
        Call(host, "CRCapture", state);
        Check(state.MapReads == 1, "allowed native recap can enter normal visibility sampling");
        var after = new BridgeMod.CombatFrame { Episode = "same-match", Me = "2", Turn = "15", Acting = "2", Phase = "Started", Complete = true };
        after.Units.Add("18", new BridgeMod.CombatUnitView { Id = "18", Owner = "1", Type = "Warrior", Coord = "(7,4)", Health = 40 });
        Call(host, "CRObserve", after, null);
        Check((long)Get(host, "_combatEventId") == 206 && (string)Get(host, "_combatStream") == stream,
            "resumed observation adds only current HP and turn differences to existing journal");
        var events = (IList)Get(host, "_combatEvents"); string eventJson = events[0].ToString();
        Check(eventJson.Contains("\"nativeAction\":null") && eventJson.Contains("\"actorId\":null"),
            "resumed snapshot differences cannot impersonate replayed native commands");
        var native = new BridgeMod.CombatCause { ActionType = "AttackAction", ActorId = "22", ActorOwner = "2", TargetId = "18", TargetCoord = "(7,4)", VisibleNativeAction = true };
        var newest = new BridgeMod.CombatFrame { Episode = "same-match", Me = "2", Turn = "15", Acting = "2", Phase = "Started", Complete = true };
        newest.Units.Add("18", new BridgeMod.CombatUnitView { Id = "18", Owner = "1", Type = "Warrior", Coord = "(7,4)", Health = 20 });
        Call(host, "CRObserve", newest, native);
        Check((long)Get(host, "_combatEventId") == 207 && events[2].ToString().Contains("AttackAction"),
            "actual visible native cause after recap still produces an attributed new event");
        Manager.LocalPlayer.Id = 1;
        Check(!(bool)Call(host, "PrivateSamplingAllowed", state), "simulation fix preserves other human seat rejection");
        Console.WriteLine("TOTAL " + passed + " passed"); return 0;
    }
}
