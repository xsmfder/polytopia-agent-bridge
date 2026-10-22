using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public class ActionBase { public string PlayerId = "2"; }
public class CityRewardAction : ActionBase { public string Coordinates = "(4,4)", Reward = "Giant"; }
public class TrainAction : ActionBase
{
    public string Coordinates = "(4,4)", Type = "NativeTribeSuperunit";
    public object trainActionAborted = false;
}
public class MoveAction : ActionBase { public string UnitId = "29", Reason = "Push"; }
public class EmbarkAction : ActionBase { public string Coordinates = "(4,5)"; }
public class DisembarkAction : ActionBase { public string Coordinates = "(4,5)"; }
public class KillUnitAction : ActionBase { public string Coordinates = "(4,4)"; }
public class NativeActionWrapper
{
    public ActionBase Value;
    public T TryCast<T>() where T : class { return Value as T; }
}
public class GameState { public RewardStack ActionStack = new RewardStack(); }
public class RewardStack
{
    public readonly List<object> Items = new List<object>(); public int FirstAllowed, Reads, FailAt = -1;
    public int Count { get { return Items.Count; } }
    public object this[int i] { get { Reads++; if (i < FirstAllowed || i == FailAt) throw new Exception("unreadable"); return Items[i]; } }
}
internal static class CityRewardEffectsTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int passed;
    private static readonly JavaScriptSerializer J = new JavaScriptSerializer();
    private static void Check(bool ok, string label) { if (!ok) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static object Get(object o, string n) { return o.GetType().GetField(n, F).GetValue(o); }
    private static void Set(object o, string n, object v) { o.GetType().GetField(n, F).SetValue(o, v); }
    private static BridgeMod.CombatUnitView Unit(string id, string at, string type = "Warrior", string passenger = null)
    { return new BridgeMod.CombatUnitView { Id = id, Coord = at, Owner = "2", Type = type, PassengerId = passenger, Health = 100 }; }
    private static BridgeMod.CombatFrame Frame(string at = "(4,4)")
    {
        var f = new BridgeMod.CombatFrame { Episode = "ep", Me = "2", Turn = "20", Acting = "2", Complete = true };
        f.OwnImprovements["(4,4)"] = "City:5:0:rewards=Workshop";
        f.Units["29"] = Unit("29", at); return f;
    }
    private static BridgeMod.CombatTicket Ticket(BridgeMod h)
    { return new BridgeMod.CombatTicket { Id = 1, Action = "city_reward", Coord = "(4,4)", Arg = "giant", Before = Frame(), Accepted = true, Dispatched = true, Stream = (string)Get(h, "_combatStream") }; }
    private static void Proof(BridgeMod h, BridgeMod.CombatTicket t, object a, string kind, string coord, GameState gs, int? oldCount, BridgeMod.CombatFrame before, BridgeMod.CombatFrame after)
    {
        var p = Activator.CreateInstance(typeof(BridgeMod).GetNestedType("CombatHookPass", F), true);
        Set(p, "Action", a); Set(p, "State", gs); Set(p, "BeforeStackCount", oldCount); Set(p, "Before", before);
        Set(p, "CandidateKind", kind); Set(p, "CandidateCoord", coord);
        ((IList)Get(p, "ProofTickets")).Add(t);
        typeof(BridgeMod).GetMethod("CRRecordNativeProof", F).Invoke(h, new object[] { p, after });
    }
    private static Dictionary<string, object> Outcome(BridgeMod.CombatTicket t, BridgeMod.CombatFrame f)
    { return J.Deserialize<Dictionary<string, object>>(BridgeMod.CRCityRewardJson(t, f)); }
    private static Dictionary<string, object> Effect(BridgeMod.CombatTicket t, int index)
    { return J.Deserialize<Dictionary<string, object>>(t.NativeCityReward.Effects[index]); }
    public static int Main()
    { try { Run(); Console.WriteLine("ALL PASS " + passed); return 0; } catch (Exception e) { Console.WriteLine(e); return 1; } }
    private static void Run()
    {
        var h = new BridgeMod(); var t = Ticket(h); var root = new CityRewardAction(); var before = Frame(); var after = Frame();
        Check(BridgeMod.CRCityRewardRootMatches(t, before, root), "own city and case-insensitive native chosen reward match");
        root.PlayerId = "3"; Check(!BridgeMod.CRCityRewardRootMatches(t, before, root), "opponent reward refused"); root.PlayerId = "2";
        root.Coordinates = "(5,5)"; Check(!BridgeMod.CRCityRewardRootMatches(t, before, root), "different city refused"); root.Coordinates = "(4,4)";
        root.Reward = "Park"; Check(!BridgeMod.CRCityRewardRootMatches(t, before, root), "different reward refused"); root.Reward = "Giant";
        before.OwnImprovements["(4,4)"] = "Farm:1"; Check(!BridgeMod.CRCityRewardRootMatches(t, before, root), "own noncity improvement refused"); before = Frame();
        t.Before.OwnImprovements.Clear(); Check(!BridgeMod.CRCityRewardRootMatches(t, before, root), "city absent at dispatch refused"); t.Before = Frame();
        var partial = Frame(); partial.Complete = false; Check(!BridgeMod.CRCityRewardRootMatches(t, partial, root), "partial frame refused");
        partial = Frame(); partial.Episode = "other"; Check(!BridgeMod.CRCityRewardRootMatches(t, partial, root), "wrong episode refused");
        partial = Frame(); partial.Me = "1"; Check(!BridgeMod.CRCityRewardRootMatches(t, partial, root), "wrong seat refused");
        partial = Frame(); partial.Turn = "21"; Check(!BridgeMod.CRCityRewardRootMatches(t, partial, root), "later turn cannot acquire root");
        t.Accepted = false; Check(!BridgeMod.CRCityRewardRootMatches(t, before, root), "rejected ticket refused"); t.Accepted = null;
        Check(BridgeMod.CRCityRewardRootMatches(t, before, root), "synchronous dispatch before acceptance callback can bind"); t.Accepted = true;
        t.FinalFrame = after; Check(!BridgeMod.CRCityRewardRootMatches(t, before, root), "frozen ticket cannot acquire root"); t.FinalFrame = null;
        var tickets = (Dictionary<long, BridgeMod.CombatTicket>)Get(h, "_combatTickets"); tickets[1] = t;
        Check(ReferenceEquals(h.CRUniqueCityRewardTicket(before, root), t), "production prefix selector finds unique reward ticket");
        var duplicate = Ticket(h); duplicate.Id = 2; tickets[2] = duplicate;
        Check(h.CRUniqueCityRewardTicket(before, root) == null, "ambiguous root cannot attach to two tickets"); duplicate.Stream = "old";
        Check(ReferenceEquals(h.CRUniqueCityRewardTicket(before, root), t), "old stream does not compete for root"); tickets.Remove(2);
        var gs = new GameState(); var old = new TrainAction(); var train = new TrainAction(); var push = new MoveAction();
        gs.ActionStack.Items.AddRange(new object[] { old, new NativeActionWrapper { Value = train }, push }); gs.ActionStack.FirstAllowed = 1;
        t.Stream = "old"; Proof(h, t, root, "CityRewardAction", "(4,4)", gs, 1, before, after);
        Check(t.NativeCityReward == null, "production proof rejects stale stream"); t.Stream = (string)Get(h, "_combatStream");
        Proof(h, t, root, "CityRewardAction", "(4,4)", gs, 1, before, after);
        Check(t.NativeCityReward.ChildrenReadable && t.NativeCityReward.Children.Count == 2 && gs.ActionStack.Reads == 2, "production parent captures exact new suffix only");
        Check(t.NativeCityReward.Effects.Count == 0 && (bool)Outcome(t, after)["nativeExecuted"], "queued unit effects are not presented as actual effects");
        Check(!BridgeMod.CRCityRewardChildMatches(t, before, old) && !BridgeMod.CRCityRewardChildMatches(t, before, new TrainAction()), "old and same-field unrelated train cannot match");
        train.PlayerId = "3"; Check(!BridgeMod.CRCityRewardChildMatches(t, before, train), "foreign child refused"); train.PlayerId = "2";
        var moved = Frame("(4,5)"); moved.Units["999"] = Unit("999", "(8,8)", "Giant");
        var embark = new EmbarkAction(); gs.ActionStack.Items.Add(embark);
        Proof(h, t, push, "MoveAction", null, gs, 3, before, moved);
        Check(t.NativeCityReward.Effects.Count == 1 && (string)Effect(t, 0)["role"] == "push", "actual pushed own actor receives causal movement receipt");
        Check(t.NativeCityReward.Children.Count == 3 && ReferenceEquals(t.NativeCityReward.Children[2], embark), "production child links exact appended embark action");
        Check(!t.NativeCityReward.Effects[0].Contains("999"), "unrelated visible spawn excluded from push receipt");
        var carrier = Frame(); carrier.Units.Clear(); carrier.Units["163"] = Unit("163", "(4,5)", "Transportship", "29");
        Proof(h, t, embark, "EmbarkAction", "(4,5)", gs, 4, moved, carrier);
        Check(t.NativeCityReward.Effects.Count == 2 && (string)Effect(t, 1)["role"] == "embark", "exact embark child records real passenger identity transition");
        var spawned = Frame(); spawned.Units.Clear(); spawned.Units["163"] = carrier.Units["163"]; spawned.Units["164"] = Unit("164", "(4,4)", "NativeTribeSuperunit");
        Proof(h, t, train, "TrainAction", "(4,4)", gs, 4, carrier, spawned);
        Check(t.NativeCityReward.Effects.Count == 3 && (string)Effect(t, 2)["role"] == "generated", "exact train child records actual generated unit");
        Check(t.NativeCityReward.Effects[2].Contains("NativeTribeSuperunit"), "native unit type preserved without giant override");
        Proof(h, t, train, "TrainAction", "(4,4)", gs, 4, carrier, spawned);
        Check(t.NativeCityReward.Effects.Count == 3, "duplicate child completion is idempotent");
        var saved = t.NativeCityReward.Effects[2]; spawned.Units["164"].Health = 1;
        Check(t.NativeCityReward.Effects[2] == saved && saved.Contains("\"health\":100"), "event-time unit state is frozen independent of later health");
        t.FinalFrame = spawned; t.FinalFingerprint = t.BeforeFingerprint = "same";
        var reply = J.Deserialize<Dictionary<string, object>>(h.CombatSettlementJson(null, 1));
        Check(((IList)reply["actualAffectedUnits"]).Count == 3 && reply["cityRewardOutcome"] != null, "formal frozen settlement integrates generated push and carrier receipts");
        Check((string)reply["generationAssociation"] != null && ((IList)reply["nativeGeneratedUnits"]).Count == 0, "legacy interval observations remain separate from exact causal receipts");
        var other = Frame(); other.Episode = "other"; Check(BridgeMod.CRCityRewardJson(t, other) == "null", "outcome refuses wrong final episode");
        // Incomplete capture preserves positive observations, never claims all effects.
        t = Ticket(h); gs = new GameState(); gs.ActionStack.Items.Add(train); gs.ActionStack.Items.Add(push); gs.ActionStack.FailAt = 1;
        Proof(h, t, root, "CityRewardAction", "(4,4)", gs, 0, before, after);
        Check(!t.NativeCityReward.ChildrenReadable && t.NativeCityReward.Children.Count == 1, "unreadable suffix stays partial while retaining exact readable child");
        var noSpawn = Frame();
        Proof(h, t, train, "TrainAction", "(4,4)", gs, 2, before, noSpawn);
        Check(t.NativeCityReward.Effects.Count == 0, "successful child without new unit does not fabricate spawn");
        Check(((IList)Outcome(t, noSpawn)["observedUnitEffects"]).Count == 0 && ((string)Outcome(t, noSpawn)["coverage"]).StartsWith("partial"), "empty observed effects does not claim no effects");
        t = Ticket(h); t.NativeCityReward = new BridgeMod.CityRewardObservation { Root = root }; t.NativeCityReward.Children.Add(embark);
        var impostor = Frame("(4,5)"); impostor.Units.Clear(); impostor.Units["163"] = Unit("163", "(4,5)", "Transportship");
        Check(BridgeMod.CRRecordCityRewardChild(t, embark, "EmbarkAction", "(4,5)", Frame("(4,5)"), impostor) && t.NativeCityReward.Effects.Count == 0, "same coordinate and carrier type cannot prove passenger relation");
        t = Ticket(h); t.NativeCityReward = new BridgeMod.CityRewardObservation { Root = root }; t.NativeCityReward.Children.Add(train);
        Check(BridgeMod.CRRecordCityRewardChild(t, train, "TrainAction", "(4,5)", Frame("(4,5)"), carrier) && t.NativeCityReward.Effects.Count == 0, "carrier creation is not counted as generated combat unit");
        t = Ticket(h); t.NativeCityReward = new BridgeMod.CityRewardObservation { Root = root }; t.NativeCityReward.Children.Add(push);
        var enemyBefore = Frame(); enemyBefore.Units["29"].Owner = "3"; var enemyAfter = Frame("(4,5)"); enemyAfter.Units["29"].Owner = "3";
        Check(BridgeMod.CRRecordCityRewardChild(t, push, "MoveAction", null, enemyBefore, enemyAfter) && t.NativeCityReward.Effects.Count == 0, "city receipts never expose foreign pushed unit state");
        t = Ticket(h); gs = new GameState(); var retryTrain = new TrainAction();
        gs.ActionStack.Items.Add(train); Proof(h, t, root, "CityRewardAction", "(4,4)", gs, 0, before, after);
        gs.ActionStack.Items.AddRange(new object[] { retryTrain, push });
        Proof(h, t, train, "TrainAction", "(4,4)", gs, 1, before, after);
        Check(t.NativeCityReward.Children.Count == 3 && t.NativeCityReward.Effects.Count == 0, "occupied-space train can append another exact train and push without fabricating spawn");
        Proof(h, t, retryTrain, "TrainAction", "(4,4)", gs, 3, carrier, spawned);
        Check(t.NativeCityReward.Effects.Count == 1, "nested retry train can provide actual generation proof later");
        var disembark = new DisembarkAction(); t.NativeCityReward.Children.Add(disembark);
        var landed = Frame("(4,5)");
        Check(BridgeMod.CRRecordCityRewardChild(t, disembark, "DisembarkAction", "(4,5)", carrier, landed)
            && (string)Effect(t, 1)["role"] == "disembark", "reverse native carrier passenger relation supports disembark");
        var kill = new KillUnitAction(); t.NativeCityReward.Children.Add(kill);
        var empty = Frame(); empty.Units.Clear();
        Check(BridgeMod.CRRecordCityRewardChild(t, kill, "KillUnitAction", "(4,4)", before, empty)
            && (string)Effect(t, 2)["role"] == "removed", "exact kill child and own disappearance prove removal");
        var unbound = new KillUnitAction(); Check(!BridgeMod.CRRecordCityRewardChild(t, unbound, "KillUnitAction", "(4,4)", before, empty), "disappearance alone cannot prove reward removal");
        var extra = new MoveAction(); t.NativeCityReward.Children.Add(extra);
        partial = Frame("(4,5)"); partial.Complete = false;
        Check(!BridgeMod.CRRecordCityRewardChild(t, extra, "MoveAction", null, before, partial), "incomplete after frame cannot consume child proof");
        partial = Frame("(4,5)"); partial.Me = "1";
        Check(!BridgeMod.CRRecordCityRewardChild(t, extra, "MoveAction", null, before, partial), "child rejects seat transition");
        partial = Frame("(4,5)"); partial.Turn = "21";
        Check(!BridgeMod.CRRecordCityRewardChild(t, extra, "MoveAction", null, before, partial), "child rejects later turn execution");
        t.Stream = "old"; Proof(h, t, extra, "MoveAction", null, gs, 3, before, Frame("(4,5)"));
        Check(t.NativeCityReward.Effects.Count == 3, "production child proof rejects old stream"); t.Stream = (string)Get(h, "_combatStream");
        extra.Reason = "Command"; Proof(h, t, extra, "MoveAction", null, gs, 3, before, Frame("(4,5)"));
        Check((string)Effect(t, 3)["role"] == "move", "move is called push only with actual native Push reason");
        int linked = t.NativeCityReward.Children.Count;
        BridgeMod.CRCityRewardAppend(t.NativeCityReward, new List<object> { extra, root, null }, true);
        Check(!t.NativeCityReward.ChildrenReadable && t.NativeCityReward.Children.Count == linked, "cycles nulls and completed children cannot create duplicate links");
        t.FinalFrame = after; Check(!BridgeMod.CRCityRewardChildMatches(t, before, new MoveAction()), "frozen ticket rejects further effects");
        t = Ticket(h); gs = new GameState(); gs.ActionStack.Items.Add(train);
        Proof(h, t, root, "CityRewardAction", "(4,4)", gs, null, before, after);
        Check(!t.NativeCityReward.ChildrenReadable && t.NativeCityReward.Children.Count == 0, "unknown stack origin cannot scan history or bind guessed children");
        bool noFalseGeneration = true;
        foreach (string kind in new[] { "aborted", "abort-unknown", "type-mismatch", "type-unknown" })
        {
            var probe = Ticket(h); var child = new TrainAction();
            if (kind == "aborted") child.trainActionAborted = true;
            else if (kind == "abort-unknown") child.trainActionAborted = null;
            else if (kind == "type-mismatch") child.Type = "OtherNativeUnit";
            else child.Type = null;
            probe.NativeCityReward = new BridgeMod.CityRewardObservation { Root = root };
            probe.NativeCityReward.Children.Add(child);
            var unrelated = Frame("(4,5)"); unrelated.Units["164"] = Unit("164", "(4,4)", "NativeTribeSuperunit");
            bool observed = BridgeMod.CRRecordCityRewardChild(probe, child, "TrainAction", "(4,4)", Frame(), unrelated);
            Console.WriteLine("CASE " + kind + " generatedEffects=" + probe.NativeCityReward.Effects.Count);
            noFalseGeneration &= observed && probe.NativeCityReward.Completed.Count == 1 && probe.NativeCityReward.Effects.Count == 0;
        }
        Check(noFalseGeneration, "aborted, unknown or differently typed training cannot claim another new visible unit as its city reward");
    }
}
