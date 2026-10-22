// codex-dsh-071 transport hot-path tests: real file behaviour and real operation counts.
//
// Run:  TransportPerformanceTests.exe <path-to-PolytopiaAgentBridge.dll>
// Loads the production assembly and drives the internal transport helpers through reflection, so
// nothing here is a copy of the implementation: every assertion exercises compiled production code
// against real files in a temp directory.
//
// What it proves:
//   * the process id is resolved once and reused (no Process object per ownership check);
//   * ownership is re-read from disk on every check - a ready-owner switch is visible immediately;
//   * fail-closed on a missing/locked/malformed ready file and on a throwing pid probe;
//   * an idle command file costs ZERO ready-file reads, and a non-owner never consumes (no swallow);
//   * every request still passes the ready-owner + bridgePid check;
//   * per-frame low-level I/O counts for the old vs new ordering, and a wall-time micro-benchmark
//     (operation counts + microseconds - explicitly NOT a Unity FPS measurement).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

internal static class TransportPerformanceTests
{
    private static Type bridge, statsType;
    private static MethodInfo mOwnReadyIn, mTargetsIn, mPoll, mSnapshotPrecheck, mReadReady,
        mResetPidForTests, mReadyPidMatches, mReadyPidValue;
    private static PropertyInfo pOwnPid, pProbeCalls, pProbeFailures;
    private static int passed, failed;
    private static readonly List<string> failures = new List<string>();

    private static void Check(bool value, string name)
    {
        Check(value, name, null);
    }

    private static void Check(bool value, string name, string detail)
    {
        if (value)
        {
            passed++;
            Console.WriteLine("PASS  " + name);
        }
        else
        {
            failed++;
            failures.Add(name + (detail == null ? "" : "  :: " + detail));
            Console.WriteLine("FAIL  " + name + (detail == null ? "" : "  :: " + detail));
        }
    }

    private static void CheckEq(object expected, object actual, string name)
    {
        bool ok = expected == null ? actual == null : expected.Equals(actual);
        Check(ok, name, ok ? null : "expected " + (expected ?? "null") + " got " + (actual ?? "null"));
    }

    // ------------------------------------------------------------------ reflection plumbing
    private static bool SameParams(MethodInfo m, Type[] ps)
    {
        var have = m.GetParameters();
        if (have.Length != ps.Length) return false;
        for (int i = 0; i < ps.Length; i++)
        {
            if (have[i].ParameterType != ps[i]) return false;
        }
        return true;
    }

    private static MethodInfo Find(string name, Type[] ps)
    {
        foreach (var m in bridge.GetMethods(BindingFlags.Static | BindingFlags.Public
            | BindingFlags.NonPublic))
        {
            if (m.Name == name && SameParams(m, ps)) return m;
        }
        throw new Exception("method not found: " + name);
    }

    private static PropertyInfo Prop(string name)
    {
        var p = bridge.GetProperty(name, BindingFlags.Static | BindingFlags.Public
            | BindingFlags.NonPublic);
        if (p == null) throw new Exception("property not found: " + name);
        return p;
    }

    private static object NewStats()
    {
        return Activator.CreateInstance(statsType, true);
    }

