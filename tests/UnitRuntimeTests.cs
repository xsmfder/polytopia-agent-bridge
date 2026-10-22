// Offline tests call production code against game-shaped read-only fixture objects.
// They do not claim that native action execution was tested in a live match.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;

public enum FixtureAbility { Dash, Persist, Stiff }
public class FixtureUnitData
{
    public int health = 100, attack = 20, defence = 20, movement = 2, range = 1, cost = 3;
    public List<FixtureAbility> unitAbilities = new List<FixtureAbility> { FixtureAbility.Dash, FixtureAbility.Persist };
}
public class GameState
{
    public object GameLogicData;
}
public static class FixtureGameManager
{
    public static GameState GameState { get; set; }
}
public class FixtureGameLogic
{
    public Dictionary<string, FixtureUnitData> AllUnitData = new Dictionary<string, FixtureUnitData>
        { { "Warrior", new FixtureUnitData() }, { "Scoutship", new FixtureUnitData() } };
}
public class BrokenPairDictionary : System.Collections.IEnumerable
{
    public int Count { get; set; }
    public object Keys { get; set; }
    public System.Collections.IEnumerator GetEnumerator() { throw new Exception("boxed native pairs must not be enumerated"); }
}
public enum NativeFixtureUnitType { Warrior, Scoutship }
public class NativeTypedKeys
{
    public NativeKeyEnumerator GetEnumerator() { return new NativeKeyEnumerator(); }
}
public class NativeKeyEnumerator
{
    private int position = -1;
    public bool MoveNext() { return ++position < 2; }
    public NativeFixtureUnitType Current { get { return position == 0 ? NativeFixtureUnitType.Warrior : NativeFixtureUnitType.Scoutship; } }
}
public class UnitState
{
    public FixtureUnitData UnitData = new FixtureUnitData();
    public int id = 4, owner = 1, health = 50, promotionLevel = 1, xp = 3;
    public int leader = 0, follower = 0;
    public string type = "Warrior", coordinates = "(2,3)";
    public bool moved = false, attacked = true;
    public List<string> effects = new List<string> { "Boosted" };
    public UnitState passengerUnit;
    public bool CanMove() { return !moved; }
    public bool CanAttack() { return !attacked; }
    public int GetFullCost(GameState gs) { return 8; }
}
public static class UnitDataExtensions
{
    public static int GetMaxHealth(UnitState u, GameState gs) { return 150; }
    public static int GetAttack(UnitState u, GameState gs) { return 30; }
    public static int GetDefence(UnitState u, GameState gs) { return 40; }
    public static int GetDefenceBonus(UnitState u, GameState gs) { return 150; }
    public static int GetMovement(UnitState u, GameState gs) { return 3; }
    public static int GetRange(UnitState u, GameState gs) { return 2; }
}
public class InvalidFixtureCommand
{
    public bool IsValid(GameState gs, out string error) { error = "Can't recover"; return false; }
}
public class PlayerState { }
public class TileData
{
    public UnitState unit;
    public bool hidden;
    public object coordinates;
    public UnitState GetUnit(GameState gs, byte player, bool includeHidden) { return hidden && !includeHidden ? null : unit; }
}
public struct WorldCoordinates
{
    public int X, Y;
    public WorldCoordinates(int x, int y) { X = x; Y = y; }
    public static bool Diagonal, CenterIncluded;
    public static List<WorldCoordinates> GetArea(WorldCoordinates center, int radius, bool allowDiagonal, bool includeCenter)
    {
        Diagonal = allowDiagonal; CenterIncluded = includeCenter;
        return new List<WorldCoordinates> { new WorldCoordinates(1, 0), new WorldCoordinates(0, 1), new WorldCoordinates(5, 5) };
    }
}
public static class TileDataExtensions
{
    public static bool IncludeCenter;
    public static List<TileData> Targets;
    public static List<TileData> GetHealOptions(TileData tile, byte player, GameState gs, bool includeCenter)
    {
        IncludeCenter = includeCenter;
        return Targets;
    }
}
public static class CommandUtils
{
    public static bool RequestedUnavailable = true;
    public static List<object> Menu = new List<object> { new InvalidFixtureCommand() };
    public static int Calls;
    public static List<object> GetUnitActions(GameState gs, PlayerState player, TileData tile, bool includeUnavailable)
    {
        Calls++;
        RequestedUnavailable = includeUnavailable;
        return Menu;
    }
}
public class FutureAbilityCommand
{
    public string Coordinates = "(3,4)";
    public string GetCommandType() { return "FutureAbility"; }
    public bool IsValid(GameState gs, out string error) { error = null; return true; }
}
internal static class UnitRuntimeTests
{
    private static int passed;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL " + label);
        passed++; Console.WriteLine("PASS " + label);
    }
    private static int Main(string[] args)
    {
        var asm = Assembly.LoadFrom(args[0]);
        var t = asm.GetType("PolytopiaAgent.BridgeMod", true);
        var instance = Activator.CreateInstance(t);
        var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
        var actionId = t.GetMethod("UnitRuntimeActionId", flags);
        var actionName = t.GetMethod("UnitRuntimeActionName", flags);
        Check((string)actionId.Invoke(null, new object[] { "Upgrade", "Scoutship" }) == "Upgrade:Scoutship", "upgrade branches have distinct stable IDs");
        Check((string)actionName.Invoke(null, new object[] { "Promote" }) == "promote", "veteran promotion stays distinct from ship upgrade");
        Check((string)actionName.Invoke(null, new object[] { "Upgrade" }) == "upgrade_unit", "ship upgrade uses existing native unit adapter");
        Check((string)actionName.Invoke(null, new object[] { "Attack" }) == "attack", "native menu command names are dynamic; native-bound parameters gate execution");
        Check((string)actionName.Invoke(null, new object[] { "BreakIce" }) == "break_ice", "new native action is not blocked by the old eight-action whitelist");
        var gs = new GameState();
        var unit = new UnitState { passengerUnit = new UnitState { id = 9, type = "Defender" } };
        var serializer = new JavaScriptSerializer();
        var props = t.GetMethod("URProperties", flags);
        var own = serializer.Deserialize<Dictionary<string, object>>((string)props.Invoke(instance, new object[] { unit, gs, true }));
        Check(Convert.ToInt32(own["maxHealthRaw"]) == 150, "native GetMaxHealth used instead of absent UnitState.maxHealth");
        Check(own["displayScale"] == null && (bool)own["scaleUnknown"], "unknown display scale remains explicit");
        var hpFields = t.GetMethod("URHPDisplayFields", flags);
        var verifiedHp = serializer.Deserialize<Dictionary<string, object>>((string)hpFields.Invoke(null, new object[] { 100, 150, true }));
        Check(Convert.ToInt32(verifiedHp["healthDisplay"]) == 10 && Convert.ToInt32(verifiedHp["maxHealthDisplay"]) == 15
            && Convert.ToDouble(verifiedHp["displayScale"]) == 0.1 && !(bool)verifiedHp["scaleUnknown"],
            "proven native HUD rule converts current and maximum HP only");
        var hpValue = t.GetMethod("URHPDisplayValue", flags);
        Check(Convert.ToInt32(hpValue.Invoke(null, new object[] { 35, true })) == 4
            && Convert.ToInt32(hpValue.Invoke(null, new object[] { 1, true })) == 1
            && Convert.ToInt32(hpValue.Invoke(null, new object[] { 0, true })) == 0,
            "native HP rounding is ceil after float32 scaling including small and zero health");
        Check(Convert.ToInt32(hpValue.Invoke(null, new object[] { (ushort)65535, true })) == 6554,
            "native ushort HP upper boundary uses the same HUD rule");
        var unverifiedHp = serializer.Deserialize<Dictionary<string, object>>((string)hpFields.Invoke(null, new object[] { 100, 150, false }));
        Check(unverifiedHp["healthDisplay"] == null && unverifiedHp["maxHealthDisplay"] == null
            && unverifiedHp["displayScale"] == null && (bool)unverifiedHp["scaleUnknown"],
            "unverified native build keeps display values and scale unknown");
        Check(hpValue.Invoke(null, new object[] { null, true }) == null
            && hpValue.Invoke(null, new object[] { "100", true }) == null
            && hpValue.Invoke(null, new object[] { true, true }) == null
            && hpValue.Invoke(null, new object[] { -1, true }) == null,
            "missing or invalid HP is never converted to a fabricated display zero");
        var buildProof = t.GetMethod("URHPNativeBuildVerified", flags);
        Check((bool)buildProof.Invoke(null, new object[] { args[1] }), "current GameAssembly matches an audited native HP evidence hash");
        Check(!(bool)buildProof.Invoke(null, new object[] { args[0] })
            && !(bool)buildProof.Invoke(null, new object[] { args[0] + ".missing" }),
            "different or unreadable binaries cannot authorize the HP display rule");
        var basis = (Dictionary<string, object>)own["base"];
        var effective = (Dictionary<string, object>)own["effective"];
        Check(Convert.ToInt32(basis["attackRaw"]) == 20 && Convert.ToInt32(effective["AttackRaw"]) == 30, "base and current attack are separated");
        Check((bool)own["canAttack"] == false && (bool)own["canMove"] == true, "false action permission preserved as boolean");
        Check(Convert.ToInt32(own["fullCost"]) == 8, "full cost comes from native unit method");
        var relation = (Dictionary<string, object>)own["relations"];
        Check(((Dictionary<string, object>)relation["passenger"])["id"].ToString() == "9", "native passenger identity disclosed for own ship");
        var enemy = serializer.Deserialize<Dictionary<string, object>>((string)props.Invoke(instance, new object[] { unit, gs, false }));
        Check(enemy["relations"] == null && enemy["fullCost"] == null, "opponent hidden passenger and full cost withheld");
        Check(!enemy.ContainsKey("moved") && !enemy.ContainsKey("attacked"), "opponent remaining actions are not exposed");
        var valid = t.GetMethod("URValid", flags);
        Check(object.Equals(valid.Invoke(null, new object[] { new InvalidFixtureCommand(), gs }), false), "native false validation is not treated as failed read");
        Check(valid.Invoke(null, new object[] { new object(), gs }) == null, "missing validation is unknown");
        Check(unit.health == 50 && unit.moved == false && unit.attacked == true && unit.passengerUnit.id == 9, "property queries never modify live-shaped unit state");
        gs.GameLogicData = new FixtureGameLogic();
        FixtureGameManager.GameState = gs;
        t.GetField("_gameStateProp", flags).SetValue(instance, typeof(FixtureGameManager).GetProperty("GameState"));
        var keys = serializer.Deserialize<Dictionary<string, object>>((string)t.GetMethod("CurrentUnitDataKeys", flags).Invoke(instance, null));
        Check((bool)keys["ok"] && Convert.ToInt32(keys["count"]) == 2, "unitdata reads active GameState logic without global currentVersion");
        var menu = (System.Collections.IList)t.GetMethod("URMenu", flags).Invoke(instance, new object[] { gs, new PlayerState(), new TileData() });
        Check(menu.Count == 1 && !CommandUtils.RequestedUnavailable, "menu uses exact native GetUnitActions with includeUnavailable false");
        var future = new FutureAbilityCommand();
        CommandUtils.Menu = new List<object> { future };
        var refreshed = (System.Collections.IList)t.GetMethod("URMenu", flags).Invoke(instance,
            new object[] { gs, new PlayerState(), new TileData() });
        Check(CommandUtils.Calls == 2 && refreshed.Count == 1 && ReferenceEquals(refreshed[0], future),
            "fresh native menu retains an unknown command object without a fixed candidate table or cache");
        CommandUtils.Menu = new List<object>();
        refreshed = (System.Collections.IList)t.GetMethod("URMenu", flags).Invoke(instance,
            new object[] { gs, new PlayerState(), new TileData() });
        Check(refreshed.Count == 0 && CommandUtils.Calls == 3, "later empty native menu removes the previously available ability");
        CommandUtils.Menu = null;
        Check(t.GetMethod("URMenu", flags).Invoke(instance, new object[] { gs, new PlayerState(), new TileData() }) == null,
            "unreadable native menu stays distinct from an available empty menu");
        var descriptor = t.GetMethod("URMenuActionDescriptor", flags);
        Func<string, string, bool, bool?, bool, Dictionary<string, object>> describe = (commandType, kind, readable, nativeValid, myTurn) =>
        {
            var row = descriptor.Invoke(null, new object[] { commandType, kind, "{\"Coordinates\":\"(3,4)\"}",
                readable, nativeValid, myTurn, new List<object> { future } });
            return serializer.Deserialize<Dictionary<string, object>>((string)row.GetType().GetMethod("Close").Invoke(row, null));
        };
        var futureRow = describe("FutureAbility", null, true, true, true);
        Check((bool)futureRow["executable"] && futureRow["executionAdapter"].ToString() == "native-menu-command"
            && futureRow["actionName"].ToString() == "future_ability",
            "new native ability remains executable through the generic prepared-command adapter");
        Check(!(bool)futureRow["effectAdapterSupported"] && futureRow["settlementCoverage"].ToString() == "unknown"
            && futureRow["unavailableReason"] == null,
            "unknown effect proof is diagnostic and does not falsely disable or confirm native execution");
        var recoverRow = describe("Recover", null, true, true, true);
        Check((bool)recoverRow["effectAdapterSupported"] && recoverRow["settlementCoverage"].ToString() == "action-specific-partial",
            "existing recovery proof is identified without claiming complete effect coverage");
        var opponentTurn = describe("FutureAbility", null, true, true, false);
        Check(!(bool)opponentTurn["executable"] && (bool)opponentTurn["nativeValid"]
            && opponentTurn["unavailableReason"].ToString() == "NOT_YOUR_TURN",
            "native-valid menu entry is not advertised as executable during another player's turn");
        var unreadableRow = describe("FutureAbility", null, false, true, true);
        Check(!(bool)unreadableRow["executable"] && !(bool)unreadableRow["parametersReadable"]
            && unreadableRow["unavailableReason"].ToString() == "NATIVE_COMMAND_PARAMETERS_UNAVAILABLE",
            "parameter failure keeps the candidate with an explicit execution gap");
        var unknownValidation = describe("FutureAbility", null, true, null, true);
        Check(!(bool)unknownValidation["executable"] && unknownValidation["nativeValid"] == null
            && unknownValidation["unavailableReason"].ToString() == "NATIVE_VALIDATION_UNAVAILABLE",
            "missing native validity is distinguished from native false");
        var invalidRow = describe("FutureAbility", null, true, false, true);
        Check(!(bool)invalidRow["executable"] && invalidRow["unavailableReason"].ToString() == "NATIVE_COMMAND_INVALID",
            "native false validation remains an explicit invalid candidate");
        var unknownUpgrade = describe("Upgrade", null, true, true, true);
        Check(!(bool)unknownUpgrade["executable"] && unknownUpgrade["unavailableReason"].ToString() == "UPGRADE_TYPE_UNAVAILABLE",
            "upgrade without a native branch type cannot masquerade as a usable generic command");
        var knownUpgrade = describe("Upgrade", "Scoutship", true, true, true);
        Check((bool)knownUpgrade["executable"] && knownUpgrade["actionId"].ToString() == "Upgrade:Scoutship",
            "native upgrade branch and legacy action ID survive added diagnostics");
        var actorType = t.GetNestedType("URPublicUnit", BindingFlags.NonPublic);
        var actor = Activator.CreateInstance(actorType, true);
        actorType.GetField("Tile").SetValue(actor, new TileData());
        TileDataExtensions.Targets = new List<TileData>
        {
            new TileData { unit = new UnitState { id = 10 }, coordinates = "(1,2)" },
            new TileData { unit = new UnitState { id = 11 }, coordinates = "(2,3)" },
            new TileData { hidden = true, unit = new UnitState { id = 12, owner = 2 }, coordinates = "(3,4)" }
        };
        var targets = serializer.Deserialize<object[]>((string)t.GetMethod("URTargets", flags).Invoke(instance,
            new object[] { "HealOthers", actor, gs, (byte)1 }));
        Check(targets.Length == 2 && !TileDataExtensions.IncludeCenter, "area healing exposes all native visible targets and withholds hidden target");
        var missingData = serializer.Deserialize<Dictionary<string, object>>((string)props.Invoke(instance,
            new object[] { new UnitState { UnitData = null }, new GameState(), false }));
        Check(((Dictionary<string, object>)missingData["base"])["attackRaw"] == null && missingData["abilities"] == null,
            "missing unit data yields unknown fields without throwing");
        var enemyActor = Activator.CreateInstance(actorType, true);
        actorType.GetField("Tile").SetValue(enemyActor, new TileData { coordinates = new WorldCoordinates(0, 0) });
        var ownCandidate = Activator.CreateInstance(actorType, true);
        actorType.GetField("Tile").SetValue(ownCandidate, new TileData { coordinates = new WorldCoordinates(1, 0) });
        actorType.GetField("Unit").SetValue(ownCandidate, new UnitState { id = 15 });
        actorType.GetField("Own").SetValue(ownCandidate, true);
        var unitList = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(actorType));
        unitList.Add(ownCandidate);
        var disclosed = new HashSet<string> { "(1,0)", "(0,1)" };
        var envelopeMethod = t.GetMethod("URAttackEnvelope", flags);
        var envelope = serializer.Deserialize<Dictionary<string, object>>((string)envelopeMethod.Invoke(null,
            new object[] { enemyActor, 2, disclosed, unitList }));
        Check((bool)envelope["geometryOnly"] && !(bool)envelope["nativeLegalAttackTargets"], "stationary envelope never claims native future legality");
        Check(((System.Collections.ICollection)envelope["tiles"]).Count == 2 && ((System.Collections.ICollection)envelope["ownUnitCandidates"]).Count == 1,
            "envelope publishes only disclosed tiles and candidate own units");
        Check(WorldCoordinates.Diagonal && !WorldCoordinates.CenterIncluded, "native area uses diagonal range and excludes center");
        var unknownEnvelope = serializer.Deserialize<Dictionary<string, object>>((string)envelopeMethod.Invoke(null,
            new object[] { enemyActor, null, disclosed, unitList }));
        Check(unknownEnvelope["tiles"] == null && unknownEnvelope["ownUnitCandidates"] == null,
            "unknown range produces null envelope rather than safe-looking empty list");
        foreach (object badRange in new object[] { false, true, "2", 2.1m, FixtureAbility.Dash })
        {
            var malformedRange = serializer.Deserialize<Dictionary<string, object>>((string)envelopeMethod.Invoke(null,
                new object[] { enemyActor, badRange, disclosed, unitList }));
            Check((string)malformedRange["status"] == "unknown" && malformedRange["tiles"] == null
                && malformedRange["ownUnitCandidates"] == null,
                "malformed enemy range cannot generate known geometry: " + badRange.GetType().Name);
        }
        var catalog = t.GetMethod("URUnitDataCatalog", flags);
        var boxedPairFixed = serializer.Deserialize<Dictionary<string, object>>((string)catalog.Invoke(null,
            new object[] { new BrokenPairDictionary { Count = 2, Keys = new NativeTypedKeys() } }));
        Check((bool)boxedPairFixed["ok"] && Convert.ToInt32(boxedPairFixed["count"]) == 2,
            "typed native Keys works without reading boxed dictionary KeyValuePair.Key");
        var mismatchedCount = serializer.Deserialize<Dictionary<string, object>>((string)catalog.Invoke(null,
            new object[] { new BrokenPairDictionary { Count = 55, Keys = new List<string>() } }));
        Check(!(bool)mismatchedCount["ok"] && mismatchedCount["count"] == null && mismatchedCount["types"] == null,
            "nonempty native Count plus unread keys cannot become successful empty catalog");
        var missingKeys = serializer.Deserialize<Dictionary<string, object>>((string)catalog.Invoke(null,
            new object[] { new BrokenPairDictionary { Count = 55 } }));
        Check(!(bool)missingKeys["ok"] && missingKeys["types"] == null,
            "missing native Keys channel is unknown rather than count zero");
        Console.WriteLine("passed=" + passed + " failed=0");
        return 0;
    }
}
