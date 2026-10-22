using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using PolytopiaAgent;

public static class PopupManager
{
    public static object Current;
    public static bool Showing = true, Throw;
    public static int Reads;
    public static bool get_PopupShowing() { return Showing; }
    public static object GetCurrentPopup() { Reads++; if (Throw) throw new Exception("unreadable popup"); return Current; }
}
public class PopupGo { public bool activeInHierarchy = true; }
public class PopupData
{
    public object id = -1;
    public string text = "OK";
    public bool closesPopup = true;
    public virtual object callback { get { return null; } }
}
public class BadCallbackData : PopupData
{ public override object callback { get { throw new Exception("unreadable callback"); } } }
public class ReadPopup
{
    public PopupGo gameObject = new PopupGo();
    public object buttonData;
}
public class ProtectedPopup : ReadPopup
{
    private object[] m_buttonData;
    public ProtectedPopup(object[] data) { buttonData = new object[0]; m_buttonData = data; }
}
public class RewardPopup : ReadPopup
{
    public bool ThrowRewards;
    public object cityRewards { get { if (ThrowRewards) throw new Exception("unreadable offers"); return new object[] { "Workshop", "Explorer" }; } }
}
public class BrokenSequence : IEnumerable
{
    public IEnumerator GetEnumerator() { yield return new PopupData(); throw new Exception("incomplete collection"); }
}
internal static class PopupReadTests
{
    private static readonly BridgeMod Host = new BridgeMod();
    private static int passed, failed;
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool ok, string label)
    { if (ok) { passed++; Console.WriteLine("PASS " + label); } else { failed++; Console.WriteLine("FAIL " + label); } }
    private static object Call(string name)
    { return typeof(BridgeMod).GetMethod(name, F, null, Type.EmptyTypes, null).Invoke(Host, null); }
    private static Dictionary<string, object> Buttons()
    {
        try { return BridgeJsonCodec.Object((string)Call("PopupButtons")); }
        catch (TargetInvocationException) { return new Dictionary<string, object>(); }
    }
    private static bool IsPartial(Dictionary<string, object> r)
    { object s; return r.TryGetValue("status", out s) && (object.Equals(s, "partial") || object.Equals(s, "unavailable")); }
    private static int Count(Dictionary<string, object> r)
    { object rows; return r.TryGetValue("buttons", out rows) ? ((List<object>)rows).Count : -1; }
    public static int Main()
    {
        try
        {
            PopupManager.Throw = true;
            Check(Call("PopupNeedsChoice") == null, "throwing current-popup getter is unknown rather than no decision");
            PopupManager.Throw = false; PopupManager.Current = null;
            Check(Call("PopupNeedsChoice") == null, "shown popup with unreadable/missing current object remains unknown");
            PopupManager.Showing = false; PopupManager.Throw = true; PopupManager.Reads = 0;
            Check(object.Equals(Call("PopupNeedsChoice"), false) && PopupManager.Reads == 0,
                "known no popup requires no failing current-object lookup");
            PopupManager.Throw = false; PopupManager.Showing = true;
            PopupManager.Current = new ReadPopup { buttonData = new object[] { new PopupData { id = 0 }, new PopupData { id = 1 } } };
            Check(object.Equals(Call("PopupNeedsChoice"), true), "generic multi-button choice is recognized without cityRewards type table");
            PopupManager.Current = new ReadPopup { buttonData = new object[] { new PopupData() } };
            Check(object.Equals(Call("PopupNeedsChoice"), false), "single native closing OK id -1 is an acknowledgement");
            PopupManager.Current = new ReadPopup { buttonData = new object[] { new PopupData(), null, new PopupData { id = 17 } } };
            var r = Buttons();
            Check(IsPartial(r) && r.ContainsKey("buttonCount") && r["buttonCount"] == null && Count(r) == 2,
                "null slot preserves later known buttons and does not claim complete count");
            PopupManager.Current = new ReadPopup { buttonData = new BrokenSequence() }; r = Buttons();
            Check(IsPartial(r) && r.ContainsKey("buttonCount") && r["buttonCount"] == null,
                "enumeration failure returns explicit unavailable metadata without escaping the request");
            PopupManager.Current = new ProtectedPopup(new object[] { new PopupData { id = 17 } }); r = Buttons();
            Check(Count(r) == 1 && object.Equals(r["status"], "ok"), "readable protected fallback is represented in the actual buttons list");
            PopupManager.Current = new ReadPopup { buttonData = new object[] { new BadCallbackData() } }; r = Buttons();
            Check(Count(r) == 1 && ((Dictionary<string, object>)((List<object>)r["buttons"])[0])["hasCallback"] == null,
                "unreadable callback presence is null rather than false");
            PopupManager.Current = new RewardPopup { buttonData = new object[0] };
            Check(object.Equals(Call("PopupNeedsChoice"), true), "current native reward offers are an actual decision");
            ((RewardPopup)PopupManager.Current).ThrowRewards = true;
            Check(Call("PopupNeedsChoice") == null, "unreadable native reward offers are not an empty choice list");
            Console.WriteLine("passed=" + passed + " failed=" + failed); return failed == 0 ? 0 : 1;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }
}
