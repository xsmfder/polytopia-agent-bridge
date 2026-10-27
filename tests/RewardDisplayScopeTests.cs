using System;
using System.Collections.Generic;
using PolytopiaAgent;
public class State {public object pendingCommandTriggers;public State(object value){pendingCommandTriggers=value;}}
 public class Trigger {
  public object type,playerId,Coordinates;
  public Trigger(object t,object p,object c){type=t;playerId=p;Coordinates=c;}
 }
 public class ForeignTrigger {
  public string type="CityLevelUp",playerId="2";
  public string Coordinates {get{throw new Exception("foreign coordinates must not be read");}}
 }
 public class RewardDisplayScopeTests {
  static int checks;
  static void Check(bool condition,string name){checks++;if(!condition)throw new Exception(name);}
  public static void Main(){
   var b=new BridgeMod();
   var other=new List<object>{new Trigger("CityLevelUp","1","(10,5)")};
   Check(b.PendingCityRewardCountFor(new State(other),1,5,6)==0,"another city does not block committed display");
   Check(BridgeMod.ResolvedRewardCanDismiss(true,true,b.PendingCityRewardCountFor(new State(other),1,5,6),"SuperUnit",new[]{"SuperUnit"}),"committed city can dismiss with another queued city");
   other.Add(new Trigger("CityLevelUp","1","(5,6)"));
   Check(b.PendingCityRewardCountFor(new State(other),1,5,6)==1,"same city pending is counted");
   Check(!BridgeMod.ResolvedRewardCanDismiss(true,true,b.PendingCityRewardCountFor(new State(other),1,5,6),"SuperUnit",new[]{"SuperUnit"}),"same city choice cannot be skipped");
   Check(b.PendingCityRewardCountFor(new object(),1,5,6)==null,"unreadable list fails closed");
   Check(b.PendingCityRewardCountFor(new State(new List<object>{null}),1,5,6)==null,"unreadable row fails closed");
   Check(b.PendingCityRewardCountFor(new State(new List<object>{new Trigger(null,"1","(5,6)")}),1,5,6)==null,"unknown type fails closed");
   Check(b.PendingCityRewardCountFor(new State(new List<object>{new Trigger("CityLevelUp",null,"(5,6)")}),1,5,6)==null,"unknown owner fails closed");
   Check(b.PendingCityRewardCountFor(new State(new List<object>{new Trigger("CityLevelUp","1",null)}),1,5,6)==null,"unknown own city fails closed");
   Check(b.PendingCityRewardCountFor(new State(new List<object>{new Trigger("CityLevelUp","1","oops")}),1,5,6)==null,"malformed own city fails closed");
   Check(b.PendingCityRewardCountFor(new State(new List<object>{new ForeignTrigger()}),1,5,6)==0,"foreign city coordinates not inspected");
   Check(b.PendingCityRewardCountFor(new State(new List<object>{new Trigger("TaskComplete",null,null)}),1,5,6)==0,"unrelated trigger remains unrelated");
   Check(!BridgeMod.ResolvedRewardCanDismiss(true,false,0,"SuperUnit",new[]{"SuperUnit"}),"enemy city rejected");
   Check(!BridgeMod.ResolvedRewardCanDismiss(false,true,0,"SuperUnit",new[]{"SuperUnit"}),"unverified identity rejected");
   Check(!BridgeMod.ResolvedRewardCanDismiss(true,true,null,"SuperUnit",new[]{"SuperUnit"}),"unknown same-city count rejected");
   Check(!BridgeMod.ResolvedRewardCanDismiss(true,true,0,"SuperUnit",new[]{"Workshop"}),"uncommitted reward rejected");
   Check(b.PendingCityRewardCountFor(new State(null),1,5,6)==0,"known native null list is empty");
   Console.WriteLine("RewardDisplayScopeTests passed="+checks+" failed=0");
  }
 }