// Transport hot-path: identity caching, budget-first ready checks and low-level I/O counters.
//
// What this file owns (codex-dsh-071):
//   * the process id is resolved ONCE per process instead of allocating a Process object inside
//     every ownership check;
//   * the ready file is read through one small helper that counts real reads, so "no I/O while the
//     tick budget is not due" is measurable rather than claimed;
//   * the command-file poll (budget -> file length -> ready owner -> read -> yield lines) lives here
//     so it can be exercised off-game with real files;
//   * the snapshot precheck (budget first, ready owner second) lives here for the same reason.
//
// Safety rules kept (unchanged semantics):
//   * every request is still checked against the CURRENT ready owner and its bridgePid immediately
//     before it is dispatched;
//   * every Emit still re-checks the ready owner in real time;
//   * a non-owner never consumes command bytes (its read offset is not advanced), so nothing is
//     swallowed;
//   * an unreadable/failed read fails CLOSED: no ownership, no consumption, no output;
//   * no value or instance is cached - only the process id and (in BridgeReflectionRuntime) the
//     reflection metadata.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        // Ready files are ~120 bytes; this window avoids a StreamReader for the common case.
        internal const int ReadyProbeMaxBytes = 512;
        internal const int CommandReadMaxBytes = 256 * 1024;
        internal const int InboxDispatchMaxLines = 4;
        internal const int InboxDispatchBudgetMs = 4;
        internal const int InboxPollEveryTicks = 3;          // ~20 command-file polls/s at 60 fps
        internal const int SnapshotBudgetTicksInGame = 30;
        internal const int SnapshotBudgetTicksIdle = 60;

        // ------------------------------------------------------------ cached process identity
        // A process id cannot change during the life of the process, so it is resolved once. A probe
        // that THROWS is deliberately not cached: ownership keeps failing closed and the probe is
        // retried, which is what the transport tests assert.
        private static readonly object OwnPidLock = new object();
        private static int _ownPidCache;                      // 0 = not resolved yet
        private static Func<int> _ownPidProbe = TransportDefaultOwnPidProbe;
        private static int _ownPidProbeCalls;
        private static int _ownPidProbeFailures;

        private static int TransportDefaultOwnPidProbe()
        {
            using (var process = System.Diagnostics.Process.GetCurrentProcess()) { return process.Id; }
        }

        internal static int OwnPid
        {
            get
            {
                int cached = _ownPidCache;
                if (cached > 0) { return cached; }
                lock (OwnPidLock)
                {
                    if (_ownPidCache > 0) { return _ownPidCache; }
                    int pid = 0;
                    try
                    {
                        Interlocked.Increment(ref _ownPidProbeCalls);
                        pid = _ownPidProbe();
                    }
                    catch (Exception) { Interlocked.Increment(ref _ownPidProbeFailures); pid = 0; }
                    if (pid > 0) { _ownPidCache = pid; }
                    return pid > 0 ? pid : 0;
                }
            }
        }

        internal static int OwnPidProbeCalls { get { return _ownPidProbeCalls; } }
        internal static int OwnPidProbeFailures { get { return _ownPidProbeFailures; } }

        /// <summary>Test seam: replace the probe and drop the cached value. Not used in production.</summary>
        internal static void TransportResetOwnPidForTests(Func<int> probe)
        {
            lock (OwnPidLock)
            {
                _ownPidProbe = probe ?? (Func<int>)TransportDefaultOwnPidProbe;
                _ownPidCache = 0;
                _ownPidProbeCalls = 0;
                _ownPidProbeFailures = 0;
            }
        }

        // ------------------------------------------------------------ low-level I/O counters
        // Cheap, allocation-free, never logged per call: they exist so a benchmark can show how many
        // real file operations a frame/poll actually performed. Counters never change behaviour.
        internal sealed class TransportIoStats
        {
            internal static readonly TransportIoStats Shared = new TransportIoStats();

            private int _pollAttempts, _pollBudgetSkips, _cmdStats, _cmdUnchanged, _cmdReads, _cmdFailures, _notOwner;
            private long _cmdBytes;
            private int _readyReads, _readyFailures, _ownershipChecks, _linesYielded;
            private int _snapshotPrechecks, _snapshotBudgetSkips, _truncationResets;
            private int _dispatchBatches, _dispatchBudgetYields, _maxDispatchBatchLines;
            private double _dispatchMs, _maxDispatchBatchMs;

            internal int PollAttempts { get { return _pollAttempts; } }
            internal int PollBudgetSkips { get { return _pollBudgetSkips; } }
            internal int CmdStats { get { return _cmdStats; } }
            internal int CmdUnchanged { get { return _cmdUnchanged; } }
            internal int CmdReads { get { return _cmdReads; } }
            internal int CmdFailures { get { return _cmdFailures; } }
            internal long CmdBytes { get { return _cmdBytes; } }
            internal int NotOwner { get { return _notOwner; } }
            internal int ReadyReads { get { return _readyReads; } }
            internal int ReadyFailures { get { return _readyFailures; } }
            internal int OwnershipChecks { get { return _ownershipChecks; } }
            internal int LinesYielded { get { return _linesYielded; } }
            internal int SnapshotPrechecks { get { return _snapshotPrechecks; } }
            internal int SnapshotBudgetSkips { get { return _snapshotBudgetSkips; } }
            internal int TruncationResets { get { return _truncationResets; } }

            internal void AddPollAttempt() { Interlocked.Increment(ref _pollAttempts); }
            internal void AddPollBudgetSkip() { Interlocked.Increment(ref _pollBudgetSkips); }
            internal void AddCmdStat() { Interlocked.Increment(ref _cmdStats); }
            internal void AddCmdUnchanged() { Interlocked.Increment(ref _cmdUnchanged); }
            internal void AddCmdRead(long bytes)
            {
                Interlocked.Increment(ref _cmdReads);
                Interlocked.Add(ref _cmdBytes, bytes);
            }
            internal void AddCmdFailure() { Interlocked.Increment(ref _cmdFailures); }
            internal void AddNotOwner() { Interlocked.Increment(ref _notOwner); }
            internal void AddReadyRead() { Interlocked.Increment(ref _readyReads); }
            internal void AddReadyFailure() { Interlocked.Increment(ref _readyFailures); }
            internal void AddOwnershipCheck() { Interlocked.Increment(ref _ownershipChecks); }
            internal void AddLineYielded() { Interlocked.Increment(ref _linesYielded); }
            internal void AddTruncationReset() { Interlocked.Increment(ref _truncationResets); }
            internal void AddSnapshotPrecheck() { Interlocked.Increment(ref _snapshotPrechecks); }
            internal void AddSnapshotBudgetSkip() { Interlocked.Increment(ref _snapshotBudgetSkips); }

            // Dispatch runs on the Unity main thread; these measurements include a whole request,
            // which cannot be preempted safely. They are not a promise of a four-ms frame ceiling.
            internal void AddDispatchBatch(int lines, long ticks, long frequency, bool yielded)
            {
                _dispatchBatches++;
                if (yielded) _dispatchBudgetYields++;
                _maxDispatchBatchLines = Math.Max(_maxDispatchBatchLines, lines);
                double ms = Math.Max(0L, ticks) * 1000.0 / frequency;
                _dispatchMs += ms; _maxDispatchBatchMs = Math.Max(_maxDispatchBatchMs, ms);
            }

            internal void Reset()
            {
                _pollAttempts = 0; _pollBudgetSkips = 0; _cmdStats = 0; _cmdUnchanged = 0;
                _cmdReads = 0; _cmdFailures = 0; _notOwner = 0; _cmdBytes = 0; _readyReads = 0; _readyFailures = 0;
                _ownershipChecks = 0; _linesYielded = 0; _snapshotPrechecks = 0;
                _snapshotBudgetSkips = 0; _truncationResets = 0;
                _dispatchBatches = 0; _dispatchBudgetYields = 0; _maxDispatchBatchLines = 0;
                _dispatchMs = 0; _maxDispatchBatchMs = 0;
            }

            /// <summary>One-line snapshot for evidence collection; never called from a hot path.</summary>
            internal string SnapshotJson()
            {
                return "{\"pollAttempts\":" + _pollAttempts
                    + ",\"pollBudgetSkips\":" + _pollBudgetSkips
                    + ",\"cmdStats\":" + _cmdStats
                    + ",\"cmdUnchanged\":" + _cmdUnchanged
                    + ",\"cmdReads\":" + _cmdReads
                    + ",\"cmdFailures\":" + _cmdFailures
                    + ",\"cmdBytes\":" + _cmdBytes.ToString(CultureInfo.InvariantCulture)
                    + ",\"readyReads\":" + _readyReads
                    + ",\"readyFailures\":" + _readyFailures
                    + ",\"ownershipChecks\":" + _ownershipChecks
                    + ",\"notOwner\":" + _notOwner
                    + ",\"linesYielded\":" + _linesYielded
                    + ",\"truncationResets\":" + _truncationResets
                    + ",\"snapshotPrechecks\":" + _snapshotPrechecks
                    + ",\"snapshotBudgetSkips\":" + _snapshotBudgetSkips
                    + ",\"dispatchBatches\":" + _dispatchBatches
                    + ",\"dispatchBudgetYields\":" + _dispatchBudgetYields
                    + ",\"maxDispatchBatchLines\":" + _maxDispatchBatchLines
                    + ",\"dispatchMs\":" + _dispatchMs.ToString("F3", CultureInfo.InvariantCulture)
                    + ",\"maxDispatchBatchMs\":" + _maxDispatchBatchMs.ToString("F3", CultureInfo.InvariantCulture) + "}";
            }
        }

        internal static string TransportIoStatsJson()
        {
            return TransportIoStats.Shared.SnapshotJson();
        }

        /// <summary>
        /// The pid written into bridge-ready.json: the cached constant, with a direct fallback so a
        /// failed probe can never publish pid 0 (startup UI phases keep this same owner).
        /// </summary>
        internal static int TransportReadyPidValue()
        {
            int pid = OwnPid;
            if (pid > 0) { return pid; }
            try
            {
                using (var process = System.Diagnostics.Process.GetCurrentProcess())
                    return process.Id;
            }
            catch (Exception) { return 0; }
        }

        // ------------------------------------------------------------ ready file (one small read)
        /// <summary>
        /// Read bridge-ready.json with ONE counted attempt, using exactly the open/read shape the
        /// transport used before this change (FileShare.ReadWrite + StreamReader). A fixed-window
        /// byte-buffer variant was tried first and measured ~2x SLOWER per call on this machine, so
        /// it was dropped: the measurable wins are the cached pid and the avoided idle-path reads.
        /// Any failure returns null - the caller must treat null as "not the owner".
        /// </summary>
        internal static string TransportReadReadyText(string workDir, TransportIoStats stats)
        {
            if (string.IsNullOrEmpty(workDir))
            {
                if (stats != null) { stats.AddReadyFailure(); }
                return null;
            }
            try
            {
                if (stats != null) { stats.AddReadyRead(); }
                using (var input = new FileStream(Path.Combine(workDir, "bridge-ready.json"),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(input, Encoding.UTF8, true))
                    return reader.ReadToEnd();
            }
            catch (Exception)
            {
                if (stats != null) { stats.AddReadyFailure(); }
                return null;
            }
        }

        /// <summary>Same read without counters.</summary>
        internal static string TransportReadReadyText(string workDir)
        {
            return TransportReadReadyText(workDir, null);
        }

        // ------------------------------------------------------------ ownership (real time)
        private bool TransportOwnsReadyFile()
        {
            return TransportOwnsReadyFileIn(WorkDir, OwnPid, TransportIoStats.Shared);
        }

        /// <summary>
        /// Real-time ownership: the ready file is read NOW and its pid must be this process. An
        /// unknown/zero pid never owns anything, and an unreadable file fails closed.
        /// </summary>
        internal static bool TransportOwnsReadyFileIn(string workDir, int ownPid, TransportIoStats stats)
        {
            if (stats != null) { stats.AddOwnershipCheck(); }
            if (ownPid <= 0) { return false; }
            string raw = TransportReadReadyText(workDir, stats);
            if (raw == null) { return false; }
            return TransportReadyPidMatches(raw, ownPid);
        }

        /// <summary>Same check without counters (used where instrumentation is not wanted).</summary>
        internal static bool TransportOwnsReadyFileIn(string workDir, int ownPid)
        {
            return TransportOwnsReadyFileIn(workDir, ownPid, null);
        }

        internal static bool TransportReadyPidMatches(string readyJson, int ownPid)
        {
            try
            {
                object pid;
                var ready = BridgeJsonCodec.Object(readyJson);
                int parsed;
                return ownPid > 0 && ready.TryGetValue("pid", out pid) && !(pid is bool)
                    && Int32.TryParse(BridgeJsonCodec.Scalar(pid), NumberStyles.None,
                        CultureInfo.InvariantCulture, out parsed) && parsed == ownPid;
            }
            catch { return false; }
        }

        // Current clients include bridgePid, so even a ready-file change between their final check
        // and append cannot send an old client's action to the new instance. Legacy hand-written
        // envelopes omit it and retain the ready-owner restriction.
        private bool TransportRequestTargetsThisInstance(string raw)
        {
            return TransportRequestTargetsThisInstanceIn(WorkDir, raw, OwnPid, TransportIoStats.Shared);
        }

        /// <summary>
        /// Full per-request identity check, unchanged in strength: the CURRENT ready owner is
        /// verified first, then the request's own bridgePid binding (legacy envelopes fall back to
        /// the ready-owner restriction).
        /// </summary>
        internal static bool TransportRequestTargetsThisInstanceIn(string workDir, string raw,
            int ownPid, TransportIoStats stats)
        {
            if (!TransportOwnsReadyFileIn(workDir, ownPid, stats)) { return false; }
            return TransportRequestPidMatches(raw, ownPid);
        }

        internal static bool TransportRequestPidMatches(string raw, int ownPid)
        {
            try
            {
                object pid;
                var request = BridgeJsonCodec.Object(raw);
                if (!request.TryGetValue("bridgePid", out pid)) return ownPid > 0;
                int parsed;
                return ownPid > 0 && !(pid is bool)
                    && Int32.TryParse(BridgeJsonCodec.Scalar(pid), NumberStyles.None,
                        CultureInfo.InvariantCulture, out parsed) && parsed == ownPid;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------ command-file poll
        /// <summary>
        /// One command-file poll. Returns the NEWLY READ bytes this process (the current ready owner)
        /// may consume, or null when there is nothing to do. Order:
        ///   1. tick budget - no I/O and no allocation at all,
        ///   2. one file-length stat; unchanged since the last poll -> null with NO ready read,
        ///   3. real-time ready-owner check,
        ///   4. only now may consumption state change: a truncated file resets offset/pending,
        ///   5. read the new bytes, advance the offset and return them.
        /// A non-owner returns null BEFORE step 4, so it can never change (or drop) another
        /// instance's consumption state, not even when the command file was truncated. Every skipped
        /// path returns null - never an empty collection - so an idle frame costs a comparison, not
        /// an allocation. `pending` is write-only here (the truncation reset lives in one place); the
        /// caller appends the returned text to its own buffer.
        /// The returned text is NOT verified per request: the caller still runs the ready-owner +
        /// bridgePid check before dispatching each request.
        /// </summary>
        internal static string TransportPollCmdText(long tick, ref long lastPollTick, ref long cmdOffset,
            ref string pending, string cmdPath, string workDir, int ownPid, TransportIoStats stats)
        {
            bool mayDispatch;
            return TransportPollCmdTextForDispatch(tick, ref lastPollTick, ref cmdOffset, ref pending,
                cmdPath, workDir, ownPid, stats, out mayDispatch);
        }

        internal static string TransportPollCmdTextForDispatch(long tick, ref long lastPollTick, ref long cmdOffset,
            ref string pending, string cmdPath, string workDir, int ownPid, TransportIoStats stats, out bool mayDispatch)
        {
            mayDispatch = false;
            if (stats != null) { stats.AddPollAttempt(); }
            if (tick - lastPollTick < InboxPollEveryTicks)
            {
                if (stats != null) { stats.AddPollBudgetSkip(); }
                return null;
            }
            lastPollTick = tick;
            if (string.IsNullOrEmpty(cmdPath)) { return null; }
            if (stats != null) { stats.AddCmdStat(); }
            long len;
            try
            {
                if (!File.Exists(cmdPath)) { return null; }
                len = new FileInfo(cmdPath).Length;
            }
            catch (Exception)
            {
                if (stats != null) { stats.AddCmdFailure(); }
                return null;
            }
            if (len == cmdOffset)
            {
                if (stats != null) { stats.AddCmdUnchanged(); }
                mayDispatch = true;               // only buffered requests need a fresh ownership check
                return null;                     // unchanged command file: no ready read, no state change
            }
            if (!TransportOwnsReadyFileIn(workDir, ownPid, stats))
            {
                if (stats != null) { stats.AddNotOwner(); }
                return null;                     // non-owner: offset/pending are left untouched
            }
            if (len < cmdOffset)                 // truncated while I own it: restart from byte 0
            {
                cmdOffset = 0;
                pending = "";
                if (stats != null) { stats.AddTruncationReset(); }
                if (len == 0) { return null; }
            }
            // A bounded dispatcher may still hold complete lines. Apply backpressure to file
            // reads, but only AFTER the owner/truncation checks above so reset semantics survive.
            if (pending.IndexOf('\n') >= 0) { mayDispatch = true; return null; }
            string text;
            int bytesRead, physicalBytesRead;
            try
            {
                using (var fs = new FileStream(cmdPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(cmdOffset, SeekOrigin.Begin);
                    long want = Math.Min(len - cmdOffset, (long)CommandReadMaxBytes);
                    var buf = new byte[(int)want];
                    int read = 0;
                    int r;
                    while (read < buf.Length && (r = fs.Read(buf, read, buf.Length - read)) > 0)
                    { read += r; }
                    // A writer or our read budget may split a multibyte character.
                    // Leave only its incomplete bytes in the file for the next poll;
                    // decoding them separately would permanently corrupt the command.
                    bytesRead = TransportUtf8CompletePrefix(buf, read);
                    physicalBytesRead = read;
                    text = Encoding.UTF8.GetString(buf, 0, bytesRead);
                }
                // A concurrent truncate can yield fewer bytes than the earlier length stat.
                // Advance only past bytes actually consumed; the diagnostic counts the delta.
                cmdOffset += bytesRead;
                if (stats != null) { stats.AddCmdRead(physicalBytesRead); }
            }
            catch (Exception)
            {
                if (stats != null) { stats.AddCmdFailure(); }
                return null;
            }
            mayDispatch = true;
            return text;
        }

        internal static int TransportDrainPending(ref string pending, string workDir, int ownPid,
            TransportIoStats stats, Action<string> dispatch, Func<long> timestamp, long frequency)
        {
            if (pending.IndexOf('\n') < 0) return 0;
            long started = timestamp();
            long budget = Math.Max(1L, frequency * InboxDispatchBudgetMs / 1000L);
            int consumed = 0; bool yielded = false;
            try
            {
                int newline;
                while ((newline = pending.IndexOf('\n')) >= 0)
                {
                    // Always permit one indivisible request to finish. Check again between requests.
                    if (consumed >= InboxDispatchMaxLines || (consumed > 0 && timestamp() - started >= budget))
                    { yielded = true; break; }
                    if (!TransportOwnsReadyFileIn(workDir, ownPid, stats)) break;
                    string line = pending.Substring(0, newline).Trim();
                    pending = pending.Substring(newline + 1);
                    consumed++;
                    if (line.Length > 0) dispatch(line);
                }
                return consumed;
            }
            finally
            {
                if (stats != null) stats.AddDispatchBatch(consumed, timestamp() - started, frequency, yielded);
            }
        }

        private static int TransportUtf8CompletePrefix(byte[] bytes, int count)
        {
            if (count == 0) return 0;
            int start = count - 1;
            while (start > 0 && count - start < 4 && (bytes[start] & 0xc0) == 0x80) start--;
            byte lead = bytes[start];
            int needed = lead >= 0xc2 && lead <= 0xdf ? 2
                : lead >= 0xe0 && lead <= 0xef ? 3
                : lead >= 0xf0 && lead <= 0xf4 ? 4 : 1;
            return count - start < needed ? start : count;
        }

        // ------------------------------------------------------------ snapshot precheck
        /// <summary>
        /// MaybeSnapshot's gate in the cheap order: the existing tick budget first (no I/O at all),
        /// and only when it is due the real-time ready-owner check. Semantics are unchanged - a
        /// caller that gets true is the ready owner and may set its probe tick; every other case
        /// returns false without touching the file.
        /// </summary>
        internal static bool TransportSnapshotPrecheck(long tick, long lastProbeTick, bool force,
            bool gameActive, string workDir, int ownPid, TransportIoStats stats)
        {
            if (stats != null) { stats.AddSnapshotPrecheck(); }
            if (!force)
            {
                long since = tick - lastProbeTick;
                long every = gameActive ? SnapshotBudgetTicksInGame : SnapshotBudgetTicksIdle;
                if (since < every)
                {
                    if (stats != null) { stats.AddSnapshotBudgetSkip(); }
                    return false;
                }
            }
            return TransportOwnsReadyFileIn(workDir, ownPid, stats);
        }
    }
}
