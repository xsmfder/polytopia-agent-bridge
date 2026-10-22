using System;
using System.Collections.Generic;
using PolytopiaAgent;

internal static class ProtocolRepairTests
{
    private static int failed, passed;
    private enum Example { Warrior, Scoutship }
    private static void Check(bool value, string message)
    {
        if (value) { passed++; }
        else { failed++; Console.WriteLine("FAIL " + message); }
    }
    private static bool Reject(string text)
    { try { BridgeJsonCodec.Object(text); return false; } catch (FormatException) { return true; } }
    public static int Main()
    {
        string input = "line\nnext\r\t\0\u0001\b\f\"\\中文😀";
        string output = new JsonObj().Str("text", input).Close();
        Check(Json.GetString(output, "text") == input, "control and Unicode roundtrip");
        Check(output.IndexOf('\n') < 0 && output.IndexOf('\t') < 0 && output.IndexOf('\0') < 0,
            "serialized frame is exactly one line");
        Check(Json.GetString("{\"detail\":\"\\\"cmd\\\":\\\"set\\\"\",\"cmd\":\"world\"}", "cmd") == "world",
            "key-like text in a string does not change command");
        Check(Json.GetString("{\"nested\":{\"cmd\":\"set\"},\"cmd\":\"world\"}", "cmd") == "world",
            "nested command does not change envelope command");
        Check(Json.GetString("{\"id\":123,\"accept\":false}", "accept") == "false", "boolean argument");
        Check(Json.GetString("{\"id\":123}", "id") == "123", "numeric request identity");
        foreach (string invalid in new[] { "[]", "{\"x\":1,}", "{\"x\":01}", "{\"x\":1e}",
            "{\"x\":NaN}", "{\"x\":1,\"x\":2}", "{\"x\":\"\n\"}", "{\"x\":\"\\q\"}",
            "{}extra", "{\"x\":[1,]}", "{\"x\":\"\\uZZZZ\"}" })
        { Check(Reject(invalid), "reject malformed JSON " + invalid); }
        var bad = BridgeJsonCodec.Object(BridgeJsonCodec.NormalizeResponse("{\"ok\":false,\"error\":\"broken\" quote}"));
        Check(BridgeJsonCodec.Scalar(bad["errorCode"]) == "INVALID_RESPONSE_JSON", "malformed response repaired");
        Check(BridgeJsonCodec.Scalar(bad["outcome"]) == "unknown", "serialization failure never claims action rejection/success");
        string enumeration = BridgeMod.InvalidEnumResponse("unit", "bad\"\nkind", typeof(Example));
        Check(Json.GetString(enumeration, "value") == "bad\"\nkind", "invalid enum input safely encoded");
        Check(Json.GetString(enumeration, "errorCode") == "INVALID_ENUM", "stable enum error code");
        Check(BridgeJsonCodec.Object(BridgeMod.SchemaInfoJson()).ContainsKey("neutralVillages"), "schema migration published");
        Check(BridgeMod.IsDecisionWorldKey("currentState") && BridgeMod.IsDecisionWorldKey("matchEnded"),
            "terminal keys allowed in real source whitelist");
        Check(!BridgeMod.IsDecisionWorldKey("hiddenUnitCount"), "hidden aggregates remain forbidden");
        string basis;
        string attackBefore = "player=1|units=41:1:Knight:(9,6):100:True:False;10:3:Archer:(10,6):100:?:?;|imps=";
        string attackAfter = "player=1|units=41:1:Knight:(10,6):100:True:True;|imps=";
        Check(BridgeMod.ConfirmAction("1", "attack", "41", "(10,6)", "", attackBefore, attackAfter, out basis) == "confirmed",
            "live Knight kill with equal defender HP follows original target identity");
        Check(BridgeMod.ConfirmAction("1", "attack", "41", "(10,6)", "", attackBefore,
            attackAfter.Replace("|imps=", "10:3:Archer:(11,6):100:?:?;|imps="), out basis) == "not_confirmed",
            "target relocation without damage cannot be confirmed by replacement occupant");
        Check(BridgeMod.ConfirmAction("1", "attack", "41", "(10,6)", "", attackBefore,
            attackAfter.Replace("|imps=", "10:3:Archer:(11,6):50:?:?;|imps="), out basis) == "confirmed",
            "original target damage remains confirmed after relocation");
        Check(BridgeMod.ConfirmAction("1", "attack", "41", "(10,6)", "", attackBefore,
            attackBefore.Replace("Knight:(9,6):100:True:False", "Knight:(9,6):100:True:True"), out basis) == "not_confirmed",
            "spent attack flag without original target effect is not success");
        Check(BridgeMod.ConfirmAction("1", "attack", "41", "(10,6)", "", attackBefore,
            attackAfter.Replace("True:True", "True:False"), out basis) == "not_confirmed",
            "original target disappearance alone cannot confirm an unused attack");
        string before = BridgeMod.Fingerprint("1", "1", "1", "0", "10", "", "false", "", "T1:1:City:2:1::false;", "", "1", "");
        string roadAfter = BridgeMod.Fingerprint("1", "1", "1", "0", "8", "", "false", "", "T1:1:City:2:1::true;", "", "1", "");
        Check(BridgeMod.ConfirmAction("1", "build", null, "T1", "Road", before, roadAfter, out basis) == "confirmed",
            "Road confirmed by independent native flag even when city improvement unchanged");
        string rewardAfter = BridgeMod.Fingerprint("1", "1", "1", "0", "10", "", "false", "", "T1:1:City:2:1:Workshop:false;", "", "1", "");
        Check(BridgeMod.ConfirmAction("1", "city_reward", null, "T1", "Workshop", before, rewardAfter, out basis) == "confirmed",
            "specific reward confirmed by native chosen list");
        Check(BridgeMod.ConfirmAction("1", "city_reward", null, "T1", "Explorer", before, rewardAfter, out basis) != "confirmed",
            "different reward never confirmed from unrelated city change");
        string parkBefore = BridgeMod.Fingerprint("1", "1", "1", "0", "10", "", "false", "", "T1:1:City:6:1:Park:false;", "", "1", "");
        string parkAfter = BridgeMod.Fingerprint("1", "1", "1", "0", "10", "", "false", "", "T1:1:City:7:1:Park,Park:false;", "", "1", "");
        Check(BridgeMod.ConfirmAction("1", "city_reward", null, "T1", "Park", parkBefore, parkAfter, out basis) == "confirmed",
            "repeated Park confirmed by incremented reward count");
        string emptyRoadBefore = BridgeMod.Fingerprint("1", "1", "1", "0", "10", "", "false", "", "", "", "1", "", "T2:false;");
        string emptyRoadAfter = BridgeMod.Fingerprint("1", "1", "1", "0", "8", "", "false", "", "", "", "1", "", "T2:true;");
        Check(BridgeMod.ConfirmAction("1", "build", null, "T2", "Road", emptyRoadBefore, emptyRoadAfter, out basis) == "confirmed",
            "Road confirmed on tile with no primary improvement");
        var reassigned = new JsonObj().Bool("ok", false).Str("description", "原生\n设置").Bool("ok", true);
        string reassignedJson = reassigned.Close();
        Check(BridgeJsonCodec.Object(reassignedJson).Count == 2 && Json.GetString(reassignedJson, "ok") == "true",
            "success replaces provisional false without duplicate JSON keys");
        Check(reassigned.Close() == reassignedJson, "closing object twice keeps a valid stable frame");
        Check(Json.GetString(new JsonObj().Str("value", "before").Str("value", "after").Close(), "value") == "after",
            "property replacement applies to nonboolean values");
        Check(!BridgeMod.IsUnitActorAction("build") && !BridgeMod.IsUnitActorAction("city_reward"),
            "a unit occupying a building target cannot become its action actor");
        Check(BridgeMod.IsUnitActorAction("move") && BridgeMod.IsUnitActorAction("upgrade_unit"),
            "legacy unit actions retain source-coordinate actor association");
        string shipBefore = "player=1|units=7:1:Raft:T1:100:False:False;|imps=T1:1:City:1:0::false;";
        string shipAfter = "player=1|units=7:1:FutureShip:T1:100:False:False;|imps=T1:1:City:1:0::false;";
        string cityOnly = "player=1|units=7:1:Raft:T1:100:False:False;|imps=T1:1:City:2:1::false;";
        foreach (string alias in new[] {"upgrade", "upgradeunit", "upgrade_unit"})
        {
            Check(BridgeMod.IsUnitActorAction(alias), "upgrade alias associates its own unit actor: " + alias);
            Check(BridgeMod.ConfirmAction("1", alias, "7", "T1", "FutureShip", shipBefore, shipAfter, out basis) == "confirmed",
                "upgrade alias confirms actual own-unit type transition: " + alias);
            Check(BridgeMod.ConfirmAction("1", alias, "7", "T1", "FutureShip", shipBefore, cityOnly, out basis) == "not_confirmed",
                "city growth cannot prove unit upgrade: " + alias);
            Check(BridgeMod.ConfirmAction("1", alias, "7", "T1", "FutureShip", shipAfter, shipAfter, out basis) == "not_confirmed",
                "already requested unit type cannot prove a new upgrade: " + alias);
            Check(BridgeMod.ConfirmAction("1", alias, "7", "T1", "FutureShip", shipBefore.Replace(":Raft:", "::"), shipAfter, out basis) == "unknown",
                "unreadable old type cannot prove unit transition: " + alias);
            Check(BridgeMod.ConfirmAction("1", alias, "7", "T1", "FutureShip", shipBefore.Replace("7:1:", "7:2:"), shipAfter, out basis) == "not_confirmed",
                "foreign unit becoming mine is not a unit upgrade: " + alias);
        }
        Check(BridgeMod.ResolvedRewardCanDismiss(true, true, 0, "Workshop", new[] { "Workshop" }),
            "already committed own reward display may be dismissed");
        Check(!BridgeMod.ResolvedRewardCanDismiss(true, true, 1, "Workshop", new[] { "Workshop" }),
            "pending upgrade cannot be dismissed even with an earlier same reward");
        Check(!BridgeMod.ResolvedRewardCanDismiss(true, true, null, "Workshop", new[] { "Workshop" }),
            "unknown trigger count never permits reward dismissal");
        Check(!BridgeMod.ResolvedRewardCanDismiss(true, false, 0, "Workshop", new[] { "Workshop" }),
            "other player's reward is not dismissal evidence");
        Check(!BridgeMod.ResolvedRewardCanDismiss(true, true, 0, "Explorer", new[] { "Workshop" }),
            "a different chosen reward does not permit dismissal");
        Console.WriteLine("ProtocolRepairTests passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
