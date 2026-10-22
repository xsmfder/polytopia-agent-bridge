// Native-shaped production receipt tests; no live reward is fabricated.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public class GameState { public RewardStack ActionStack = new RewardStack(); }
public class RewardStack
{
    public List<object> Items = new List<object>(); public int FirstAllowed, Reads, FailAt=-1;
    public int Count { get { return Items.Count; } }
    public object this[int i] { get { Reads++; if(i<FirstAllowed || i==FailAt)throw new Exception("unreadable index"); return Items[i]; } }
}
public class ExamineRuinsAction { public string Reward="Resources"; }
public class IncreaseCurrencyAction
{
    public string PlayerId="2", Source="(2,3)"; public int Value=1, AmountReads;
    public int Amount { get { AmountReads++; return Value; } }
}
public class BadNativeCast
{
    public T TryCast<T>() { throw new Exception("cast unavailable"); }
}
internal static class RuinsCurrencyTests
{
    private const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static int passed;
    private static readonly JavaScriptSerializer J=new JavaScriptSerializer();
    private static void Check(bool b,string label) { if(!b)throw new Exception(label); passed++; Console.WriteLine("PASS "+label); }
    private static object Get(object o,string n) { return o.GetType().GetField(n,F).GetValue(o); }
    private static void Set(object o,string n,object v) { o.GetType().GetField(n,F).SetValue(o,v); }
    private static object Call(BridgeMod h,string n,params object[] a) { return typeof(BridgeMod).GetMethod(n,F).Invoke(h,a); }
    private static BridgeMod.CombatFrame Frame(bool ruin,string stars="10")
    {
        var f=new BridgeMod.CombatFrame {Episode="ep",Me="2",Stars=stars,Complete=true};
        f.Units["7"]=new BridgeMod.CombatUnitView {Id="7",Owner="2",Coord="(2,3)"};
        f.RuinsUnderOwnUnits["(2,3)"]=ruin; return f;
    }
    private static BridgeMod.CombatTicket Ticket(BridgeMod h)
    { return new BridgeMod.CombatTicket {Id=1,Action="examine_ruins",UnitId="7",Coord="(2,3)",Accepted=true,Before=Frame(true),Stream=(string)Get(h,"_combatStream")}; }
    private static void Proof(BridgeMod h,BridgeMod.CombatTicket t,object action,string kind,GameState gs,int? oldCount,BridgeMod.CombatFrame before,BridgeMod.CombatFrame after)
    {
        var p=Activator.CreateInstance(typeof(BridgeMod).GetNestedType("CombatHookPass",F),true);
        Set(p,"Action",action); Set(p,"State",gs); Set(p,"BeforeStackCount",oldCount); Set(p,"Before",before); Set(p,"CandidateKind",kind);
        Set(p,"Cause",new BridgeMod.CombatCause {ActionType=kind,ActorId="7",ActorOwner="2",TargetCoord="(2,3)"});
        ((IList)Get(p,"ProofTickets")).Add(t); Call(h,"CRRecordNativeProof",p,after);
    }
    private static Dictionary<string,object> Outcome(BridgeMod.CombatTicket t)
    { return J.Deserialize<Dictionary<string,object>>(BridgeMod.CRRuinsJson(t,Frame(false))); }
    private static BridgeMod.CombatTicket Bind(BridgeMod h,GameState gs,int? count)
    { var t=Ticket(h); Proof(h,t,new ExamineRuinsAction(),"ExamineRuinsAction",gs,count,Frame(true),Frame(false)); return t; }
    private static bool Record(BridgeMod.CombatTicket t,object a,string before="10",string after="11")
    { return BridgeMod.CRRecordRuinsCurrency(t,a,Frame(false,before),Frame(false,after)); }
    public static int Main()
    { try { Run(); Console.WriteLine("ALL PASS "+passed); return 0; } catch(Exception e) {Console.WriteLine(e); return 1;} }
    private static void Run()
    {
        var h=new BridgeMod(); var gs=new GameState(); var old=new IncreaseCurrencyAction(); var a=new IncreaseCurrencyAction(); var b=new IncreaseCurrencyAction {Value=4};
        gs.ActionStack.Items.AddRange(new object[] {old,a,new object(),b}); gs.ActionStack.FirstAllowed=1;
        var t=Bind(h,gs,1);
        Check(t.NativeRuins.CurrencyChildrenComplete && t.NativeRuins.CurrencyChildren.Count==2 && gs.ActionStack.Reads==3,"bind exact new children without old history");
        Check(a.AmountReads==0 && b.AmountReads==0,"pending Amount not read or published");
        Check(ReferenceEquals(t.NativeRuins.CurrencyChildren[0],a),"binding retains exact child object");
        Check(Outcome(t)["rewardApplied"]==null && Outcome(t)["rewardAmount"]==null,"queued reward is not applied");
        Check(!Record(t,old) && old.AmountReads==0,"old income cannot satisfy reward");
        var unrelated=new IncreaseCurrencyAction(); Check(!Record(t,unrelated) && unrelated.AmountReads==0,"same fields are insufficient without exact native child");
        a.PlayerId="3"; Check(!Record(t,a) && a.AmountReads==0,"other player rejected before Amount read"); a.PlayerId="2";
        a.Source="(9,9)"; Check(!Record(t,a) && a.AmountReads==0,"other source rejected before Amount read"); a.Source="(2,3)";
        Check(!Record(t,a,"10","12"),"unrelated extra income is not attributed to one-star child");
        Check(!Record(t,a,"10",null),"unreadable after currency is unknown");
        Check(!Record(t,a,null,"11"),"unreadable before currency is unknown");
        Check(!Record(t,a,"-1","0"),"invalid negative currency rejected");
        a.Value=0; Check(!Record(t,a,"10","10"),"zero amount does not prove reward"); a.Value=1;
        Check(!Record(t,a,"2147483647","-2147483648"),"overflow cannot prove positive income");
        var wrong=Frame(false,"11"); wrong.Episode="other";
        Check(!BridgeMod.CRRecordRuinsCurrency(t,a,Frame(false),wrong),"child cannot cross episode");
        wrong=Frame(false,"11"); wrong.Me="3";
        Check(!BridgeMod.CRRecordRuinsCurrency(t,a,Frame(false),wrong),"child cannot cross seat");
        wrong=Frame(false,"11"); wrong.Complete=false;
        Check(!BridgeMod.CRRecordRuinsCurrency(t,a,Frame(false),wrong),"partial capture cannot prove reward");
        Check(Record(t,a),"first exact child proves actual star credit");
        var o=Outcome(t); Check((string)o["rewardStatus"]=="partially_applied" && (int)o["observedAppliedAmount"]==1 && o["rewardAmount"]==null,"partial credit stays distinct from full amount");
        int reads=a.AmountReads; Check(!Record(t,a) && a.AmountReads==reads,"duplicate Execute receipt not double counted");
        Proof(h,t,b,"IncreaseCurrencyAction",gs,null,Frame(false,"100"),Frame(false,"104"));
        o=Outcome(t); Check((bool)o["rewardApplied"] && (int)o["rewardAmount"]==5 && (string)o["rewardUnit"]=="stars","production child proof confirms sum independent of overall final stars");
        Check((string)o["rewardStatus"]=="applied","all associated children applied status");
        t.FinalFrame=Frame(false); Check(!Record(t,a),"frozen ticket cannot acquire late credit"); t.FinalFrame=null;
        t.Accepted=false; Check(!Record(t,a),"rejected ticket cannot acquire credit"); t.Accepted=true;
        t.Action="move"; Check(!Record(t,a),"proof cannot move to another action"); t.Action="examine_ruins";
        var stale=Bind(h,gs,1); stale.Stream="old";
        Proof(h,stale,a,"IncreaseCurrencyAction",gs,null,Frame(false),Frame(false,"11"));
        Check(stale.NativeRuins.CurrencyReceipts.Count==0,"production child proof rejects old stream");
        var noCount=Bind(h,gs,null); Check(!noCount.NativeRuins.CurrencyChildrenComplete && Outcome(noCount)["rewardApplied"]==null,"missing initial Count cannot imply all children applied");
        var shortStack=Bind(h,gs,5); Check(!shortStack.NativeRuins.CurrencyChildrenComplete,"shrinking action stack is incomplete");
        gs.ActionStack.FailAt=3; var partial=Bind(h,gs,1); gs.ActionStack.FailAt=-1;
        Check(!partial.NativeRuins.CurrencyChildrenComplete && partial.NativeRuins.CurrencyChildren.Count==1,"failed suffix index cannot silently shorten expected children");
        Check(Record(partial,a) && Outcome(partial)["rewardApplied"]==null,"known credit cannot complete an unreadable child list");
        var empty=Bind(h,gs,4); Check(empty.NativeRuins.CurrencyChildrenComplete && Outcome(empty)["rewardApplied"]==null,"empty suffix is not a zero-star success");
        gs.ActionStack.Items.Add(new BadNativeCast()); var badCast=Bind(h,gs,1);
        Check(!badCast.NativeRuins.CurrencyChildrenComplete,"native cast failure keeps child discovery incomplete");
        gs.ActionStack.Items.RemoveAt(4); gs.ActionStack.Items.Add(null); var nullChild=Bind(h,gs,1);
        Check(!nullChild.NativeRuins.CurrencyChildrenComplete,"null native stack entry keeps discovery incomplete");
        gs.ActionStack.Items[4]=a; var duplicateChild=Bind(h,gs,1);
        Check(!duplicateChild.NativeRuins.CurrencyChildrenComplete,"repeated native object cannot shorten the expected execution sequence");
        var tech=Ticket(h); Proof(h,tech,new ExamineRuinsAction {Reward="FreeTech"},"ExamineRuinsAction",gs,1,Frame(true),Frame(false));
        Check(tech.NativeRuins.CurrencyChildren.Count==0 && !Record(tech,a),"non-resource reward cannot use currency evidence");
        Proof(h,t,new ExamineRuinsAction(),"ExamineRuinsAction",gs,1,Frame(true),Frame(false));
        Check(t.NativeRuins.CurrencyReceipts.Count==2 && (int)Outcome(t)["rewardAmount"]==5,"duplicate parent cannot reset proved children");
        var tickets=(Dictionary<long,BridgeMod.CombatTicket>)Get(h,"_combatTickets"); tickets[1]=t;
        Check((bool)Call(h,"CRHasPendingRuinsCurrency",a),"fast gate admits exact unsettled child");
        Check(!(bool)Call(h,"CRHasPendingRuinsCurrency",unrelated),"fast gate skips unrelated income before sampling");
        t.FinalFrame=Frame(false); Check(!(bool)Call(h,"CRHasPendingRuinsCurrency",a),"fast gate stops after settlement");
    }
}
