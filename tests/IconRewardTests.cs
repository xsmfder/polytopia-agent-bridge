using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;
public class BaseEventData { }
public class ButtonObject { public bool activeInHierarchy = true; }
public class NativeLabel { public string text = "Receive"; }
public class NativeRewardButton
{
    public ButtonObject gameObject = new ButtonObject(); public NativeLabel label = new NativeLabel();
    public int id = 17; public bool Enabled=true, ThrowEnabled;
    public bool ButtonEnabled { get { if(ThrowEnabled)throw new Exception("unreadable"); return Enabled; } set { Enabled=value; } }
}
public class IconRewardPopup
{
    public NativeRewardButton RewardButton { get; set; }
    public IconRewardPopup() { RewardButton=new NativeRewardButton(); }
    public object buttonData = new object[0]; public int Calls; public bool Throw;
    private void RewardButtonClicked<T>(int id, BaseEventData data) { throw new Exception("open generic must not be selected"); }
    private void RewardButtonClicked(int id, BaseEventData data)
    { Calls++; if (id != RewardButton.id || Throw) throw new Exception("uncertain"); }
}
public static class PopupManager
{
    public static object Current;
    public static object GetCurrentPopup() { return Current; }
}
public class OnePassIconData : IEnumerable
{
    public int Reads;
    public IEnumerator GetEnumerator()
    {
        if (++Reads > 1) throw new Exception("native lazy menu cannot be enumerated twice");
        yield return new IconDataRow();
    }
}
public class IconDataRow
{
    public int id=17; public string text="generic wrapper"; public bool closesPopup=true;
    public object callback;
}
internal static class IconRewardTests
{
    private const BindingFlags F=BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    private static int passed;
    private static void Check(bool ok,string label) { if(!ok)throw new Exception(label); passed++; Console.WriteLine("PASS "+label); }
    private static readonly JavaScriptSerializer J=new JavaScriptSerializer();
    private static Dictionary<string,object> Call(BridgeMod h,string name,params object[] args)
    { return J.Deserialize<Dictionary<string,object>>((string)typeof(BridgeMod).GetMethod(name,F).Invoke(h,args)); }
    public static int Main()
    {
        try
        {
            var h=new BridgeMod(); var popup=new IconRewardPopup(); PopupManager.Current=popup;
            var buttons=Call(h,"PopupButtons");
            Check(Convert.ToInt32(buttons["buttonCount"])==1,"empty PopupButtonData still exposes physical native reward button");
            var row=(Dictionary<string,object>)((IList)buttons["buttons"])[0];
            Check(Convert.ToInt32(row["id"])==17 && Convert.ToInt32(row["index"])==0,"read real native id rather than guessing zero");
            Check((string)row["text"]=="Receive" && (bool)row["enabled"],"read current localized label and enabled flag");
            Check(row["closesPopup"]==null && popup.Calls==0,"reading choice does not execute handler or guess closing semantics");
            var once=new OnePassIconData();
            popup.buttonData=once;
            var lazy=Call(h,"PopupButtons");
            Check(once.Reads==1 && Convert.ToInt32(lazy["buttonCount"])==1,"physical reward deduplication enumerates native data once");
            Check((string)((Dictionary<string,object>)((IList)lazy["buttons"])[0])["source"]=="native_IconRewardPopup_RewardButton",
                "real physical reward metadata supersedes its generic duplicate row");
            popup.buttonData=new object[0];
            Check((bool)Call(h,"PopupChoose","17")["ok"] && popup.Calls==1,"explicit choice invokes exact native handler once");
            Check(!(bool)Call(h,"PopupChoose","0")["ok"] && popup.Calls==1,"wrong guessed id does not call handler");
            var unavailable=Call(h,"PopupChoose","0");
            Check(object.Equals(unavailable["dispatched"],false) && (string)unavailable["outcome"]=="not_dispatched",
                "wrong physical reward id proves no callback was invoked");
            popup.RewardButton.ButtonEnabled=false;
            Check(!(bool)Call(h,"PopupChoose","17")["ok"] && popup.Calls==1,"disabled reward is not invoked");
            Check(BridgeMod.ReadIconRewardChoice(popup).Enabled==false,"visible disabled button is represented explicitly");
            popup.RewardButton.ThrowEnabled=true;
            Check(!(bool)Call(h,"PopupChoose","17")["ok"] && popup.Calls==1,"unknown enabled state does not permit invocation");
            popup.RewardButton.ThrowEnabled=false; popup.RewardButton.ButtonEnabled=true; popup.RewardButton.gameObject.activeInHierarchy=false;
            Check(BridgeMod.ReadIconRewardChoice(popup)==null,"inactive physical control is not exposed");
            Check(!(bool)Call(h,"PopupChoose","17")["ok"] && popup.Calls==1,"hidden reward cannot be invoked");
            popup.RewardButton.gameObject.activeInHierarchy=true; popup.Throw=true;
            var uncertain=Call(h,"PopupChoose","17");
            Check((string)uncertain["errorCode"]=="POPUP_CALLBACK_UNCERTAIN" && (bool)uncertain["callbackAttempted"] && !(bool)uncertain["retryAllowed"],"callback exception retains uncertainty and no-retry marker");
            Check(popup.Calls==2,"uncertain native callback not retried through another fallback");
            Check((string)uncertain["outcome"]=="unknown" && !uncertain.ContainsKey("dispatched"),
                "physical reward callback failure never claims nonexecution");
            popup.RewardButton=null;
            Check(BridgeMod.ReadIconRewardChoice(popup)==null,"missing native control stays unavailable");
            Check(!BridgeMod.IsIconRewardPopup(new object()) && BridgeMod.ReadIconRewardChoice(new object())==null,"unrelated popup cannot acquire icon reward behavior");
            Console.WriteLine("ALL PASS "+passed); return 0;
        }
        catch(Exception e) { Console.WriteLine(e); return 1; }
    }
}
