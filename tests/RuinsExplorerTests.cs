// Real production linkage and effect serialization against managed native-shaped fixtures.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public class GameState { public ExplorerStack ActionStack = new ExplorerStack(); }
public class ExplorerStack
{
    public List<object> Items = new List<object>(); public int FirstAllowed, FailAt = -1, Reads;
    public int Count { get { return Items.Count; } }
    public object this[int i] { get { Reads++; if (i < FirstAllowed || i == FailAt) throw new Exception("unreadable suffix"); return Items[i]; } }
}
public class ExamineRuinsAction { public string Reward = "Explorer"; }
public class ScoutMoveAction
{
    public string PlayerId = "2";
    public string From { get { throw new Exception("do not disclose route"); } }
    public string To { get { throw new Exception("do not disclose future target"); } }
    public uint UnitId { get { throw new Exception("private scout identity"); } }
    public object Path { get { throw new Exception("private route"); } }
}
public class ExploreAction { public string PlayerId = "2", Coordinates = "(1,1)"; }
public class UnboundExplorer
{ public string PlayerId { get { throw new Exception("unlinked action read"); } } }

internal static class RuinsExplorerTests
{
    private const BindingFlags F = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly JavaScriptSerializer J = new JavaScriptSerializer();
    private static int passed;
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    private static object Get(object o, string n) { return o.GetType().GetField(n,F).GetValue(o); }
    private static void Set(object o, string n, object v) { o.GetType().GetField(n,F).SetValue(o,v); }
    private static object Call(BridgeMod h,string n,params object[] a) { return typeof(BridgeMod).GetMethod(n,F).Invoke(h,a); }
    private static BridgeMod.CombatFrame Frame(bool ruin=false)
    {
        var f=new BridgeMod.CombatFrame {Episode="ep",Me="2",Turn="9",Complete=true};
        f.Units["7"]=new BridgeMod.CombatUnitView {Id="7",Owner="2",Coord="(2,3)"};
        f.RuinsUnderOwnUnits["(2,3)"]=ruin; return f;
    }
    private static void Proof(BridgeMod h,BridgeMod.CombatTicket t,object action,string kind,GameState gs,int? oldCount,
        BridgeMod.CombatFrame before=null,BridgeMod.CombatFrame after=null)
    {
        var p=Activator.CreateInstance(typeof(BridgeMod).GetNestedType("CombatHookPass",F),true);
        Set(p,"Action",action);Set(p,"State",gs);Set(p,"CandidateKind",kind);Set(p,"BeforeStackCount",oldCount);
        Set(p,"Before",before??Frame());Set(p,"Cause",new BridgeMod.CombatCause {ActionType=kind,ActorId="7",ActorOwner="2",TargetCoord="(2,3)"});
        ((IList)Get(p,"ProofTickets")).Add(t);Call(h,"CRRecordNativeProof",p,after??Frame());
    }
    private static BridgeMod.CombatTicket Bind(BridgeMod h,GameState gs,int? oldCount=0)
    {
        var t=new BridgeMod.CombatTicket {Id=1,Action="examine_ruins",UnitId="7",Coord="(2,3)",Before=Frame(true),Accepted=true,Stream=(string)Get(h,"_combatStream")};
        Proof(h,t,new ExamineRuinsAction(),"ExamineRuinsAction",gs,oldCount,Frame(true)); return t;
    }
    private static Dictionary<string,object> Outcome(BridgeMod.CombatTicket t)
    { return J.Deserialize<Dictionary<string,object>>(BridgeMod.CRRuinsJson(t,Frame())); }
    private static bool Explore(BridgeMod.CombatTicket t,object a,bool? before=false,bool? after=true,
        BridgeMod.CombatFrame frame=null)
    { return BridgeMod.CRRecordRuinsExplore(t,a,Frame(),frame??Frame(),before,after); }
    public static int Main()
    { try { Run();Console.WriteLine("ALL PASS "+passed);return 0; }catch(Exception e){Console.WriteLine(e);return 1;} }
    private static void Run()
    {
        var h=new BridgeMod();var gs=new GameState();var scout=new ScoutMoveAction();gs.ActionStack.Items.Add(scout);
        var t=Bind(h,gs);
        Check(t.NativeRuins.Explorer.Scouts.Count==1&&t.NativeRuins.Explorer.ChildrenReadable,"root binds exact queued scout");
        var initial=Outcome(t);
        Check(initial["rewardApplied"]==null&&initial["rewardAmount"]==null
            &&((IList)((Dictionary<string,object>)initial["exploration"])["newlyExploredTiles"]).Count==0,
            "pending scout does not disclose path or anticipated reveal tiles");
        Check(!BridgeMod.CRRuinsExplorerChildMatches(t,new UnboundExplorer(),"ScoutMoveAction"),"unlinked metadata never inspected");
        Check(!BridgeMod.CRRuinsExplorerChildMatches(t,new ScoutMoveAction(),"ScoutMoveAction"),"same-field different scout cannot acquire proof");
        scout.PlayerId="3";Check(!BridgeMod.CRRuinsExplorerChildMatches(t,scout,"ScoutMoveAction"),"opponent scout is not own reward");scout.PlayerId="2";
        ((Dictionary<long,BridgeMod.CombatTicket>)Get(h,"_combatTickets"))[1]=t;
        Check((bool)Call(h,"CRHasPendingRuinsExplorer",scout,"ScoutMoveAction"),"linked own scout opens narrow hook sampler");
        t.Stream="stale";Check(!(bool)Call(h,"CRHasPendingRuinsExplorer",scout,"ScoutMoveAction"),"stale stream cannot enable sampler");t.Stream=(string)Get(h,"_combatStream");
        var reveal=new ExploreAction();var known=new ExploreAction {Coordinates="(0,1)"};var next=new ScoutMoveAction();
        gs.ActionStack.Items.AddRange(new object[]{reveal,known,next});gs.ActionStack.FirstAllowed=1;
        Proof(h,t,scout,"ScoutMoveAction",gs,1);
        Check(t.NativeRuins.Explorer.Scouts.Count==2&&t.NativeRuins.Explorer.Explores.Count==2
            &&t.NativeRuins.Explorer.CompletedScouts.Count==1,"executed scout links only its new scout/explore suffix");
        Check(Outcome(t)["rewardApplied"]==null,"appended reveal and next scout remain uncredited");
        Check(!Explore(t,new ExploreAction()),"unrelated same-coordinate reveal cannot credit reward");
        reveal.PlayerId="3";Check(!Explore(t,reveal),"foreign reveal cannot credit own map");reveal.PlayerId="2";
        Check(!Explore(t,reveal,null,true)&&!Explore(t,reveal,false,null)&&!Explore(t,reveal,false,false),
            "unknown bits and unexecuted reveal are not coerced into gained tiles");
        var wrong=Frame();wrong.Episode="other";Check(!Explore(t,reveal,false,true,wrong),"reveal receipt cannot cross episode");
        wrong=Frame();wrong.Me="3";Check(!Explore(t,reveal,false,true,wrong),"reveal receipt cannot cross seat");
        wrong=Frame();wrong.Turn="10";Check(!Explore(t,reveal,false,true,wrong),"reveal receipt cannot cross turn");
        wrong=Frame();wrong.Complete=false;Check(!Explore(t,reveal,false,true,wrong),"partial frame cannot prove reveal");
        Check(Explore(t,reveal)&&!Explore(t,reveal),"newly revealed tile credited once for exact executed child");
        var partial=Outcome(t);
        Check((string)partial["rewardStatus"]=="partially_applied"&&partial["rewardAmount"]==null
            &&Convert.ToInt32(partial["observedAppliedAmount"])==1,"partial reveal reports only observed count");
        Check(Explore(t,known,true,true),"already explored tile can finish child without inventing new area");
        Proof(h,t,next,"ScoutMoveAction",gs,4);
        var result=Outcome(t);var exploration=(Dictionary<string,object>)result["exploration"];
        Check((bool)result["rewardApplied"]&&(string)result["rewardUnit"]=="tiles"&&Convert.ToInt32(result["rewardAmount"])==1,
            "complete executed explorer chain confirms actual one-tile gain");
        Check(((IList)exploration["newlyExploredTiles"]).Count==1&&(string)((IList)exploration["newlyExploredTiles"])[0]=="(1,1)",
            "only newly revealed public tile is serialized, not path or old tile");
        reveal.Coordinates="(9,9)";Check((string)((IList)((Dictionary<string,object>)Outcome(t)["exploration"])["newlyExploredTiles"])[0]=="(1,1)",
            "receipt remains frozen when native child changes later");
        Check(!BridgeMod.CRRecordRuinsScout(t,next,Frame(),Frame(),gs,4),"completed scout is not appended again");
        t.FinalFrame=Frame();Check(!Explore(t,reveal),"finalized ticket cannot acquire late reveal proof");
        var emptyGs=new GameState();var emptyScout=new ScoutMoveAction();emptyGs.ActionStack.Items.Add(emptyScout);
        var empty=Bind(h,emptyGs);Proof(h,empty,emptyScout,"ScoutMoveAction",emptyGs,1);
        Check((bool)Outcome(empty)["rewardApplied"]&&Convert.ToInt32(Outcome(empty)["rewardAmount"])==0,
            "readable executed zero-gain chain is known zero, distinct from unknown");
        var brokenGs=new GameState();var brokenScout=new ScoutMoveAction();brokenGs.ActionStack.Items.Add(brokenScout);
        var broken=Bind(h,brokenGs);brokenGs.ActionStack.Items.Add(new ExploreAction());brokenGs.ActionStack.FailAt=1;
        Proof(h,broken,brokenScout,"ScoutMoveAction",brokenGs,1);
        Check(!broken.NativeRuins.Explorer.ChildrenReadable&&Outcome(broken)["rewardApplied"]==null,
            "failed suffix read cannot shorten chain to a falsely complete zero");
        var missing=Bind(h,new GameState());Check(Outcome(missing)["rewardApplied"]==null,"no native root scout is not inferred as applied");
    }
}
