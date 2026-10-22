using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using PolytopiaAgent;

internal static class InboxDispatchBudgetTests
{
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static int passed, failed;
    private static void Check(bool value, string name)
    { Console.WriteLine((value ? "PASS " : "FAIL ") + name); if (value) passed++; else failed++; }
    private static void Set(BridgeMod host, string key, object value) { typeof(BridgeMod).GetField(key, F).SetValue(host, value); }
    private static object Get(BridgeMod host, string key) { return typeof(BridgeMod).GetField(key, F).GetValue(host); }
    private static string Request(int id) { return "{\"id\":" + id + ",\"cmd\":\"ping\"}\n"; }
    private static List<long> Replies()
    {
        var result = new List<long>();
        if (File.Exists(BridgeMod.OutPath)) foreach (string line in File.ReadAllLines(BridgeMod.OutPath))
        {
            object id;
            if (BridgeJsonCodec.Object(line).TryGetValue("reqId", out id))
                result.Add(long.Parse(BridgeJsonCodec.Scalar(id), CultureInfo.InvariantCulture));
        }
        return result;
    }
    private static void Poll(BridgeMod host)
    {
        Set(host, "_tick", (long)Get(host, "_tick") + 3);
        Set(host, "_nextInboxPollTime", 0L);
        typeof(BridgeMod).GetMethod("ProcessInbox", F).Invoke(host, null);
    }
    public static int Main()
    {
        string root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        if (!Path.GetFullPath(BridgeMod.WorkDir).StartsWith(root, StringComparison.OrdinalIgnoreCase)) return 2;
        Directory.CreateDirectory(BridgeMod.WorkDir);
        string ready = Path.Combine(BridgeMod.WorkDir, "bridge-ready.json");
        string cmd = Path.Combine(BridgeMod.WorkDir, "cmd.jsonl");
        string[] paths = { ready, cmd, BridgeMod.OutPath };
        var saved = new Dictionary<string, byte[]>();
        foreach (string p in paths) saved[p] = File.Exists(p) ? File.ReadAllBytes(p) : null;
        Action<bool> owner = delegate(bool mine) { File.WriteAllText(ready, "{\"pid\":" + (BridgeMod.OwnPid + (mine ? 0 : 1)) + "}"); };
        try
        {
            owner(true); File.WriteAllText(BridgeMod.OutPath, "");
            var text = new StringBuilder(); for (int i = 1; i <= 40; i++) text.Append(Request(i));
            File.WriteAllText(cmd, text.ToString(), new UTF8Encoding(false));
            var host = new BridgeMod(); Poll(host);
            int first = Replies().Count;
            Check(first > 0 && first <= 4, "production ProcessInbox yields after a bounded prefix instead of draining forty requests in one frame");
            Check(((string)Get(host, "_pending")).Length > 0, "unprocessed requests stay buffered after first dispatch batch");
            long readOffset = (long)Get(host, "_cmdOffset");
            for (int i = 41; i <= 45; i++) File.AppendAllText(cmd, Request(i), new UTF8Encoding(false));
            Poll(host);
            Check((long)Get(host, "_cmdOffset") == readOffset, "complete buffered requests apply backpressure before reading another append");
            int rounds = 0;
            while (Replies().Count < 45 && rounds++ < 80) Poll(host);
            var ids = Replies(); bool ordered = ids.Count == 45;
            for (int i = 0; ordered && i < ids.Count; i++) ordered = ids[i] == i + 1;
            Check(ordered, "buffered and later appended requests drain exactly once in original order even without further appends");

            // A complete buffered request must survive ownership loss even with unchanged file length.
            Set(host, "_pending", Request(77)); int before = Replies().Count; owner(false); Poll(host);
            Check((string)Get(host, "_pending") == Request(77) && Replies().Count == before,
                "ownership loss preserves buffered complete requests and emits nothing");
            owner(true); Poll(host);
            Check(Replies().Count == before + 1 && Replies()[before] == 77 && (string)Get(host, "_pending") == "",
                "regaining ownership drains the same buffered request without new file bytes");
            Set(host, "_pending", Request(78)); before = Replies().Count;
            byte[] commandBytes = File.ReadAllBytes(cmd); File.Delete(cmd); Poll(host);
            Check((string)Get(host, "_pending") == Request(78) && Replies().Count == before,
                "missing command file pauses buffered dispatch until truncation can be checked");
            File.WriteAllBytes(cmd, commandBytes); Poll(host);
            Check(Replies().Count == before + 1 && Replies()[before] == 78,
                "restoring unchanged command file resumes buffered dispatch without replay");
            Set(host, "_pending", Request(88));
            File.WriteAllText(cmd, Request(99), new UTF8Encoding(false)); Poll(host);
            Check(Replies()[Replies().Count - 1] == 99 && !Replies().Contains(88),
                "owner-side file truncation invalidates stale buffered requests before dispatch");

            string partial = Request(101).TrimEnd('\n'); File.AppendAllText(cmd, partial, new UTF8Encoding(false));
            before = Replies().Count; Poll(host);
            Check(Replies().Count == before && (string)Get(host, "_pending") == partial, "partial final request waits for its newline");
            File.AppendAllText(cmd, "\n", new UTF8Encoding(false)); Poll(host);
            Check(Replies().Count == before + 1 && Replies()[before] == 101, "completed partial request is dispatched once on later poll");
            File.AppendAllText(cmd, "{\"id\":102, broken}\n" + Request(103), new UTF8Encoding(false));
            before = Replies().Count; int rawBefore = File.ReadAllLines(BridgeMod.OutPath).Length; Poll(host); Poll(host);
            Check(File.ReadAllLines(BridgeMod.OutPath).Length == rawBefore + 2
                && Replies().Count == before + 1 && Replies()[before] == 103,
                "malformed JSON keeps its uncorrelated failure response and does not lose following valid request");
            Check(!(bool)Get(host, "_readRequestScope") && (long)Get(host, "_activeRequestId") == 0,
                "each deferred request finishes request scope cleanup even after a malformed predecessor");
            var timingsMethod = typeof(BridgeMod).GetMethod("RequestTimingsJson", F);
            var timings = BridgeJsonCodec.Object((string)timingsMethod.Invoke(host, null));
            var pingTiming = (Dictionary<string, object>)timings["ping"];
            Check(long.Parse(BridgeJsonCodec.Scalar(pingTiming["samples"])) == Replies().Count,
                "actual ProcessInbox records each completed ping exactly once, excluding malformed JSON");
            var timingHost = new BridgeMod(); var recordTiming = typeof(BridgeMod).GetMethod("RecordRequestTiming", F);
            long frequency = System.Diagnostics.Stopwatch.Frequency;
            recordTiming.Invoke(timingHost, new object[] { "world", frequency / 100 });
            recordTiming.Invoke(timingHost, new object[] { "world", frequency * 3 / 100 });
            for (int i = 0; i < 100; i++) recordTiming.Invoke(timingHost, new object[] { "SECRET_UNTRUSTED_" + i, frequency / 1000 });
            string timingJson = (string)timingsMethod.Invoke(timingHost, null);
            timings = BridgeJsonCodec.Object(timingJson); var worldTiming = (Dictionary<string, object>)timings["world"];
            Check(long.Parse(BridgeJsonCodec.Scalar(worldTiming["samples"])) == 2
                && Math.Abs(double.Parse(BridgeJsonCodec.Scalar(worldTiming["totalMs"]), CultureInfo.InvariantCulture) - 40) < 0.1
                && Math.Abs(double.Parse(BridgeJsonCodec.Scalar(worldTiming["maxMs"]), CultureInfo.InvariantCulture) - 30) < 0.1
                && Math.Abs(double.Parse(BridgeJsonCodec.Scalar(worldTiming["lastMs"]), CultureInfo.InvariantCulture) - 30) < 0.1,
                "request timing distinguishes total, slowest and latest main-thread duration");
            Check(timings.Count == 2 && !timingJson.Contains("SECRET")
                && long.Parse(BridgeJsonCodec.Scalar(((Dictionary<string, object>)timings["other"])["samples"])) == 100,
                "arbitrary request names cannot grow timing buckets or leak argument-like strings");

            var drain = typeof(BridgeMod).GetMethod("TransportDrainPending", F);
            Check(drain != null, "production dispatch scheduler exposes its actual bounded drain for deterministic clock verification");
            if (drain != null)
            {
                long now = 0; var delivered = new List<string>(); var stats = new BridgeMod.TransportIoStats();
                Func<long> clock = delegate { return now; };
                Action<string> expensive = delegate(string line) { delivered.Add(line); now += 10; };
                object[] args = { "a\nb\nc\n", BridgeMod.WorkDir, BridgeMod.OwnPid, stats, expensive, clock, 1000L };
                drain.Invoke(null, args);
                Check(delivered.Count == 1 && (string)args[0] == "b\nc\n", "single slow request is allowed to finish but no second request starts beyond time budget");
                now = 0; delivered.Clear();
                Action<string> lose = delegate(string line) { delivered.Add(line); owner(false); };
                args = new object[] { "a\nb\n", BridgeMod.WorkDir, BridgeMod.OwnPid, stats, lose, clock, 1000L };
                drain.Invoke(null, args);
                Check(delivered.Count == 1 && (string)args[0] == "b\n", "ownership is checked again before consuming the next buffered line");
                owner(true); delivered.Clear(); now = 0;
                Action<string> fast = delegate(string line) { delivered.Add(line); };
                args = new object[] { "a\nb\nc\nd\ne\n", BridgeMod.WorkDir, BridgeMod.OwnPid, stats, fast, clock, 1000L };
                drain.Invoke(null, args);
                Check(delivered.Count == 4 && (string)args[0] == "e\n", "fast dispatch still obeys independent four-line cap");
                args = new object[] { "\n \n\n\na\n", BridgeMod.WorkDir, BridgeMod.OwnPid, stats, fast, clock, 1000L };
                delivered.Clear(); drain.Invoke(null, args);
                Check(delivered.Count == 0 && (string)args[0] == "a\n", "blank lines also consume scheduling budget without reaching command router");
                var measured = BridgeJsonCodec.Object(stats.SnapshotJson());
                Check(long.Parse(BridgeJsonCodec.Scalar(measured["dispatchBudgetYields"])) >= 3
                    && long.Parse(BridgeJsonCodec.Scalar(measured["maxDispatchBatchLines"])) == 4,
                    "private diagnostic counters report yielded batches and maximum dispatched prefix");
            }
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); failed++; }
        finally
        {
            foreach (string p in paths) { if (saved[p] == null) File.Delete(p); else File.WriteAllBytes(p, saved[p]); }
        }
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
