using System;
using System.Collections.Generic;
using System.Reflection;
using PolytopiaAgent;

internal static class ActionReplyTests
{
    private static int passed, failed;
    private static void Check(bool ok, string label)
    { if (ok) { passed++; Console.WriteLine("PASS " + label); } else { failed++; Console.WriteLine("FAIL " + label); } }
    public static int Main()
    {
        try
        {
            var h = new BridgeMod();
            const BindingFlags f = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(BridgeMod).GetField("_combatStream", f).SetValue(h, "s");
            var tickets = (Dictionary<long, BridgeMod.CombatTicket>)typeof(BridgeMod).GetField("_combatTickets", f).GetValue(h);
            var frame = new BridgeMod.CombatFrame { Episode = "ep", Me = "1", Acting = "1", Turn = "5", Phase = "Started", Complete = true };
            var t = new BridgeMod.CombatTicket { Id = 7, RequestId = 55, Stream = "s", Action = "FutureSkill", Before = frame,
                FinalFrame = frame, Dispatched = true, Accepted = true, BeforeFingerprint = "same", FinalFingerprint = "same" };
            tickets[7] = t;
            string optimistic = "{\"ok\":true,\"accepted\":true,\"controlledPlayerId\":\"1\",\"verdict\":\"confirmed\",\"confirmation\":\"confirmed\",\"actionCompleted\":true,\"needsRedecision\":false,\"reobserve\":\"optional\"}";
            var r = BridgeJsonCodec.Object(h.AttachCombatSettlement(optimistic, null, 7));
            var native = (Dictionary<string, object>)r["settlement"];
            Check(object.Equals(native["settlementComplete"], true) && (string)native["effectConfirmation"] == "unknown",
                "actual production ticket is complete but has unknown effect");
            Check((string)r["verdict"] == "unknown" && object.Equals(r["actionCompleted"], false),
                "completed ticket with unknown effect overrides optimistic legacy verdict");
            Check((string)r["confirmation"] == "unknown" && (string)r["reobserve"] == "required",
                "public immediate confirmation follows this exact original ticket");
            t.Accepted = false;
            r = BridgeJsonCodec.Object(h.AttachCombatSettlement(optimistic, null, 7));
            Check((string)r["verdict"] == "rejected" && object.Equals(r["actionCompleted"], false),
                "native ticket rejection overrides stale immediate success");
            t.Accepted = true; t.Action = "move"; t.UnitId = "4"; t.Coord = "(2,1)";
            t.BeforeFingerprint = "player=1|units=4:1:Warrior:(1,1):100:False:False;|imps=";
            t.FinalFingerprint = "player=1|units=4:1:Warrior:(2,1):100:True:False;|imps=";
            string pessimistic = optimistic.Replace("\"verdict\":\"confirmed\"", "\"verdict\":\"unknown\"")
                .Replace("\"confirmation\":\"confirmed\"", "\"confirmation\":\"unknown\"").Replace("\"actionCompleted\":true", "\"actionCompleted\":false");
            r = BridgeJsonCodec.Object(h.AttachCombatSettlement(pessimistic, null, 7));
            Check((string)((Dictionary<string, object>)r["settlement"])["effectConfirmation"] == "confirmed",
                "actual stored move ticket confirms exact destination");
            Check((string)r["verdict"] == "confirmed" && object.Equals(r["actionCompleted"], true),
                "confirmed ticket supersedes stale immediate unknown");
            var stored = (Dictionary<string, object>)r["settlement"];
            string confirmed = BridgeJsonCodec.ParsedValueJson(stored);
            foreach (string status in new[] { "pending", "needs_resolution" })
            {
                var pending = BridgeJsonCodec.Object(confirmed);
                pending["settlementStatus"] = status; pending["settlementComplete"] = false;
                pending["effectConfirmation"] = "unknown"; pending["actionCompleted"] = false;
                var projected = BridgeJsonCodec.Object(BridgeMod.MergeActionSettlement(optimistic, BridgeJsonCodec.ParsedValueJson(pending), 7));
                Check((string)projected["verdict"] == (status == "pending" ? "accepted_pending_confirmation" : status)
                    && object.Equals(projected["actionCompleted"], false), "pending and paused receipts retain actual stage: " + status);
            }
            var inconsistent = BridgeJsonCodec.Object(confirmed); inconsistent["actionCompleted"] = false;
            var invalid = BridgeJsonCodec.Object(BridgeMod.MergeActionSettlement(optimistic, BridgeJsonCodec.ParsedValueJson(inconsistent), 7));
            Check((string)invalid["verdict"] == "unknown", "confirmed text without positive completion flag is not sufficient");
            invalid = BridgeJsonCodec.Object(BridgeMod.MergeActionSettlement(optimistic.Replace("\"1\"", "\"2\""), confirmed, 7));
            Check((string)invalid["verdict"] == "unknown", "different controlled seat cannot acquire a ticket's success");
            invalid = BridgeJsonCodec.Object(BridgeMod.MergeActionSettlement(optimistic, confirmed, 8));
            Check((string)invalid["verdict"] == "unknown", "another original ticket cannot acquire success");
            invalid = BridgeJsonCodec.Object(BridgeMod.MergeActionSettlement("broken response", confirmed, 7));
            Check((string)invalid["outcome"] == "unknown" && BridgeJsonCodec.Scalar(invalid["settlementTicket"]) == "7"
                && !invalid.ContainsKey("dispatched"), "malformed post-execution response keeps original ticket without nonexecution proof");
            string extra = "{\"ok\":true,\"accepted\":true,\"controlledPlayerId\":\"1\",\"large\":9007199254740993,\"note\":\"中文\\n\\\"quoted\\\"\",\"items\":[false,null,0],\"nested\":{\"actionCompleted\":true}}";
            var encoded = BridgeJsonCodec.Object(BridgeMod.MergeActionSettlement(extra, confirmed, 7));
            Check(BridgeJsonCodec.Scalar(encoded["large"]) == "9007199254740993" && (string)encoded["note"] == "中文\n\"quoted\"",
                "receipt composition preserves numeric precision and escaped Unicode strings");
            Check(object.Equals(((Dictionary<string, object>)encoded["nested"])["actionCompleted"], true)
                && ((List<object>)encoded["items"])[1] == null, "top-level projection does not rewrite nested independent fields");
            t.FinalFingerprint = t.BeforeFingerprint;
            r = BridgeJsonCodec.Object(h.AttachCombatSettlement(optimistic, null, 7));
            Check((string)r["verdict"] == "not_confirmed" && (string)r["confirmation"] == "not_confirmed",
                "completed negative effect follows ticket rather than old snapshot");
            r = BridgeJsonCodec.Object(h.AttachCombatSettlement(optimistic, null, 999));
            Check((string)r["verdict"] == "unknown" && object.Equals(r["actionCompleted"], false),
                "unavailable original ticket cannot produce optimistic success");
            var echo = new BridgeMod.ActionEcho { Accepted = true, ControlledId = "1", ActionName = "move",
                TargetUnitId = "4", TargetCoord = "(2,1)", Before = t.BeforeFingerprint,
                After = "player=1|units=4:1:Warrior:(2,1):100:True:False;|imps=", PendingMineAfter = 0, PopupAfter = false };
            var admission = BridgeJsonCodec.Object(BridgeMod.BuildActionResponse(echo));
            Check(object.Equals(admission["actionCompleted"], false) && !admission.ContainsKey("snapshotComparison"),
                "public admission has no competing completed flag or snapshot comparison");
            echo.Audit = true; admission = BridgeJsonCodec.Object(BridgeMod.BuildActionResponse(echo));
            Check((string)((Dictionary<string, object>)admission["snapshotComparison"])["confirmation"] == "confirmed"
                && object.Equals(admission["actionCompleted"], false), "audit keeps immediate effect comparison without declaring command complete");
            Console.WriteLine("passed=" + passed + " failed=" + failed); return failed == 0 ? 0 : 1;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }
}
