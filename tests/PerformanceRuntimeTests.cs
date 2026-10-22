using System;
using System.Collections;
using System.Reflection;
using PolytopiaAgent;

public sealed class ClientActionManager
{
    public static int Constructions;
    public ClientActionManager(object state) { Constructions++; }
}

internal static class PerformanceRuntimeTests
{
    private sealed class Player { public byte Id = 2; }
    private sealed class State
    {
        public int MapReads, QueueReads;
        public object Map { get { MapReads++; return null; } }
        public bool HasQueuedCommands() { QueueReads++; return false; }
        public int CurrentTurn { get { return 14; } }
        public string CurrentState { get { return "Started"; } }
    }
    private sealed class ActionManager { public object GameState; public bool IsSimulating { get { return false; } } }
    private sealed class Client { public object GameState, ActionManager; public bool IsReady { get { return true; } } public bool IsReplay { get { return false; } } public Guid? CurrentGameId = Guid.NewGuid(); }
    private sealed class Manager
    {
        public static Manager instance { get; set; }
        public Client client;
        public Client fakeSession;
        public static object GameState { get; set; }
        public static Player LocalPlayer { get; set; }
    }
    private sealed class OverlayProperty { public static object instance { get; set; } }
    private sealed class OverlayGetter { public static object Value; public static object get_instance() { return Value; } }
    private static int passed;
    private static readonly BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private static object Call(BridgeMod b, string method, params object[] args)
    { return typeof(BridgeMod).GetMethod(method, F).Invoke(b, args); }
    private static void Set(BridgeMod b, string field, object value)
    { typeof(BridgeMod).GetField(field, F).SetValue(b, value); }
    private static object Get(BridgeMod b, string field)
    { return typeof(BridgeMod).GetField(field, F).GetValue(b); }
    private static void Check(bool ok, string name)
    { if (!ok) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }

    public static int Main()
    {
        try { return Run(); }
        catch (Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            Console.WriteLine("FAIL " + e.GetType().FullName + ": " + e.Message); return 1;
        }
    }

