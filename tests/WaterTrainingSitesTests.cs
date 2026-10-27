using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;
using Il2CppPolytopia.Data;

namespace Il2CppPolytopia.Data
{
    public sealed class UnitData
    {
        public enum Type { Raychi, Island }
        public int cost = 5;
    }
}
public sealed class WorldCoordinates
{
    public int x, y;
    public WorldCoordinates(int x, int y) { this.x=x; this.y=y; }
    public override string ToString() { return "("+x+","+y+")"; }
}
public sealed class MapData { }
public sealed class GameState
{
    public GameLogicData GameLogicData { get; set; }
    public MapData Map = new MapData();
    public GameState() { GameLogicData = new GameLogicData(); }
}
public sealed class GameLogicData { public UnitData GetUnitData(UnitData.Type type) { return new UnitData(); } }
public sealed class PlayerState { public byte Id = 1; }
public sealed class TileData
{
    public enum EffectType { Algae, Flooded }
    public WorldCoordinates coordinates;
    public byte owner=1;
    public bool water, ThrowWater;
    public int WaterReads;
    public object effects = new List<EffectType>();
    public bool IsWater { get { WaterReads++; if(ThrowWater) throw new Exception("do not inspect foreign or unexplored water");return water; } }
    public TileData(int x,int y) { coordinates=new WorldCoordinates(x,y); }
}
public sealed class TrainCommand
{
    public byte PlayerId=1;
    public WorldCoordinates Coordinates;
    public UnitData.Type Type=UnitData.Type.Raychi;
    public bool Valid=true, ThrowValidity;
    public static int ValidityCalls;
    public bool IsValid(GameState gs,out string error)
    {
        ValidityCalls++;error="";
        if(ThrowValidity)throw new Exception("native validator unavailable");
        return Valid;
    }
}
namespace Il2CppPolytopia
{
    public static class CommandUtils
    {
        public static readonly List<string> Calls=new List<string>();
        public static bool Invalid, Unknown, Duplicate, ForeignCommand;
        public static IList GetTrainableUnits(GameState gs,PlayerState player,TileData tile,bool includeUnavailable)
        {
            if(includeUnavailable)throw new Exception("discovery must use current executable menu");
            Calls.Add(tile.coordinates.ToString());
            var command=new TrainCommand { Coordinates=tile.coordinates,Valid=!Invalid,ThrowValidity=Unknown,PlayerId=(byte)(ForeignCommand?2:1) };
            return Duplicate ? new[]{command,command} : new[]{command};
        }
    }
}
public static class CommandValidation
{
    public static readonly List<string> SupportCalls=new List<string>();
    public static bool CanCitySupportUnit(GameState gs,WorldCoordinates coord) { SupportCalls.Add(coord.ToString());return true; }
}
public static class MapDataExtensions { public static int GetCityUnitCount(MapData map,WorldCoordinates coord) { return 2; } }

