using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaCityOps;

// Game-shaped fixtures exercise the public production snapshot and adjacency query.
public static class GameManager
{
    public static GameState GameState = new GameState();
    public static PlayerState LocalPlayer = GameState.PlayerStates[0];
}
public sealed class PlayerState
{
    public byte Id = 1;
    public int Currency = 7;
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
    public TileData[] Tiles = new[] { new TileData() };
}
public sealed class TileData
{
    public int owner = 1;
    public string coordinates = "(1,1)", terrain = "Field", rulingCityCoordinates = "(1,1)";
    public object improvement;
    public bool IsWater = false;
    public object Explored = true;
    public object NeededPopulation = 2;
    public bool ThrowUpgradeRules;
    public bool NativeDevelopmentHint;
    public int NativeDevelopmentHintReads;
    public object PopulationNeededToUpgradeCity()
    { if (ThrowUpgradeRules) throw new Exception("SECRET_CITY_POPULATION"); return NeededPopulation; }
    public bool CanCityBeUpgraded(GameState state)
    { NativeDevelopmentHintReads++; if (ThrowUpgradeRules) throw new Exception("SECRET_CITY_UPGRADE"); return NativeDevelopmentHint; }
    public object GetExplored(byte player) { return Explored; }
    public object GetResource(GameState state, byte player) { return null; }
    public object GetUnit(GameState state, byte player, bool hidden) { return null; }
}
public sealed class ImprovementData
{
    public enum Type { Port, City, FutureFactory, FutureMonument }
    public object type, cost = 3;
    public string displayName = "native future building";
    public bool hidden = false;
    public object[] terrainRequirements = new object[0], improvementAbilities = new object[0];
    public object[] adjacencyImprovements = new object[0], adjacencyRequirements = new object[0];
    public ImprovementData(object kind) { type = kind; }
}
public enum CityReward { CityWall }
public sealed class GameLogicData
{
    public object NativeUnlocked = new object[] { new ImprovementData(ImprovementData.Type.FutureFactory) };
    public object BuildResult = true;
    public object GetUnlockedImprovements(PlayerState player) { return NativeUnlocked; }
    public object[] GetUnlockableImprovements(PlayerState player, GameState state) { return new object[0]; }
    public ImprovementData GetImprovementData(ImprovementData.Type type)
    {
        var values = NativeUnlocked as object[];
        if (values != null) foreach (var value in values)
        {
            var data = value as ImprovementData;
            if (data != null && object.Equals(data.type, type)) return data;
        }
        return new ImprovementData(type);
    }
    public object CanBuild(GameState state, TileData tile, PlayerState player, ImprovementData improvement)
    { return BuildResult; }
}
internal static class CityCandidateSemanticsTests
{
    private sealed class NativeIndexedList
    {
        public int Count { get { return 2; } }
        public object this[int index]
        { get { if (index == 0) throw new Exception("native slot unreadable"); return new ImprovementData(ImprovementData.Type.FutureFactory); } }
    }
    private sealed class BadCountList
    {
        public object Count { get; set; }
        public object this[int index] { get { return new ImprovementData(ImprovementData.Type.FutureFactory); } }
    }
    private sealed class OverloadedIndexedList
    {
        public int Count { get { return 1; } }
        public object this[int index] { get { return new ImprovementData(ImprovementData.Type.FutureFactory); } }
        public object this[string index] { get { throw new Exception("unrelated indexer"); } }
    }
    private static int passed;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static void Check(bool value, string label)
    { if (!value) { Console.WriteLine("FAIL " + label); throw new Exception(label); } passed++; Console.WriteLine("PASS " + label); }
    private static Dictionary<string, object> Snapshot()
    { return Json.Deserialize<Dictionary<string, object>>(CityOptionsSnapshot.Snapshot()); }
    private static Dictionary<string, object> Placement(string name)
    {
        var query = Json.Deserialize<Dictionary<string, object>>(CityOptionsSnapshot.AdjacencyQuery("(1,1):" + name));
        return (Dictionary<string, object>)query["placement"];
    }
    private static IList Rows(Dictionary<string, object> value, string key) { return (IList)value[key]; }
    private sealed class CityImprovement
    { public ImprovementData.Type type = ImprovementData.Type.City; public int level = 1, population = 0; public object[] rewards = new object[0]; }
    public static int Main()
    {
        var gs = GameManager.GameState; var logic = gs.GameLogicData;
        var future = new ImprovementData(ImprovementData.Type.FutureFactory);
        logic.NativeUnlocked = new object[] { future };
        var snapshot = Snapshot();
        Check((bool)snapshot["currentUnlockedImprovementsComplete"], "complete dynamic native directory is marked complete");
        Check((bool)snapshot["tileOptionsComplete"], "all own tile predicates read successfully");
        Check(Rows(snapshot, "currentUnlockedImprovements").Count == 1 && Rows(snapshot, "tileOptions").Count == 1,
            "native future candidate passes without fixed building list");
        Check((string)((Dictionary<string, object>)Rows(snapshot, "tileOptions")[0])["improvement"] == "FutureFactory",
            "per-tile option identity comes from the actual unlocked data");
        Check((bool)Placement("futurefactory")["unlockedByTech"], "case-insensitive query keeps canonical native membership");

        logic.NativeUnlocked = new object[] { null, future, new ImprovementData(null) };
        snapshot = Snapshot();
        Check(!(bool)snapshot["currentUnlockedImprovementsComplete"] && !(bool)snapshot["tileOptionsComplete"],
            "unreadable native members mark directory and tile options incomplete");
        Check(Rows(snapshot, "currentUnlockedImprovements").Count == 1 && Rows(snapshot, "tileOptions").Count == 1,
            "partial directory preserves known legal options and omits unknown identities");
        Check((bool)Placement("FutureFactory")["unlockedByTech"], "positive membership survives unreadable peers");
        Check(Placement("FutureMonument")["unlockedByTech"] == null, "missing member in partial directory remains unknown");
        logic.NativeUnlocked = new object[] { future };
        Check(!(bool)Placement("FutureMonument")["unlockedByTech"], "complete native directory proves missing membership false");
        logic.NativeUnlocked = new NativeIndexedList();
        snapshot = Snapshot();
        Check(!(bool)snapshot["currentUnlockedImprovementsComplete"] && Rows(snapshot, "currentUnlockedImprovements").Count == 1,
            "throwing native index preserves failed slot and known positive member");
        Check(Placement("FutureMonument")["unlockedByTech"] == null, "native index failure cannot prove locked");

        foreach (object count in new object[] { true, "1", 1.5m, -1 })
        {
            logic.NativeUnlocked = new BadCountList { Count=count };
            snapshot=Snapshot();
            Check(snapshot["currentUnlockedImprovements"] == null && snapshot["currentUnlockedImprovementsComplete"] == null,
                "malformed catalog Count cannot create a complete building menu: "+count);
        }
        logic.NativeUnlocked = new OverloadedIndexedList();
        snapshot=Snapshot();
        Check((bool)snapshot["currentUnlockedImprovementsComplete"] && Rows(snapshot,"tileOptions").Count==1,
            "native integer indexer remains usable beside unrelated overload");

        logic.NativeUnlocked = new object[] { future }; logic.BuildResult = "false";
        Check(Placement("FutureFactory")["canBuild"] == null, "non-boolean native CanBuild is unknown rather than false");
        snapshot = Snapshot();
        Check(!(bool)snapshot["tileOptionsComplete"] && Rows(snapshot, "tileOptions").Count == 0,
            "unknown CanBuild withholds option and marks selection incomplete");
        logic.BuildResult = false;
        Check(!(bool)Placement("FutureFactory")["canBuild"], "native boolean false stays a known rejection");
        snapshot = Snapshot();
        Check((bool)snapshot["tileOptionsComplete"] && Rows(snapshot, "tileOptions").Count == 0,
            "known rejected candidate is a complete empty legal selection");
        logic.BuildResult = true; future.cost = null;
        snapshot = Snapshot();
        var option = (Dictionary<string, object>)Rows(snapshot, "tileOptions")[0];
        Check(option["cost"] == null && option["affordable"] == null && option["buildableNow"] == null,
            "missing native cost never becomes zero or affordable");
        future.cost = 0;
        snapshot = Snapshot(); option = (Dictionary<string, object>)Rows(snapshot, "tileOptions")[0];
        Check(Convert.ToInt32(option["cost"]) == 0 && (bool)option["affordable"], "actual native zero cost remains usable");
        gs.Map.Tiles[0].Explored = null;
        Check(!(bool)Snapshot()["tileOptionsComplete"], "unreadable explored predicate is not complete empty options");
        gs.Map.Tiles[0].Explored = true;
        logic.NativeUnlocked = null;
        snapshot = Snapshot();
        Check(snapshot["currentUnlockedImprovements"] == null && snapshot["currentUnlockedImprovementsComplete"] == null
            && snapshot["tileOptionsComplete"] == null, "unavailable directory keeps explicit null availability");
        logic.NativeUnlocked = new object[0];
        var city = gs.Map.Tiles[0]; city.improvement = new CityImprovement();
        city.NeededPopulation = 5; city.NativeDevelopmentHint = true;
        int hintReads = city.NativeDevelopmentHintReads;
        snapshot = Snapshot();
        var hintCity = (Dictionary<string, object>)Rows(snapshot, "myCities")[0];
        var semantics = (Dictionary<string, object>)snapshot["cityUpgradeSemantics"];
        Check((string)semantics["canCityBeUpgradedRole"] == "cached-native-construction-potential"
            && (string)semantics["canCityBeUpgradedPersistence"] == "not-guaranteed-across-save-resume",
            "native development hint is explicitly cached and not promised persistent");
        Check((bool)hintCity["canCityBeUpgraded"] && Convert.ToInt32(hintCity["populationNeededToUpgrade"]) == 5,
            "positive native construction potential never overwrites the actual population requirement");
        Check(city.NativeDevelopmentHintReads == hintReads + 1, "semantic annotation does not repeat native city getter");
        var pendingHint = (Dictionary<string, object>)snapshot["pendingCityReward"];
        Check((string)semantics["populationRequirementField"] == "populationNeededToUpgrade"
            && (string)semantics["executableChoiceField"] == "pendingCityReward"
            && (string)pendingHint["status"] == "none" && Rows(pendingHint, "choices").Count == 0,
            "native cached true cannot create an executable reward choice");
        city.NativeDevelopmentHint = false; snapshot = Snapshot();
        Check(Equals(((Dictionary<string, object>)Rows(snapshot, "myCities")[0])["canCityBeUpgraded"], false),
            "native false hint is retained rather than normalized to prior cached true");
        foreach (object invalid in new object[] { true, "2", 2.4 })
        {
            city.NeededPopulation = invalid; snapshot = Snapshot();
            var row = (Dictionary<string, object>)Rows(snapshot, "myCities")[0];
            Check(row["populationNeededToUpgrade"] == null,
                "native city population requirement does not coerce Boolean, string or float: " + invalid);
        }
        city.NeededPopulation = 0; snapshot = Snapshot();
        Check(Convert.ToInt32(((Dictionary<string, object>)Rows(snapshot, "myCities")[0])["populationNeededToUpgrade"]) == 0,
            "actual native zero population requirement remains known");
        city.ThrowUpgradeRules = true; snapshot = Snapshot();
        Check(((Dictionary<string, object>)Rows(snapshot, "myCities")[0])["canCityBeUpgraded"] == null
            && snapshot.ContainsKey("cityUpgradeSemantics"),
            "unreadable hint stays null while its field meaning remains available");
        string reasons = Json.Serialize(snapshot["reasonCodes"]);
        Check(reasons.Contains("populationneeded-threw") && reasons.Contains("cancitybeupgraded-threw"),
            "failed native city upgrade rules carry explicit public failure reasons");
        Check(!reasons.Contains("SECRET_CITY"), "city failure reasons contain no exception payload");
        city.ThrowUpgradeRules = false;
        city.improvement = new CityImprovement { rewards = new object[] { null } };
        var cityState = (Dictionary<string, object>)((Dictionary<string, object>)Rows(Snapshot(), "myCities")[0])["cityState"];
        Check(cityState["hasCityWall"] == null, "partial reward list without positive wall evidence cannot claim wall absence");
        city.improvement = new CityImprovement { rewards = new object[0] };
        cityState = (Dictionary<string, object>)((Dictionary<string, object>)Rows(Snapshot(), "myCities")[0])["cityState"];
        Check(Equals(cityState["hasCityWall"], false), "complete empty rewards still prove no city wall");
        city.improvement = new CityImprovement { rewards = new object[] { null, CityReward.CityWall } };
        cityState = (Dictionary<string, object>)((Dictionary<string, object>)Rows(Snapshot(), "myCities")[0])["cityState"];
        Check(Equals(cityState["hasCityWall"], true), "positive native wall evidence survives unreadable peer rewards");
        Console.WriteLine("PASS total " + passed); return 0;
    }
}
