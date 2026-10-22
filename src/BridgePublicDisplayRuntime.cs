// Observe only the native statistics rows and actually shown diplomacy notifications.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private static BridgeMod _displayHost;
        private bool _leaderboardHooksInstalled, _notificationHookInstalled;
        private sealed class LeaderboardCapture
        {
            internal BridgeMod Host;
            internal object State, Screen;
            internal string Episode, Stamp, HoldingKind, Key, Turn, ViewRequestId, Stream;
            internal long Revision, OriginRequestId;
            internal byte Me;
            internal int? Score, Holdings;
            internal readonly List<string> Rows = new List<string>();
        }
        private LeaderboardCapture _leaderboardCapture, _leaderboardSnapshot;
        private object _leaderboardOwnedScreen;
        private object _leaderboardViewUI;
        private string _leaderboardViewRequestId, _leaderboardViewOperation;
        private bool _leaderboardClosing, _leaderboardCallbackCompleted, _leaderboardDispatchActive;
        private long _leaderboardOpenedTick, _leaderboardOpenOriginRequestId;

        private void ClearLeaderboardView()
        {
            _leaderboardOwnedScreen = _leaderboardViewUI = null;
            _leaderboardViewRequestId = _leaderboardViewOperation = null;
            _leaderboardClosing = _leaderboardCallbackCompleted = _leaderboardDispatchActive = false;
            _leaderboardOpenedTick = _leaderboardOpenOriginRequestId = 0;
        }

        private static string LeaderboardUnavailable(string reason, string requestId = null)
        {
            return new JsonObj().Bool("ok", false).Str("schema", "leaderboard-1").Str("status", "unavailable")
                .Str("reason", reason).Str("outcome", "not_dispatched").Bool("callbackAttempted", false)
                .NullOrStr("viewRequestId", string.IsNullOrEmpty(requestId) ? null : requestId).Close();
        }

        private JsonObj LeaderboardViewResult(object gs, byte me, string status, bool ok, bool confirmed)
        {
            return new JsonObj().Bool("ok", ok).Str("schema", "leaderboard-1").Str("status", status)
                .Str("episodeId", EpisodeId(gs)).Raw("controlledPlayerId", me.ToString(CultureInfo.InvariantCulture))
                .Str("streamId", _combatStream).NullOrStr("viewRequestId", _leaderboardViewRequestId)
                .NullOrStr("viewOperation", _leaderboardViewOperation).Bool("callbackCompleted", _leaderboardCallbackCompleted)
                .Bool("viewEffectConfirmed", confirmed).Str("outcome", confirmed ? "confirmed" : _leaderboardCallbackCompleted ? "invoked" : "unknown");
        }

        private bool LeaderboardSnapshotAvailable(object gs, byte me)
        {
            return _leaderboardSnapshot != null && CRSameNative(_leaderboardSnapshot.State, gs)
                && _leaderboardSnapshot.Episode == EpisodeId(gs) && _leaderboardSnapshot.Me == me;
        }

        private bool LeaderboardSnapshotFresh(object gs, byte me)
        {
            if (!LeaderboardSnapshotAvailable(gs, me)) return false;
            bool settled = Json.GetString(CombatStateJson(gs), "settlementStatus") == "complete";
            return settled && _leaderboardSnapshot.Stamp == PublicDisplayStamp(gs, me);
        }

        private JsonObj LeaderboardRows(JsonObj result, object gs, byte me, bool? freshValue = null)
        {
            if (!LeaderboardSnapshotAvailable(gs, me)) return result.Raw("rows", "null").Str("snapshotStatus", "unavailable");
            var snapshot = _leaderboardSnapshot;
            bool fresh = freshValue ?? LeaderboardSnapshotFresh(gs, me);
            return result.Str("snapshotStatus", fresh ? "ok" : "historical").Bool("fresh", fresh)
                .Bool("observerInstalled", _leaderboardHooksInstalled).Str("snapshotKey", snapshot.Key)
                .NullOrStr("observedTurn", snapshot.Turn).Raw("observationRevision", snapshot.Revision.ToString(CultureInfo.InvariantCulture))
                .Raw("rows", "[" + string.Join(",", snapshot.Rows.ToArray()) + "]")
                .Str("rankingBasis", "native sorted display order; position is an ordinal, not inferred tie rank; row keys belong to this snapshot")
                .Str("authority", "naturally generated GameStatsScreen rows, cached score result and displayed count argument");
        }

        private string PollLeaderboardClose(object gs, byte me)
        {
            bool closed = _leaderboardOwnedScreen == null || ReadActive(_leaderboardOwnedScreen) == "false";
            if (closed) _leaderboardOwnedScreen = null;
            var result = LeaderboardViewResult(gs, me, closed ? "closed" : _leaderboardCallbackCompleted ? "closing" : "unknown",
                closed || _leaderboardCallbackCompleted, closed);
            if (!closed) result.Str("next", "read the original navigation attempt; OnBack will not be repeated");
            return LeaderboardRows(result, gs, me).Close();
        }
        private string _notificationScope, _notificationStream = Guid.NewGuid().ToString("N");
        private long _notificationId;
        private long _unreadableNotificationCount;
        private readonly List<KeyValuePair<long, string>> _publicNotifications = new List<KeyValuePair<long, string>>();
        private object _lastNotification;
        private long _lastNotificationTick = -1;

        private void InstallPublicDisplayHooks()
        {
            _displayHost = this;
            if (HarmonyInstance == null) return;
            var stats = FindGameType("GameStatsScreen");
            bool populate = DisplayPatch(DisplayMethod(stats, "PopulatePlayers", false, new string[0]),
                "LeaderboardPopulatePrefix", null, "LeaderboardPopulateFinalizer");
            bool score = DisplayPatch(DisplayMethod(stats, "GetCachedScoreForPlayer", false, new[] { "PlayerState" }),
                null, "LeaderboardScorePostfix", null);
            bool description = DisplayPatch(DisplayMethod(stats, "GetDescription", false, new[] { "PlayerState", "Int32", "String" }),
                null, "LeaderboardDescriptionPostfix", null);
            bool row = DisplayPatch(DisplayMethod(FindGameType("StatsRowController"), "LayoutPlayerInfo", true,
                new[] { "StatsRowView", "LayoutInfo", "String", "String", "String", "Boolean", "Boolean", "Int32", "String" }),
                null, "LeaderboardRowPostfix", null);
            bool navigation = DisplayPatch(DisplayMethod(FindGameType("UIManager"), "ShowScreen", false,
                new[] { "Screens", "Boolean", "UIDeepLinkData" }), "LeaderboardNavigationPrefix", null, null);
            _leaderboardHooksInstalled = populate && score && description && row && navigation;
            _notificationHookInstalled = DisplayPatch(DisplayMethod(FindGameType("NotificationBase"), "Show", false, new[] { "Single" }),
                null, "DiplomacyNotificationShownPostfix", null);
            Log("public display hooks: leaderboard=" + _leaderboardHooksInstalled + " notification=" + _notificationHookInstalled);
        }

        private static MethodInfo DisplayMethod(Type type, string name, bool isStatic, string[] parameters)
        {
            if (type == null) return null;
            MethodInfo result = null;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance)))
            {
                if (method.Name != name) continue;
                var args = method.GetParameters(); if (args.Length != parameters.Length) continue;
                bool match = true;
                for (int i = 0; i < args.Length; i++) if (args[i].ParameterType.Name != parameters[i]) match = false;
                if (!match) continue;
                if (result != null) return null;
                result = method;
            }
            return result;
        }

        private bool DisplayPatch(MethodInfo method, string prefix, string postfix, string finalizer)
        {
            if (method == null) return false;
            try { return HarmonyInstance.Patch(method, prefix == null ? null : CRHook(prefix),
                postfix == null ? null : CRHook(postfix), null, finalizer == null ? null : CRHook(finalizer), null) != null; }
            catch (Exception error) { Log("public display hook " + method.Name + " " + error.GetType().Name); return false; }
        }

        private string PublicDisplayStamp(object gs, byte me)
        {
            return EpisodeId(gs) + ":" + me + ":" + SafeScalar(MemberWithAccessor(gs, "CurrentTurn"))
                + ":" + SafeScalar(MemberWithAccessor(MemberWithAccessor(gs, "CommandStack"), "Count")) + ":" + _combatVersion;
        }

        private static string DisplayPlainText(string value)
        { return value == null ? null : System.Text.RegularExpressions.Regex.Replace(value, "<[^>]*>", ""); }

        private void BeginLeaderboardCapture(object screen)
        {
            _leaderboardCapture = null;
            var gs = GameStateInstance();
            if (screen == null || gs == null || !PrivateSamplingAllowed(gs)) return;
            var me = ResolveControlledIdentity(gs);
            if (!me.Verified || CurrentPlayerId(gs) != me.Id) return;
            _leaderboardSnapshot = null;
            string mode = SafeScalar(MemberWithAccessor(MemberWithAccessor(gs, "Settings"), "RulesGameMode"));
            _leaderboardCapture = new LeaderboardCapture { Host = this, State = gs, Screen = screen, Episode = EpisodeId(gs), Me = me.Id, Stream = _combatStream,
                HoldingKind = mode == "Might" ? "capitals" : mode == null ? null : "cities" };
            if (_leaderboardViewOperation == "open" && (_leaderboardDispatchActive || CRSameNative(screen, _leaderboardOwnedScreen)))
            {
                _leaderboardCapture.ViewRequestId = _leaderboardViewRequestId;
                _leaderboardCapture.OriginRequestId = _leaderboardOpenOriginRequestId;
                if (_leaderboardDispatchActive && _leaderboardOwnedScreen == null) _leaderboardOwnedScreen = screen;
            }
        }

        private void CompleteLeaderboardCapture(object screen, bool failed)
        {
            var capture = _leaderboardCapture; _leaderboardCapture = null;
            if (capture == null || failed || !CRSameNative(capture.Screen, screen) || capture.Rows.Count == 0) return;
            var gs = GameStateInstance();
            if (!CRSameNative(gs, capture.State) || !PrivateSamplingAllowed(gs)) return;
            var me = ResolveControlledIdentity(gs);
            if (!me.Verified || me.Id != capture.Me || capture.Episode != EpisodeId(gs)) return;
            capture.Stamp = PublicDisplayStamp(gs, me.Id); capture.Key = Guid.NewGuid().ToString("N");
            capture.Turn = SafeScalar(MemberWithAccessor(gs, "CurrentTurn")); capture.Revision = _observationRevision;
            _leaderboardSnapshot = capture;
        }

        private void LogLeaderboardCaptureFailure(string stage, LeaderboardCapture capture, Exception error, bool dispatch = false)
        {
            long origin = capture == null ? 0 : capture.OriginRequestId;
            string view = capture == null ? null : capture.ViewRequestId;
            // Only a prefix currently inside our own ShowScreen dispatch may use
            // the pending open attempt. A later callback without a token is unknown.
            if (capture == null && dispatch && _leaderboardDispatchActive && _leaderboardViewOperation == "open")
            { origin = _leaderboardOpenOriginRequestId; view = _leaderboardViewRequestId; }
            Exception cause = error is TargetInvocationException && error.InnerException != null ? error.InnerException : error;
            Log("leaderboard capture failure originRequest=" + (origin > 0 ? origin.ToString(CultureInfo.InvariantCulture) : "?")
                + " viewRequest=" + ReadTraceToken(view, 64) + " stage=" + ReadTraceToken(stage, 32)
                + " ep=" + ReadTraceToken(capture == null ? null : capture.Episode, 64)
                + " player=" + (capture == null ? "?" : capture.Me.ToString(CultureInfo.InvariantCulture))
                + " stream=" + ReadTraceToken(capture == null ? null : capture.Stream, 64)
                + " type=" + ReadTraceToken(cause == null ? "unavailable" : cause.GetType().Name, 32));
        }

        private static void LeaderboardPopulatePrefix(object __instance, out LeaderboardCapture __state)
        {
            __state = null;
            var h = _displayHost;
            if (h == null) return;
            try { h.BeginLeaderboardCapture(__instance); __state = h._leaderboardCapture; }
            catch (Exception error)
            { h.LogLeaderboardCaptureFailure("begin", h._leaderboardCapture, error, true); h._leaderboardCapture = null; }
        }
        private static void LeaderboardNavigationPrefix(object __instance)
        {
            var h = _displayHost;
            if (h == null || h._leaderboardDispatchActive || h._leaderboardViewOperation != "open"
                || !CRSameNative(__instance, h._leaderboardViewUI)) return;
            // Any later navigation has its own origin. Pooled screens must not inherit
            // an old open request's ownership merely because the CLR object is reused.
            h.ClearLeaderboardView();
        }
        private static Exception LeaderboardPopulateFinalizer(object __instance, Exception __exception, LeaderboardCapture __state)
        {
            var h = __state == null ? _displayHost : __state.Host;
            if (h == null) return __exception;
            if (__exception != null) h.LogLeaderboardCaptureFailure("native_populate", __state, __exception);
            // A screen can be pooled or populated again before an old finalizer.
            // Its old token grants no right to finalize/clear the newer capture.
            if (__state == null || !ReferenceEquals(h._leaderboardCapture, __state)) return __exception;
            try { h.CompleteLeaderboardCapture(__instance, __exception != null); }
            catch (Exception error)
            {
                h.LogLeaderboardCaptureFailure("complete", __state, error);
                if (ReferenceEquals(h._leaderboardCapture, __state)) h._leaderboardCapture = null;
            }
            return __exception;
        }
        private static void LeaderboardScorePostfix(object __instance, int __result)
        { var h = _displayHost; var c = h == null ? null : h._leaderboardCapture; if (c != null && CRSameNative(c.Screen, __instance)) c.Score = __result; }
        private static void LeaderboardDescriptionPostfix(object __instance, int __1)
        { var h = _displayHost; var c = h == null ? null : h._leaderboardCapture; if (c != null && CRSameNative(c.Screen, __instance)) c.Holdings = __1; }
        private static void LeaderboardRowPostfix(string __2, string __3, string __4, bool __5, bool __6)
        {
            var h = _displayHost; if (h == null) return;
            var capture = h._leaderboardCapture;
            try { h.AppendLeaderboardRow(__2, __3, __4, __5, __6); }
            catch (Exception error)
            { h.LogLeaderboardCaptureFailure("row", capture, error); if (ReferenceEquals(h._leaderboardCapture, capture)) h._leaderboardCapture = null; }
        }

        private void AppendLeaderboardRow(string label, string scoreText, string summary, bool alive, bool highlighted)
        {
            var c = _leaderboardCapture; if (c == null) return;
            // Only scalar outputs of this native PopulatePlayers invocation. No player argument,
            // avatar payload, layout icon, username input or native identity is serialized.
            int position = c.Rows.Count + 1;
            // GetDescription omits the count for dead rows. In Might its branch uses
            // cached score instead of the supplied CountCapitals argument: withhold that input.
            int? displayedHoldings = alive && c.HoldingKind == "cities" ? c.Holdings : null;
            var row = new JsonObj().Str("rowKey", "row-" + position).Raw("position", position.ToString(CultureInfo.InvariantCulture))
                .NullOrStr("label", DisplayPlainText(label)).NullOrStr("scoreText", DisplayPlainText(scoreText))
                .Raw("score", c.Score.HasValue ? c.Score.Value.ToString(CultureInfo.InvariantCulture) : "null")
                .NullOrStr("summaryText", DisplayPlainText(summary))
                .Raw("holdingCount", displayedHoldings.HasValue ? displayedHoldings.Value.ToString(CultureInfo.InvariantCulture) : "null")
                .NullOrStr("holdingKind", displayedHoldings.HasValue ? "cities" : null).Bool("alive", alive).Bool("highlighted", highlighted);
            c.Rows.Add(row.Close()); c.Score = null; c.Holdings = null;
        }

        private object CurrentStatsScreen(out object ui)
        {
            Type type = FindGameType("UIManager"); ui = type == null ? null : InstanceOf(type);
            var current = ui == null ? null : InvokeOn(ui, "GetCurrentScreen", null);
            return current == null ? null : CRTryNativeCast(current, FindGameType("GameStatsScreen"));
        }

        private string LeaderboardPublic(string mode)
        { return LeaderboardPublicRequest(mode, null); }

        private string LeaderboardPublicRequest(string mode, string viewRequestId)
        {
            var gs = GameStateInstance();
            if (gs == null || !PrivateSamplingAllowed(gs)) return LeaderboardUnavailable("no observable controlled game", viewRequestId);
            var me = ResolveControlledIdentity(gs);
            if (!me.Verified || CurrentPlayerId(gs) != me.Id) return LeaderboardUnavailable("not controlled player's turn", viewRequestId);
            if (string.IsNullOrEmpty(mode)) mode = "read";
            if (mode != "open" && mode != "close" && mode != "read") return LeaderboardUnavailable("mode must be open, read, or close", viewRequestId);
            if (_leaderboardClosing && mode != "open")
            {
                if (mode == "close" && viewRequestId != _leaderboardViewRequestId)
                    return LeaderboardUnavailable("close already submitted; read the original attempt", viewRequestId);
                return PollLeaderboardClose(gs, me.Id);
            }
            object ui; var screen = CurrentStatsScreen(out ui);
            if (mode == "close")
            {
                if (_leaderboardOwnedScreen != null && ReadActive(_leaderboardOwnedScreen) == "false")
                {
                    _leaderboardClosing = true; _leaderboardViewRequestId = viewRequestId; _leaderboardViewOperation = "close";
                    _leaderboardCallbackCompleted = false;
                    return PollLeaderboardClose(gs, me.Id);
                }
                if (screen == null || _leaderboardOwnedScreen == null || !CRSameNative(screen, _leaderboardOwnedScreen))
                    return LeaderboardUnavailable("no matching statistics screen opened by this interface", viewRequestId);
                // Native PopCurrentScreen removes only the stack item. OnBack is the
                // normal screen callback that hides the target and restores its predecessor.
                var pop = DisplayMethod(ui.GetType(), "OnBack", false, new string[0]);
                if (pop == null || pop.ContainsGenericParameters || pop.ReturnType != typeof(void)) return LeaderboardUnavailable("native close unavailable", viewRequestId);
                _leaderboardClosing = true; _leaderboardViewRequestId = viewRequestId; _leaderboardViewOperation = "close";
                _leaderboardCallbackCompleted = false;
                try { pop.Invoke(ui, null); _leaderboardCallbackCompleted = true; }
                catch (Exception error) { LogUiCallback("uncertain " + (error.InnerException ?? error).GetType().Name, "leaderboard.OnBack"); }
                // Observe a possibly completed navigation; never pop a second screen.
                return PollLeaderboardClose(gs, me.Id);
            }
            if (mode == "open")
            {
                if (_leaderboardOwnedScreen != null || _leaderboardViewOperation == "open")
                    return LeaderboardUnavailable("statistics view already has an attempt; read or close it first", viewRequestId);
                if (!_leaderboardHooksInstalled) return LeaderboardUnavailable("native statistics hooks unavailable", viewRequestId);
                if (PopupIsOpen() != false || Json.GetString(CombatStateJson(gs), "settlementStatus") != "complete")
                    return LeaderboardUnavailable("resolve pending popup or settlement first", viewRequestId);
                if (ui == null || SafeScalar(MemberWithAccessor(ui, "CurrentScreen")) != "Hud")
                    return LeaderboardUnavailable("native HUD must be current before opening statistics", viewRequestId);
                var show = DisplayMethod(ui.GetType(), "ShowScreen", false, new[] { "Screens", "Boolean", "UIDeepLinkData" });
                if (show == null || show.ContainsGenericParameters) return LeaderboardUnavailable("native statistics navigation unavailable", viewRequestId);
                object screenValue;
                try { screenValue = Enum.Parse(show.GetParameters()[0].ParameterType, "StatsScreen"); }
                catch { return LeaderboardUnavailable("native statistics enum unavailable", viewRequestId); }
                _leaderboardSnapshot = null; _leaderboardCapture = null; _leaderboardViewUI = ui;
                _leaderboardViewRequestId = viewRequestId; _leaderboardViewOperation = "open";
                _leaderboardOpenOriginRequestId = _activeRequestId;
                _leaderboardClosing = false; _leaderboardCallbackCompleted = false; _leaderboardOpenedTick = _tick;
                object returned = null;
                _leaderboardDispatchActive = true;
                try { returned = show.Invoke(ui, new[] { screenValue, (object)false, null }); _leaderboardCallbackCompleted = true; }
                catch (Exception error) { LogUiCallback("uncertain " + (error.InnerException ?? error).GetType().Name, "leaderboard.ShowScreen"); }
                finally { _leaderboardDispatchActive = false; }
                var candidate = CRTryNativeCast(returned, FindGameType("GameStatsScreen"));
                if (candidate != null) _leaderboardOwnedScreen = candidate;
                // The callback may have shown the screen before throwing. This immediate
                // main-thread observation cannot be a later manual navigation.
                var current = CurrentStatsScreen(out ui);
                if (_leaderboardOwnedScreen == null && CRSameNative(ui, _leaderboardViewUI)) _leaderboardOwnedScreen = current;
                return LeaderboardViewResult(gs, me.Id, _leaderboardCallbackCompleted ? "opening" : "unknown", _leaderboardCallbackCompleted, false)
                    .Str("next", "read after native statistics have populated").Close();
            }
            if (!LeaderboardSnapshotAvailable(gs, me.Id))
            {
                if (_leaderboardViewOperation == "open")
                    return LeaderboardViewResult(gs, me.Id, _leaderboardCallbackCompleted ? "opening" : "unknown", _leaderboardCallbackCompleted, false)
                        .Raw("rows", "null").Str("next", "read after the original screen population completes").Close();
                return LeaderboardUnavailable("no native leaderboard captured in this game; open first");
            }
            bool fresh = LeaderboardSnapshotFresh(gs, me.Id);
            bool confirmed = _leaderboardViewOperation == "open" && fresh && _tick > _leaderboardOpenedTick
                && _leaderboardSnapshot.ViewRequestId == _leaderboardViewRequestId && _leaderboardViewRequestId != null
                && _leaderboardOwnedScreen != null && CRSameNative(screen, _leaderboardOwnedScreen)
                && CRSameNative(ui, _leaderboardViewUI) && CRSameNative(_leaderboardSnapshot.Screen, screen) && ReadActive(screen) == "true";
            return LeaderboardRows(LeaderboardViewResult(gs, me.Id, fresh ? "ok" : "historical", true, confirmed), gs, me.Id, fresh).Close();
        }

        private void EnsureNotificationScope(object gs, byte me)
        {
            string scope = EpisodeId(gs) + ":" + me;
            if (_notificationScope == scope) return;
            _notificationScope = scope; _notificationStream = Guid.NewGuid().ToString("N"); _notificationId = 0;
            _unreadableNotificationCount = 0;
            _publicNotifications.Clear(); _lastNotification = null; _lastNotificationTick = -1;
        }

        private static void DiplomacyNotificationShownPostfix(object __instance)
        {
            var h = _displayHost; if (h == null) return;
            try { h.ObserveDiplomacyNotification(__instance); }
            catch (Exception error) { Log("public diplomacy notification " + error.GetType().Name); }
        }

        private void ObserveDiplomacyNotification(object notification)
        {
            var gs = GameStateInstance();
            if (gs == null || !PrivateSamplingAllowed(gs)) return;
            var me = ResolveControlledIdentity(gs); if (!me.Verified) return;
            // Native Notify has already applied receiver/tech/contact conditions. Observe Show,
            // rather than its inputs, so rejected notifications and another hotseat stay private.
            if (CRTryNativeCast(notification, FindGameType("DiplomacyNotification")) == null
                || ReadActive(notification) != "true" || SafeScalar(MemberWithAccessor(notification, "state")) != "Showing") return;
            EnsureNotificationScope(gs, me.Id);
            if (CRSameNative(_lastNotification, notification) && _lastNotificationTick == _tick) return;
            _lastNotification = notification; _lastNotificationTick = _tick;
            // Notify assigns TMP text before Show. GetParsedText can still contain
            // an empty or previous mesh until the next canvas update; never use
            // that cache as the current message, and do not force a UI refresh.
            string title = ShownNotificationText(MemberWithAccessor(notification, "header"));
            string message = ShownNotificationText(MemberWithAccessor(notification, "description"));
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(message))
            { _unreadableNotificationCount++; return; }
            long id = ++_notificationId;
            var row = new JsonObj().Raw("eventId", id.ToString(CultureInfo.InvariantCulture)).Str("kind", "native_diplomacy_notification")
                .Str("episodeId", EpisodeId(gs)).Raw("controlledPlayerId", me.Id.ToString(CultureInfo.InvariantCulture))
                .Raw("observedTurn", PublicNumber(MemberWithAccessor(gs, "CurrentTurn")))
                .Raw("observationRevision", _observationRevision.ToString(CultureInfo.InvariantCulture))
                .NullOrStr("title", title).NullOrStr("message", message)
                .Str("authority", "actually shown native diplomacy notification; rendered text only; historical notice, not current relation");
            _publicNotifications.Add(new KeyValuePair<long, string>(id, row.Close()));
            if (_publicNotifications.Count > 128) _publicNotifications.RemoveAt(0);
        }

        private string ShownNotificationText(object control)
        {
            if (control == null || ReadActive(control) != "true") return null;
            // Exact native TMP_Text.text accessor, one read. No model/private
            // notification payload or fallback to an old parsed mesh is used.
            return DisplayPlainText(MemberWithAccessor(control, "text") as string);
        }

        private string NotificationsPublic(long after, string stream, int limit)
        {
            var gs = GameStateInstance();
            if (gs == null || !PrivateSamplingAllowed(gs)) return PublicUnavailable("public-notifications-1", "no observable controlled game");
            var me = ResolveControlledIdentity(gs); if (!me.Verified) return PublicUnavailable("public-notifications-1", "controlled identity unavailable");
            EnsureNotificationScope(gs, me.Id);
            if (!string.IsNullOrEmpty(stream) && stream != _notificationStream) return PublicUnavailable("public-notifications-1", "notification stream changed; restart cursor");
            limit = Math.Max(1, Math.Min(128, limit)); after = Math.Max(0, after);
            long oldest = _publicNotifications.Count == 0 ? _notificationId + 1 : _publicNotifications[0].Key, cursor = after;
            var rows = new List<string>();
            foreach (var item in _publicNotifications) if (item.Key > after && rows.Count < limit) { rows.Add(item.Value); cursor = item.Key; }
            return new JsonObj().Bool("ok", true).Str("schema", "public-notifications-1").Str("episodeId", EpisodeId(gs))
                .Raw("controlledPlayerId", me.Id.ToString(CultureInfo.InvariantCulture)).Str("streamId", _notificationStream)
                .Str("status", _notificationHookInstalled ? "ok" : "unavailable")
                .Bool("observerInstalled", _notificationHookInstalled).Str("coverage", "only notices shown since this observer and game scope started")
                .Bool("captureComplete", _notificationHookInstalled && _unreadableNotificationCount == 0)
                .Raw("unreadableNoticeCount", _unreadableNotificationCount.ToString(CultureInfo.InvariantCulture))
                .Raw("nextEventId", cursor.ToString(CultureInfo.InvariantCulture)).Bool("historyGap", after < oldest - 1)
                .Bool("hasMore", cursor < _notificationId).Raw("events", "[" + string.Join(",", rows.ToArray()) + "]").Close();
        }
    }
}