internal static class WaterTrainingSitesTests
{
    static readonly BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic;
    static int assertions;
    static void Check(bool condition,string label) { if(!condition)throw new Exception(label);assertions++; }
    static Dictionary<string,object> Parse(string json) { return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json); }
    static Dictionary<string,object> Discover(GameState state,PlayerState player,List<object> cities,List<BridgeMod.OwnedWaterTrainSite> sites)
    {
        return Parse((string)typeof(BridgeMod).GetMethod("TrainOptionsForSites",F).Invoke(new BridgeMod(),new object[]{state,player,cities,sites}));
    }
    static Dictionary<string,object> FirstOption(Dictionary<string,object> result)
    { return (Dictionary<string,object>)((IList)((Dictionary<string,object>)((IList)result["sites"])[0])["options"])[0]; }
    static void Reset()
    {
        Il2CppPolytopia.CommandUtils.Calls.Clear();
        Il2CppPolytopia.CommandUtils.Invalid=false;Il2CppPolytopia.CommandUtils.Unknown=false;
        Il2CppPolytopia.CommandUtils.Duplicate=false;Il2CppPolytopia.CommandUtils.ForeignCommand=false;
        TrainCommand.ValidityCalls=0;
    }
    public static int Main()
    {
        try { Run();Console.WriteLine("PASS WaterTrainingSitesTests assertions="+assertions);return 0; }
        catch(Exception e){Console.WriteLine(e);return 1;}
    }
    static void Run()
    {
        var state=new GameState();var player=new PlayerState();
        var city=new TileData(1,1);var water=new TileData(2,2){water=true};
        var algae=new TileData(3,3){effects=new List<TileData.EffectType>{TileData.EffectType.Algae}};
        var foreign=new TileData(4,4){owner=2,water=true,ThrowWater=true};
        var unseen=new TileData(5,5){water=true,ThrowWater=true};
        var neutral=new TileData(6,6){owner=0,water=true,ThrowWater=true};
        var ordinaryLand=new TileData(7,7);
        var sites=new List<BridgeMod.OwnedWaterTrainSite>();
        foreach(var tile in new[]{foreign,unseen,neutral,ordinaryLand})
        {
            var site=BridgeMod.OwnWaterTrainSite(tile,1,tile!=unseen);
            if(site!=null)sites.Add(site);
        }
        Reset();var empty=Discover(state,player,new List<object>(),sites);
        Check(Il2CppPolytopia.CommandUtils.Calls.Count==0 && ((IList)empty["sites"]).Count==0,"foreign, neutral, unexplored and non-water land never query native training");
        Check(foreign.WaterReads==0 && unseen.WaterReads==0 && neutral.WaterReads==0,"visibility and ownership gates precede water/effect inspection");
        var waterSite=BridgeMod.OwnWaterTrainSite(water,1,true);
        var algaeSite=BridgeMod.OwnWaterTrainSite(algae,1,true);
        Check(waterSite!=null && waterSite.Kind=="owned-water" && algaeSite!=null && algaeSite.Kind=="owned-algae","native water or actual typed Algae effect admits own explored site");
        Reset();var result=Discover(state,player,new List<object>(),new List<BridgeMod.OwnedWaterTrainSite>{waterSite});
        Check((string)result["status"]=="ok" && ((IList)result["cities"]).Count==0 && Convert.ToInt32(result["cityCount"])==0 && Convert.ToInt32(result["siteCount"])==1,"non-city training site does not become a city");
        Check((bool)FirstOption(result)["trainable"] && (bool)FirstOption(result)["nativeValid"] && TrainCommand.ValidityCalls==1,"positive non-city candidate requires actual native TrainCommand.IsValid");
        Reset();Il2CppPolytopia.CommandUtils.Invalid=true;
        result=Discover(state,player,new List<object>(),new List<BridgeMod.OwnedWaterTrainSite>{waterSite});
        Check(!(bool)FirstOption(result)["trainable"] && object.Equals(FirstOption(result)["nativeValid"],false),"returned but natively invalid command is never executable");
        Reset();Il2CppPolytopia.CommandUtils.Unknown=true;
        result=Discover(state,player,new List<object>(),new List<BridgeMod.OwnedWaterTrainSite>{waterSite});
        Check(!(bool)FirstOption(result)["trainable"] && FirstOption(result)["nativeValid"]==null,"unknown native validation remains unknown");
        Reset();Il2CppPolytopia.CommandUtils.ForeignCommand=true;
        result=Discover(state,player,new List<object>(),new List<BridgeMod.OwnedWaterTrainSite>{waterSite});
        Check(!(bool)FirstOption(result)["trainable"] && TrainCommand.ValidityCalls==0,"foreign command rejected before invoking its validator");
        Reset();Il2CppPolytopia.CommandUtils.Duplicate=true;
        var cityList=new List<object>{city};
        var duplicatedCity=new BridgeMod.OwnedWaterTrainSite{Tile=city,Coordinates="(1,1)",Kind="owned-water"};
        result=Discover(state,player,cityList,new List<BridgeMod.OwnedWaterTrainSite>{duplicatedCity,waterSite,waterSite,algaeSite});
        Check(Il2CppPolytopia.CommandUtils.Calls.Count==3 && ((IList)result["sites"]).Count==3 && ((IList)result["cities"]).Count==1,"same location queried once across city/water and duplicate water rows");
        Check(Convert.ToInt32(result["optionCount"])==3 && TrainCommand.ValidityCalls==3,"duplicate native commands are deduplicated");
        Check(cityList.Count==1,"training discovery never mutates the existing city-capacity roster");
        var limits=Parse((string)typeof(BridgeMod).GetMethod("TrainLimits",F).Invoke(new BridgeMod(),new object[]{state,cityList,new Dictionary<string,int>{{"(1,1)",3}}}));
        var capacity=(Dictionary<string,object>)((IList)limits["cities"])[0];
        Check(Convert.ToInt32(limits["cityCount"])==1 && Convert.ToInt32(capacity["capacity"])==4 && CommandValidation.SupportCalls.Count==1 && CommandValidation.SupportCalls[0]=="(1,1)","water discovery preserves actual city-only capacity semantics");
        string basis;
        Check(BridgeMod.ConfirmAction("1","train",null,"(2,2)","Raychi","player=1|units=|imps=",
            "player=1|units=77:1:Raychi:(2,2):100:False:False;|imps=",out basis)=="confirmed",
            "existing train effect confirmation accepts a known non-city coordinate without fabricated city state");
    }
}
