// Calls production lifecycle entrypoints with a native-shaped visible tile, no game process.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using PolytopiaAgent;

public sealed class HotSeatOverlay
{
    public sealed class GameObject { public bool activeInHierarchy; }
    public static HotSeatOverlay instance = new HotSeatOverlay();
    public static bool IsActive { get; set; }
    public GameObject gameObject = new GameObject();
}
internal static class HotseatBoundaryTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Player { public byte Id = 2; public int Currency = 10; }
    private sealed class Unit { public int id = 18; public byte owner = 1; public string type = "Warrior"; public int health = 100; }
    private sealed class Tile
    {
        public string coordinates = "(8,3)"; public byte owner = 1; public bool visible = true;
        public Unit Unit = new Unit(); public int Reads;
        public bool GetExplored(byte player) { return player == 2; }
        public object GetUnit(GameState gs, byte player, bool hidden)
        { Reads++; if (player != 2 || hidden) throw new Exception("invalid viewer"); return visible ? Unit : null; }
    }
    private sealed class Map { public Tile[] Tiles; }
    private sealed class GameState
    {
        public int Reads, Turn = 17; public bool Queued;
        public Map Data = new Map { Tiles = new[] { new Tile() } };
        public Map Map { get { Reads++; return Data; } }
        public int CurrentTurn { get { return Turn; } }
        public string CurrentState { get { return "Started"; } }
        public int CurrentPlayerIndex { get { return 0; } }
        public Player[] Players { get { return new[] { Manager.LocalPlayer }; } }
        public bool HasQueuedCommands() { return Queued; }
    }
    private sealed class ActionManager
    {
        public object GameState; public bool IsSimulating { get; set; }
        public bool IsRecap { get; set; } public bool IsProcessing { get; set; }
    }
    private sealed class Client
    {
        public string clientType = "PassAndPlay"; public object GameState, ActionManager;
        public Guid CurrentGameId = Guid.NewGuid();
        public bool IsReady { get { return true; } } public bool IsReplay { get { return false; } }
    }
    private sealed class Manager
    {
        public static Manager instance { get; set; } public Client client;
        public static object GameState { get; set; } public static Player LocalPlayer { get; set; }
    }
    private sealed class MoveAction
    { public string UnitId = "18", Origin = "(8,3)", Target = "(7,4)"; public byte PlayerId = 1; public void Execute(GameState state) { } }
    private sealed class EmbarkAction
    { public string UnitId = "18", Origin = "(7,4)", Target = "(7,4)"; public byte PlayerId = 1; public void Execute(GameState state) { } }
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
    { try { return Run(); } catch (Exception e) { while (e.InnerException != null) e = e.InnerException; Console.WriteLine(e); return 1; } }
    private static int Run()
    {
        var host = new BridgeMod(); var gs = new GameState(); var am = new ActionManager { GameState = gs };
        var client = new Client { GameState = gs, ActionManager = am };
        Manager.instance = new Manager { client = client }; Manager.GameState = gs; Manager.LocalPlayer = new Player();
        Set(host, "_fixedControlledSeat", (byte?)2); Set(host, "_gameManagerType", typeof(Manager));
        Set(host, "_gameStateProp", typeof(Manager).GetProperty("GameState"));
        Set(host, "_mapProp", typeof(GameState).GetProperty("Map"));
        Set(host, "_playersProp", typeof(GameState).GetProperty("Players"));
        Set(host, "_currentPlayerIndexProp", typeof(GameState).GetProperty("CurrentPlayerIndex"));
        Set(host, "_currentTurnProp", typeof(GameState).GetProperty("CurrentTurn"));
        Set(host, "_currentStateProp", typeof(GameState).GetProperty("CurrentState")); Set(host, "_combatHookHost", host);
        var trusted = (BridgeMod.CombatFrame)Call(host, "CRCapture", gs);
        Check(trusted != null && trusted.Complete && trusted.Units.ContainsKey("18"), "fixture uses the real visibility projection with an enemy unit");
        Call(host, "CRObserve", trusted, null); string stream = (string)Get(host, "_combatStream");
        var ticket = new BridgeMod.CombatTicket { Id = 1, Stream = stream, FinalFrame = trusted, FinalEvent = 44 };
        ((IDictionary)Get(host, "_combatTickets")).Add(1L, ticket); Set(host, "_combatEventId", 44L);
        var events = (IList)Get(host, "_combatEvents"); int reads = gs.Reads;
        Call(host, "HotseatClientBoundaryPrefix", client);
        Manager.LocalPlayer.Id = 1; gs.Turn = 18; gs.Data.Tiles[0].coordinates = "(7,4)";
        Call(host, "CombatRuntimeTick", gs); Manager.LocalPlayer.Id = 2; HotSeatOverlay.IsActive = true;
        Call(host, "CombatRuntimeTick", gs);
        Check(gs.Reads == reads && events.Count == 0 && (long)Get(host, "_combatEventId") == 44,
            "handoff final state is not sampled before visible recap");
        am.IsSimulating = true; gs.Turn = 17; gs.Data.Tiles[0].coordinates = "(8,3)";
        Call(host, "CombatRuntimeTick", gs); am.IsSimulating = false;
        Call(host, "HotseatResumePostfix", am);
        Check((bool)Get(host, "_hotseatBoundaryPending") && gs.Reads == reads,
            "simulation flag clearing and unrelated Resume cannot release the boundary");
        am.IsRecap = true; Call(host, "HotseatRecapStartedPrefix");
        Check((bool)Get(host, "_hotseatBoundaryPending") && gs.Reads == reads,
            "RecapStarted under active overlay records only a signal without reading the concealed map");
        var concealedMove = new MoveAction(); var concealedPrefix = new object[] { concealedMove, gs, typeof(MoveAction).GetMethod("Execute"), null };
        Call(host, "CombatActionPrefix", concealedPrefix); gs.Data.Tiles[0].coordinates = "(9,3)";
        Check(concealedPrefix[3] == null && gs.Reads == reads && events.Count == 0,
            "recap action concealed by handoff overlay is neither sampled nor later invented");
        HotSeatOverlay.IsActive = false;
        HotSeatOverlay.instance.gameObject.activeInHierarchy = true;
        Check(!(bool)Call(host, "PrivateSamplingAllowed", gs) && gs.Reads == reads && events.Count == 0,
            "fade-out is still concealed after native IsActive becomes false");
        HotSeatOverlay.instance.gameObject.activeInHierarchy = false;
        Check((bool)Call(host, "PrivateSamplingAllowed", gs), "visible recap boundary releases when handoff overlay is gone");
        Check(((BridgeMod.CombatFrame)Get(host, "_combatFrame")).Units["18"].Coord == "(9,3)" && events.Count == 0,
            "first displayed sample rebases to actual current recap position without backfilling concealed movement");
        Check(!(bool)Get(host, "_hotseatBoundaryPending") && gs.Reads == reads + 1 && events.Count == 0,
            "RecapStarted captures its actual visible start baseline without reverse snapshot events");
        Check((string)Get(host, "_combatStream") == stream && (long)Get(host, "_combatEventId") == 44
            && ticket.FinalFrame == trusted && Object.ReferenceEquals(((IDictionary)Get(host, "_combatTickets"))[1L], ticket),
            "baseline rebase retains cursor stream and exact frozen ticket");
        var move = new MoveAction { Origin = "(9,3)" }; var prefix = new object[] { move, gs, typeof(MoveAction).GetMethod("Execute"), null };
        Call(host, "CombatActionPrefix", prefix);
        Check(prefix[3] != null, "normal visible opponent MoveAction enters the real native hook path during recap");
        gs.Data.Tiles[0].coordinates = "(7,4)"; Call(host, "CombatActionPostfix", prefix[3]);
        Check(events.Count == 1 && events[0].ToString().Contains("MoveAction") && (long)Get(host, "_combatEventId") == 45,
            "opponent visible move is emitted once after the silent recap boundary");
        var embark = new EmbarkAction(); prefix = new object[] { embark, gs, typeof(EmbarkAction).GetMethod("Execute"), null };
        Call(host, "CombatActionPrefix", prefix); gs.Data.Tiles[0].Unit.id = 33; gs.Data.Tiles[0].Unit.type = "Transportship";
        Call(host, "CombatActionPostfix", prefix[3]);
        Check(events.Count == 3 && events[1].ToString().Contains("EmbarkAction") && events[2].ToString().Contains("EmbarkAction"),
            "visible embark retains native attribution without reading hidden passenger data");
        am.IsRecap = false; HotSeatOverlay.IsActive = false;
        HotSeatOverlay.instance.gameObject.activeInHierarchy = true;
        reads = gs.Reads;
        Check(!(bool)Call(host, "PrivateSamplingAllowed", gs) && gs.Reads == reads,
            "fresh fade-in without a pending lifecycle cannot reveal the concealed next map");
        HotSeatOverlay.instance.gameObject.activeInHierarchy = false;
        Call(host, "HotseatClientBoundaryPrefix", client); gs.Turn = 19;
        reads = gs.Reads; Call(host, "PrivateSamplingAllowed", gs);
        Check((bool)Get(host, "_hotseatBoundaryPending") && gs.Reads == reads,
            "no-recap branch waits for successful natural state update completion");
        HotSeatOverlay.IsActive = true; Call(host, "HotseatStateUpdatedPostfix", client);
        Check((bool)Get(host, "_hotseatBoundaryPending") && gs.Reads == reads,
            "completed state update does not expose map under handoff overlay");
        HotSeatOverlay.IsActive = false; gs.Queued = true; Call(host, "HotseatResumePostfix", am);
        Check((bool)Get(host, "_hotseatBoundaryPending"), "unfinished native commands cannot release no-recap branch");
        gs.Queued = false; am.IsProcessing = true; Call(host, "HotseatResumePostfix", am);
        Check((bool)Get(host, "_hotseatBoundaryPending"), "native processing completion is required without recap");
        am.IsProcessing = false;
        Check((bool)Call(host, "PrivateSamplingAllowed", gs) && !(bool)Get(host, "_hotseatBoundaryPending") && events.Count == 3,
            "normal no-recap completion releases on quiet native state without permanent blockage or fake diff");
        Call(host, "HotseatClientBoundaryPrefix", client); gs.Data.Tiles[0].Unit.health = 80;
        Call(host, "HotseatResumePostfix", am);
        Check((bool)Get(host, "_hotseatBoundaryPending"), "SkipRecap-style async prefix is not mistaken for completion");
        Call(host, "HotseatStateUpdatedPostfix", client);
        Check(!(bool)Get(host, "_hotseatBoundaryPending") && events.Count == 3
            && ((BridgeMod.CombatFrame)Get(host, "_combatFrame")).Units["33"].Health == 80,
            "skipped recap updates current baseline after native state completion without inventing intermediate actions");
        var foreign = new Client { GameState = gs, ActionManager = am }; Call(host, "HotseatClientBoundaryPrefix", foreign);
        Check(!(bool)Get(host, "_hotseatBoundaryPending"), "fake or foreign client cannot create a hotseat lifecycle");
        client.clientType = "Local"; Call(host, "HotseatClientBoundaryPrefix", client);
        Check(!(bool)Get(host, "_hotseatBoundaryPending"), "single player ignores hotseat boundary hooks");
        client.clientType = "PassAndPlay"; Call(host, "HotseatClientBoundaryPrefix", client);
        int priorEvents = events.Count; client.CurrentGameId = Guid.NewGuid();
        Check((bool)Call(host, "PrivateSamplingAllowed", gs) && !(bool)Get(host, "_hotseatBoundaryPending") && events.Count == priorEvents,
            "new main game ID discards only stale pending lifecycle without erasing journal or tickets");
        Call(host, "HotseatClientBoundaryPrefix", client); Manager.instance.client = null; Manager.GameState = null;
        Check(!(bool)Call(host, "PrivateSamplingAllowed", (object)null) && !(bool)Get(host, "_hotseatBoundaryPending"),
            "return to menu clears stale pending boundary without reading a map");
        client.clientType = "Local"; Manager.instance.client = client; Manager.GameState = gs;
        Check((bool)Call(host, "PrivateSamplingAllowed", gs), "later single-player session is not blocked by old hotseat boundary");
        Console.WriteLine("TOTAL " + passed + " passed"); return 0;
    }
}
