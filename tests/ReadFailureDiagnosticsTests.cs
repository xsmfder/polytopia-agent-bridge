using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using PolytopiaAgent;

public static class DiagnosticNativeRules
{
    public static int Calls;
    public static int Read() { Calls++; throw new Exception("SECRET_NATIVE_RULE"); }
}

internal static class ReadFailureDiagnosticsTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int passed, failed;
    private static readonly BridgeMod Host = new BridgeMod();
    private static void Check(bool ok, string label)
    { Console.WriteLine((ok ? "PASS " : "FAIL ") + label); if (ok) passed++; else failed++; }
    private static object Call(string name, params object[] args)
    { return typeof(BridgeMod).GetMethod(name, F).Invoke(Host, args); }
    private static void Set(string name, object value) { typeof(BridgeMod).GetField(name, F).SetValue(Host, value); }
    public sealed class Broken
    {
        public int Reads;
        public object health { get { Reads++; throw new InvalidOperationException("SECRET_NATIVE_VALUE_(99,99)"); } }
        public bool moved { get { throw new InvalidOperationException("SECRET_NATIVE_FLAG"); } }
    }
    public sealed class Known { public int health = 0; public object currentReaction = null; }
    public sealed class VoidPipelineGetter { public int Calls; public void get_currentReaction() { Calls++; } }
    public sealed class AmbiguousNative
    {
        public int Calls;
        public int get_Health() { Calls++; return 1; }
        public int get_Health<T>() { Calls++; return 2; }
    }
    public sealed class BrokenMap { public object Map { get { throw new Exception("SECRET_NATIVE_MAP"); } } }
    public sealed class BrokenSequence : IEnumerable
    {
        public IEnumerator GetEnumerator() { yield return 1; throw new Exception("SECRET_NATIVE_LIST"); }
    }
    private static string Tail(long start)
    { string s = File.Exists(BridgeMod.LogPath) ? File.ReadAllText(BridgeMod.LogPath) : ""; return s.Substring((int)start); }
    private static long Offset() { return File.Exists(BridgeMod.LogPath) ? File.ReadAllText(BridgeMod.LogPath).Length : 0; }
    private static void Begin(long id, string cmd)
    { Set("_activeRequestId", id); Call("BeginReadRequest", cmd); }
    public static int Main()
    {
        Directory.CreateDirectory(BridgeMod.WorkDir);
        Set("_combatFrame", new BridgeMod.CombatFrame { Episode = "ep-visible", Me = "2" });
        Set("_combatStream", "stream-visible");
        var tickets = (Dictionary<long, BridgeMod.CombatTicket>)typeof(BridgeMod).GetField("_combatTickets", F).GetValue(Host);
        tickets[9] = new BridgeMod.CombatTicket { Id = 9, RequestId = 55, Stream = "stream-visible" };
        long start = Offset(); Begin(55, "world");
        var broken = new Broken(); object value;
        for (int i = 0; i < 100; i++) BridgeMod.TryReadNativeMember(broken, "health", out value);
        Check(broken.Reads == 100, "diagnostic collection never retries getters or caches their failure values");
        Call("EndReadRequest"); string trace = Tail(start);
        Check(trace.Contains("read failure #55") && trace.Contains("cmd=world") && trace.Contains("ep=ep-visible")
            && trace.Contains("player=2") && trace.Contains("stream=stream-visible") && trace.Contains("ticket=9"),
            "actual request finalization logs request, frame, stream and unique native ticket");
        Check(trace.Contains("health") && trace.Contains("InvalidOperationException") && trace.Contains("100"),
            "repeated native errors are summarized with a source and actual occurrence count");
        Check(!trace.Contains("SECRET_NATIVE") && !trace.Contains("(99,99)"), "exception payload is not copied to the request trace");
        Check(trace.Split(new[] { "read failure #55" }, StringSplitOptions.None).Length == 2,
            "one request writes one read-failure summary");
        start = Offset(); Call("EndReadRequest");
        BridgeMod.TryReadNativeMember(broken, "health", out value);
        Check(Tail(start) == "", "ended requests and background reads do not emit stale or per-frame diagnostics");
        Begin(56, "world");
        Check(BridgeMod.TryReadNativeMember(new Known(), "health", out value) && Equals(value, 0), "native zero remains known");
        object[] readNull = { new Known(), "currentReaction", null };
        Check((bool)Call("CRTryPipelineMember", readNull) && readNull[2] == null, "known null pipeline state is not a failure");
        BridgeMod.TryReadNativeMember(new Known(), "absent_optional", out value);
        Call("EndReadRequest");
        Check(Tail(start) == "", "healthy values and absent optional metadata do not create error logs");
        start = Offset(); Begin(57, "world");
        BridgeMod.ReadNativeBooleanMember(broken, "moved");
        Call("EndReadRequest");
        Check(Tail(start).Contains("read failure #57") && Tail(start).Contains("moved"), "Boolean getter errors use the same request diagnostics");
        var voidGetter = new VoidPipelineGetter(); object[] unsupported = { voidGetter, "currentReaction", null };
        Check(!(bool)Call("CRTryPipelineMember", unsupported) && voidGetter.Calls == 0,
            "void pipeline getter cannot be invoked as evidence of a known null reaction");
        var recorder = typeof(BridgeMod).GetMethod("RecordNativeReadFailure", F);
        Check(recorder != null, "shared native error recorder is available to specialized readers");
        if (recorder != null)
        {
            start = Offset(); Begin(58, "world");
            for (int i = 0; i < 100; i++) recorder.Invoke(null,
                new object[] { typeof(Broken), "source_" + i, new Exception("SECRET_NATIVE_OVERFLOW") });
            Call("EndReadRequest"); trace = Tail(start);
            Check(trace.Contains("dropped=84") && !trace.Contains("source_99") && trace.Length < 4096,
                "read failure sources and output stay bounded when many distinct sources fail");
            Check(!trace.Contains("SECRET_NATIVE"), "bounded diagnostics never retain exception payloads");
        }
        start = Offset(); Begin(59, "world");
        var thread = new Thread(() => { object ignored; BridgeMod.TryReadNativeMember(new Broken(), "health", out ignored); });
        thread.Start(); thread.Join(); Call("EndReadRequest");
        Check(Tail(start) == "", "another thread's background read does not enter the active request");
        start = Offset(); Begin(60, "world");
        Check(Call("Val", typeof(BrokenMap).GetProperty("Map"), new BrokenMap()) == null,
            "cached property reader preserves an actual getter failure as unknown");
        Check(BridgeMod.ReadNativeList(new BrokenSequence()) == null,
            "partial enumeration failure cannot become a known partial or empty collection");
        Call("EndReadRequest"); trace = Tail(start);
        Check(trace.Contains("Map") && trace.Contains("collection") && !trace.Contains("SECRET_NATIVE"),
            "cached property and collection failures share the bounded private trace");
        start = Offset(); Set("_combatTicketId", 9L); Begin(55, "world");
        BridgeMod.TryReadNativeMember(broken, "health", out value); Call("EndReadRequest");
        Check(Tail(start).Contains("ticket=?"), "an old ticket with a reused request ID is not attributed to a new read failure");
        start = Offset(); Begin(61, "settlement");
        var note = typeof(BridgeMod).GetMethod("NoteReadDiagnosticTicket", F);
        if (note != null) note.Invoke(Host, new object[] { "settlement", "{\"ticket\":9}" });
        BridgeMod.TryReadNativeMember(broken, "health", out value); Call("EndReadRequest"); trace = Tail(start);
        Check(note != null && trace.Contains("ticket=?") && trace.Contains("requestedTicket=9"),
            "a queried original ticket is identified as requested without fabricating a new execution ticket");
        start = Offset(); Begin(62, "unit_properties");
        Check(Call("URNative", "DiagnosticNativeRules", "Read", new string[0], new object[0]) == null
            && DiagnosticNativeRules.Calls == 1, "native rule exception preserves unknown without retrying the method");
        Call("EndReadRequest"); trace = Tail(start);
        Check(trace.Contains("DiagnosticNativeRules.Read") && trace.Contains("#62") && !trace.Contains("SECRET_NATIVE"),
            "unit rule failures identify the native source without the exception payload");
        var ambiguous = new AmbiguousNative(); int afterCold = 0;
        for (int id = 65; id <= 66; id++)
        {
            start = Offset(); Begin(id, "world");
            Check(!BridgeMod.TryReadNativeMember(ambiguous, "health", out value) && ambiguous.Calls == 0,
                "ambiguous native metadata never invokes either getter at request " + id);
            Call("EndReadRequest"); trace = Tail(start);
            Check(trace.Contains("read failure #" + id) && trace.Contains("AmbiguousMatchException"),
                "cold and warm metadata failures retain their own request diagnostics: " + id);
            if (id == 65) afterCold = BridgeMod.ReflectionPlanBuilds;
        }
        Check(BridgeMod.ReflectionPlanBuilds == afterCold, "warm ambiguity is logged without rebuilding metadata");
        Set("_activeRequestId", 0);
        // The executable's candidate directory owns this log; restore any previous file.
        string log = Path.GetFullPath(BridgeMod.LogPath), root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        if (!log.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new Exception("test log escaped candidate directory");
        string backup = log + "." + Guid.NewGuid().ToString("N") + ".backup";
        bool hadLog = File.Exists(log); if (hadLog) File.Move(log, backup);
        var previousError = Console.Error; var errors = new StringWriter(); Console.SetError(errors);
        try
        {
            Directory.CreateDirectory(log);
            for (int i = 0; i < 100; i++) BridgeMod.Log("SECRET_NATIVE_LOG_PAYLOAD");
            var counter = typeof(BridgeMod).GetProperty("LogWriteFailureCount", F);
            Check(counter != null && (long)counter.GetValue(null, null) >= 100, "failed primary log writes increment a visible counter");
            string output = errors.ToString();
            Check(output.Contains("bridge log write failed") && !output.Contains("SECRET_NATIVE"),
                "primary log failure reaches a coarse secondary sink without native payload");
            Check(output.Split(new[] { "bridge log write failed" }, StringSplitOptions.None).Length == 2,
                "repeated primary log failures do not flood the secondary sink");
            Begin(63, "world"); BridgeMod.TryReadNativeMember(broken, "health", out value); Call("EndReadRequest");
            Check(!(bool)typeof(BridgeMod).GetField("_readRequestScope", F).GetValue(Host),
                "request cache scope is cleared even when its diagnostic log cannot be written");
            long beforeHealthy = counter == null ? -1 : (long)counter.GetValue(null, null);
            Begin(64, "world"); BridgeMod.TryReadNativeMember(new Known(), "health", out value); Call("EndReadRequest");
            Check(counter != null && (long)counter.GetValue(null, null) == beforeHealthy,
                "next healthy request does not inherit failed summary data or attempt another log write");
        }
        finally
        {
            Console.SetError(previousError); Directory.Delete(log);
            if (hadLog) File.Move(backup, log);
        }
        var counters = BridgeJsonCodec.Object(BridgeMod.PerformanceCountersJson());
        Check(counters.ContainsKey("logWriteFailures"), "diagnostic counters expose lost primary log writes even without a game runtime");
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
