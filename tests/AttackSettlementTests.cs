// Replay archived player-visible observations through production proof/settlement code.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

internal static class AttackSettlementTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static int passed;
    private static void Check(bool yes, string message)
    { if (!yes) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, F).SetValue(target, value); }
    private static object Get(object target, string name) { return target.GetType().GetField(name, F).GetValue(target); }
    private static string Text(Dictionary<string, object> d, string key)
    { return d.ContainsKey(key) && d[key] != null ? Convert.ToString(d[key]) : null; }
    private static bool? Bool(Dictionary<string, object> d, string key)
    { return d.ContainsKey(key) && d[key] is bool ? (bool?)d[key] : null; }
    private static BridgeMod.CombatUnitView Unit(Dictionary<string, object> d)
    {
        if (d == null) return null;
        return new BridgeMod.CombatUnitView { Id = Text(d, "unitId"), Owner = Text(d, "owner"),
            Type = Text(d, "type"), Coord = Text(d, "coords"), PassengerId = Text(d, "passengerId"),
            Health = d.ContainsKey("health") && d["health"] != null ? (int?)Convert.ToInt32(d["health"]) : null,
            Moved = Bool(d, "moved"), Attacked = Bool(d, "attacked") };
    }
    private static BridgeMod.CombatFrame Frame(string episode)
    { return new BridgeMod.CombatFrame { Episode = episode, Me = "1", Complete = true, Turn = "7", Acting = "1", Phase = "Started" }; }
    private static BridgeMod.CombatFrame Clone(BridgeMod.CombatFrame before)
    { var f = Frame(before.Episode); f.Me = before.Me; foreach (var x in before.Units) f.Units.Add(x.Key, x.Value); return f; }
    private static string Fingerprint(BridgeMod.CombatFrame f)
    {
        var units = new List<string>();
        foreach (var u in f.Units.Values) units.Add(u.Id + ":" + u.Owner + ":" + u.Type + ":" + u.Coord + ":" + u.Health + ":" + u.Moved + ":" + u.Attacked);
        return "player=" + f.Me + "|units=" + string.Join(";", units.ToArray()) + ";|imps=";
    }
    private static Dictionary<string, object> Reply(BridgeMod host, BridgeMod.CombatTicket t, BridgeMod.CombatFrame final)
    {
        t.FinalFrame = final; t.FinalFingerprint = Fingerprint(final);
        var tickets = (Dictionary<long, BridgeMod.CombatTicket>)Get(host, "_combatTickets");
        tickets[t.Id] = t;
        return Json.Deserialize<Dictionary<string, object>>(host.CombatSettlementJson(null, t.Id));
    }
    private static void Proof(BridgeMod host, BridgeMod.CombatTicket t, BridgeMod.CombatFrame before,
        BridgeMod.CombatFrame after, Dictionary<string, object> e)
    {
        string kind = Text(e, "nativeAction");
        if (kind != "AttackAction" && kind != "KillUnitAction") return;
        BridgeMod.CombatUnitView actor, target;
        before.Units.TryGetValue(Text(e, "actorId") ?? "", out actor);
        before.Units.TryGetValue(Text(e, "nativeTargetId") ?? "", out target);
        string coord = Text(e, "nativeTargetCoord");
        bool bound = kind == "AttackAction"
            ? BridgeMod.CRVisibleAttackActorMatches(t, actor, target, Text(e, "actingPlayerId"), coord)
                || BridgeMod.CRRetaliationMatches(t, actor, target, coord)
            : BridgeMod.CRTrackedAttackKillMatches(t, target);
        if (!bound) return;
        var passType = typeof(BridgeMod).GetNestedType("CombatHookPass", F);
        var pass = Activator.CreateInstance(passType, true);
        Set(pass, "CandidateKind", kind); Set(pass, "CandidateCoord", coord); Set(pass, "Before", before);
        Set(pass, "Cause", new BridgeMod.CombatCause { ActionType = kind, ActorId = Text(e, "actorId"),
            ActorOwner = Text(e, "actingPlayerId"), TargetId = Text(e, "nativeTargetId"), TargetCoord = coord });
        ((IList)Get(pass, "ProofTickets")).Add(t);
        typeof(BridgeMod).GetMethod("CRRecordNativeProof", F).Invoke(host, new object[] { pass, after });
    }
    private static void Replay(Dictionary<string, object> c)
    {
        var receipt = (Dictionary<string, object>)c["ticket"];
        long id = Convert.ToInt64(receipt["ticketId"]);
        var host = new BridgeMod();
        var before = Frame(Text(receipt, "episodeId"));
        foreach (Dictionary<string, object> change in (IList)receipt["visibleChanges"])
        { var u = Unit(change["before"] as Dictionary<string, object>); if (u != null) before.Units[u.Id] = u; }
        var t = new BridgeMod.CombatTicket { Id = id, Before = before, Action = "attack", UnitId = Text(receipt, "actorId"),
            Coord = Text(receipt, "targetCoord"), Accepted = true, Dispatched = true,
            Stream = (string)Get(host, "_combatStream"), BeforeFingerprint = Fingerprint(before) };
        var current = before;
        foreach (Dictionary<string, object> e in (IList)c["events"])
        {
            var next = Clone(current);
            var old = Unit(e.ContainsKey("before") ? e["before"] as Dictionary<string, object> : null);
            var now = Unit(e.ContainsKey("after") ? e["after"] as Dictionary<string, object> : null);
            if (old != null && (now == null || old.Id != now.Id)) next.Units.Remove(old.Id);
            if (now != null) next.Units[now.Id] = now;
            Proof(host, t, current, next, e); current = next;
        }
        var reply = Reply(host, t, current);
        string label = "archived ticket " + id + " ";
        Check((string)receipt["effectConfirmation"] == "unknown", label + "was originally unknown");
        Check((string)reply["effectConfirmation"] == "confirmed" && (bool)reply["actionCompleted"], label + "now confirms through production settlement");
        var outcome = (Dictionary<string, object>)reply["attackOutcome"];
        Check((string)outcome["actorStatus"] == (id == 161 ? "disembarked" : "died"), label + "actor outcome is explicit");
        Check(((IList)reply["actualAffectedUnits"]).Count == (id == 161 ? 1 : 2), label + "actual primary and retaliation effects projected");
        if (id == 161)
        {
            var survivor = (Dictionary<string, object>)outcome["actorAfter"];
            Check((string)survivor["unitId"] == "162" && (bool)outcome["primaryTargetKilled"], "carrier175 restores actual passenger162 after target kill");
            var passenger = current.Units["162"];
            passenger.Attacked = null;
            Check((string)Reply(host, t, current)["effectConfirmation"] == "unknown", "unknown passenger flags cannot prove attack landing"); passenger.Attacked = true;
            passenger.Owner = "2";
            Check((string)Reply(host, t, current)["effectConfirmation"] == "unknown", "opponent passenger cannot confirm own attack"); passenger.Owner = "1";
            before.Units[t.UnitId].PassengerId = "999";
            Check((string)Reply(host, t, current)["effectConfirmation"] == "unknown", "same position and type do not substitute another passenger"); before.Units[t.UnitId].PassengerId = "162";
        }
        t.NativeAttackKilledIds.Clear();
        Check((string)Reply(host, t, current)["effectConfirmation"] == "unknown", label + "missing exact kill proof remains unknown");
        t.FinalFrame = null;
        var zero = t.NativePrimaryAfter;
        Check(!BridgeMod.CRTrackedAttackKillMatches(t, new BridgeMod.CombatUnitView { Id = "unrelated", Health = 0 }), label + "unrelated death is rejected");
        if (zero != null && zero.Health > 0)
            Check(!BridgeMod.CRTrackedAttackKillMatches(t, zero), label + "nonzero target cannot establish death");
        t.NativeAttackTargets.Clear();
        Check((string)Reply(host, t, current)["effectConfirmation"] == "unknown", label + "missing own primary native attack remains unknown");
        t.NativeAttackTargets.Add("fabricated");
        Check((string)Reply(host, t, current)["effectConfirmation"] == "unknown", label + "wrong target receipt cannot confirm");
        t.NativeAttackTargets.Clear();
        // Restoring proof is deliberately separate from disappearance. Both viewpoint
        // and acceptance checks must still reject it.
        t.NativeAttackTargets.Add(t.NativePrimaryBefore.Id);
        t.NativeAttackKilledIds.Add(id == 161 ? t.NativePrimaryBefore.Id : t.UnitId);
        current.Episode = "different";
        Check((string)Reply(host, t, current)["effectConfirmation"] == "unknown", label + "proof cannot cross episode"); current.Episode = before.Episode;
        current.Me = "2";
        Check((string)Reply(host, t, current)["effectConfirmation"] == "unknown", label + "proof cannot cross seats"); current.Me = "1";
        t.Accepted = false;
        Check((string)Reply(host, t, current)["effectConfirmation"] == "unknown", label + "rejected ticket cannot use attack proof");
    }
    private static void RetainedAttack()
    {
        // Native T34 ticket64: Knight558 killed Warrior867, advanced, gained XP,
        // and still had attacked=false / CanAttack=true because of Persist.
        // Replay the exact primary-damage and kill proof through production hooks;
        // do not infer completion from XP, movement or a disappeared enemy alone.
        var host = new BridgeMod();
        var before = Frame("retained-attack");
        var actor = new BridgeMod.CombatUnitView { Id = "558", Owner = "1", Type = "Knight",
            Coord = "(7,1)", Health = 70, Moved = true, Attacked = false, CanAttack = true, Xp = 0 };
        var defender = new BridgeMod.CombatUnitView { Id = "867", Owner = "15", Type = "Warrior",
            Coord = "(8,2)", Health = 40 };
        before.Units[actor.Id] = actor; before.Units[defender.Id] = defender;
        var t = new BridgeMod.CombatTicket { Id = 64, Before = before, Action = "attack", UnitId = actor.Id,
            Coord = defender.Coord, Accepted = true, Dispatched = true,
            Stream = (string)Get(host, "_combatStream"), BeforeFingerprint = Fingerprint(before) };
        var damaged = Clone(before);
        var zero = new BridgeMod.CombatUnitView { Id = defender.Id, Owner = defender.Owner,
            Type = defender.Type, Coord = defender.Coord, Health = 0 };
        damaged.Units[zero.Id] = zero;
        Proof(host, t, before, damaged, new Dictionary<string, object> {
            { "nativeAction", "AttackAction" }, { "actorId", actor.Id }, { "actingPlayerId", "1" },
            { "nativeTargetId", defender.Id }, { "nativeTargetCoord", defender.Coord } });
        var killed = Clone(damaged); killed.Units.Remove(defender.Id);
        Proof(host, t, damaged, killed, new Dictionary<string, object> {
            { "nativeAction", "KillUnitAction" }, { "nativeTargetId", defender.Id },
            { "nativeTargetCoord", defender.Coord } });
        var survivor = new BridgeMod.CombatUnitView { Id = actor.Id, Owner = "1", Type = "Knight",
            Coord = defender.Coord, Health = 70, Moved = true, Attacked = false, CanAttack = true, Xp = 1 };
        killed.Units[actor.Id] = survivor;
        var reply = Reply(host, t, killed);
        Check((string)reply["effectConfirmation"] == "confirmed" && (bool)reply["actionCompleted"],
            "real retained-attack flags cannot negate exact primary damage and native kill proof");
        var outcome = (Dictionary<string, object>)reply["attackOutcome"];
        Check((string)outcome["actorStatus"] == "survived" && (bool)outcome["primaryTargetKilled"],
            "retained-attack actor is explicitly alive and primary target killed");
        var after = (Dictionary<string, object>)outcome["actorAfter"];
        Check((bool)after["attacked"] == false && (bool)after["canAttack"] == true,
            "receipt preserves actual reset flags rather than forcing consumed attack");
        t.NativeAttackKilledIds.Clear();
        Check((string)Reply(host, t, killed)["effectConfirmation"] != "confirmed",
            "XP and advance plus damage to zero cannot substitute exact target kill proof");
        t.NativeAttackKilledIds.Add(defender.Id);
        t.NativeAttackTargets.Clear();
        Check((string)Reply(host, t, killed)["effectConfirmation"] != "confirmed",
            "target kill cannot substitute matched own primary attack");
        t.NativeAttackTargets.Add(defender.Id);
        zero.Health = 10;
        Check((string)Reply(host, t, killed)["effectConfirmation"] != "confirmed",
            "retained attack requires observed lethal primary damage"); zero.Health = 0;
        zero.Coord = "(9,2)";
        Check((string)Reply(host, t, killed)["effectConfirmation"] != "confirmed",
            "other-coordinate damage cannot confirm retained attack"); zero.Coord = defender.Coord;
        zero.Owner = "2";
        Check((string)Reply(host, t, killed)["effectConfirmation"] != "confirmed",
            "other-owner damage cannot confirm retained attack"); zero.Owner = defender.Owner;
        killed.Units[defender.Id] = zero;
        Check((string)Reply(host, t, killed)["effectConfirmation"] != "confirmed",
            "target still present cannot prove completed retained kill"); killed.Units.Remove(defender.Id);
        survivor.Health = null;
        Check((string)Reply(host, t, killed)["effectConfirmation"] != "confirmed",
            "unreadable surviving actor HP remains unknown"); survivor.Health = 70;
        survivor.Attacked = null;
        Check((string)Reply(host, t, killed)["effectConfirmation"] != "confirmed",
            "unknown actor attack flag is not treated as retained false"); survivor.Attacked = false;
        survivor.Owner = "2";
        Check((string)Reply(host, t, killed)["effectConfirmation"] != "confirmed",
            "same ID now owned by another player cannot prove own survival"); survivor.Owner = "1";
        killed.Episode = "different";
        Check((string)Reply(host, t, killed)["effectConfirmation"] != "confirmed",
            "retained proof does not cross episodes"); killed.Episode = before.Episode;
        t.Accepted = false;
        Check((string)Reply(host, t, killed)["effectConfirmation"] != "confirmed",
            "rejected retained attack cannot use native effect proof");
    }
    public static int Main(string[] args)
    {
        try
        {
            RetainedAttack();
            var root = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0]));
            foreach (Dictionary<string, object> c in (IList)root["cases"]) Replay(c);
            Console.WriteLine("ALL PASS " + passed); return 0;
        }
        catch (Exception e) { Console.WriteLine(e); return 1; }
    }
}
