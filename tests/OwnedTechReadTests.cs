using System;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public enum CurrentOwnedTech { Basic, Future_NativeTech }
public sealed class OwnedTechPlayer
{
    public object Data;
    public int Reads;
    public bool FailFirst;
    public object availableTech { get { if (++Reads == 1 && FailFirst) throw new Exception("unreadable"); return Data; } }
}
public sealed class SingleOwnedTechEnumeration : IEnumerable
{
    public int Reads;
    public IEnumerator GetEnumerator()
    {
        if (++Reads != 1) throw new Exception("do not enumerate a native snapshot twice");
        return new object[] { CurrentOwnedTech.Basic, CurrentOwnedTech.Future_NativeTech }.GetEnumerator();
    }
}
internal static class OwnedTechReadTests
{
    private static int passed, failed;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static void Check(bool good, string name)
    { if (good) { passed++; Console.WriteLine("PASS " + name); } else { failed++; Console.WriteLine("FAIL " + name); } }
    private static Dictionary<string, object> Read(object player, out HashSet<string> names)
    {
        var output = new JsonObj();
        names = BridgeMod.AppendOwnedTechFields(output, player);
        return Json.Deserialize<Dictionary<string, object>>(output.Close());
    }
    public static int Main()
    {
        HashSet<string> names;
        foreach (object bad in new object[] { null, new object[] { null }, new object[] { "Basic", "Basic" }, new object[] { 0 } })
        {
            var player = new OwnedTechPlayer { Data = bad };
            var result = Read(player, out names);
            Check(names == null && result["ownedTechs"] == null && result["availableTechs"] == null
                && (string)result["ownedTechsStatus"] == "unavailable" && player.Reads == 1,
                "unreadable or invalid native tech collection is explicit unknown");
        }
        var empty = Read(new OwnedTechPlayer { Data = new object[0] }, out names);
        Check(names != null && names.Count == 0 && ((IList)empty["ownedTechs"]).Count == 0
            && (string)empty["ownedTechsStatus"] == "ok", "actual complete empty set remains distinct from unknown");
        var list = new SingleOwnedTechEnumeration();
        var current = new OwnedTechPlayer { Data = list };
        var full = Read(current, out names);
        Check(current.Reads == 1 && list.Reads == 1 && names.SetEquals(new[] { "Basic", "Future_NativeTech" }),
            "one native property and one enumeration feed the returned known set");
        Check(((IList)full["ownedTechs"]).Count == 2 && (bool)full["availableTechsDeprecated"]
            && (string)full["ownedTechsStatus"] == "ok", "current native names and legacy alias carry known completeness");
        var flaky = new OwnedTechPlayer { Data = new object[0], FailFirst = true };
        var failedRead = Read(flaky, out names);
        Check(names == null && failedRead["ownedTechs"] == null && flaky.Reads == 1,
            "failed owned-tech getter is not retried into an empty player");
        Read(flaky, out names);
        Check(names != null && names.Count == 0 && flaky.Reads == 2,
            "a later explicit query can establish the actual empty set");
        Read(new object(), out names);
        Check(names == null, "absent owned-tech metadata is unknown");
        Check(BridgeMod.ReadOwnedTechNames(new object[]{"1000"})==null
            && BridgeMod.ReadOwnedTechNames(new object[]{(CurrentOwnedTech)1000})==null,
            "unregistered raw numeric owned technology stays unknown");
        Check(BridgeMod.OwnedTechFingerprint(new HashSet<string>{"Basic","1000"})=="1000,Basic",
            "already certified owned rule IDs survive settlement fingerprinting");
        Check(BridgeMod.IsDecisionWorldKey("ownedTechsStatus"), "world schema declares technology completeness");
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
