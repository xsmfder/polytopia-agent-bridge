// Real lookup, coordinate factory and capture preparation; no game process or commands dispatched.
using System;
using System.Collections;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public sealed class PlayerState { public object Id = (byte)1; }
public sealed class GameState
{
    public MapData Data = new MapData(); public int MapReads;
    public MapData Map { get { MapReads++; return Data; } }
    public PlayerState[] PlayerStates { get; set; }
    public object CurrentPlayerIndex { get; set; }
    public object pendingCommandTriggers;
}
public sealed class MapData
{
    public object WidthValue = (ushort)2, HeightValue = (ushort)2;
    public object Width { get { return WidthValue; } }
    public object Height { get { return HeightValue; } }
    public int TileReads; public object TileValues;
    public object Tiles { get { TileReads++; return TileValues; } }
}
public sealed class UnitState
{
    public object owner = (byte)1, IdValue = 7u; public int IdReads;
    public UnitData.Type type = UnitData.Type.Raft;
    public object id { get { IdReads++; return IdValue; } }
}
public class TileData
{
    public string coordinates; public bool Visible = true; public int RawReads, FilterReads;
    public UnitState Value = new UnitState();
    public object unit { get { RawReads++; return Value; } }
    public object GetUnit(GameState state, byte player, bool hidden)
    { FilterReads++; if (hidden) throw new Exception("hidden-unit inspection"); return Visible ? Value : null; }
}
public sealed class ExploredTileData : TileData
{
    public bool ExploredValue, ThrowExplored; public int ExploredReads;
    public bool GetExplored(byte player) { ExploredReads++; if (ThrowExplored) throw new Exception("fog read failed"); return ExploredValue; }
    public bool GetExplored<T>(byte player) { throw new Exception("unbound fog getter"); }
}
public sealed class ReadExploreFixture { public string Coordinates = "(0,0)"; }
public sealed class InvalidFogTile
{
    public string coordinates = "(0,0)"; public int Reads;
    public int GetExplored(byte player) { Reads++; return 0; }
}
public sealed class WorldCoordinates
{
    public int x, y; public static int Calls; public static bool WrongResult;
    public override string ToString() { return "(" + x + "," + y + ")"; }
    public static WorldCoordinates FromIndex(int index, int width)
    { Calls++; return new WorldCoordinates {x = WrongResult ? 99 : index % width, y = index / width}; }
    public static WorldCoordinates FromIndex<T>(int index, int width) { throw new Exception("unbound generic"); }
}
public abstract class CommandBase { }
public enum CityReward { Workshop }
public sealed class CityRewardCommand : CommandBase
{
    public static int Constructors;
    public CityRewardCommand(byte player, CityReward reward, WorldCoordinates coordinates) { Constructors++; }
}
public sealed class MoveCommand : CommandBase
{
    public static int Constructors;
    public MoveCommand(byte player, UnitState unit, WorldCoordinates target) { Constructors++; }
}
public sealed class PeaceRequestResponseCommand : CommandBase
{
    public static int Constructors;
    public static bool LastAccepted;
    public PeaceRequestResponseCommand(byte player, byte other, bool accepted) { Constructors++; LastAccepted=accepted; }
}
public sealed class UnitData { public enum Type { Raft, FutureShip = 73 } }
public sealed class ImprovementData { public enum Type { City, FutureShip = 73 } }
public sealed class UpgradeCommand : CommandBase
{
    public static int Constructors; public UnitData.Type Target; public WorldCoordinates Coordinates;
    public UpgradeCommand(byte player, UnitData.Type target, WorldCoordinates coordinates)
    { Constructors++; Target = target; Coordinates = coordinates; }
}
public sealed class CaptureCommand : CommandBase
{
    public static int Constructors; public byte Player; public uint UnitId; public WorldCoordinates Coordinates;
    public static bool Throw;
    public CaptureCommand(byte player, uint id, WorldCoordinates coordinates)
    { Constructors++; if (Throw) throw new Exception("constructor entered"); Player = player; UnitId = id; Coordinates = coordinates; }
}
public sealed class NativeCommandTrigger
{
    public string type = "CityLevelUp"; public object playerId = (byte)1, opponentId = (byte)2;
    public string Value = "(0,0)"; public int CoordinateReads;
    public string Coordinates { get { CoordinateReads++; return Value; } }
}
public sealed class LowercaseCommandTrigger
{
    public string type = "CityLevelUp"; public byte playerId = 1, opponentId = 2;
    public string coordinates = "(0,0)";
}
public enum RawIdentifier { Zero, One }
public sealed class RawCommandManager
{
    public object GameState; public int Executions;
    public bool ExecuteCommand(object command) { Executions++; throw new Exception("negative tests must not dispatch"); }
    public bool ExecuteCommand(CommandBase command, out string error)
    {
        Executions++; error = null;
        var upgrade = command as UpgradeCommand;
        if (upgrade != null)
        {
            var state = (global::GameState)GameState;
            var tiles = (TileData[])state.Data.TileValues;
            tiles[upgrade.Coordinates.y * 2 + upgrade.Coordinates.x].Value.type = upgrade.Target;
        }
        return true;
    }
}
public sealed class RawClient
{
    public string clientType = "SinglePlayer"; public object GameState;
    public RawCommandManager ActionManager;
    public Guid CurrentGameId = Guid.NewGuid();
}
public sealed class RawManager
{
    public static RawManager instance { get; set; }
    public static GameState GameState { get; set; }
    public static PlayerState LocalPlayer { get { return GameState.PlayerStates[0]; } }
    public RawClient client;
}

