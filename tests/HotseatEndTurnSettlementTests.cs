// Managed regression of production dispatch, boundary and settlement entrypoints.
// No game process, native curtain interaction or save changes are claimed here.
using System;
using System.Reflection;
using PolytopiaAgent;

public sealed class HotSeatOverlay
{
    public sealed class ObjectState { public bool activeInHierarchy; }
    public static HotSeatOverlay instance = new HotSeatOverlay();
    public static bool IsActive { get; set; }
    public object player;
    public ObjectState gameObject = new ObjectState();
}
internal static class HotseatEndTurnSettlementTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Player { public byte Id; public int Currency = 10; }
    private sealed class Tile
    {
        public string coordinates = "(2,3)"; public byte owner = 2;
        public bool GetExplored(byte player) { return true; }
        public object GetUnit(GameState state, byte player, bool hidden) { return null; }
    }
    private sealed class Map { public Tile[] Tiles = new[] { new Tile() }; }
    private sealed class GameState
    {
        public byte Acting; public int Turn = 20, PrivateReads; public bool Concealed;
        public string Phase = "Started";
        private readonly Player[] players = new[] { new Player { Id = 1 }, new Player { Id = 2 } };
        public int CurrentTurn { get { return Turn; } }
        public byte CurrentPlayer { get { return Acting; } }
        public int CurrentPlayerIndex { get { return Acting - 1; } }
        public string CurrentState { get { return Phase; } }
        private void Private() { PrivateReads++; if (Concealed) throw new Exception("private read during human curtain"); }
        public Player[] Players { get { Private(); return players; } }
        public Map Map { get { Private(); return new Map(); } }
        public bool HasQueuedCommands() { Private(); return false; }
    }
    private sealed class ActionManager { public object GameState; public bool IsSimulating { get; set; } }
    private sealed class Client
    {
        public string clientType = "PassAndPlay";
        public object GameState, ActionManager;
        public Guid CurrentGameId = Guid.NewGuid();
        public bool IsReady { get { return true; } } public bool IsReplay { get; set; }
    }
    private sealed class Manager
    {
        public static Manager instance { get; set; }
        public static object GameState { get; set; }
        public static Player LocalPlayer { get; set; }
        public Client client;
    }
    private sealed class Fixture
    {
        internal BridgeMod Host = new BridgeMod(); internal GameState State = new GameState();
        internal Client Client; internal ActionManager Manager; internal long Ticket;
        internal int Reads;
        internal Fixture(byte seat, string action, bool? accepted, bool dispatched)
        {
            State.Acting = seat; Manager = new ActionManager { GameState = State };
            Client = new Client { GameState = State, ActionManager = Manager };
            HotseatEndTurnSettlementTests.Manager.instance = new Manager { client = Client };
            HotseatEndTurnSettlementTests.Manager.GameState = State;
            HotseatEndTurnSettlementTests.Manager.LocalPlayer = new Player { Id = seat };
            HotSeatOverlay.instance = new HotSeatOverlay(); HotSeatOverlay.IsActive = false;
            Set(Host, "_fixedControlledSeat", (byte?)seat); Set(Host, "_gameManagerType", typeof(Manager));
            Set(Host, "_gameStateProp", typeof(Manager).GetProperty("GameState"));
            Set(Host, "_mapProp", typeof(GameState).GetProperty("Map"));
            Set(Host, "_playersProp", typeof(GameState).GetProperty("Players"));
            Set(Host, "_currentPlayerIndexProp", typeof(GameState).GetProperty("CurrentPlayerIndex"));
            Set(Host, "_currentTurnProp", typeof(GameState).GetProperty("CurrentTurn"));
            Set(Host, "_currentStateProp", typeof(GameState).GetProperty("CurrentState"));
            Ticket = Host.BeginCombatSettlement(State, action, null, null, null, 71);
            if (Ticket == 0) throw new Exception("real capture failed to create ticket");
            if (dispatched) Host.CompleteCombatDispatch(Ticket, accepted);
            Call(Host, "BeginHotseatBoundary", Client);
            State.Concealed = true; Reads = State.PrivateReads;
        }
        internal void Curtain(byte target, int turn)
        {
            State.Acting = target; State.Turn = turn;
            HotseatEndTurnSettlementTests.Manager.LocalPlayer.Id = target;
            HotSeatOverlay.instance.player = new Player { Id = target };
            HotSeatOverlay.instance.gameObject.activeInHierarchy = true; HotSeatOverlay.IsActive = true;
        }
        internal string Receipt() { return Host.CombatSettlementJson(State, Ticket); }
    }
    private static int passed;
    private static object Call(BridgeMod h, string name, params object[] args) { return typeof(BridgeMod).GetMethod(name, F).Invoke(h, args); }
    private static void Set(BridgeMod h, string name, object value) { typeof(BridgeMod).GetField(name, F).SetValue(h, value); }
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
    private static void Unconfirmed(Fixture f, string name)
    {
        string receipt = f.Receipt();
        Check(receipt.Contains("\"actionCompleted\":false") && receipt.Contains("\"publicHandoff\":null")
            && f.State.PrivateReads == f.Reads, name);
    }
    public static int Main()
    { try { return Run(); } catch (Exception e) { while (e.InnerException != null) e = e.InnerException; Console.WriteLine(e); return 1; } }
    private static int Run()
    {
        var f = new Fixture(2, "end_turn", true, true); f.Curtain(1, 21); string receipt = f.Receipt();
        Check(receipt.Contains("\"settlementStatus\":\"complete\"") && receipt.Contains("\"effectConfirmation\":\"confirmed\"")
            && receipt.Contains("\"actionCompleted\":true"), "accepted P2T20 EndTurn settles at native P1T21 curtain");
        Check(receipt.Contains("\"pipelineIdle\":null") && receipt.Contains("\"targetPlayerId\":\"1\"")
            && receipt.Contains("\"visibleChanges\":[]") && f.State.PrivateReads == f.Reads,
            "public proof preserves old visible frame and never samples human pipeline or map");
        HotSeatOverlay.IsActive = false; HotSeatOverlay.instance.gameObject.activeInHierarchy = false;
        Check(f.Receipt() == receipt && f.State.PrivateReads == f.Reads, "completed ticket stays frozen when human opens map");
        f = new Fixture(1, "end_turn", true, true); f.Curtain(2, 20);
        Check(f.Receipt().Contains("\"actionCompleted\":true") && f.State.PrivateReads == f.Reads,
            "same-turn P1 to P2 handoff also confirms EndTurn");
        f = new Fixture(2, "end_turn", null, true); f.Curtain(1, 21); Unconfirmed(f, "unknown acceptance never confirms from curtain alone");
        f = new Fixture(2, "end_turn", false, true); f.Curtain(1, 21);
        Check(f.Receipt().Contains("\"settlementStatus\":\"rejected\"") && f.State.PrivateReads == f.Reads,
            "rejected EndTurn remains rejected without human reads");
        f = new Fixture(2, "end_turn", true, false); f.Curtain(1, 21); Unconfirmed(f, "unsubmitted ticket cannot complete");
        f = new Fixture(2, "research", true, true); f.Curtain(1, 21); Unconfirmed(f, "other action cannot borrow EndTurn curtain proof");
        f = new Fixture(2, "end_turn", true, true); f.Curtain(2, 21); Unconfirmed(f, "same-seat curtain is not turn handoff");
        f = new Fixture(2, "end_turn", true, true); f.Curtain(1, 21); f.State.Acting = 2; Unconfirmed(f, "curtain target and public player must agree");
        f = new Fixture(2, "end_turn", true, true); f.Curtain(1, 21); HotSeatOverlay.instance.player = null; Unconfirmed(f, "unknown target stays unknown");
        f = new Fixture(2, "end_turn", true, true); f.Curtain(1, 21); HotSeatOverlay.IsActive = false; Unconfirmed(f, "fade transition is not active curtain proof");
        f = new Fixture(2, "end_turn", true, true); f.Curtain(1, 21); HotSeatOverlay.instance.gameObject.activeInHierarchy = false; Unconfirmed(f, "inactive presentation does not complete");
        f = new Fixture(2, "end_turn", true, true); f.Curtain(1, 21); f.State.Phase = "Ended"; Unconfirmed(f, "terminal game uses terminal completion authority instead");
        f = new Fixture(2, "end_turn", true, true); f.Curtain(1, 21); f.Client.CurrentGameId = Guid.NewGuid(); Unconfirmed(f, "another save cannot complete old ticket");
        f = new Fixture(2, "end_turn", true, true); f.Curtain(1, 21); Manager.GameState = new GameState(); Unconfirmed(f, "main state must be the displayed native state");
        f = new Fixture(2, "end_turn", true, true); f.Curtain(1, 21); f.Manager.IsSimulating = true; Unconfirmed(f, "simulation cannot provide live handoff authority");
        f = new Fixture(2, "end_turn", true, true); f.Curtain(1, 21); f.Client.IsReplay = true; Unconfirmed(f, "replay cannot provide live handoff authority");
        f = new Fixture(2, "end_turn", true, true); f.Curtain(1, 19); Unconfirmed(f, "rewound turn cannot complete");
        f = new Fixture(2, "end_turn", true, true); f.Curtain(1, 23); Unconfirmed(f, "unrelated later round cannot complete original ticket");
        Console.WriteLine("HotseatEndTurnSettlementTests: " + passed + " passed"); return 0;
    }
}
