// Public local-game administration, independent of tactical action tickets.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private sealed class GameOperation
        {
            internal string Id, Operation, GameId, RequestedGameId, ExpectedEpisode, Stage, Error, Path, BeforeStamp, AfterStamp;
            internal string Status = "submitted", NativeList, LoadedEpisode;
            internal long QuitAfterTick;
            internal DateTime QuitAfterTime;
            internal bool Submitted, Saved, QuitScheduled;
        }
        private readonly Dictionary<string, GameOperation> _gameOperations = new Dictionary<string, GameOperation>();
        private GameOperation _gameOperation;
        private DateTime _nextGameOperationPoll;
        private string _gameOperationReceiptDirectory;

        internal static void PersistGameOperationIn(string directory, string operationId, string json)
        {
            Guid parsed;
            if (!Guid.TryParseExact(operationId, "N", out parsed)) throw new ArgumentException("operationId");
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, operationId + ".json");
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(json);
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string GameOperationError(string code)
        {
            return new JsonObj().Str("schema", "game-lifecycle-1").Bool("ok", false).Str("status", "rejected")
                .Str("errorCode", code).Bool("callbackSubmitted", false).Bool("retryAllowed", false).Close();
        }
        internal static string SaveFileStamp(string path)
        {
            if (!File.Exists(path)) return "missing";
            var info = new FileInfo(path);
            if (info.Length == 0) return "empty";
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var hash = SHA256.Create())
                return info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + info.Length + ":"
                    + BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        private static MethodInfo GameMethod(Type type, string name, bool isStatic, params Type[] args)
        {
            return type == null ? null : type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic
                | (isStatic ? BindingFlags.Static : BindingFlags.Instance), null, args, null);
        }
        private bool GameMenuReady(object screen)
        {
            return CRTryNativeCast(screen, FindGameType("StartScreen_UI2")) != null
                || ResolveCurrentHotseatScreen(screen) != null;
        }
        private string GameSaveGate(string expectedEpisode, out object client)
        {
            client = MainClient(); object gs = MainClientState();
            if (gs == null || !PrivateSamplingAllowed(gs)) return "CONTROLLED_GAME_NOT_READY";
            var identity = ResolveControlledIdentity(gs);
            if (!identity.Verified || !_fixedControlledSeat.HasValue || identity.Id != _fixedControlledSeat.Value
                || CurrentPlayerId(gs) != identity.Id) return "CONTROLLED_TURN_REQUIRED";
            if (string.IsNullOrEmpty(expectedEpisode) || expectedEpisode != EpisodeId(gs)) return "EPISODE_CHANGED";
            string kind = SafeScalar(MemberWithAccessor(client, "clientType"));
            if (kind != "Local" && kind != "PassAndPlay") return "LOCAL_GAME_REQUIRED";
            return CRGameplayPipelineIdle(CRReadPipeline(gs)) == true ? null : "GAME_PIPELINE_NOT_IDLE";
        }
        private string GameLifecycleAction(string operation, string operationId, string gameId, string expectedEpisode, string playerId)
        {
            Guid id;
            if (!Guid.TryParseExact(operationId, "N", out id)) return GameOperationError("INVALID_OPERATION_ID");
            if (!_fixedControlledSeat.HasValue || playerId != _fixedControlledSeat.Value.ToString(CultureInfo.InvariantCulture))
                return GameOperationError("CONTROLLED_SEAT_MISMATCH");
            GameOperation old;
            if (_gameOperations.TryGetValue(operationId, out old))
            {
                if (old.Operation != operation || old.RequestedGameId != gameId || old.ExpectedEpisode != expectedEpisode)
                    return GameOperationError("OPERATION_ID_CONFLICT");
                PollGameOperation(old); return GameOperationJson(old);
            }
            if (operation != "load_singleplayer" && operation != "load_hotseat" && operation != "open_hotseat"
                && operation != "save" && operation != "return_to_menu" && operation != "quit")
                return GameOperationError("INVALID_OPERATION");
            if (_gameOperation != null && _gameOperation.Status != "confirmed" && _gameOperation.Status != "rejected")
                return GameOperationError("GAME_OPERATION_PENDING");
            if (_gameOperations.Count >= 128) return GameOperationError("GAME_OPERATION_HISTORY_FULL");
            bool active = MainClientState() != null;
            object client = null;
            string error = null;
            if (operation == "save" || operation == "return_to_menu" || operation == "quit" && active)
                error = GameSaveGate(expectedEpisode, out client);
            else
            {
                object manager, screen;
                error = HotseatResumeMenuGate(out manager, out screen);
                if (error == null && !GameMenuReady(screen)) error = "EXPECTED_MENU_NOT_CURRENT";
                if (error == null && operation == "load_singleplayer")
                {
                    object start = CRTryNativeCast(screen, FindGameType("StartScreen_UI2"));
                    object button = MemberWithAccessor(start, "resumeButton");
                    if (_fixedControlledSeat != 1 || start == null || ReadActive(button) != "true"
                        || ReadBoolPropertyOrGetter(button, "ButtonEnabled") != true) error = "SINGLEPLAYER_RESUME_UNAVAILABLE";
                }
            }
            if (error != null) return GameOperationError(error);
            var row = new GameOperation { Id = operationId, Operation = operation, GameId = gameId, RequestedGameId = gameId, ExpectedEpisode = expectedEpisode };
            try
            {
                if (client != null)
                {
                    row.GameId = MainSessionGameId(client);
                    // The caller cannot supply arbitrary file paths or bypass native validation.
                    Guid savedId;
                    if (!Guid.TryParse(row.GameId, out savedId)) return GameOperationError("SAVE_GAME_ID_UNAVAILABLE");
                    string kind = SafeScalar(MemberWithAccessor(client, "clientType"));
                    MethodInfo path = GameMethod(FindGameType("Paths"), kind == "PassAndPlay" ? "GetHotseatFilePath" : "GetSingleplayerFilePath", true, typeof(string));
                    MethodInfo save = GameMethod(client.GetType(), "SaveSession", false, typeof(string), typeof(bool), typeof(bool));
                    if (path == null || save == null) return GameOperationError("NATIVE_SAVE_UNAVAILABLE");
                    row.Path = path.Invoke(null, new object[] { row.GameId }) as string;
                    if (string.IsNullOrEmpty(row.Path)) return GameOperationError("SAVE_PATH_UNAVAILABLE");
                    row.BeforeStamp = SaveFileStamp(row.Path); row.Stage = "saving";
                    RegisterGameOperation(row);
                    row.Submitted = true;
                    save.Invoke(client, new object[] { row.GameId, true, false });
                }
                else if (operation == "load_singleplayer")
                {
                    MethodInfo load = GameMethod(_gameManagerType, "ResumeSingleplayerGame", false);
                    if (load == null) return GameOperationError("NATIVE_RESUME_UNAVAILABLE");
                    row.Stage = "loading"; RegisterGameOperation(row); row.Submitted = true;
                    load.Invoke(SafeInstance(), null);
                }
                else if (operation == "load_hotseat" || operation == "open_hotseat")
                {
                    // These typed adapters already check the visible native list and game id.
                    string reply = operation == "open_hotseat" ? HotseatGames("open") : HotseatResume(gameId);
                    var parsed = BridgeJsonCodec.Object(reply);
                    if (BridgeJsonCodec.Scalar(parsed.ContainsKey("ok") ? parsed["ok"] : null) != "true") return reply;
                    row.Stage = operation == "open_hotseat" ? "opening" : "loading";
                    row.Submitted = true; RegisterGameOperation(row);
                }
                else
                {
                    // Quit is permitted without a match only from a known local menu.
                    row.Stage = "menu"; RegisterGameOperation(row); ScheduleGameQuit(row);
                }
                InvalidateNativeSamples(); PollGameOperation(row);
            }
            catch (Exception ex)
            {
                if (!row.Submitted && !_gameOperations.ContainsKey(row.Id)) return GameOperationError("NATIVE_PREPARATION_FAILED");
                row.Status = "unknown"; row.Error = (ex.InnerException ?? ex).GetType().Name;
            }
            return GameOperationJson(row);
        }
        private void RegisterGameOperation(GameOperation row)
        { _gameOperations.Add(row.Id, row); _gameOperation = row; }

        private void ScheduleGameQuit(GameOperation row)
        {
            if (GameQuitMethod() == null)
            { row.Status = "unknown"; row.Error = "NATIVE_QUIT_UNAVAILABLE"; return; }
            row.Stage = "quit_scheduled"; row.QuitScheduled = true; row.Submitted = true;
            row.QuitAfterTick = _tick + 2; row.QuitAfterTime = DateTime.UtcNow.AddMilliseconds(750);
        }
        private static MethodInfo GameQuitMethod()
        {
            // Unity and its IL2CPP wrapper can both be loaded. Resolve the exact
            // wrapper name, as the existing diagnostic callback does, not a suffix.
            return GameMethod(FindType("Il2Cpp.UnityEngine.Application") ?? FindType("UnityEngine.Application"), "Quit", true);
        }
        private void PollGameOperation(GameOperation row)
        {
            if (row == null || row.Status == "confirmed" || row.Status == "rejected" || row.Status == "unknown") return;
            try
            {
                if (row.Stage == "saving")
                {
                    row.AfterStamp = SaveFileStamp(row.Path);
                    if (row.AfterStamp == "missing" || row.AfterStamp == "empty" || row.AfterStamp == row.BeforeStamp) return;
                    if (PopupIsOpen() != false) { row.Status = "unknown"; row.Error = "POPUP_AFTER_SAVE"; return; }
                    row.Saved = true;
                    if (row.Operation == "save") { row.Stage = "saved"; row.Status = "confirmed"; return; }
                    object client;
                    string gate = GameSaveGate(row.ExpectedEpisode, out client);
                    if (gate != null || MainSessionGameId(client) != row.GameId)
                    { row.Status = "unknown"; row.Error = gate ?? "SAVE_GAME_CHANGED"; return; }
                    MethodInfo exit = GameMethod(_gameManagerType, "ReturnToMenu", true);
                    if (exit == null) { row.Status = "unknown"; row.Error = "NATIVE_MENU_UNAVAILABLE"; return; }
                    row.Stage = "returning_to_menu"; exit.Invoke(null, null); InvalidateNativeSamples();
                }
                if (row.Stage == "returning_to_menu")
                {
                    if (MainClientState() != null || !NoActiveDisplayedState()) return;
                    object manager, screen;
                    if (HotseatResumeMenuGate(out manager, out screen) != null || !GameMenuReady(screen)) return;
                    row.Stage = "menu";
                    if (row.Operation == "return_to_menu") row.Status = "confirmed";
                    else ScheduleGameQuit(row);
                }
                if (row.Stage == "opening")
                {
                    string games = HotseatGames("list");
                    var data = BridgeJsonCodec.Object(games);
                    if (BridgeJsonCodec.Scalar(data.ContainsKey("ok") ? data["ok"] : null) == "true")
                    { row.NativeList = games; row.Stage = "hotseat_menu"; row.Status = "confirmed"; }
                }
                if (row.Stage == "loading")
                {
                    object gs = MainClientState();
                    if (gs == null) return;
                    string kind = SafeScalar(MemberWithAccessor(MainClient(), "clientType"));
                    if (kind != (row.Operation == "load_hotseat" ? "PassAndPlay" : "Local")
                        || row.Operation == "load_hotseat" && MainSessionGameId(MainClient()) != row.GameId)
                    { row.Status = "unknown"; row.Error = "LOADED_GAME_MISMATCH"; return; }
                    string phase;
                    if (row.Operation == "load_singleplayer")
                    {
                        if (!PrivateSamplingAllowed(gs) || ReadBoolPropertyOrGetter(MainClient(), "IsReady") != true) return;
                        phase = PopupIsOpen() == true ? "needs_decision" : "game_loaded";
                    }
                    else
                    {
                        var handoff = BridgeJsonCodec.Object(HandoffStatus());
                        phase = BridgeJsonCodec.Scalar(handoff.ContainsKey("phase") ? handoff["phase"] : null);
                        if (phase != "map_ready" && phase != "handoff_overlay" && phase != "human_turn" && phase != "needs_decision" && phase != "recap") return;
                    }
                    row.Stage = phase; row.LoadedEpisode = EpisodeId(gs); row.Status = "confirmed";
                }
            }
            catch (Exception ex) { row.Status = "unknown"; row.Error = (ex.InnerException ?? ex).GetType().Name; }
        }
        private void TickGameOperation()
        {
            if (_gameOperation == null || DateTime.UtcNow < _nextGameOperationPoll) return;
            _nextGameOperationPoll = DateTime.UtcNow.AddMilliseconds(200);
            PollGameOperation(_gameOperation);
            var row = _gameOperation;
            if (row.Status == "submitted" && row.Stage == "quit_scheduled" && _tick >= row.QuitAfterTick
                && DateTime.UtcNow >= row.QuitAfterTime)
            {
                object manager, screen;
                if (MainClientState() != null || !NoActiveDisplayedState()
                    || HotseatResumeMenuGate(out manager, out screen) != null || !GameMenuReady(screen))
                { row.Status = "unknown"; row.Error = "GAME_CHANGED_BEFORE_QUIT"; return; }
                row.Stage = "quit_requested"; // Never invoke twice, even if native Quit throws.
                try
                {
                    // Keep the final native progress after the queue owner exits, even
                    // when the caller's last reply still said returning_to_menu.
                    PersistGameOperationIn(_gameOperationReceiptDirectory ?? Path.Combine(WorkDir, "lifecycle-receipts"),
                        row.Id, GameOperationJson(row));
                }
                catch (Exception)
                { row.Status = "unknown"; row.Error = "QUIT_RECEIPT_NOT_DURABLE"; return; }
                try { GameQuitMethod().Invoke(null, null); }
                catch (Exception ex) { row.Status = "unknown"; row.Error = (ex.InnerException ?? ex).GetType().Name; }
            }
        }
        private string GameLifecycleStatus(string operationId, string playerId)
        {
            if (!_fixedControlledSeat.HasValue || playerId != _fixedControlledSeat.Value.ToString(CultureInfo.InvariantCulture))
                return GameOperationError("CONTROLLED_SEAT_MISMATCH");
            GameOperation row;
            if (!_gameOperations.TryGetValue(operationId, out row)) return GameOperationError("OPERATION_NOT_FOUND");
            PollGameOperation(row); return GameOperationJson(row);
        }
        private string GameOperationJson(GameOperation row)
        {
            return new JsonObj().Str("schema", "game-lifecycle-1").Bool("ok", row.Status != "unknown")
                .Str("status", row.Status).Str("operationId", row.Id).Str("operation", row.Operation)
                .Str("phase", row.Stage).Raw("pid", OwnPid.ToString(CultureInfo.InvariantCulture))
                .Str("bridgeStarted", _startupStarted)
                .Raw("controlledPlayerId", _fixedControlledSeat.Value.ToString(CultureInfo.InvariantCulture))
                .NullOrStr("gameId", row.GameId).NullOrStr("episodeId", row.LoadedEpisode)
                .Bool("callbackSubmitted", row.Submitted).Bool("saved", row.Saved)
                .Bool("quitScheduled", row.QuitScheduled).Bool("retryAllowed", false)
                .NullOrStr("saveWriteStamp", row.AfterStamp).NullOrStr("errorCode", row.Error)
                .Raw("savedGames", row.NativeList ?? "null").Close();
        }
    }
}
