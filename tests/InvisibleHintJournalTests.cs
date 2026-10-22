// Own native UI indicators must survive transient changes between planner reads.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public sealed class GameState { }
internal static class InvisibleHintJournalTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static int passed;
    private sealed class NativeUnit
    {
        public bool Present, Throw;
        public int Calls;
        public bool IsDetectingHiddenUnits(GameState state)
        { Calls++; if (Throw) throw new Exception("hint unavailable"); return Present; }
        public object Neighbors { get { throw new Exception("hidden-neighbor scan forbidden"); } }
    }
    private static void Check(bool yes, string label)
    { if (!yes) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static Dictionary<string, string> Hints(BridgeMod.CombatFrame frame)
    {
        var field = typeof(BridgeMod.CombatFrame).GetField("OwnInvisibleThreatHints", F);
        return field == null ? null : (Dictionary<string, string>)field.GetValue(frame);
    }
    private static void Capture(BridgeMod.CombatFrame frame, BridgeMod.CombatUnitView view, NativeUnit unit)
    {
        var method = typeof(BridgeMod).GetMethod("CRCaptureInvisibleHint", F);
        if (method != null) method.Invoke(null, new object[] { frame, view, unit, new GameState() });
    }
    private static BridgeMod.CombatFrame Frame(NativeUnit unit, NativeUnit enemy)
    {
        var frame = new BridgeMod.CombatFrame { Episode = "hint-episode", Me = "1", Acting = "2", Turn = "8", Phase = "Started", Complete = true };
        var own = new BridgeMod.CombatUnitView { Id = "own", Owner = "1", Coord = "(4,4)", Type = "Warrior", Health = 100 };
        var foe = new BridgeMod.CombatUnitView { Id = "visible-enemy", Owner = "2", Coord = "(7,7)", Type = "Warrior", Health = 100 };
        frame.Units[own.Id] = own; frame.Units[foe.Id] = foe;
        Capture(frame, own, unit); Capture(frame, foe, enemy);
        return frame;
    }
    private static void Observe(BridgeMod host, BridgeMod.CombatFrame frame, BridgeMod.CombatCause cause)
    { typeof(BridgeMod).GetMethod("CRObserve", F).Invoke(host, new object[] { frame, cause }); }
    private static Dictionary<string, object> Events(BridgeMod host)
    { return Json.Deserialize<Dictionary<string, object>>(host.CombatEventsJson(null, 0, 256)); }
    private static IList Rows(BridgeMod host) { return (IList)Events(host)["events"]; }
    public static int Main()
    {
        try
        {
            var host = new BridgeMod(); var unit = new NativeUnit(); var enemy = new NativeUnit { Throw = true };
            var before = Frame(unit, enemy); Observe(host, before, null);
            // A hidden move may target an OWN empty tile. Its private coordinate must
            // not be attached to this public indicator even if a hook knows it.
            var hidden = new BridgeMod.CombatCause { ActionType = "MoveAction", ActorId = "hidden-unit",
                ActorOwner = "hidden-owner", TargetCoord = "(secret-target)", TargetId = "hidden-target", VisibleNativeAction = true };
            unit.Present = true; Observe(host, Frame(unit, enemy), hidden);
            unit.Present = false; Observe(host, Frame(unit, enemy), hidden);
            var rows = Rows(host);
            Check(rows.Count == 2, "transient own false-true-false hint survives as two journal events");
            var first = (Dictionary<string, object>)rows[0]; var second = (Dictionary<string, object>)rows[1];
            Check((string)first["kind"] == "own_invisible_threat_changed" && (string)first["unitId"] == "own" && (string)first["coords"] == "(4,4)", "hint event identifies only the observable own unit and its coordinate");
            Check((bool)((Dictionary<string, object>)first["before"])["present"] == false && (bool)((Dictionary<string, object>)first["after"])["present"] == true && (bool)((Dictionary<string, object>)second["after"])["present"] == false, "both historical values remain explicit rather than collapsing to current false");
            Check(first["actorId"] == null && first["actingPlayerId"] == null && first["nativeAction"] == null && first["nativeTargetId"] == null && first["nativeTargetCoord"] == null && !Json.Serialize(first).Contains("secret-target"), "private hidden action identity and destination never annotate a public hint event");
            Check(((Dictionary<string, object>)first["after"]).Count == 3, "native hint payload retains only present status and authority");
            Check(unit.Calls == 3 && enemy.Calls == 0, "production capture reads own native hint fresh and never invokes enemy hint getter");
            Observe(host, Frame(unit, enemy), hidden);
            Check(Rows(host).Count == 2, "unchanged hint does not create repeated events");
            unit.Throw = true; Observe(host, Frame(unit, enemy), hidden);
            var unknown = (Dictionary<string, object>)Rows(host)[2];
            Check(((Dictionary<string, object>)unknown["after"])["present"] == null && (string)((Dictionary<string, object>)unknown["after"])["status"] == "unavailable", "failed read becomes explicit unknown instead of safe false");
            unit.Throw = false; Observe(host, Frame(unit, enemy), hidden);
            var restored = (Dictionary<string, object>)Rows(host)[3];
            Check(((Dictionary<string, object>)restored["before"])["present"] == null && (bool)((Dictionary<string, object>)restored["after"])["present"] == false, "recovered false retains unknown historical predecessor");
            var damage = Frame(unit, enemy); damage.Units["own"].Health = 90;
            Observe(host, damage, new BridgeMod.CombatCause { ActionType = "AttackAction", ActorId = "visible-enemy", ActorOwner = "2", TargetId = "own", TargetCoord = "(4,4)", VisibleNativeAction = true });
            var normal = (Dictionary<string, object>)Rows(host)[4];
            Check((string)normal["kind"] == "unit_changed" && (string)normal["actorId"] == "visible-enemy" && (string)normal["nativeAction"] == "AttackAction", "ordinary visible damage keeps its native causal evidence");
            var isolated = new BridgeMod(); var baseFrame = Frame(unit, enemy); Observe(isolated, baseFrame, null);
            var polluted = Frame(unit, enemy); Hints(polluted)["visible-enemy"] = "{\"present\":true}";
            Observe(isolated, polluted, hidden);
            Check(Rows(isolated).Count == 0 && polluted.Signature() == baseFrame.Signature(), "enemy dictionary entries cannot leak through events or state-version signature");
            var fresh = Frame(unit, enemy); var added = new BridgeMod.CombatUnitView { Id = "new-own", Owner = "1", Coord = "(5,4)", Type = "Warrior", Health = 100 };
            fresh.Units[added.Id] = added; Capture(fresh, added, new NativeUnit { Present = true }); Observe(isolated, fresh, null);
            var created = Rows(isolated); var addedHint = (Dictionary<string, object>)created[created.Count - 1];
            Check((string)addedHint["kind"] == "own_invisible_threat_changed" && addedHint["before"] == null && (bool)((Dictionary<string, object>)addedHint["after"])["present"], "new own detector starts from unknown without inventing a previous false");
            var gone = Frame(unit, enemy); gone.Units.Remove("own"); Hints(gone).Remove("own"); var goneDiff = typeof(BridgeMod).GetMethod("CRInvisibleHintDiff", F);
            Check(((List<string>)goneDiff.Invoke(null, new object[] { baseFrame, gone })).Count == 0, "removed detector does not manufacture a safe-false observation");
            var changedGame = Frame(unit, enemy); changedGame.Episode = "another-episode"; Observe(isolated, changedGame, null);
            Check(Rows(isolated).Count == 0, "new episode does not inherit prior alert history");
            var changedSeat = Frame(unit, enemy); changedSeat.Me = "2"; Observe(isolated, changedSeat, null);
            Check(Rows(isolated).Count == 0, "seat change does not inherit another player's alert history");
            Console.WriteLine("ALL PASS " + passed); return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }
}
