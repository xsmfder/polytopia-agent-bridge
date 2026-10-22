using System;
using System.Globalization;
using System.Reflection;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal sealed class IconRewardChoice
        {
            internal object Popup;
            internal MethodInfo Handler;
            internal int Id;
            internal string Text;
            internal bool? Enabled;
            internal string Json(int index)
            {
                return new JsonObj().Raw("index", index.ToString(CultureInfo.InvariantCulture))
                    .Raw("id", Id.ToString(CultureInfo.InvariantCulture)).NullOrStr("text", Text)
                    .Bool("hasCallback", true).Raw("closesPopup", "null").NullableBool("enabled", Enabled)
                    .Str("source", "native_IconRewardPopup_RewardButton")
                    .Str("handler", "RewardButtonClicked").Close();
            }
        }

        internal static bool IsIconRewardPopup(object popup)
        { return CRTryNativeCast(popup, FindGameType("IconRewardPopup")) != null; }

        internal static IconRewardChoice ReadIconRewardChoice(object popup)
        {
            popup = CRTryNativeCast(popup, FindGameType("IconRewardPopup"));
            if (popup == null) return null;
            var button = MemberWithAccessor(popup, "RewardButton");
            if (button == null || CRBool(MemberWithAccessor(MemberWithAccessor(button, "gameObject"), "activeInHierarchy")) != true)
                return null;
            var id = CRInt(MemberWithAccessor(button, "id"));
            if (!id.HasValue) return null;
            MethodInfo handler = FindPopupButtonHandler(popup.GetType(), "RewardButtonClicked");
            if (handler == null) return null;
            return new IconRewardChoice { Popup = popup, Handler = handler, Id = id.Value,
                Text = PublicText(MemberWithAccessor(button, "label"), 0),
                Enabled = CRBool(ReadBoolPropertyOrGetter(button, "ButtonEnabled")) };
        }

        private string ChooseIconReward(object popup, int wanted)
        {
            var choice = ReadIconRewardChoice(popup);
            if (choice == null || choice.Id != wanted || choice.Enabled != true)
                return UiPreparationRejection().Str("errorCode", "POPUP_BUTTON_UNAVAILABLE")
                    .Str("error", "current native reward button is absent, disabled, unknown or has another id").Close();
            try
            {
                LogUiCallback("dispatch", "IconRewardPopup.RewardButtonClicked");
                choice.Handler.Invoke(choice.Popup, new object[] { choice.Id, null });
                LogUiCallback("returned", "IconRewardPopup.RewardButtonClicked");
                return new JsonObj().Bool("ok", true).Str("rewardHandler", choice.Handler.Name)
                    .Raw("index", choice.Id.ToString(CultureInfo.InvariantCulture)).Bool("callbackAttempted", true).Bool("invokedOnRewardButtonClicked", true).Str("outcome", "invoked").Str("reobserve", "required").Close();
            }
            catch(Exception ex)
            {
                return UiCallbackFailure(new JsonObj(), "POPUP_CALLBACK_UNCERTAIN", "onRewardButtonClicked", ex);
            }
        }
    }
}