    private static int Run()
    {
        var b = new BridgeMod(); var state = new State(); var client = new Client { GameState = state };
        Manager.instance = new Manager { client = client }; Manager.GameState = state; Manager.LocalPlayer = new Player();
        Set(b, "_gameManagerType", typeof(Manager)); Set(b, "_gameStateProp", typeof(Manager).GetProperty("GameState"));
        Set(b, "_mapProp", typeof(State).GetProperty("Map")); Set(b, "_currentTurnProp", typeof(State).GetProperty("CurrentTurn"));
        Set(b, "_currentStateProp", typeof(State).GetProperty("CurrentState")); Set(b, "_fixedControlledSeat", (byte?)2);
        Manager.instance.fakeSession = new Client { ActionManager = new object() };
        Check(Call(b, "FindActionManager", Manager.instance, false) == null, "missing main action manager never falls back to simulation manager");
        var unavailable = (string)Call(b, "ExecuteGameAction", "end_turn", "", "", "", "", "", "", "", null, null);
        Check(ClientActionManager.Constructions == 0, "real action path never constructs a replacement manager; actual constructions=" + ClientActionManager.Constructions);
        Check(unavailable.Contains("ACTION_MANAGER_UNAVAILABLE") && unavailable.Contains("not_dispatched"),
            "missing main manager supplies explicit nonexecution proof");
        client.ActionManager = new object();
        Check(Object.ReferenceEquals(Call(b, "FindActionManager", Manager.instance, false), client.ActionManager), "configured runtime uses main client's actual action manager");
        client.ActionManager = new ActionManager { GameState = state };
        Check((bool)Call(b, "PrivateSamplingAllowed", state), "configured own seat accepts real main state");
        Manager.LocalPlayer.Id = 1;
        Check(!(bool)Call(b, "PrivateSamplingAllowed", state), "other human seat pauses observation");
        Check(Call(b, "CRCapture", state) == null && state.MapReads == 0, "other seat rejected before reading map");
        var tickets = (IDictionary)Get(b, "_combatTickets");
        var ticketType = typeof(BridgeMod).GetNestedType("CombatTicket", BindingFlags.NonPublic);
        object ticket = Activator.CreateInstance(ticketType, true); tickets.Add(123L, ticket);
        object stream = Get(b, "_combatStream");
        Call(b, "CombatRuntimeTick", state);
        Check(tickets.Contains(123L) && Object.ReferenceEquals(tickets[123L], ticket)
            && Object.Equals(stream, Get(b, "_combatStream")), "other seat tick preserves own tickets and stream");
        string paused = (string)Call(b, "GameInfoPublic");
        Check(paused.Contains("paused-other-seat-or-simulation") && !paused.Contains("stars") && state.MapReads == 0,
            "paused game info exposes only handoff metadata");
        Manager.LocalPlayer.Id = 2; var fake = new State(); Manager.GameState = fake;
        Check(!(bool)Call(b, "PrivateSamplingAllowed", state) && !(bool)Call(b, "PrivateSamplingAllowed", fake),
            "fake displayed state and main state are both withheld during simulation");
        Check(Object.ReferenceEquals(Call(b, "GameStateInstance"), state), "canonical state bypasses fake-session preferred getter");
        Manager.GameState = state;
        string episode = (string)Call(b, "EpisodeId", state);
        client.GameState = new State(); Manager.GameState = client.GameState;
        Check((string)Call(b, "EpisodeId", client.GameState) == episode, "same main client and game keeps episode across hotseat state copies");
        Check(Call(b, "EpisodeId", fake) == null && (string)Call(b, "EpisodeId", client.GameState) == episode,
            "foreign simulation cannot replace episode cache");
        client.CurrentGameId = Guid.NewGuid();
        Check((string)Call(b, "EpisodeId", client.GameState) != episode, "new game ID starts a different opaque episode");
        episode = (string)Call(b, "EpisodeId", client.GameState);
        Manager.instance.client = new Client { GameState = state, ActionManager = new ActionManager { GameState = state }, CurrentGameId = client.CurrentGameId }; Manager.GameState = state;
        Check((string)Call(b, "EpisodeId", state) != episode, "new main session with same game ID still resets episode");

        Call(b, "BeginReadRequest", "world");
        object pipeline = Call(b, "CRReadPipeline", state); int reads = state.QueueReads;
        Check(Object.ReferenceEquals(pipeline, Call(b, "CRReadPipeline", state)) && state.QueueReads == reads,
            "one read request reuses actual pipeline sample without rereading dynamic state");
        Call(b, "InvalidateNativeSamples");
        Check(!Object.ReferenceEquals(pipeline, Call(b, "CRReadPipeline", state)) && state.QueueReads == reads + 1,
            "native generation invalidation forces fresh pipeline sample");
        pipeline = Call(b, "CRReadPipeline", state);
        Check(!Object.ReferenceEquals(pipeline, Call(b, "CRReadPipeline", fake)), "different native state never shares pipeline sample");
        Call(b, "EndReadRequest");
        Check(!Object.ReferenceEquals(Call(b, "CRReadPipeline", state), Call(b, "CRReadPipeline", state)),
            "no pipeline value cache survives request boundary");
        Check(!BridgeMod.ReadRequestCanReuse("action") && !BridgeMod.ReadRequestCanReuse("unit_action")
            && !BridgeMod.ReadRequestCanReuse("popup") && !BridgeMod.ReadRequestCanReuse("call"),
            "mutation and arbitrary callbacks never enable read request reuse");

        Call(b, "BeginReadRequest", "world"); Call(b, "CombatRuntimeTick", state);
        reads = state.MapReads; object stable = Get(b, "_combatStableFrames");
        Call(b, "CombatRuntimeTick", state);
        Check(state.MapReads == reads && Object.Equals(stable, Get(b, "_combatStableFrames")),
            "duplicate tick in same request neither rescans map nor creates stability samples");
        Call(b, "InvalidateNativeSamples"); Call(b, "CombatRuntimeTick", state);
        Check(state.MapReads == reads + 1, "native invalidation preserves fresh combat sampling even in same request");
        Call(b, "EndReadRequest");
        OverlayProperty.instance = new object(); OverlayGetter.Value = new object();
        Check(Object.ReferenceEquals(Call(b, "InstanceOf", typeof(OverlayProperty)), OverlayProperty.instance),
            "lowercase instance property resolves hotseat overlay without constructing it");
        Check(Object.ReferenceEquals(Call(b, "InstanceOf", typeof(OverlayGetter)), OverlayGetter.Value),
            "generated static get_instance resolves hotseat overlay without constructing it");
        const int batches = 1000;
        Call(b, "EndReadRequest"); state.QueueReads = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < batches; i++)
            for (int j = 0; j < 3; j++) Call(b, "CRReadPipelineCore", state);
        watch.Stop(); double uncachedMs = watch.Elapsed.TotalMilliseconds; int uncachedReads = state.QueueReads;
        state.QueueReads = 0; watch.Restart();
        for (int i = 0; i < batches; i++)
        {
            Call(b, "BeginReadRequest", "world");
            for (int j = 0; j < 3; j++) Call(b, "CRReadPipeline", state);
            Call(b, "EndReadRequest");
        }
        watch.Stop();
        Check(uncachedReads == 3000 && state.QueueReads == 1000, "request reuse benchmark measures actual native-shaped queue read reduction");
        Console.WriteLine("BENCH {\"batches\":1000,\"pipelineCallsPerRequest\":3,\"beforeReads\":" + uncachedReads
            + ",\"afterReads\":" + state.QueueReads + ",\"beforeMs\":" + uncachedMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
            + ",\"afterMs\":" + watch.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "}");
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bridge-root-metrics-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        int pid = BridgeMod.OwnPid;
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "bridge-ready.json"), "{\"pid\":" + pid + "}");
        string cmdPath = System.IO.Path.Combine(directory, "cmd.jsonl");
        System.IO.File.WriteAllText(cmdPath, new string('x', 100) + "a\n", new System.Text.UTF8Encoding(false));
        long offset = 100, lastPoll = 0; string pending = "";
        var stats = new BridgeMod.TransportIoStats();
        Check(BridgeMod.TransportPollCmdText(100, ref lastPoll, ref offset, ref pending, cmdPath, directory, pid, stats) == "a\n",
            "transport consumes only appended bytes");
        Check(offset == 102 && stats.CmdBytes == 2, "transport byte metric counts the read delta rather than total file size");
        System.IO.File.AppendAllText(cmdPath, "b\n", new System.Text.UTF8Encoding(false));
        Check(BridgeMod.TransportPollCmdText(200, ref lastPoll, ref offset, ref pending, cmdPath, directory, pid, stats) == "b\n"
            && offset == 104 && stats.CmdBytes == 4, "successive polls advance by actual bytes read");
        Check(BridgeMod.TransportPollCmdText(300, ref lastPoll, ref offset, ref pending, cmdPath, directory, pid, stats) == null
            && stats.CmdBytes == 4, "unchanged file adds no measured bytes");
        string resume = "{\"type\":\"GameManager\",\"method\":\"ResumeSingleplayerGame\"}";
        Check(!(bool)Call(b, "SeatIndependentCommand", "call", resume), "single-player resume never interrupts active hotseat match");
        Set(b, "_fixedControlledSeat", (byte?)1);
        Check(!(bool)Call(b, "SeatIndependentCommand", "call", resume), "single-player resume never interrupts active single-player match");
        Manager.instance.client.GameState = null;
        Check(!(bool)Call(b, "SeatIndependentCommand", "call", resume), "missing main state with active displayed state cannot resume over it");
        Manager.GameState = null;
        Check((bool)Call(b, "SeatIndependentCommand", "call", resume), "menu permits exact normal single-player resume without private map");
        Check(!(bool)Call(b, "SeatIndependentCommand", "call", resume.Replace("ResumeSingleplayerGame", "ResumeHotseatGame"))
            && !(bool)Call(b, "SeatIndependentCommand", "call", resume.Replace("}", ",\"args\":\"other-save\"}")),
            "resume exception excludes hotseat and alternate arguments");
        foreach (int fps in new[] { 60, 1200 })
        {
            long nextSample = 0, nextInbox = 0, nextSnapshot = 0;
            int samples = 0, polls = 0, snapshots = 0;
            // Exact synthetic monotonic clock; ten seconds with identical elapsed time.
            for (int frame = 0; frame < fps * 10; frame++)
            {
                long now = frame * 60000L / fps;
                if (frame % 6 == 0 && BridgeMod.PeriodicWorkDue(now, 60000, ref nextSample, 100)) samples++;
                if (frame % 3 == 0 && BridgeMod.PeriodicWorkDue(now, 60000, ref nextInbox, 50)) polls++;
                if (BridgeMod.PeriodicWorkDue(now, 60000, ref nextSnapshot, 500)) snapshots++;
            }
            Check(samples == 100, "background native samples bounded to 10/sec at " + fps + " fps");
            Check(polls == 200, "command polls bounded to 20/sec at " + fps + " fps");
            Check(snapshots == 20, "unsolicited snapshots bounded to 2/sec at " + fps + " fps");
        }
        long nextDeadline = 0;
        Check(BridgeMod.PeriodicWorkDue(0, 1000, ref nextDeadline, 100)
            && !BridgeMod.PeriodicWorkDue(99, 1000, ref nextDeadline, 100)
            && BridgeMod.PeriodicWorkDue(100, 1000, ref nextDeadline, 100), "cadence uses actual elapsed time and exact deadline");
        Check(BridgeMod.PeriodicWorkDue(1000, 1000, ref nextDeadline, 100) && nextDeadline == 1100
            && !BridgeMod.PeriodicWorkDue(1000, 1000, ref nextDeadline, 100), "long stall resumes once without catch-up burst");
        Call(b, "EndReadRequest"); Manager.instance.client.GameState = state; Manager.GameState = state; Manager.LocalPlayer.Id = 1;
        Set(b, "_nextCombatSampleTime", long.MaxValue); reads = state.MapReads;
        Call(b, "CombatRuntimeTick", state);
        Check(state.MapReads == reads + 1, "explicit request sampling bypasses background cadence");
        Console.WriteLine("TOTAL " + passed + " passed"); return 0;
    }
}
