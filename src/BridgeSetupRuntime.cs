// Audit-only test setup: invoke the game's current menu callbacks; no fabricated match state.
using System;
using System.Reflection;
namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private bool SingleplayerResumeReady()
        {
            try
            {
                var managerType = FindGameType("UIManager");
                var manager = managerType == null ? null : InstanceOf(managerType);
                var getter = managerType == null ? null : managerType.GetMethod("GetCurrentScreen", Type.EmptyTypes);
                var current = manager == null || getter == null ? null : getter.Invoke(manager, null);
                var startType = FindGameType("StartScreen_UI2");
                var popupType = FindGameType("PopupManager");
                return current != null && startType != null && CRTryNativeCast(current, startType) != null
                    && popupType != null && StaticMember(popupType, "instance") != null;
            }
            catch { return false; }
        }

        private string NativeSetupStep(string step, string tribe, string opponentsArg = "", string mapPresetArg = "", string difficultyArg = "")
        {
            if (GameStateInstance() != null || !NoActiveDisplayedState()) return "{\"ok\":false,\"errorCode\":\"ACTIVE_GAME_PRESERVED\"}";
            var managerType = FindGameType("UIManager");
            var manager = managerType == null ? null : InstanceOf(managerType);
            var getter = managerType == null ? null : managerType.GetMethod("GetCurrentScreen", Type.EmptyTypes);
            var current = manager == null || getter == null ? null : getter.Invoke(manager, null);
            var output = new JsonObj().Bool("ok", false).Str("auditOnly", "normal current-menu callbacks");
            output.NullOrStr("currentScreen", SafeScalar(MemberWithAccessor(manager, "CurrentScreen")));
            if (step == "status")
            {
                return output.Bool("ok", true).Bool("singleplayerResumeReady", SingleplayerResumeReady()).Close();
            }
            string screenName = step == "start" ? "StartScreen_UI2"
                : step == "domination" || step == "perfection" ? "GameModeScreen_UI2"
                : step == "configure" || step == "continue" ? "GameSetupScreen_UI2" : "TribePickerScreen_UI2";
            var screenType = FindGameType(screenName);
            var screen = current == null || screenType == null ? null : CRTryNativeCast(current, screenType);
            if (screen == null) return output.Str("errorCode", "EXPECTED_MENU_NOT_CURRENT").Str("expectedScreen", screenName).Close();
            if (step == "configure")
            {
                int opponents = 0;
                if (!string.IsNullOrEmpty(opponentsArg) && (!int.TryParse(opponentsArg, out opponents) || opponents < 0 || opponents > 3))
                    return output.Str("errorCode", "INVALID_TEST_OPPONENT_COUNT").Close();
                string preset = string.IsNullOrEmpty(mapPresetArg) ? "Continents" : mapPresetArg;
                if (preset != "Continents" && preset != "Dryland" && preset != "Lakes")
                    return output.Str("errorCode", "INVALID_TEST_MAP_PRESET").Close();
                var property = _gameManagerType.GetProperty("PreliminaryGameSettings", BindingFlags.Static | BindingFlags.Public);
                var settings = property == null ? null : property.GetValue(null, null);
                if (settings == null) return output.Str("errorCode", "SETUP_SETTINGS_UNAVAILABLE").Close();
                output.NullOrStr("opponents", SetMember(settings, "OpponentCount", opponents));
                output.NullOrStr("mapWidth", SetMember(settings, "MapSize", 14));
                string wantedDifficulty=string.IsNullOrEmpty(difficultyArg) ? "Normal" : difficultyArg;
                output.Str("difficulty", SetEnumMember(settings, "Difficulty", wantedDifficulty));
                output.Str("mapPreset", SetEnumMember(settings, "mapPreset", preset));
                output.Raw("configuredOpponentCount", opponents.ToString(System.Globalization.CultureInfo.InvariantCulture));
                bool verified=SafeScalar(MemberWithAccessor(settings,"OpponentCount"))==opponents.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    && SafeScalar(MemberWithAccessor(settings,"MapSize"))=="14"
                    && string.Equals(SafeScalar(MemberWithAccessor(settings,"Difficulty")),wantedDifficulty,StringComparison.OrdinalIgnoreCase)
                    && string.Equals(SafeScalar(MemberWithAccessor(settings,"mapPreset")),preset,StringComparison.OrdinalIgnoreCase);
                return output.Bool("ok", verified).Bool("settingsVerified",verified)
                    .NullOrStr("errorCode",verified ? null : "SETUP_SETTINGS_NOT_APPLIED").Raw("actualSettings",SetupSettingsJson(settings)).Close();
            }
            string methodName = step == "start" ? "ClickNewGame"
                : step == "domination" ? "OnDomination"
                : step == "perfection" ? "OnPerfection"
                : step == "continue" ? "OnContinueClicked_StartSingleplayerGame"
                : step == "tribe" ? "OnTribeButtonClicked"
                : step == "confirm" ? "OnSelectTribeMainButtonPressed" : null;
            if (methodName == null) return output.Str("errorCode", "UNKNOWN_SETUP_STEP").Close();
            string callbackSeen;
            var method = FindInstanceMethodUnique(screenType, methodName,
                step == "tribe" ? new[] { "TribeType", "TribeType" } : new string[0], "Void", out callbackSeen);
            if (method == null || method.ContainsGenericParameters || method.ReturnType != typeof(void))
                return output.Str("errorCode", "NATIVE_MENU_CALLBACK_UNAVAILABLE").Close();
            object[] args = null;
            if (step == "tribe")
            {
                var pars = method.GetParameters();
                if (pars.Length != 2 || !pars[0].ParameterType.IsEnum || !pars[1].ParameterType.IsEnum)
                    return output.Str("errorCode", "NATIVE_TRIBE_CALLBACK_SIGNATURE_UNKNOWN").Close();
                args = new[] { Enum.Parse(pars[0].ParameterType, tribe, true), Enum.ToObject(pars[1].ParameterType, 0) };
            }
            method.Invoke(screen, args);
            return output.Bool("ok", true).Str("invoked", screenName + "." + methodName).Close();
        }
    }
}
