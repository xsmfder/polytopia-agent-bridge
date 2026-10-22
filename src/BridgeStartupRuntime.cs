using System;
using System.IO;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private string _startupUiPhase = "initializing";
        private readonly string _startupStarted = DateTime.UtcNow.ToString("o");
        private bool _startupUiAcknowledged;
        private long _nextStartupUiProbe;

        internal static bool PublishStartupReady(string directory, string document, int pid, bool requireOwner)
        {
            if (!TransportReadyPidMatches(document, pid)) throw new ArgumentException("ready document owner mismatch");
            if (requireOwner && !TransportOwnsReadyFileIn(directory, pid)) return false;
            string target = Path.Combine(directory, "bridge-ready.json");
            string temporary = target + "." + pid + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, document + "\n", new System.Text.UTF8Encoding(false));
                if (requireOwner && !TransportOwnsReadyFileIn(directory, pid)) return false;
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
                return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        // Startup proof uses menu presentation only, never map/player/trigger data.
        // In particular, OnInitializeMelon and a live transport are too early.
        private string ReadStartupUiPhase()
        {
            if (!_controlledSeatLoaded) return "configuration_pending";
            if (_fixedControlledSeat == 0 || _fixedControlledSeat == 255) return "configuration_invalid";
            if (!NoActiveDisplayedState()) return "active_game_or_unknown";
            bool? loading = ReadBoolMember(SafeInstance(), "isLoadingGame");
            if (!loading.HasValue) return "loading_unknown";
            if (loading.Value) return "loading";
            var fader = FindUniqueNativeMethod(FindGameType("UIBlackFader"), "IsShowing", new string[0], "Boolean", true);
            if (fader == null || fader.ReturnType != typeof(bool)) return "fader_unknown";
            if ((bool)fader.Invoke(null, null)) return "fading";
            var uiType = FindGameType("UIManager");
            object ui = uiType == null ? null : InstanceOf(uiType);
            var getter = UniqueInvocableMethod(uiType, "GetCurrentScreen", null, false);
            if (ui == null || getter == null || getter.ReturnType == typeof(void)
                || getter.ReturnType.IsValueType) return "menu_unavailable";
            object screen = CRTryNativeCast(getter.Invoke(ui, null), FindGameType("StartScreen_UI2"));
            object holder;
            // ScreenBase_UI2 is a controller, not a Component. Its inherited
            // screenHolder GameObject is what native Show/Hide activates.
            if (screen == null || !CRTryPipelineMember(screen, "screenHolder", out holder)
                || NativeBool(MemberWithAccessor(holder, "activeInHierarchy")) != true) return "menu_hidden";
            object start = MemberWithAccessor(screen, "startButton");
            if (ReadActive(start) != "true" || ReadBoolPropertyOrGetter(start, "ButtonEnabled") != true)
                return "button_unavailable";
            bool? popup = PopupIsOpen();
            if (!popup.HasValue) return "popup_unknown";
            return popup.Value ? "needs_resolution" : "start_menu";
        }

        private void ObserveStartupUiReadiness()
        {
            if (_startupUiAcknowledged || !_controlledSeatLoaded
                || !PeriodicWorkDue(ref _nextStartupUiProbe, 250) || !TransportOwnsReadyFile()) return;
            string phase;
            try { phase = ReadStartupUiPhase(); }
            catch (Exception error)
            {
                phase = "unavailable";
                if (_startupUiPhase != phase) Log("startup UI unavailable: " + error.GetType().Name);
            }
            if (phase == _startupUiPhase) return;
            string previous = _startupUiPhase;
            _startupUiPhase = phase;
            if (!WriteReadyFile(true)) { _startupUiPhase = previous; return; }
            _startupUiAcknowledged = phase == "start_menu";
            Log("startup UI phase=" + phase + " pid=" + OwnPid);
        }
    }
}
