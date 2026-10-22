// These managed tests exercise real production gates, projection, Guid adaptation and
// nonblocking Task settlement. They do not substitute for live menu/resume acceptance.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using PolytopiaAgent;

public sealed class StartScreen_UI2 { }
public sealed class GameInfoRow
{
    public object summaryViewModel;
    public Label nameLabel = new Label { text = "Saved players" }, infoLabel = new Label { text = "Turn 20" };
    public sealed class Label { public string text; }
}
public sealed class MultiplayerScreen
{
    public sealed class GameObject { public bool activeInHierarchy = true; }
    public GameObject gameObject = new GameObject();
    public List<HotseatResumeTests.Summary> Games = new List<HotseatResumeTests.Summary>();
    private List<GameInfoRow> _rows = new List<GameInfoRow>();
    public int RowReads;
    public bool ThrowRows;
    public List<GameInfoRow> rows { get { RowReads++; if (ThrowRows) throw new Exception("labels unavailable"); return _rows; } }
    public bool UseRawGames;
    public object RawGames;
    public int Reads;
    private object GetHotSeatGames() { Reads++; return UseRawGames ? RawGames : (object)Games; }
}
public sealed class InvalidSavedGameCount
{
    public object Count { get; set; }
    public object this[int index] { get { return null; } }
}
public sealed class MultiplayerSelectionScreen
{
    private enum Screens { MultiplayerScreen, ReplaysScreen }
    private MultiplayerScreen multiplayerScreen;
    private Screens currentScreenType = Screens.MultiplayerScreen;
    public MultiplayerSelectionScreen(MultiplayerScreen child) { multiplayerScreen = child; }
    public void Replay(bool replay) { currentScreenType = replay ? Screens.ReplaysScreen : Screens.MultiplayerScreen; }
}
public sealed class UIManager
{
    public static UIManager Instance { get; set; }
    public static MultiplayerScreen Games = new MultiplayerScreen();
    public static int Opens;
    public static bool FailOpen;
    public object Current = new StartScreen_UI2();
    public object GetCurrentScreen() { return Current; }
    public static void OpenMultiplayerScreen()
    { Opens++; if (FailOpen) throw new InvalidOperationException(); Instance.Current = new MultiplayerSelectionScreen(Games); }
}
public static class PopupManager
{ public static bool Showing; public static bool get_PopupShowing() { return Showing; } }
namespace Il2CppSystem
{
    public sealed class Guid
    {
        private System.Guid value;
        private Guid(System.Guid value) { this.value = value; }
        public static Guid Parse(string text) { return new Guid(System.Guid.Parse(text)); }
        public override string ToString() { return value.ToString("D"); }
    }
}

