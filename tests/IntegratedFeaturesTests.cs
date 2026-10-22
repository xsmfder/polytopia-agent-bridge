using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

public class CommandBase { }
public class BreakIceCommand : CommandBase
{
    public string Coordinates { get; set; }
    public string GetCommandType() { return "BreakIce"; }
}
public class OpaqueCommand : CommandBase { public object Path = new object(); }
namespace Il2CppInterop.Runtime.InteropTypes { public class Il2CppObjectBase { } }
namespace Il2CppInterop.Runtime
{
    public static class IL2CPP
    {
        public static uint il2cpp_field_get_flags(IntPtr field) { return field.ToInt64() == 1 ? 1u : 6u; }
        public static uint il2cpp_method_get_flags(IntPtr method, ref uint implementationFlags) { implementationFlags = 0; return 6u; }
    }
}
public class NativeMenuCommand : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
{
    private static IntPtr NativeFieldInfoPtr_hiddenCache = new IntPtr(1);
    private static IntPtr NativeFieldInfoPtr_visibleParameter = new IntPtr(2);
    private static IntPtr NativeMethodInfoPtr_get_Coordinates_Public_get_String_0 = new IntPtr(3);
    public int hiddenCache { get { throw new Exception("private native getter must not be called"); } }
    public int visibleParameter { get { return 4; } }
    public string Coordinates { get { return "(2,3)"; } }
}
public class MissingNativeMetadataCommand : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
{
    public int Secret { get { throw new Exception("unverified native getter must not be called"); } }
}
public class PlayerState
{
    public byte Id = 1;
    public string tribe = "Imperius";
    public List<int> availableTech = new List<int> { 0, 3 };
    public List<int> unlockedTechCache = new List<int> { 77 };
}
public class GameState
{
    public int CurrentTurn = 20;
    public GrowthLogic GameLogicData { get; set; }
    public GameState() { GameLogicData = new GrowthLogic(); }
}
public class GrowthReward { public int score = 50, population = 0; }
public class ImprovementData
{
    public int growthRate = 3;
    public List<GrowthReward> growthRewards = new List<GrowthReward> { new GrowthReward() };
}
public class GrowthLogic
{
    public ImprovementData Data = new ImprovementData();
    public ImprovementData GetImprovementData(string type) { return Data; }
}
public class ImprovementState
{
    public string type = "Temple";
    public int founded = 18, level = 0;
    public int GetAge(GameState gs) { return gs.CurrentTurn - founded; }
}
public class TileData { public ImprovementState improvement = new ImprovementState(); }
public static class ImprovementExtensions
{
    public static int MaxLevel(ImprovementData data, PlayerState player, GameState gs) { return 4; }
}
public static class ScoreExtensions
{
    public static int CalculateImprovementScore(GameState gs, TileData tile) { return 100 + 50 * tile.improvement.level; }
}
public class ScoreDetails
{
    public int territoryScore = 10, cultureScore = 100, cityScore = 20, techScore = 30, totalScore = 160;
    public double difficultyBonusMultiplier = 0.25;
    public int difficultyBonus = 40;
    public int TotalScoreIncludingBonus { get { return 200; } }
}
public class PlayerRating
{
    public float battleScore = 75, wipeScore = 100, timeGoal = 20, timeScore = 50, difficultyScore = 90, averageRating = 78.75f;
}
public static class PlayerExtensions
{
    public static PlayerState LastPlayer;
    public static bool LastWin;
    public static ScoreDetails GetScore(PlayerState p, GameState gs)
    { LastPlayer = p; p.unlockedTechCache.Clear(); p.availableTech.Remove(0); return new ScoreDetails(); }
    public static PlayerRating GetRating(PlayerState p, GameState gs, bool won)
    { LastWin = won; return new PlayerRating { timeScore = won ? 50 : 0 }; }
}
public class UIObject { public bool activeInHierarchy = true; }
public class UIText
{
    public UIObject gameObject = new UIObject();
    public string text;
    public UIText(string value, bool active) { text = value; gameObject.activeInHierarchy = active; }
}
public class UISlider
{
    public UIObject gameObject = new UIObject();
    public int value = 99;
}
public class RelationRow
{
    public UIObject gameObject = new UIObject();
    public UIText Label;
    public RelationRow(string text, bool active) { Label = new UIText(text, active); gameObject.activeInHierarchy = active; }
}
public class InfoPopup
{
    public UIText relationText = new UIText("Friendly", true);
    public UIText opinionText = new UIText("Shared enemy\nEmbassy", true);
    public UISlider relationSlider = new UISlider();
    public List<RelationRow> tribeRelations = new List<RelationRow> {
        new RelationRow("<link=123>Unknown tribe</link> has peace with Imperius", true),
        new RelationRow("Hidden native row must stay hidden", false) };
}
internal static class IntegratedFeaturesTests
{
    private static Type T;
    private static int passed;
    private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static object Call(string name, params object[] args) { return T.GetMethod(name, Flags).Invoke(null, args); }
    private static Dictionary<string, object> Parse(string json) { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); }
    private static void Check(bool good, string label) { if (!good) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static string Confirm(string before, string after, string action)
    { var args = new object[] { "1", action, null, "(2,3)", "", before, after, null }; return (string)Call("ConfirmAction", args); }
    private static string F(string imp, string terrain, string player)
    { return "player=" + player + "|units=|imps=" + imp + (terrain == null ? "" : "|terrains=" + terrain); }
    public static int Main(string[] args)
    {
        try
        {
            T = Assembly.LoadFrom(args[0]).GetType("PolytopiaAgent.BridgeMod", true);
            object[] read = { new BreakIceCommand { Coordinates = "(2,3)" }, null };
            string parameters = (string)Call("URCommandParameters", read);
            Check((bool)read[1] && Parse(parameters)["Coordinates"].ToString() == "(2,3)", "native target parameters preserved");
            var menu = new List<object> { new BreakIceCommand { Coordinates = "(2,3)" }, new BreakIceCommand { Coordinates = "(3,3)" } };
            string id = (string)Call("URMenuActionId", "BreakIce", null, parameters, menu);
            object[] second = { menu[1], null };
            string other = (string)Call("URCommandParameters", second);
            Check(id != (string)Call("URMenuActionId", "BreakIce", null, other, menu), "two adjacent targets have distinct action IDs");
            Check(id == (string)Call("URMenuActionId", "BreakIce", null, parameters, menu), "action ID remains stable across menu reads");
            object[] opaque = { new OpaqueCommand(), null }; Call("URCommandParameters", opaque);
            Check(!(bool)opaque[1], "unreadable compound parameters cannot be dispatched as self-action");
            object[] missing = { new CommandBase(), null }; Call("URCommandParameters", missing);
            Check(!(bool)missing[1], "uncast base wrapper is explicitly unreadable");
            object[] native = { new NativeMenuCommand(), null };
            var nativeParameters = Parse((string)Call("URCommandParameters", native));
            Check((bool)native[1] && !nativeParameters.ContainsKey("hiddenCache"), "native private fields are excluded before invoking interop public getters");
            Check(Convert.ToInt32(nativeParameters["visibleParameter"]) == 4 && nativeParameters["Coordinates"].ToString() == "(2,3)", "native public field and public getter parameters are retained");
            object[] noMetadata = { new MissingNativeMetadataCommand(), null };
            var unknownParameters = Parse((string)Call("URCommandParameters", noMetadata));
            Check(!(bool)noMetadata[1] && !unknownParameters.ContainsKey("Secret"), "missing native access metadata prevents disclosure and dispatch");
            string b = F("(2,3):1:Temple:2:0:False:False", "(2,3):Field", "1");
            Check(Confirm(b, F("", "(2,3):Field", "1"), "destroy") == "confirmed", "Destroy requires the named owned building to disappear");
            Check(Confirm(b, b, "destroy") == "not_confirmed", "unchanged target cannot confirm Destroy");
            Check(Confirm(b, F("", null, "1"), "destroy") == "unknown", "missing afterwards visibility cannot prove destruction");
            Check(Confirm(b, F("", "(2,3):Field", "2"), "destroy") == "unknown", "another seat cannot confirm destruction");
            Check(Confirm(F("(2,3):2:Temple:2:0", "(2,3):Field", "1"), F("", "(2,3):Field", "1"), "destroy") == "unknown", "opponent building cannot be attributed to own Destroy");
            Check(Confirm(F("(2,3):1:City:2:0", "(2,3):Field", "1"), F("", "(2,3):Field", "1"), "destroy") == "not_confirmed", "city disappearance is not building demolition");
            Check(Confirm(F("", "(2,3):Ice", "1"), F("", "(2,3):Water", "1"), "break_ice") == "confirmed", "named ice-water transition confirms ice break");
            Check(Confirm("player=1|units=|imps=|terrains=|publicTerrains=(2,3):Ice", "player=1|units=|imps=|terrains=|publicTerrains=(2,3):Ocean", "break_ice") == "confirmed", "disclosed ice outside own territory can be confirmed");
            Check(Confirm(F("", "(2,3):Field", "1"), F("", "(2,3):Field", "1"), "break_ice") == "unknown", "unobserved lava-effect removal is not fabricated as ice success");
            var player = new PlayerState(); var gs = new GameState();
            var score = Call("ResultsComputeScore", typeof(PlayerExtensions), player, gs);
            Check(score is ScoreDetails && !ReferenceEquals(PlayerExtensions.LastPlayer, player), "current score uses independent player copy");
            Check(player.availableTech.Count == 2 && player.unlockedTechCache[0] == 77, "native score cache mutations do not reach live player");
            var rating = Call("ResultsComputeRating", typeof(PlayerExtensions), player, gs, false);
            Check(!PlayerExtensions.LastWin && ((PlayerRating)rating).timeScore == 0, "loser rating gets the native non-winner time parameter");
            Check((bool)Call("ResultsPayloadComplete", "Perfection", score, null), "all perfection score components returned");
            Check(!(bool)Call("ResultsPayloadComplete", "Perfection", null, null), "missing score is not fabricated as zero");
            var payload = Parse((string)Call("ResultsPayload", "Domination", null, rating, (bool?)false));
            Check(Convert.ToDouble(payload["nativeFinalTotal"]) == 78.75, "domination native percentage scale preserved");
            Check(Call("ResultsNumber", float.NaN).ToString() == "null" && Call("ResultsNumber", true).ToString() == "null", "invalid score values remain unknown");
            var tile = new TileData();
            string growth = (string)Call("BuildingGrowthSnapshot", gs, tile, player);
            var growing = Parse(growth);
            Check(Convert.ToInt32(growing["ageTurns"]) == 2 && Convert.ToInt32(growing["foundedTurn"]) == 18, "building age comes from native getter and founded turn");
            Check(Convert.ToInt32(growing["maximumLevel"]) == 4 && Convert.ToInt32(growing["currentScore"]) == 100, "building score and cap use native getters rather than a guessed growth total");
            Check(growth == (string)Call("BuildingGrowthSnapshot", gs, tile, player) && tile.improvement.level == 0 && gs.CurrentTurn == 20, "repeated growth reads do not advance time or maturity");
            gs.CurrentTurn = 21; tile.improvement.level = 1;
            growing = Parse((string)Call("BuildingGrowthSnapshot", gs, tile, player));
            Check(Convert.ToInt32(growing["ageTurns"]) == 3 && Convert.ToInt32(growing["currentScore"]) == 150, "snapshot reflects a later settled fixture state without performing its upgrade");
            gs.GameLogicData.Data = null;
            growing = Parse((string)Call("BuildingGrowthSnapshot", gs, tile, player));
            Check(growing["growthRateTurns"] == null && growing["growthRewards"] == null, "unreadable growth rules are not reported as zero or empty rewards");
            tile.improvement = null;
            Check((string)Call("BuildingGrowthSnapshot", gs, tile, player) == "null", "demolished building has no remaining growth snapshot");
            object instance = Activator.CreateInstance(T);
            var view = new InfoPopup();
            var details = Parse((string)T.GetMethod("TribeInfoVisibleDetails", Flags).Invoke(instance, new object[] { view }));
            Check(details["attitudeLabel"].ToString() == "Friendly" && details["attitudeReasonsText"].ToString() == "Shared enemy\nEmbassy", "attitude label and reason text are not reversed");
            var relations = (System.Collections.IList)details["thirdPartyRelations"];
            string label = ((Dictionary<string, object>)relations[0])["text"].ToString();
            Check(relations.Count == 1 && !label.Contains("123") && !label.Contains("link="), "visible native relation rows preserve anonymous text without hidden rich-text IDs");
            view.relationSlider.gameObject.activeInHierarchy = false;
            details = Parse((string)T.GetMethod("TribeInfoVisibleDetails", Flags).Invoke(instance, new object[] { view }));
            Check(details["attitudeSliderValue"] == null, "native hidden slider is not published as attitude amount");
            view.opinionText.gameObject.activeInHierarchy = false;
            details = Parse((string)T.GetMethod("TribeInfoVisibleDetails", Flags).Invoke(instance, new object[] { view }));
            Check(details["attitudeReasonsText"] == null, "hidden native reason control remains unknown");
            Check((bool)Call("IsUnitActorAction", "explode"), "legacy Explode associates its own actor in the shared settlement pipeline");
            string directory = Path.Combine(Path.GetDirectoryName(args[0]), "results-fixture-" + Guid.NewGuid().ToString("N"));
            T.GetField("_resultsDirectoryOverride", Flags).SetValue(instance, directory);
            string ep = "ep-0123456789abcdef";
            string record = "{\"schema\":\"native-final-1\",\"episodeId\":\"" + ep + "\",\"controlledPlayerId\":1,\"mode\":\"Perfection\",\"final\":true,\"complete\":true,\"scores\":"
                + (string)Call("ResultsPayload", "Perfection", score, null, null) + "}";
            T.GetMethod("ResultsRememberFinal", Flags).Invoke(instance, new object[] { ep, record });
            Check(File.ReadAllText(Path.Combine(directory, ep + ".json")) == record, "native final result persisted with its own episode");
            string readback = (string)T.GetMethod("ResultPublic", Flags).Invoke(instance, new object[] { ep });
            Check((bool)Parse(readback)["ok"], "archived result remains readable from menu");
            string unknown = (string)T.GetMethod("ResultPublic", Flags).Invoke(instance, new object[] { "ep-fedcba9876543210" });
            Check(!(bool)Parse(unknown)["ok"], "another episode never borrows an old score");

            // Multi-match training must not retain every persisted result in memory.
            var remember = T.GetMethod("ResultsRememberFinal", Flags);
            var cache = (Dictionary<string, string>)T.GetField("_nativeFinalResults", Flags).GetValue(instance);
            var episodes = new List<string>();
            for (int i = 0; i < 40; i++)
            {
                string next = "ep-" + i.ToString("x16"); episodes.Add(next);
                remember.Invoke(instance, new object[] { next, record.Replace(ep, next) });
            }
            Check(cache.Count <= 32, "persisted final-result cache remains bounded across forty matches");
            Check(cache.ContainsKey(episodes[39]), "latest native final remains in memory");
            string evicted = null;
            foreach (var candidate in episodes) if (!cache.ContainsKey(candidate)) { evicted = candidate; break; }
            Check(evicted != null, "older persisted result actually left the memory cache");
            var evictedResult = Parse((string)T.GetMethod("ResultPublic", Flags).Invoke(instance, new object[] { evicted }));
            Check((bool)evictedResult["ok"] && evictedResult["episodeId"].ToString() == evicted,
                "evicted archive is still available through the public result API");
            string frozen = File.ReadAllText(Path.Combine(directory, evicted + ".json"));
            remember.Invoke(instance, new object[] { evicted, frozen.Replace("\"complete\":true", "\"complete\":false") });
            Check(File.ReadAllText(Path.Combine(directory, evicted + ".json")) == frozen,
                "eviction cannot allow a frozen complete archive to be overwritten by a later partial result");
            Check(T.GetField("_nativeFinalLatestEpisode", Flags).GetValue(instance).ToString() == episodes[39],
                "historical archive lookup or repeated capture cannot replace the latest result pointer");
            Check(File.Exists(Path.Combine(directory, ep + ".json")) && Directory.GetFiles(directory, "ep-*.json").Length == 41,
                "memory trimming does not delete persisted final records");

            object blocked = Activator.CreateInstance(T);
            string notDirectory = Path.Combine(directory, "blocked"); File.WriteAllText(notDirectory, "file");
            T.GetField("_resultsDirectoryOverride", Flags).SetValue(blocked, notDirectory);
            for (int i = 100; i < 135; i++)
            {
                string next = "ep-" + i.ToString("x16");
                remember.Invoke(blocked, new object[] { next, record.Replace(ep, next) });
            }
            var unsaved = (Dictionary<string, string>)T.GetField("_nativeFinalResults", Flags).GetValue(blocked);
            Check(unsaved.Count == 35, "storage failure preserves every otherwise unrecorded final instead of enforcing a lossy hard cap");
            Check((bool)Parse((string)T.GetMethod("ResultPublic", Flags).Invoke(blocked,
                new object[] { "ep-0000000000000064" }))["ok"], "old unsaved result remains readable after storage failure");
            Console.WriteLine("PASS total " + passed); return 0;
        }
        catch (Exception e) { Console.WriteLine(e); return 1; }
    }
}
