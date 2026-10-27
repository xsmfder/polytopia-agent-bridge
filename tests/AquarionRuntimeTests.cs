using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

namespace AquarionFixture {
 // Matches IL2CPP typed enumeration without managed IEnumerable or integer indexer.
 public class OverrideDictionary {
  public int Count { get { return ReportedCount; } }
  public int ReportedCount=1;
  public OverrideValues Values { get { return new OverrideValues(); } }
 }
 public class OverrideValues {
  public OverrideIterator GetEnumerator() { return new OverrideIterator(); }
 }
 public class OverrideIterator {
  private int position=-1;
  public bool MoveNext() { position++;return position==0; }
  public UnitData Current { get { return new UnitData{type="Crab",hidden=true}; } }
 }
 public enum CityReward { Explorer, Workshop }
 public class TerrainData { public enum Type { Field, Ocean } public enum EffectType { Flooded } }
 public class OnlyEffects { public enum EffectType { Flooded } }
 public class TribeData {
  public List<TechData> startingTech=new List<TechData>{new TechData{type="AquaBase"}};
  public UnitData startingUnit=new UnitData();
  public OverrideDictionary unitOverrides=new OverrideDictionary();
 }
 public class Player { public string tribe="Aquarion"; }
 public class UnitData { public string type="Warrior",displayName="Mermaid"; public int cost=2; public bool hidden; public List<string> unitAbilities=new List<string>{"Tentacles"}; }
 public class ImprovementData { public string type="Farm",displayName="Atoll"; public int cost=5; public List<object> creates=new List<object>(); }
 public class TechData { public string type="AquaBase",displayName="Marine Life"; public int cost=0; public List<UnitData> unitUnlocks=new List<UnitData>{new UnitData(),new UnitData{type="Bunny",hidden=true}}; }
 public class Logic {
  public bool Missing,IncompleteOverrides;
  public TribeData GetTribeData(string tribe) { var data=new TribeData();if(IncompleteOverrides)data.unitOverrides.ReportedCount=2;return data; }
  public List<TechData> GetAllTechForTribe(TribeData tribe) { return Missing?null:new List<TechData>{new TechData{type="Basic"}}; }
  public List<ImprovementData> GetImprovementsFromTech(TechData tech,TribeData tribe) { return new List<ImprovementData>{new ImprovementData()}; }
  public UnitData GetOverride(UnitData unit,TribeData tribe) { return unit.type=="Warrior"?new UnitData{type="MermaidWarrior"}:unit; }
  public TechData GetOverride(TechData tech,TribeData tribe) { return tech.type=="AquaBase"?tech:new TechData{type="MarineLife"}; }
  public ImprovementData GetOverride(ImprovementData improvement,TribeData tribe) { return new ImprovementData{type="Atoll"}; }
 }
 public class Tile { public string terrain="Field"; public List<string> effects=new List<string>(); }
 public class FloodAction { public string PlayerId="2",Coordinates="(2,3)"; }
 public class CanalAction { public string PlayerId="2",Coordinates="(2,3)",Type="Canal"; }
 public class ClearAction { public string PlayerId="2",Target="(2,3)",Effect="Flooded"; }
}

public class FloodTileAction : AquarionFixture.FloodAction { }
public class CanalActionQueue {
 public List<object> Items=new List<object>();
 public int Count { get { return Items.Count; } }
 public object this[int index] { get { return Items[index]; } }
}
public class CanalQueueState { public CanalActionQueue ActionStack=new CanalActionQueue(); }

