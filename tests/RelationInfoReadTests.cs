// Actual native-UI projection, including unreadable collections and hidden rows.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public sealed class RelationObject { public object activeInHierarchy = true; }
public sealed class RelationText
{
    public RelationObject gameObject = new RelationObject();
    public string text;
    public RelationText(string value) { text = value; }
}
public sealed class RelationEntry
{
    public RelationObject gameObject = new RelationObject();
    public object Label;
    public int Reads;
    public object label { get { Reads++; throw new Exception("hidden label must not be read"); } }
    public RelationEntry(string text) { Label = new RelationText(text); }
}
public sealed class RelationPopup
{
    public RelationText relationText = new RelationText("Friendly"), opinionText = new RelationText("Shared enemy");
    public object tribeRelations;
}
public sealed class InterruptedRelations : IEnumerable
{
    public object First;
    public IEnumerator GetEnumerator() { yield return First; throw new Exception("unreadable suffix"); }
}
public sealed class IndexedRelations
{
    public object Count { get; set; }
    public IndexedRelations() { Count = 3; }
    public object First, Last;
    public object this[int i] { get { if (i == 1) throw new Exception("unreadable slot"); return i == 0 ? First : Last; } }
}
internal static class RelationInfoReadTests
{
    private static readonly BridgeMod Host = new BridgeMod();
    private static readonly JavaScriptSerializer J = new JavaScriptSerializer();
    private static int passed, failed;
    private static void Check(bool value, string label)
    { if (value) { passed++; Console.WriteLine("PASS " + label); } else { failed++; Console.WriteLine("FAIL " + label); } }
    private static Dictionary<string, object> Read(object rows)
    {
        try { return J.Deserialize<Dictionary<string, object>>((string)typeof(BridgeMod)
            .GetMethod("TribeInfoVisibleDetails", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(Host, new object[] { new RelationPopup { tribeRelations = rows } })); }
        catch { return new Dictionary<string, object>(); }
    }
    private static bool Status(Dictionary<string, object> value, string status)
    { return value.ContainsKey("thirdPartyRelationsStatus") && (string)value["thirdPartyRelationsStatus"] == status; }
    private static IList Rows(Dictionary<string, object> value)
    { return value.ContainsKey("thirdPartyRelations") ? value["thirdPartyRelations"] as IList : null; }
    public static int Main()
    {
        try { Run(); Console.WriteLine("ALL PASS " + passed + " FAILED " + failed); return failed == 0 ? 0 : 1; }
        catch (Exception e) { Console.WriteLine(e); return 1; }
    }
    private static void Run()
    {
        var known = new RelationEntry("<link=secret-id>Unknown tribe</link> has peace with Imperius");
        var hidden = new RelationEntry("hidden private relation"); hidden.gameObject.activeInHierarchy = false;
        var value = Read(new object[] { known, hidden });
        Check(Status(value, "ok") && Rows(value).Count == 1, "visible and hidden native rows form complete visible list");
        string json = J.Serialize(value);
        Check(json.Contains("Unknown tribe") && !json.Contains("secret-id") && !json.Contains("hidden private"), "native anonymity and hidden row text preserved");
        Check(hidden.Reads == 0, "hidden row getter never inspected");
        Check(value.ContainsKey("attitudeLabel") && (string)value["attitudeLabel"] == "Friendly", "other visible diplomacy details retained");
        value = Read(new object[0]); Check(Status(value, "ok") && Rows(value).Count == 0, "known empty remains complete empty");
        value = Read(null); Check(Status(value, "unavailable") && value.ContainsKey("thirdPartyRelations") && value["thirdPartyRelations"] == null, "missing list remains unknown");
        value = Read(new InterruptedRelations { First = known });
        Check(Status(value, "unavailable") && value.ContainsKey("attitudeLabel") && Rows(value) == null, "enumeration failure cannot abort other details or publish prefix as complete");
        value = Read(new object[] { known, null }); Check(Status(value, "partial") && Rows(value).Count == 1, "null row preserves known information with partial status");
        var unknown = new RelationEntry("unknown activity secret"); unknown.gameObject.activeInHierarchy = null;
        value = Read(new object[] { known, unknown });
        Check(Status(value, "partial") && Rows(value).Count == 1 && unknown.Reads == 0 && !J.Serialize(value).Contains("unknown activity secret"), "unknown activity not exposed or counted as complete");
        var unreadable = new RelationEntry(null); value = Read(new object[] { known, unreadable });
        Check(Status(value, "partial") && Rows(value).Count == 1, "visible unreadable label does not claim full coverage");
        var indexed = new IndexedRelations { First = known, Last = new RelationEntry("Second rendered relation") };
        value = Read(indexed); Check(Status(value, "partial") && Rows(value).Count == 2, "bad index preserves both readable rendered relations");
        foreach (var count in new object[] { -1, 2.5, true, "3", null })
        {
            indexed.Count = count; value = Read(indexed);
            Check(Status(value, "unavailable") && Rows(value) == null, "invalid native count is not an empty relationship list");
        }
        value = Read(new object[] { hidden }); Check(Status(value, "ok") && Rows(value).Count == 0, "all known hidden rows legitimately produce empty visible list");
    }
}
