// Read-only game-shaped fixtures exercise the actual panel reader and journal formatter.
// Build verification is injected only into its cached proof; these are not live-game tests.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public enum PanelAbility { Dash, Static }
public static class PanelNativeAbility { public enum Type { Dash, Static } }
public class UnitData
{
    public string type = "Transportship";
    public UnitData promotesTo;
    public int getPromotionLimit(PlayerState player, GameState gs) { return player.Limit; }
    public int health = 150, attack = 25, defence = 35, movement = 3, range = 2, cost = 8;
    public List<object> unitAbilities = new List<object>();
}
public struct WorldCoordinates
{
    public int X, Y;
    public WorldCoordinates(int x, int y) { X = x; Y = y; }
    public override string ToString() { return "(" + X + "," + Y + ")"; }
}
public class UnitState
{
    public UnitData UnitData = new UnitData();
    public int id = 51, owner = 2, health = 35, xp = 2, promotionLevel, leader = 555, follower = 666;
    public string type = "Transportship", coordinates = "(3,4)";
    public WorldCoordinates home = new WorldCoordinates(91, 92);
    public UnitState passengerUnit;
    public bool moved, attacked;
    public List<string> effects = new List<string> { "SecretInternalEffect" };
    public bool CanMove() { return !moved; }
    public bool CanAttack() { return !attacked; }
    public int GetFullCost(GameState gs) { return 8; }
}
public class PlayerState { public int Id, Limit; }
public class GameState
{
    public MapData Map = new MapData();
    public object Players = new List<PlayerState> {
        new PlayerState { Id = 1, Limit = 3 }, new PlayerState { Id = 2, Limit = 2 } };
}
public class MapData { public object Tile; public bool Throw; }
public class PanelTile { public object improvement; }
public class PanelCity
{
    public string type = "City";
    public string name = "Visible home label";
    public int level { get { throw new Exception("city level must never be read"); } }
    public object owner { get { throw new Exception("city owner must never be read"); } }
    public object coordinates { get { throw new Exception("city coordinates must never be read"); } }
}
public class ThrowingNameCity
{
    public string type = "City";
    public string name { get { throw new Exception("unreadable city name"); } }
}
public class MissingNameCity { public string type = "City"; }
public class ThrowingPassenger
{
    public object passengerUnit { get { throw new Exception("unreadable passenger"); } }
}
public class NullPassenger { public object passengerUnit { get { return null; } } }
public class MalformedPanelUnit
{
    public object xp = 2.6m, promotionLevel = 0.2m, health = 35.5m;
    public object passengerUnit;
}
public class MalformedPanelData
{
    public object attack = 25.5m, defence = "35", movement = true, range = PanelAbility.Static;
    public List<object> unitAbilities = new List<object>();
}
public class InterruptedPanelPlayers : IEnumerable
{
    public IEnumerator GetEnumerator() { yield return new PlayerState { Id = 1, Limit = 3 }; throw new Exception("roster suffix unavailable"); }
}
public static class MapDataExtensions
{
    public static int Calls;
    public static WorldCoordinates LastHome;
    public static object GetTile(MapData map, WorldCoordinates home)
    {
        Calls++; LastHome = home;
        if (map.Throw) throw new Exception("native map read failed");
        return map.Tile;
    }
}
public static class UnitDataExtensions
{
    public static bool? StaticOverride;
    public static bool ThrowAbility;
    public static int AbilityCalls;
    public static bool HasAbility(UnitData data, PanelNativeAbility.Type ability)
    {
        AbilityCalls++;
        if (ThrowAbility || data == null || data.unitAbilities == null) throw new Exception("unreadable native ability");
        if (StaticOverride.HasValue) return StaticOverride.Value;
        foreach (var value in data.unitAbilities)
        {
            if (value == null || !(value is PanelAbility)) throw new Exception("unreadable native ability value");
            if (value.ToString() == ability.ToString()) return true;
        }
        return false;
    }
    public static int GetAttack(UnitData data) { return data.attack * 10; }
    public static int GetAttack(UnitState unit, GameState gs) { return 375; }
    public static int GetMaxHealth(UnitState unit, GameState gs) { return 150; }
    public static int GetDefence(UnitState unit, GameState gs) { return 35; }
    public static int GetDefenceBonus(UnitState unit, GameState gs) { return 15; }
    public static int GetMovement(UnitState unit, GameState gs) { return 1; }
    public static int GetRange(UnitState unit, GameState gs) { return 2; }
}

