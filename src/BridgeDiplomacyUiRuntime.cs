using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal sealed class PeaceResponseDisplay
        {
            internal object Popup;
            internal string Episode;
            internal byte Me, Other;
            internal bool Attempted;
        }
        private readonly Dictionary<long, PeaceResponseDisplay> _peaceResponseDisplays =
            new Dictionary<long, PeaceResponseDisplay>();

        // Bind the display BEFORE dispatching the normal response command. Ambiguous
        // pending choices are left to the explicit UI path, never force-closed.
        private void CapturePeaceResponseDisplay(object gs, byte me, byte other)
        {
            if (_activeRequestId == 0 || PendingTriggerCountFor(gs, me, new JsonArr()) != 1) return;
            string reason;
            if (PublicActivePeaceTrigger(PendingTriggerList(gs), me, other, out reason) != true) return;
            var manager = FindGameType("PopupManager");
            var popup = manager == null ? null : BestPopupComponent(InvokeStatic(manager, "GetCurrentPopup"));
            if (popup == null || popup.GetType().Name != "BasicPopup") return;
            var buttons = AsEnumerable(MemberWithAccessor(popup, "buttonData"))
                ?? AsEnumerable(MemberWithAccessor(popup, "m_buttonData"));
            if (buttons == null) return;
            var labels = new List<string>();
            foreach (var button in buttons)
            {
                if (button == null || labels.Count >= 2) return;
                labels.Add(SafeScalar(MemberWithAccessor(button, "text")));
            }
            if (labels.Count != 2 || labels[0] != "friendlist.new.reject" || labels[1] != "friendlist.new.accept") return;
            _peaceResponseDisplays[_activeRequestId] = new PeaceResponseDisplay
                { Popup = popup, Episode = EpisodeId(gs), Me = me, Other = other };
            if (_peaceResponseDisplays.Count > 128)
            { var keys = new List<long>(_peaceResponseDisplays.Keys); keys.Sort(); _peaceResponseDisplays.Remove(keys[0]); }
        }

        internal static bool PeaceResponseDisplayCanDismiss(PeaceResponseDisplay display,
            PublicDiplomacyObservation proof, bool sameContext, bool samePopup,
            int? pendingMine, bool? incomingRequest)
        {
            if (display == null || display.Attempted || !sameContext || !samePopup
                || pendingMine != 0 || incomingRequest != false || proof == null
                || display.Episode != proof.Episode || display.Me != proof.Me || display.Other != proof.Other) return false;
            string basis;
            return PublicDiplomacyProofConfirmation(proof, "peace_response", out basis) == "confirmed";
        }

        internal static bool HidePeaceResponseDisplayOnce(PeaceResponseDisplay display)
        {
            if (display == null || display.Attempted || display.Popup == null) return false;
            var hide = FindVoidUiCallback(display.Popup.GetType(), "Hide");
            if (hide == null) return false;
            display.Attempted = true; // An exception is uncertain: never retry a UI callback.
            try { hide.Invoke(display.Popup, null); return true; }
            catch { return false; }
        }

        private void DismissCompletedPeaceResponseDisplay(object gs, CombatTicket ticket)
        {
            PeaceResponseDisplay display;
            PublicDiplomacyObservation proof;
            if (ticket == null || ticket.Action != "peace_response" || ticket.Accepted != true
                || !ticket.Dispatched || ticket.FinalFrame != null || ticket.Before == null
                || !_peaceResponseDisplays.TryGetValue(ticket.RequestId, out display)
                || !_publicDiplomacyProof.TryGetValue(ticket.Id, out proof) || proof.Ticket != ticket.Id) return;
            var identity = ResolveControlledIdentity(gs);
            bool sameContext = identity.Verified && identity.Id == display.Me && CurrentPlayerId(gs) == display.Me
                && ticket.Stream == _combatStream && ticket.Before.Episode == EpisodeId(gs)
                && display.Episode == ticket.Before.Episode
                && ticket.Before.Me == display.Me.ToString(CultureInfo.InvariantCulture)
                && ticket.Arg == display.Other.ToString(CultureInfo.InvariantCulture);
            if (!sameContext || display.Attempted) return;
            var manager = FindGameType("PopupManager");
            var popup = manager == null ? null : BestPopupComponent(InvokeStatic(manager, "GetCurrentPopup"));
            var ps = PlayerStateOf(gs, display.Me);
            if (!PeaceResponseDisplayCanDismiss(display, proof, sameContext, CRSameNative(display.Popup, popup),
                PendingTriggerCountFor(gs, display.Me, new JsonArr()), PublicIncomingPeaceRequest(ps, display.Other))) return;
            bool requested = HidePeaceResponseDisplayOnce(display);
            Log("peace-response display ticket=" + ticket.Id + " attempted=" + display.Attempted
                + " hideRequested=" + requested + "; response not resubmitted; settlement must reobserve pipeline");
        }
    }
}
