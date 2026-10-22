// Production readers with native-shaped menus and temple growth metadata.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public class PlayerState { }
public class GameState
{
    public GrowthLogic GameLogicData { get; set; }
    public GameState() { GameLogicData = new GrowthLogic(); }
}
public class GrowthLogic
{
    public GrowthData Data = new GrowthData();
    public GrowthData GetImprovementData(string type) { return Data; }
}
public class GrowthData { public int growthRate = 3; public object growthRewards; }
public class GrowthReward { public object score = 50, population = 0; }
public class ImprovementState
{
    public string type = "Temple"; public int founded = 1, level = 1;
    public int GetAge(GameState state) { return 3; }
}
public class TileData { public ImprovementState improvement = new ImprovementState(); }
public static class ImprovementExtensions
{ public static int MaxLevel(GrowthData data, PlayerState player, GameState state) { return 4; } }
public static class ScoreExtensions
{ public static int CalculateImprovementScore(GameState state, TileData tile) { return 150; } }
public class FutureNativeCommand { public string GetCommandType() { return "FutureNativeAction"; } }
public static class CommandUtils
{
    public static object Result;
    public static int Calls;
    public static object GetDiplomacyCommandsForOpponent(GameState state, PlayerState player, byte opponent, bool includeUnavailable)
    { Calls++; return Result; }
    public static object GetImprovementAbilities(GameState state, PlayerState player, TileData tile, bool includeUnavailable)
    { Calls++; return Result; }
    public static object GetUnitActions(GameState state, PlayerState player, TileData tile, bool includeUnavailable)
    { Calls++; return Result; }
}
public class IndexedValues
{
    public object Count { get; set; }
    public List<object> Values = new List<object>();
    public int FailAt = -1;
    public object this[int i] { get { if (i == FailAt) throw new Exception("unreadable slot"); return Values[i]; } }
}
public class OverloadedValues
{
    public object Value;
    public int Count { get { return 1; } }
    public object this[int i] { get { return Value; } }
    public object this[string key] { get { throw new Exception("unrelated indexer"); } }
}
public class BrokenValues : IEnumerable
{
    public object First;
    public IEnumerator GetEnumerator() { yield return First; throw new Exception("enumeration interrupted"); }
}
internal static class MenuGrowthReadTests
{
    private const BindingFlags F = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly BridgeMod Host = new BridgeMod();
    private static readonly JavaScriptSerializer J = new JavaScriptSerializer();
    private static int passed, failed;
    private static void Check(bool value, string label)
    { if (value) { passed++; Console.WriteLine("PASS " + label); } else { failed++; Console.WriteLine("FAIL " + label); } }
    private static List<object> Menu(string name, GameState state, object raw, out bool threw)
    {
        CommandUtils.Result = raw; threw = false;
        object[] args = name == "PublicDiplomacyMenu" ? new object[] { state, new PlayerState(), (byte)3 }
            : new object[] { state, new PlayerState(), new TileData() };
        try { return (List<object>)typeof(BridgeMod).GetMethod(name, F).Invoke(Host, args); }
        catch { threw = true; return null; }
    }
    private static Dictionary<string, object> Growth(GameState state, object raw, out bool threw)
    {
        state.GameLogicData.Data.growthRewards = raw; threw = false;
        try
        {
            var json = (string)typeof(BridgeMod).GetMethod("BuildingGrowthSnapshot", F).Invoke(null,
                new object[] { state, new TileData(), new PlayerState() });
            return J.Deserialize<Dictionary<string, object>>(json);
        }
        catch { threw = true; return null; }
    }
    private static bool Status(Dictionary<string, object> row, string expected)
    { return row != null && row.ContainsKey("growthRewardsStatus") && (string)row["growthRewardsStatus"] == expected; }
    public static int Main()
    {
        var state = new GameState(); var command = new FutureNativeCommand(); bool threw;
        foreach (string name in new[] { "PublicDiplomacyMenu", "BuildingMenu", "URMenu" })
        {
            var menu = Menu(name, state, new object[0], out threw);
            Check(!threw && menu != null && menu.Count == 0, name + " preserves proven empty menu");
            menu = Menu(name, state, new object[] { command }, out threw);
            Check(!threw && menu != null && menu.Count == 1 && Object.ReferenceEquals(menu[0], command), name + " retains exact future native candidate");
            int calls = CommandUtils.Calls;
            menu = Menu(name, state, new object[] { command, null }, out threw);
            Check(!threw && menu == null, name + " null slot cannot silently shorten menu");
            Check(CommandUtils.Calls == calls + 1, name + " incomplete read does not retry native menu callback");
            menu = Menu(name, state, new BrokenValues { First = command }, out threw);
            Check(!threw && menu == null, name + " interrupted enumeration is unavailable without escaping exception");
            foreach (object bad in new object[] { false, 0.2m, "1", -1, null })
            {
                menu = Menu(name, state, new IndexedValues { Count = bad, Values = new List<object> { command } }, out threw);
                Check(!threw && menu == null, name + " rejects invalid Count " + (bad == null ? "null" : bad.ToString()));
            }
            menu = Menu(name, state, new IndexedValues { Count = 2, Values = new List<object> { command, command }, FailAt = 1 }, out threw);
            Check(!threw && menu == null, name + " unreadable index cannot claim complete prefix");
            menu = Menu(name, state, new OverloadedValues { Value = command }, out threw);
            Check(!threw && menu != null && menu.Count == 1 && Object.ReferenceEquals(menu[0], command), name + " uses int indexer despite unrelated overload");
            menu = Menu(name, state, "not a native list", out threw);
            Check(!threw && menu == null, name + " rejects raw text as command list");
        }
        var reward = new GrowthReward();
        var growth = Growth(state, new object[] { reward }, out threw);
        Check(!threw && Status(growth, "ok") && ((IList)growth["growthRewards"]).Count == 1, "complete growth table explicitly ok");
        Check(growth != null && Convert.ToInt32(growth["currentScore"]) == 150 && Convert.ToInt32(growth["level"]) == 1, "current native score and level independent from table status");
        growth = Growth(state, new object[0], out threw);
        Check(!threw && Status(growth, "ok") && ((IList)growth["growthRewards"]).Count == 0, "readable empty growth table is known empty");
        growth = Growth(state, null, out threw);
        Check(!threw && Status(growth, "unavailable") && growth["growthRewards"] == null, "missing growth table is unavailable rather than empty");
        growth = Growth(state, new BrokenValues { First = reward }, out threw);
        Check(!threw && Status(growth, "unavailable") && growth["growthRewards"] == null, "interrupted growth enumeration cannot crash whole snapshot");
        growth = Growth(state, new IndexedValues { Count = 2, Values = new List<object> { reward, reward }, FailAt = 0 }, out threw);
        Check(!threw && Status(growth, "partial") && ((IList)growth["growthRewards"]).Count == 2
            && ((IList)growth["growthRewards"])[0] == null, "unreadable indexed growth slot preserves level position and partial status");
        growth = Growth(state, new object[] { reward, null }, out threw);
        Check(!threw && Status(growth, "partial") && ((IList)growth["growthRewards"])[1] == null, "null growth entry is not a zero reward or omitted level");
        growth = Growth(state, new object[] { new GrowthReward { score = true } }, out threw);
        Check(!threw && Status(growth, "partial") && ((Dictionary<string, object>)((IList)growth["growthRewards"])[0])["score"] == null,
            "invalid reward scalar makes table partial without converting to score");
        growth = Growth(state, new object[] { new GrowthReward { score = 0, population = 0 } }, out threw);
        Check(!threw && Status(growth, "ok"), "real zero rewards remain complete");
        Check(state.GameLogicData.Data.growthRate == 3, "read-only growth queries never advance maturity");
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
