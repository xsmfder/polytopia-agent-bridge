// Production unit/menu/threat endpoints. No live game or native execution.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public sealed class GameState
{
    public MapData Map { get; set; }
    public PlayerState[] PlayerStates { get; set; }
    public int CurrentPlayerIndex { get { return 0; } }
}
public sealed class PlayerState { public byte Id = 1; }
public static class GameManager
{
    public static GameState GameState { get; set; }
    public static PlayerState LocalPlayer { get; set; }
}
public sealed class MapData { public object Tiles { get; set; } }
public sealed class UnitState
{
    public uint id;
    public byte owner;
    public string type = "Warrior";
    public UnitData UnitData = new UnitData();
}
public sealed class UnitData { public object unitAbilities = new[] { "Dash" }; }
public sealed class TileData
{
    public WorldCoordinates coordinates;
    public UnitState unit;
    public bool Explored = true, Hidden, FilterBroken;
    public int ExplorationReads, UnitReads;
    public bool GetExplored(byte player) { ExplorationReads++; return Explored; }
    public object GetUnit(GameState gs, byte player, bool hidden)
    {
        UnitReads++;
        if (!Explored) throw new Exception("unexplored unit must not be read");
        if (FilterBroken) throw new Exception("native visibility unavailable");
        return Hidden && !hidden ? null : unit;
    }
}
public sealed class WorldCoordinates
{
    public int x, y;
    public WorldCoordinates(int a,int b) { x=a; y=b; }
    public static object Area;
    public static object GetArea(WorldCoordinates center,int range,bool diagonal,bool includeCenter) { return Area; }
}
public static class UnitDataExtensions
{
    public static int GetRange(UnitState unit,GameState gs) { return 2; }
    public static int GetMovement(UnitState unit,GameState gs) { return 1; }
}
public static class CommandUtils
{
    public static object Menu;
    public static object GetUnitActions(GameState gs,PlayerState player,TileData tile,bool unavailable) { return Menu; }
}
public static class TileDataExtensions
{
    public static object Targets;
    public static object GetHealOptions(TileData tile,byte player,GameState gs,bool includeCenter) { return Targets; }
}
public sealed class NamedCommand
{
    public string Coordinates = "(0,0)";
    public string Kind;
    public string GetCommandType() { return Kind; }
    public bool IsValid(GameState gs,out string error) { error=null; return true; }
}
public sealed class InterruptedCollection : IEnumerable
{
    public object First;
    public IEnumerator GetEnumerator() { yield return First; throw new Exception("native collection interrupted"); }
}
public sealed class FailedIndexCollection
{
    public int Count { get { return 2; } }
    public object First;
    public object this[int index] { get { if (index==1) throw new Exception("native index unreadable"); return First; } }
}
internal static class UnitCollectionTests
{
    private const BindingFlags F = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static int passed,failed;
    private static void Check(bool value,string name)
    { if(value)passed++; else { failed++; Console.WriteLine("FAIL "+name); } }
    private static object Call(BridgeMod host,string name,params object[] args)
    { try { return typeof(BridgeMod).GetMethod(name,F).Invoke(host,args); } catch { return "THREW"; } }
    private static Dictionary<string,object> Parse(object raw)
    {
        try { return Json.Deserialize<Dictionary<string,object>>(raw as string ?? "{}"); }
        catch { return new Dictionary<string,object> {{"status","THREW"}}; }
    }
    private static object Actor(TileData tile)
    {
        var type=typeof(BridgeMod).GetNestedType("URPublicUnit",F);
        var actor=Activator.CreateInstance(type,true); type.GetField("Tile").SetValue(actor,tile);
        type.GetField("Unit").SetValue(actor,tile.unit); return actor;
    }
    private static Dictionary<string,object> Envelope(TileData enemy)
    {
        var type=typeof(BridgeMod).GetNestedType("URPublicUnit",F);
        var list=Activator.CreateInstance(typeof(List<>).MakeGenericType(type));
        return Parse(Call(null,"URAttackEnvelope",Actor(enemy),2,new HashSet<string> {"(0,0)","(1,0)"},list));
    }
    public static int Main()
    {
        var own=new TileData { coordinates=new WorldCoordinates(0,0),unit=new UnitState { id=7,owner=1 } };
        var enemy=new TileData { coordinates=new WorldCoordinates(1,0),unit=new UnitState { id=8,owner=2 } };
        var fog=new TileData { coordinates=new WorldCoordinates(2,0),unit=new UnitState { id=9,owner=2 },Explored=false };
        var tiles=new[] {own,enemy,fog};
        var gs=new GameState {Map=new MapData {Tiles=tiles},PlayerStates=new[] {new PlayerState()}};
        var host=new BridgeMod(); GameManager.GameState=gs; GameManager.LocalPlayer=gs.PlayerStates[0];
        foreach(var pair in new[] {new[] {"_gameStateProp","GameState"},new[] {"_mapProp","Map"},
                                  new[] {"_playersProp","PlayerStates"},new[] {"_currentPlayerIndexProp","CurrentPlayerIndex"}})
            typeof(BridgeMod).GetField(pair[0],F).SetValue(host,(pair[0]=="_gameStateProp"?typeof(GameManager):typeof(GameState)).GetProperty(pair[1]));
        typeof(BridgeMod).GetField("_gameManagerType",F).SetValue(host,typeof(GameManager));
        WorldCoordinates.Area=new[] {new WorldCoordinates(0,0),new WorldCoordinates(2,0)};
        var threat=Parse(Call(host,"VisibleThreatSnapshot"));
        Console.WriteLine("MEASURE exploration reads="+own.ExplorationReads+"/"+enemy.ExplorationReads+"/"+fog.ExplorationReads
            +"; visible-unit filter reads="+own.UnitReads+"/"+enemy.UnitReads+"/"+fog.UnitReads);
        Check(own.ExplorationReads==1 && enemy.ExplorationReads==1 && fog.ExplorationReads==1,
            "threat query scans exploration once instead of twice per tile");
        Check(fog.UnitReads==0 && own.UnitReads==1 && enemy.UnitReads==1,"unexplored unit never accessed; visible filter still called per explored tile");
        Check((string)threat["status"]=="partial" && ((IList)threat["enemies"]).Count==1,"normal public threat stays partial and nonempty");
        var publicEnemy=(Dictionary<string,object>)((IList)threat["enemies"])[0];
        Check(publicEnemy["reachableTiles"]==null && publicEnemy["attackTargetsAfterMove"]==null,"no hidden native reachability added");
        Check(((IList)((Dictionary<string,object>)publicEnemy["stationaryRangeEnvelope"])["tiles"]).Count==1,"range geometry only contains disclosed coordinates");

        foreach(object bad in new object[] {new InterruptedCollection {First=new NamedCommand {Kind="Recover"}},
                                           new FailedIndexCollection {First=new NamedCommand {Kind="Recover"}},
                                           new object[] {new NamedCommand {Kind="Recover"},null}})
        {
            CommandUtils.Menu=bad;
            Check(Call(host,"URMenu",gs,gs.PlayerStates[0],own)==null,"bad native menu cannot return shortened complete commands: "+bad.GetType().Name);
        }
        CommandUtils.Menu=new object[0];
        Check(((IList)Call(host,"URMenu",gs,gs.PlayerStates[0],own)).Count==0,"proven empty unit menu stays empty");
        CommandUtils.Menu=new object[] {new NamedCommand {Kind="Recover"},new NamedCommand {Kind=null}};
        var menu=Parse(Call(host,"UnitActionsSnapshot",new Dictionary<string,string>()));
        Check((string)menu["status"]=="partial" && (string)((Dictionary<string,object>)((IList)menu["units"])[0])["status"]=="partial",
            "unreadable command type marks both menu and individual unit partial");

        foreach(object bad in new object[] {new InterruptedCollection {First=own},new FailedIndexCollection {First=own}})
        {
            TileDataExtensions.Targets=bad;
            Check((string)Call(host,"URTargets","HealOthers",Actor(own),gs,(byte)1)=="null","interrupted healing list stays unknown: "+bad.GetType().Name);
        }
        TileDataExtensions.Targets=new object[] {own,new TileData {coordinates=new WorldCoordinates(1,1),FilterBroken=true}};
        Check((string)Call(host,"URTargets","HealOthers",Actor(own),gs,(byte)1)=="null","failed target visibility filter cannot silently shorten healing list");
        TileDataExtensions.Targets=new object[] {own,new TileData {coordinates=new WorldCoordinates(1,1),Hidden=true,unit=new UnitState {owner=2}}};
        Check(Json.Deserialize<object[]>((string)Call(host,"URTargets","HealOthers",Actor(own),gs,(byte)1)).Length==1,"successful hidden filter still withholds hidden target");

        foreach(object bad in new object[] {new InterruptedCollection {First=new WorldCoordinates(0,0)},new FailedIndexCollection {First=new WorldCoordinates(0,0)}})
        {
            WorldCoordinates.Area=bad;
            var envelope=Envelope(enemy);
            Check(envelope.ContainsKey("status") && (string)envelope["status"]=="unknown" && envelope["tiles"]==null,
                "failed range collection cannot become known geometry: "+bad.GetType().Name);
        }
        WorldCoordinates.Area=new object[] {new WorldCoordinates(0,0),null};
        var unreadableCoordinate=Envelope(enemy);
        Check((string)unreadableCoordinate["status"]=="unknown" && unreadableCoordinate["tiles"]==null,
            "unreadable area coordinate cannot prove an empty or shortened geometry");
        foreach(object bad in new object[] {new InterruptedCollection {First="Dash"},new FailedIndexCollection {First="Dash"},new object[] {"Dash",new object()}})
            Check((string)Call(null,"URScalars",bad)=="null","unreadable ability list cannot publish missing or empty labels: "+bad.GetType().Name);
        Check((string)Call(null,"URScalars",(object)new object[0])=="[]","proven empty ability list remains empty");
        gs.Map.Tiles=new FailedIndexCollection {First=own};
        Check(Call(host,"URUnits",gs,(byte)1)==null,"failed map slot cannot yield a complete visible unit list");
        Console.WriteLine("passed="+passed+" failed="+failed);return failed==0?0:1;
    }
}
