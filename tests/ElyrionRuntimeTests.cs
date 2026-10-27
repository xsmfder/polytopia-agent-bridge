using System;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using PolytopiaAgent;

namespace ElyrionFixture {
 public class UnitData {
  public string type="Warrior",displayName="native"; public int cost=3,growthRate=3; public bool hidden;
  public List<string> unitAbilities=new List<string>();
 }
 public class Creates { public UnitData unit; }
 public class ImprovementData {
  public string type="EnchantAnimal",displayName="native"; public int cost=3;
  public List<Creates> creates=new List<Creates>{new Creates{unit=new UnitData{type="Polytaur",hidden=true}}};
 }
 public class TechData {
  public string type="ForestMagic",displayName="native"; public int cost=0;
  public List<UnitData> unitUnlocks=new List<UnitData>{new UnitData{type="Bunny",hidden=true}};
 }
 public class TribeData {
  public List<TechData> startingTech=new List<TechData>();
  public UnitData startingUnit=new UnitData();
  public Dictionary<string,UnitData> unitOverrides=new Dictionary<string,UnitData>{
   {"Giant",new UnitData{type="DragonEgg",hidden=true,unitAbilities=new List<string>{"Grow"}}}};
 }
 public class Player { public string tribe="Elyrion"; }
 public class Logic {
  public bool BrokenGrowth;
  public TribeData GetTribeData(string tribe){return new TribeData();}
  public List<TechData> GetAllTechForTribe(TribeData tribe){return new List<TechData>{new TechData()};}
  public List<ImprovementData> GetImprovementsFromTech(TechData tech,TribeData tribe){return new List<ImprovementData>{new ImprovementData()};}
  public UnitData GetOverride(UnitData data,TribeData tribe){return data;}
  public TechData GetOverride(TechData data,TribeData tribe){return data;}
  public ImprovementData GetOverride(ImprovementData data,TribeData tribe){return data;}
  public UnitData GetUnitWillGrowInto(UnitData data){
   if(BrokenGrowth)throw new Exception("missing rule");
   return data.type=="DragonEgg"?new UnitData{type="BabyDragon",hidden=true,unitAbilities=new List<string>{"Grow"}}
    :new UnitData{type="FireDragon",hidden=true};
  }
 }
 public class Build {public string PlayerId="1",Coordinates="(2,3)",Type="EnchantAnimal";}
 public class Train {public string PlayerId="1",Coordinates="(2,3)",Type="NativeCreatedUnit";public object trainActionAborted=false;}
 public class Attract {public string Coordinates="(2,3)",Reason="Attract",Type="Game";}
}
internal static class ElyrionRuntimeTests {
 static int count;
 static void Check(bool ok,string label){if(!ok)throw new Exception(label);count++;}
 static Dictionary<string,object> Parse(string json){return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);}
 static BridgeMod.CombatFrame Frame(string resource,bool spawned=false){
  var f=new BridgeMod.CombatFrame{Episode="ely",Me="1",Turn="4",Complete=true};
  if(resource!=null)f.OwnResources["(2,3)"]=resource;
  if(spawned)f.Units["new"]=new BridgeMod.CombatUnitView{Id="new",Type="NativeCreatedUnit",Owner="1",Coord="(2,3)"};
  return f;
 }
 public static int Main(){try{Run();Console.WriteLine("PASS ElyrionRuntimeTests assertions="+count);return 0;}catch(Exception e){Console.WriteLine(e);return 1;}}
 static void Run(){
  var catalog=Parse(BridgeMod.ProjectTribeCatalog(new ElyrionFixture.Logic(),new ElyrionFixture.Player(),null));
  Check((string)catalog["status"]=="ok","native Elyrion graph read");
  var types=new HashSet<string>();foreach(Dictionary<string,object> r in (IList)catalog["units"])types.Add((string)r["type"]);
  Check(types.SetEquals(new[]{"Warrior","Polytaur","DragonEgg","BabyDragon","FireDragon"}),"creation and growth closure preserves hidden native stages, excludes unrelated Bunny");
  var imp=(Dictionary<string,object>)((IList)catalog["improvements"])[0];
  Check((string)((IList)imp["createsUnits"])[0]=="Polytaur","creation linked to improvement, not direct training");
  var tech=(Dictionary<string,object>)((IList)catalog["techs"])[0];
  Check(((IList)((Dictionary<string,object>)tech["unlocks"])["units"]).Count==0,"spawn units do not become train unlocks");
  Check((string)Parse(BridgeMod.ProjectTribeCatalog(new ElyrionFixture.Logic{BrokenGrowth=true},new ElyrionFixture.Player(),null))["status"]=="unavailable","broken native growth graph cannot claim complete catalog");
  var before=Frame("Game");var after=Frame("none",true);
  var ticket=new BridgeMod.CombatTicket{Before=before,Action="build",Arg="EnchantAnimal",Coord="(2,3)",Accepted=true};
  var build=new ElyrionFixture.Build();var train=new ElyrionFixture.Train();
  Check(BridgeMod.CREnchantBuildMatches(ticket,before,build),"own native animal build binds");
  Check(!BridgeMod.CRRecordEnchantedUnit(ticket,train,Frame("none"),after),"unbound train does not confirm");
  ticket.NativeEnchantBuildObserved=true;ticket.NativeEnchantTrains.Add(train);
  Check(!BridgeMod.CRRecordEnchantedUnit(ticket,new ElyrionFixture.Train(),Frame("none"),after),"matching fields without exact reference do not confirm");
  Check(BridgeMod.CRRecordEnchantedUnit(ticket,train,Frame("none"),after),"exact executed child plus resource and new native own type confirms");
  Check(BridgeMod.CREnchantmentSettled(ticket,after),"final visible state matches proof");
  train.trainActionAborted=true;Check(!BridgeMod.CRRecordEnchantedUnit(ticket,train,Frame("none"),after),"aborted train rejected");train.trainActionAborted=null;
  Check(!BridgeMod.CRRecordEnchantedUnit(ticket,train,Frame("none"),after),"unknown abort flag rejected");train.trainActionAborted=false;
  after.Units["new"].Type="Other";Check(!BridgeMod.CRRecordEnchantedUnit(ticket,train,Frame("none"),after),"different native type rejected");after=Frame("none",true);
  after.Units["new"].Owner="2";Check(!BridgeMod.CRRecordEnchantedUnit(ticket,train,Frame("none"),after),"foreign unit rejected");after=Frame("none",true);
  Check(!BridgeMod.CRRecordEnchantedUnit(ticket,train,Frame("none",true),after),"existing visible unit is not newly created");
  after.Turn="5";Check(!BridgeMod.CRRecordEnchantedUnit(ticket,train,Frame("none"),after),"later turn cannot acquire proof");after=Frame("none",true);
  after.Episode="other";Check(!BridgeMod.CRRecordEnchantedUnit(ticket,train,Frame("none"),after),"other episode rejected");after=Frame("none",true);
  ticket.FinalFrame=after;Check(!BridgeMod.CRRecordEnchantedUnit(ticket,train,Frame("none"),after),"frozen ticket cannot gain proof");ticket.FinalFrame=null;
  before.OwnResources.Clear();Check(!BridgeMod.CREnchantBuildMatches(ticket,before,build),"unknown resource does not mean Game");
  var attraction=new ElyrionFixture.Attract();var empty=Frame("none");var game=Frame("Game");game.Turn="5";
  Check(BridgeMod.CRAttractObserved(empty,game,attraction),"own resource transition and actual native Attract reason supports cross-turn attraction");
  Check(!BridgeMod.CRAttractObserved(Frame(null),game,attraction),"unobserved old resource cannot claim attraction");
  Check(!BridgeMod.CRAttractObserved(Frame("Game"),game,attraction),"existing animal is not newly attracted");
  attraction.Reason="None";Check(!BridgeMod.CRAttractObserved(empty,game,attraction),"other native spawn reason rejected");attraction.Reason="Attract";
  game.Episode="other";Check(!BridgeMod.CRAttractObserved(empty,game,attraction),"other episode attraction rejected");game.Episode=empty.Episode;
  var cause=new BridgeMod.CombatCause{NativeAttract=true,TargetCoord="(2,3)"};
  var diff=string.Join("",BridgeMod.CRDiff(empty,game,cause).ToArray());
  Check(diff.Contains("animal_attracted")&&diff.Contains("\"exactSanctuaryCoord\":null"),"native cause reported without invented sanctuary source");
  Check(!string.Join("",BridgeMod.CRDiff(Frame(null),game,cause).ToArray()).Contains("animal_attracted"),"withheld resources cannot produce attraction events");
  var egg=new ElyrionFixture.UnitData{type="DragonEgg",unitAbilities=new List<string>{"Grow"}};
  var baby=new ElyrionFixture.UnitData{type="BabyDragon",unitAbilities=new List<string>{"Grow"}};
  var fire=new ElyrionFixture.UnitData{type="FireDragon"};
  for(int age=0;age<=3;age++){
   var growth=Parse(BridgeMod.ProjectUnitGrowth(true,egg,age,baby,true,true));
   Check((string)growth["status"]=="growing"&&(int)growth["turnsToNextStage"]==4-age,"natural initial egg age sample "+age);
  }
  Check((int)Parse(BridgeMod.ProjectUnitGrowth(true,baby,1,fire,true,true))["turnsToNextStage"]==3,"new baby stage resets native age independently of zero XP");
  Check((string)Parse(BridgeMod.ProjectUnitGrowth(true,fire,4,null,false,true))["status"]=="not_growing","final fire dragon has no countdown");
  Check(Parse(BridgeMod.ProjectUnitGrowth(true,egg,null,baby,true,true))["turnsToNextStage"]==null,"unavailable age cannot become zero");
  Check(Parse(BridgeMod.ProjectUnitGrowth(true,egg,1,baby,true,false))["turnsToNextStage"]==null,"unverified native timing withholds estimate");
  Check(Parse(BridgeMod.ProjectUnitGrowth(true,egg,1,null,false,true))["turnsToNextStage"]==null,"missing native next stage withholds countdown");
  Check(Parse(BridgeMod.ProjectUnitGrowth(false,egg,1,baby,true,true))["ageTurns"]==null,"foreign age and next stage withheld");
 }
}
