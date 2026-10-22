using System;
using System.Collections.Generic;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private const string DiplomacyMenuAuthority = "CommandUtils.GetDiplomacyCommandsForOpponent(includeUnavailable:false); own correlated incoming trigger for responses";

        private static List<object> PublicDiplomacyMenu(object gs, object player, byte other)
        {
            var raw = URNative("CommandUtils", "GetDiplomacyCommandsForOpponent",
                new[] { "GameState", "PlayerState", "Byte", "Boolean" }, new[] { gs, player, (object)other, false });
            return ReadNativeCommandList(raw);
        }

        // These names select execution adapters. They never determine which candidates exist.
        private static string PublicDiplomacyOperation(string type)
        {
            switch (type)
            {
                case "PeaceTreaty": return "peace_request";
                case "PeaceRequestResponse": return "peace_response";
                case "EstablishEmbassy": return "establish_embassy";
                case "BreakPeace": return "break_peace";
                case "DestroyEmbassy": return "destroy_embassy";
                default: return type == null ? null : "native:" + type;
            }
        }

        private static bool PublicDiplomacyAdapterSupported(string operation)
        { return operation == "peace_request" || operation == "peace_response" || operation == "establish_embassy"
            || operation == "break_peace" || operation == "destroy_embassy"; }

        private static string PublicDiplomacyActionOperation(string kind)
        {
            switch (kind)
            {
                case "PeaceTreatyAction": return "peace_request";
                case "PeaceRequestResponseAction": return "peace_response";
                case "EstablishEmbassyAction": return "establish_embassy";
                case "BreakPeaceAction": return "break_peace";
                case "DestroyEmbassyAction": return "destroy_embassy";
                case "ReceiveDiplomacyMessageAction": return "receive_message";
                default: return null;
            }
        }

        private static string PublicCommandCoordinateForEcho(object command, string operation)
        {
            if (PublicDiplomacyAdapterSupported(operation)) return null;
            return SafeScalar(MemberWithAccessor(command, "Target") ?? MemberWithAccessor(command, "To")
                ?? MemberWithAccessor(command, "Coordinates"));
        }

        private static object PublicDiplomacySelect(List<object> menu, string operation, byte me, byte other)
        {
            if (menu == null || !PublicDiplomacyAdapterSupported(operation) || operation == "peace_response") return null;
            object selected = null;
            foreach (var command in menu)
            {
                var type = URCommandType(command);
                if (PublicDiplomacyOperation(type) != operation) continue;
                var typed = URConcreteCommand(command, type);
                // Do not inspect or publish command Coordinates: the native UI may anchor an
                // opponent action at an undisclosed capital. Actor/target are sufficient here.
                if (PublicInt(MemberWithAccessor(typed, "PlayerId")) != me
                    || PublicInt(MemberWithAccessor(typed, "OpponentId")) != other) return null;
                if (selected != null) return null;
                selected = typed;
            }
            return selected;
        }

        private string PublicDiplomacyOperations(object gs, object player, byte me, byte other, List<object> menu)
        {
            var rows = new JsonArr();
            if (menu != null) foreach (var command in menu)
            {
                var type = URCommandType(command);
                var operation = PublicDiplomacyOperation(type);
                var typed = type == null ? command : URConcreteCommand(command, type);
                bool supported = PublicDiplomacyAdapterSupported(operation) && operation != "peace_response";
                bool matched = supported && PublicDiplomacySelect(menu, operation, me, other) != null;
                string error;
                bool? valid = PublicCommandValid(typed, gs, out error);
                rows.Raw(new JsonObj().NullOrStr("actionId", operation).NullOrStr("commandType", type)
                    .Bool("adapterSupported", supported).NullableBool("nativeValid", valid)
                    .NullableBool("canExecute", !supported || !matched ? (bool?)false : valid)
                    .Bool("bridgeQualification", matched)
                    .NullOrStr("bridgeQualificationError", !supported ? "execution/settlement adapter unavailable"
                        : !matched ? "native actor/target unavailable or candidate ambiguous" : null)
                    .NullOrStr("validationError", error).Str("qualificationAuthority", DiplomacyMenuAuthority).Close());
            }
            // A response belongs to the incoming request UI, not the ordinary opponent menu.
            string reason;
            bool? response = PublicPeaceResponseAvailable(player, PendingTriggerList(gs), me, other,
                _publicPeaceReceipts.Contains(PublicReceiptKey(gs, me, other)), out reason);
            if (response == true) foreach (bool accept in new[] { false, true })
            {
                string error;
                bool? valid = PublicCommandValid(PublicDiplomacyCommand(gs, me, other, "peace_response", accept), gs, out error);
                rows.Raw(new JsonObj().Str("actionId", "peace_response").Str("commandType", "PeaceRequestResponse")
                    .Bool("accept", accept).Bool("adapterSupported", true).NullableBool("nativeValid", valid)
                    .NullableBool("canExecute", valid).Bool("bridgeQualification", true)
                    .NullOrStr("bridgeQualificationError", null).NullOrStr("validationError", error)
                    .Str("qualificationAuthority", DiplomacyMenuAuthority).Close());
            }
            return rows.Close();
        }
    }
}
