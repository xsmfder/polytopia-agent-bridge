using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private object _hotseatResumeTask;
        private string _hotseatResumeId, _hotseatResumeGameId, _hotseatResumeError;
        private bool _hotseatResumeSubmitted;
        private bool _hotseatResumeObservedActive;
        private bool? _hotseatResumeCompleted, _hotseatResumeSucceeded;
        private bool _hotseatMenuOpenPending;
        private string _hotseatMenuOpenError;

        private sealed class HotseatMenuItem
        { internal string GameId, State, Name, Description, LabelsStatus; }

        private static string HotseatAdminError(string code, string message)
        {
            return new JsonObj().Bool("ok", false).Bool("accepted", false).Bool("callbackSubmitted", false)
                .Str("errorCode", code).Str("error", message).Close();
        }

        // The parent command dispatcher exposes these only as audit management commands.
        // Opening is explicit; the default/read operation never navigates or probes a server.
        private string HotseatGames(string op)
        {
            if (String.IsNullOrEmpty(op)) op = "list";
            if (op != "list" && op != "open")
                return HotseatAdminError("INVALID_OPERATION", "hotseat_games op must be list or open");
            object manager, screen;
            string error = HotseatResumeMenuGate(out manager, out screen);
            if (error != null) return error;
            object multiplayer = ResolveCurrentHotseatScreen(screen);
            if (op == "open")
            {
                if (multiplayer != null)
                {
                    // Native ReturnToMenu can return straight to this pooled list.
                    // A completed resume that actually reached an active game may
                    // start a new cycle only after the full no-game menu gate above.
                    if (_hotseatResumeObservedActive) RetireSettledHotseatResumeForNewMenu();
                    _hotseatMenuOpenPending = false; _hotseatMenuOpenError = null;
                    return new JsonObj().Bool("ok", true).Str("schema", "hotseat-games-1")
                        .Str("phase", "menu_ready").Bool("accepted", false).Bool("callbackSubmitted", false)
                        .Bool("alreadyOpen", true).Close();
                }
                // An explicit new start-menu cycle may retire a known terminal
                // resume. Merely polling, repeating open on the same list, or an
                // uncertain native Task must keep the original one-shot receipt.
                if (CRTryNativeCast(screen, FindGameType("StartScreen_UI2")) != null)
                    RetireSettledHotseatResumeForNewMenu();
                if (_hotseatMenuOpenPending)
                    return new JsonObj().Bool("ok", _hotseatMenuOpenError == null).Str("schema", "hotseat-games-1")
                        .Str("phase", _hotseatMenuOpenError == null ? "menu_open_pending" : "menu_open_uncertain")
                        .Bool("callbackSubmitted", true).Bool("duplicate", true)
                        .NullOrStr("error", _hotseatMenuOpenError).Close();
                if (CRTryNativeCast(screen, FindGameType("StartScreen_UI2")) == null)
                    return HotseatAdminError("EXPECTED_MENU_NOT_CURRENT", "open requires the native start screen");
                Type uiType = FindGameType("UIManager");
                MethodInfo open = uiType == null ? null : uiType.GetMethod("OpenMultiplayerScreen",
                    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (open == null) return HotseatAdminError("NATIVE_CALLBACK_UNAVAILABLE", "UIManager.OpenMultiplayerScreen() unavailable");
                _hotseatMenuOpenPending = true;
                try
                {
                    // This is the player's normal menu callback. The native screen may
                    // initialize its own online list; this adapter calls no network API.
                    open.Invoke(null, null);
                    return new JsonObj().Bool("ok", true).Str("schema", "hotseat-games-1")
                        .Str("phase", "menu_open_pending").Bool("accepted", true)
                        .Bool("callbackSubmitted", true).Bool("ready", false).Close();
                }
                catch (Exception failure)
                {
                    _hotseatMenuOpenError = (failure.InnerException ?? failure).GetType().Name;
                    return new JsonObj().Bool("ok", false).Str("schema", "hotseat-games-1")
                        .Str("phase", "menu_open_uncertain").Bool("callbackSubmitted", true)
                        .Str("errorCode", "MENU_OPEN_INVOCATION_UNCERTAIN").Str("error", _hotseatMenuOpenError).Close();
                }
            }
            if (multiplayer == null)
                return HotseatAdminError("EXPECTED_MENU_NOT_CURRENT", "use hotseat_games op=open explicitly before reading the list");
            _hotseatMenuOpenPending = false; _hotseatMenuOpenError = null;
            List<HotseatMenuItem> games;
            error = ReadNativeHotseatGames(multiplayer, out games);
            if (error != null) return error;
            var items = new JsonArr();
            foreach (var game in games)
                items.Raw(new JsonObj().Str("gameId", game.GameId).NullOrStr("state", game.State)
                    .NullOrStr("name", game.Name).NullOrStr("description", game.Description)
                    .NullOrStr("labelsStatus", game.LabelsStatus).Close());
            return new JsonObj().Bool("ok", true).Str("schema", "hotseat-games-1").Str("phase", "menu_ready")
                .Str("source", "MultiplayerScreen.GetHotSeatGames; displayed GameInfoRow labels")
                .Bool("callbackSubmitted", false).Raw("games", items.Close()).Close();
        }

        private void RetireSettledHotseatResumeForNewMenu()
        {
            if (!_hotseatResumeSubmitted || _hotseatResumeCompleted != true
                || !_hotseatResumeSucceeded.HasValue || _hotseatResumeError != null) return;
            _hotseatResumeTask = null;
            _hotseatResumeId = _hotseatResumeGameId = _hotseatResumeError = null;
            _hotseatResumeSubmitted = false;
            _hotseatResumeObservedActive = false;
            _hotseatResumeCompleted = _hotseatResumeSucceeded = null;
            _hotseatMenuOpenPending = false; _hotseatMenuOpenError = null;
        }

        private string HotseatResumeMenuGate(out object manager, out object screen)
        {
            manager = screen = null;
            if (!_fixedControlledSeat.HasValue || _fixedControlledSeat == 0 || _fixedControlledSeat == NaturePlayerId)
                return HotseatAdminError("CONTROLLED_SEAT_REQUIRED", "configure an explicit human seat before hotseat administration");
            if (MainClientState() != null || !NoActiveDisplayedState())
                return HotseatAdminError("ACTIVE_GAME_PRESERVED", "an active main or displayed game must be saved and exited normally first");
            manager = SafeInstance();
            object loading;
            if (manager == null || !CRTryPipelineMember(manager, "isLoadingGame", out loading) || !(loading is bool))
                return HotseatAdminError("NATIVE_LOADING_STATE_UNKNOWN", "native isLoadingGame is unreadable");
            if ((bool)loading)
                return HotseatAdminError("NATIVE_LOADING_IN_PROGRESS", "native game loading is already in progress");
            PollHotseatResume();
            if (_hotseatResumeSubmitted && (!_hotseatResumeCompleted.HasValue || !_hotseatResumeCompleted.Value
                || !_hotseatResumeSucceeded.HasValue))
                return HotseatAdminError("RESUME_ALREADY_PENDING_OR_UNKNOWN", "observe hotseat_resume_status; do not resubmit or navigate");
            Type uiType = FindGameType("UIManager");
            object ui = uiType == null ? null : InstanceOf(uiType);
            MethodInfo getter = uiType == null ? null : uiType.GetMethod("GetCurrentScreen", BindingFlags.Public | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            try { screen = ui == null || getter == null ? null : getter.Invoke(ui, null); }
            catch { }
            if (screen == null) return HotseatAdminError("NATIVE_MENU_NOT_READY", "native current screen is unavailable");
            bool? popup = PopupIsOpen();
            if (popup != false)
                return HotseatAdminError(popup == true ? "NATIVE_POPUP_REQUIRES_ATTENTION" : "NATIVE_POPUP_STATE_UNKNOWN",
                    "hotseat administration does not dismiss a pending popup");
            return null;
        }

        private object ResolveCurrentHotseatScreen(object current)
        {
            // UIManager owns the IScreen outer. The game list is a MonoBehaviour child,
            // not an IScreen: casting GetCurrentScreen straight to MultiplayerScreen
            // cannot resolve the real UI composition.
            object selection = CRTryNativeCast(current, FindGameType("MultiplayerSelectionScreen"));
            if (selection == null || SafeScalar(MemberWithAccessor(selection, "currentScreenType")) != "MultiplayerScreen")
                return null;
            object list = CRTryNativeCast(MemberWithAccessor(selection, "multiplayerScreen"), FindGameType("MultiplayerScreen"));
            // The same outer also owns a replay page. Never read a cached/off-screen
            // game list just because its serialized component reference still exists.
            return list != null && ReadActive(list) == "true" ? list : null;
        }

        private string ReadNativeHotseatGames(object screen, out List<HotseatMenuItem> games)
        {
            games = new List<HotseatMenuItem>();
            MethodInfo getter = screen.GetType().GetMethod("GetHotSeatGames", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (getter == null) return HotseatAdminError("NATIVE_LIST_UNAVAILABLE", "current MultiplayerScreen.GetHotSeatGames() unavailable");
            try
            {
                object raw = getter.Invoke(screen, null);
                var list = ReadNativeList(raw);
                if (list == null) return HotseatAdminError("NATIVE_LIST_UNREADABLE", "native local game list is not readable");
                // Cosmetic labels are one snapshot, not another list traversal per save.
                // Their failure does not make the authoritative native IDs unreadable.
                var rows = ReadNativeList(MemberWithAccessor(screen, "rows"));
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (object summary in list)
                {
                    string gameId = HotseatGuidText(MemberWithAccessor(summary, "GameId"));
                    if (gameId == null || !ids.Add(gameId))
                        return HotseatAdminError("NATIVE_LIST_ID_INVALID", "native local game ID is missing, invalid or duplicated");
                    var item = new HotseatMenuItem { GameId = gameId, State = SafeScalar(MemberWithAccessor(summary, "State")),
                        LabelsStatus = rows == null ? "unavailable" : "not-found" };
                    // Only actual menu labels are exposed. Do not serialize SummaryData,
                    // Participators, gameState, player profiles, seed, map or units.
                    if (rows != null)
                        foreach (object rawRow in rows)
                        {
                            object row = CRTryNativeCast(rawRow, FindGameType("GameInfoRow"));
                            if (row == null) continue;
                            object rowSummary = MemberWithAccessor(row, "summaryViewModel");
                            if (HotseatGuidText(MemberWithAccessor(rowSummary, "GameId")) != gameId) continue;
                            item.Name = SafeScalar(MemberWithAccessor(MemberWithAccessor(row, "nameLabel"), "text"));
                            item.Description = SafeScalar(MemberWithAccessor(MemberWithAccessor(row, "infoLabel"), "text"));
                            item.LabelsStatus = item.Name != null && item.Description != null ? "ok" : "partial";
                            break;
                        }
                    games.Add(item);
                }
                return null;
            }
            catch (Exception failure)
            { return HotseatAdminError("NATIVE_LIST_READ_FAILED", (failure.InnerException ?? failure).GetType().Name); }
        }

        private static string HotseatGuidText(object value)
        {
            if (value == null) return null;
            Guid parsed;
            if (value is Guid) return (Guid)value == Guid.Empty ? null : ((Guid)value).ToString("D");
            if (value.GetType().FullName != "Il2CppSystem.Guid") return null;
            try
            {
                MethodInfo text = value.GetType().GetMethod("ToString", Type.EmptyTypes);
                string raw = text == null ? null : text.Invoke(value, null) as string;
                return Guid.TryParse(raw, out parsed) && parsed != Guid.Empty ? parsed.ToString("D") : null;
            }
            catch { return null; }
        }

        private static object HotseatNativeGuid(Type type, Guid parsed)
        {
            if (type == typeof(Guid)) return parsed;
            if (type == null || type.FullName != "Il2CppSystem.Guid") return null;
            MethodInfo parse = type.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            return parse == null ? null : parse.Invoke(null, new object[] { parsed.ToString("D") });
        }

        private string HotseatResume(string gameId)
        {
            Guid parsed;
            if (!Guid.TryParse(gameId, out parsed) || parsed == Guid.Empty)
                return HotseatAdminError("INVALID_GAME_ID", "gameId must be a non-empty GUID from the current native hotseat menu");
            string canonical = parsed.ToString("D");
            if (_hotseatResumeSubmitted && canonical == _hotseatResumeGameId)
            { PollHotseatResume(); return HotseatResumeReceipt(true); }
            object manager, current;
            string error = HotseatResumeMenuGate(out manager, out current);
            if (error != null) return error;
            object screen = ResolveCurrentHotseatScreen(current);
            if (screen == null) return HotseatAdminError("EXPECTED_MENU_NOT_CURRENT", "resume requires the current native multiplayer game list");
            List<HotseatMenuItem> games;
            error = ReadNativeHotseatGames(screen, out games);
            if (error != null) return error;
            bool found = false;
            foreach (HotseatMenuItem game in games) if (game.GameId == canonical) { found = true; break; }
            if (!found) return HotseatAdminError("GAME_NOT_IN_CURRENT_MENU", "gameId does not match a currently listed local hotseat game");
            MethodInfo resume = null;
            foreach (MethodInfo method in manager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                ParameterInfo[] args = method.GetParameters();
                if (method.Name != "ResumeHotseatGame" || args.Length != 1) continue;
                Type guidType = args[0].ParameterType, taskType = method.ReturnType;
                if ((guidType != typeof(Guid) && guidType.FullName != "Il2CppSystem.Guid") || !taskType.IsGenericType
                    || taskType.GetGenericArguments().Length != 1 || taskType.GetGenericArguments()[0] != typeof(bool)) continue;
                string taskName = taskType.GetGenericTypeDefinition().FullName;
                if (taskName != "System.Threading.Tasks.Task`1" && taskName != "Il2CppSystem.Threading.Tasks.Task`1") continue;
                if (resume != null) return HotseatAdminError("NATIVE_RESUME_SIGNATURE_AMBIGUOUS", "more than one typed native resume method");
                resume = method;
            }
            if (resume == null) return HotseatAdminError("NATIVE_RESUME_SIGNATURE_UNAVAILABLE", "ResumeHotseatGame(Guid) -> Task<bool> unavailable");
            object nativeId;
            try { nativeId = HotseatNativeGuid(resume.GetParameters()[0].ParameterType, parsed); }
            catch (Exception failure) { return HotseatAdminError("NATIVE_GUID_CONVERSION_FAILED", (failure.InnerException ?? failure).GetType().Name); }
            if (nativeId == null) return HotseatAdminError("NATIVE_GUID_CONVERSION_FAILED", "native Guid.Parse(string) unavailable");
            _hotseatResumeId = "resume-" + Guid.NewGuid().ToString("N");
            _hotseatResumeGameId = canonical; _hotseatResumeTask = null;
            _hotseatResumeObservedActive = false;
            _hotseatResumeCompleted = _hotseatResumeSucceeded = null; _hotseatResumeError = null;
            _hotseatResumeSubmitted = true;
            try
            {
                // Same typed callback as GameInfoPopup's actual continue button.
                _hotseatResumeTask = resume.Invoke(manager, new[] { nativeId });
                if (_hotseatResumeTask == null) _hotseatResumeError = "native resume returned no Task";
                InvalidateNativeSamples();
            }
            catch (Exception failure)
            { _hotseatResumeError = "native invocation uncertain: " + (failure.InnerException ?? failure).GetType().Name; }
            PollHotseatResume();
            return HotseatResumeReceipt(false);
        }

        private void PollHotseatResume()
        {
            ObserveHotseatResumeActivation();
            if (!_hotseatResumeSubmitted || _hotseatResumeSucceeded.HasValue || _hotseatResumeError != null) return;
            if (_hotseatResumeTask == null) { _hotseatResumeError = "native resume Task unavailable"; return; }
            bool? completed = ReadBoolPropertyOrGetter(_hotseatResumeTask, "IsCompleted");
            _hotseatResumeCompleted = completed;
            if (completed != true) return; // Never block the Unity thread on Task.Result.
            if (ReadBoolPropertyOrGetter(_hotseatResumeTask, "IsFaulted") == true
                || ReadBoolPropertyOrGetter(_hotseatResumeTask, "IsCanceled") == true)
            { _hotseatResumeError = "native resume Task faulted or was canceled; loaded state remains uncertain"; return; }
            object result;
            if (!CRTryPipelineMember(_hotseatResumeTask, "Result", out result) || !(result is bool))
            { _hotseatResumeError = "completed native resume Task has no readable boolean Result"; return; }
            _hotseatResumeSucceeded = (bool)result;
            ObserveHotseatResumeActivation();
        }

        private void ObserveHotseatResumeActivation()
        {
            // Presence only: no map, player, unit or action-manager sampling.
            // Run from the existing bounded tick too, so callers need not poll
            // a completed receipt before normal menu return to establish this.
            if (_hotseatResumeSubmitted && _hotseatResumeSucceeded == true
                && _hotseatResumeError == null && !_hotseatResumeObservedActive
                && MainClientState() != null) _hotseatResumeObservedActive = true;
        }

        private string HotseatResumeStatus() { PollHotseatResume(); return HotseatResumeReceipt(false); }

        private string HotseatResumeReceipt(bool duplicate)
        {
            bool taskUnknown = _hotseatResumeSubmitted && _hotseatResumeTask != null
                && !_hotseatResumeCompleted.HasValue && _hotseatResumeError == null;
            string phase = !_hotseatResumeSubmitted ? "idle" : _hotseatResumeError != null || taskUnknown ? "uncertain"
                : _hotseatResumeCompleted != true ? "pending" : _hotseatResumeSucceeded == true ? "native_completed" : "native_failed";
            string code = _hotseatResumeError != null ? "NATIVE_RESUME_UNCERTAIN"
                : taskUnknown ? "NATIVE_TASK_STATE_UNKNOWN" : _hotseatResumeSucceeded == false ? "NATIVE_RESUME_FAILED" : null;
            return new JsonObj().Bool("ok", code == null).Str("schema", "hotseat-resume-1").Str("phase", phase)
                .NullOrStr("resumeId", _hotseatResumeId).NullOrStr("gameId", _hotseatResumeGameId)
                .Bool("callbackSubmitted", _hotseatResumeSubmitted)
                .Bool("accepted", _hotseatResumeSubmitted && _hotseatResumeTask != null)
                .Bool("duplicate", duplicate).Raw("nativeCompleted", CRB(_hotseatResumeCompleted))
                .Raw("nativeSucceeded", CRB(_hotseatResumeSucceeded)).Bool("ready", false)
                .Str("next", "observe handoff_status; native completion is not controlled-map readiness")
                .NullOrStr("errorCode", code)
                .NullOrStr("error", taskUnknown ? "native Task.IsCompleted is unreadable; do not resubmit" : _hotseatResumeError).Close();
        }
    }
}
