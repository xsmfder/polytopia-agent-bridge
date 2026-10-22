using System;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using PolytopiaCityOps;

// Actual production Snapshot traverses this native-shaped board and two dynamic build candidates.
public static class GameManager
{
    public static GameState GameState = new GameState();
    public static PlayerState LocalPlayer = GameState.PlayerStates[0];
}
public sealed class PlayerState
{
    public byte Id = 1;
    public int Currency = 9;
    public object[] tasks = new object[0], builtUniqueImprovements = new object[0];
}
public sealed class GameState
{
    public GameLogicData GameLogicData { get; set; }
    public PlayerState[] PlayerStates = new[] { new PlayerState() };
    public MapData Map = new MapData();
    public object[] pendingCommandTriggers = new object[0];
    public string CurrentState = "Started";
    public GameState() { GameLogicData = new GameLogicData(); }
}
public sealed class MapData
{
    public int Width = 3, Height = 3;
    public TileData[] Tiles;
    public MapData()
    {
        var list = new List<TileData>();
        for (int x = 0; x < 3; x++) for (int y = 0; y < 3; y++)
            list.Add(new TileData { coordinates = "(" + x + "," + y + ")" });
        Tiles = list.ToArray();
    }
}
public sealed class TileData
{
    public int owner = 1, ExploredReads, ResourceReads;
    public string coordinates, terrain = "Field", rulingCityCoordinates = "(1,1)";
    public object improvement;
    public bool IsWater = false, ResourceThrows;
    public object Explored = true;
    public object GetExplored(byte player) { ExploredReads++; return Explored; }
    public object GetResource(GameState state, byte player)
    { ResourceReads++; if (ResourceThrows) throw new Exception("unreadable"); return null; }
    public object GetUnit(GameState state, byte player, bool hidden) { return null; }
}
public sealed class ImprovementData
{
    public enum Type { Port, FutureFactory, FutureMill, FutureMonument }
    public Type type;
    public int CostReads, NativeCost = 3;
    public bool FailRepeatedCost, CostUnavailable;
    public int cost { get { CostReads++; if (CostUnavailable || (FailRepeatedCost && CostReads > 1)) throw new Exception("unreadable cost"); return NativeCost; } }
    public string displayName = "native dynamic candidate";
    public bool hidden = false;
    public object[] terrainRequirements = new object[0], improvementAbilities = new object[0];
    public object[] adjacencyImprovements = new object[0], adjacencyRequirements = new object[0];
    public ImprovementData(Type kind) { type = kind; }
}
public sealed class GameLogicData
{
    public int UnlockedReads, BuildReads;
    public bool UnlockThrows;
    public ImprovementData UnlockableProbe;
    public object NativeUnlocked = new object[] { new ImprovementData(ImprovementData.Type.FutureFactory),
        new ImprovementData(ImprovementData.Type.FutureMill) };
    public object GetUnlockedImprovements(PlayerState player)
    { UnlockedReads++; if (UnlockThrows) throw new Exception("unreadable"); return NativeUnlocked; }
    public object[] GetUnlockableImprovements(PlayerState player, GameState state)
    { return UnlockableProbe == null ? new object[0] : new object[] { UnlockableProbe }; }
    public ImprovementData GetImprovementData(ImprovementData.Type kind) { return new ImprovementData(kind); }
    public bool CanBuild(GameState state, TileData tile, PlayerState player, ImprovementData improvement)
    { BuildReads++; return true; }
}
public static class ActionUtils
{
    public static int BonusReads;
    public static int GetAdjacencyBonusAt(GameState state, TileData tile, ImprovementData improvement)
    { BonusReads++; return 7; }
}
internal static class ObservationReadPerformanceTests
{
    private static int passed;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static Dictionary<string, object> Snapshot()
    { return Json.Deserialize<Dictionary<string, object>>(CityOptionsSnapshot.Snapshot()); }
    private static IList Rows(Dictionary<string, object> snapshot, string key) { return (IList)snapshot[key]; }
    private static Dictionary<string, object> CenterOption(Dictionary<string, object> snapshot)
    {
        foreach (Dictionary<string, object> option in Rows(snapshot, "tileOptions"))
            if ((string)option["coords"] == "(1,1)") return option;
        throw new Exception("center candidate missing");
    }
    private static void ResetCounts()
    {
        foreach (var tile in GameManager.GameState.Map.Tiles) { tile.ExploredReads = 0; tile.ResourceReads = 0; }
        GameManager.GameState.GameLogicData.UnlockedReads = 0;
        GameManager.GameState.GameLogicData.BuildReads = 0;
        ActionUtils.BonusReads = 0;
    }
    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    private static int Run(string[] args)
    {
        var gs = GameManager.GameState; var logic = gs.GameLogicData;
        var snapshot = Snapshot();
        int firstResources = 0, firstExplored = 0;
        foreach (var tile in gs.Map.Tiles) { firstResources += tile.ResourceReads; firstExplored += tile.ExploredReads; }
        string benchmark = "BENCH {\"tiles\":9,\"candidates\":2,\"unlockedReads\":" + logic.UnlockedReads
            + ",\"resourceReads\":" + firstResources + ",\"exploredReads\":" + firstExplored
            + ",\"optionRows\":" + Rows(snapshot, "tileOptions").Count + "}";
        if (args.Length > 0 && args[0] == "baseline") { Console.WriteLine(benchmark); return 0; }
        Check(logic.UnlockedReads == 1, "port and candidate catalog reuse one actual native unlocked list");
        Check(Rows(snapshot, "tileOptions").Count == 18 && logic.BuildReads == 18,
            "every dynamic candidate retains its own native per-tile build predicate");
        Check((bool)snapshot["tileOptionsComplete"] && (bool)snapshot["currentUnlockedImprovementsComplete"],
            "complete native observation retains completeness");
        Check(ActionUtils.BonusReads == 18, "candidate-specific native adjacency bonuses are never shared");
        foreach (var tile in gs.Map.Tiles)
        {
            Check(tile.ExploredReads == 1, "explored read once across economic and adjacency loops " + tile.coordinates);
            Check(tile.ResourceReads == 1, "filtered resource read once across option and adjacency loops " + tile.coordinates);
        }
        var adjacency = (Dictionary<string, object>)CenterOption(snapshot)["adjacency"];
        Check((string)adjacency["countStatus"] == "ok" && Convert.ToInt32(adjacency["eligibleNeighborCount"]) == 0
            && Convert.ToInt32(adjacency["nativeAdjacencyBonus"]) == 7, "visible adjacency output preserves native values");

        ResetCounts();
        gs.Map.Tiles[0].Explored = null;
        snapshot = Snapshot(); adjacency = (Dictionary<string, object>)CenterOption(snapshot)["adjacency"];
        Check(!(bool)snapshot["tileOptionsComplete"], "unknown visibility remains incomplete");
        Check((string)adjacency["countStatus"] == "incomplete" && adjacency["eligibleNeighborCount"] == null
            && adjacency["nativeAdjacencyBonus"] == null, "unknown neighbour withholds count and native bonus");
        Check(gs.Map.Tiles[0].ExploredReads == 1, "unknown explored sample is retained throughout one observation");
        Check(Rows(snapshot, "tileOptions").Count == 16, "unknown target tile is omitted but known candidates survive");

        ResetCounts(); gs.Map.Tiles[0].Explored = false;
        snapshot = Snapshot(); adjacency = (Dictionary<string, object>)CenterOption(snapshot)["adjacency"];
        Check(adjacency["eligibleNeighborCount"] == null && adjacency["nativeAdjacencyBonus"] == null,
            "actual hidden neighbour remains hidden in the next fresh observation");
        Check(gs.Map.Tiles[0].ExploredReads == 1, "hidden visibility is sampled once, without cross-request reuse");

        ResetCounts(); gs.Map.Tiles[0].Explored = true; gs.Map.Tiles[0].ResourceThrows = true;
        snapshot = Snapshot();
        Check(!(bool)snapshot["tileOptionsComplete"] && gs.Map.Tiles[0].ResourceReads == 1,
            "failed filtered resource sample stays unavailable without repeated native retries");
        adjacency = (Dictionary<string, object>)CenterOption(snapshot)["adjacency"];
        Check((string)adjacency["countStatus"] == "incomplete" && adjacency["eligibleNeighborCount"] == null
            && adjacency["nativeAdjacencyBonus"] == null,
            "failed neighbour resource predicate cannot become a zero adjacency count or native bonus");
        ResetCounts(); gs.Map.Tiles[0].ResourceThrows = false;
        logic.NativeUnlocked = new object[] { new ImprovementData(ImprovementData.Type.FutureMonument) };
        snapshot = Snapshot();
        Check(logic.UnlockedReads == 1 && Rows(snapshot, "tileOptions").Count == 9 && (bool)snapshot["tileOptionsComplete"],
            "next observation reads newly unlocked technology and recovered resources");
        Check((string)CenterOption(snapshot)["improvement"] == "FutureMonument",
            "dynamic native future candidate replaces prior candidates without cached catalog");
        ResetCounts(); logic.UnlockThrows = true;
        snapshot = Snapshot();
        Check(logic.UnlockedReads == 1 && snapshot["currentUnlockedImprovements"] == null
            && snapshot["tileOptionsComplete"] == null, "failed unlocked-list read remains unknown and is not retried within snapshot");
        logic.UnlockThrows = false;
        snapshot = Snapshot();
        Check(logic.UnlockedReads == 2 && Rows(snapshot, "tileOptions").Count == 9,
            "failed unlocked-list sample never survives the next request");
        logic.UnlockableProbe = new ImprovementData(ImprovementData.Type.FutureFactory) { FailRepeatedCost = true };
        snapshot = Snapshot(); var probeRows = Rows(snapshot, "improvementsFromUnlockableTechs");
        Check(probeRows.Count == 1 && Convert.ToInt32(((Dictionary<string, object>)probeRows[0])["cost"]) == 3
            && Equals(((Dictionary<string, object>)probeRows[0])["affordable"], true) && logic.UnlockableProbe.CostReads == 1,
            "legacy unlockable output and affordability share one real cost read rather than dropping the row on a second getter failure");
        logic.UnlockableProbe.CostReads = 0; logic.UnlockableProbe.NativeCost = 5;
        snapshot = Snapshot(); probeRows = Rows(snapshot, "improvementsFromUnlockableTechs");
        Check(Convert.ToInt32(((Dictionary<string, object>)probeRows[0])["cost"]) == 5 && logic.UnlockableProbe.CostReads == 1,
            "single-read formatting does not cache cost values across later observations");
        logic.UnlockableProbe.CostReads = 0; logic.UnlockableProbe.CostUnavailable = true;
        snapshot = Snapshot(); probeRows = Rows(snapshot, "improvementsFromUnlockableTechs");
        Check(((Dictionary<string, object>)probeRows[0])["cost"] == null && ((Dictionary<string, object>)probeRows[0])["affordable"] == null
            && logic.UnlockableProbe.CostReads == 1, "unavailable cost remains consistently unknown without a second native read");
        Console.WriteLine(benchmark);
        Console.WriteLine("PASS total " + passed); return 0;
    }
}
