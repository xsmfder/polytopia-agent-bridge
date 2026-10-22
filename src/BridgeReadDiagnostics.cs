// Request-scoped coarse read diagnostics. No native values or exception messages survive.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private sealed class NativeReadDiagnostics
        {
            internal BridgeMod Owner;
            internal string Command, InitialStream;
            internal long InitialTicket, Total, Dropped;
            internal long? RequestedTicket;
            internal Dictionary<string, long> Sources;
        }
        [ThreadStatic] private static NativeReadDiagnostics _nativeReadDiagnostics;
        private const int MaxReadFailureSources = 16;
        private static long _logWriteFailures, _lastLogFailureNotice;
        internal static long LogWriteFailureCount { get { return Interlocked.Read(ref _logWriteFailures); } }

        private static string ReadTraceToken(string text, int limit)
        {
            if (string.IsNullOrEmpty(text)) return "?";
            var result = new StringBuilder(Math.Min(text.Length, limit));
            for (int i = 0; i < text.Length && i < limit; i++)
            {
                char c = text[i];
                result.Append(char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '+' || c == '-' || c == '`' ? c : '_');
            }
            return result.ToString();
        }

        internal static void RecordNativeReadFailure(Type type, string member, Exception error)
        {
            if (_nativeReadDiagnostics == null) return;
            Exception cause = error is TargetInvocationException && error.InnerException != null ? error.InnerException : error;
            RecordNativeReadFailureKind(type, member, cause == null ? "unavailable" : cause.GetType().Name);
        }

        private static void RecordNativeReadFailureKind(Type type, string member, string kind)
        {
            var scope = _nativeReadDiagnostics;
            if (scope == null) return; // Expected cold/detached background state must not flood logs.
            string key = ReadTraceToken(type == null ? null : type.FullName, 64) + "."
                + ReadTraceToken(member, 48) + ":" + ReadTraceToken(kind, 32);
            if (scope.Sources == null) scope.Sources = new Dictionary<string, long>(StringComparer.Ordinal);
            if (scope.Total < long.MaxValue) scope.Total++;
            long count;
            if (scope.Sources.TryGetValue(key, out count))
            { if (count < long.MaxValue) scope.Sources[key] = count + 1; }
            else if (scope.Sources.Count < MaxReadFailureSources) scope.Sources.Add(key, 1);
            else if (scope.Dropped < long.MaxValue) scope.Dropped++;
        }

        private void BeginNativeReadDiagnostics(string command)
        {
            _nativeReadDiagnostics = new NativeReadDiagnostics { Owner = this, Command = ReadTraceToken(command, 64),
                InitialStream = _combatStream, InitialTicket = _combatTicketId };
        }

        private void NoteReadDiagnosticTicket(string command, string raw)
        {
            var scope = _nativeReadDiagnostics;
            if (scope == null || scope.Owner != this || command != "settlement") return;
            long ticket;
            if (long.TryParse(Json.GetString(raw, "ticket"), NumberStyles.None, CultureInfo.InvariantCulture, out ticket)
                && ticket > 0) scope.RequestedTicket = ticket;
        }

        private void EndNativeReadDiagnostics()
        {
            var scope = _nativeReadDiagnostics;
            if (scope == null || scope.Owner != this) return;
            _nativeReadDiagnostics = null; // Logging cannot recursively add to the request being finalized.
            if (scope.Total == 0 || _activeRequestId <= 0) return;
            var sources = new List<string>();
            foreach (var pair in scope.Sources) sources.Add(pair.Key + "=" + pair.Value.ToString(CultureInfo.InvariantCulture));
            sources.Sort(StringComparer.Ordinal);
            Log("read failure #" + _activeRequestId + " cmd=" + scope.Command + RequestLogContext(_activeRequestId, scope)
                + " count=" + scope.Total.ToString(CultureInfo.InvariantCulture)
                + " dropped=" + scope.Dropped.ToString(CultureInfo.InvariantCulture) + " sources=" + string.Join(",", sources.ToArray()));
        }

        private long? NewRequestLogTicket(long requestId, NativeReadDiagnostics scope)
        {
            if (scope == null || scope.Owner != this || requestId <= 0 || requestId != _activeRequestId) return null;
            long? matching = null;
            foreach (var ticket in _combatTickets.Values)
                if (ticket != null && ticket.Id > 0 && ticket.RequestId == requestId && ticket.Stream == _combatStream
                    && (ticket.Stream != scope.InitialStream || ticket.Id > scope.InitialTicket))
                { if (matching.HasValue) { matching = null; break; } matching = ticket.Id; }
            return matching;
        }

        private string RequestLogContext(long requestId, NativeReadDiagnostics scope = null)
        {
            scope = scope ?? _nativeReadDiagnostics;
            bool matchingScope = scope != null && scope.Owner == this && requestId > 0 && requestId == _activeRequestId;
            long? matching = NewRequestLogTicket(requestId, scope);
            var frame = _combatFrame;
            return " ep=" + ReadTraceToken(frame == null ? null : frame.Episode, 64)
                + " player=" + ReadTraceToken(frame == null ? null : frame.Me, 16)
                + " stream=" + ReadTraceToken(_combatStream, 64)
                + " ticket=" + (matching.HasValue ? matching.Value.ToString(CultureInfo.InvariantCulture) : "?")
                + " requestedTicket=" + (matchingScope && scope.RequestedTicket.HasValue ? scope.RequestedTicket.Value.ToString(CultureInfo.InvariantCulture) : "?")
                + " context=last_frame";
        }

        private void LogReplyDeliveryFailure(long requestId, string stage, Exception error)
        {
            Exception cause = error is TargetInvocationException && error.InnerException != null ? error.InnerException : error;
            Log("reply delivery failed #" + requestId.ToString(CultureInfo.InvariantCulture) + RequestLogContext(requestId)
                + " stage=" + ReadTraceToken(stage, 32) + " type=" + ReadTraceToken(cause == null ? "unavailable" : cause.GetType().Name, 32));
        }

        private static void RecordLogWriteFailure(Exception error)
        {
            Interlocked.Increment(ref _logWriteFailures);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long previous = Interlocked.Read(ref _lastLogFailureNotice);
            if (previous != 0 && now - previous < System.Diagnostics.Stopwatch.Frequency * 30L) return;
            if (Interlocked.CompareExchange(ref _lastLogFailureNotice, now, previous) != previous) return;
            try { Console.Error.WriteLine("bridge log write failed: " + error.GetType().Name
                + "; logWriteFailures=" + LogWriteFailureCount.ToString(CultureInfo.InvariantCulture)); }
            catch { /* The counter remains available if both sinks are unavailable. */ }
        }
    }
}
