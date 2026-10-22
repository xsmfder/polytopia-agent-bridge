using System;
using System.Globalization;
using System.IO;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private static BridgeMod _performanceHost;
        // Explicit seat selection is optional for single-seat clients. Hotseat tests configure 2.
        private byte? _fixedControlledSeat;
        private bool _controlledSeatLoaded;
        private bool _readRequestScope;
        private object _requestSampleState, _requestPipelineState;
        private long _requestSampleGeneration = -1, _requestPipelineGeneration = -1;
        private long _nativeSampleGeneration;
        private CombatPipelineView _requestPipeline;
        private object _episodeMainClient;
        private string _episodeMainGameId;
        private long _perfCaptureCount, _perfCaptureTicks, _perfPipelineReads;
        private sealed class RequestTiming { internal long Samples, TotalTicks, MaxTicks, LastTicks; }
        private readonly System.Collections.Generic.Dictionary<string, RequestTiming> _requestTimings
            = new System.Collections.Generic.Dictionary<string, RequestTiming>(StringComparer.Ordinal);

        private void RecordRequestTiming(string command, long ticks)
        {
            string bucket;
            switch (command)
            {
                case "world": case "city_options": case "events": case "settlement": case "unit_actions":
                case "unit_properties": case "tribe_relations": case "call": case "ping": bucket = command; break;
                default: bucket = "other"; break; // Bounded categories, no raw arguments or arbitrary request names.
            }
            RequestTiming timing;
            if (!_requestTimings.TryGetValue(bucket, out timing))
            { timing = new RequestTiming(); _requestTimings.Add(bucket, timing); }
            ticks = Math.Max(0L, ticks); timing.Samples++; timing.TotalTicks += ticks;
            timing.LastTicks = ticks; timing.MaxTicks = Math.Max(timing.MaxTicks, ticks);
        }

        private string RequestTimingsJson()
        {
            var result = new JsonObj();
            var keys = new System.Collections.Generic.List<string>(_requestTimings.Keys); keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                var timing = _requestTimings[key];
                double scale = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                result.Raw(key, new JsonObj().Raw("samples", timing.Samples.ToString(CultureInfo.InvariantCulture))
                    .Raw("totalMs", (timing.TotalTicks * scale).ToString("F3", CultureInfo.InvariantCulture))
                    .Raw("maxMs", (timing.MaxTicks * scale).ToString("F3", CultureInfo.InvariantCulture))
                    .Raw("lastMs", (timing.LastTicks * scale).ToString("F3", CultureInfo.InvariantCulture)).Close());
            }
            return result.Close();
        }
        private long _nextCombatSampleTime, _nextInboxPollTime, _nextSnapshotTime;
        private object _hotseatBoundaryClient;
        private string _hotseatBoundaryGameId;
        private bool _hotseatBoundaryPending, _hotseatStateUpdateCompleted, _hotseatRecapStarted;

        // Frame budgets are retained, with a monotonic time limit as well. An uncapped game
        // must not turn a 60fps polling budget into hundreds of map scans or disk reads/sec.
        internal static bool PeriodicWorkDue(long now, long frequency, ref long next, int intervalMs)
        {
            if (now < next) return false;
            next = now + Math.Max(1L, frequency * intervalMs / 1000L);
            return true;
        }

        private bool PeriodicWorkDue(ref long next, int intervalMs)
        {
            return PeriodicWorkDue(System.Diagnostics.Stopwatch.GetTimestamp(),
                System.Diagnostics.Stopwatch.Frequency, ref next, intervalMs);
        }

        private bool NoActiveDisplayedState()
        {
            if (_gameStateProp == null) return false;
            try { return _gameStateProp.GetValue(null, null) == null; }
            catch (Exception error) { RecordNativeReadFailure(_gameStateProp.DeclaringType, _gameStateProp.Name, error); return false; }
        }

        internal static string PerformanceCountersJson()
        {
            var host = _performanceHost ?? _combatHookHost;
            if (host == null) return "{\"ok\":false,\"error\":\"no active runtime\",\"logWriteFailures\":" + LogWriteFailureCount
                + ",\"readPreparation\":" + ReadPreparationJson() + "}";
            return "{\"ok\":true,\"captureCount\":" + host._perfCaptureCount
                + ",\"combatRuntimeAvailable\":" + (_combatHookHost == null ? "false" : "true")
                + ",\"updateFrames\":" + host._updateFrameTimings.Json()
                + ",\"nativeExecution\":" + host._nativeExecutionTimings.Json()
                + ",\"captureMs\":" + (host._perfCaptureTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture)
                + ",\"pipelineReads\":" + host._perfPipelineReads + ",\"logWriteFailures\":" + LogWriteFailureCount
                + ",\"requestTimings\":" + host.RequestTimingsJson()
                + ",\"cityReadPhases\":" + host.CityReadPhasesJson()
                + ",\"nativeMethodPlans\":" + NativeMethodPlanStatsJson()
                + ",\"assemblyTypeCatalogs\":" + AssemblyTypeCatalogStatsJson()
                + ",\"publicMarkers\":" + (host._publicMarkerHooks == null ? "null" : host._publicMarkerHooks.DiagnosticsJson())
                + ",\"readPreparation\":" + ReadPreparationJson() + "}";
        }

        private void LoadControlledSeat()
        {
            string path = Path.Combine(WorkDir, "controlled-seat.json");
            if (!File.Exists(path)) return;
            try
            {
                object value;
                var config = BridgeJsonCodec.Object(File.ReadAllText(path));
                byte seat;
                if (!config.TryGetValue("playerId", out value) || value is bool ||
                    !Byte.TryParse(BridgeJsonCodec.Scalar(value), NumberStyles.None,
                        CultureInfo.InvariantCulture, out seat) || seat == 0 || seat == 255)
                    throw new FormatException("invalid playerId");
                _fixedControlledSeat = seat;
                Log("private observation fixed to seat " + seat);
            }
            catch
            {
                // A malformed explicit selection cannot silently revert to the other human.
                _fixedControlledSeat = 255;
                Log("controlled-seat configuration invalid; private observation paused");
            }
        }

        // Published after the one-time seat load. This is startup configuration metadata,
        // not a claim that any match or player map is ready for observation.
        internal static string StartupReadyJson(string cmd, string output, int pid, string started,
            byte? configuredSeat, bool seatLoaded, string uiPhase = "initializing")
        {
            return new JsonObj().Str("transport", "file").Str("cmd", cmd).Str("out", output)
                .Raw("pid", pid.ToString(CultureInfo.InvariantCulture)).Str("started", started)
                .Str("startupSchema", "bridge-startup-1").Bool("controlledSeatLoaded", seatLoaded)
                .Bool("startupUiReady", seatLoaded && uiPhase == "start_menu")
                .Str("startupUiPhase", uiPhase)
                .Raw("readPreparation", ReadPreparationJson())
                .Raw("configuredPlayerId", configuredSeat.HasValue
                    ? configuredSeat.Value.ToString(CultureInfo.InvariantCulture) : "null").Close();
        }

        private object MainClient()
        {
            object manager = SafeInstance();
            return MemberWithAccessor(manager, "client");
        }

        private object MainClientState()
        {
            object client = MainClient();
            return MemberWithAccessor(client, "GameState") ?? MemberWithAccessor(client, "currentGameState");
        }

        private static string MainSessionGameId(object client)
        {
            object value = MemberWithAccessor(client, "gameId") ?? MemberWithAccessor(client, "CurrentGameId");
            if (value == null) return null;
            object hasValue = MemberWithAccessor(value, "HasValue");
            if (hasValue is bool)
            {
                if (!(bool)hasValue) return null;
                value = MemberWithAccessor(value, "Value");
            }
            if (value is Guid) return ((Guid)value).ToString("D");
            if (value == null || value.GetType().FullName != "Il2CppSystem.Guid") return null;
            try
            {
                var method = value.GetType().GetMethod("ToString", Type.EmptyTypes);
                Guid parsed;
                var raw = method == null ? null : method.Invoke(value, null) as string;
                return raw != null && Guid.TryParse(raw, out parsed) ? parsed.ToString("D") : null;
            }
            catch { return null; }
        }

        private bool PrivateSamplingAllowed(object gs)
        {
            if (_hotseatBoundaryPending)
            {
                object main = MainClient(); string gameId = MainSessionGameId(main);
                if (!CRSameNative(main, _hotseatBoundaryClient)
                    || (_hotseatBoundaryGameId != null && gameId != _hotseatBoundaryGameId)) ClearHotseatBoundary();
            }
            if (!PrivateSamplingBaseAllowed(gs)) return false;
            if (_hotseatBoundaryPending)
            {
                TryFinishHotseatWithoutRecap();
                if (_hotseatBoundaryPending) return false;
            }
            // BaseAllowed already verified the fixed seat; avoid another native identity
            // lookup on every observation/hook when a seat is configured.
            if (_fixedControlledSeat.HasValue)
                EnsurePlayerRuntimeScope(EpisodeId(gs), _fixedControlledSeat.Value.ToString(CultureInfo.InvariantCulture));
            else
            {
                var identity = ResolveControlledIdentity(gs);
                if (identity.Verified)
                    EnsurePlayerRuntimeScope(EpisodeId(gs), identity.Id.ToString(CultureInfo.InvariantCulture));
            }
            return true;
        }

        private bool PrivateSamplingBaseAllowed(object gs)
        {
            if (gs == null) return false;
            if (!_fixedControlledSeat.HasValue)
                return SafeScalar(MemberWithAccessor(MainClient(), "clientType")) != "PassAndPlay";
            if (!CRSameNative(gs, MainClientState())) return false;
            object displayed;
            try { displayed = _gameStateProp == null ? null : _gameStateProp.GetValue(null, null); }
            catch { return false; }
            // GameManager.GameState can temporarily point to fakeSession. Never sample it.
            if (!CRSameNative(gs, displayed)) return false;
            // Native seek restores a historical state directly into this main client, then
            // runs GameManager.Simulate. Identity checks alone cannot distinguish it from
            // live play. IsRecap remains allowed: that is the player's visible turn recap.
            object client = MainClient();
            if (CRBool(ReadBoolPropertyOrGetter(client, "IsReady")) != true
                || CRBool(ReadBoolPropertyOrGetter(client, "IsReplay")) != false) return false;
            if (IsMainHotseatClient(client) && !HotseatOverlayInactive()) return false;
            object manager = MemberWithAccessor(client, "ActionManager");
            if (manager == null || !CRSameNative(MemberWithAccessor(manager, "GameState"), gs)
                || CRBool(ReadBoolPropertyOrGetter(manager, "IsSimulating")) != false) return false;
            var identity = ResolveControlledIdentity(gs);
            return identity.Verified && identity.Id == _fixedControlledSeat.Value;
        }

        private bool IsMainHotseatClient(object client)
        {
            return _fixedControlledSeat.HasValue && client != null && CRSameNative(client, MainClient())
                && SafeScalar(MemberWithAccessor(client, "clientType")) == "PassAndPlay";
        }

        private void BeginHotseatBoundary(object client)
        {
            if (!IsMainHotseatClient(client)) return;
            _hotseatBoundaryClient = client;
            _hotseatBoundaryGameId = MainSessionGameId(client);
            _hotseatBoundaryPending = true;
            _hotseatStateUpdateCompleted = false;
            _hotseatRecapStarted = false;
            InvalidateNativeSamples();
            _combatCaptureAvailable = false; _combatStableFrames = 0;
        }

        private void CompleteHotseatStateUpdate(object client)
        {
            if (!_hotseatBoundaryPending || !IsMainHotseatClient(client)
                || !CRSameNative(client, _hotseatBoundaryClient)) return;
            _hotseatStateUpdateCompleted = true;
            TryFinishHotseatWithoutRecap();
        }

        private bool HotseatOverlayInactive()
        {
            return HandoffPresentationInactive();
        }

        private void TryFinishHotseatWithoutRecap()
        {
            if (!_hotseatBoundaryPending || _combatSamplingDepth > 0
                || !IsMainHotseatClient(_hotseatBoundaryClient) || !HotseatOverlayInactive()) return;
            object gs = MainClientState();
            if (!PrivateSamplingBaseAllowed(gs)) return;
            var am = MemberWithAccessor(_hotseatBoundaryClient, "ActionManager");
            if (_hotseatRecapStarted && CRBool(ReadBoolPropertyOrGetter(am, "IsRecap")) == true)
            { CaptureHotseatBaseline(gs); return; }
            if (!_hotseatStateUpdateCompleted) return;
            if (CRBool(ReadBoolPropertyOrGetter(am, "IsRecap")) != false
                || CRNoArgBool(gs, "HasQueuedCommands") != false
                || CRBool(ReadBoolPropertyOrGetter(am, "IsProcessing")) != false) return;
            CaptureHotseatBaseline(gs);
        }

        private void StartHotseatVisibleRecap()
        {
            if (!_hotseatBoundaryPending || _combatSamplingDepth > 0
                || !IsMainHotseatClient(_hotseatBoundaryClient)) return;
            // Native RecapStarted does not guarantee that the handoff overlay has faded.
            // Keep only a lifecycle signal until the player's map is actually displayed.
            _hotseatRecapStarted = true;
            object gs = MainClientState();
            if (!PrivateSamplingBaseAllowed(gs)) return;
            var am = MemberWithAccessor(_hotseatBoundaryClient, "ActionManager");
            if (CRBool(ReadBoolPropertyOrGetter(am, "IsRecap")) != true) return;
            CaptureHotseatBaseline(gs);
        }

        private void CaptureHotseatBaseline(object gs)
        {
            _combatSamplingDepth++;
            try { RebaseHotseatWithoutEvents(CRCaptureCore(gs)); }
            catch { _combatCaptureAvailable = false; }
            finally { _combatSamplingDepth--; }
        }

        private void RebaseHotseatWithoutEvents(CombatFrame frame)
        {
            if (frame == null || !frame.Complete) return;
            // Start a genuinely new episode through the existing policy. A same-game
            // presentation rewind only replaces the working baseline, never its journal.
            if (_combatFrame == null || _combatFrame.Episode != frame.Episode || _combatFrame.Me != frame.Me)
                CRObserve(frame, null);
            else
            {
                string signature = frame.Signature();
                if (signature != _combatSignature) _combatVersion++;
                _combatFrame = frame; _combatSignature = signature;
            }
            _combatCaptureAvailable = true; _combatStableFrames = 0;
            _combatLastChangeTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            ClearHotseatBoundary();
            InvalidateNativeSamples();
        }

        private void ClearHotseatBoundary()
        {
            _hotseatBoundaryPending = false; _hotseatStateUpdateCompleted = false; _hotseatRecapStarted = false;
            _hotseatBoundaryClient = null; _hotseatBoundaryGameId = null;
        }

        private string PausedSeatInfo(object gs)
        {
            var identity = ResolveControlledIdentity(gs);
            return new JsonObj().Bool("ok", true).Str("schema", "game-info-1")
                .Str("status", "paused-other-seat-or-simulation")
                .Raw("controlledPlayerId", identity.Verified ? identity.Id.ToString(CultureInfo.InvariantCulture) : "null")
                .Raw("configuredPlayerId", _fixedControlledSeat.HasValue ? _fixedControlledSeat.Value.ToString(CultureInfo.InvariantCulture) : "null")
                .NullOrStr("turn", SafeScalar(Val(_currentTurnProp, gs)))
                .NullOrStr("phase", SafeScalar(Val(_currentStateProp, gs)))
                .Bool("canAct", false).Close();
        }

        internal static bool ReadRequestCanReuse(string cmd)
        {
            switch (cmd)
            {
                case "world": case "game_info": case "events": case "settlement":
                case "tribe_relations": case "unit_properties": case "unit_actions":
                case "city_options": case "threat_map": return true;
                default: return false;
            }
        }

        private bool SeatIndependentCommand(string cmd, string raw)
        {
            if (IsSetupAdminCommand(cmd)) return true; // Dedicated endpoints enforce their own menu/seat gates.
            if (cmd == "ping" || cmd == "schema" || cmd == "game_info" || cmd == "events" || cmd == "settlement" || cmd == "result"
                || cmd == "handoff_status" || cmd == "handoff_start"
                || cmd == "hotseat_games" || cmd == "hotseat_resume" || cmd == "hotseat_resume_status") return true;
            if (cmd == "setup_flow") return GameStateInstance() == null;
            // Normal single-player resume is a menu action and needs no private observation.
            // Restrict this administrative exception to the configured single-player seat,
            // no active main/displayed state, and the exact zero-argument native callback.
            if (cmd == "call" && _fixedControlledSeat == 1
                && Json.GetString(raw, "type") == "GameManager"
                && Json.GetString(raw, "method") == "ResumeSingleplayerGame"
                && string.IsNullOrEmpty(Json.GetString(raw, "args")))
                return GameStateInstance() == null && NoActiveDisplayedState();
            return cmd == "call" && Json.GetString(raw, "type") == "UnityEngine.Application"
                && Json.GetString(raw, "method") == "Quit";
        }

        private void BeginReadRequest(string cmd)
        {
            EndReadRequest();
            _readRequestScope = ReadRequestCanReuse(cmd);
            BeginNativeReadDiagnostics(cmd);
        }

        private void EndReadRequest()
        {
            try { EndNativeReadDiagnostics(); }
            finally
            {
                _readRequestScope = false;
                _requestSampleState = _requestPipelineState = null;
                _requestSampleGeneration = _requestPipelineGeneration = -1;
                _requestPipeline = null;
            }
        }

        private void InvalidateNativeSamples()
        {
            _nativeSampleGeneration++;
            _requestSampleGeneration = _requestPipelineGeneration = -1;
            _requestPipeline = null;
        }
    }
}