internal static class UnitPanelTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly Type Host = typeof(BridgeMod);
    private static readonly object Instance = Activator.CreateInstance(Host);
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static int passed;
    private static void Check(bool value, string label)
    { if (!value) throw new Exception("FAIL " + label); passed++; Console.WriteLine("PASS " + label); }
    private static object Call(string method, params object[] args)
    { return Host.GetMethod(method, F).Invoke(Instance, args); }
    private static Dictionary<string, object> Parse(string raw)
    { return Json.Deserialize<Dictionary<string, object>>(raw); }
    private static Dictionary<string, object> Row(object value) { return (Dictionary<string, object>)value; }
    private static string PanelRaw(object unit, GameState state, UnitData data, bool verified)
    { return (string)Call("URPublicInfo", unit, state, data, verified, null); }
    private static Dictionary<string, object> Panel(object unit, GameState state, UnitData data, bool verified)
    { return Parse(PanelRaw(unit, state, data, verified)); }
    private static object[] Home(object unit, GameState state)
    {
        var args = new object[] { unit, state, null, null };
        Host.GetMethod("URPanelHomeName", F).Invoke(null, args); return args;
    }
    private static object[] Progress(object unit, UnitData data, bool verified)
    {
        var args = new object[] { unit, data, verified, null, null, null, null };
        Host.GetMethod("URPanelProgress", F).Invoke(null, args); return args;
    }
    private static object View(string info)
    {
        var v = Activator.CreateInstance(Host.GetNestedType("CombatUnitView", F), true);
        foreach (var pair in new Dictionary<string, object> {
            { "Id", "51" }, { "Owner", "2" }, { "Type", "Transportship" },
            { "Coord", "(3,4)" }, { "Health", 35 }, { "PublicInfo", info } })
            v.GetType().GetField(pair.Key, F).SetValue(v, pair.Value);
        return v;
    }
    private static object Frame(object view)
    {
        var frame = Activator.CreateInstance(Host.GetNestedType("CombatFrame", F), true);
        frame.GetType().GetField("Me", F).SetValue(frame, "1");
        ((IDictionary)frame.GetType().GetField("Units", F).GetValue(frame)).Add("51", view);
        return frame;
    }
    public static int Main()
    {
        try { Run(); Console.WriteLine("PASS total " + passed); return 0; }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
    private static void Run()
    {
        var data = new UnitData();
        var unit = new UnitState { UnitData = data, passengerUnit = new UnitState { id = 987654, type = "Knight" } };
        var gs = new GameState(); gs.Map.Tile = new PanelTile { improvement = new PanelCity() };
        string raw = PanelRaw(unit, gs, data, true);
        var panel = Parse(raw); var stats = Row(panel["stats"]);
        Check((string)panel["status"] == "ok" && (string)panel["schema"] == "unit-public-info-1", "complete native-shaped panel is available");
        Check((string)panel["passengerType"] == "Knight" && (string)panel["passengerStatus"] == "ok", "visible enemy ship discloses passenger type");
        Check(!raw.Contains("987654") && !raw.Contains("passengerId") && !raw.Contains("leader") && !raw.Contains("follower"), "public passenger projection withholds identity and linkage");
        Check((string)panel["homeCityName"] == "Visible home label" && (string)panel["homeCityStatus"] == "ok", "home city uses only displayed native name");
        Check(!raw.Contains("91") && !raw.Contains("92") && !raw.Contains("level") && !raw.Contains("owner") && !raw.Contains("coordinates"), "home coordinates and city internals never enter panel JSON");
        Check(MapDataExtensions.LastHome.X == 91 && MapDataExtensions.LastHome.Y == 92, "home lookup follows the unit native home coordinate without a map search");
        Check(Convert.ToInt32(panel["kills"]) == 2 && !(bool)panel["veteran"], "non-veteran panel displays kills and false veteran state");
        Check((string)panel["progressKind"] == "veteran" && Convert.ToInt32(panel["progressLimit"]) == 2
            && panel["progressTarget"] == null, "progress denominator follows this unit owner rather than the controlled player");
        data.promotesTo = new UnitData { type = "Knight" };
        var evolve = Panel(unit, gs, data, true);
        Check((string)evolve["progressKind"] == "evolve" && (string)evolve["progressTarget"] == "Knight"
            && Convert.ToInt32(evolve["progressLimit"]) == 2, "evolution progress includes the public target type and native limit");
        data.promotesTo = null;
        var savedPlayers = gs.Players; gs.Players = null;
        var missingPlayer = Panel(unit, gs, data, true);
        Check((string)missingPlayer["status"] == "partial" && missingPlayer["progressLimit"] == null
            && Convert.ToInt32(missingPlayer["kills"]) == 2, "unreadable progress denominator does not erase an actually displayed numerator");
        gs.Players = savedPlayers;
        gs.Players = new InterruptedPanelPlayers();
        try
        {
            var interrupted = Panel(unit, gs, data, true);
            Check((string)interrupted["status"] == "partial" && interrupted["progressLimit"] == null
                && Convert.ToInt32(interrupted["kills"]) == 2, "interrupted owner roster preserves readable panel and unknown progress limit");
        }
        catch (TargetInvocationException) { Check(false, "interrupted owner roster must not abort whole panel"); }
        gs.Players = savedPlayers;
        Check(Convert.ToDecimal(stats["attack"]) == 2.5m && Convert.ToDecimal(stats["defence"]) == 3.5m, "base combat values use the panel scale");
        Check(Convert.ToDecimal(stats["attackBonus"]) == 1.25m, "attack bonus subtracts native UnitData attack then divides by 100");
        Check(Convert.ToDecimal(stats["defenceMultiplier"]) == 1.5m, "defence bonus is a multiplier in panel scale");
        Check(Convert.ToInt32(stats["movement"]) == 3 && Convert.ToInt32(stats["movementBonus"]) == -2, "movement panel keeps negative current movement bonus");
        Check(Convert.ToInt32(stats["health"]) == 4 && Convert.ToInt32(stats["maxHealth"]) == 15, "panel health follows native ceil display scale");
        var injected = new Dictionary<string, object> { { "GetMaxHealth", 1 }, { "GetAttack", 0 }, { "GetDefenceBonus", 10 }, { "GetMovement", 0 } };
        var altered = Parse((string)Call("URPublicInfo", unit, gs, data, true, injected));
        Check(Convert.ToInt32(Row(altered["stats"])["attackBonus"]) == 0 && Convert.ToInt32(Row(altered["stats"])["maxHealth"]) == 1,
            "pre-read native rules are reused and negative attack bonus is clamped");
        unit.promotionLevel = 1; unit.xp = 12345;
        var veteran = Progress(unit, data, true);
        Check(veteran[3] == null && (bool)veteran[4] && (string)veteran[5] == "not-displayed" && (string)veteran[6] == "ok", "veteran panel suppresses internal XP");
        var hiddenProgress = Panel(unit, gs, data, true);
        Check(hiddenProgress["progressKind"] == null && hiddenProgress["progressLimit"] == null
            && hiddenProgress["progressTarget"] == null, "veteran panel does not expose hidden progress details");
        data.unitAbilities.Add(PanelAbility.Static);
        var stationary = Progress(unit, data, true);
        Check(stationary[3] == null && stationary[4] == null && (string)stationary[5] == "not-displayed" && (string)stationary[6] == "not-displayed", "Static units suppress kills and veteran badges");
        data.unitAbilities.Clear(); unit.promotionLevel = 0; unit.xp = -1;
        var invalid = Progress(unit, data, true);
        Check(invalid[3] == null && (string)invalid[5] == "unavailable" && (bool)invalid[4] == false, "invalid kills remain unknown without erasing known non-veteran state");
        unit.xp = 2;
        UnitDataExtensions.StaticOverride = true;
        int abilityCalls = UnitDataExtensions.AbilityCalls;
        var nativeStationary = Progress(unit, data, true);
        Check(nativeStationary[3] == null && nativeStationary[4] == null && (string)nativeStationary[5] == "not-displayed"
            && UnitDataExtensions.AbilityCalls == abilityCalls + 1,
            "native HasAbility Static result takes precedence over enum list without Static");
        UnitDataExtensions.StaticOverride = null;
        UnitDataExtensions.ThrowAbility = true;
        var failedAbility = Progress(unit, data, true);
        Check(failedAbility[3] == null && failedAbility[4] == null && (string)failedAbility[5] == "unavailable",
            "throwing native HasAbility cannot fall back to a plausible non-Static list");
        UnitDataExtensions.ThrowAbility = false;
        data.unitAbilities = null;
        Check(Progress(unit, data, true)[4] == null, "missing ability collection cannot imply non-Static");
        data.unitAbilities = new List<object> { null };
        Check(Progress(unit, data, true)[4] == null, "unreadable ability value cannot imply non-Static");
        data.unitAbilities.Clear();
        var memberArgs = new object[] { new NullPassenger(), "passengerUnit", "sentinel" };
        bool readable = (bool)Host.GetMethod("URPanelMember", F).Invoke(null, memberArgs);
        Check(readable && memberArgs[2] == null, "successful native null is distinct from missing passenger accessor");
        memberArgs = new object[] { new ThrowingPassenger(), "passengerUnit", null };
        Check(!(bool)Host.GetMethod("URPanelMember", F).Invoke(null, memberArgs), "throwing passenger accessor remains unreadable");
        Check((string)Panel(new NullPassenger(), gs, data, true)["passengerStatus"] == "none", "known empty passenger is explicit none");
        Check((string)Panel(new ThrowingPassenger(), gs, data, true)["passengerStatus"] == "unavailable", "throwing passenger stays unavailable rather than none");
        Check((string)Panel(new object(), gs, data, true)["passengerStatus"] == "unavailable", "missing passenger stays unavailable rather than none");
        gs.Map.Tile = null;
        Check((string)Home(unit, gs)[3] == "none", "missing home tile has no displayed home name");
        gs.Map.Tile = new PanelTile { improvement = new PanelCity { type = "Farm" } };
        Check((string)Home(unit, gs)[3] == "none", "non-city home improvement cannot expose a city name");
        gs.Map.Tile = new PanelTile { improvement = new PanelCity { name = "" } };
        var h = Home(unit, gs);
        Check(h[2] == null && (string)h[3] == "none", "empty native home name is explicit none");
        gs.Map.Tile = new PanelTile { improvement = new MissingNameCity() };
        Check((string)Home(unit, gs)[3] == "unavailable", "missing city name remains unavailable");
        gs.Map.Tile = new PanelTile { improvement = new ThrowingNameCity() };
        Check((string)Home(unit, gs)[3] == "unavailable", "throwing city name remains unavailable");
        gs.Map.Throw = true;
        Check((string)Home(unit, gs)[3] == "unavailable", "failed native tile lookup remains unavailable");
        gs.Map.Throw = false; gs.Map.Tile = new PanelTile { improvement = new PanelCity() };
        int beforeCalls = MapDataExtensions.Calls;
        var unknown = Panel(unit, gs, data, false);
        Check((string)unknown["status"] == "unavailable" && unknown["passengerType"] == null && unknown["homeCityName"] == null
            && unknown["kills"] == null && unknown["veteran"] == null, "unverified build exposes no panel values");
        foreach (var stat in Row(unknown["stats"])) Check(stat.Value == null, "unverified stat remains unknown: " + stat.Key);
        Check(MapDataExtensions.Calls == beforeCalls, "unverified build does not invoke home-city lookup");
        foreach (object badNumber in new object[] { true, "2", 2.6m, 2.6d, PanelAbility.Static, (long)int.MaxValue + 1L })
            Check(Call("URPanelInteger", badNumber) == null, "strict panel integer rejects coercion from " + badNumber.GetType().Name);
        var malformed = Parse((string)Call("URPublicInfo", new MalformedPanelUnit(), gs, new MalformedPanelData(), true,
            new Dictionary<string, object> { { "GetMaxHealth", 150.5m }, { "GetAttack", 375.5m },
                { "GetDefenceBonus", "15" }, { "GetMovement", true } }));
        Check(malformed["kills"] == null && malformed["veteran"] == null
            && (string)malformed["killsStatus"] == "unavailable" && (string)malformed["veteranStatus"] == "unavailable",
            "fractional progress cannot round into plausible kills or veteran state");
        foreach (var stat in Row(malformed["stats"]))
            Check(stat.Value == null, "malformed native stat remains unknown: " + stat.Key);
        Host.GetField("URHPBuildVerified", F).SetValue(null, (bool?)true);
        Check(BridgeMod.URPublicKills(unit, gs) == 2, "shared preview progress reader exposes displayed non-veteran kills");
        data.unitAbilities.Add(PanelAbility.Static);
        Check(BridgeMod.URPublicKills(unit, gs) == null, "shared preview reader withholds Static raw XP");
        data.unitAbilities.Clear();
        var enemy = Parse((string)Call("URProperties", unit, gs, false));
        Check(Convert.ToInt32(enemy["xp"]) == 2 && enemy["promotionLevel"] == null && enemy["effects"] == null, "enemy legacy properties keep displayed kills and withhold internal promotion/effects");
        Check(enemy["relations"] == null && (string)Row(enemy["publicInfo"])["passengerType"] == "Knight", "enemy public passenger type does not unlock private relations");
        unit.promotionLevel = 1; unit.xp = 12345;
        Check(BridgeMod.URPublicKills(unit, gs) == null, "shared preview reader withholds veteran internal XP");
        enemy = Parse((string)Call("URProperties", unit, gs, false));
        Check(enemy["xp"] == null && !Json.Serialize(enemy).Contains("12345"), "enemy legacy XP cannot leak veteran internal count");
        unit.promotionLevel = 0;
        var own = Parse((string)Call("URProperties", unit, gs, true));
        Check(Convert.ToInt32(own["xp"]) == 12345 && Convert.ToInt32(own["promotionLevel"]) == 0 && own["effects"] != null,
            "own private progress and effects remain available");
        Check((string)Row(Row(own["relations"])["passenger"])["id"] == "987654", "own passenger identity remains available to settlement");
        Host.GetField("URHPBuildVerified", F).SetValue(null, (bool?)false);
        Check(BridgeMod.URPublicKills(unit, gs) == null, "shared preview reader withholds unverified build progress");
        enemy = Parse((string)Call("URProperties", unit, gs, false));
        Check(enemy["xp"] == null && enemy["promotionLevel"] == null && (string)Row(enemy["publicInfo"])["status"] == "unavailable",
            "properties integration honors failed build verification for enemy progress");
        Host.GetField("URHPBuildVerified", F).SetValue(null, (bool?)true);
        unit.xp = 2;
        string frozen = PanelRaw(unit, gs, data, true);
        var oldView = View(frozen); var before = Frame(oldView);
        unit.passengerUnit.type = "Defender"; unit.xp = 3;
        var after = Frame(View(PanelRaw(unit, gs, data, true)));
        var changes = (List<string>)Call("CRDiff", before, after, null);
        Check(changes.Count == 1, "panel-only changes create one visible unit journal difference");
        var change = Parse("{" + changes[0] + "}");
        Check((string)change["kind"] == "unit_changed" && (string)Row(Row(change["before"])["publicInfo"])["passengerType"] == "Knight"
            && (string)Row(Row(change["after"])["publicInfo"])["passengerType"] == "Defender", "journal before and after carry independent public passenger types");
        Check((string)oldView.GetType().GetField("PublicInfo", F).GetValue(oldView) == frozen
            && Convert.ToInt32(Row(Row(change["before"])["publicInfo"])["kills"]) == 2, "later native mutation cannot rewrite frozen old panel frame");
        Check(unit.health == 35 && unit.home.X == 91 && unit.home.Y == 92 && unit.passengerUnit.id == 987654, "panel and properties readers leave fixture unit state unchanged");
    }
}
