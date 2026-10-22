// Optional server-side context guard for SDK actions. Existing raw clients stay compatible.
using System;
using System.Globalization;
using System.Collections.Generic;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        // Only use at gates before command dispatch. Exceptions after a callback
        // starts cannot prove that the game did not execute the request.
        internal static JsonObj PreDispatchRejection()
        {
            return new JsonObj().Bool("ok", false).Bool("accepted", false)
                .Bool("dispatched", false).Str("outcome", "not_dispatched");
        }

        private static string ActionPreparationError(string code, string message, string schema = null)
        {
            var o = PreDispatchRejection().Str("verdict", "rejected").Str("errorCode", code).Str("error", message);
            if (schema != null) o.Str("schema", schema).Str("status", "rejected").Str("reason", message);
            return o.Close();
        }

        private static bool TryNamedNativeEnum(Type type, string name, out object value)
        {
            value = null;
            if (type == null || !type.IsEnum || string.IsNullOrEmpty(name)) return false;
            foreach (string candidate in Enum.GetNames(type))
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                { value = Enum.Parse(type, candidate); return true; }
            return false;
        }

        private static bool TryActionBoolean(string text, bool defaultValue, out bool value)
        {
            value = defaultValue;
            if (string.IsNullOrEmpty(text)) return true; // Legacy raw request default, not native data coercion.
            text = text.Trim();
            if (text == "1") { value = true; return true; }
            if (text == "0") { value = false; return true; }
            return bool.TryParse(text, out value);
        }

        private static string DefaultableActionArgument(string raw, string key)
        {
            object value; var request = BridgeJsonCodec.Object(raw);
            if (!request.TryGetValue(key, out value)) return "";
            var scalar = BridgeJsonCodec.Scalar(value);
            // Absent legacy arguments have defaults. Explicit unknown/structured
            // values must not silently turn into a selected reward or peace response.
            return string.IsNullOrEmpty(scalar) ? "<invalid action argument>" : scalar;
        }

        // Exceptions outside a command's own handler may happen after native execution.
        // Return only a newly allocated, unique ticket from THIS request and stream.
        private string UnhandledRequestFailure(Exception error, long requestId, bool routerEntered,
            string previousStream, long previousTicket, bool audit)
        {
            long? ticket = null;
            if (routerEntered && requestId > 0)
                foreach (var t in _combatTickets.Values)
                    if (t.Id > 0 && t.Stream == _combatStream && t.RequestId == requestId
                        && (t.Stream != previousStream || t.Id > previousTicket))
                    { if (ticket.HasValue) { ticket = null; break; } ticket = t.Id; }
            string code = !routerEntered && error is FormatException ? "INVALID_REQUEST_JSON" : "REQUEST_FAILED";
            var response = routerEntered ? new JsonObj().Bool("ok", false).Str("outcome", "unknown").Str("verdict", "unknown")
                : PreDispatchRejection().Str("verdict", "rejected");
            response.Str("errorCode", code).Str("error", routerEntered ? "request failed; reconcile the original request" : "request could not be parsed or routed");
            if (ticket.HasValue) response.Raw("settlementTicket", ticket.Value.ToString(CultureInfo.InvariantCulture));
            if (audit && routerEntered) response.Bool("diagnostics", true).Str("requestDetail", error.GetType().Name + ": " + error.Message);
            else response.Bool("diagnosticsWithheld", true);
            var frame = _combatFrame;
            Log("request failure #" + requestId + " phase=" + (routerEntered ? "router" : "parse")
                + " stream=" + _combatStream + " ep=" + (frame == null ? "?" : frame.Episode)
                + " player=" + (frame == null ? "?" : frame.Me) + " ticket=" + (ticket.HasValue ? ticket.ToString() : "?")
                + " code=" + code + " type=" + error.GetType().Name);
            return response.Close();
        }

        internal static bool ContextGuardedCommand(string command)
        {
            return command == "action" || command == "unit_action" || command == "tile_action"
                || command == "tribe_action" || command == "popup" || command == "end_turn"
                || command == "attack_preview" || command == "game_info" || command == "unit_properties"
                || command == "score_snapshot" || command == "threat_map" || command == "composite_preview"
                || command == "tribe_info" || command == "leaderboard";
        }

        internal static string CompareActionContext(string expectedEpisode, string expectedPlayer,
            string expectedStream, string expectedVersion, string episode, string player,
            string stream, long version, bool available)
        {
            bool any = expectedEpisode != null || expectedPlayer != null || expectedStream != null || expectedVersion != null;
            if (!any) return null; // Backward compatibility; SDK always supplies all four.
            int seat; long revision;
            if (string.IsNullOrEmpty(expectedEpisode) || string.IsNullOrEmpty(expectedStream)
                || !int.TryParse(expectedPlayer, NumberStyles.None, CultureInfo.InvariantCulture, out seat)
                || seat <= 0 || seat >= NaturePlayerId
                || !long.TryParse(expectedVersion, NumberStyles.None, CultureInfo.InvariantCulture, out revision)
                || revision <= 0)
                return "INVALID_ACTION_CONTEXT";
            if (!available || episode == null || player == null || stream == null)
                return "ACTION_CONTEXT_UNAVAILABLE";
            if (expectedEpisode != episode || seat.ToString(CultureInfo.InvariantCulture) != player
                || expectedStream != stream || revision != version)
                return "STALE_ACTION_CONTEXT";
            return null;
        }

        internal static string RequestActionContextField(Dictionary<string, object> request, string key)
        {
            object value;
            // Json.GetString intentionally maps absent fields to "" for legacy
            // callers. Here absence is optional, but a present null/empty is invalid.
            return request.TryGetValue(key, out value) ? BridgeJsonCodec.Scalar(value) ?? "" : null;
        }

        private string ValidateActionContext(string command, string raw)
        {
            if (!ContextGuardedCommand(command)) return null;
            var request = BridgeJsonCodec.Object(raw);
            string ep = RequestActionContextField(request, "expectedEpisodeId"), player = RequestActionContextField(request, "expectedPlayerId"),
                stream = RequestActionContextField(request, "expectedStreamId"), version = RequestActionContextField(request, "expectedStateVersion");
            if (ep == null && player == null && stream == null && version == null) return null;
            var gs = GameStateInstance();
            bool available = gs != null && PrivateSamplingAllowed(gs);
            if (available) CombatRuntimeTick(gs);
            var frame = _combatFrame;
            available = available && _combatCaptureAvailable && frame != null && frame.Complete;
            return CompareActionContext(ep, player, stream, version,
                available ? frame.Episode : null, available ? frame.Me : null,
                available ? _combatStream : null, _combatVersion, available);
        }
    }
}
