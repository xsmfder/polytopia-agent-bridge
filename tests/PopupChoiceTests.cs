using System;
using System.Collections.Generic;
using System.Reflection;
using PolytopiaAgent;

public class BaseEventData { }
public static class PopupManager
{
    public static object Current;
    public static bool PopupShowing { get { return Current != null; } }
    public static object GetCurrentPopup() { return Current; }
}
public enum ChoiceState { None, Selected, Disabled, Alternative }
public class ChoiceData
{
    public int id = 17;
    public object state = ChoiceState.None, closesPopup = true, callback;
    public string text = "native choice";
}
public class ChoicePopup
{
    public object[] buttonData;
    public int OnHides, Hides;
    public bool ThrowOnHide;
    public ChoicePopup(ChoiceData data) { buttonData = new object[] { data }; }
    public void OnHide(int id, BaseEventData data)
    { OnHides++; if (ThrowOnHide) throw new InvalidOperationException("after cleanup entered"); PopupManager.Current = null; }
    public void Hide() { Hides++; PopupManager.Current = null; }
}
public class WrongCallback
{
    public int Calls;
    public void Invoke(int implicitZero) { Calls++; }
}
public class NakedHandlerPopup
{
    public object[] buttonData = new object[0];
    public int Calls;
    public void RewardButtonClicked(int id, BaseEventData data) { Calls++; }
}
public class UnreadableCallbackData
{
    public int id = 17;
    public ChoiceState state = ChoiceState.None;
    public bool closesPopup = true;
    public object callback { get { throw new Exception("unreadable delegate is not null delegate"); } }
}

public class ChangingCallbackData
{
    public int id = 17, Reads;
    public ChoiceState state = ChoiceState.None;
    public bool closesPopup = true;
    public string text = "native choice";
    public Action Value, Change;
    public object callback { get { Reads++; if (Reads == 2 && Change != null) Change(); return Value; } }
}

internal static class PopupChoiceTests
{
    private static int passed, failed;
    private static readonly BridgeMod Host = new BridgeMod();
    private static void Check(bool ok, string label)
    { if (ok) { passed++; Console.WriteLine("PASS " + label); } else { failed++; Console.WriteLine("FAIL " + label); } }
    private static Dictionary<string, object> Choose(string id)
    {
        return BridgeJsonCodec.Object((string)typeof(BridgeMod).GetMethod("PopupChoose",
            BindingFlags.NonPublic | BindingFlags.Instance).Invoke(Host, new object[] { id }));
    }
    private static bool Rejected(Dictionary<string, object> r)
    { object v; return r.TryGetValue("dispatched", out v) && object.Equals(v, false) && (string)r["outcome"] == "not_dispatched"; }
    private static bool Unknown(Dictionary<string, object> r)
    { object v; return r.TryGetValue("outcome", out v) && object.Equals(v, "unknown") && !Rejected(r); }

