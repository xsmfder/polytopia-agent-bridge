using System;
using System.Globalization;
using System.Reflection;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private object _handoffClient, _handoffOverlay;
        private string _handoffGameId, _handoffTurn, _handoffId;
        private byte? _handoffTarget;
        private bool _handoffOverlaySeen, _handoffSubmitted;
        private string _handoffInvocationError;

        internal sealed class HandoffView
        {
            internal string Phase = "identity_unknown", Id, Turn, ErrorCode, Error;
            internal byte? Target, Configured;
            internal bool? OverlayActive, OverlayPresented, ButtonActive, ButtonEnabled, CallbackPresent, NeedsDecision, Recapping;
            internal bool CanStart, Ready, Submitted;
            internal object Overlay;
            internal MethodInfo Continue;
            internal string Json(bool ok, bool accepted, bool duplicate, bool write = false)
            {
                var result = new JsonObj().Bool("ok", ok).Str("schema", "handoff-1")
                    .Str("phase", Phase).NullOrStr("handoffId", Id).NullOrStr("turn", Turn)
                    .Raw("targetPlayerId", Target.HasValue ? Target.Value.ToString(CultureInfo.InvariantCulture) : "null")
                    .Raw("configuredPlayerId", Configured.HasValue ? Configured.Value.ToString(CultureInfo.InvariantCulture) : "null")
                    .Raw("overlayActive", CRB(OverlayActive)).Raw("overlayPresented", CRB(OverlayPresented))
                    .Raw("startButtonActive", CRB(ButtonActive))
                    .Raw("startButtonEnabled", CRB(ButtonEnabled)).Raw("continueCallbackPresent", CRB(CallbackPresent))
                    .Bool("canStart", CanStart).Bool("ready", Ready).Raw("needsDecision", CRB(NeedsDecision))
                    .Raw("recapping", CRB(Recapping))
                    .Bool("callbackSubmitted", Submitted).Bool("accepted", accepted).Bool("duplicate", duplicate)
                    .NullOrStr("errorCode", ErrorCode).NullOrStr("error", Error);
                if (write)
                {
                    result.Bool("callbackAttempted", Submitted).Bool("retryAllowed", false)
                        .Str("outcome", Submitted ? (ok && accepted ? "invoked" : "unknown") : "not_dispatched");
                    if (!Submitted) result.Bool("dispatched", false).Str("verdict", "rejected");
                }
                return result.Close();
            }
        }

        // These endpoints intentionally do not call CRCapture, World, CRReadPipeline or
        // pending-trigger readers while the next player's presentation remains concealed.
        private string HandoffStatus() { return ReadHandoff().Json(true, false, false); }

        private string HandoffStart(string playerId, string expectedHandoffId)
        {
            HandoffView view = ReadHandoff();
            byte requested;
            if (!Byte.TryParse(playerId, NumberStyles.None, CultureInfo.InvariantCulture, out requested)
                || requested == 0 || requested == NaturePlayerId)
                return HandoffError(view, "INVALID_PLAYER_ID", "playerId must identify a human seat");
            if (!view.Configured.HasValue || requested != view.Configured.Value)
                return HandoffError(view, "UNCONTROLLED_SEAT", "playerId must equal the configured controlled seat");
            if (String.IsNullOrEmpty(expectedHandoffId) || expectedHandoffId != view.Id)
                return HandoffError(view, "STALE_HANDOFF", "refresh handoff_status before starting this handoff");
            // An invocation can have consumed the native delegate even if it threw. Never
            // blind-retry that invocation; observe the native state instead.
            if (view.Submitted)
            {
                if (_handoffInvocationError != null)
                {
                    view.ErrorCode = "HANDOFF_INVOCATION_UNCERTAIN"; view.Error = "native handoff callback did not complete; observe without resubmitting";
                    return view.Json(false, false, true, true);
                }
                return view.Json(true, true, true, true);
            }
            if (!view.CanStart)
                return HandoffError(view, "HANDOFF_NOT_READY", "the controlled seat's native start button is not ready");
            _handoffSubmitted = true;
            view.Submitted = true; view.CanStart = false;
            try
            {
                // Native ContinueClicked and StartButtonOnClicked share RVA 0x1B7BBA0.
                // It consumes continueCallback before invoking it. Do not call Hide,
                // SetNewLocalPlayerTurnForPassAndPlay, Resume or any game action directly.
                Log("handoff dispatch #" + _activeRequestId + " handoff=" + view.Id
                    + " ep=" + _handoffGameId + " player=" + requested + " stream=" + _combatStream);
                view.Continue.Invoke(view.Overlay, null);
                InvalidateNativeSamples();
                view.Phase = "transition";
                return view.Json(true, true, false, true);
            }
            catch (Exception error)
            {
                var cause = error.InnerException ?? error;
                _handoffInvocationError = cause.GetType().Name + ": " + cause.Message;
                view.Phase = "transition";
                Log("handoff uncertain #" + _activeRequestId + " handoff=" + view.Id + " type=" + cause.GetType().Name);
                return HandoffError(view, "HANDOFF_INVOCATION_UNCERTAIN", "native handoff callback did not complete; observe without resubmitting");
            }
        }

        private static string HandoffError(HandoffView view, string code, string detail)
        { view.ErrorCode = code; view.Error = detail; return view.Json(false, false, false, true); }

        private void ResetHandoff()
        {
            _handoffClient = _handoffOverlay = null;
            _handoffGameId = _handoffTurn = _handoffId = null;
            _handoffInvocationError = null;
            _handoffTarget = null; _handoffOverlaySeen = _handoffSubmitted = false;
        }

        private static bool? HandoffStaticBool(Type type, string name)
        {
            object value;
            return TryReadNativeStaticMember(type, name, out value) ? NativeBool(value) : null;
        }

        private object ResolveHandoffOverlay(Type type)
        {
            if (type == null) return null;
            object singleton = InstanceOf(type);
            if (singleton != null) return CRTryNativeCast(singleton, type);
            // Includes inactive scene objects during Show's transition, but requires a
            // unique typed instance. Never construct a component or guess the first object.
            object candidate = null;
            foreach (object raw in LiveInstances(type))
            {
                object typed = CRTryNativeCast(raw, type);
                if (typed == null) continue;
                if (candidate != null && !CRSameNative(candidate, typed)) return null;
                candidate = typed;
            }
            return candidate;
        }

        // IsActive is false during BOTH fade-in and fade-out. The native object stays
        // active in the hierarchy until Hide's fade-complete callback closes it. Reading
        // private data before that callback would expose a map behind the dark curtain.
        private bool HandoffPresentationInactive()
        {
            Type type = FindGameType("HotSeatOverlay");
            if (HandoffStaticBool(type, "IsActive") != false) return false;
            object overlay = ResolveHandoffOverlay(type);
            if (overlay != null) return ReadActive(overlay) == "false";
            // Native IsActive returns false when the Unity singleton is null/destroyed.
            // Prove that singleton field/getter is readable rather than treating a
            // failed instance lookup as an absent presentation.
            object singleton;
            return HandoffTryStatic(type, "instance", out singleton) && singleton == null;
        }

        private static bool HandoffTryStatic(Type type, string name, out object value)
        {
            return TryReadNativeStaticMember(type, name, out value);
        }

        private static byte? HandoffPlayerId(object player)
        {
            byte value;
            string text = SafeScalar(MemberWithAccessor(player, "Id"));
            return Byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
                && value != 0 && value != NaturePlayerId ? (byte?)value : null;
        }

        private HandoffView ReadHandoff()
        {
            var view = new HandoffView { Configured = _fixedControlledSeat };
            object client = MainClient(), gs = MainClientState();
            if (client == null || gs == null)
            { ResetHandoff(); view.Phase = "no_active_game"; return view; }
            if (SafeScalar(MemberWithAccessor(client, "clientType")) != "PassAndPlay")
            { ResetHandoff(); view.Phase = "not_hotseat"; return view; }
            if (!_fixedControlledSeat.HasValue || _fixedControlledSeat == 0 || _fixedControlledSeat == NaturePlayerId)
            { ResetHandoff(); view.Phase = "configuration_required"; return view; }
            string gameId = MainSessionGameId(client);
            if (!CRSameNative(client, _handoffClient) || gameId != _handoffGameId)
            { ResetHandoff(); _handoffClient = client; _handoffGameId = gameId; }
            view.Turn = SafeScalar(Val(_currentTurnProp, gs));
            if (SafeScalar(Val(_currentStateProp, gs)) == "Ended")
            { view.Phase = "ended"; return view; }
            if (ReadBoolPropertyOrGetter(client, "IsReplay") != false)
            { view.Phase = "identity_unknown"; return view; }

            Type overlayType = FindGameType("HotSeatOverlay");
            view.OverlayActive = HandoffStaticBool(overlayType, "IsActive");
            if (view.OverlayActive == true)
            {
                view.Overlay = ResolveHandoffOverlay(overlayType);
                view.Target = HandoffPlayerId(MemberWithAccessor(view.Overlay, "player"));
                if (!_handoffOverlaySeen || !CRSameNative(view.Overlay, _handoffOverlay)
                    || view.Target != _handoffTarget || view.Turn != _handoffTurn)
                {
                    _handoffId = "handoff-" + Guid.NewGuid().ToString("N");
                    _handoffOverlay = view.Overlay; _handoffTarget = view.Target;
                    _handoffTurn = view.Turn; _handoffSubmitted = false;
                    _handoffInvocationError = null;
                }
                _handoffOverlaySeen = true;
                view.Id = _handoffId; view.Submitted = _handoffSubmitted;
                string presented = ReadActive(view.Overlay);
                view.OverlayPresented = presented == "true" ? true : presented == "false" ? (bool?)false : null;
                object button = MemberWithAccessor(view.Overlay, "startButton");
                string active = ReadActive(button);
                view.ButtonActive = active == "true" ? true : active == "false" ? (bool?)false : null;
                view.ButtonEnabled = ReadBoolPropertyOrGetter(button, "ButtonEnabled");
                object callback;
                if (CRTryPipelineMember(view.Overlay, "continueCallback", out callback)) view.CallbackPresent = callback != null;
                try
                {
                    view.Continue = view.Overlay == null ? null : FindVoidUiCallback(view.Overlay.GetType(), "ContinueClicked");
                }
                catch { }
                view.Phase = view.Target.HasValue && view.Target != view.Configured ? "human_turn"
                    : view.Submitted ? "transition" : "handoff_overlay";
                object handoffManager = MemberWithAccessor(client, "ActionManager");
                view.CanStart = view.Target == view.Configured && view.Target.HasValue
                    && view.ButtonActive == true && view.ButtonEnabled == true && view.CallbackPresent == true
                    && view.OverlayPresented == true && view.Continue != null && !_handoffSubmitted
                    && CRSameNative(MemberWithAccessor(handoffManager, "GameState"), gs)
                    && ReadBoolPropertyOrGetter(handoffManager, "IsSimulating") == false;
                return view;
            }
            if (view.OverlayActive == null)
            { view.Phase = "identity_unknown"; return view; }
            object fadingOverlay = ResolveHandoffOverlay(overlayType);
            string fadingActivity = ReadActive(fadingOverlay);
            view.OverlayPresented = fadingActivity == "true" ? true : fadingActivity == "false" ? (bool?)false : null;
            if (!HandoffPresentationInactive())
            {
                view.Id = _handoffId; view.Submitted = _handoffSubmitted;
                view.Target = HandoffPlayerId(MemberWithAccessor(fadingOverlay, "player"));
                view.Phase = "transition";
                return view;
            }
            _handoffOverlaySeen = false;
            view.Id = _handoffId; view.Submitted = _handoffSubmitted;
            var identity = ResolveControlledIdentity(gs);
            if (!identity.Verified || identity.Id == 0)
            { view.Phase = "identity_unknown"; return view; }
            view.Target = identity.Id;
            if (identity.Id != _fixedControlledSeat.Value)
            { view.Phase = "human_turn"; return view; }
            object am = MemberWithAccessor(client, "ActionManager");
            if (ReadBoolPropertyOrGetter(client, "IsReady") != true || !PrivateSamplingAllowed(gs))
            { view.Phase = "transition"; return view; }
            bool? recapping = ReadBoolPropertyOrGetter(am, "IsRecap");
            view.Recapping = recapping;
            view.NeedsDecision = PopupIsOpen();
            if (view.NeedsDecision == true) { view.Phase = "needs_decision"; return view; }
            // A visible notification can pause the controlled player's turn recap.
            // Surface its popup first; waiting for recap completion would deadlock.
            if (recapping == true) { view.Phase = "recap"; return view; }
            bool? processing = ReadBoolPropertyOrGetter(am, "IsProcessing");
            bool? paused = ReadBoolPropertyOrGetter(am, "IsPausedOrPausing");
            bool? simulating = ReadBoolPropertyOrGetter(am, "IsSimulating");
            bool? queued = CRNoArgBool(gs, "HasQueuedCommands");
            if (processing == true || paused == true || simulating == true || queued == true)
            { view.Phase = "settling"; return view; }
            if (processing == false && paused == false && simulating == false && queued == false
                && recapping == false && view.NeedsDecision == false)
            {
                byte acting;
                if (!Byte.TryParse(SafeScalar(MemberWithAccessor(gs, "CurrentPlayer")), NumberStyles.None,
                    CultureInfo.InvariantCulture, out acting))
                { view.Phase = "identity_unknown"; return view; }
                if (acting != identity.Id) { view.Phase = "transition"; return view; }
                view.Phase = "map_ready"; view.Ready = true; return view;
            }
            view.Phase = "identity_unknown";
            return view;
        }
    }
}
