using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private static JsonObj UiPreparationRejection()
        { return PreDispatchRejection().Str("verdict", "rejected").Bool("callbackAttempted", false); }

        private static string UiPreparationError(string code, string message)
        { return UiPreparationRejection().Str("errorCode", code).Str("error", message).Close(); }

        private void LogUiCallback(string phase, string target)
        {
            var frame = _combatFrame;
            Log("ui " + phase + " #" + _activeRequestId + " ep=" + (frame == null ? "?" : frame.Episode)
                + " player=" + (frame == null ? "?" : frame.Me) + " stream=" + _combatStream + " target=" + target);
        }

        private string UiCallbackFailure(JsonObj result, string code, string stage, Exception error)
        {
            var cause = error.InnerException ?? error;
            LogUiCallback("uncertain " + cause.GetType().Name, stage);
            result.Bool("ok", false).Str("errorCode", code).Str("outcome", "unknown")
                .Bool("callbackAttempted", true).Bool("retryAllowed", false)
                .Str("error", "native UI callback did not complete; reobserve without resubmitting");
            if (_activeAuditRequested) result.Str(stage + "Error", cause.GetType().Name + ": " + cause.Message);
            else result.Bool("diagnosticsWithheld", true);
            return result.Close();
        }

        // All supported button handlers are void(int, BaseEventData). Do not
        // manufacture defaults for another overload or invoke open generics.
        private static MethodInfo FindPopupButtonHandler(Type type, string name)
        {
            var method = FindUniqueNativeMethod(type, name, new[] { "Int32", "BaseEventData" }, "Void", false);
            if (method == null || method.ReturnType != typeof(void)) return null;
            var p = method.GetParameters();
            return p[0].ParameterType == typeof(int) && !p[1].ParameterType.IsValueType ? method : null;
        }

        private static bool PopupChoiceEnabled(object data)
        {
            object state = MemberWithAccessor(data, "state");
            return state != null && state.GetType().IsEnum && Enum.IsDefined(state.GetType(), state)
                && !string.Equals(state.ToString(), "Disabled", StringComparison.Ordinal);
        }

        private bool? SameCurrentPopup(Type manager, object popup)
        {
            object current = InvokeStatic(manager, "GetCurrentPopup");
            if (current == null) return null; // Absent and unreadable both prohibit another callback.
            current = BestPopupComponent(current);
            return CRSameNative(current, popup);
        }

        // Native id is the public choice identity. An ordinal must never select
        // a different id. After any callback starts, exceptions stop the chain.
        private string PopupChoose(string idArg)
        {
            var manager = FindGameType("PopupManager");
            if (manager == null) return UiPreparationError("POPUP_MANAGER_UNAVAILABLE", "native popup manager unavailable");
            object current;
            if (!TryReadCurrentPopup(out current)) return UiPreparationError("POPUP_READ_UNAVAILABLE", "current popup unreadable");
            if (current == null) return UiPreparationError("POPUP_NOT_CURRENT", "no readable current popup component");
            int wanted;
            if (string.IsNullOrEmpty(idArg) || !int.TryParse(idArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out wanted))
            {
                if (!string.IsNullOrEmpty(idArg) && current.GetType().Name == "RewardPopup")
                    return ChooseReward(current, idArg);
                return UiPreparationError("INVALID_POPUP_CHOICE", "choose requires a current native button id");
            }
            if (IsIconRewardPopup(current)) return ChooseIconReward(current, wanted);
            var snapshot = ReadPopupButtons(current);
            if (!snapshot.Complete) return UiPreparationError("POPUP_BUTTONS_UNAVAILABLE", "current native button identities incomplete");
            var datas = snapshot.Data;
            object chosen = null; int ordinal = -1;
            for (int i = 0; i < datas.Count; i++)
            {
                int? id = NativeInt32(MemberWithAccessor(datas[i], "id"));
                if (!id.HasValue) return UiPreparationError("POPUP_BUTTONS_UNAVAILABLE", "current native button identity unreadable");
                if (id.Value != wanted) continue;
                if (chosen != null) return UiPreparationError("POPUP_BUTTON_AMBIGUOUS", "native button id is not unique");
                chosen = datas[i]; ordinal = i;
            }
            if (chosen == null) return UiPreparationError("POPUP_BUTTON_UNAVAILABLE", "no current native button has this id");
            if (!PopupChoiceEnabled(chosen)) return UiPreparationError("POPUP_BUTTON_UNAVAILABLE", "native button state is disabled or unreadable");
            object callback;
            if (!CRTryPipelineMember(chosen, "callback", out callback))
                return UiPreparationError("POPUP_CALLBACK_UNAVAILABLE", "native button callback unreadable");
            var invoke = callback == null ? null : FindVoidUiCallback(callback.GetType(), "Invoke");
            if (callback != null && invoke == null)
                return UiPreparationError("POPUP_CALLBACK_UNAVAILABLE", "native button callback signature unavailable");
            bool? closes = CRBool(MemberWithAccessor(chosen, "closesPopup"));
            var close = closes == true ? FindPopupButtonHandler(current.GetType(), "OnHide") : null;
            if (close == null && closes == true) close = FindVoidUiCallback(current.GetType(), "Hide");
            if (callback == null && (closes != true || close == null))
                return UiPreparationError("POPUP_CALLBACK_UNAVAILABLE", "native button has no readable callback or closing handler");
            var result = new JsonObj().Str("popupType", current.GetType().Name)
                .Raw("wanted", wanted.ToString(CultureInfo.InvariantCulture))
                .Raw("ordinal", ordinal.ToString(CultureInfo.InvariantCulture))
                .Raw("button", ButtonDataJson(chosen).Close()).NullableBool("closesPopup", closes);
            var tried = new JsonArr(); bool invoked = false;
            // Native getters/localization may refresh or replace a view while reading its
            // button details. Recheck after those reads, before the first callback or cleanup.
            if (NativeInt32(MemberWithAccessor(chosen, "id")) != wanted || !PopupChoiceEnabled(chosen))
                return UiPreparationError("POPUP_BUTTON_UNAVAILABLE", "native button identity or state changed before invocation");
            if (SameCurrentPopup(manager, current) != true)
                return UiPreparationError("POPUP_CHANGED", "native popup changed before invocation");
            if (callback != null)
            {
                LogUiCallback("dispatch", current.GetType().Name + ".callback");
                try { invoke.Invoke(callback, null); }
                catch (Exception error) { return UiCallbackFailure(result, "POPUP_CALLBACK_UNCERTAIN", "callback", error); }
                invoked = true;
                result.Bool("callbackAttempted", true).Bool("invokedCallback", true); tried.Raw("\"callback\"");
                LogUiCallback("returned", current.GetType().Name + ".callback");
            }
            // A successful callback may intentionally keep the popup open or show a
            // replacement. Native closesPopup=true and the SAME current popup are
            // required before one closing handler, never a cascade of fallbacks.
            if (closes == true && SameCurrentPopup(manager, current) == true)
            {
                if (close == null)
                    return result.Bool("ok", false).Str("errorCode", "POPUP_CLOSE_UNAVAILABLE")
                        .Str("outcome", "unknown").Bool("retryAllowed", false).Close();
                LogUiCallback("dispatch", current.GetType().Name + "." + close.Name);
                try { close.Invoke(current, close.Name == "OnHide" ? new object[] { wanted, null } : null); }
                catch (Exception error) { return UiCallbackFailure(result, "POPUP_CALLBACK_UNCERTAIN", close.Name == "OnHide" ? "onHide" : "hide", error); }
                invoked = true;
                result.Bool("callbackAttempted", true).Bool(close.Name == "OnHide" ? "invokedOnHide" : "invokedHide", true);
                tried.Raw("\"" + Esc(close.Name) + "\""); LogUiCallback("returned", current.GetType().Name + "." + close.Name);
            }
            if (!invoked) return UiPreparationError("POPUP_CHANGED", "native popup changed before invocation");
            result.Raw("tried", tried.Close()).Bool("ok", true).Bool("callbackCompleted", true)
                .Str("outcome", "invoked").Str("reobserve", "required");
            return result.Close();
        }
    }
}
