// Tests invoke production reward binding, child receipts, and strict tech readers.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public enum TechType { Hunting, Sailing, FutureNativeTech }
public class TechData { public TechType type = TechType.FutureNativeTech; }
public class ExamineRuinsAction { public string Reward = "FreeTech"; public TechData Tech = new TechData(); }
public class ResearchAction
{
    public string PlayerId = "2";
    public object Type = TechType.FutureNativeTech, Cost = 0;
}
public class PlayerState { public string Id = "2"; public object availableTech = new object[0]; }
public class GameState
{
    public ChildStack ActionStack = new ChildStack();
    public List<object> Players { get; set; }
}
public class ChildStack
{
    public List<object> Items = new List<object>();
    public int FirstAllowed, FailAt = -1, Reads;
    public int Count { get { return Items.Count; } }
    public object this[int i] { get { Reads++; if (i < FirstAllowed || i == FailAt) throw new Exception("unreadable"); return Items[i]; } }
}
public class BrokenTechList : IEnumerable
{
    public IEnumerator GetEnumerator() { yield return TechType.Hunting; throw new Exception("partial list"); }
}

internal static class RuinsTechTests
{
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly JavaScriptSerializer J = new JavaScriptSerializer();
    private static int passed;
    private static void Check(bool ok, string label) { if (!ok) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static object Get(object o, string n) { return o.GetType().GetField(n, F).GetValue(o); }
    private static void Set(object o, string n, object v) { o.GetType().GetField(n, F).SetValue(o, v); }
    private static object Call(BridgeMod h, string n, params object[] args) { return typeof(BridgeMod).GetMethod(n, F).Invoke(h, args); }
    private static HashSet<string> Techs(params string[] names) { return new HashSet<string>(names, StringComparer.Ordinal); }
    private static BridgeMod.CombatFrame Frame(bool ruin)
    {
        var f = new BridgeMod.CombatFrame { Episode = "ep", Me = "2", Complete = true };
        f.Units["7"] = new BridgeMod.CombatUnitView { Id = "7", Owner = "2", Coord = "(2,3)" };
        f.RuinsUnderOwnUnits["(2,3)"] = ruin;
        return f;
    }
    private static BridgeMod.CombatTicket Ticket(BridgeMod h)
    { return new BridgeMod.CombatTicket { Id = 1, Stream = (string)Get(h, "_combatStream"), Before = Frame(true), Accepted = true,
        Action = "examine_ruins", UnitId = "7", Coord = "(2,3)" }; }
    private static void Proof(BridgeMod h, BridgeMod.CombatTicket t, object action, string kind, GameState gs, int? oldCount,
        BridgeMod.CombatFrame before, BridgeMod.CombatFrame after, HashSet<string> beforeTechs = null)
    {
        var pass = Activator.CreateInstance(typeof(BridgeMod).GetNestedType("CombatHookPass", F), true);
        Set(pass, "Action", action); Set(pass, "State", gs); Set(pass, "CandidateKind", kind); Set(pass, "BeforeStackCount", oldCount);
        Set(pass, "Before", before); Set(pass, "RuinsTechsBefore", beforeTechs);
        Set(pass, "Cause", new BridgeMod.CombatCause { ActionType = kind, ActorId = "7", ActorOwner = "2", TargetCoord = "(2,3)" });
        ((IList)Get(pass, "ProofTickets")).Add(t); Call(h, "CRRecordNativeProof", pass, after);
    }
    private static BridgeMod.CombatTicket Bind(BridgeMod h, GameState gs, int? count)
    { var t = Ticket(h); Proof(h, t, new ExamineRuinsAction(), "ExamineRuinsAction", gs, count, Frame(true), Frame(false)); return t; }
    private static Dictionary<string, object> Outcome(BridgeMod.CombatTicket t)
    { return J.Deserialize<Dictionary<string, object>>(BridgeMod.CRRuinsJson(t, Frame(false))); }
    private static bool Record(BridgeMod.CombatTicket t, ResearchAction a, HashSet<string> before = null, HashSet<string> after = null)
    { return BridgeMod.CRRecordRuinsResearch(t, a, Frame(false), Frame(false), before ?? Techs("Hunting"), after ?? Techs("Hunting", "FutureNativeTech")); }
    public static int Main()
    { try { Run(); Console.WriteLine("ALL PASS " + passed); return 0; } catch (Exception e) { Console.WriteLine(e); return 1; } }
    private static void Run()
    {
        Check(BridgeMod.ReadOwnedTechNames(new object[0]).Count == 0, "readable empty owned-tech list is known empty");
        Check(BridgeMod.ReadOwnedTechNames(null) == null, "missing tech list stays unknown");
        Check(BridgeMod.ReadOwnedTechNames(new BrokenTechList()) == null, "partial enumeration cannot claim missing tech");
        Check(BridgeMod.ReadOwnedTechNames(new object[] { TechType.Hunting, null }) == null, "null entry prevents complete tech snapshot");
        Check(BridgeMod.ReadOwnedTechNames(new object[] { 1 }) == null, "raw number is not a named tech");
        Check(BridgeMod.ReadOwnedTechNames(new object[] { (TechType)99 }) == null, "unnamed enum cannot prove tech ownership");
        Check(BridgeMod.ReadOwnedTechNames(new object[] { true }) == null, "boolean cannot stand for a tech");
        Check(BridgeMod.ReadOwnedTechNames(new object[] { "Hunting|techs=Sailing" }) == null, "invalid tech label cannot alter fingerprint fields");
        Check(BridgeMod.ReadOwnedTechNames(new object[] { TechType.Hunting, TechType.Hunting }) == null, "duplicate source entries remain invalid");
        Check(BridgeMod.OwnedTechFingerprint(new object[] { TechType.Sailing, TechType.FutureNativeTech }) == "FutureNativeTech,Sailing", "owned names support future enums and stable ordering");

        var h = new BridgeMod(); var own = new PlayerState { availableTech = new object[] { TechType.Hunting, TechType.FutureNativeTech } };
        var gs = new GameState { Players = new List<object> { new PlayerState { Id = "3", availableTech = new BrokenTechList() }, own } };
        Set(h, "_playersProp", typeof(GameState).GetProperty("Players"));
        var old = new ResearchAction(); var child = new ResearchAction(); gs.ActionStack.Items.AddRange(new object[] { old, child });
        gs.ActionStack.FirstAllowed = 1;
        var t = Bind(h, gs, 1);
        Check(t.NativeRuins.TechChildrenComplete && t.NativeRuins.TechChildren.Count == 1 && gs.ActionStack.Reads == 1, "parent binds exact newly appended research without historical reads");
        Check(Outcome(t)["rewardApplied"] == null && Outcome(t)["appliedTech"] == null, "selected and queued tech is not credited");
        Check(!Record(t, old) && !Record(t, new ResearchAction()), "old and unrelated same-field research do not satisfy reward");
        child.PlayerId = "3"; Check(!Record(t, child), "opponent child cannot credit own tech"); child.PlayerId = "2";
        child.Type = TechType.Sailing; Check(!Record(t, child), "different researched tech cannot credit selection"); child.Type = TechType.FutureNativeTech;
        child.Cost = 1; Check(!Record(t, child), "paid research cannot credit free reward");
        child.Cost = false; Check(!Record(t, child), "invalid cost is not zero"); child.Cost = 0;
        Check(!Record(t, child, Techs("FutureNativeTech")), "already-owned tech is not a new reward");
        Check(!Record(t, child, Techs("Hunting"), Techs("Hunting", "Sailing")), "other tech gain is insufficient");
        Check(!BridgeMod.CRRecordRuinsResearch(t, child, Frame(false), Frame(false), null, Techs("FutureNativeTech")), "unknown before tech list cannot prove new gain");
        Check(!BridgeMod.CRRecordRuinsResearch(t, child, Frame(false), Frame(false), Techs(), null), "unknown after tech list cannot prove gain");
        var wrong = Frame(false); wrong.Complete = false;
        Check(!BridgeMod.CRRecordRuinsResearch(t, child, Frame(false), wrong, Techs(), Techs("FutureNativeTech")), "partial frame cannot prove reward");
        wrong = Frame(false); wrong.Me = "3";
        Check(!BridgeMod.CRRecordRuinsResearch(t, child, Frame(false), wrong, Techs(), Techs("FutureNativeTech")), "reward cannot cross seat");
        wrong = Frame(false); wrong.Episode = "other";
        Check(!BridgeMod.CRRecordRuinsResearch(t, child, Frame(false), wrong, Techs(), Techs("FutureNativeTech")), "reward cannot cross episode");
        Proof(h, t, child, "ResearchAction", gs, null, Frame(false), Frame(false), Techs("Hunting"));
        var o = Outcome(t);
        Check((bool)o["rewardApplied"] && (string)o["appliedTech"] == "FutureNativeTech" && (string)o["rewardStatus"] == "applied", "production child proof confirms selected tech actually gained");
        Check(o["rewardAmount"] == null && (string)o["rewardUnit"] == "technology", "technology reward does not fabricate a star amount");
        Check(!Record(t, child) && t.NativeRuins.AppliedTechChildren.Count == 1, "duplicate child proof does not double-credit");
        own.availableTech = new object[0];
        Check((bool)Outcome(t)["rewardApplied"], "applied receipt remains frozen event evidence without reading future state");
        t.FinalFrame = Frame(false); Check(!Record(t, child), "finalized reward cannot acquire late proof"); t.FinalFrame = null;
        t.Accepted = false; Check(!Record(t, child), "rejected parent cannot acquire proof"); t.Accepted = true;
        t.Action = "move"; Check(!Record(t, child), "reward proof cannot transfer to other command"); t.Action = "examine_ruins";
        var stale = Bind(h, gs, 1); stale.Stream = "old";
        Proof(h, stale, child, "ResearchAction", gs, null, Frame(false), Frame(false), Techs());
        Check(stale.NativeRuins.AppliedTechChildren.Count == 0, "production proof rejects stale stream");
        var missing = Bind(h, gs, null); Check(!Record(missing, child) && Outcome(missing)["rewardApplied"] == null, "missing initial count cannot bind an unproved research child");
        var empty = Bind(h, gs, 2); Check(Outcome(empty)["rewardApplied"] == null, "empty child suffix is not a free-tech success");
        gs.ActionStack.Items.Add(new ResearchAction()); gs.ActionStack.FailAt = 2;
        var partial = Bind(h, gs, 1); gs.ActionStack.FailAt = -1;
        Check(!partial.NativeRuins.TechChildrenComplete && Record(partial, child) && Outcome(partial)["rewardApplied"] == null, "suffix read failure cannot manufacture complete reward");
        Check((string)Outcome(partial)["rewardStatus"] == "partially_applied", "partial proof has explicit partial status");
        gs.ActionStack.Items[2] = child;
        var duplicate = Bind(h, gs, 1); Check(!duplicate.NativeRuins.TechChildrenComplete, "duplicate native child invalidates full execution sequence");
        var tickets = (Dictionary<long, BridgeMod.CombatTicket>)Get(h, "_combatTickets"); tickets[1] = t;
        Check((bool)Call(h, "CRHasPendingRuinsResearch", child), "fast gate accepts bound unsettled reward child");
        Check(!(bool)Call(h, "CRHasPendingRuinsResearch", old), "fast gate skips unrelated ordinary research");
        t.FinalFrame = Frame(false); Check(!(bool)Call(h, "CRHasPendingRuinsResearch", child), "fast gate stops after frozen settlement");
    }
}
