using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public class RuinsActionFixture
{
    public string NativeReward="Resources"; public int RewardReads,TechReads;
    public string Reward { get { RewardReads++; return NativeReward; } }
    public object Tech { get { TechReads++; if(NativeReward!="FreeTech")throw new Exception("unrelated tech read"); return new TechFixture(); } }
}
public class TechFixture { public string type="FutureNativeTech"; }
public class ImprovementFixture { public string type="Ruin"; }
public class TileFixture { public object improvement=new ImprovementFixture(); }
public class ThrowingTileFixture { public object improvement { get { throw new Exception("unreadable"); } } }
internal static class RuinsSettlementTests
{
    private const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private static int passed;
    private static readonly JavaScriptSerializer J=new JavaScriptSerializer();
    private static void Check(bool ok,string label) { if(!ok)throw new Exception(label); passed++; Console.WriteLine("PASS "+label); }
    private static object Get(object o,string name) { return o.GetType().GetField(name,F).GetValue(o); }
    private static void Set(object o,string name,object value) { o.GetType().GetField(name,F).SetValue(o,value); }
    private static BridgeMod.CombatFrame Frame(bool ruin)
    {
        var f=new BridgeMod.CombatFrame { Episode="ep-one",Me="2",Acting="2",Turn="9",Phase="Started",Complete=true };
        f.Units["7"]=new BridgeMod.CombatUnitView { Id="7",Owner="2",Coord="(2,3)",Type="Warrior",Health=100 };
        f.Units["8"]=new BridgeMod.CombatUnitView { Id="8",Owner="3",Coord="(3,3)",Type="Warrior",Health=100 };
        f.RuinsUnderOwnUnits["(2,3)"]=ruin;
        return f;
    }
    private static BridgeMod.CombatTicket Ticket(BridgeMod h)
    { return new BridgeMod.CombatTicket { Id=1,Action="examine_ruins",UnitId="7",Coord="(2,3)",Before=Frame(true),
        Stream=(string)Get(h,"_combatStream"),Accepted=true,Dispatched=true,BeforeFingerprint="same" }; }
    private static BridgeMod.CombatCause Cause()
    { return new BridgeMod.CombatCause { ActionType="ExamineRuinsAction",ActorId="7",ActorOwner="2",TargetCoord="(2,3)",TargetId="7" }; }
    private static void Proof(BridgeMod h,BridgeMod.CombatTicket t,object action,BridgeMod.CombatFrame before,BridgeMod.CombatFrame after)
    {
        var p=Activator.CreateInstance(typeof(BridgeMod).GetNestedType("CombatHookPass",F),true);
        Set(p,"Before",before); Set(p,"Action",action); Set(p,"Cause",Cause()); Set(p,"CandidateKind","ExamineRuinsAction");
        Set(p,"CandidateCoord","(2,3)"); ((IList)Get(p,"ProofTickets")).Add(t);
        typeof(BridgeMod).GetMethod("CRRecordNativeProof",F).Invoke(h,new object[] {p,after});
    }
    private static bool Record(BridgeMod.CombatTicket t,RuinsActionFixture a,BridgeMod.CombatFrame before,BridgeMod.CombatFrame after,BridgeMod.CombatCause cause=null)
    { return BridgeMod.CRRecordRuins(t,a,before,after,cause??Cause()); }
    public static int Main()
    {
        try
        {
            var h=new BridgeMod(); var t=Ticket(h); var action=new RuinsActionFixture(); var before=Frame(true); var after=Frame(false);
            var tile=new TileFixture();
            Check(BridgeMod.ReadRuinsOnOwnTile(tile)==true,"read actual native Ruin type");
            tile.improvement=null; Check(BridgeMod.ReadRuinsOnOwnTile(tile)==false,"readable null improvement proves no ruin");
            tile.improvement=new object(); Check(BridgeMod.ReadRuinsOnOwnTile(tile)==null,"unknown improvement type stays unknown");
            Check(BridgeMod.ReadRuinsOnOwnTile(new object())==null,"missing improvement accessor is not absence");
            Check(BridgeMod.ReadRuinsOnOwnTile(new ThrowingTileFixture())==null,"throwing accessor stays unknown");
            Check(!Record(t,action,before,before) && action.RewardReads==0,"no removal cannot disclose pending reward");
            var absent=Frame(false); absent.RuinsUnderOwnUnits.Clear();
            Check(!Record(t,action,before,absent),"missing after observation cannot prove removal");
            absent=Frame(true); absent.RuinsUnderOwnUnits.Clear();
            Check(!Record(t,action,absent,after),"missing before observation cannot prove ruin existence");
            Check(!Record(t,action,Frame(false),after),"native Execute without a previous ruin cannot prove consumption");
            var wrong=Cause(); wrong.ActorId="8"; wrong.ActorOwner="3";
            Check(!Record(t,action,before,after,wrong),"opponent exploration cannot satisfy own ticket");
            wrong=Cause(); wrong.TargetCoord="(3,3)";
            Check(!Record(t,action,before,after,wrong),"different target does not satisfy original request");
            wrong=Cause(); wrong.ActionType="IncreaseCurrencyAction";
            Check(!Record(t,action,before,after,wrong),"star increase cannot substitute native ExamineRuinsAction");
            var partial=Frame(false); partial.Complete=false;
            Check(!Record(t,action,before,partial),"partial after frame cannot establish native consumption");
            partial=Frame(false); partial.Episode="other";
            Check(!Record(t,action,before,partial),"proof cannot cross episode");
            partial=Frame(false); partial.Me="3";
            Check(!Record(t,action,before,partial),"proof cannot cross seats");
            t.Accepted=false; Check(!Record(t,action,before,after),"rejected command does not acquire proof"); t.Accepted=true;
            t.FinalFrame=after; Check(!Record(t,action,before,after),"settled ticket cannot acquire later proof"); t.FinalFrame=null;
            t.UnitId="99"; Check(!Record(t,action,before,after),"different own unit cannot satisfy actor binding"); t.UnitId="7";
            t.Stream="old"; Proof(h,t,action,before,after);
            Check(t.NativeRuins==null && action.RewardReads==0,"production proof rejects stale stream before reward read");
            t.Stream=(string)Get(h,"_combatStream"); Proof(h,t,action,before,after);
            Check(BridgeMod.CRRuinsConfirmed(t,after),"production Execute proof and visible ruin removal confirm interaction");
            Check(t.NativeRuins.Reward=="Resources" && action.RewardReads==1 && action.TechReads==0,"read native completed selection without guessing from stars or reading unrelated tech");
            var outcome=J.Deserialize<Dictionary<string,object>>(BridgeMod.CRRuinsJson(t,after));
            Check((bool)outcome["ruinConsumed"] && outcome["rewardAmount"]==null && outcome["rewardApplied"]==null,"queued reward application and amount remain explicitly unknown");
            Check(BridgeMod.CRRuinsConfirmed(t,Frame(true)),
                "historical consumption remains a proved event independent of current tile observation");
            var finalWrong=Frame(false); finalWrong.Episode="other";
            Check(!BridgeMod.CRRuinsConfirmed(t,finalWrong) && BridgeMod.CRRuinsJson(t,finalWrong)=="null","final scope mismatch cannot return ruins outcome");
            t.Action="move"; Check(!BridgeMod.CRRuinsConfirmed(t,after),"ruins proof cannot be reused for another action"); t.Action="examine_ruins";
            var unknown=Ticket(h); var unknownAction=new RuinsActionFixture { NativeReward=null };
            Check(Record(unknown,unknownAction,before,after),"consumption can be confirmed even if native reward accessor is unavailable");
            outcome=J.Deserialize<Dictionary<string,object>>(BridgeMod.CRRuinsJson(unknown,after));
            Check((string)outcome["rewardStatus"]=="unavailable" && outcome["nativeReward"]==null,"unknown reward is not inferred as resources");
            var tech=Ticket(h); action.NativeReward="FreeTech";
            Check(Record(tech,action,before,after) && tech.NativeRuins.Tech=="FutureNativeTech","completed FreeTech reports actual selected native tech");
            t.FinalFrame=after; t.FinalFingerprint="same";
            ((Dictionary<long,BridgeMod.CombatTicket>)Get(h,"_combatTickets"))[1]=t;
            var reply=J.Deserialize<Dictionary<string,object>>(h.CombatSettlementJson(null,1));
            Check((string)reply["effectConfirmation"]=="confirmed" && (bool)reply["actionCompleted"],"final settlement confirms target-bound interaction without star delta");
            Check((string)((Dictionary<string,object>)reply["ruinsOutcome"])["nativeReward"]=="Resources","frozen settlement includes native reward selection");
            Console.WriteLine("ALL PASS "+passed); return 0;
        }
        catch(Exception e) { Console.WriteLine(e); return 1; }
    }
}
