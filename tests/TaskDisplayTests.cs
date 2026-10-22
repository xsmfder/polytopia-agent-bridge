using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaCityOps;

internal static class TaskDisplayTests
{
    private sealed class GameState
    { public object Players { get { throw new Exception("unneeded roster access"); } } }
    private sealed class PlayerState { public object[] tasks; }
    private enum Type { FutureTask }
    private sealed class TaskData
    { public string displayName = "Task", description = "Native description"; public object[] improvementUnlocks = new object[0]; }
    private sealed class Logic
    { public TaskData GetTaskData(Type type) { return new TaskData(); } }
    private class Task
    {
        public bool? IsCompleted = false;
        public bool IsStarted = true, Throw;
        public int Calls;
        public string Text = "3 / 10";
        public GameState SeenState; public PlayerState SeenPlayer;
        public Type GetTaskType() { return Type.FutureTask; }
        public virtual string GetCompletionStatus(GameState state, PlayerState player)
        { Calls++; SeenState = state; SeenPlayer = player; if (Throw) throw new Exception("missing text"); return Text; }
    }
    private sealed class DerivedTask : Task
    { public override string GetCompletionStatus(GameState state, PlayerState player) { Calls++; return "特殊原生文本"; } }
    private sealed class MissingTask
    { public bool IsCompleted = false, IsStarted = true; public Type GetTaskType() { return Type.FutureTask; } }
    private static int passed;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static readonly MethodInfo Reader = typeof(CityOptionsSnapshot).GetMethod("TasksJson", BindingFlags.NonPublic | BindingFlags.Static);
    private static Dictionary<string, object> Read(GameState state, PlayerState player, object logic)
    {
        string text = (string)Reader.Invoke(null, new object[] { state, player, logic, null });
        return Json.Deserialize<Dictionary<string, object>[]>(text)[0];
    }
    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    public static int Main()
    {
        var gs = new GameState(); var task = new Task(); var player = new PlayerState { tasks = new object[] { task } }; var logic = new Logic();
        var row = Read(gs, player, logic);
        Check((string)row["completionText"] == "3 / 10" && (string)row["completionTextStatus"] == "ok", "unfinished native task string returned exactly");
        Check(Object.ReferenceEquals(task.SeenState, gs) && Object.ReferenceEquals(task.SeenPlayer, player), "native progress receives the same own player and game state");
        Check(!row.ContainsKey("progress") && !row.ContainsKey("target") && !row.ContainsKey("monumentBuilt"), "text does not invent numeric progress or built status");
        task.Text = "4 / 10";
        Check((string)Read(gs, player, logic)["completionText"] == "4 / 10" && task.Calls == 2, "new observation refreshes completion text");
        task.IsCompleted = true;
        row = Read(gs, player, logic);
        Check((bool)row["completed"] && row["completionText"] == null && (string)row["completionTextStatus"] == "completed" && task.Calls == 2,
              "completed row skips native progress as the UI does");
        task.IsCompleted = null;
        Check(Read(gs, player, logic)["completionText"] == null && task.Calls == 2, "unknown completed state does not invent an unfinished task");
        task.IsCompleted = false; task.Throw = true;
        row = Read(gs, player, logic);
        Check(row["completionText"] == null && (string)row["completionTextStatus"] == "unavailable", "throwing progress remains unavailable");
        task.Throw = false; task.Text = "";
        row = Read(gs, player, logic);
        Check((string)row["completionText"] == "" && (string)row["completionTextStatus"] == "ok", "native empty text is preserved as a successful read");
        int calls = task.Calls;
        Check(Read(null, player, logic)["completionText"] == null && task.Calls == calls, "missing game state does not invoke native text");
        player.tasks = new object[] { new MissingTask() };
        Check(Read(gs, player, logic)["completionText"] == null, "missing completion method remains unknown");
        player.tasks = new object[] { new DerivedTask() };
        Check((string)Read(gs, player, logic)["completionText"] == "特殊原生文本", "runtime task override supplies its own localized text");
        row = Read(gs, player, null);
        Check(row["name"] == null && (string)row["completionText"] == "特殊原生文本", "unavailable task catalog does not erase valid native progress text");
        Console.WriteLine("PASS total " + passed); return 0;
    }
}
