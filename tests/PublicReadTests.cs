// Production public-channel readers: typed IDs, complete collection reads and panel fallback.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using PolytopiaAgent;

public sealed class PublicReadPlayer { public object knownPlayers, messages; }
public sealed class PublicReadState { public object pendingCommandTriggers; }
public sealed class PublicReadMessage { public object Sender = (byte)2; public string Type = "PeaceRequest"; }
public sealed class PublicReadTrigger { public object playerId = (byte)1, opponentId = (byte)2; public string type = "PeaceRequest"; }
public sealed class PublicReadPair { public object Key; public object Value = new object(); }
public sealed class PublicReadIndexed
{
    public object Count { get; set; }
    public object First;
    public bool Fail;
    public object this[int i] { get { if (Fail && i == 1) throw new Exception("unreadable slot"); return First; } }
}
public sealed class PublicReadInterrupted : IEnumerable
{
    public object First;
    public IEnumerator GetEnumerator() { yield return First; throw new Exception("unreadable suffix"); }
}
public enum PublicReadAbility { Dash, Static }
public sealed class PublicReadData { public object unitAbilities; }
internal static class PublicReadTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly BridgeMod Host = new BridgeMod();
    private static int passed, failed;
    private static object Call(string method, params object[] args)
    { return typeof(BridgeMod).GetMethod(method, F).Invoke(Host, args); }
    private static void Check(bool value, string label)
    { if (value) { passed++; Console.WriteLine("PASS " + label); } else { failed++; Console.WriteLine("FAIL " + label); } }
    private static void Unknown(string method, string label, params object[] args)
    {
        try { Check(Call(method, args) == null, label); }
        catch { Check(false, label + " (reader threw)"); }
    }
    public static int Main()
    {
        try { Run(); Console.WriteLine("ALL PASS " + passed + " FAILED " + failed); return failed == 0 ? 0 : 1; }
        catch (Exception e) { Console.WriteLine(e); return 1; }
    }
    private static void Run()
    {
        foreach (object value in new object[] { null, true, false, "0", "1.5", PublicReadAbility.Static,
                Double.NaN, Double.PositiveInfinity, Single.NegativeInfinity })
            Check((string)Call("PublicNumber", value) == "null",
                "public numeric fields reject coercion/non-finite values: " + (value == null ? "null" : value.GetType().Name));
        Check((string)Call("PublicNumber", 0) == "0", "public actual zero stays numeric zero");
        Check((string)Call("PublicNumber", UInt64.MaxValue) == "18446744073709551615",
            "public unsigned integer retains full precision");
        Check((string)Call("PublicNumber", 0.1f) == "0.1", "public slider float uses native round-trip format");
        Check((string)Call("PublicNumber", 1e-40) == BridgeMod.ResultsNumber(1e-40)
            && (string)Call("PublicNumber", 1e-40) != "0", "small public floating values cannot underflow through decimal to zero");
        Check((string)Call("PublicNumber", 1e40) == BridgeMod.ResultsNumber(1e40)
            && (string)Call("PublicNumber", 1e40) != "null", "finite public double is not limited by decimal range");
        Check((string)Call("PublicNumber", 0.1234567890123456789012345678m) == "0.1234567890123456789012345678",
            "public decimal precision is retained");
        foreach (object value in new object[] { 1.2m, 1.0, 1f, "1", true, PublicReadAbility.Static, uint.MaxValue })
            Unknown("PublicInt", "non-native integer cannot round or coerce into player identity", value);
        foreach (object value in new object[] { (byte)2, (short)2, 2, 2L, 2UL })
            Check((int?)Call("PublicInt", value) == 2, "native integral CLR types retained");

        var player = new PublicReadPlayer { knownPlayers = new object[] { (byte)2, (byte)2, (byte)3 } };
        var known = (HashSet<byte>)Call("PublicKnownPlayers", player, (byte)1);
        Check(known != null && known.SetEquals(new byte[] { 1, 2, 3 }), "known players retain own ID and deduplicate real IDs");
        player.knownPlayers = new object[0]; known = (HashSet<byte>)Call("PublicKnownPlayers", player, (byte)1);
        Check(known != null && known.SetEquals(new byte[] { 1 }), "real empty contact list retains self");
        player.knownPlayers = new byte[] { 2, 0, 255 };
        known = (HashSet<byte>)Call("PublicKnownPlayers", player, (byte)1);
        Check(known != null && known.SetEquals(new byte[] { 1, 2 }), "native Nature and unknown sentinels stay excluded without invalidating real contacts");
        foreach (object bad in new object[] { null, true, "2", 2.1, -1, 258 })
        {
            player.knownPlayers = new object[] { (byte)2, bad };
            Unknown("PublicKnownPlayers", "bad contact slot does not become a complete smaller roster", player, (byte)1);
        }
        player.knownPlayers = new PublicReadInterrupted { First = (byte)2 };
        Unknown("PublicKnownPlayers", "interrupted known-player enumeration stays unknown without exception", player, (byte)1);
        player.knownPlayers = new PublicReadIndexed { Count = 2.5, First = (byte)2 };
        Unknown("PublicKnownPlayers", "bad contact Count is not a complete empty roster", player, (byte)1);

        var pair = new PublicReadPair { Key = (byte)2 };
        Check(Object.ReferenceEquals(Call("PublicDictionaryValue", new object[] { pair }, (byte)2), pair.Value), "legacy keyed relation fixture remains supported");
        pair.Key = 2.2;
        Unknown("PublicDictionaryValue", "fractional relation key cannot match real player", new object[] { pair }, (byte)2);
        pair.Key = (byte)2;
        Unknown("PublicDictionaryValue", "interrupted fallback cannot return a misleading readable prefix", new PublicReadInterrupted { First = pair }, (byte)2);

        var message = new PublicReadMessage(); player.messages = new object[] { message };
        Check((bool?)Call("PublicIncomingPeaceRequest", player, (byte)2) == true, "real correlated incoming request retained");
        foreach (object sender in new object[] { 2.2, "2", true, 0, 255 })
        {
            message.Sender = sender;
            Unknown("PublicIncomingPeaceRequest", "malformed sender cannot authorize or deny a request", player, (byte)2);
        }
        message.Sender = (byte)2; player.messages = new PublicReadInterrupted { First = message };
        Unknown("PublicIncomingPeaceRequest", "interrupted inbox read does not throw or authorize prefix request", player, (byte)2);
        player.messages = new PublicReadIndexed { Count = 1.5, First = message };
        Unknown("PublicIncomingPeaceRequest", "invalid inbox Count cannot prove absence", player, (byte)2);
        player.messages = new object[0];
        Check((bool?)Call("PublicIncomingPeaceRequest", player, (byte)2) == false, "known empty inbox proves no stored request");

        var trigger = new PublicReadTrigger { playerId = 1.2 };
        Unknown("PublicActivePeaceTrigger", "fractional trigger actor cannot match controlled player", new object[] { trigger }, (byte)1, (byte)2, null);
        trigger.playerId = (byte)1; trigger.opponentId = 2.2;
        Unknown("PublicActivePeaceTrigger", "fractional trigger opponent cannot authorize response", new object[] { trigger }, (byte)1, (byte)2, null);
        var state = new PublicReadState { pendingCommandTriggers = new PublicReadInterrupted { First = trigger } };
        try
        {
            var triggers = Call("PendingTriggerList", state) as IEnumerable;
            bool empty = triggers == null;
            if (triggers != null) foreach (var item in triggers) { empty = false; }
            Check(empty, "pending-trigger enumeration failure converted to unavailable before caller iterates");
        }
        catch { Check(false, "pending-trigger reader left throwing lazy enumeration"); }
        state.pendingCommandTriggers = null;
        Check(((IList)Call("PendingTriggerList", state)).Count == 0, "verified native null-on-fresh-match trigger convention preserved");

        var data = new PublicReadData { unitAbilities = new PublicReadIndexed { Count = 2.5, First = PublicReadAbility.Static } };
        Unknown("URPanelStatic", "bad fallback ability Count cannot claim non-Static or reveal progress", data);
        data.unitAbilities = new object[0]; Check((bool?)Call("URPanelStatic", data) == false, "complete empty ability list proves non-Static");
        data.unitAbilities = new object[] { PublicReadAbility.Dash, PublicReadAbility.Static };
        Check((bool?)Call("URPanelStatic", data) == true, "complete native Static ability retained");
    }
}
