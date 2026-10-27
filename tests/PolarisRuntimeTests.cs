using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using PolytopiaAgent;

namespace PolarisFixture {
 public enum UnitEffect {Frozen=0,Boosted=1}
 public enum OtherEffect {Frozen=0}
 public class OwnUnit {public object effects=new List<UnitEffect>();}
 public class Area {public string PlayerId="1",Coordinates="(3,3)";public object Radius=1;}
 public class Ice {public string PlayerId="1",Coordinates="(2,4)";}
 public class Freeze {public string PlayerId="1",Origin="(3,3)",Target="(4,4)";}
 public class Move {public string PlayerId="1",UnitId="4",Reason="Command";public List<string> Path=new List<string>{"(3,3)","(4,4)"};}
}
internal static class PolarisRuntimeTests {
 const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
 static int count;
 static void Check(bool ok,string label){if(!ok)throw new Exception(label);count++;}
 static object Call(object host,string name,params object[] args){return typeof(BridgeMod).GetMethod(name,F).Invoke(host,args);}
 static BridgeMod.CombatFrame Frame(string terrain){
  var f=new BridgeMod.CombatFrame{Episode="polaris",Me="1",Turn="3",Complete=true};
  f.PublicTerrains["(3,3)"]="Field";f.PublicTerrains["(2,4)"]=terrain;
  f.Units["4"]=new BridgeMod.CombatUnitView{Id="4",Owner="1",Type="Warrior",Coord="(3,3)",Frozen=false};
  return f;
 }
 static BridgeMod.CombatTicket Ticket(BridgeMod.CombatFrame before,string action="break_ice"){
  return new BridgeMod.CombatTicket{Before=before,Action=action,UnitId="4",Coord="(3,3)",Accepted=true};
 }
 static object Pass(string kind,object action,BridgeMod.CombatFrame before,BridgeMod.CombatTicket ticket){
  var type=typeof(BridgeMod).GetNestedType("CombatHookPass",F);var p=Activator.CreateInstance(type,true);
  type.GetField("CandidateKind",F).SetValue(p,kind);type.GetField("Action",F).SetValue(p,action);
  type.GetField("Before",F).SetValue(p,before);
  ((IList)type.GetField("ProofTickets",F).GetValue(p)).Add(ticket);return p;
 }
 static string Confirm(BridgeMod.CombatTicket ticket,BridgeMod.CombatFrame current,out List<string> changes){
  changes=new List<string>();object[] args={ticket,current,changes,null};
  return (string)typeof(BridgeMod).GetMethod("PolarisConfirmation",F).Invoke(null,args);
 }
 public static int Main(){try{Run();Console.WriteLine("PASS PolarisRuntimeTests assertions="+count);return 0;}catch(Exception e){Console.WriteLine(e);return 1;}}
 static void Run(){
  var own=new PolarisFixture.OwnUnit();
  Check(BridgeMod.PolarisOwnFrozen(own)==false,"readable native empty effects means unfrozen");
  own.effects=new List<PolarisFixture.UnitEffect>{PolarisFixture.UnitEffect.Frozen};
  Check(BridgeMod.PolarisOwnFrozen(own)==true,"native zero-valued Frozen is true, never numeric false");
  own.effects=new List<PolarisFixture.OtherEffect>{PolarisFixture.OtherEffect.Frozen};
  Check(BridgeMod.PolarisOwnFrozen(own)==null,"unrelated enum does not become Frozen");
  own.effects=null;Check(BridgeMod.PolarisOwnFrozen(own)==null,"unknown effects are not unfrozen");
  const string clear="{\"schema\":\"unit-visual-effects-1\",\"status\":\"ok\",\"particles\":{\"status\":\"ok\",\"types\":[]}}";
  string frozen=clear.Replace("[]","[\"Frozen\"]");
  Check(BridgeMod.PolarisVisibleFrozen(frozen)==true,"actual complete Frozen particle is public positive");
  Check(BridgeMod.PolarisVisibleFrozen(clear)==false,"complete empty renderer projection is public clear");
  Check(BridgeMod.PolarisVisibleFrozen(frozen.Replace("\"status\":\"ok\",\"particles\"","\"status\":\"withheld\",\"particles\""))==null,"withheld renderer cannot disclose Frozen");
  Check(BridgeMod.PolarisVisibleFrozen(clear.Replace("[]","null"))==null,"unknown particle types do not mean clear");
  Check(BridgeMod.PolarisVisibleFrozen(clear.Replace("[]","[0]"))==null,"malformed particle list is not coerced");
  Check(BridgeMod.PolarisInArea("(3,3)","(2,4)",1),"diagonal target belongs to native square area");
  Check(!BridgeMod.PolarisInArea("(3,3)","(1,4)",1),"outside radius rejected");
  Check(!BridgeMod.PolarisInArea("3,3","(2,4)",1),"malformed coordinate rejected");
  Check(BridgeMod.PolarisInArea("(-2147483648,0)","(2147483647,0)",Int32.MaxValue)==false,"radius arithmetic cannot overflow");
  var before=Frame("Ice");var after=Frame("Water");var ticket=Ticket(before);
  var host=new BridgeMod();var area=new PolarisFixture.Area();var child=new PolarisFixture.Ice();
  Check((bool)Call(host,"PolarisBind",ticket,before,area,"BreakIceAreaAction"),"own requested break area binds while actor stands on Field");
  Check(!BridgeMod.PolarisRecordIce(ticket,child,before,after,"BreakIceAction"),"uncorrelated child cannot confirm visible change");
  object pass=Pass("BreakIceAreaAction",area,before,ticket);
  var active=(IList)typeof(BridgeMod).GetField("_polarisActivePasses",F).GetValue(host);active.Add(pass);
  Check((bool)Call(host,"PolarisBind",ticket,before,child,"BreakIceAction"),"executed nested child under bound own area links by parent context");
  active.Clear();
  Check(!BridgeMod.PolarisRecordIce(ticket,new PolarisFixture.Ice(),before,after,"BreakIceAction"),"same fields without exact object reference rejected");
  Check(BridgeMod.PolarisRecordIce(ticket,child,before,after,"BreakIceAction"),"actual ice-to-water child confirms affected neighbouring tile");
  Check(BridgeMod.PolarisRecordIce(ticket,child,before,after,"BreakIceAction")&&ticket.Polaris.IceChanges.Count==1,"same native transition cannot duplicate affected tiles");
  List<string> changes;
  Check(Confirm(ticket,after,out changes)=="confirmed"&&changes.Count==1,"range receipt confirms neighbour while actor ground remains Field");
  var unrelated=Frame("Ice");Check(Confirm(ticket,unrelated,out changes)=="unknown","final ice reversal cannot confirm saved water change");
  Check(!BridgeMod.PolarisRecordIce(ticket,child,before,Frame("Ice"),"BreakIceAction"),"no ice removal cannot confirm");
  child.PlayerId="2";Check(!BridgeMod.PolarisRecordIce(ticket,child,before,after,"BreakIceAction"),"foreign child rejected");child.PlayerId="1";
  after.Turn="4";Check(!BridgeMod.PolarisRecordIce(ticket,child,before,after,"BreakIceAction"),"later turn cannot acquire old receipt proof");after.Turn="3";
  after.Episode="other";Check(!BridgeMod.PolarisRecordIce(ticket,child,before,after,"BreakIceAction"),"other match rejected");after.Episode="polaris";
  ticket.FinalFrame=after;Check(!BridgeMod.PolarisRecordIce(ticket,child,before,after,"BreakIceAction"),"frozen receipt cannot gain later effects");ticket.FinalFrame=null;
  var move=new PolarisFixture.Move();ticket=Ticket(before,"move");ticket.Coord="(4,4)";
  Check(BridgeMod.PolarisMoveMatches(ticket,before,move),"native own move identity binds before checking actual execution result");
  move.UnitId="9";Check(!BridgeMod.PolarisMoveMatches(ticket,before,move),"other actor's move rejected");move.UnitId="4";
  move.Path.Clear();Check(BridgeMod.PolarisMoveMatches(ticket,before,move),"path order and boxing are not mistaken for actual displacement proof");
  move.PlayerId="0";Check(BridgeMod.PolarisMoveMatches(ticket,before,move),"unattributed native command move still binds only a known owned actor");
  move.Reason="Push";Check(!BridgeMod.PolarisMoveMatches(ticket,before,move),"push displacement is not the requested command move");move.Reason="Command";
  move.PlayerId="2";Check(!BridgeMod.PolarisMoveMatches(ticket,before,move),"explicit foreign native player rejected");move.PlayerId="1";
  pass=Pass("MoveAction",move,before,ticket);Call(host,"PolarisRecord",pass,before,ticket);
  Check(!ticket.Polaris.NamedMoveObserved,"matching actor alone without requested displacement is insufficient");
  var movedFrame=Frame("Water");movedFrame.Units["4"].Coord="(4,4)";movedFrame.Units["4"].AutoFreeze=true;
  before.PublicTerrains["(2,4)"]="Water";movedFrame.PublicTerrains["(2,4)"]="Ice";
  Call(host,"PolarisRecord",pass,movedFrame,ticket);
  Check(ticket.Polaris.NamedMoveObserved&&ticket.Polaris.AutoFreezeMoveObserved,"actual native destination and own AutoFreeze ability establish observed move effects");
  Check(ticket.Polaris.IceChanges.Count==1&&ticket.Polaris.IceChanges[0].Association.Contains("visible-transition"),"inline AutoFreeze terrain changes are not mislabeled as exact child-reference proof");
  before=Frame("Water");after=Frame("Ice");ticket=Ticket(before,"move");ticket.Polaris.FreezeTiles.Add(child);
  Check(BridgeMod.PolarisRecordIce(ticket,child,before,after,"FreezeTileAction"),"actual reference-bound water-to-ice child captured");
  var diff=string.Join("",BridgeMod.CRDiff(before,after,null).ToArray());
  Check(diff.Contains("ice_terrain_changed")&&diff.Contains("Water")&&diff.Contains("Ice"),"public terrain journal includes neutral revealed ice transitions");
  var unknown=Frame("Water");unknown.PublicTerrains.Clear();
  Check(!string.Join("",BridgeMod.CRDiff(unknown,after,null).ToArray()).Contains("ice_terrain_changed"),"unknown old terrain is not invented water");
  before=Frame("Water");after=Frame("Water");ticket=Ticket(before,"attack");ticket.Coord="(4,4)";
  before.Units["4"].FreezeAttack=true;after.Units["4"].FreezeAttack=true;
  before.Units["9"]=new BridgeMod.CombatUnitView{Id="9",Owner="2",Type="Warrior",Coord="(4,4)",Frozen=false,Health=100};
  after.Units["9"]=new BridgeMod.CombatUnitView{Id="9",Owner="2",Type="Warrior",Coord="(4,4)",Frozen=false,Health=100};
  var shot=new PolarisFixture.Freeze();
  Check((bool)Call(host,"PolarisBind",ticket,before,shot,"FreezeUnitAction"),"own freeze-capable attack binds actual origin and target");
  pass=Pass("FreezeUnitAction",shot,before,ticket);Call(host,"PolarisRecord",pass,after,ticket);
  Check(Confirm(ticket,after,out changes)=="unknown","execution before renderer update is not frozen success");
  after.Units["9"].Frozen=true;
  Check(Confirm(ticket,after,out changes)=="confirmed"&&changes.Count==1,"later public Frozen transition confirms ice arrow without fake HP damage");
  before.Units["9"].Frozen=true;Check(Confirm(ticket,after,out changes)=="unknown","already frozen target cannot prove a new freeze or timer refresh");
  before.Units["9"].Frozen=false;after.Units["9"].Owner="1";
  Check(Confirm(ticket,after,out changes)=="unknown","converted target cannot inherit foreign freeze proof");
 }
}
