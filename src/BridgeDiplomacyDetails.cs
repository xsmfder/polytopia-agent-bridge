using System;
using System.Globalization;
using System.Reflection;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private object _tribeInfoOwnedPopup;
        private string _tribeInfoStamp;
        private long _tribeInfoOpenedTick;
        private string _tribeInfoViewRequestId, _tribeInfoViewOpponent, _tribeInfoViewOperation;
        private bool _tribeInfoClosing, _tribeInfoCallbackCompleted;
        private string _tribeInfoCallbackOutcome;

        private void ClearTribeInfoView()
        {
            _tribeInfoOwnedPopup = null; _tribeInfoStamp = null; _tribeInfoOpenedTick = 0;
            _tribeInfoViewRequestId = _tribeInfoViewOpponent = _tribeInfoViewOperation = null;
            _tribeInfoClosing = _tribeInfoCallbackCompleted = false; _tribeInfoCallbackOutcome = null;
        }

        private static string TribeInfoUnavailable(string reason, string requestId = null,
            string outcome = "not_dispatched")
        {
            return new JsonObj().Bool("ok", false).Str("schema", "tribe-info-1")
                .Str("status", outcome == "unknown" ? "unknown" : "unavailable").Str("reason", reason)
                .Str("outcome", outcome).Bool("callbackAttempted", outcome != "not_dispatched")
                .NullOrStr("viewRequestId", requestId).Close();
        }

        private JsonObj TribeInfoViewResult(object gs, byte other, string status, bool ok, bool confirmed)
        {
            return new JsonObj().Bool("ok", ok).Str("schema", "tribe-info-1").Str("status", status)
                .Str("episodeId", EpisodeId(gs)).Raw("controlledPlayerId", ResolveControlledIdentity(gs).Id.ToString(CultureInfo.InvariantCulture))
                .Raw("playerId", other.ToString(CultureInfo.InvariantCulture))
                .NullOrStr("viewRequestId", _tribeInfoViewRequestId).NullOrStr("viewOperation", _tribeInfoViewOperation)
                .Bool("callbackCompleted", _tribeInfoCallbackCompleted).Bool("viewEffectConfirmed", confirmed)
                .Str("outcome", confirmed ? "confirmed" : _tribeInfoCallbackOutcome ?? "unknown");
        }

        private string PollTribeInfoClose(object gs, byte other)
        {
            bool closed = _tribeInfoOwnedPopup == null || ReadActive(_tribeInfoOwnedPopup) == "false";
            if (closed)
            {
                _tribeInfoOwnedPopup = null; _tribeInfoStamp = null; _tribeInfoOpenedTick = 0;
                return TribeInfoViewResult(gs, other, "closed", true, true).Close();
            }
            return TribeInfoViewResult(gs, other, _tribeInfoCallbackCompleted ? "closing" : "unknown",
                _tribeInfoCallbackCompleted, false).Raw("details", "null")
                .Str("next", "read to check the original close attempt; Hide will not be repeated").Close();
        }

        private string TribeInfoStamp(object gs)
        {
            var id = ResolveControlledIdentity(gs);
            return EpisodeId(gs) + ":" + id.Id + ":" + SafeScalar(MemberWithAccessor(gs, "CurrentTurn"))
                + ":" + SafeScalar(MemberWithAccessor(MemberWithAccessor(gs, "CommandStack"), "Count"));
        }

        private object CurrentPlayerInfoPopup()
        {
            var manager = FindGameType("PopupManager");
            var current = manager == null ? null : InvokeStatic(manager, "GetCurrentPopup");
            var type = FindGameType("PlayerInfoPopup");
            if (current == null || type == null) return null;
            return type.IsInstanceOfType(current) ? current : GetComponentOfType(MemberWithAccessor(current, "gameObject"), type);
        }

        private string TribeInfoPublic(string opponent, string mode)
        { return TribeInfoPublicRequest(opponent, mode, null); }

        private string TribeInfoPublicRequest(string opponent, string mode, string viewRequestId)
        {
            var gs = GameStateInstance();
            if (gs == null || !PrivateSamplingAllowed(gs)) return TribeInfoUnavailable("no observable controlled game", viewRequestId);
            var identity = ResolveControlledIdentity(gs);
            if (!identity.Verified || CurrentPlayerId(gs) != identity.Id) return TribeInfoUnavailable("not controlled player's turn", viewRequestId);
            byte other;
            var known = PublicKnownPlayers(PlayerStateOf(gs, identity.Id), identity.Id);
            if (!byte.TryParse(opponent, out other) || other == identity.Id || known == null || !known.Contains(other))
                return TribeInfoUnavailable("known opponent required", viewRequestId);
            opponent = other.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(mode)) mode = "open";
            if (mode != "open" && mode != "read" && mode != "close") return TribeInfoUnavailable("mode must be open, read, or close", viewRequestId);
            if (_tribeInfoClosing && _tribeInfoViewOpponent == opponent && mode != "open")
            {
                if (mode == "close" && viewRequestId != _tribeInfoViewRequestId)
                    return TribeInfoUnavailable("close already submitted; read the original attempt", viewRequestId);
                return PollTribeInfoClose(gs, other);
            }
            var popup = CurrentPlayerInfoPopup();
            if (mode == "close")
            {
                if (_tribeInfoOwnedPopup != null && _tribeInfoViewOpponent == opponent && ReadActive(_tribeInfoOwnedPopup) == "false")
                {
                    _tribeInfoClosing = true; _tribeInfoViewOperation = "close"; _tribeInfoViewRequestId = viewRequestId;
                    _tribeInfoCallbackCompleted = false; _tribeInfoCallbackOutcome = "not_dispatched";
                    return PollTribeInfoClose(gs, other);
                }
                if (popup == null || _tribeInfoOwnedPopup == null || !CRSameNative(popup, _tribeInfoOwnedPopup)
                    || PublicInt(MemberWithAccessor(MemberWithAccessor(popup, "player"), "Id")) != other)
                    return TribeInfoUnavailable("no matching information popup opened by this interface", viewRequestId);
                MethodInfo hide = UniqueInvocableMethod(popup.GetType(), "Hide", null, false);
                if (hide == null || hide.ReturnType != typeof(void)) return TribeInfoUnavailable("native Hide callback unavailable", viewRequestId);
                _tribeInfoClosing = true; _tribeInfoViewRequestId = viewRequestId;
                _tribeInfoViewOperation = "close"; _tribeInfoViewOpponent = opponent;
                _tribeInfoCallbackCompleted = false; _tribeInfoCallbackOutcome = "unknown";
                try { hide.Invoke(popup, null); _tribeInfoCallbackCompleted = true; _tribeInfoCallbackOutcome = "invoked"; }
                catch (Exception error) { LogUiCallback("uncertain " + (error.InnerException ?? error).GetType().Name, "tribe_info.Hide"); }
                // The call may already have taken effect. Observe it, never invoke again.
                return PollTribeInfoClose(gs, other);
            }
            if (mode == "open")
            {
                if (_tribeInfoOwnedPopup != null) return TribeInfoUnavailable("information view already has an attempt; read or close it first", viewRequestId);
                var manager = FindGameType("PopupManager");
                var current = manager == null ? null : InvokeStatic(manager, "GetCurrentPopup");
                if (current != null && ReadActive(current) != "false")
                    return TribeInfoUnavailable("resolve or close the current popup first", viewRequestId);
                if (Json.GetString(CombatStateJson(gs), "settlementStatus") != "complete")
                    return TribeInfoUnavailable("game settlement is pending", viewRequestId);
                popup = manager == null ? null : InvokeStatic(manager, "GetPlayerInfoPopup");
                var player = PlayerStateOf(gs, other);
                var vectorType = FindType("UnityEngine.Vector2") ?? FindTypeBySuffix("Vector2");
                if (popup == null || player == null || vectorType == null) return TribeInfoUnavailable("native information popup unavailable", viewRequestId);
                object origin;
                try { origin = Activator.CreateInstance(vectorType); }
                catch { return TribeInfoUnavailable("native view origin unavailable", viewRequestId); }
                var setData = UniqueInvocableMethod(popup.GetType(), "SetData", new[] { player }, false);
                var show = UniqueInvocableMethod(popup.GetType(), "Show", new[] { origin }, false);
                if (setData == null || show == null || setData.ReturnType != typeof(void) || show.ReturnType != typeof(void))
                    return TribeInfoUnavailable("native information callbacks unavailable", viewRequestId);
                // This is the same native view operation as selecting a known tribe in the UI.
                // Opinion caches, localization and tech gates are updated by the game itself.
                try { setData.Invoke(popup, new[] { player }); }
                catch (Exception error)
                { LogUiCallback("uncertain " + (error.InnerException ?? error).GetType().Name, "tribe_info.SetData"); return TribeInfoUnavailable("native SetData callback failed; Show was not attempted", viewRequestId, "unknown"); }
                _tribeInfoOwnedPopup = popup; _tribeInfoStamp = TribeInfoStamp(gs);
                _tribeInfoOpenedTick = _tick;
                _tribeInfoViewRequestId = viewRequestId; _tribeInfoViewOpponent = opponent; _tribeInfoViewOperation = "open";
                _tribeInfoClosing = false; _tribeInfoCallbackCompleted = false; _tribeInfoCallbackOutcome = "unknown";
                try { show.Invoke(popup, new[] { origin }); _tribeInfoCallbackCompleted = true; _tribeInfoCallbackOutcome = "invoked"; }
                catch (Exception error) { LogUiCallback("uncertain " + (error.InnerException ?? error).GetType().Name, "tribe_info.Show"); }
                return TribeInfoViewResult(gs, other, _tribeInfoCallbackCompleted ? "opening" : "unknown", _tribeInfoCallbackCompleted, false)
                    .Raw("details", "null").Str("next", "read after the native UI has rendered a frame").Close();
            }
            popup = CurrentPlayerInfoPopup();
            if (popup == null || ReadActive(popup) != "true"
                || PublicInt(MemberWithAccessor(MemberWithAccessor(popup, "player"), "Id")) != other)
                return TribeInfoUnavailable("native view is not yet displaying this opponent; retry read");
            if (_tribeInfoStamp == null || _tribeInfoStamp != TribeInfoStamp(gs) || !CRSameNative(popup, _tribeInfoOwnedPopup))
                return TribeInfoUnavailable("view was not refreshed for the current game state; close and reopen");
            if (_tick <= _tribeInfoOpenedTick)
                return TribeInfoUnavailable("native view is waiting for its first rendered frame; retry read");
            return TribeInfoViewResult(gs, other, "ok", true, true).Raw("details", TribeInfoVisibleDetails(popup))
                .Str("authority", "currently displayed native PlayerInfoPopup; no private opinion reconstruction").Close();
        }

        private string VisibleControlText(object control)
        {
            if (control == null || ReadActive(control) != "true") return null;
            var parsed = InvokeOn(control, "GetParsedText", null) as string;
            var text = parsed ?? PublicText(control, 0);
            // Rich-text link tags can carry IDs which the rendered label anonymizes.
            return text == null ? null : System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", "");
        }

        private string TribeInfoVisibleDetails(object popup)
        {
            var slider = MemberWithAccessor(popup, "relationSlider");
            var relations = ReadNativeList(MemberWithAccessor(popup, "tribeRelations"), true);
            var rows = new JsonArr();
            bool partial = false;
            if (relations != null) foreach (var relation in relations)
            {
                if (relation == null) { partial = true; continue; }
                string active = ReadActive(relation);
                if (active == "false") continue;
                if (active != "true") { partial = true; continue; }
                string text = VisibleControlText(MemberWithAccessor(relation, "Label"))
                    ?? VisibleControlText(MemberWithAccessor(relation, "label"));
                if (text != null) rows.Raw(new JsonObj().Str("text", text).Str("authority", "native visible relation row; native anonymization preserved").Close());
                else partial = true;
            }
            return new JsonObj().NullOrStr("attitudeLabel", VisibleControlText(MemberWithAccessor(popup, "relationText")))
                .NullOrStr("attitudeReasonsText", VisibleControlText(MemberWithAccessor(popup, "opinionText")))
                .Raw("attitudeSliderValue", slider != null && ReadActive(slider) == "true" ? PublicNumber(MemberWithAccessor(slider, "value")) : "null")
                .Raw("thirdPartyRelations", relations == null ? "null" : rows.Close())
                .Str("thirdPartyRelationsStatus", relations == null ? "unavailable" : partial ? "partial" : "ok")
                .Str("scope", "only native visible controls; no hidden third-party ids or opinion weights").Close();
        }
    }
}
