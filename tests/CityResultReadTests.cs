// Public city snapshot and result helpers; no game commands or live process.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;
using PolytopiaCityOps;

public static class GameManager
{
    public static GameState GameState = new GameState();
    public static PlayerState LocalPlayer = GameState.PlayerStates[0];
}
public sealed class PlayerState
{
    public byte Id = 1; public int Currency = 10; public string tribe = "Imperius";
    public object[] tasks = new object[0], builtUniqueImprovements = new object[0];
    public ScoreTechList availableTech = new ScoreTechList(), unlockedTechCache = new ScoreTechList();
}
public sealed class ScoreTechList
{
    public List<object> Items = new List<object>(); public int FailAt = -1;
    public int Count { get { return Items.Count; } }
    public object this[int i] { get { if(i==FailAt)throw new Exception("unreadable technology");return Items[i]; } }
    public void Add(object value) { Items.Add(value); }
}
public sealed class GameState
{
    public GameLogicData GameLogicData { get; set; }
    public PlayerState[] PlayerStates = new[] {new PlayerState()};
    public MapData Map = new MapData(); public object[] pendingCommandTriggers = new object[0];
    public string CurrentState = "Started";
    public GameState() {GameLogicData=new GameLogicData();}
}
public sealed class MapData {public int Width=1,Height=1;public TileData[] Tiles=new[] {new TileData()};}
public sealed class TileData
{
    public byte owner=1;public string coordinates="(0,0)",terrain="Field",rulingCityCoordinates="(0,0)";
    public object improvement;public bool IsWater=false;
    public UnitState Unit = new UnitState();
    public bool GetExplored(byte p) {return true;}
    public object GetResource(GameState gs,byte p) {return null;}
    public object GetUnit(GameState gs,byte p,bool hidden) {return Unit;}
}
public sealed class UnitState {public int id=1,xp=0;public byte owner=1;public UnitData.Type type=UnitData.Type.Warrior;}
public sealed class CityRewardCommandTrigger
{
    public string type = "CityLevelUp";
    public object playerId;
    public int CoordinateReads;
    public string Coordinates { get { CoordinateReads++; return "(99,98)"; } }
}
public sealed class UnreadableRewardTrigger
{
    public string type { get { throw new Exception("trigger kind unavailable"); } }
}
public static class RewardListSource
{
    public static object Value;
    public static object Read() { return Value; }
}
public sealed class UnitData
{
    public enum Type {Warrior,FutureShipA,FutureShipB}
    public object type=Type.Warrior,cost=3;
}
public sealed class GameLogicData
{
    public object Upgrades=new UnitData[0]; public bool UpgradeThrows;
    public UnitData Data=new UnitData();
    public UnitData GetUnitData(UnitData.Type type) {return Data;}
    public object GetUnlockedUpgradesForUnit(PlayerState p,GameState gs,UnitData d)
    {if(UpgradeThrows)throw new Exception("upgrade read failed");return Upgrades;}
}
internal static class CityResultReadTests
{
    private const BindingFlags F=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static readonly JavaScriptSerializer J=new JavaScriptSerializer();
    private static int passed,failed;
    private static void Check(bool ok,string label)
    {if(ok)passed++;else {failed++;Console.WriteLine("FAIL "+label);}}
    private static Dictionary<string,object> Options()
    {
        var all=J.Deserialize<Dictionary<string,object>>(CityOptionsSnapshot.Snapshot());
        return (Dictionary<string,object>)((IList)all["myUnitOptions"])[0];
    }
    private static object Call(string method,params object[] args)
    {return typeof(BridgeMod).GetMethod(method,F).Invoke(null,args);}
    private static Dictionary<string,object> Final(string mode,bool complete)
    {
        var row=new Dictionary<string,object>();
        var fields=mode=="Perfection"?new[] {"territoryScore","cultureScore","cityScore","techScore","totalScore","difficultyBonusMultiplier","difficultyBonus","TotalScoreIncludingBonus"}
            :new[] {"battleScore","wipeScore","timeGoal","timeScore","difficultyScore","averageRating"};
        foreach(string f in fields)row[f]=0;
        return new Dictionary<string,object> {{"schema","native-final-1"},{"episodeId","ep-0123456789abcdef"},
            {"final",true},{"complete",complete},{"mode",mode},
            {"scores",new Dictionary<string,object>{{mode=="Perfection"?"scoreDetails":"playerRating",row},{"nativeFinalTotal",0}}}};
    }
    private static bool Valid(Dictionary<string,object> record)
    {return (bool)Call("ResultsRecordValid",J.Serialize(record),"ep-0123456789abcdef");}
    private static Dictionary<string,object> PendingReward(object owner, out CityRewardCommandTrigger trigger)
    {
        trigger = new CityRewardCommandTrigger { playerId = owner };
        var state = new GameState { pendingCommandTriggers = new object[] { trigger } };
        string value = (string)typeof(CityOptionsSnapshot).GetMethod("PendingCityRewardJson",F).Invoke(null,
            new object[] { state, (byte)1, null, null, null, null, new List<string>() });
        return J.Deserialize<Dictionary<string,object>>(value);
    }
    private static Dictionary<string,object> RewardCatalog(object raw)
    {
        RewardListSource.Value = raw;
        string value = (string)typeof(CityOptionsSnapshot).GetMethod("CityRewardDataJson",F).Invoke(null,
            new object[] { typeof(RewardListSource).GetMethod("Read"), null, new GameState(), 50, 3, 5 });
        return J.Deserialize<Dictionary<string,object>>(value);
    }
    public static int Main()
    {
        foreach (object raw in new object[] { new object[] { null }, new object[] { new UnreadableRewardTrigger() } })
        {
            var state = new GameState { pendingCommandTriggers = raw as object[] };
            var pendingArgs = new object[] { state, (byte)1, null, null, null, null, new List<string>() };
            string pendingJson = (string)typeof(CityOptionsSnapshot).GetMethod("PendingCityRewardJson",F).Invoke(null,pendingArgs);
            var pending = J.Deserialize<Dictionary<string,object>>(pendingJson);
            Check((string)pending["status"] == "unavailable" && pending["city"] == null,
                "unreadable reward trigger collection cannot prove no pending choice");
        }
        var freshState = new GameState { pendingCommandTriggers = null };
        string freshJson = (string)typeof(CityOptionsSnapshot).GetMethod("PendingCityRewardJson",F).Invoke(null,
            new object[] { freshState, (byte)1, null, null, null, null, new List<string>() });
        Check((string)J.Deserialize<Dictionary<string,object>>(freshJson)["status"] == "none",
            "known native null trigger collection uses the verified empty convention");
        var catalog = RewardCatalog(new object[] { "Workshop", null, "FutureNativeReward" });
        Check((string)catalog["status"] == "partial", "null reward definition cannot claim complete catalog");
        Check(Convert.ToInt32(catalog["rewardCount"]) == ((IList)catalog["rewards"]).Count
            && ((IList)catalog["rewards"]).Count == 2, "rewardCount matches actually returned known definitions");
        Check(catalog.ContainsKey("nativeRewardCount") && Convert.ToInt32(catalog["nativeRewardCount"]) == 3,
            "partial catalog keeps native slot count separate from returned count");
        Check((string)((Dictionary<string,object>)((IList)catalog["rewards"])[1])["reward"] == "FutureNativeReward",
            "future native reward definitions retained without known-name table");
        catalog = RewardCatalog(null);
        Check((string)catalog["status"] == "unavailable" && catalog["rewardCount"] == null,
            "unreadable reward definitions are not zero rewards");
        catalog = RewardCatalog(new object[0]);
        Check((string)catalog["status"] == "ok" && Convert.ToInt32(catalog["rewardCount"]) == 0,
            "proved empty reward catalog stays known zero");
        foreach (object badOwner in new object[] { null, true, 1.2m, "1" })
        {
            CityRewardCommandTrigger trigger;
            var pending = PendingReward(badOwner, out trigger);
            Check((string)pending["status"] == "unavailable" && pending["city"] == null
                && ((IList)pending["choices"]).Count == 0, "unreadable city reward owner cannot appear as own pending reward");
            Check(trigger.CoordinateReads == 0, "unknown reward owner rejected before hidden city coordinate read");
        }
        CityRewardCommandTrigger otherTrigger;
        var otherPending = PendingReward((byte)2, out otherTrigger);
        Check((string)otherPending["status"] == "none" && otherTrigger.CoordinateReads == 0,
            "known other player's reward omitted before coordinate access");
        CityRewardCommandTrigger ownTrigger;
        var ownPending = PendingReward((byte)1, out ownTrigger);
        Check((string)ownPending["city"] == "(99,98)" && ownTrigger.CoordinateReads == 1
            && (string)ownPending["status"] == "trigger-present-popup-unreadable", "known own trigger remains visible without inventing reward menu");
        var logic=GameManager.GameState.GameLogicData;
        logic.Upgrades=null;var option=Options();
        Check(option.ContainsKey("growsIntoStatus") && (string)option["growsIntoStatus"]=="unavailable" && option["growsInto"]==null,
            "unreadable upgrade catalog is null rather than empty");
        logic.Upgrades=new UnitData[0];option=Options();
        Check(option.ContainsKey("growsIntoStatus") && (string)option["growsIntoStatus"]=="ok" && ((IList)option["growsInto"]).Count==0,
            "proven empty upgrade catalog is complete");
        logic.Upgrades=new object[] {new UnitData {type=UnitData.Type.FutureShipA},null,new UnitData {type=UnitData.Type.FutureShipB}};
        option=Options();
        Check(option.ContainsKey("growsIntoStatus") && (string)option["growsIntoStatus"]=="partial" && ((IList)option["growsInto"]).Count==2,
            "partial dynamic upgrades retain both known future branches");
        logic.UpgradeThrows=true;option=Options();
        Check(option["growsInto"]==null,"throwing upgrade getter cannot claim no branches");logic.UpgradeThrows=false;
        logic.Upgrades=new[] {new UnitData {type=UnitData.Type.FutureShipA,cost=1.5m}};option=Options();
        var upgrade=(Dictionary<string,object>)((IList)option["growsInto"])[0];
        Check(upgrade["cost"]==null,"fractional native upgrade cost is not rounded");

        var chosen=typeof(CityOptionsSnapshot).GetMethod("ChosenRewardsJson",F);
        var args=new object[] {new object[] {"Workshop",null},null,null,null,50,2,5};
        string rewards=(string)chosen.Invoke(null,args);
        Check(args[3]==null && rewards.Contains("null"),"partial chosen rewards cannot prove absence of wall and retain unknown slot");
        args=new object[] {new object[] {"CityWall",null},null,null,null,50,2,5};chosen.Invoke(null,args);
        Check(object.Equals(args[3],true),"positive wall evidence survives an unreadable peer");
        args=new object[] {new object[] {"FutureCityWallBonus"},null,null,null,50,2,5};chosen.Invoke(null,args);
        Check(object.Equals(args[3],false),"reward name substring does not prove actual CityWall");
        var effect=(string)typeof(CityOptionsSnapshot).GetMethod("RewardEffectJson",F).Invoke(null,new object[] {"FutureParkBonus",null,null,50,2,5});
        Check(J.Deserialize<Dictionary<string,object>>(effect)["scoreConstant"]==null,"future reward name cannot borrow Park score constant");
        foreach(string name in new[] {"Park","PopulationGrowth","Resources"})
        {
            effect=(string)typeof(CityOptionsSnapshot).GetMethod("RewardEffectJson",F).Invoke(null,new object[] {name,null,null,50,3,5});
            var constants=J.Deserialize<Dictionary<string,object>>(effect);
            string key=name=="Park"?"scoreConstant":name=="PopulationGrowth"?"populationConstant":"resourceConstant";
            Check(Convert.ToInt32(constants[key])==(name=="Park"?50:name=="PopulationGrowth"?3:5),
                "actual native reward keeps its own constant: "+name);
        }

        var player=new PlayerState();player.availableTech.Add(1);player.availableTech.Add(2);player.availableTech.FailAt=1;
        Check(Call("ResultsScorePlayerCopy",player)==null,"broken technology copy cannot compute a complete reduced score");
        Check(player.availableTech.Count==2 && player.unlockedTechCache.Count==0,"failed score copying leaves live state intact");
        player.availableTech.FailAt=-1;
        var copy=Call("ResultsScorePlayerCopy",player) as PlayerState;
        Check(copy!=null && copy.availableTech.Count==2 && !ReferenceEquals(copy.availableTech,player.availableTech),"normal native scoring gets an independent full technology list");

        foreach(string mode in new[] {"Perfection","Domination"})
        {
            var record=Final(mode,true);
            Check(Valid(record),mode+" complete zero components remain legitimate zeros");
            var scores=(Dictionary<string,object>)record["scores"];
            var details=(Dictionary<string,object>)scores[mode=="Perfection"?"scoreDetails":"playerRating"];
            string field=mode=="Perfection"?"techScore":"battleScore";
            foreach(object bad in new object[] {null,true,"0"})
            {details[field]=bad;Check(!Valid(record),mode+" archive complete flag cannot hide unknown/nonnumeric component");}
            details[field]=0;scores.Remove("nativeFinalTotal");
            Check(!Valid(record),mode+" complete archive must have actual final total");
            record["complete"]=false;Check(Valid(record),mode+" partial archive remains readable without guessed missing numbers");
        }
        Console.WriteLine("passed="+passed+" failed="+failed);return failed==0?0:1;
    }
}