public static class HotseatResumeTests
{
    public sealed class Summary
    {
        public object GameId;
        public string State = "Started";
        public object GameSummaryData { get { throw new Exception("private binary data must not be read"); } }
        public object Participators { get { throw new Exception("private player list must not be read"); } }
    }
    public sealed class Client { public object GameState; }
    public class Manager
    {
        public static Manager instance;
        public static object GameState { get; set; }
        public Client client = new Client();
        private bool isLoadingGame;
        public int Resumes;
        public System.Guid LastId;
        public bool FailInvoke;
        public TaskCompletionSource<bool> Completion = new TaskCompletionSource<bool>();
        public void Loading(bool value) { isLoadingGame = value; }
        public Task<bool> ResumeHotseatGame(System.Guid gameId)
        { Resumes++; LastId = gameId; if (FailInvoke) throw new InvalidOperationException(); return Completion.Task; }
    }
    public sealed class NativeGuidManager
    {
        public static NativeGuidManager instance;
        public Client client = new Client();
        private bool isLoadingGame = false;
        public int Resumes;
        public string LastId;
        public TaskCompletionSource<bool> Completion = new TaskCompletionSource<bool>();
        public Task<bool> ResumeHotseatGame(Il2CppSystem.Guid gameId)
        { Resumes++; LastId = gameId.ToString(); return Completion.Task; }
    }
    public sealed class NoLoadingManager
    { public static NoLoadingManager instance = new NoLoadingManager(); public Client client = new Client(); }
    private static readonly BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static int passed;
    private static object Call(BridgeMod host, string name, params object[] args)
    { return typeof(BridgeMod).GetMethod(name, F).Invoke(host, args); }
    private static void Set(BridgeMod host, string field, object value)
    { typeof(BridgeMod).GetField(field, F).SetValue(host, value); }
    private static string Games(BridgeMod host, string op) { return (string)Call(host, "HotseatGames", op); }
    private static string Resume(BridgeMod host, System.Guid id) { return (string)Call(host, "HotseatResume", id.ToString("D")); }
    private static string Status(BridgeMod host) { return (string)Call(host, "HotseatResumeStatus"); }
    private static void Check(bool condition, string description)
    { if (!condition) throw new Exception("FAIL " + description); passed++; Console.WriteLine("PASS " + description); }
    private static BridgeMod Host(Type managerType)
    {
        var host = new BridgeMod();
        Set(host, "_gameManagerType", managerType); Set(host, "_fixedControlledSeat", (byte?)2);
        Set(host, "_gameStateProp", typeof(Manager).GetProperty("GameState"));
        return host;
    }
    private static void ResetMenu(System.Guid id)
    {
        Manager.GameState = null; Manager.instance = new Manager(); PopupManager.Showing = false;
        UIManager.Instance = new UIManager(); UIManager.Opens = 0; UIManager.FailOpen = false;
        UIManager.Games = new MultiplayerScreen();
        var summary = new Summary { GameId = id };
        UIManager.Games.Games.Add(summary);
        UIManager.Games.rows.Add(new GameInfoRow { summaryViewModel = summary });
    }
    public static int Main()
    {
        try { return Run(); }
        catch (Exception error) { while (error.InnerException != null) error = error.InnerException; Console.WriteLine(error); return 1; }
    }
    private static int Run()
    {
        System.Guid id = System.Guid.NewGuid(); ResetMenu(id); BridgeMod host = Host(typeof(Manager));
        var readList = typeof(BridgeMod).GetMethod("ReadNativeHotseatGames", F);
        foreach (object count in new object[] { false, 0.2m, "0", null })
        {
            var native = new MultiplayerScreen { UseRawGames = true, RawGames = new InvalidSavedGameCount { Count = count } };
            var args = new object[] { native, null };
            var error = readList.Invoke(host, args) as string;
            Check(error != null && error.Contains("NATIVE_LIST_UNREADABLE"), "invalid saved-game Count cannot mean no saves");
        }
        var emptyNative = new MultiplayerScreen { UseRawGames = true, RawGames = new object[0] };
        var emptyArgs = new object[] { emptyNative, null };
        Check(readList.Invoke(host, emptyArgs) == null && ((ICollection)emptyArgs[1]).Count == 0, "proved empty saved-game list remains valid");
        var labelNative = new MultiplayerScreen();
        for (int i = 0; i < 3; i++) labelNative.Games.Add(new Summary { GameId = System.Guid.NewGuid() });
        var labelArgs = new object[] { labelNative, null };
        Check(readList.Invoke(host, labelArgs) == null && labelNative.RowReads == 1, "menu labels are read once for the complete save list");
        labelNative.ThrowRows = true; labelArgs = new object[] { labelNative, null };
        Check(readList.Invoke(host, labelArgs) == null && ((ICollection)labelArgs[1]).Count == 3,
            "unreadable optional labels do not discard valid native game ids");
        Check(Games(host, "list").Contains("EXPECTED_MENU_NOT_CURRENT") && UIManager.Opens == 0 && UIManager.Games.Reads == 0,
            "read-only list at main menu neither navigates nor loads a game list");
        Check(Games(host, "open").Contains("\"callbackSubmitted\":true") && UIManager.Opens == 1,
            "explicit open invokes the native menu callback once");
        Check(Games(host, "open").Contains("alreadyOpen") && UIManager.Opens == 1,
            "open at the existing native list does not repeat navigation");
        string list = Games(host, "");
        Check(list.Contains(id.ToString("D")) && list.Contains("Saved players") && list.Contains("Turn 20")
            && !list.Contains("GameSummaryData") && !list.Contains("Participators"),
            "menu projection exposes exact ID and displayed labels without private binary or participant data");
        Check(UIManager.Instance.Current is MultiplayerSelectionScreen && UIManager.Games.Reads == 1,
            "real current outer screen resolves only its owned active MonoBehaviour game-list child");
        var selection = (MultiplayerSelectionScreen)UIManager.Instance.Current;
        int listReads = UIManager.Games.Reads;
        selection.Replay(true);
        Check(Games(host, "list").Contains("EXPECTED_MENU_NOT_CURRENT")
            && Resume(host, id).Contains("EXPECTED_MENU_NOT_CURRENT") && UIManager.Games.Reads == listReads && Manager.instance.Resumes == 0,
            "current replay page cannot read or resume the same outer's cached hotseat game list");
        selection.Replay(false); UIManager.Games.gameObject.activeInHierarchy = false;
        Check(Games(host, "list").Contains("EXPECTED_MENU_NOT_CURRENT") && Resume(host, id).Contains("EXPECTED_MENU_NOT_CURRENT")
            && UIManager.Games.Reads == listReads && Manager.instance.Resumes == 0,
            "inactive owned game-list child is neither read nor used to resume");
        UIManager.Games.gameObject = null;
        Check(Games(host, "list").Contains("EXPECTED_MENU_NOT_CURRENT") && UIManager.Games.Reads == listReads,
            "unreadable child activity cannot be assumed to be the current list");
        UIManager.Games.gameObject = new MultiplayerScreen.GameObject(); UIManager.Instance.Current = UIManager.Games;
        Check(Games(host, "list").Contains("EXPECTED_MENU_NOT_CURRENT") && UIManager.Games.Reads == listReads,
            "an unattached direct list cannot impersonate the native outer IScreen");
        UIManager.Instance.Current = selection;
        UIManager.Games.rows.Clear();
        Check(Games(host, "list").Contains("\"name\":null"), "missing rendered label remains null instead of inferred save metadata");
        Check(Resume(host, System.Guid.NewGuid()).Contains("GAME_NOT_IN_CURRENT_MENU") && Manager.instance.Resumes == 0,
            "resume requires an exact ID in the current native local list");
        Check(((string)Call(host, "HotseatResume", "latest")).Contains("INVALID_GAME_ID") && Manager.instance.Resumes == 0,
            "resume never guesses latest or accepts arbitrary file paths");
        Manager.GameState = new object();
        Check(Resume(host, id).Contains("ACTIVE_GAME_PRESERVED") && Manager.instance.Resumes == 0,
            "displayed active game is preserved even if main client has no state");
        Manager.GameState = null; Manager.instance.client.GameState = new object();
        Check(Games(host, "open").Contains("ACTIVE_GAME_PRESERVED") && Manager.instance.Resumes == 0,
            "main client active game prevents menu navigation even when displayed state is null");
        Manager.instance.client.GameState = null; Manager.instance.Loading(true);
        Check(Resume(host, id).Contains("NATIVE_LOADING_IN_PROGRESS") && Manager.instance.Resumes == 0,
            "native loading flag prevents a concurrent resume");
        Manager.instance.Loading(false); PopupManager.Showing = true;
        Check(Resume(host, id).Contains("NATIVE_POPUP_REQUIRES_ATTENTION") && Manager.instance.Resumes == 0,
            "pending native popup is not dismissed to force a resume");
        PopupManager.Showing = false; Set(host, "_fixedControlledSeat", (byte?)null);
        Check(Resume(host, id).Contains("CONTROLLED_SEAT_REQUIRED"), "unconfigured seat cannot administer a hotseat resume");
        Set(host, "_fixedControlledSeat", (byte?)2);
        string submitted = Resume(host, id);
        Check(submitted.Contains("\"phase\":\"pending\"") && submitted.Contains("\"nativeSucceeded\":null")
            && Manager.instance.Resumes == 1 && Manager.instance.LastId == id,
            "typed resume returns immediately while real Task<bool> is incomplete");
        Check(Resume(host, id).Contains("\"duplicate\":true") && Manager.instance.Resumes == 1,
            "duplicate submitted ID cannot invoke ResumeHotseatGame twice");
        System.Guid other = System.Guid.NewGuid(); UIManager.Games.Games.Add(new Summary { GameId = other });
        Check(Resume(host, other).Contains("RESUME_ALREADY_PENDING_OR_UNKNOWN") && Manager.instance.Resumes == 1,
            "another listed ID cannot replace an outstanding native Task");
        Manager.instance.Completion.SetResult(true);
        string done = Status(host);
        Check(done.Contains("\"phase\":\"native_completed\"") && done.Contains("\"nativeSucceeded\":true")
            && done.Contains("\"ready\":false"),
            "completed native success still requires separate controlled-map handoff confirmation");
        Check(Resume(host, id).Contains("\"duplicate\":true") && Manager.instance.Resumes == 1,
            "settled duplicate retains the original receipt rather than silently reloading a match");

        string firstResumeId = (string)typeof(BridgeMod).GetField("_hotseatResumeId", F).GetValue(host);
        Check(Games(host, "open").Contains("alreadyOpen") && Status(host).Contains(firstResumeId),
            "repeated open on the same game list preserves a settled receipt");
        Manager.GameState = new object(); UIManager.Instance.Current = new StartScreen_UI2();
        Check(Games(host, "open").Contains("ACTIVE_GAME_PRESERVED") && Status(host).Contains(firstResumeId),
            "an active game cannot rearm a settled resume just by opening the menu");
        Manager.GameState = null;
        Check(Games(host, "open").Contains("\"callbackSubmitted\":true") && Manager.instance.Resumes == 1,
            "explicit fresh menu opening after exit does not itself reload the saved game");
        Check(Status(host).Contains("\"phase\":\"idle\"") && !Status(host).Contains(firstResumeId),
            "fresh native start-menu cycle retires the old settled resume instead of returning it forever");
        Manager.instance.Completion = new TaskCompletionSource<bool>();
        Check(Resume(host, id).Contains("\"duplicate\":false") && Manager.instance.Resumes == 2
            && !Status(host).Contains(firstResumeId),
            "same saved game can be explicitly resumed again with a new receipt after normal menu return");
        Check(Resume(host, id).Contains("\"duplicate\":true") && Manager.instance.Resumes == 2,
            "duplicate within the new menu cycle still never replays its callback");

        // Real ReturnToMenu restores the pooled multiplayer list directly, not StartScreen.
        ResetMenu(id); host = Host(typeof(Manager)); Games(host, "open"); Resume(host, id);
        Manager.instance.Completion.SetResult(true);
        Manager.instance.client.GameState = new object();
        string activeReceipt = Status(host);
        string returnedResumeId = (string)typeof(BridgeMod).GetField("_hotseatResumeId", F).GetValue(host);
        Check(activeReceipt.Contains("native_completed") && Games(host, "open").Contains("ACTIVE_GAME_PRESERVED"),
            "successful loaded game cannot rearm before actually leaving its active state");
        Manager.instance.client.GameState = null;
        Check(Games(host, "list").Contains("menu_ready") && Status(host).Contains(returnedResumeId),
            "reading returned pooled list preserves completed receipt until explicit open");
        PopupManager.Showing = true;
        Check(Games(host, "open").Contains("NATIVE_POPUP_REQUIRES_ATTENTION") && Status(host).Contains(returnedResumeId),
            "returned-list popup cannot retire the original receipt");
        PopupManager.Showing = false; Manager.instance.Loading(true);
        Check(Games(host, "open").Contains("NATIVE_LOADING_IN_PROGRESS") && Status(host).Contains(returnedResumeId),
            "returned-list loading cannot retire the original receipt");
        Manager.instance.Loading(false);
        Check(Games(host, "open").Contains("alreadyOpen") && UIManager.Opens == 1 && Manager.instance.Resumes == 1,
            "explicit returned-list open reuses native screen without navigation or loading");
        Check(Status(host).Contains("\"phase\":\"idle\"") && !Status(host).Contains(returnedResumeId),
            "explicit open of list reached after active game retires the completed resume");
        Manager.instance.Completion = new TaskCompletionSource<bool>();
        Check(Resume(host, id).Contains("\"duplicate\":false") && Manager.instance.Resumes == 2
            && !Status(host).Contains(returnedResumeId),
            "same save actually resumes with a new request after direct return to pooled list");
        Manager.instance.Completion.SetResult(true); string newReceipt = Status(host);
        Check(Games(host, "open").Contains("alreadyOpen") && Status(host) == newReceipt,
            "repeated open without another loaded game cannot discard the new completed receipt");
        Check(Resume(host, id).Contains("\"duplicate\":true") && Manager.instance.Resumes == 2,
            "duplicate after returned-list cycle still submits only once");
        Manager.instance.client.GameState = new object();
        Call(host, "ObserveHotseatResumeActivation");
        Manager.instance.client.GameState = null;
        Check(Games(host, "open").Contains("alreadyOpen") && Status(host).Contains("\"phase\":\"idle\""),
            "bounded tick recognizes active game even without another receipt poll before return");
        Check(Manager.instance.Resumes == 2 && UIManager.Opens == 1,
            "activation sampling and explicit menu retirement never submit native callbacks");

        ResetMenu(id); host = Host(typeof(Manager)); Games(host, "open"); Manager.instance.FailInvoke = true;
        Check(Resume(host, id).Contains("NATIVE_RESUME_UNCERTAIN") && Manager.instance.Resumes == 1,
            "native invocation exception records uncertain submission");
        Manager.instance.FailInvoke = false;
        Check(Resume(host, id).Contains("\"duplicate\":true") && Manager.instance.Resumes == 1,
            "uncertain same-ID invocation is never blindly retried");
        UIManager.Games.Games.Add(new Summary { GameId = other });
        Check(Resume(host, other).Contains("RESUME_ALREADY_PENDING_OR_UNKNOWN") && Manager.instance.Resumes == 1,
            "uncertain invocation also blocks a different target game");

        ResetMenu(id); host = Host(typeof(Manager)); Games(host, "open"); Resume(host, id);
        Manager.instance.Completion.SetResult(false);
        Check(Status(host).Contains("NATIVE_RESUME_FAILED") && Status(host).Contains("\"nativeSucceeded\":false"),
            "native false result is explicit failure rather than accepted-as-success");
        Check(Resume(host, id).Contains("\"duplicate\":true") && Manager.instance.Resumes == 1,
            "known failed result is not automatically retried");
        UIManager.Instance.Current = new StartScreen_UI2(); Games(host, "open");
        Manager.instance.Completion = new TaskCompletionSource<bool>();
        Check(Resume(host, id).Contains("\"duplicate\":false") && Manager.instance.Resumes == 2,
            "an explicit fresh menu cycle can start a new attempt after a known false native result");
        ResetMenu(id); host = Host(typeof(Manager)); Games(host, "open"); Resume(host, id);
        Manager.instance.Completion.SetException(new InvalidOperationException("native task failure"));
        Check(Status(host).Contains("NATIVE_RESUME_UNCERTAIN") && Status(host).Contains("\"nativeSucceeded\":null"),
            "faulted Task does not invent a false loaded-state result");
        Check(Games(host, "list").Contains("RESUME_ALREADY_PENDING_OR_UNKNOWN"),
            "unresolved failed Task cannot be cleared by reading or reopening the menu");
        UIManager.Instance.Current = new StartScreen_UI2();
        Check(Games(host, "open").Contains("RESUME_ALREADY_PENDING_OR_UNKNOWN") && Manager.instance.Resumes == 1,
            "explicit start-menu opening cannot clear an uncertain native resume or enable a retry");
        ResetMenu(id); host = Host(typeof(Manager)); Games(host, "open"); Resume(host, id);
        Set(host, "_hotseatResumeTask", new object());
        Check(Status(host).Contains("NATIVE_TASK_STATE_UNKNOWN") && Status(host).Contains("\"nativeCompleted\":null"),
            "unreadable Task completion is explicitly uncertain rather than false or ordinary pending");
        UIManager.Games.Games.Add(new Summary { GameId = other });
        Check(Resume(host, other).Contains("RESUME_ALREADY_PENDING_OR_UNKNOWN") && Manager.instance.Resumes == 1,
            "unknown Task cannot be replaced by a new listed resume target");
        ResetMenu(id); host = Host(typeof(NoLoadingManager));
        Check(Games(host, "open").Contains("NATIVE_LOADING_STATE_UNKNOWN") && UIManager.Opens == 0,
            "unreadable native loading flag cannot default to idle");

        ResetMenu(id); NativeGuidManager.instance = new NativeGuidManager(); host = Host(typeof(NativeGuidManager));
        UIManager.Games.Games[0].GameId = Il2CppSystem.Guid.Parse(id.ToString("D"));
        Games(host, "open");
        Check(Resume(host, id).Contains("\"callbackSubmitted\":true") && NativeGuidManager.instance.Resumes == 1
            && NativeGuidManager.instance.LastId == id.ToString("D"),
            "actual method parameter type selects Il2CppSystem.Guid.Parse instead of forcing a managed Guid");
        NativeGuidManager.instance.Completion.SetResult(true);
        Check(Status(host).Contains("\"nativeSucceeded\":true"), "native-shaped Guid path uses the same nonblocking Task settlement");

        ResetMenu(id); host = Host(typeof(Manager)); Games(host, "open");
        UIManager.Games.Games.Add(new Summary { GameId = id });
        Check(Resume(host, id).Contains("NATIVE_LIST_ID_INVALID") && Manager.instance.Resumes == 0,
            "duplicated or malformed native menu IDs refuse ambiguous matching");
        Check(!BridgeMod.IsDecisionCommand("hotseat_games")
            && !BridgeMod.IsDecisionCommand("hotseat_resume")
            && !BridgeMod.IsDecisionCommand("hotseat_resume_status"),
            "administrative menu and resume commands remain outside the decision-command list");
        Check(Array.IndexOf(BridgeMod.AdapterCommands, "hotseat_games") < 0 && Array.IndexOf(BridgeMod.AdapterCommands, "hotseat_resume") < 0
            && Array.IndexOf(BridgeMod.AdapterCommands, "hotseat_resume_status") < 0,
            "normal planner adapter cannot request administrative menu or resume operations");
        Check(BridgeMod.AdapterDecision("hotseat_resume", "", "", "", "") == "deny:not_in_adapter_allowlist"
            && BridgeMod.AdapterDecision("hotseat_games", "open", "1", "", "") == "deny:audit_override_not_accepted_by_adapter",
            "actual planner adapter rejects resume and cannot unlock menu administration with audit override");
        Console.WriteLine("TOTAL " + passed + " passed"); return 0;
    }
}
