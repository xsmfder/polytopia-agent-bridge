using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using PolytopiaAgent;

public sealed class AttributionTrain
{
    public string PlayerId = "1", Type = "FutureNativeUnit";
    public object trainActionAborted = false;
}
internal static class TrainAttributionTests
{
    private const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    private static int passed, failed;
    private static void Check(bool ok, string label)
    { Console.WriteLine((ok ? "PASS " : "FAIL ") + label); if (ok) passed++; else failed++; }
    private static BridgeMod.CombatFrame Frame(bool spawned)
    {
        var frame = new BridgeMod.CombatFrame { Episode = "e", Me = "1", Acting = "1", Turn = "7", Phase = "Started", Complete = true };
        if (spawned) frame.Units["new"] = new BridgeMod.CombatUnitView { Id = "new", Owner = "1", Type = "FutureNativeUnit", Coord = "(1,1)", Health = 100 };
        return frame;
    }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, F).SetValue(target, value); }
    private static object Get(object target, string name) { return target.GetType().GetField(name, F).GetValue(target); }
    public static int Main()
    {
        foreach (string kind in new[] { "no-applied-proof", "different-type", "different-owner", "different-target", "qualified" })
        {
            var host = new BridgeMod(); var before = Frame(false); var after = Frame(true);
            typeof(BridgeMod).GetMethod("CRObserve", F).Invoke(host, new object[] { before, null });
            var ticket = new BridgeMod.CombatTicket { Id = 1, Before = before, Accepted = true, Dispatched = true,
                Stream = (string)Get(host, "_combatStream"), Action = "future_native_action" };
            ((Dictionary<long, BridgeMod.CombatTicket>)Get(host, "_combatTickets")).Add(1, ticket);
            var cause = new BridgeMod.CombatCause { ActionType = "TrainAction", ActorOwner = "1", TargetCoord = "(1,1)", VisibleNativeAction = true };
            var typeField = cause.GetType().GetField("AppliedTrainType", F);
            if (typeField != null) typeField.SetValue(cause, kind == "no-applied-proof" ? null : kind == "different-type" ? "OtherNativeUnit" : "FutureNativeUnit");
            if (kind == "different-owner") cause.ActorOwner = "2";
            if (kind == "different-target") cause.TargetCoord = "(2,2)";
            var diff = BridgeMod.CRDiff(before, after, cause);
            typeof(BridgeMod).GetMethod("CRObserve", F).Invoke(host, new object[] { after, cause });
            bool generated = diff.Count == 1 && diff[0].Contains("\"generatedByAction\":true");
            Check(generated == (kind == "qualified") && ticket.NativeSpawnIds.Count == (kind == "qualified" ? 1 : 0),
                "journal and interval spawn IDs agree on actual train qualification: " + kind);
        }
        foreach (string kind in new[] { "aborted", "abort-unknown", "different-type", "type-unknown", "foreign", "unlinked", "qualified" })
        {
            var host = new BridgeMod(); var before = Frame(false); var after = Frame(true);
            var action = new AttributionTrain();
            if (kind == "aborted") action.trainActionAborted = true;
            if (kind == "abort-unknown") action.trainActionAborted = null;
            if (kind == "different-type") action.Type = "OtherNativeUnit";
            if (kind == "type-unknown") action.Type = null;
            if (kind == "foreign") action.PlayerId = "2";
            var ticket = new BridgeMod.CombatTicket { Id = 1, Before = before, Accepted = true, Dispatched = true,
                Stream = (string)Get(host, "_combatStream"), Action = "infiltrate", NativeInfiltrateObserved = true };
            if (kind != "unlinked") ticket.NativeInfiltrationTrains.Add(action);
            var pass = Activator.CreateInstance(typeof(BridgeMod).GetNestedType("CombatHookPass", F), true);
            Set(pass, "Action", action); Set(pass, "CandidateKind", "TrainAction"); Set(pass, "CandidateCoord", "(1,1)"); Set(pass, "Before", before);
            ((IList)Get(pass, "ProofTickets")).Add(ticket);
            typeof(BridgeMod).GetMethod("CRRecordNativeProof", F).Invoke(host, new object[] { pass, after });
            Check(ticket.NativeInfiltrationSpawnIds.Count == (kind == "qualified" ? 1 : 0),
                "exact infiltration spawn proof rejects unrelated or unavailable training: " + kind);
        }
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
