// Native-shaped read-only menu fixtures; no game process or main-loop writes.
using System;
using System.IO;
using System.Reflection;
using PolytopiaAgent;

public class StartupManager
{
    public static StartupManager instance = new StartupManager();
    public static object GameState { get; set; }
    public bool isLoadingGame;
}
public class UiObject { public bool activeInHierarchy = true; }
public class UiButton
{
    public UiObject gameObject = new UiObject();
    public bool Enabled = true;
    public bool ButtonEnabled { get { return Enabled; } }
}
public class ScreenBase_UI2
{
    private UiObject screenHolder = new UiObject();
    public void SetVisible(bool visible) { screenHolder.activeInHierarchy = visible; }
}
public class StartScreen_UI2 : ScreenBase_UI2
{
    public UiButton startButton = new UiButton();
    public void ClickNewGame() { throw new Exception("startup must not press anything"); }
}
public class UIManager
{
    public static UIManager Instance { get; set; }
    static UIManager() { Instance = new UIManager(); }
    public object Current;
    public int Reads;
    public object GetCurrentScreen() { Reads++; return Current; }
    public object GetCurrentScreen<T>() { throw new Exception("unbound generic getter"); }
}
public static class UIBlackFader
{
    public static bool Showing;
    public static bool IsShowing() { return Showing; }
}
public static class PopupManager
{
    public static object Showing = false;
    public static object get_PopupShowing() { return Showing; }
}

internal static class StartupUiTests
{
    private static int passed;
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BridgeMod Host = new BridgeMod();
    private static void Set(string name, object value) { typeof(BridgeMod).GetField(name, F).SetValue(Host, value); }
    private static string Phase() { return (string)typeof(BridgeMod).GetMethod("ReadStartupUiPhase", F).Invoke(Host, null); }
    private static void Check(bool ok, string label) { if (!ok) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    public static int Main()
    {
        string directory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "startup-ui-" + Guid.NewGuid().ToString("N")));
        try
        {
            Set("_gameManagerType", typeof(StartupManager));
            Set("_gameStateProp", typeof(StartupManager).GetProperty("GameState"));
            Set("_fixedControlledSeat", (byte?)2);
            var screen = new StartScreen_UI2(); UIManager.Instance.Current = screen;
            Check(Phase() == "configuration_pending" && UIManager.Instance.Reads == 0, "seat load precedes any menu probe");
            Set("_controlledSeatLoaded", true);
            Check(Phase() == "start_menu", "actual active native menu and enabled start button establish readiness");
            StartupManager.instance.isLoadingGame = true;
            Check(Phase() == "loading", "live loading flag keeps startup pending");
            StartupManager.instance.isLoadingGame = false; UIBlackFader.Showing = true;
            Check(Phase() == "fading", "black fade keeps startup pending");
            UIBlackFader.Showing = false; screen.SetVisible(false);
            Check(Phase() == "menu_hidden", "inactive menu cannot acknowledge startup");
            screen.SetVisible(true); screen.startButton.Enabled = false;
            Check(Phase() == "button_unavailable", "disabled start control cannot acknowledge startup");
            screen.startButton.Enabled = true; screen.startButton.gameObject.activeInHierarchy = false;
            Check(Phase() == "button_unavailable", "inactive start control cannot acknowledge startup");
            screen.startButton.gameObject.activeInHierarchy = true; PopupManager.Showing = true;
            Check(Phase() == "needs_resolution", "blocking popup is reported without choosing it");
            PopupManager.Showing = 0;
            Check(Phase() == "popup_unknown", "numeric popup state cannot be coerced into ready");
            PopupManager.Showing = "false";
            Check(Phase() == "popup_unknown", "string popup state remains unreadable");
            PopupManager.Showing = false; StartupManager.GameState = new object(); int reads = UIManager.Instance.Reads;
            Check(Phase() == "active_game_or_unknown" && UIManager.Instance.Reads == reads,
                "already active state prevents startup menu proof without reading player/map data");
            StartupManager.GameState = null; Set("_fixedControlledSeat", (byte?)255);
            Check(Phase() == "configuration_invalid", "invalid explicit seat cannot publish menu readiness");
            Set("_fixedControlledSeat", (byte?)2); UIManager.Instance.Current = new object();
            Check(Phase() == "menu_hidden", "another screen cannot stand in for the start menu");
            Directory.CreateDirectory(directory);
            string initializing = BridgeMod.StartupReadyJson("cmd", "out", 123, "same-session", 2, true);
            string ready = BridgeMod.StartupReadyJson("cmd", "out", 123, "same-session", 2, true, "start_menu");
            Check(BridgeMod.PublishStartupReady(directory, initializing, 123, false), "initial transport metadata publishes a complete document");
            Check(BridgeMod.PublishStartupReady(directory, ready, 123, true)
                && File.ReadAllText(Path.Combine(directory, "bridge-ready.json")).Trim() == ready,
                "owned UI transition atomically replaces the document and preserves the start identity");
            Check(!BridgeMod.PublishStartupReady(directory, initializing.Replace("123", "456"), 456, true)
                && File.ReadAllText(Path.Combine(directory, "bridge-ready.json")).Trim() == ready,
                "foreign owner cannot overwrite a menu proof");
            Check(Directory.GetFiles(directory).Length == 1, "metadata publication leaves no staging files");
            Console.WriteLine("ALL PASS " + passed); return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
        finally
        {
            string readyFile = Path.Combine(directory, "bridge-ready.json");
            if (File.Exists(readyFile)) File.Delete(readyFile);
            if (Directory.Exists(directory)) Directory.Delete(directory, false);
        }
    }
}
