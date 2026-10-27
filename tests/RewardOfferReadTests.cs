// Exercise the actual pending-trigger projection, native popup resolution and offer reads.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;
using PolytopiaCityOps;

public sealed class GameState { public object pendingCommandTriggers = new object[0]; }
public sealed class CityLevelCommandTrigger
{
    public string type = "CityLevelUp";
    public object playerId = (byte)2;
    public string Coordinates = "(2,3)";
}
public sealed class RewardGameObject
{
    public object activeInHierarchy = true;
    public RewardPopup Popup;
    public T GetComponent<T>() where T : class { return Popup as T; }
}
public sealed class RewardTile { public string coordinates = "(2,3)"; }
public sealed class RewardPopup
{
    public RewardGameObject gameObject = new RewardGameObject();
    public object tile = new RewardTile(), RawRewards;
    public int Reads;
    public object cityRewards { get { Reads++; return RawRewards; } }
    public RewardPopup() { gameObject.Popup = this; }
}
public sealed class PopupWrapper { public RewardGameObject gameObject; }
public static class PopupManager
{
    public static object Current;
    public static int Reads;
    public static object GetCurrentPopup() { Reads++; return Current; }
}
public sealed class BrokenOffer : IEnumerable
{
    public IEnumerator GetEnumerator() { yield return "Workshop"; throw new Exception("unreadable suffix"); }
}
public sealed class IndexedOffer
{
    public int Count { get { return 3; } }
    public object this[int index] { get { if (index == 1) throw new Exception("unreadable slot"); return index == 0 ? "Workshop" : "FutureNativeReward"; } }
}
internal static class RewardOfferReadTests
{
    private const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly JavaScriptSerializer J = new JavaScriptSerializer();
    private static int passed;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static Dictionary<string, object> Read(GameState state)
    {
        var args = new object[] { state, (byte)2, null, 50, 3, 5, new List<string>() };
        return J.Deserialize<Dictionary<string, object>>((string)typeof(CityOptionsSnapshot).GetMethod("PendingCityRewardJson", F).Invoke(null, args));
    }
    private static bool Empty(Dictionary<string, object> value)
    { return ((IList)value["choices"]).Count == 0 && (string)value["status"] == "trigger-present-popup-unreadable"; }
    public static int Main()
    {
        try { Run(); Console.WriteLine("ALL PASS " + passed); return 0; }
        catch (Exception e) { Console.WriteLine(e); return 1; }
    }
    private static void Run()
    {
        var trigger = new CityLevelCommandTrigger();
        var state = new GameState { pendingCommandTriggers = new object[] { trigger } };
        var popup = new RewardPopup { RawRewards = new object[] { "Workshop", "FutureNativeReward" } };
        PopupManager.Current = popup;
        Check(Object.ReferenceEquals(BridgeMod.CurrentCityRewardPopupComponent(), popup) && popup.Reads == 0,
            "component resolution does not inspect rewards before matching city");
        var value = Read(state);
        Check((string)value["status"] == "offered" && Convert.ToInt32(value["choiceCount"]) == 2, "matching own active city exposes real offer");
        Check(Convert.ToInt32(value["nativeChoiceCount"]) == 2 && popup.Reads == 1, "offer list read once with consistent native count");
        Check((string)((Dictionary<string, object>)((IList)value["choices"])[1])["reward"] == "FutureNativeReward", "future native rewards retained without fixed candidate list");

        popup.Reads = 0; popup.tile = new RewardTile { coordinates = "(99,98)" };
        value = Read(state);
        Check(Empty(value) && popup.Reads == 0, "other city's popup choices not read or borrowed");
        Check(J.Serialize(value).IndexOf("99,98", StringComparison.Ordinal) < 0, "mismatched popup city is not disclosed");
        popup.tile = null; Check(Empty(Read(state)) && popup.Reads == 0, "unknown popup city cannot grant offer access");
        popup.tile = new RewardTile(); popup.gameObject.activeInHierarchy = false;
        Check(Empty(Read(state)) && popup.Reads == 0, "inactive pooled popup cannot donate old choices");
        popup.gameObject.activeInHierarchy = null;
        Check(Empty(Read(state)) && popup.Reads == 0, "unknown popup activity cannot grant offer access");
        popup.gameObject.activeInHierarchy = true;
        PopupManager.Current = new PopupWrapper { gameObject = popup.gameObject };
        Check((string)Read(state)["status"] == "offered", "typed component resolved from current native wrapper");
        PopupManager.Current = popup;

        popup.RawRewards = new object[] { "Workshop", null, "FutureNativeReward" };
        value = Read(state);
        Check((string)value["status"] == "offered-partial" && Convert.ToInt32(value["choiceCount"]) == 2
            && Convert.ToInt32(value["nativeChoiceCount"]) == 3, "null choice keeps known candidates with explicit partial and separate counts");
        popup.RawRewards = new IndexedOffer(); value = Read(state);
        Check((string)value["status"] == "offered-partial" && ((IList)value["choices"]).Count == 2, "unreadable native offer slot does not discard known choices or claim complete");
        popup.RawRewards = new BrokenOffer(); value = Read(state);
        Check(Empty(value) && value["choiceCount"] == null && value["nativeChoiceCount"] == null, "interrupted enumeration cannot publish prefix as full offer");
        popup.RawRewards = new object[0]; value = Read(state);
        Check((string)value["status"] == "offered" && Convert.ToInt32(value["choiceCount"]) == 0, "known empty current offer remains known empty");
        popup.RawRewards = null; Check(Empty(Read(state)), "unreadable current offer is distinct from empty");

        int reads = PopupManager.Reads;
        state.pendingCommandTriggers = null;
        Check((string)Read(state)["status"] == "none" && PopupManager.Reads == reads, "known native null queue is empty without inspecting UI");
        state.pendingCommandTriggers = new object();
        Check((string)Read(state)["status"] == "unavailable" && PopupManager.Reads == reads, "unreadable trigger list does not inspect UI");
        state.pendingCommandTriggers = new object[0];
        Check((string)Read(state)["status"] == "none" && PopupManager.Reads == reads, "known no triggers is distinct from unreadable");
        trigger.Coordinates = null; state.pendingCommandTriggers = new object[] { trigger };
        Check((string)Read(state)["status"] == "unavailable" && PopupManager.Reads == reads, "own trigger with missing city cannot become no reward");
        trigger.Coordinates = "(2,3)"; trigger.type = "FutureCityLevelUp";
        Check((string)Read(state)["status"] == "none" && PopupManager.Reads == reads, "substring trigger name cannot acquire city reward access");
        trigger.type = "CityLevelUp"; trigger.playerId = (byte)3;
        Check((string)Read(state)["status"] == "none" && PopupManager.Reads == reads, "other player's trigger does not inspect current reward popup");
    }
}
