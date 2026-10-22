using System;
using System.Collections.Generic;
using System.Globalization;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private static object ReplyField(Dictionary<string, object> data, string name)
        { object value; return data.TryGetValue(name, out value) ? value : null; }

        // Admission and visible immediate fingerprints cannot establish completion.
        // All public final fields come from ONE sample of the original ticket.
        internal static string MergeActionSettlement(string response, string settlement, long ticket)
        {
            Dictionary<string, object> reply, native;
            try { reply = BridgeJsonCodec.Object(response); native = BridgeJsonCodec.Object(settlement); }
            catch (FormatException)
            {
                return new JsonObj().Bool("ok", false).Str("errorCode", "INVALID_SETTLEMENT_RESPONSE")
                    .Str("outcome", "unknown").Str("verdict", "unknown").Str("confirmation", "unknown")
                    .Bool("actionCompleted", false).Bool("retryAllowed", false)
                    .Raw("settlementTicket", ticket.ToString(CultureInfo.InvariantCulture)).Close();
            }
            long actualTicket;
            var number = ReplyField(native, "ticketId") as BridgeJsonCodec.Number;
            bool matches = ticket > 0 && number != null
                && long.TryParse(number.Text, NumberStyles.None, CultureInfo.InvariantCulture, out actualTicket)
                && actualTicket == ticket && object.Equals(ReplyField(native, "ok"), true);
            object player = ReplyField(reply, "controlledPlayerId");
            if (player != null && !object.Equals(player, ReplyField(native, "controlledPlayerId"))) matches = false;
            string status = matches ? ReplyField(native, "settlementStatus") as string : null;
            bool complete = object.Equals(ReplyField(native, "settlementComplete"), true);
            string effect = matches ? ReplyField(native, "effectConfirmation") as string : "unknown";
            string verdict = "unknown";
            bool completed = false;
            if (status == "rejected" && !complete) verdict = "rejected";
            else if (status == "complete" && complete)
            {
                completed = effect == "confirmed" && object.Equals(ReplyField(native, "actionCompleted"), true);
                if (completed) verdict = "confirmed";
                else if (effect == "not_confirmed" && object.Equals(ReplyField(native, "actionCompleted"), false)) verdict = "not_confirmed";
            }
            else if (!complete && status == "needs_resolution") verdict = "needs_resolution";
            else if (!complete && status == "pending") verdict = "accepted_pending_confirmation";
            if (verdict != "confirmed" && verdict != "not_confirmed") effect = "unknown";
            reply["verdict"] = verdict;
            reply["confirmation"] = effect;
            reply["effectConfirmation"] = effect;
            reply["confirmationBasis"] = matches ? ReplyField(native, "confirmationBasis") : null;
            reply["actionCompleted"] = completed;
            reply["settlementComplete"] = matches ? ReplyField(native, "settlementComplete") : null;
            reply["needsRedecision"] = !completed;
            reply["reobserve"] = completed ? "optional" : "required";
            reply["outcome"] = verdict;
            reply["retryAllowed"] = false;
            reply["settlementTicket"] = new BridgeJsonCodec.Number(ticket.ToString(CultureInfo.InvariantCulture));
            reply["settlement"] = native;
            return BridgeJsonCodec.ParsedValueJson(reply);
        }
    }
}