internal static class RawActionCoordinateTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static int passed, failed;
    private static readonly BridgeMod Host = new BridgeMod();
    private static GameState State; private static TileData[] Tiles;
    private static object Call(string name, params object[] args)
    {
        var method = typeof(BridgeMod).GetMethod(name, F);
        if (method == null) return null;
        try { return method.Invoke(Host, args); }
        catch { return "THREW"; }
    }
    private static void Check(bool value, string name)
    { if (value) passed++; else { failed++; Console.WriteLine("FAIL " + name); } }
    private static void Reset()
    {
        State = new GameState {PlayerStates = new[] {new PlayerState()}, CurrentPlayerIndex = 0};
        Tiles = new[] {new TileData {coordinates="(0,0)"},new TileData {coordinates="(1,0)"},
            new TileData {coordinates="(0,1)"},new TileData {coordinates="(1,1)"}};
        State.Data.TileValues = Tiles;
        RawManager.GameState = State;
        RawManager.instance = new RawManager {client = new RawClient {GameState = State,
            ActionManager = new RawCommandManager {GameState = State}}};
        WorldCoordinates.Calls = CaptureCommand.Constructors = 0; WorldCoordinates.WrongResult = false;
        CaptureCommand.Throw = false;
        UpgradeCommand.Constructors = 0;
        MoveCommand.Constructors = CityRewardCommand.Constructors = PeaceRequestResponseCommand.Constructors = 0;
    }
    private static bool Prepare(out object command, out string code)
    {
        var method = typeof(BridgeMod).GetMethod("TryPrepareCaptureCommand", F);
        command = null; code = "missing production preparation";
        if (method == null) return false;
        object[] args = {State, "(0,0)", "(0,0)", null, null};
        bool ok = (bool)method.Invoke(Host, args); command = args[3]; code = (string)args[4]; return ok;
    }
    private static bool Upgrade(string from, string at, string kind, out object[] plan, out string code)
    {
        object[] args = {State, from, at, kind, null, null};
        bool ok = (bool)typeof(BridgeMod).GetMethod("TryPrepareUnitUpgrade", F).Invoke(Host, args);
        plan = (object[])args[4]; code = (string)args[5]; return ok;
    }
    public static int Main()
    {
        typeof(BridgeMod).GetField("_mapProp", F).SetValue(Host, typeof(GameState).GetProperty("Map"));
        typeof(BridgeMod).GetField("_playersProp", F).SetValue(Host, typeof(GameState).GetProperty("PlayerStates"));
        typeof(BridgeMod).GetField("_currentPlayerIndexProp", F).SetValue(Host, typeof(GameState).GetProperty("CurrentPlayerIndex"));
        typeof(BridgeMod).GetField("_gameStateType", F).SetValue(Host, typeof(GameState));
        typeof(BridgeMod).GetField("_gameManagerType", F).SetValue(Host, typeof(RawManager));
        typeof(BridgeMod).GetField("_gameStateProp", F).SetValue(Host, typeof(RawManager).GetProperty("GameState"));
        foreach (int[] pair in new[] {new[] {2,0},new[] {-1,1},new[] {0,-1},new[] {0,2},new[] {0,Int32.MinValue},new[] {0,Int32.MaxValue}})
        {
            Reset(); string pos = "(" + pair[0] + "," + pair[1] + ")";
            Check(Call("FindUnitAt", State, pos) == null && State.Data.TileReads == 0,
                "out-of-bounds or overflow lookup cannot alias/read any tile: " + pos);
            Check(Call("BuildCoordinates", pair[0], pair[1], State) == null && WorldCoordinates.Calls == 0,
                "out-of-bounds pair never reaches native coordinate factory: " + pos);
        }
        Reset();
        Check(Object.ReferenceEquals(Call("FindUnitAt", State, "(1,1)"), Tiles[3].Value)
            && Tiles[3].FilterReads == 1 && Tiles[3].RawReads == 0, "own unit lookup uses native visibility rather than raw field");
        var coords = Call("BuildCoordinates", 1, 1, State) as WorldCoordinates;
        Check(coords != null && coords.x == 1 && coords.y == 1 && WorldCoordinates.Calls == 1,
            "valid edge coordinate uses the actual factory once");
        Reset(); Tiles[0].Visible = false;
        Check(Call("FindUnitAt", State, "(0,0)") == null && Tiles[0].RawReads == 0,
            "hidden unit never falls back to raw tile.unit");
        Reset(); Tiles[0].Value.owner = (byte)2;
        Check(Call("FindUnitAt", State, "(0,0)") == null && Tiles[0].Value.IdReads == 0,
            "foreign unit cannot become the controlled action actor");
        Reset(); Tiles[0].coordinates = "(9,9)";
        Check(Call("TileAtCoordinates", State, 0, 0) == null && Tiles[0].FilterReads == 0 && Tiles[0].RawReads == 0,
            "array position must match requested tile coordinate before unit reads");
        foreach (object bad in new object[] {null, false, "2", 2.5m, RawIdentifier.One, -1, 0})
        {
            Reset(); State.Data.WidthValue = bad;
            Check(Call("BuildCoordinates", 0, 0, State) == null && WorldCoordinates.Calls == 0,
                "unreadable/non-native width cannot construct a coordinate");
            Reset(); State.Data.HeightValue = bad;
            Check(Call("FindUnitAt", State, "(0,0)") == null && State.Data.TileReads == 0,
                "unreadable/non-native height cannot address tiles");
        }
        Reset(); WorldCoordinates.WrongResult = true;
        Check(Call("BuildCoordinates", 0, 0, State) == null && WorldCoordinates.Calls == 1,
            "native factory mismatch is rejected without trying another overload");
        Reset(); State.Data.WidthValue = Int32.MaxValue; State.Data.HeightValue = Int32.MaxValue;
        Check(Call("BuildCoordinates", 1, 2, State) == null && WorldCoordinates.Calls == 0,
            "valid bounds with nonrepresentable linear index cannot wrap Int32");
        object command; string code;
        foreach (object bad in new object[] {null, true, false, "0", 1.5m, -1, UInt64.MaxValue, RawIdentifier.Zero})
        {
            Reset(); Tiles[0].Value.IdValue = bad;
            Check(!Prepare(out command, out code) && command == null && code == "UNIT_ID_UNAVAILABLE"
                && CaptureCommand.Constructors == 0 && WorldCoordinates.Calls == 0,
                "bad capture ID rejected before native command/coordinate construction");
            var reply = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(
                (string)Call("ExecuteGameAction", "capture", "", "", "(0,0)", "", "(0,0)", "", "", null, null));
            Check((string)reply["errorCode"] == "UNIT_ID_UNAVAILABLE" && object.Equals(reply["dispatched"], false)
                && object.Equals(reply["accepted"], false) && (string)reply["outcome"] == "not_dispatched"
                && CaptureCommand.Constructors == 0 && RawManager.instance.client.ActionManager.Executions == 0,
                "real legacy capture action returns pre-dispatch proof without constructing ID zero");
        }
        foreach (uint value in new[] {0u, 7u, UInt32.MaxValue})
        {
            Reset(); Tiles[0].Value.IdValue = value;
            Check(Prepare(out command, out code) && command is CaptureCommand && code == null
                && ((CaptureCommand)command).UnitId == value && ((CaptureCommand)command).Player == 1
                && CaptureCommand.Constructors == 1 && WorldCoordinates.Calls == 1,
                "known uint capture ID preserved exactly, including zero and upper boundary");
        }
        foreach (object actor in new object[] {(byte)2, null, true, "1"})
        {
            Reset(); Tiles[0].Value.owner = actor;
            Check(!Prepare(out command, out code) && command == null && CaptureCommand.Constructors == 0
                && Tiles[0].Value.IdReads == 0, "unknown/foreign actor rejected before unit ID access");
        }
        foreach (object actor in new object[] {(byte)2, null, true, "1"})
        {
            Reset(); var trigger = new NativeCommandTrigger {playerId = actor};
            State.pendingCommandTriggers = new object[] {trigger};
            Check(Call("BuildCoordinatesFromPendingTrigger", State) == null && trigger.CoordinateReads == 0
                && WorldCoordinates.Calls == 0, "foreign/unknown reward actor rejected before coordinates");
        }
        Reset(); var own = new NativeCommandTrigger(); State.pendingCommandTriggers = new object[] {own};
        Check(Call("BuildCoordinatesFromPendingTrigger", State) is WorldCoordinates && own.CoordinateReads == 1,
            "own pending reward uses the common bounded coordinate path");
        Reset(); State.pendingCommandTriggers = new object[] {new LowercaseCommandTrigger()};
        Check(Call("BuildCoordinatesFromPendingTrigger", State) is WorldCoordinates,
            "actual native lowercase coordinates field prepares city reward target");
        Reset(); CityRewardCommand.Constructors = 0;
        var rewardFailure = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(
            (string)Call("ExecuteGameAction", "city_reward", "Workshop", "", "", "", "", "", "", null, null));
        Check((string)rewardFailure["errorCode"] == "CITY_REWARD_COORDINATES_UNAVAILABLE"
            && object.Equals(rewardFailure["dispatched"], false) && object.Equals(rewardFailure["accepted"], false)
            && CityRewardCommand.Constructors == 0 && RawManager.instance.client.ActionManager.Executions == 0,
            "missing reward coordinates carry explicit nonexecution before constructor or native dispatch");
        Reset(); own = new NativeCommandTrigger {Value="(2,0)"}; State.pendingCommandTriggers = new object[] {own};
        Check(Call("BuildCoordinatesFromPendingTrigger", State) == null && WorldCoordinates.Calls == 0,
            "own reward cannot wrap to another city through x equal to width");
        Reset(); var peace = new NativeCommandTrigger {type="PeaceRequest"};
        State.pendingCommandTriggers = new object[] {peace};
        var pendingMethod = typeof(BridgeMod).GetMethod("FindPendingTrigger", F);
        object[] pendingArgs = {State, "PeaceRequest", null};
        Check((bool)pendingMethod.Invoke(Host, pendingArgs) && peace.CoordinateReads == 0,
            "peace request metadata does not read an unrelated map coordinate");
        Reset(); State.Data.WidthValue = "2";
        var unreadableWorld = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>((string)Call("WorldDump"));
        Check(unreadableWorld.ContainsKey("width") && unreadableWorld["width"] == null
            && ((IList)unreadableWorld["unknownFields"]).Contains("map.width"),
            "public world reports unreadable map width as null with unknown-field reason");
        Reset();
        var normalWorld = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>((string)Call("WorldDump"));
        Check(Convert.ToInt32(normalWorld["width"]) == 2, "public world preserves a real known width");
        Reset(); typeof(BridgeMod).GetField("_fixedControlledSeat", F).SetValue(Host, (byte?)2);
        Check(Call("FindUnitAt", State, "(0,0)") == null && Call("BuildCoordinates", 0, 0, State) == null
            && State.MapReads == 0, "inactive configured seat rejects lookups before any map access");
        typeof(BridgeMod).GetField("_fixedControlledSeat", F).SetValue(Host, null);
        Reset(); CaptureCommand.Throw = true;
        var failedConstruction = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(
            (string)Call("ExecuteGameAction", "capture", "", "", "(0,0)", "", "(0,0)", "", "", null, null));
        Check((string)failedConstruction["errorCode"] == "CAPTURE_CONSTRUCTION_FAILED"
            && (string)failedConstruction["outcome"] == "unknown" && !failedConstruction.ContainsKey("dispatched")
            && !failedConstruction.ContainsKey("accepted"), "entered capture constructor failure does not claim nonexecution");
        Check(CaptureCommand.Constructors == 1 && RawManager.instance.client.ActionManager.Executions == 0,
            "capture constructor failure never retries or invokes the action manager");
        object[] plan;
        foreach (string[] positions in new[] {new[] {"(0,0)", ""}, new[] {"", "(0,0)"}, new[] {"( 0 , 0 )", "(0,0)"}})
        {
            Reset();
            bool planned = Upgrade(positions[0], positions[1], "futureship", out plan, out code);
            Check(planned
                && plan[1] is UnitData.Type && (UnitData.Type)plan[1] == UnitData.Type.FutureShip,
                "upgrade plan uses native unit enum and preserves future native branch: " + code);
            Check(UpgradeCommand.Constructors == 0, "preparation never constructs or dispatches a command");
            if (!planned) continue;
            var ctor = (ConstructorInfo)Call("UniqueInvocableConstructor", typeof(UpgradeCommand), plan);
            var native = (UpgradeCommand)ctor.Invoke(plan);
            var manager = RawManager.instance.client.ActionManager;
            var executor = (MethodInfo)Call("FindCommandExecutor", manager.GetType(), native.GetType());
            object[] invoke = {executor, manager, native, null};
            Check(object.Equals(Call("InvokeCommandExecutor", invoke), true) && manager.Executions == 1
                && UpgradeCommand.Constructors == 1 && Tiles[0].Value.type == UnitData.Type.FutureShip,
                "real production binder/executor upgrades the selected unit once");
        }
        foreach (string kind in new[] {"", "City", "73", "999", "Raft, FutureShip"})
        {
            Reset();
            Check(!Upgrade("(0,0)", "", kind, out plan, out code) && code == "INVALID_UNIT_TYPE"
                && UpgradeCommand.Constructors == 0 && WorldCoordinates.Calls == 0,
                "missing/building/numeric/composite kind cannot create a unit upgrade");
        }
        Reset();
        Check(!Upgrade("(0,0)", "(1,0)", "FutureShip", out plan, out code)
            && code == "UPGRADE_COORDINATE_MISMATCH" && Tiles[0].FilterReads == 0 && Tiles[1].FilterReads == 0,
            "conflicting actor/target tiles rejected before reading either unit");
        Reset(); Tiles[0].Value.owner = (byte)2;
        Check(!Upgrade("", "(0,0)", "FutureShip", out plan, out code)
            && code == "UNIT_ACTOR_UNAVAILABLE" && UpgradeCommand.Constructors == 0,
            "at-only upgrade cannot act on foreign unit");
        Reset();
        Check(Call("UniqueInvocableConstructor", typeof(UpgradeCommand),
            new object[] {(byte)1, ImprovementData.Type.FutureShip, new WorldCoordinates()}) == null,
            "same-valued nested Type enum from improvements cannot bind unit upgrade");
        foreach (string alias in new[] {"upgrade", "upgrade_unit", "upgradeunit"})
        {
            Reset();
            var invalid = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(
                (string)Call("ExecuteGameAction", alias, "", "", "", "", "(0,0)", "City", "", null, null));
            Check((string)invalid["errorCode"] == "INVALID_UNIT_TYPE" && object.Equals(invalid["dispatched"], false)
                && UpgradeCommand.Constructors == 0 && RawManager.instance.client.ActionManager.Executions == 0,
                "actual raw alias rejects old city-upgrade interpretation before dispatch: " + alias);
            Reset();
            string actual = (string)Call("ExecuteGameAction", alias, "", "", "", "", "(0,0)", "FutureShip", "", null, null);
            Check(UpgradeCommand.Constructors == 1 && actual != "THREW",
                "actual raw alias reaches common correctly typed construction: " + alias);
            var reply = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(actual);
            Check((string)reply["errorCode"] == "ACTION_PIPELINE_BUSY" && managerExecutions() == 0,
                "offline alias honors real pipeline gate rather than bypassing dispatch conditions: " + alias);
        }
        Reset(); var explorerFrame = new BridgeMod.CombatFrame {Me="1",Complete=true}; var explorerAction = new ReadExploreFixture();
        var fogTile = new ExploredTileData {coordinates="(0,0)", ExploredValue=true};
        State.Data.TileValues = new object[] {fogTile,Tiles[1],Tiles[2],Tiles[3]};
        Check(object.Equals(Call("CRReadRuinsExplorerTile",State,explorerFrame,explorerAction),true) && fogTile.ExploredReads==1,
            "explorer proof reads exact native byte/bool fog getter once despite generic overload");
        Check(fogTile.RawReads==0 && fogTile.FilterReads==0,
            "explorer bit sampling never inspects tile unit or hidden resource fields");
        fogTile.ExploredValue = false;
        Check(object.Equals(Call("CRReadRuinsExplorerTile",State,explorerFrame,explorerAction),false),
            "native unexplored bit is known false rather than missing");
        fogTile.ThrowExplored = true;
        Check(Call("CRReadRuinsExplorerTile",State,explorerFrame,explorerAction)==null,
            "throwing explored getter remains unknown rather than false");
        Reset(); explorerAction.Coordinates="(2,0)";
        Check(Call("CRReadRuinsExplorerTile",State,explorerFrame,explorerAction)==null && State.Data.TileReads==0,
            "explorer target outside map is rejected before tile lookup");
        Reset(); explorerAction.Coordinates="(0,0)"; explorerFrame.Me="2";
        Check(Call("CRReadRuinsExplorerTile",State,explorerFrame,explorerAction)==null && State.Data.TileReads==0,
            "different player frame cannot inspect another seat's explored bit");
        explorerFrame.Me="1"; var invalidFog=new InvalidFogTile();
        State.Data.TileValues=new object[] {invalidFog,Tiles[1],Tiles[2],Tiles[3]};
        Check(Call("CRReadRuinsExplorerTile",State,explorerFrame,explorerAction)==null && invalidFog.Reads==0,
            "numeric fog getter is unavailable without invoking or coercing it");
        foreach (string name in new[] {"not_an_action", "attack"})
        {
            Reset(); var reply = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(
                (string)Call("ExecuteGameAction", name, "", "", "(0,0)", "(1,1)", "", "", "", null, null));
            Check(object.Equals(reply.ContainsKey("dispatched") ? reply["dispatched"] : null,false)
                && object.Equals(reply.ContainsKey("accepted") ? reply["accepted"] : null,false),
                "raw unknown/missing command reports actual pre-dispatch refusal: " + name);
        }
        Reset(); var moveFailure = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(
            (string)Call("ExecuteGameAction", "move", "", "", "(0,0)", "not coordinates", "", "", "", null, null));
        Check(object.Equals(moveFailure.ContainsKey("dispatched") ? moveFailure["dispatched"] : null,false)
            && MoveCommand.Constructors==0, "bad move target proves no construction or dispatch");
        foreach(string rewardName in new[] {"not_a_reward", "0", "Workshop, Workshop"})
        {
            Reset(); State.pendingCommandTriggers = new object[]{new LowercaseCommandTrigger()};
            var reply = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(
                (string)Call("ExecuteGameAction", "city_reward", rewardName, "", "", "", "", "", "", null, null));
            Check(reply.ContainsKey("errorCode") && (string)reply["errorCode"]=="INVALID_ENUM"
                && object.Equals(reply.ContainsKey("dispatched") ? reply["dispatched"] : null,false)
                && CityRewardCommand.Constructors==0, "invalid reward name never creates/dispatches a choice: " + rewardName);
        }
        Reset(); State.pendingCommandTriggers=new object[]{new NativeCommandTrigger {type="PeaceRequest"}};
        var peaceFailure=new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(
            (string)Call("ExecuteGameAction", "peace_response", "", "accept_typo", "", "", "", "", "", null, null));
        Check(peaceFailure.ContainsKey("errorCode") && (string)peaceFailure["errorCode"]=="INVALID_BOOLEAN"
            && PeaceRequestResponseCommand.Constructors==0, "invalid peace boolean is not silently accepted as true");
        foreach(string choice in new[]{"0","false","1","true"})
        {
            Reset(); State.pendingCommandTriggers=new object[]{new NativeCommandTrigger {type="PeaceRequest"}};
            Call("ExecuteGameAction", "peace_response", "", choice, "", "", "", "", "", null, null);
            Check(PeaceRequestResponseCommand.Constructors==1 && PeaceRequestResponseCommand.LastAccepted==(choice=="1"||choice=="true"),
                "actual peace constructor receives explicit wire boolean: "+choice);
        }
        Reset(); State.pendingCommandTriggers=new object[]{new LowercaseCommandTrigger()};
        Call("ExecuteGameAction", "city_reward", "workshop", "", "", "", "", "", "", null, null);
        Check(CityRewardCommand.Constructors==1,"legitimate case-insensitive native reward name still constructs once");
        Reset(); State.pendingCommandTriggers=new object[]{new LowercaseCommandTrigger()};
        Call("ChooseReward", new object(), "workshop");
        Check(CityRewardCommand.Constructors==1,"legacy named popup reward uses actual native command construction");
        Reset(); State.pendingCommandTriggers=new object[]{new LowercaseCommandTrigger()};
        var namedReward=new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string,object>>(
            (string)Call("ChooseReward", new object(), "Workshop, Workshop"));
        Check((string)namedReward["errorCode"]=="INVALID_ENUM" && CityRewardCommand.Constructors==0
            && object.Equals(namedReward["dispatched"],false),"legacy named reward cannot bypass native enum and preparation checks");
        Console.WriteLine("passed=" + passed + " failed=" + failed); return failed == 0 ? 0 : 1;
    }
    private static int managerExecutions() { return RawManager.instance.client.ActionManager.Executions; }
}