    private static int Stat(object stats, string name)
    {
        var p = statsType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public
            | BindingFlags.NonPublic);
        return (int)p.GetValue(stats, null);
    }

    private static bool OwnReady(string dir, int pid, object stats)
    {
        return (bool)mOwnReadyIn.Invoke(null, new object[] { dir, pid, stats });
    }

    private static bool Targets(string dir, string raw, int pid, object stats)
    {
        return (bool)mTargetsIn.Invoke(null, new object[] { dir, raw, pid, stats });
    }

    private static bool SnapshotPrecheck(long tick, long lastProbe, bool force, bool gameActive,
        string dir, int pid, object stats)
    {
        return (bool)mSnapshotPrecheck.Invoke(null,
            new object[] { tick, lastProbe, force, gameActive, dir, pid, stats });
    }

    private sealed class PollState
    {
        internal long LastPollTick = -1000;   // the first poll of a frame sequence is due
        internal long Offset;
        internal string Pending = "";
        internal string LastText;             // null = the seam skipped (no I/O, no allocation)
    }

    /// <summary>
    /// One poll exactly as ProcessInbox drives it: the transport seam returns the newly readable
    /// bytes (or null), and the caller appends them and splits complete lines. The split mirrors the
    /// UNCHANGED loop in BridgeMod.ProcessInbox, so the frequency assertions stay about the transport
    /// seam rather than about a copy of the dispatch loop.
    /// </summary>
    private static List<string> Poll(long tick, string cmdPath, string dir, int pid, object stats,
        PollState state)
    {
        var args = new object[] { tick, state.LastPollTick, state.Offset, state.Pending, cmdPath, dir,
            pid, stats };
        string text = (string)mPoll.Invoke(null, args);
        state.LastPollTick = (long)args[1];
        state.Offset = (long)args[2];
        state.Pending = (string)args[3];
        state.LastText = text;
        var lines = new List<string>();
        if (text == null) { return lines; }
        state.Pending += text;
        int nl;
        while ((nl = state.Pending.IndexOf('\n')) >= 0)
        {
            string line = state.Pending.Substring(0, nl).Trim();
            state.Pending = state.Pending.Substring(nl + 1);
            if (line.Length > 0) { lines.Add(line); }
        }
        return lines;
    }

    private static int OwnPid()
    {
        return (int)pOwnPid.GetValue(null, null);
    }

    private static int ThrowingProbe()
    {
        throw new InvalidOperationException("probe-failure");
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "transport-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteReady(string dir, string json)
    {
        File.WriteAllText(Path.Combine(dir, "bridge-ready.json"), json, new UTF8Encoding(false));
    }

    private static void Append(string path, string text)
    {
        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            sw.Write(text);
            sw.Write('\n');
        }
    }

    // ------------------------------------------------------------------ tests
    private static void Main(string[] args)
    {
        int exit = Run(args);
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        for (int i = 0; i < failures.Count; i++) Console.WriteLine("  FAILED " + failures[i]);
        Environment.ExitCode = exit;
    }

    private static int Run(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("usage: TransportPerformanceTests <dll>"); return 2; }
        var assembly = Assembly.LoadFrom(args[0]);
        bridge = assembly.GetType("PolytopiaAgent.BridgeMod", true);
        statsType = bridge.GetNestedType("TransportIoStats", BindingFlags.Public | BindingFlags.NonPublic);
        if (statsType == null) throw new Exception("TransportIoStats not found");

        mOwnReadyIn = Find("TransportOwnsReadyFileIn",
            new[] { typeof(string), typeof(int), statsType });
        mTargetsIn = Find("TransportRequestTargetsThisInstanceIn",
            new[] { typeof(string), typeof(string), typeof(int), statsType });
        mPoll = Find("TransportPollCmdText", new[] { typeof(long), typeof(long).MakeByRefType(),
            typeof(long).MakeByRefType(), typeof(string).MakeByRefType(), typeof(string), typeof(string),
            typeof(int), statsType });
        mSnapshotPrecheck = Find("TransportSnapshotPrecheck", new[] { typeof(long), typeof(long),
            typeof(bool), typeof(bool), typeof(string), typeof(int), statsType });
        mReadReady = Find("TransportReadReadyText", new[] { typeof(string), statsType });
        mResetPidForTests = Find("TransportResetOwnPidForTests", new[] { typeof(Func<int>) });
        mReadyPidMatches = Find("TransportReadyPidMatches", new[] { typeof(string), typeof(int) });
        mReadyPidValue = Find("TransportReadyPidValue", Type.EmptyTypes);
        pOwnPid = Prop("OwnPid");
        pProbeCalls = Prop("OwnPidProbeCalls");
        pProbeFailures = Prop("OwnPidProbeFailures");

        const int MINE = 6972, OTHER = 33184;
        string dir = TempDir();

        // ------------- 1. pid is resolved once and reused -------------
        Console.WriteLine("=== process id: resolved once, failures not cached ===");
        mResetPidForTests.Invoke(null, new object[] { (Func<int>)delegate { return MINE; } });
        WriteReady(dir, "{\"transport\":\"file\",\"pid\":" + MINE + "}");
        var stats = NewStats();
        bool allOwned = true;
        for (int i = 0; i < 50; i++)
        {
            if (!OwnReady(dir, OwnPid(), stats)) { allOwned = false; break; }
        }
        Check(allOwned, "50 ownership checks all succeed with the resolved pid", null);
        CheckEq(1, (int)pProbeCalls.GetValue(null, null), "the pid probe ran exactly once for 50 checks");
        CheckEq(MINE, OwnPid(), "OwnPid returns the cached constant");
        CheckEq(MINE, (int)mReadyPidValue.Invoke(null, null), "TransportReadyPidValue uses the cached pid");

        // ------------- 2. ready-owner switch is visible immediately -------------
        Console.WriteLine("=== ready owner switch: no stale cache ===");
        var s2 = NewStats();
        WriteReady(dir, "{\"pid\":" + OTHER + "}");
        Check(!OwnReady(dir, OwnPid(), s2), "another process's ready file is not my ownership");
        WriteReady(dir, "{\"pid\":" + MINE + "}");
        Check(OwnReady(dir, OwnPid(), s2), "ownership returns as soon as the ready file is mine again");
        WriteReady(dir, "{\"pid\":" + OTHER + "}");
        Check(!OwnReady(dir, OwnPid(), s2), "ownership is lost again when the file switches back");
        CheckEq(3, Stat(s2, "ReadyReads"),
            "three ownership checks performed three REAL ready-file reads");
        WriteReady(dir, "{\"pid\":" + MINE + "}");

        // ------------- 3. per-request identity: ready owner + bridgePid -------------
        Console.WriteLine("=== per-request identity (ready owner + bridgePid) ===");
        string bound = "{\"id\":1,\"cmd\":\"ping\",\"bridgePid\":" + MINE + "}";
        string legacy = "{\"id\":2,\"cmd\":\"ping\"}";
        string boundOther = "{\"id\":3,\"cmd\":\"ping\",\"bridgePid\":" + OTHER + "}";
        WriteReady(dir, "{\"pid\":" + MINE + "}");
        Check(Targets(dir, bound, OwnPid(), stats), "bound request targets its own instance");
        Check(Targets(dir, legacy, OwnPid(), stats), "legacy envelope uses the ready-owner guard");
        Check(!Targets(dir, boundOther, OwnPid(), stats),
            "request bound to another pid is refused while I own the ready file");
        WriteReady(dir, "{\"pid\":" + OTHER + "}");
        Check(!Targets(dir, bound, OwnPid(), stats),
            "owner switch alone refuses the previously valid bound request");
        Check(!Targets(dir, legacy, OwnPid(), stats),
            "owner switch alone refuses the legacy request too");
        WriteReady(dir, "{\"pid\":" + MINE + "}");

        // ------------- 4. fail-closed on exceptions -------------
        Console.WriteLine("=== fail closed: missing, locked, malformed, throwing probe ===");
        var s4 = NewStats();
        string missing = TempDir();
        Check(!OwnReady(missing, OwnPid(), s4), "missing ready file is not ownership");
        Check(Stat(s4, "ReadyFailures") >= 1, "missing ready file is counted as a failed read");

        string lockedDir = TempDir();
        string lockedPath = Path.Combine(lockedDir, "bridge-ready.json");
        File.WriteAllText(lockedPath, "{\"pid\":" + MINE + "}");
        bool lockedResult;
        using (var hold = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            lockedResult = OwnReady(lockedDir, OwnPid(), s4);
        }
        Check(!lockedResult, "a ready file locked by another handle fails closed (no exception escapes)");
        Check(!OwnReady(dir, 0, s4), "pid 0 can never own the transport");

        WriteReady(dir, "{\"pid\":");
        Check(!OwnReady(dir, OwnPid(), s4), "malformed ready JSON is not ownership");
        WriteReady(dir, "{\"pid\":" + MINE + "}");

        mResetPidForTests.Invoke(null, new object[] { (Func<int>)ThrowingProbe });
        CheckEq(0, OwnPid(), "a throwing pid probe resolves to pid 0");
        Check(!OwnReady(dir, OwnPid(), s4), "a throwing pid probe fails closed");
        Check(!Targets(dir, bound, OwnPid(), s4), "no request targets this instance without a pid");
        Check((int)pProbeFailures.GetValue(null, null) >= 1, "the probe failure is counted");
        mResetPidForTests.Invoke(null, new object[] { (Func<int>)delegate { return MINE; } });
        CheckEq(MINE, OwnPid(), "a recovered probe resolves again (failures are not cached)");
        Check(OwnReady(dir, OwnPid(), s4), "ownership works again after the probe recovers");

        // ------------- 5. idle command file costs zero ready reads -------------
        Console.WriteLine("=== command-file poll: budget -> length -> ready owner ===");
        string ioDir = TempDir();
        string ioCmd = Path.Combine(ioDir, "cmd.jsonl");
        WriteReady(ioDir, "{\"pid\":" + MINE + "}");
        File.WriteAllText(ioCmd, "", new UTF8Encoding(false));
        Append(ioCmd, "{\"id\":1,\"cmd\":\"ping\",\"bridgePid\":" + MINE + "}");
        var st = new PollState();
        var s5 = NewStats();

        var l0 = Poll(0, ioCmd, ioDir, MINE, s5, st);          // first poll is due
        CheckEq(1, l0.Count, "first poll yields the pending request");
        Check(st.LastText != null, "the due poll returned the new bytes", null);
        CheckEq(1, Stat(s5, "ReadyReads"), "first poll read the ready file once");
        CheckEq(1, Stat(s5, "CmdStats"), "first poll performed one command-file length stat");
        CheckEq(1, Stat(s5, "CmdReads"), "first poll read the command bytes once");

        int readyBefore = Stat(s5, "ReadyReads"), cmdStatsBefore = Stat(s5, "CmdStats");
        Poll(1, ioCmd, ioDir, MINE, s5, st);
        Check(st.LastText == null, "a budget-skipped frame returns null (no allocation, no I/O)", null);
        Poll(2, ioCmd, ioDir, MINE, s5, st);
        Check(st.LastText == null, "the second budget-skipped frame returns null too", null);
        CheckEq(readyBefore, Stat(s5, "ReadyReads"), "two budget-skipped frames did zero ready reads");
        CheckEq(cmdStatsBefore, Stat(s5, "CmdStats"), "two budget-skipped frames did zero file stats");
        CheckEq(2, Stat(s5, "PollBudgetSkips"), "both frames were recorded as budget skips");

        int readyBefore2 = Stat(s5, "ReadyReads");
        var l3 = Poll(3, ioCmd, ioDir, MINE, s5, st);          // due, but the file did not change
        CheckEq(0, l3.Count, "an unchanged command file yields nothing");
        Check(st.LastText == null, "an unchanged command file returns null (no allocation)", null);
        CheckEq(readyBefore2, Stat(s5, "ReadyReads"),
            "an unchanged command file did ZERO ready-file reads after the budget passed");
        CheckEq(1, Stat(s5, "CmdUnchanged"), "the unchanged command file was detected by length only");

        Append(ioCmd, "{\"id\":2,\"cmd\":\"ping\",\"bridgePid\":" + MINE + "}");
        var l6 = Poll(6, ioCmd, ioDir, MINE, s5, st);
        CheckEq(1, l6.Count, "the newly appended request is yielded");
        CheckEq(2, Stat(s5, "CmdReads"), "the appended bytes were read once");

        // ------------- 6. a non-owner never consumes (nothing is swallowed) -------------
        Console.WriteLine("=== non-owner does not consume; no request is swallowed ===");
        Append(ioCmd, "{\"id\":3,\"cmd\":\"ping\",\"bridgePid\":" + MINE + "}");
        long offsetBefore = st.Offset;
        WriteReady(ioDir, "{\"pid\":" + OTHER + "}");
        int notOwnerBefore = Stat(s5, "NotOwner");
        var l9 = Poll(9, ioCmd, ioDir, MINE, s5, st);
        CheckEq(0, l9.Count, "a non-owner yields no request");
        Check(st.LastText == null, "a non-owner poll returns null", null);
        CheckEq(notOwnerBefore + 1, Stat(s5, "NotOwner"), "the non-owner poll is recorded");
        CheckEq(offsetBefore, st.Offset, "a non-owner does not advance the read offset");
        WriteReady(ioDir, "{\"pid\":" + MINE + "}");
        var l12 = Poll(12, ioCmd, ioDir, MINE, s5, st);
        CheckEq(1, l12.Count, "the request is still there when the ready owner returns");
        CheckEq("{\"id\":3,\"cmd\":\"ping\",\"bridgePid\":" + MINE + "}", l12[0],
            "the yielded request is exactly the one the non-owner skipped");

        // ------------- 6b. a non-owner must not reset consumption state on truncation -------------
        Console.WriteLine("=== non-owner + truncated file: consumption state is untouched ===");
        st.Pending = "{\"partial\":";
        long offBeforeTrunc = st.Offset;
        int resetsBefore = Stat(s5, "TruncationResets");
        File.WriteAllText(ioCmd, "", new UTF8Encoding(false));       // truncated while I am NOT owner
        WriteReady(ioDir, "{\"pid\":" + OTHER + "}");
        var l18 = Poll(18, ioCmd, ioDir, MINE, s5, st);
        CheckEq(0, l18.Count, "a non-owner sees nothing after the truncation");
        CheckEq(offBeforeTrunc, st.Offset, "a non-owner did NOT reset the offset on truncation");
        CheckEq("{\"partial\":", st.Pending, "a non-owner did NOT clear the pending buffer");
        CheckEq(resetsBefore, Stat(s5, "TruncationResets"), "no truncation reset was counted");
        WriteReady(ioDir, "{\"pid\":" + MINE + "}");
        Append(ioCmd, "{\"id\":5,\"cmd\":\"ping\",\"bridgePid\":" + MINE + "}");
        var l21 = Poll(21, ioCmd, ioDir, MINE, s5, st);
        CheckEq(1, l21.Count, "the owner then reads the new file from offset 0");
        Check(l21.Count == 1 && l21[0].IndexOf("\"id\":5") >= 0, "the owner got the new request",
            l21.Count == 1 ? l21[0] : "none");
        CheckEq(resetsBefore + 1, Stat(s5, "TruncationResets"),
            "the reset happened once, as the owner");

        // ------------- 7. truncation is recovered, not swallowed -------------
        Console.WriteLine("=== truncated command file is re-read from the start ===");
        // NOTE: the transport detects new bytes by LENGTH (as before). A rewritten file only counts
        // as a truncation when it is SHORTER than the current offset; an equal-length rewrite is
        // "unchanged" and a longer one is read as a suffix - both are the pre-existing behaviour.
        File.WriteAllText(ioCmd, "", new UTF8Encoding(false));
        Append(ioCmd, "{\"id\":4,\"cmd\":\"ping\"}");     // shorter than the previous request
        var l24 = Poll(24, ioCmd, ioDir, MINE, s5, st);
        CheckEq(1, l24.Count, "after truncation the new request is read from offset 0");
        Check(l24.Count == 1 && l24[0].IndexOf("\"id\":4") >= 0, "the re-read request is the new one",
            l24.Count == 1 ? l24[0] : "none");

        // ------------- 8. snapshot precheck: budget before any I/O -------------
        Console.WriteLine("=== snapshot precheck: budget first, then ready owner ===");
        var s8 = NewStats();
        string snapDir = TempDir();
        WriteReady(snapDir, "{\"pid\":" + OTHER + "}");   // NOT mine: budget must still skip first
        int readsBefore = Stat(s8, "ReadyReads");
        Check(!SnapshotPrecheck(100, 100, false, false, snapDir, MINE, s8),
            "budget not due -> precheck false even when this process is not the owner");
        CheckEq(readsBefore, Stat(s8, "ReadyReads"),
            "budget not due performed ZERO ready-file reads");
        CheckEq(1, Stat(s8, "SnapshotBudgetSkips"), "the budget skip is recorded");

        Check(!SnapshotPrecheck(200, 100, false, false, snapDir, MINE, s8),
            "budget due but another owner -> false");
        CheckEq(readsBefore + 1, Stat(s8, "ReadyReads"), "budget due performed exactly one ready read");

        WriteReady(snapDir, "{\"pid\":" + MINE + "}");
        Check(SnapshotPrecheck(200, 100, false, false, snapDir, MINE, s8),
            "budget due + this owner -> true (idle budget 60)");
        Check(SnapshotPrecheck(140, 110, false, true, snapDir, MINE, s8),
            "in-game budget is 30 ticks");
        Check(!SnapshotPrecheck(139, 110, false, true, snapDir, MINE, s8),
            "29 ticks in game is still a skip");
        Check(SnapshotPrecheck(0, 999, true, false, snapDir, MINE, s8),
            "force bypasses the budget but still verifies the owner");
        WriteReady(snapDir, "{\"pid\":" + OTHER + "}");
        Check(!SnapshotPrecheck(0, 999, true, false, snapDir, MINE, s8),
            "force with another owner is still refused");

        // ------------- 9. frequency + wall time of the low-level operations -------------
        Console.WriteLine("=== low-level counts and timing (NOT a Unity FPS claim) ===");
        string benchDir = TempDir();
        string benchCmd = Path.Combine(benchDir, "cmd.jsonl");
        WriteReady(benchDir, "{\"pid\":" + MINE + "}");
        File.WriteAllText(benchCmd, "", new UTF8Encoding(false));

        // (a) low-level components, measured separately, best of several alternating rounds so OS /
        // cache / AV noise cannot decide the result. Nothing here is a Unity FPS claim.
        const int PER = 5000, ROUNDS = 4;
        var ownReadyFast = (Func<string, int, bool>)Delegate.CreateDelegate(
            typeof(Func<string, int, bool>), Find("TransportOwnsReadyFileIn",
                new[] { typeof(string), typeof(int) }));
        var readyMatch = (Func<string, int, bool>)Delegate.CreateDelegate(
            typeof(Func<string, int, bool>), mReadyPidMatches);
        var readReadyFast = (Func<string, string>)Delegate.CreateDelegate(
            typeof(Func<string, string>), Find("TransportReadReadyText", new[] { typeof(string) }));
        var pidGetter = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>),
            pOwnPid.GetGetMethod(true));
        string benchReady = Path.Combine(benchDir, "bridge-ready.json");
        int realPid = Process.GetCurrentProcess().Id;
        int cachedPid = OwnPid();

        double pidOldUs = double.MaxValue, pidNewUs = double.MaxValue;
        double readOldUs = double.MaxValue, readNewUs = double.MaxValue;
        double ownOldUs = double.MaxValue, ownNewUs = double.MaxValue;
        int oldOk = 0, newOk = 0, pidOldOk = 0, pidNewOk = 0, readOldOk = 0, readNewOk = 0;
        var sw = Stopwatch.StartNew();
        for (int round = 0; round < ROUNDS; round++)
        {
            // ---- old pid probe: a fresh Process object per check
            sw.Restart();
            for (int i = 0; i < PER; i++) { if (Process.GetCurrentProcess().Id > 0) pidOldOk++; }
            sw.Stop();
            pidOldUs = Math.Min(pidOldUs, sw.Elapsed.TotalMilliseconds * 1000.0 / PER);
            // ---- new pid read: the cached constant
            sw.Restart();
            for (int i = 0; i < PER; i++) { if (pidGetter() > 0) pidNewOk++; }
            sw.Stop();
            pidNewUs = Math.Min(pidNewUs, sw.Elapsed.TotalMilliseconds * 1000.0 / PER);

            // ---- old ready read: File.ReadAllText on the same file
            WriteReady(benchDir, "{\"pid\":" + realPid + "}");
            sw.Restart();
            for (int i = 0; i < PER; i++) { string raw = File.ReadAllText(benchReady); if (raw != null) readOldOk++; }
            sw.Stop();
            readOldUs = Math.Min(readOldUs, sw.Elapsed.TotalMilliseconds * 1000.0 / PER);
            // ---- new ready read: the transport's own helper
            sw.Restart();
            for (int i = 0; i < PER; i++) { string raw = readReadyFast(benchDir); if (raw != null) readNewOk++; }
            sw.Stop();
            readNewUs = Math.Min(readNewUs, sw.Elapsed.TotalMilliseconds * 1000.0 / PER);

            // ---- full ownership check, old shape (fresh Process + read + parse)
            WriteReady(benchDir, "{\"pid\":" + realPid + "}");
            sw.Restart();
            for (int i = 0; i < PER; i++)
            {
                int pid = Process.GetCurrentProcess().Id;
                string raw = File.ReadAllText(benchReady);
                if (raw != null && readyMatch(raw, pid)) oldOk++;
            }
            sw.Stop();
            ownOldUs = Math.Min(ownOldUs, sw.Elapsed.TotalMilliseconds * 1000.0 / PER);
            // ---- full ownership check, new shape (cached pid + read + parse)
            WriteReady(benchDir, "{\"pid\":" + MINE + "}");
            sw.Restart();
            for (int i = 0; i < PER; i++) { if (ownReadyFast(benchDir, cachedPid)) newOk++; }
            sw.Stop();
            ownNewUs = Math.Min(ownNewUs, sw.Elapsed.TotalMilliseconds * 1000.0 / PER);
        }
        CheckEq(ROUNDS * PER, oldOk, "old-shape ownership check accepts every iteration");
        CheckEq(ROUNDS * PER, newOk, "new-shape ownership check accepts every iteration");
        CheckEq(ROUNDS * PER, pidOldOk, "old pid probe returns a pid every iteration");
        CheckEq(ROUNDS * PER, pidNewOk, "cached pid returns a pid every iteration");
        CheckEq(ROUNDS * PER, readOldOk, "File.ReadAllText returns text every iteration");
        CheckEq(ROUNDS * PER, readNewOk, "transport ready read returns text every iteration");
        CheckEq(1, (int)pProbeCalls.GetValue(null, null),
            "the new shape resolved the process id once, not " + (ROUNDS * PER) + " times");
        Console.WriteLine("      pid probe:  old " + pidOldUs.ToString("F3") + "us/call  new "
            + pidNewUs.ToString("F3") + "us/call  (best of " + ROUNDS + "x" + PER + ")");
        Console.WriteLine("      ready read: old " + readOldUs.ToString("F3") + "us/call  new "
            + readNewUs.ToString("F3") + "us/call");
        Console.WriteLine("      ownership:  old " + ownOldUs.ToString("F3") + "us/call  new "
            + ownNewUs.ToString("F3") + "us/call");
        var sOld = NewStats();
        for (int i = 0; i < 10; i++) { OwnReady(benchDir, cachedPid, sOld); }
        CheckEq(10, Stat(sOld, "ReadyReads"),
            "each counted ownership check performed exactly one ready-file read");

        // (b) 600 idle frames, two measurements that do NOT fake a recent sample:
        //     * the old order checked the ready file in BOTH ProcessInbox and MaybeSnapshot on every
        //       frame (that is the pre-071 cost this change removes);
        //     * the new order polls the command file and prechecks the snapshot with a PERSISTENT
        //       lastProbeTick that only advances when a precheck actually succeeds (exactly like
        //       MaybeSnapshot does). With 600 frames from tick 0 that is the idle 60-tick budget
        //       firing at 60,120,...,540 = 9 times, and the in-game 30-tick budget 19 times - NOT
        //       zero. The unchanged command file still costs zero ready reads.
        //     Scope note: this covers ONLY the two prechecks. A real Emit and each request dispatch
        //     still perform their own real-time ownership checks, which this benchmark does not model.
        const int FRAMES = 600;
        var sFrameOld = NewStats();
        sw = Stopwatch.StartNew();
        int oldReads = 0;
        for (int i = 0; i < FRAMES; i++)
        {
            if (OwnReady(benchDir, MINE, sFrameOld)) oldReads++;     // ProcessInbox (before the budget)
            if (OwnReady(benchDir, MINE, sFrameOld)) oldReads++;     // MaybeSnapshot (before the budget)
        }
        sw.Stop();
        long frameOldMs = sw.ElapsedMilliseconds;

        var sFrameNew = NewStats();
        var fstate = new PollState();
        long lastProbeIdle = 0, lastProbeGame = 0;
        int idleProbes = 0, gameProbes = 0;
        sw = Stopwatch.StartNew();
        for (int i = 0; i < FRAMES; i++)
        {
            Poll(i, benchCmd, benchDir, MINE, sFrameNew, fstate);                       // inbox
            if (SnapshotPrecheck(i, lastProbeIdle, false, false, benchDir, MINE, sFrameNew))
            { lastProbeIdle = i; idleProbes++; }                                        // idle snapshot
            if (SnapshotPrecheck(i, lastProbeGame, false, true, benchDir, MINE, sFrameNew))
            { lastProbeGame = i; gameProbes++; }                                        // in-game snapshot
        }
        sw.Stop();
        long frameNewMs = sw.ElapsedMilliseconds;
        CheckEq(FRAMES * 2, oldReads,
            "old ordering performed 2 ready reads per idle frame (" + FRAMES + " frames)");
        CheckEq(FRAMES * 2, Stat(sFrameOld, "ReadyReads"),
            "the old-order read count is confirmed by the counter");
        CheckEq(9, idleProbes, "the idle 60-tick budget fired 9 times across " + FRAMES + " frames");
        CheckEq(19, gameProbes, "the in-game 30-tick budget fired 19 times across " + FRAMES + " frames");
        CheckEq(28, Stat(sFrameNew, "ReadyReads"),
            "the new order performed exactly one ready read per budget-fired precheck (28)");
        Check(Stat(sFrameNew, "PollBudgetSkips") > 0, "idle frames were skipped by the tick budget",
            null);

        // the inbox alone (no prechecks): a due poll of an unchanged file still costs zero ready reads
        var sInboxOnly = NewStats();
        var istate = new PollState();
        for (int i = 0; i < FRAMES; i++) { Poll(i, benchCmd, benchDir, MINE, sInboxOnly, istate); }
        CheckEq(0, Stat(sInboxOnly, "ReadyReads"),
            FRAMES + " inbox polls of an unchanged command file performed zero ready reads");
        Check(Stat(sInboxOnly, "CmdUnchanged") > 0,
            "those polls were detected as unchanged by file length alone (" 
            + Stat(sInboxOnly, "CmdUnchanged") + ")", null);
        Console.WriteLine("      " + FRAMES + " idle frames: old=" + frameOldMs + "ms/"
            + Stat(sFrameOld, "ReadyReads") + " ready reads, new=" + frameNewMs + "ms/"
            + Stat(sFrameNew, "ReadyReads") + " ready reads, "
            + Stat(sFrameNew, "PollBudgetSkips") + " budget skips");
        Console.WriteLine("      per idle frame: old "
            + (frameOldMs * 1000.0 / FRAMES).ToString("F2") + "us, new "
            + (frameNewMs * 1000.0 / FRAMES).ToString("F2") + "us (file I/O only, not FPS)");

        Console.WriteLine("=".PadRight(70, '='));
        return failed == 0 ? 0 : 1;
    }
}
