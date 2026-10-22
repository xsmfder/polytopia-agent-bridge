using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal static bool ResolvedRewardCanDismiss(bool verified, bool ownCity,
            int? pending, string expected, IEnumerable<string> chosen)
        {
            if (!verified || !ownCity || pending != 0 || string.IsNullOrEmpty(expected) || chosen == null)
                return false;
            foreach (string reward in chosen)
                if (string.Equals(reward, expected, StringComparison.Ordinal)) return true;
            return false;
        }

        // Only dismiss the display of an already committed city reward. No reward callback,
        // command, trigger mutation or global popup-stack clearing is performed here.
        private string DismissResolvedReward(string expected)
        {
            var gs = GameStateInstance();
            if (gs == null) return UiPreparationError("NO_ACTIVE_GAME", "no active game");
            var identity = ResolveControlledIdentity(gs);
            if (!identity.Verified) return UiPreparationError("CONTROLLED_IDENTITY_UNAVAILABLE", "controlled identity unavailable");
            var manager = FindType("PopupManager") ?? FindType("Il2Cpp.PopupManager");
            object popup = manager == null ? null : InvokeStatic(manager, "GetCurrentPopup");
            popup = popup == null ? null : BestPopupComponent(popup);
            if (popup == null || popup.GetType().Name != "RewardPopup")
                return UiPreparationRejection().Str("errorCode", "REWARD_DISPLAY_NOT_CURRENT").Close();
            var uiTile = MemberWithAccessor(popup, "tile");
            string coord = SafeScalar(MemberWithAccessor(uiTile, "coordinates"));
            int x, y;
            var tile = TryParseCoords(coord, out x, out y) ? TileAtCoordinates(gs, x, y) : null;
            var imp = MemberWithAccessor(tile, "improvement");
            string owner = SafeScalar(MemberWithAccessor(tile, "owner"));
            bool ownCity = owner == identity.Id.ToString(CultureInfo.InvariantCulture)
                && SafeScalar(MemberWithAccessor(imp, "type")) == "City";
            var rewards = ReadNativeList(MemberWithAccessor(imp, "rewards"));
            List<string> chosen = rewards == null ? null : new List<string>();
            if (rewards != null) foreach (var reward in rewards) chosen.Add(SafeScalar(reward));
            int? pending = identity.Verified ? PendingTriggerCountFor(gs, identity.Id, new JsonArr()) : null;
            if (!ResolvedRewardCanDismiss(identity.Verified, ownCity, pending, expected, chosen))
                return UiPreparationRejection().Str("errorCode", "REWARD_NOT_PROVEN_RESOLVED")
                    .NullOrStr("city", coord).Str("expectedReward", expected)
                    .Str("outcome", "not_dispatched").Close();
            var hide = FindVoidUiCallback(popup.GetType(), "Hide");
            if (hide == null)
                return UiPreparationRejection().Str("errorCode", "REWARD_HIDE_UNAVAILABLE").Close();
            try
            {
                LogUiCallback("dispatch", "resolved RewardPopup.Hide");
                hide.Invoke(popup, null);
                LogUiCallback("returned", "resolved RewardPopup.Hide");
                return new JsonObj().Bool("ok", true).Str("via", "resolved RewardPopup.Hide()")
                    .Str("city", coord).Str("reward", expected).Bool("dismissRequested", true)
                    .Bool("rewardResubmitted", false).Bool("callbackAttempted", true).Str("outcome", "invoked").Str("reobserve", "required").Close();
            }
            catch (Exception error)
            {
                return UiCallbackFailure(new JsonObj(), "REWARD_DISMISS_FAILED", "hide", error);
            }
        }
    }
}