    public static int Main()
    {
        try
        {
            PopupManager.Current = null;
            Check(Rejected(Choose("17")), "missing popup proves no callback was called");
            int calls = 0;
            var data = new ChoiceData { callback = (Action)delegate { calls++; throw new Exception("effect happened before failure"); } };
            var popup = new ChoicePopup(data); PopupManager.Current = popup;
            var r = Choose("17");
            Check(calls == 1 && popup.OnHides == 0 && popup.Hides == 0 && Unknown(r),
                "callback exception is unknown and never falls through to OnHide or Hide");
            calls = 0; data.callback = (Action)delegate { calls++; }; PopupManager.Current = popup;
            r = Choose("0");
            Check(calls == 0 && Rejected(r), "unmatched native id cannot select an ordinal with a different id");
            calls = 0; popup = new ChoicePopup(data); PopupManager.Current = popup; data.state = ChoiceState.Disabled;
            r = Choose("17");
            Check(calls == 0 && popup.OnHides == 0 && Rejected(r), "native disabled choice never runs callback or cleanup");
            calls = 0; data.state = null;
            Check(Rejected(Choose("17")) && calls == 0, "unknown native choice state cannot authorize invocation");
            data.state = ChoiceState.None; data.closesPopup = false;
            popup = new ChoicePopup(data); PopupManager.Current = popup; r = Choose("17");
            Check(calls == 1 && popup.OnHides == 0 && popup.Hides == 0 && object.Equals(r["ok"], true),
                "nonclosing choice runs its callback without OnHide or Hide");
            calls = 0; data.closesPopup = null; popup = new ChoicePopup(data); PopupManager.Current = popup;
            r = Choose("17");
            Check(calls == 1 && popup.OnHides == 0 && popup.Hides == 0,
                "unknown closesPopup does not default to closing the display");
            data.closesPopup = true;
            object replacement = new object(); data.callback = (Action)delegate { PopupManager.Current = replacement; };
            popup = new ChoicePopup(data); PopupManager.Current = popup; r = Choose("17");
            Check(ReferenceEquals(PopupManager.Current, replacement) && popup.OnHides == 0 && popup.Hides == 0,
                "callback replacing current popup cannot trigger cleanup against the old popup");
            data.callback = null; popup = new ChoicePopup(data) { ThrowOnHide = true }; PopupManager.Current = popup;
            r = Choose("17");
            Check(popup.OnHides == 1 && popup.Hides == 0 && Unknown(r),
                "OnHide exception never falls back to Hide or reports successful acknowledgement");
            var wrong = new WrongCallback(); data.callback = wrong;
            popup = new ChoicePopup(data); PopupManager.Current = popup; r = Choose("17");
            Check(wrong.Calls == 0 && popup.OnHides == 0 && popup.Hides == 0 && Rejected(r),
                "unsupported callback signature cannot be invoked with manufactured default arguments");
            var naked = new NakedHandlerPopup(); PopupManager.Current = naked; r = Choose("42");
            Check(naked.Calls == 0 && Rejected(r), "unlisted reward handler cannot authorize an arbitrary numeric choice");
            data.callback = null; popup = new ChoicePopup(data); PopupManager.Current = popup; r = Choose("17");
            Check(object.Equals(r["ok"], true) && popup.OnHides == 1 && popup.Hides == 0,
                "ordinary callback-free closing acknowledgement invokes native OnHide once");
            calls = 0; data.callback = (Action)delegate { calls++; };
            popup = new ChoicePopup(data); PopupManager.Current = popup; r = Choose("17");
            Check(object.Equals(r["ok"], true) && calls == 1 && popup.OnHides == 1 && popup.Hides == 0,
                "successful native callback is followed by one native closing handler");
            popup = new ChoicePopup(data); popup.buttonData = new object[] { new UnreadableCallbackData() };
            PopupManager.Current = popup; r = Choose("17");
            Check(Rejected(r) && popup.OnHides == 0 && popup.Hides == 0,
                "unreadable callback cannot be treated as callback-free acknowledgement");
            popup = new ChoicePopup(data); popup.buttonData = new object[] { data, data };
            PopupManager.Current = popup; calls = 0; r = Choose("17");
            Check(Rejected(r) && calls == 0 && popup.OnHides == 0,
                "duplicate native ids cannot select an arbitrary first choice");
            data.state = (ChoiceState)99; popup = new ChoicePopup(data); PopupManager.Current = popup; r = Choose("17");
            Check(Rejected(r) && calls == 0, "undefined native state cannot act as an enabled choice");
            foreach (string change in new[] { "replace", "close", "disable", "renumber" })
            {
                calls = 0;
                var changing = new ChangingCallbackData { Value = delegate { calls++; } };
                var original = new ChoicePopup(new ChoiceData()); original.buttonData = new object[] { changing };
                object next = new object(); PopupManager.Current = original;
                changing.Change = delegate
                {
                    if (change == "replace") PopupManager.Current = next;
                    else if (change == "close") PopupManager.Current = null;
                    else if (change == "disable") changing.state = ChoiceState.Disabled;
                    else changing.id = 99;
                };
                r = Choose("17");
                Check(Rejected(r) && calls == 0 && original.OnHides == 0 && original.Hides == 0,
                    "last-minute native button change rejects before callback or cleanup: " + change);
            }
            Console.WriteLine("passed=" + passed + " failed=" + failed);
            return failed == 0 ? 0 : 1;
        }
        catch (Exception e) { Console.WriteLine(e); return 1; }
    }
}
