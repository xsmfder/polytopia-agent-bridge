// Native-shaped helper fixtures exercise the real production reader; no live battle is claimed.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
public struct WorldCoordinates { public int x, y; public WorldCoordinates(int a, int b) { x = a; y = b; } }
public static class Abilities { public enum Type { Splash, Stomp, Tentacles, Poison } }
public class PlayerState { public byte Id = 1; }
public class GameState { public int Version = 121; public MapData Map = new MapData(); }
public class MapData { public List<TileData> Tiles = new List<TileData>(); }
public static class GameManager { public static GameState GameState { get; set; } public static PlayerState LocalPlayer { get; set; } }
public class UnitState
{
    public uint id; public byte owner; public int health = 100, xp;
    public int promotionLevel;
    public UnitData UnitData;
    public string type; public bool attacked, moved; public WorldCoordinates coordinates;
    public bool CanAttack() { return !attacked; } public bool CanMove() { return !moved; }
    public bool HasActivePeaceTreaty(GameState gs, PlayerState player) { return owner == player.Id; }
    // Match the native transition clone: identity/flags copied; battle fields omitted.
    public void cloneState(UnitState old) { id = old.id; attacked = old.attacked; moved = old.moved; }
}
public class UnitData { }
public class TileData
{
    public WorldCoordinates coordinates; public UnitState unit; public bool revealed = true, hidden;
    public bool GetExplored(byte player) { return revealed; }
    public UnitState GetUnit(GameState gs, byte player, bool includeHidden) { return revealed && (!hidden || includeHidden) ? unit : null; }
}
public static class UnitDataExtensions
{
    public static readonly List<uint> OptionQueries = new List<uint>();
    public static bool HasAbility(UnitState u, Abilities.Type ability)
    { if (u.UnitData == null) throw new Exception("native UnitData absent"); return ability == Abilities.Type.Splash && u.type == "Bombership" || ability == Abilities.Type.Stomp && u.type == "Juggernaut"; }
    public static int GetRange(UnitState u, GameState gs) { return 3; }
    public static int GetMovement(UnitState u, GameState gs) { return 2; }
    public static List<WorldCoordinates> GetAttackOptions(UnitState u, GameState gs, int range, bool ignore)
    { OptionQueries.Add(u.id); return new List<WorldCoordinates> { new WorldCoordinates(3, 3) }; }
}
public struct BattleResults
{
    public int attackDamage, retaliationDamage; public bool shouldMoveToDefeatedEnemyTile;
    public static int HalfCalls;
    public int GetFlooredHalfAttackDamage() { HalfCalls++; return 17; }
}
public static class BattleHelpers
{
    public static UnitState LastAttacker;
    public static int Calls;
    public static BattleResults GetBattleResults(GameState gs, UnitState a, UnitState d, bool peace, bool simple)
    { Calls++; LastAttacker = a; return new BattleResults { attackDamage = 33, retaliationDamage = -1, shouldMoveToDefeatedEnemyTile = false }; }
}
public static class MapDataExtensions
{
    public static List<TileData> GetTileNeighbors(MapData map, WorldCoordinates center)
    { return map.Tiles.FindAll(t => t.coordinates.x != center.x || t.coordinates.y != center.y); }
}
public static class PathFinder
{
    public static List<WorldCoordinates> OverridePath;
    public static List<WorldCoordinates> GetPath(GameState gs, WorldCoordinates a, WorldCoordinates b, int max, UnitState u)
    { return OverridePath ?? new List<WorldCoordinates> { b, a }; }
    public static List<WorldCoordinates> GetMoveOptions(GameState gs, WorldCoordinates a, int max, UnitState u)
    { return new List<WorldCoordinates> { new WorldCoordinates(2, 2) }; }
}
internal static class CompositePreviewTests
{
    private static int passed;
    private static JavaScriptSerializer j = new JavaScriptSerializer();
    private static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL " + label); passed++; Console.WriteLine("PASS " + label); }
    private static TileData Tile(int x, int y, uint id, byte owner, string type)
    { var c = new WorldCoordinates(x, y); return new TileData { coordinates = c, unit = new UnitState { id = id, owner = owner, type = type, coordinates = c, UnitData = new UnitData() } }; }
    private static int Main(string[] args) { try { return Run(args); } catch(Exception e) { Console.WriteLine("TEST ERROR " + e.GetType().Name + " " + e.Message); if(e.InnerException != null) Console.WriteLine("INNER " + e.InnerException.GetType().Name + " " + e.InnerException.Message); return 1; } }
    private static int Run(string[] args)
    {
        var asm = Assembly.LoadFrom(args[0]); var t = asm.GetType("PolytopiaBattlePreview.BattlePreviewSnapshot", true);
        GameManager.LocalPlayer = new PlayerState(); GameManager.GameState = new GameState();
        var gs = GameManager.GameState; var ship = Tile(1, 1, 1, 1, "Bombership");
        var target = Tile(3, 3, 2, 2, "Warrior"); var visible = Tile(3, 2, 3, 2, "Defender");
        var hidden = Tile(4, 3, 4, 2, "Defender"); hidden.hidden = true;
        var ally = Tile(2, 3, 5, 1, "Warrior");
        gs.Map.Tiles.AddRange(new[] { ship, target, visible, hidden, ally });
        gs.Map.Tiles.Add(new TileData { coordinates = new WorldCoordinates(2, 2) });
        var root = j.Deserialize<Dictionary<string, object>>((string)t.GetMethod("Snapshot").Invoke(null, null));
        Check(root["schema"].ToString() == "attack-previews-3", "attack preview schema records sentinel/composite change");
        var attacker = (Dictionary<string, object>)((IList)root["attackers"])[0];
        var preview = (Dictionary<string, object>)((IList)attacker["targets"])[0];
        Check(preview["damageReceived"] == null && Convert.ToInt32(preview["nativeDamageReceived"]) == -1 && preview["wouldDieToRetaliation"] == null,
            "negative retaliation sentinel is not HP loss, zero, healing, or lethal=false");
        var splash = (Dictionary<string, object>)preview["compositeEffects"];
        var victims = (IList)splash["affectedUnits"];
        Check(victims.Count == 1 && ((Dictionary<string, object>)victims[0])["unitId"].ToString() == "3", "splash lists visible hostile neighbor and excludes hidden/friendly units");
        Check(Convert.ToInt32(((Dictionary<string, object>)victims[0])["damage"]) == 17 && BattleResults.HalfCalls > 0,
            "splash uses native half helper, not local attackDamage/2 arithmetic");
        Check(gs.Map.Tiles[0].unit.health == 100 && !gs.Map.Tiles[0].unit.attacked && hidden.unit.health == 100,
            "native preview never mutates live-shaped army or hidden unit");
        gs.Version = 120;
        root = j.Deserialize<Dictionary<string, object>>((string)t.GetMethod("Snapshot").Invoke(null, null));
        preview = (Dictionary<string, object>)((IList)((Dictionary<string, object>)((IList)root["attackers"])[0])["targets"])[0];
        Check(((Dictionary<string, object>)preview["compositeEffects"])["status"].ToString() == "unavailable", "older native rounding dispatch is not silently replaced");
        gs.Version = 121; ship.unit.type = "Juggernaut";
        ship.unit.health = 400; ship.unit.xp = 3; ship.unit.promotionLevel = 1;
        var move = j.Deserialize<Dictionary<string, object>>((string)t.GetMethod("CompositeSnapshot").Invoke(null, new object[] { "1", "(2,2)", "move" }));
        Check(move["status"].ToString() == "partial" && ((IList)move["steps"]).Count == 1, "Stomp exposes conditional destination preview rather than exact whole-action prediction");
        var effects = (Dictionary<string, object>)((Dictionary<string, object>)((IList)move["steps"])[0])["effects"];
        Check(effects["status"].ToString() == "partial" && ((IList)effects["affectedUnits"]).Count > 0,
            "native transition clone missing UnitData is repaired before ability and battle queries");
        Check(!Object.ReferenceEquals(BattleHelpers.LastAttacker, ship.unit)
            && BattleHelpers.LastAttacker.owner == 1 && BattleHelpers.LastAttacker.health == 400
            && BattleHelpers.LastAttacker.xp == 3 && BattleHelpers.LastAttacker.promotionLevel == 1
            && BattleHelpers.LastAttacker.type == "Juggernaut" && BattleHelpers.LastAttacker.UnitData == ship.unit.UnitData,
            "independent preview actor retains actual battle and ability fields omitted by cloneState");
        Check(effects.ContainsKey("conditional") && ship.unit.coordinates.x == 1 && ship.unit.coordinates.y == 1,
            "future actor position belongs to an independent native copy and conditional assumptions are explicit");
        Check(((Dictionary<string, object>)((IList)move["steps"])[0])["coords"].ToString() == "(2,2)",
            "native destination-first path is normalized before evaluating Stomp");
        PathFinder.OverridePath = new List<WorldCoordinates> { new WorldCoordinates(2, 2), new WorldCoordinates(2, 1), new WorldCoordinates(1, 1) };
        move = j.Deserialize<Dictionary<string, object>>((string)t.GetMethod("CompositeSnapshot").Invoke(null, new object[] { "1", "(2,2)", "move" }));
        Check(((IList)move["steps"]).Count == 2
            && ((Dictionary<string, object>)((IList)move["steps"])[0])["coords"].ToString() == "(2,1)"
            && ((Dictionary<string, object>)((IList)move["steps"])[1])["coords"].ToString() == "(2,2)",
            "destination-first multi-step path exposes intermediate positions in travel order");
        Check(PathFinder.OverridePath[0].x == 2 && PathFinder.OverridePath[0].y == 2 && ship.unit.coordinates.x == 1,
            "normalizing preview leaves native-shaped path and live actor unchanged");
        var intermediate = (Dictionary<string, object>)((Dictionary<string, object>)((IList)move["steps"])[0])["effects"];
        var destination = (Dictionary<string, object>)((Dictionary<string, object>)((IList)move["steps"])[1])["effects"];
        Check(intermediate["status"].ToString() == "not-applicable" && ((IList)intermediate["affectedUnits"]).Count == 0
            && destination["effect"].ToString() == "Stomp-or-Tentacles-at-destination"
            && ((IList)destination["affectedUnits"]).Count > 0,
            "multi-tile movement predicts area damage only at destination and never sums intermediate hits");
        PathFinder.OverridePath = new List<WorldCoordinates> { new WorldCoordinates(1, 1), new WorldCoordinates(2, 2) };
        move = j.Deserialize<Dictionary<string, object>>((string)t.GetMethod("CompositeSnapshot").Invoke(null, new object[] { "1", "(2,2)", "move" }));
        Check(((IList)move["steps"]).Count == 1, "origin-first helper remains supported with verified endpoints");
        PathFinder.OverridePath = new List<WorldCoordinates> { new WorldCoordinates(9, 9), new WorldCoordinates(2, 2) };
        move = j.Deserialize<Dictionary<string, object>>((string)t.GetMethod("CompositeSnapshot").Invoke(null, new object[] { "1", "(2,2)", "move" }));
        Check(move["status"].ToString() == "unavailable", "malformed native path endpoints are rejected");
        PathFinder.OverridePath = null;
        var refused = j.Deserialize<Dictionary<string, object>>((string)t.GetMethod("CompositeSnapshot").Invoke(null, new object[] { "1", "(3,3)", "move" }));
        Check(refused["status"].ToString() == "unavailable", "destination absent from native move menu is not advertised as a legal skill");
        var beforeCounters = asm.GetType("PolytopiaAgent.BridgeMod").GetField("UnitFilter", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var counter = beforeCounters.GetType().GetField("Attempted"); int beforeCount = (int)counter.GetValue(beforeCounters);
        t.GetMethod("CompositeSnapshot").Invoke(null, new object[] { "1", "(2,2)", "move" });
        Check((int)counter.GetValue(beforeCounters) == beforeCount, "composite query restores shared observation filter counters");
        UnitDataExtensions.OptionQueries.Clear();
        root = j.Deserialize<Dictionary<string, object>>((string)t.GetMethod("Query").Invoke(null, new object[] { "1", "(3,3)" }));
        Check(((IList)root["attackers"]).Count == 1 && UnitDataExtensions.OptionQueries.Count == 1
            && UnitDataExtensions.OptionQueries[0] == 1, "selected own attacker skips other armies native target computation");
        int calls = BattleHelpers.Calls;
        root = j.Deserialize<Dictionary<string, object>>((string)t.GetMethod("Query").Invoke(null, new object[] { "1", "(9,9)" }));
        attacker = (Dictionary<string, object>)((IList)root["attackers"])[0];
        Check(attacker["status"].ToString() == "no-target" && ((IList)attacker["targets"]).Count == 0
            && calls == BattleHelpers.Calls, "target absent from native menu never invokes battle prediction");
        root = j.Deserialize<Dictionary<string, object>>((string)t.GetMethod("Query").Invoke(null, new object[] { "2", "" }));
        Check(root["status"].ToString() == "unavailable" && ((IList)root["attackers"]).Count == 0,
            "enemy unit ID never becomes an attacker query");
        target.revealed = false;
        root = j.Deserialize<Dictionary<string, object>>((string)t.GetMethod("Query").Invoke(null, new object[] { "1", "(3,3)" }));
        attacker = (Dictionary<string, object>)((IList)root["attackers"])[0];
        Check(root["status"].ToString() == "partial" && ((IList)attacker["targets"]).Count == 0
            && calls == BattleHelpers.Calls, "unexplored target is omitted before reading defender or predicting damage");
        target.revealed = true;
        var bridge = asm.GetType("PolytopiaAgent.BridgeMod",true);
        var bind = bridge.GetMethod("BindAttackPreviewResult",BindingFlags.Static|BindingFlags.NonPublic);
        root = j.Deserialize<Dictionary<string, object>>((string)bind.Invoke(null,new object[] {
            "{\"status\":\"partial\",\"controlledPlayerId\":1,\"attackers\":[]}","ep",(byte)1,"stream",7L,"1","(3,3)" }));
        Check((bool)root["ok"] && root["episodeId"].ToString()=="ep" && root["streamId"].ToString()=="stream"
            && Convert.ToInt32(root["stateVersion"])==7 && root["status"].ToString()=="partial",
            "formal preview binds native partial data to exact episode seat stream and version");
        root = j.Deserialize<Dictionary<string, object>>((string)bind.Invoke(null,new object[] {
            "{\"status\":\"ok\",\"controlledPlayerId\":2,\"attackers\":[]}","ep",(byte)1,"stream",7L,null,null }));
        Check(!(bool)root["ok"] && root["errorCode"].ToString()=="PREVIEW_IDENTITY_CHANGED",
            "native preview from another local identity is refused");
        var allow = bridge.GetMethod("AdapterDecision",BindingFlags.Static|BindingFlags.NonPublic);
        Check(allow.Invoke(null,new object[] {"attack_preview","","","",""}).ToString()=="allow",
            "formal attack preview is available without audit override");
        Console.WriteLine("ALL PASS " + passed); return 0;
    }
}
