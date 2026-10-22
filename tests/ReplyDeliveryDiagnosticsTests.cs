using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using PolytopiaAgent;

internal static class ReplyDeliveryDiagnosticsTests
{
    private const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    private static readonly BridgeMod Host = new BridgeMod();
    private static int passed, failed;
    private static void Check(bool ok, string label)
    { Console.WriteLine((ok ? "PASS " : "FAIL ") + label); if (ok) passed++; else failed++; }
    private static object Call(string name, params object[] args) { return typeof(BridgeMod).GetMethod(name, F).Invoke(Host, args); }
    private static void Set(string name, object value) { typeof(BridgeMod).GetField(name, F).SetValue(Host, value); }
    private static string Log() { return File.Exists(BridgeMod.LogPath) ? File.ReadAllText(BridgeMod.LogPath) : ""; }
    public static int Main()
    {
        string root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        if (!Path.GetFullPath(BridgeMod.WorkDir).StartsWith(root, StringComparison.OrdinalIgnoreCase)) return 2;
        Directory.CreateDirectory(BridgeMod.WorkDir);
        string ready = Path.Combine(BridgeMod.WorkDir, "bridge-ready.json"), output = BridgeMod.OutPath;
        byte[] oldReady = File.Exists(ready) ? File.ReadAllBytes(ready) : null;
        string backup = output + "." + Guid.NewGuid().ToString("N") + ".backup";
        bool oldOutput = File.Exists(output);
        if (oldOutput) File.Move(output, backup);
        try
        {
            File.WriteAllText(ready, "{\"pid\":" + BridgeMod.OwnPid.ToString(CultureInfo.InvariantCulture) + "}");
            Set("_activeRequestId", 77L); Set("_combatStream", "reply-stream");
            Set("_combatFrame", new BridgeMod.CombatFrame { Episode = "reply-ep", Me = "2" });
            Call("BeginReadRequest", "action");
            var tickets = (Dictionary<long, BridgeMod.CombatTicket>)typeof(BridgeMod).GetField("_combatTickets", F).GetValue(Host);
            var ticket = new BridgeMod.CombatTicket { Id = 9, RequestId = 77, Stream = "reply-stream", Dispatched = true };
            tickets[9] = ticket; Set("_combatTicketId", 9L);
            Directory.CreateDirectory(output);
            string before = Log(); Call("Emit", "{\"ok\":true,\"private\":\"SECRET_REPLY_PAYLOAD_(99,99)\"}", 77L);
            string trace = Log().Substring(before.Length);
            Check(trace.Contains("reply delivery failed #77") && trace.Contains("ep=reply-ep") && trace.Contains("player=2")
                && trace.Contains("stream=reply-stream") && trace.Contains("ticket=9") && trace.Contains("stage=write"),
                "actual output I/O failure records request, context and its unique new native ticket");
            Check(!trace.Contains("SECRET_REPLY") && !trace.Contains("(99,99)"), "delivery failure trace never copies the response payload");
            Check(tickets.Count == 1 && ReferenceEquals(tickets[9], ticket) && ticket.Dispatched && ticket.FinalFrame == null,
                "failed delivery does not execute, complete, replace or replay a native ticket");
            Call("EndReadRequest"); Directory.Delete(output);
            Call("Emit", "{\"ok\":true}", 78L);
            var response = BridgeJsonCodec.Object(File.ReadAllText(output).Trim());
            Check(long.Parse(BridgeJsonCodec.Scalar(response["reqId"]), CultureInfo.InvariantCulture) == 78 && Equals(response["ok"], true),
                "successful reply format and reqId remain unchanged");
            File.WriteAllText(ready, "{\"pid\":" + (BridgeMod.OwnPid + 1).ToString(CultureInfo.InvariantCulture) + "}");
            long length = new FileInfo(output).Length; before = Log();
            Set("_activeRequestId", 79L); Call("BeginReadRequest", "world"); Call("Emit", "{\"ok\":true}", 79L); Call("EndReadRequest");
            trace = Log().Substring(before.Length);
            Check(trace.Contains("reply delivery failed #79") && trace.Contains("stage=ownership") && trace.Contains("ticket=?"),
                "explicit reply refused after ownership loss is logged without guessing an old ticket");
            Check(new FileInfo(output).Length == length && tickets.Count == 1, "ownership loss still prevents all output and native state changes");
            before = Log(); Call("Emit", "{\"ok\":true}", 0L);
            Check(Log() == before, "expected non-owner background snapshot does not flood logs");
        }
        finally
        {
            Call("EndReadRequest");
            if (Directory.Exists(output)) Directory.Delete(output);
            if (File.Exists(output)) File.Delete(output);
            if (oldOutput) File.Move(backup, output);
            if (oldReady == null) File.Delete(ready); else File.WriteAllBytes(ready, oldReady);
        }
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
