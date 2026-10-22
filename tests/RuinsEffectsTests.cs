// Production parent/child binding and executed effect receipts, without a game process.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public class GameState { public EffectStack ActionStack = new EffectStack(); }
public class EffectStack
{
    public List<object> Items = new List<object>(); public int FirstAllowed, FailAt = -1, Reads;
    public int Count { get { return Items.Count; } }
    public object this[int i] { get { Reads++; if (i < FirstAllowed || i == FailAt) throw new Exception("unreadable"); return Items[i]; } }
}
public class ExamineRuinsAction { public string Reward = "PopulationGrowth"; }
public class IncreasePopulationAction
{
    public string PlayerId = "2", Source = "(2,3)", Target = "(1,1)";
}
public class TrainAction
{
    public string PlayerId = "2", Coordinates = "(2,3)", Type = "FutureNativeUnit";
    public object Cost = 0, trainActionAborted = false;
}
public class UnrelatedPopulation
{
    public string PlayerId { get { throw new Exception("unbound foreign action read"); } }
    public string Source { get { throw new Exception("unbound source read"); } }
}

internal static class RuinsEffectsTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly JavaScriptSerializer J = new JavaScriptSerializer();
    private static int passed;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static object Get(object obj, string name) { return obj.GetType().GetField(name, F).GetValue(obj); }
    private static void Set(object obj, string name, object value) { obj.GetType().GetField(name, F).SetValue(obj, value); }
    private static object Call(BridgeMod host, string name, params object[] args) { return typeof(BridgeMod).GetMethod(name, F).Invoke(host, args); }
    private static BridgeMod.CombatFrame Frame(bool ruin = false, int pop = 0)
    {
        var f = new BridgeMod.CombatFrame {Episode = "ep", Me = "2", Turn = "9", Complete = true};
        f.Units["7"] = new BridgeMod.CombatUnitView {Id = "7", Owner = "2", Coord = "(2,3)", Type = "Warrior", Health = 100};
        f.OwnImprovements["(1,1)"] = "City:2:" + pop + ":rewards=Workshop";
        f.RuinsUnderOwnUnits["(2,3)"] = ruin; f.CitiesUnderOwnUnits["(2,3)"] = false; return f;
    }
    private static void Proof(BridgeMod h, BridgeMod.CombatTicket t, object action, string kind,
        GameState gs, int? count, BridgeMod.CombatFrame before, BridgeMod.CombatFrame after)
    {
        var pass = Activator.CreateInstance(typeof(BridgeMod).GetNestedType("CombatHookPass", F), true);
        Set(pass,"Action",action); Set(pass,"State",gs); Set(pass,"BeforeStackCount",count);
        Set(pass,"Before",before); Set(pass,"CandidateKind",kind); Set(pass,"CandidateCoord","(2,3)");
        Set(pass,"Cause",new BridgeMod.CombatCause {ActionType=kind,ActorId="7",ActorOwner="2",TargetCoord="(2,3)"});
        ((IList)Get(pass,"ProofTickets")).Add(t); Call(h,"CRRecordNativeProof",pass,after);
    }
    private static BridgeMod.CombatTicket Bind(BridgeMod h, GameState gs, string reward, int? count = 0,
        BridgeMod.CombatFrame after = null)
    {
        var t = new BridgeMod.CombatTicket {Id=1,Action="examine_ruins",UnitId="7",Coord="(2,3)",Accepted=true,
            Before=Frame(true),Stream=(string)Get(h,"_combatStream")};
        Proof(h,t,new ExamineRuinsAction {Reward=reward},"ExamineRuinsAction",gs,count,Frame(true),after ?? Frame()); return t;
    }
    private static Dictionary<string,object> Outcome(BridgeMod.CombatTicket t)
    { return J.Deserialize<Dictionary<string,object>>(BridgeMod.CRRuinsJson(t,Frame())); }
    private static bool Population(BridgeMod.CombatTicket t, object action, BridgeMod.CombatFrame before = null, BridgeMod.CombatFrame after = null)
    { return BridgeMod.CRRecordRuinsPopulation(t,action,before ?? Frame(),after ?? Frame(false,1)); }
    private static BridgeMod.CombatFrame Spawn(string owner = "2", string type = "FutureNativeUnit", string id = "20")
    {
        var f = Frame(); f.Units["7"].Coord = "(2,4)";
        f.Units[id] = new BridgeMod.CombatUnitView {Id=id,Owner=owner,Type=type,Coord="(2,3)",Health=100}; return f;
    }
    public static int Main()
    { try { Run(); Console.WriteLine("ALL PASS " + passed); return 0; } catch(Exception e) { Console.WriteLine(e); return 1; } }
    private static void Run()
    {
        var h = new BridgeMod(); var gs = new GameState();
        var old = new IncreasePopulationAction(); var a = new IncreasePopulationAction();
        var b = new IncreasePopulationAction(); var c = new IncreasePopulationAction();
        gs.ActionStack.Items.AddRange(new object[] {old,a,b,c}); gs.ActionStack.FirstAllowed=1;
        var t = Bind(h,gs,"PopulationGrowth",1);
        Check(t.NativeRuins.PopulationChildrenComplete && t.NativeRuins.PopulationChildren.Count==3 && gs.ActionStack.Reads==3,
            "parent links only newly appended native population children");
        Check(Outcome(t)["rewardApplied"]==null && ((IList)Outcome(t)["populationGains"]).Count==0,
            "queued population children do not disclose expected payout");
        Check(!Population(t,old) && !Population(t,new IncreasePopulationAction()), "old or unrelated population cannot credit ruins");
        Check(!BridgeMod.CRRuinsEffectChildMatches(t,new UnrelatedPopulation(),"IncreasePopulationAction"),
            "unlinked action metadata is not inspected");
        ((Dictionary<long,BridgeMod.CombatTicket>)Get(h,"_combatTickets"))[1]=t;
        Check((bool)Call(h,"CRHasPendingRuinsPopulation",a),"only linked own population child opens expensive sampler");
        t.Stream="stale"; Check(!(bool)Call(h,"CRHasPendingRuinsPopulation",a),"stale stream does not sample population actions");
        t.Stream=(string)Get(h,"_combatStream");
        a.PlayerId="3"; Check(!Population(t,a),"foreign population cannot credit own reward"); a.PlayerId="2";
        a.Source="(9,9)"; Check(!Population(t,a),"population must originate from matched ruin"); a.Source="(2,3)";
        a.Target="(8,8)"; Check(!Population(t,a),"hidden/non-owned city does not enter receipt"); a.Target="(1,1)";
        Check(!Population(t,a,Frame(),Frame()),"no actual population gain is unconfirmed");
        Check(!Population(t,a,Frame(),Frame(false,2)),"unattributed multi-point gain is not one native child");
        var wrong=Frame(false,1); wrong.OwnImprovements["(1,1)"]="Port:2:1";
        Check(!Population(t,a,Frame(),wrong),"noncity improvement cannot stand for population target");
        wrong=Frame(false,1); wrong.OwnImprovements["(1,1)"]="City:?:1";
        Check(!Population(t,a,Frame(),wrong),"unreadable city level is not defaulted");
        wrong=Frame(false,1); wrong.Me="3"; Check(!Population(t,a,Frame(),wrong),"receipt cannot cross seat");
        wrong=Frame(false,1); wrong.Episode="other"; Check(!Population(t,a,Frame(),wrong),"receipt cannot cross episode");
        wrong=Frame(false,1); wrong.Turn="10"; Check(!Population(t,a,Frame(),wrong),"receipt cannot cross turn");
        wrong=Frame(false,1); wrong.Complete=false; Check(!Population(t,a,Frame(),wrong),"partial frame is not a payout proof");
        Proof(h,t,a,"IncreasePopulationAction",gs,null,Frame(),Frame(false,1));
        Check(t.NativeRuins.PopulationReceipts.Count==1 && (string)Outcome(t)["rewardStatus"]=="partially_applied"
            && Outcome(t)["rewardAmount"]==null && Convert.ToInt32(Outcome(t)["observedAppliedAmount"])==1,
            "production child proof records partial actual population without claiming all applied");
        Check(!Population(t,a),"duplicate child is not counted again");
        Check(Population(t,b,Frame(false,1),Frame(false,2)) && Population(t,c,Frame(false,2),Frame(false,3)),"remaining exact executed children gain own population receipts");
        var o=Outcome(t);
        Check((bool)o["rewardApplied"] && Convert.ToInt32(o["rewardAmount"])==3 && (string)o["rewardUnit"]=="population"
            && ((IList)o["populationGains"]).Count==3,"all executed children report three population, not stars");
        t.FinalFrame=Frame(); Check(!Population(t,b),"finalized ticket cannot acquire population proof");
        var partialGs=new GameState(); partialGs.ActionStack.Items.AddRange(new object[] {a,b}); partialGs.ActionStack.FailAt=1;
        var partial=Bind(h,partialGs,"PopulationGrowth"); Population(partial,a);
        Check(!partial.NativeRuins.PopulationChildrenComplete && Outcome(partial)["rewardApplied"]==null,
            "unreadable appended suffix cannot claim full reward applied");
        var train=new TrainAction(); var trainGs=new GameState(); trainGs.ActionStack.Items.Add(train);
        foreach(string reward in new[] {"SuperUnit","Battleship","Seamonster","Swordsman"})
        {
            var unitTicket=Bind(h,trainGs,reward);
            Check(unitTicket.NativeRuins.UnitChildrenComplete && unitTicket.NativeRuins.UnitChildren.Count==1
                && Outcome(unitTicket)["rewardApplied"]==null,"unit reward queued without revealing unit: "+reward);
            Proof(h,unitTicket,train,"TrainAction",trainGs,null,Frame(),Spawn());
            var result=Outcome(unitTicket);
            Check((bool)result["rewardApplied"] && ((IList)result["generatedUnits"]).Count==1
                && (string)((Dictionary<string,object>)((IList)result["generatedUnits"])[0])["type"]=="FutureNativeUnit",
                "executed native train credits actual dynamic generated unit: "+reward);
        }
        var u=Bind(h,trainGs,"SuperUnit");
        Check(!BridgeMod.CRRecordRuinsUnit(u,new TrainAction(),Frame(),Spawn()),"unrelated same-field train cannot credit unit reward");
        train.Cost=1; Check(!BridgeMod.CRRecordRuinsUnit(u,train,Frame(),Spawn()),"paid train is not free ruins unit"); train.Cost=0;
        train.trainActionAborted=true; Check(!BridgeMod.CRRecordRuinsUnit(u,train,Frame(),Spawn()),"aborted train does not credit unit");
        train.trainActionAborted=null; Check(!BridgeMod.CRRecordRuinsUnit(u,train,Frame(),Spawn()),"unknown aborted state is not success"); train.trainActionAborted=false;
        Check(!BridgeMod.CRRecordRuinsUnit(u,train,Frame(),Spawn("3")),"foreign generated unit cannot be published as own reward");
        Check(!BridgeMod.CRRecordRuinsUnit(u,train,Frame(),Spawn("2","Different")),"wrong resulting type cannot credit native branch");
        Check(!BridgeMod.CRRecordRuinsUnit(u,train,Frame(),Spawn("2","FutureNativeUnit","7")),"existing unit is not newly generated");
        var carrierBefore=Frame(); carrierBefore.Units["7"].PassengerId="20";
        Check(!BridgeMod.CRRecordRuinsUnit(u,train,carrierBefore,Spawn()),"carrier/passenger conversion is not generated reward");
        Check(BridgeMod.CRRecordRuinsUnit(u,train,Frame(),Spawn()) && !BridgeMod.CRRecordRuinsUnit(u,train,Frame(),Spawn()),
            "generated unit receipt captured only once");
        var sameUnitChild=new TrainAction(); u.NativeRuins.UnitChildren.Add(sameUnitChild);
        Check(!BridgeMod.CRRecordRuinsUnit(u,sameUnitChild,Frame(),Spawn()),"two children cannot credit the same generated unit twice");
        var afterUnit=Spawn(); var frozen=Bind(h,trainGs,"SuperUnit");
        BridgeMod.CRRecordRuinsUnit(frozen,train,Frame(),afterUnit); afterUnit.Units["20"].Type="ChangedLater";
        Check((string)((Dictionary<string,object>)((IList)Outcome(frozen)["generatedUnits"])[0])["type"]=="FutureNativeUnit",
            "generated unit receipt freezes executed event rather than future mutable view");
        var cityAfter=Frame(); cityAfter.CitiesUnderOwnUnits["(2,3)"]=true;
        var city=Bind(h,new GameState(),"City",0,cityAfter);
        Check((bool)Outcome(city)["rewardApplied"] && (string)((Dictionary<string,object>)Outcome(city)["createdCity"])["coords"]=="(2,3)",
            "completed root reports actual new visible city without claiming ownership");
        Check(((Dictionary<string,object>)Outcome(city)["createdCity"]).Count==2,
            "city appearance adds no inferred owner level or population");
        var noCity=Bind(h,new GameState(),"City");
        Check(Outcome(noCity)["rewardApplied"]==null && Outcome(noCity)["createdCity"]==null,"selected city without actual city appearance remains unconfirmed");
        var unknownCityAfter=Frame(); unknownCityAfter.CitiesUnderOwnUnits.Clear();
        Check(Outcome(Bind(h,new GameState(),"City",0,unknownCityAfter))["rewardApplied"]==null,
            "unknown city channel cannot be treated as observed city creation");
        var explorer=Bind(h,new GameState(),"Explorer");
        Check(Outcome(explorer)["rewardApplied"]==null && Outcome(explorer)["rewardAmount"]==null,
            "missing executed explorer chain is not promoted to successful payout");
    }
}