internal static class AquarionRuntimeTests {
 static int count;
 static void Check(bool condition,string label) { if(!condition)throw new Exception(label);count++; }
 static Dictionary<string,object> Parse(string json) { return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json); }
 static BridgeMod.CombatFrame Frame(string state) {
  var frame=new BridgeMod.CombatFrame{Episode="ep-own",Me="2",Turn="4",Complete=true};
  frame.OwnTileStates["(2,3)"]=state;return frame;
 }
 static BridgeMod.CombatFrame CanalFrame(bool flooded) {
  var frame=Frame("{}");
  frame.PublicFloodTiles["(2,3)"]=new BridgeMod.PublicFloodView{Terrain="Forest",Flooded=flooded};
  frame.Units["17"]=new BridgeMod.CombatUnitView{Id="17",Owner="2",Coord="(2,3)"};return frame;
 }
 public static int Main() {
  try { Run();Console.WriteLine("PASS AquarionRuntimeTests assertions="+count);return 0; }
  catch(Exception error){Console.WriteLine(error);return 1;}
 }
 static void Run() {
  Check(BridgeMod.DeclaredCatalogEnum(typeof(AquarionFixture.TerrainData),"Type")==typeof(AquarionFixture.TerrainData.Type),"terrain uses Type, never EffectType");
  Check(BridgeMod.DeclaredCatalogEnum(typeof(AquarionFixture.OnlyEffects),"Type")==null,"missing terrain enum is not replaced by effects");
  Check(BridgeMod.DeclaredCatalogEnum(typeof(AquarionFixture.CityReward),null)==typeof(AquarionFixture.CityReward),"direct reward enum is preserved");
  Check(BridgeMod.DeclaredCatalogEnum(typeof(AquarionFixture.Tile),null)==null,"non-enum direct host unavailable");
  var catalog=Parse(BridgeMod.ProjectTribeCatalog(new AquarionFixture.Logic(),new AquarionFixture.Player(),null));
  Check((string)catalog["status"]=="ok","native tribe rule catalog read");
  Check((string)((Dictionary<string,object>)((IList)catalog["techs"])[0])["type"]=="AquaBase","special technology survives");
  var unitTypes=new List<string>();foreach(Dictionary<string,object> row in (IList)catalog["units"]) unitTypes.Add((string)row["type"]);
  Check(unitTypes.Contains("MermaidWarrior"),"native unit override used");
  Check(unitTypes.Contains("Crab"),"tribe-specific spawn-only override retained");
  Check(!unitTypes.Contains("Bunny"),"hidden generic unlock excluded");
  var techTypes=new List<string>();foreach(Dictionary<string,object> row in (IList)catalog["techs"]) techTypes.Add((string)row["type"]);
  Check(techTypes.Contains("AquaBase")&&techTypes.Contains("MarineLife")&&!techTypes.Contains("Basic"),"starting technology and native tech overrides both applied");
  Check((string)((Dictionary<string,object>)((IList)catalog["improvements"])[0])["type"]=="Atoll","native improvement override used");
  var unlocks=(Dictionary<string,object>)((Dictionary<string,object>)((IList)catalog["techs"])[0])["unlocks"];
  Check(((IList)unlocks["units"]).Count==1&&(string)((IList)unlocks["units"])[0]=="MermaidWarrior","tech unlock links use the native override and exclude hidden generic units");
  Check((string)((IList)unlocks["improvements"])[0]=="Atoll","technology improvement link uses the native override");
  var missing=Parse(BridgeMod.ProjectTribeCatalog(new AquarionFixture.Logic{Missing=true},new AquarionFixture.Player(),null));
  Check((string)missing["status"]=="unavailable"&&missing["techs"]==null,"unreadable list is not empty or successful");
  var incomplete=Parse(BridgeMod.ProjectTribeCatalog(new AquarionFixture.Logic{IncompleteOverrides=true},new AquarionFixture.Player(),null));
  Check((string)incomplete["status"]=="unavailable"&&incomplete["units"]==null,"native dictionary count mismatch cannot produce a partial success");
  var tile=new AquarionFixture.Tile();string beforeState=BridgeMod.AquaticTileState(tile);tile.effects.Add("Flooded");string afterState=BridgeMod.AquaticTileState(tile);
  Check(beforeState!=afterState,"flood effect captured even without terrain enum change");
  Check(BridgeMod.AquaticTileState(new object())==null,"unknown terrain/effects remain unavailable");
  var publicFlood=BridgeMod.PublicFloodState(tile);
  Check(publicFlood!=null&&publicFlood.Flooded&&publicFlood.Terrain=="Field","rendered flood flag read independently of ownership");
  tile.effects.Add("Tentacle");
  Check(!BridgeMod.PublicFloodState(tile).Json().Contains("Tentacle"),"public terrain projection does not leak foreign effect list");
  Check(BridgeMod.PublicFloodState(new object())==null,"unknown public flood bit is not false");
  var canalBefore=CanalFrame(false);var canalAfter=CanalFrame(true);
  var canalTicket=new BridgeMod.CombatTicket{Before=canalBefore,Action="build",Arg="Canal",Coord="(2,3)",UnitId="17",Accepted=true};
  var canalAction=new AquarionFixture.CanalAction();
  Check(BridgeMod.CRRecordCanal(canalTicket,canalAction,canalBefore,canalAfter),"own native Canal action plus visible foreign tile false-to-true change confirms");
  Check(canalTicket.NativeCanalBefore.Contains("false")&&canalTicket.NativeCanalAfter.Contains("true"),"Canal proof freezes public before and after");
  Check(canalBefore.Signature()!=canalAfter.Signature(),"public flood changes advance visible state version");
  Check(!BridgeMod.CRRecordCanal(canalTicket,canalAction,canalBefore,CanalFrame(false)),"unchanged dry tile cannot confirm Canal");
  Check(!BridgeMod.CRRecordCanal(canalTicket,canalAction,CanalFrame(true),canalAfter),"already flooded tile cannot confirm a new flood");
  canalAfter.PublicFloodTiles.Clear();Check(!BridgeMod.CRRecordCanal(canalTicket,canalAction,canalBefore,canalAfter),"withheld terrain cannot confirm Canal");canalAfter=CanalFrame(true);
  canalAction.Type="Farm";Check(!BridgeMod.CRRecordCanal(canalTicket,canalAction,canalBefore,canalAfter),"other BuildAction cannot confirm Canal");canalAction.Type="Canal";
  canalAction.PlayerId="3";Check(!BridgeMod.CRRecordCanal(canalTicket,canalAction,canalBefore,canalAfter),"foreign player action cannot bind Canal");canalAction.PlayerId="2";
  canalAction.Coordinates="(3,4)";Check(!BridgeMod.CRRecordCanal(canalTicket,canalAction,canalBefore,canalAfter),"other native coordinates cannot bind Canal");canalAction.Coordinates="(2,3)";
  canalAfter.Units["17"].Owner="3";Check(!BridgeMod.CRRecordCanal(canalTicket,canalAction,canalBefore,canalAfter),"foreign or converted actor cannot bind Canal");canalAfter=CanalFrame(true);
  canalAfter.Units["17"].Coord="(4,5)";Check(!BridgeMod.CRRecordCanal(canalTicket,canalAction,canalBefore,canalAfter),"other actor position cannot bind Canal");canalAfter=CanalFrame(true);
  canalAfter.Episode="other";Check(!BridgeMod.CRRecordCanal(canalTicket,canalAction,canalBefore,canalAfter),"other episode cannot bind Canal");canalAfter=CanalFrame(true);
  canalAfter.Turn="9";Check(!BridgeMod.CRRecordCanal(canalTicket,canalAction,canalBefore,canalAfter),"later turn cannot bind Canal");canalAfter=CanalFrame(true);
  canalTicket.Accepted=false;Check(!BridgeMod.CRRecordCanal(canalTicket,canalAction,canalBefore,canalAfter),"rejected Canal cannot acquire proof");canalTicket.Accepted=true;
  canalTicket.FinalFrame=canalAfter;Check(!BridgeMod.CRRecordCanal(canalTicket,canalAction,canalBefore,canalAfter),"frozen ticket cannot gain later Canal proof");
  // Real production suffix reader + asynchronous parent/child sequence. The
  // BuildAction consumes the unit first; terrain changes only in a later child.
  var queuedTicket=new BridgeMod.CombatTicket{Before=canalBefore,Action="build",Arg="Canal",Coord="(2,3)",UnitId="17",Accepted=true};
  var stateQueue=new CanalQueueState();stateQueue.ActionStack.Items.Add(new object());
  var flags=BindingFlags.Static|BindingFlags.NonPublic;
  var countBefore=typeof(BridgeMod).GetMethod("CRStackCount",flags).Invoke(null,new object[]{stateQueue});
  var floodChild=new FloodTileAction();stateQueue.ActionStack.Items.Add(floodChild);
  var children=(List<object>)typeof(BridgeMod).GetMethod("CRAppendedActions",flags).Invoke(null,new object[]{stateQueue,countBefore,"FloodTileAction"});
  Check(children.Count==1&&object.ReferenceEquals(children[0],floodChild),"production queue suffix captures exact newly appended FloodTileAction");
  Check(!BridgeMod.CRRecordCanal(queuedTicket,canalAction,canalBefore,CanalFrame(false)),"BuildAction completing before its child does not fake terrain confirmation");
  Check(BridgeMod.CRRecordCanalChildren(queuedTicket,canalAction,canalBefore,CanalFrame(false),children),"completed own Canal BuildAction records its appended child without requiring an immediate flood");
  Check(queuedTicket.NativeCanalBuildObserved&&queuedTicket.NativeCanalAfter==null,"parent observed remains unconfirmed until terrain child actually executes");
  Check(!BridgeMod.CRRecordCanalFlood(queuedTicket,new FloodTileAction(),canalBefore,CanalFrame(true)),"same coordinates and player on a different native child cannot acquire proof");
  Check(!BridgeMod.CRRecordCanalFlood(queuedTicket,floodChild,canalBefore,CanalFrame(false)),"reference-matched child with no terrain change cannot confirm");
  Check(BridgeMod.CRRecordCanalFlood(queuedTicket,floodChild,canalBefore,CanalFrame(true)),"later correlated FloodTileAction confirms actual public false-to-true terrain change");
  Check(queuedTicket.NativeCanalBefore.Contains("false")&&queuedTicket.NativeCanalAfter.Contains("true"),"asynchronous child freezes genuine before and after terrain");
  queuedTicket.FinalFrame=CanalFrame(true);
  Check(!BridgeMod.CRRecordCanalFlood(queuedTicket,floodChild,canalBefore,CanalFrame(true)),"late queued child cannot change a frozen ticket");
  var drainBefore=CanalFrame(true);var drainAfter=CanalFrame(false);
  drainBefore.OwnTileStates.Clear();drainAfter.OwnTileStates.Clear(); // Neutral terrain under an own unit, as in live hotseat.
  var drainTicket=new BridgeMod.CombatTicket{Before=drainBefore,Action="clear_tile_effect",Arg="Flooded",Coord="(2,3)",UnitId="17",Accepted=true};
  var drainAction=new AquarionFixture.ClearAction();
  Check(!BridgeMod.CRRecordClearFlood(drainTicket,drainAction,drainBefore,drainAfter),"unbound clear action cannot acquire terrain proof");
  Check(BridgeMod.CRBindClearFloodAction(drainTicket,drainBefore,drainAction),"requested native Flooded removal binds at own unit on neutral tile");
  Check(!BridgeMod.CRBindClearFloodAction(drainTicket,drainBefore,new AquarionFixture.ClearAction()),"different native clear action at same coordinates cannot replace bound action");
  Check(!BridgeMod.CRRecordClearFlood(drainTicket,new AquarionFixture.ClearAction(),drainBefore,drainAfter),"unrelated same-coordinate clear action cannot supply proof");
  Check(BridgeMod.CRRecordClearFlood(drainTicket,drainAction,drainBefore,drainAfter),"reference-bound ClearTileEffectAction confirms neutral own-unit tile true-to-false");
  Check(drainTicket.NativeClearFloodBefore.Contains("true")&&drainTicket.NativeClearFloodAfter.Contains("false"),"Drain freezes exact public before and after without reading neutral effect list");
  Check(!BridgeMod.CRRecordClearFlood(drainTicket,drainAction,drainBefore,CanalFrame(true)),"unchanged flooded tile cannot confirm Drain");
  Check(!BridgeMod.CRRecordClearFlood(drainTicket,drainAction,CanalFrame(false),drainAfter),"already dry tile cannot confirm a new Drain");
  drainAction.Effect="Algae";Check(!BridgeMod.CRRecordClearFlood(drainTicket,drainAction,drainBefore,drainAfter),"other clear effect cannot confirm Flooded removal");drainAction.Effect="Flooded";
  drainAction.PlayerId="3";Check(!BridgeMod.CRRecordClearFlood(drainTicket,drainAction,drainBefore,drainAfter),"foreign clear cannot acquire own Drain proof");drainAction.PlayerId="2";
  drainAction.Target="(4,5)";Check(!BridgeMod.CRRecordClearFlood(drainTicket,drainAction,drainBefore,drainAfter),"other clear target cannot confirm requested Drain");drainAction.Target="(2,3)";
  drainAfter.PublicFloodTiles.Clear();Check(!BridgeMod.CRRecordClearFlood(drainTicket,drainAction,drainBefore,drainAfter),"withheld flood flag stays unconfirmed");drainAfter=CanalFrame(false);
  drainAfter.PublicFloodTiles["(2,3)"].Terrain="Field";Check(!BridgeMod.CRRecordClearFlood(drainTicket,drainAction,drainBefore,drainAfter),"different terrain transition cannot masquerade as pure Flooded removal");drainAfter=CanalFrame(false);
  drainAfter.Units["17"].Owner="3";Check(!BridgeMod.CRRecordClearFlood(drainTicket,drainAction,drainBefore,drainAfter),"converted or foreign actor cannot confirm own Drain");drainAfter=CanalFrame(false);
  drainAfter.Units["17"].Coord="(3,4)";Check(!BridgeMod.CRRecordClearFlood(drainTicket,drainAction,drainBefore,drainAfter),"actor leaving target cannot confirm own-unit Drain");drainAfter=CanalFrame(false);
  drainAfter.Episode="other";Check(!BridgeMod.CRRecordClearFlood(drainTicket,drainAction,drainBefore,drainAfter),"other match cannot bind Drain");drainAfter=CanalFrame(false);
  drainAfter.Turn="5";Check(!BridgeMod.CRRecordClearFlood(drainTicket,drainAction,drainBefore,drainAfter),"later turn cannot supply Drain proof");drainAfter=CanalFrame(false);
  drainTicket.Arg="Algae";Check(!BridgeMod.CRRecordClearFlood(drainTicket,drainAction,drainBefore,drainAfter),"request for another tile effect cannot claim Drain");drainTicket.Arg="Flooded";
  drainTicket.Accepted=false;Check(!BridgeMod.CRRecordClearFlood(drainTicket,drainAction,drainBefore,drainAfter),"rejected command cannot claim Drain");drainTicket.Accepted=true;
  drainTicket.FinalFrame=drainAfter;Check(!BridgeMod.CRRecordClearFlood(drainTicket,drainAction,drainBefore,drainAfter),"frozen ticket cannot gain late Drain proof");
  var descriptorMethod=typeof(BridgeMod).GetMethod("URMenuActionDescriptor",BindingFlags.Static|BindingFlags.NonPublic);
  var drainDescriptor=descriptorMethod.Invoke(null,new object[]{"ClearTileEffect",null,"{\"Effect\":\"Flooded\"}",true,(bool?)true,true,new List<object>()});
  var drainRow=Parse((string)drainDescriptor.GetType().GetMethod("Close",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Invoke(drainDescriptor,null));
  Check((bool)drainRow["effectAdapterSupported"] && (bool)drainRow["executable"],"real menu projection advertises Flooded-clear proof while preserving native eligibility");
  var algaeDescriptor=descriptorMethod.Invoke(null,new object[]{"ClearTileEffect",null,"{\"Effect\":\"Algae\"}",true,(bool?)true,true,new List<object>()});
  var algaeRow=Parse((string)algaeDescriptor.GetType().GetMethod("Close",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Invoke(algaeDescriptor,null));
  Check(!(bool)algaeRow["effectAdapterSupported"] && (bool)algaeRow["executable"],"other native clear effects remain executable without pretending their effects are confirmed");
  var before=Frame(beforeState);var after=Frame(afterState);
  Check(before.Signature()!=after.Signature(),"owned tile effects contribute to observation version");
  var ticket=new BridgeMod.CombatTicket{Before=before,Action="flood",Coord="(2,3)",Accepted=true};
  var action=new AquarionFixture.FloodAction();
  Check(BridgeMod.CRRecordFlood(ticket,action,before,after),"executed requested flood records changed owned tile");
  Check(ticket.NativeFloodBefore==beforeState&&ticket.NativeFloodAfter==afterState,"flood receipt freezes before/after");
  Check(!BridgeMod.CRRecordFlood(ticket,action,before,Frame(beforeState)),"no change cannot confirm flood");
  action.PlayerId="3";Check(!BridgeMod.CRRecordFlood(ticket,action,before,after),"foreign player cannot bind");action.PlayerId="2";
  action.Coordinates="(4,5)";Check(!BridgeMod.CRRecordFlood(ticket,action,before,after),"other coordinate cannot bind");action.Coordinates="(2,3)";
  after.OwnTileStates.Clear();Check(!BridgeMod.CRRecordFlood(ticket,action,before,after),"unowned or unobserved tile cannot confirm");after=Frame(afterState);
  after.Episode="other";Check(!BridgeMod.CRRecordFlood(ticket,action,before,after),"other match cannot bind");after.Episode=before.Episode;
  after.Turn="5";Check(!BridgeMod.CRRecordFlood(ticket,action,before,after),"cross-turn snapshot cannot bind");after.Turn=before.Turn;
  ticket.Accepted=false;Check(!BridgeMod.CRRecordFlood(ticket,action,before,after),"rejected action cannot bind");ticket.Accepted=true;
  ticket.FinalFrame=after;Check(!BridgeMod.CRRecordFlood(ticket,action,before,after),"completed ticket cannot gain later proof");
 }
}
