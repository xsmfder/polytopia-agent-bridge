// Exercise real discovery endpoints with game-shaped native menus.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;
using Il2CppPolytopia.Data;

namespace Il2CppPolytopia.Data
{
    public sealed class TechData
    {
        public enum Type { FutureTechnology }
        public Type type { get { return Type.FutureTechnology; } }
    }
    public sealed class UnitData
    {
        public enum Type { FutureUnit }
        public int cost = 3;
    }
}
public sealed class GameState
{
    public GameLogicData GameLogicData { get; set; }
    public GameState() { GameLogicData = new GameLogicData(); }
}
public sealed class PlayerState { public byte Id = 1; public bool AutoPlay { get { return false; } } }
public sealed class GameLogicData
{
    public object Research = new object[0];
    public object GetUnlockableTech(PlayerState player, GameState state) { return Research; }
    public int GetTechPrice(TechData tech, PlayerState player, GameState state) { return 4; }
    public UnitData GetUnitData(UnitData.Type type) { return new UnitData(); }
}
public sealed class TrainCommand
{
    public byte PlayerId = 1;
    public string Coordinates = "(1,1)";
    public UnitData.Type Type = UnitData.Type.FutureUnit;
    public bool IsValid(GameState state, out string error) { error = ""; return true; }
}
public sealed class TileData
{
    public string coordinates = "(1,1)";
    public UnitState unit = new UnitState();
    public object GetUnit(GameState state, byte player, bool includeHidden) { return unit; }
}
public sealed class UnitState
{
    public byte owner = 1;
    public uint id = 7;
    public bool moved = false, attacked = false;
    public bool CanMove() { return true; }
    public bool CanAttack() { return true; }
}
namespace Il2CppPolytopia
{
    public static class CommandUtils
    {
        public static object Menu = new TrainCommand[0];
        public static IList GetTrainableUnits(GameState state, PlayerState player, TileData tile, bool unavailable)
        { return (IList)Menu; }
    }
}
public static class UnitDataExtensions
{
    public static object Menu = new object[0];
    public static int GetMovement(UnitState unit, GameState state) { return 1; }
    public static int GetRange(UnitState unit, GameState state) { return 1; }
    public static object GetMovementOptions(UnitState unit, GameState state, int range) { return Menu; }
    public static object GetAttackOptions(UnitState unit, GameState state, int range, bool ignoreDiplomacy) { return Menu; }
}
public sealed class WorldCoordinates
{
    public int x = 1, y = 2;
    public override string ToString() { return "(1,2)"; }
}
public sealed class BrokenMenu : IEnumerable
{
    public object First;
    public IEnumerator GetEnumerator() { yield return First; throw new Exception("interrupted native collection"); }
}
public sealed class IndexedMenu
{
    public object Count { get; set; }
    public int FailedIndex = -1;
    public object First;
    public object this[int index]
    { get { if (index == FailedIndex) throw new Exception("unreadable slot"); return First; } }
}
internal static class CandidateDiscoveryTests
{
    private static int passed, failed;
    private static readonly BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly JavaScriptSerializer J = new JavaScriptSerializer();
    private static readonly BridgeMod Host = new BridgeMod();
    private static readonly GameState State = new GameState();
    private static readonly PlayerState Player = new PlayerState();
    private static readonly TileData Tile = new TileData();
    private static Dictionary<string, object> Call(string method, params object[] args)
    {
        try { return J.Deserialize<Dictionary<string, object>>((string)typeof(BridgeMod).GetMethod(method,F).Invoke(Host,args)); }
        catch { return new Dictionary<string, object> { { "status", "THREW" } }; }
    }
    private static void Check(bool condition,string name)
    { if (condition) passed++; else { failed++; Console.WriteLine("FAIL " + name); } }
    private static Dictionary<string,object> Research()
    { return Call("ResearchableTechs", State, Player, new HashSet<string>()); }
    private static Dictionary<string,object> Train()
    { return Call("TrainOptions", State, Player, new List<object> { Tile }); }
    private static IList Rows(Dictionary<string,object> data,string key)
    { return data.ContainsKey(key) ? data[key] as IList : null; }
    private static Dictionary<string,object> Targets(bool attack)
    {
        var visible=new HashSet<string> { "(1,2)" };
        return attack ? Call("LegalAttackTargets",State,Player,(byte)1,true,new List<object> { Tile },visible)
                      : Call("LegalMoveTargets",State,Player,(byte)1,new List<object> { Tile },visible);
    }
    public static int Main()
    {
        State.GameLogicData.Research = new object[0];
        Check((string)Research()["status"]=="ok", "complete empty research menu is ok");
        State.GameLogicData.Research = new object[] { new TechData(), null };
        var row=Research();
        Check((string)row["status"]=="partial" && Convert.ToInt32(row["count"])==2,
            "null native tech cannot silently make a complete list");
        Check((string)((Dictionary<string,object>)Rows(row,"items")[0])["tech"]=="FutureTechnology",
            "new native technology remains discoverable without a fixed table");
        State.GameLogicData.Research = new BrokenMenu { First=new TechData() };
        Check((string)Research()["status"]=="unavailable", "interrupted research enumeration does not escape");
        foreach (object count in new object[] { true, "1", 1.5m, -1 })
        {
            State.GameLogicData.Research = new IndexedMenu { Count=count, First=new TechData() };
            Check((string)Research()["status"]=="unavailable", "bad research Count is not a complete menu: "+count);
        }
        State.GameLogicData.Research = new IndexedMenu { Count=2, First=new TechData(), FailedIndex=1 };
        Check((string)Research()["status"]=="unavailable", "indexed research failure does not silently drop a tech");
        State.GameLogicData.Research = new object[] { new TechData() };
        Check((string)Research()["status"]=="ok", "normal native research returns after failure");

        Il2CppPolytopia.CommandUtils.Menu = new object[] { null, new TrainCommand() };
        row=Train();
        Check((string)row["status"]=="partial", "one partial city retains known training options");
        var city=(Dictionary<string,object>)Rows(row,"cities")[0];
        Check((bool)((Dictionary<string,object>)Rows(city,"options")[0])["trainable"], "valid dynamic future unit stays trainable beside null entry");
        Il2CppPolytopia.CommandUtils.Menu = new TrainCommand[0];
        Check((string)Train()["status"]=="ok", "normal empty training menu is complete");

        foreach (bool attack in new[] { false,true })
        {
            string kind=attack?"attack":"move";
            UnitDataExtensions.Menu = new BrokenMenu { First=new WorldCoordinates() };
            Check((string)Targets(attack)["status"]=="unavailable", kind+" interrupted menu is unavailable, not an escaping exception");
            UnitDataExtensions.Menu = new IndexedMenu { Count=2, First=new WorldCoordinates(), FailedIndex=1 };
            Check((string)Targets(attack)["status"]=="unavailable", kind+" failed index cannot publish shortened complete targets");
            UnitDataExtensions.Menu = new object[] { new WorldCoordinates() };
            row=Targets(attack);
            Check((string)row["status"]=="ok" && Rows(row,"units").Count==1, kind+" normal native target still returned");
            UnitDataExtensions.Menu = new object[0];
            Check((string)Targets(attack)["status"]=="ok", kind+" proven empty remains complete");
        }
        Console.WriteLine("passed="+passed+" failed="+failed);
        return failed==0?0:1;
    }
}
