// Managed native-shaped integration fixture; this does not claim a new live battle.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public class GameState { public GuardedStack ActionStack = new GuardedStack(); }
public class PlayerState { public byte Id = 2; }
public static class GameManager
{ public static GameState GameState { get; set; } public static PlayerState LocalPlayer { get; set; } }
public class UnitState
{
    public byte owner = 2; public uint UnitId = 7; public int IdReads;
    public uint id { get { IdReads++; if (owner != 2) throw new Exception("foreign ID read"); return UnitId; } }
}
public class AttackAction
{
    public string Animation = "Splash";
    public string Target { get { throw new Exception("pending target must not be read"); } }
}
public class GuardedStack
{
    public List<object> Items = new List<object>(); public int FirstAllowed, Reads;
    public int Count { get { return Items.Count; } }
    public object this[int index]
    { get { Reads++; if (index < FirstAllowed) throw new Exception("old history read"); return Items[index]; } }
}
internal static class AreaSettlementTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int passed;
    private static readonly JavaScriptSerializer J = new JavaScriptSerializer();
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static object Call(BridgeMod h, string name, params object[] args)
    { return typeof(BridgeMod).GetMethod(name, F).Invoke(h, args); }
    private static void Set(object o, string n, object v)
    { (o as Type ?? o.GetType()).GetField(n, F).SetValue(o is Type ? null : o, v); }
    private static object Get(object o, string n) { return o.GetType().GetField(n, F).GetValue(o); }
    private static BridgeMod.CombatUnitView Unit(string id, string owner, string coord, int? hp)
    { return new BridgeMod.CombatUnitView { Id=id, Owner=owner, Coord=coord, Health=hp, Type="Warrior", Attacked=false, Moved=false }; }
    private static BridgeMod.CombatFrame Clone(BridgeMod.CombatFrame f)
    {
        var r = new BridgeMod.CombatFrame { Episode=f.Episode, Me=f.Me, Turn=f.Turn, Acting=f.Acting, Phase=f.Phase, Complete=f.Complete };
        foreach(var x in f.Units) r.Units[x.Key]=x.Value;
        return r;
    }
    private static BridgeMod.CombatTicket Ticket(BridgeMod h, string ep)
    {
        var f = new BridgeMod.CombatFrame { Episode=ep, Me="2", Turn="5", Acting="2", Phase="Started", Complete=true };
        f.Units["7"]=Unit("7","2","(1,1)",100);
        f.Units["8"]=Unit("8","3","(3,3)",100);
        f.Units["9"]=Unit("9","3","(3,2)",100);
        return new BridgeMod.CombatTicket { Id=1, Action="attack", UnitId="7", Coord="(3,3)", Before=f,
            Accepted=true, Dispatched=true, Stream=(string)Get(h,"_combatStream") };
    }
    private static object Begin(GameState gs, UnitState u, string center)
    {
        var args=new object[] { gs,u,center,null };
        Call(null,"AreaDamagePrefix",args); return args[3];
    }
    private static void End(object pass, bool failed)
    { Call(null,"CloseAreaDamage",pass,failed); }
    private static BridgeMod.CombatCause Cause(string kind="AttackAction",string actor="7",string target="9",string coord="(3,2)")
    { return new BridgeMod.CombatCause { ActionType=kind,ActorId=actor,ActorOwner="2",TargetId=target,TargetCoord=coord }; }
    private static void Proof(BridgeMod h, BridgeMod.CombatTicket t, object action, BridgeMod.CombatFrame before,
        BridgeMod.CombatFrame after, BridgeMod.CombatCause cause)
    {
        var p=Activator.CreateInstance(typeof(BridgeMod).GetNestedType("CombatHookPass",F),true);
        Set(p,"Before",before); Set(p,"Action",action); Set(p,"Cause",cause);
        Set(p,"CandidateKind",cause.ActionType); Set(p,"CandidateCoord",cause.TargetCoord);
        ((IList)Get(p,"ProofTickets")).Add(t);
        Call(h,"CRRecordNativeProof",p,after);
    }
    private static Dictionary<string,object> Row(BridgeMod.CombatTicket t)
    { return J.Deserialize<Dictionary<string,object>>(BridgeMod.CRAreaDamageJson(t)[0]); }
    public static int Main()
    { try { Run(); Console.WriteLine("ALL PASS " + passed); return 0; } catch(Exception e) { Console.WriteLine(e); return 1; } }
    private static void Run()
    {
        var h=new BridgeMod(); var gs=new GameState(); GameManager.GameState=gs; GameManager.LocalPlayer=new PlayerState();
        Set(h,"_gameStateProp",typeof(GameManager).GetProperty("GameState")); Set(h,"_gameManagerType",typeof(GameManager));
        Set(typeof(BridgeMod),"_combatHookHost",h);
        string ep=(string)Call(h,"EpisodeId",gs);
        var t=Ticket(h,ep); var tickets=(Dictionary<long,BridgeMod.CombatTicket>)Get(h,"_combatTickets"); tickets[1]=t;
        var actor=new UnitState(); var foreign=new UnitState { owner=3 };
        Check(Begin(gs,foreign,t.Coord)==null && foreign.IdReads==0,"foreign helper refused before unit ID access");
        Check(Begin(new GameState(),actor,t.Coord)==null,"different GameState refused");
        Check(Begin(gs,actor,"(0,0)")==null,"different helper center refused");
        Check(Begin(gs,new UnitState { UnitId=99 },t.Coord)==null,"unrelated own actor refused");
        t.Before.Units["7"].PassengerId="70";
        Check(Begin(gs,new UnitState { UnitId=70 },t.Coord)!=null,"known native passenger accepted");
        t.Before.Units["7"].PassengerId=null;
        t.Stream="old"; Check(Begin(gs,actor,t.Coord)==null,"old stream refused"); t.Stream=(string)Get(h,"_combatStream");
        t.Before.Episode="old"; Check(Begin(gs,actor,t.Coord)==null,"old episode refused"); t.Before.Episode=ep;
        t.Accepted=false; Check(Begin(gs,actor,t.Coord)==null,"rejected helper refused"); t.Accepted=true;
        t.Before.Complete=false; Check(Begin(gs,actor,t.Coord)==null,"incomplete origin frame refused"); t.Before.Complete=true;
        t.FinalFrame=Clone(t.Before); Check(Begin(gs,actor,t.Coord)==null,"settled ticket refused"); t.FinalFrame=null;
        tickets[2]=Ticket(h,ep); Check(Begin(gs,actor,t.Coord)==null,"ambiguous matching tickets refused"); tickets.Remove(2);
        var prior=new AttackAction(); gs.ActionStack.Items.Add(prior); gs.ActionStack.FirstAllowed=1;
        var pass=Begin(gs,actor,t.Coord); Check(pass!=null,"own helper captures initial action count");
        var splash=new AttackAction(); gs.ActionStack.Items.AddRange(new object[] { new object(),splash });
        End(pass,false);
        Check(t.NativeAreaActions.Count==1 && ReferenceEquals(t.NativeAreaActions[0],splash) && gs.ActionStack.Reads==2,
            "capture reads appended range only and keeps exact attack object without pending fields");
        End(pass,false); Check(t.NativeAreaActions.Count==1 && gs.ActionStack.Reads==2,"postfix and finalizer close once");
        pass=Begin(gs,actor,t.Coord); gs.ActionStack.Items.Add(new AttackAction()); End(pass,true);
        Check(t.NativeAreaActions.Count==1,"failed helper does not acquire children");
        pass=Begin(gs,actor,t.Coord); gs.ActionStack.Items.Add(new AttackAction()); GameManager.LocalPlayer.Id=3; End(pass,false);
        Check(t.NativeAreaActions.Count==1,"seat changes before helper close refuse capture"); GameManager.LocalPlayer.Id=2;
        pass=Begin(gs,actor,t.Coord); gs.ActionStack.Items.Add(new AttackAction()); t.Accepted=false; End(pass,false);
        Check(t.NativeAreaActions.Count==1,"rejection before helper close refuses capture"); t.Accepted=true;
        var before=Clone(t.Before); var after=Clone(before); after.Units["9"]=Unit("9","3","(3,2)",83);
        Check(!BridgeMod.CRRecordAreaDamage(t,prior,before,after,Cause(),"(3,2)","Splash"),"unassociated attack cannot become collateral receipt");
        var bad=Clone(before); bad.Units.Remove("9");
        Check(!BridgeMod.CRRecordAreaDamage(t,splash,bad,after,Cause(),"(3,2)","Splash"),"unseen victim has no receipt");
        bad=Clone(after); bad.Units["9"]=Unit("9","3","(3,2)",null);
        Check(!BridgeMod.CRRecordAreaDamage(t,splash,before,bad,Cause(),"(3,2)","Splash"),"unknown HP cannot become zero or damage");
        bad=Clone(after); bad.Units.Remove("9");
        Check(!BridgeMod.CRRecordAreaDamage(t,splash,before,bad,Cause(),"(3,2)","Splash"),"disappearance is not damage or death");
        bad=Clone(after); bad.Units["9"]=Unit("9","2","(3,2)",83);
        Check(!BridgeMod.CRRecordAreaDamage(t,splash,before,bad,Cause(),"(3,2)","Splash"),"changed owner cannot substitute damage");
        bad=Clone(after); bad.Units["9"]=Unit("9","3","(4,2)",83);
        Check(!BridgeMod.CRRecordAreaDamage(t,splash,before,bad,Cause(),"(3,2)","Splash"),"changed tile cannot substitute damage");
        bad=Clone(after); bad.Episode="old";
        Check(!BridgeMod.CRRecordAreaDamage(t,splash,before,bad,Cause(),"(3,2)","Splash"),"damage cannot cross episode");
        bad=Clone(after); bad.Me="3";
        Check(!BridgeMod.CRRecordAreaDamage(t,splash,before,bad,Cause(),"(3,2)","Splash"),"damage cannot cross seats");
        bad=Clone(after); bad.Complete=false;
        Check(!BridgeMod.CRRecordAreaDamage(t,splash,before,bad,Cause(),"(3,2)","Splash"),"incomplete event frame cannot prove damage");
        Check(!BridgeMod.CRRecordAreaDamage(t,splash,before,before,Cause(),"(3,2)","Splash"),"no HP change does not invent damage");
        Check(!BridgeMod.CRRecordAreaDamage(t,splash,before,after,Cause(actor:"8"),"(3,2)","Splash"),"foreign visible attacker refused");
        Check(!BridgeMod.CRRecordAreaDamage(t,splash,before,after,Cause(),"(0,0)","Splash"),"wrong native target coordinate refused");
        t.Stream="old"; Proof(h,t,splash,before,after,Cause());
        Check(t.NativeAreaDamage.Count==0,"production proof refuses old stream"); t.Stream=(string)Get(h,"_combatStream");
        Proof(h,t,splash,before,after,Cause());
        Check(t.NativeAreaDamage.Count==1 && (string)Row(t)["role"]=="splash" && (bool)Row(t)["death"]==false,
            "production proof yields actual visible splash damage");
        Check(Convert.ToInt32(((Dictionary<string,object>)Row(t)["after"])["health"])==83,"receipt preserves native HP scale");
        Proof(h,t,splash,before,after,Cause()); Check(t.NativeAreaDamage.Count==1,"exact executed action deduplicated");
        var passive=new AttackAction { Animation="Passive" }; t.NativeAreaActions.Add(passive);
        t.Action="move"; var zero=Clone(after); zero.Units["9"]=Unit("9","3","(3,2)",0);
        Proof(h,t,passive,after,zero,Cause());
        var passiveRow=J.Deserialize<Dictionary<string,object>>(BridgeMod.CRAreaDamageJson(t)[1]);
        Check((string)passiveRow["role"]=="movement_area" && passiveRow["death"]==null,"passive damage zero retains unknown death until kill");
        Check(!BridgeMod.CRAreaKillMatches(t,after.Units["9"]),"nonzero HP cannot bind area kill");
        var removed=Clone(zero); removed.Units.Remove("9");
        Proof(h,t,new object(),zero,removed,Cause("KillUnitAction"));
        passiveRow=J.Deserialize<Dictionary<string,object>>(BridgeMod.CRAreaDamageJson(t)[1]);
        Check((bool)passiveRow["death"] && t.NativeAreaKilledIds.Contains("9"),"production kill removal confirms actual area death");
        Check(!BridgeMod.CRAreaKillMatches(t,Unit("88","3","(3,2)",0)),"different zero HP unit cannot inherit kill");
        t.Action="attack"; t.FinalFrame=removed; t.FinalFingerprint="unchanged"; t.BeforeFingerprint="unchanged";
        var reply=J.Deserialize<Dictionary<string,object>>(h.CombatSettlementJson(null,1));
        Check(((IList)reply["actualAffectedUnits"]).Count==2,"final settlement includes both exact executed area actions");
        Check((string)reply["effectConfirmation"]!="confirmed","collateral damage alone cannot confirm main attack");
        Check(((string)reply["actualAffectedUnitsCoverage"]).Contains("partial"),"receipt declares coverage limits");
    }
}
