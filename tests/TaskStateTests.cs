using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaCityOps;

internal static class TaskStateTests
{
    private sealed class GameState
    { public object PlayerStates { get { throw new Exception("must not read opponent roster"); } } }
    private sealed class PlayerState
    {
        public object[] tasks;
        public object Built = new object[0];
        public bool ThrowBuilt;
        public object builtUniqueImprovements
        { get { if (ThrowBuilt) throw new Exception("missing native record"); return Built; } }
    }
    private enum Type { NewTask }
    private enum ImprovementType { FutureMonument, AnotherReward }
    private sealed class ImprovementData
    { public object type; public ImprovementData(object value) { type = value; } }
    private sealed class TaskData
    {
        public string displayName = "Native task", description = "Native condition";
        public object improvementUnlocks = new object[] { new ImprovementData(ImprovementType.FutureMonument) };
    }
    private sealed class Logic
    {
        public TaskData Data = new TaskData(); public bool Throw;
        public TaskData GetTaskData(Type type)
        { if (Throw) throw new Exception("unavailable catalog"); return Data; }
    }
    private sealed class Task
    {
        public bool? IsCompleted = false;
        public bool IsStarted = true;
        public string Text = "native status";
        public int Calls;
        public Type GetTaskType() { return Type.NewTask; }
        public string GetCompletionStatus(GameState gs, PlayerState ps) { Calls++; return Text; }
    }
    private static int passed;
    private static readonly MethodInfo Reader = typeof(CityOptionsSnapshot).GetMethod("TasksJson", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static object[] unlocked = new object[0];
    private static Dictionary<string, object> Read(PlayerState ps, Logic logic)
    {
        string text = (string)Reader.Invoke(null, new object[] { new GameState(), ps, logic,
            unlocked == null ? null : new List<object>(unlocked) });
        return Json.Deserialize<Dictionary<string, object>[]>(text)[0];
    }
    private static Dictionary<string, object> Reward(Dictionary<string, object> row, int index)
    { return (Dictionary<string, object>)((IList)row["rewardImprovements"])[index]; }
    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    public static int Main()
    {
        var task = new Task(); var player = new PlayerState { tasks = new object[] { task } }; var logic = new Logic();
        var row = Read(player, logic); var reward = Reward(row, 0);
        Check((string)reward["improvement"] == "FutureMonument", "reward types come from native task catalog without fixed list");
        Check(!(bool)row["completed"] && !(bool)reward["currentlyUnlocked"] && !(bool)reward["uniqueBuildRecorded"], "unfinished locked and never built are separate native values");
        Check((string)((IList)row["improvementUnlocks"])[0] == "FutureMonument", "legacy reward definition list is preserved");
        Check((string)row["completionText"] == "native status", "original native progress text is preserved");
        task.IsCompleted = true;
        reward = Reward(Read(player, logic), 0);
        Check(!(bool)reward["currentlyUnlocked"] && !(bool)reward["uniqueBuildRecorded"], "completion alone does not grant an unlock or built record");
        Check(task.Calls == 1, "completed task never computes extra progress");
        unlocked = new object[] { new ImprovementData(ImprovementType.FutureMonument) };
        reward = Reward(Read(player, logic), 0);
        Check((bool)reward["currentlyUnlocked"] && !(bool)reward["uniqueBuildRecorded"], "completed unlocked reward is not declared built");
        player.Built = new object[] { ImprovementType.FutureMonument };
        reward = Reward(Read(player, logic), 0);
        Check((bool)reward["currentlyUnlocked"] && (bool)reward["uniqueBuildRecorded"], "native unlock membership and build record can both be true");
        unlocked = new object[0];
        reward = Reward(Read(player, logic), 0);
        Check(!(bool)reward["currentlyUnlocked"] && (bool)reward["uniqueBuildRecorded"], "built record survives native unlocked-list removal without deriving completion");
        task.IsCompleted = null;
        row = Read(player, logic);
        Check(row["completed"] == null && (bool)Reward(row, 0)["uniqueBuildRecorded"], "built record does not manufacture completion when completion unreadable");
        Check(!Reward(row, 0).ContainsKey("currentlyBuilt") && !row.ContainsKey("monumentBuilt"), "historical unique record is not claimed as present map building");
        Check(((string)row["rewardImprovementsAuthority"]).Contains("not current map presence"), "record authority explicitly distinguishes historical record from current presence");
        unlocked = null;
        Check(Reward(Read(player, logic), 0)["currentlyUnlocked"] == null, "unreadable native unlock list stays unknown");
        player.Built = null;
        Check(Reward(Read(player, logic), 0)["uniqueBuildRecorded"] == null, "missing build record stays unknown");
        player.Built = new object[0]; player.ThrowBuilt = true;
        Check(Reward(Read(player, logic), 0)["uniqueBuildRecorded"] == null, "throwing build record stays unknown");
        player.ThrowBuilt = false;
        unlocked = new object[] { null, new ImprovementData(ImprovementType.AnotherReward) };
        player.Built = new object[] { null, ImprovementType.AnotherReward };
        reward = Reward(Read(player, logic), 0);
        Check(reward["currentlyUnlocked"] == null && reward["uniqueBuildRecorded"] == null, "partially unreadable lists do not prove absence");
        unlocked = new object[] { null, new ImprovementData(ImprovementType.FutureMonument) };
        player.Built = new object[] { null, ImprovementType.FutureMonument };
        reward = Reward(Read(player, logic), 0);
        Check((bool)reward["currentlyUnlocked"] && (bool)reward["uniqueBuildRecorded"], "positive membership remains valid in partial lists");
        logic.Data.improvementUnlocks = new object[] { new ImprovementData(ImprovementType.FutureMonument), new ImprovementData(ImprovementType.AnotherReward) };
        unlocked = new object[] { new ImprovementData(ImprovementType.AnotherReward) }; player.Built = new object[] { ImprovementType.FutureMonument };
        row = Read(player, logic);
        Check(!(bool)Reward(row, 0)["currentlyUnlocked"] && (bool)Reward(row, 0)["uniqueBuildRecorded"]
            && (bool)Reward(row, 1)["currentlyUnlocked"] && !(bool)Reward(row, 1)["uniqueBuildRecorded"], "multiple dynamic task rewards have independent states");
        logic.Data.improvementUnlocks = new object[] { new ImprovementData(null) };
        reward = Reward(Read(player, logic), 0);
        Check(reward["improvement"] == null && reward["currentlyUnlocked"] == null && reward["uniqueBuildRecorded"] == null, "unreadable reward identity remains unknown");
        logic.Data.improvementUnlocks = null;
        Check(Read(player, logic)["rewardImprovements"] == null, "unreadable task reward definition is not an empty reward set");
        logic.Data.improvementUnlocks = new object[0];
        Check(((IList)Read(player, logic)["rewardImprovements"]).Count == 0, "readable zero rewards is an empty set");
        logic.Throw = true;
        Check(Read(player, logic)["rewardImprovements"] == null, "throwing task catalog does not produce reward guesses");
        Console.WriteLine("PASS total " + passed); return 0;
    }
}
