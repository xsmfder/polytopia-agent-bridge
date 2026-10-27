// Read actual production city projection and share the native null-list convention.
using System;
using System.Collections.Generic;
using System.Reflection;
using PolytopiaAgent;
public class PendingRewardState { public object pendingCommandTriggers; public PendingRewardState(object value) { pendingCommandTriggers = value; } }
public class ThrowingRewardState { public object pendingCommandTriggers { get { throw new Exception("unreadable"); } } }
public class CityRewardCommandTrigger { public string type="CityLevelUp"; public byte playerId=1; public string Coordinates="(5,6)"; }
public class ForeignRewardCommandTrigger {
    public string type="CityLevelUp"; public byte playerId=2;
    public string Coordinates { get { throw new Exception("do not read foreign rewards"); } }
}
public static class PendingRewardReadTests {
    private static int passed;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; }
    private static string Project(object state) {
        var m=typeof(PolytopiaCityOps.CityOptionsSnapshot).GetMethod("PendingCityRewardJson", BindingFlags.NonPublic|BindingFlags.Static);
        return (string)m.Invoke(null,new object[]{state,(byte)1,null,null,null,null,new List<string>()});
    }
    public static void Main() {
        Check(Project(new PendingRewardState(null)).Contains("\"status\":\"none\""),"native known null is empty in city view");
        Check(Project(new PendingRewardState(new List<object>())).Contains("\"status\":\"none\""),"native empty list is empty");
        Check(Project(new object()).Contains("pendingcommandtriggers-not-enumerable"),"absent member remains unknown");
        Check(Project(new ThrowingRewardState()).Contains("pendingcommandtriggers-not-enumerable"),"throwing member remains unknown");
        Check(Project(new PendingRewardState(new object())).Contains("pendingcommandtriggers-not-enumerable"),"unreadable list remains unknown");
        Check(Project(new PendingRewardState(new List<object>{null})).Contains("unavailable"),"unreadable trigger is not empty");
        Check(Project(new PendingRewardState(new List<object>{new ForeignRewardCommandTrigger()})).Contains("\"status\":\"none\""),"foreign city coordinates never inspected");
        Check(Project(new PendingRewardState(new List<object>{new CityRewardCommandTrigger()})).Contains("trigger-present-popup-unreadable"),"own trigger without offer remains pending");
        Check(BridgeMod.PendingPopupAdvanceAllowed(true,true,true,false,true,false,1),"native queue advances only in known own visible context");
        Check(!BridgeMod.PendingPopupAdvanceAllowed(false,true,true,false,true,false,1),"hidden handoff cannot advance");
        Check(!BridgeMod.PendingPopupAdvanceAllowed(true,false,true,false,true,false,1),"unknown identity cannot advance");
        Check(!BridgeMod.PendingPopupAdvanceAllowed(true,true,false,false,true,false,1),"other turn cannot advance");
        Check(!BridgeMod.PendingPopupAdvanceAllowed(true,true,true,true,true,true,1),"visible popup cannot be skipped");
        Check(!BridgeMod.PendingPopupAdvanceAllowed(true,true,true,null,true,false,1),"unknown popup status cannot advance");
        Check(!BridgeMod.PendingPopupAdvanceAllowed(true,true,true,false,false,false,1),"unreadable current popup cannot advance");
        Check(!BridgeMod.PendingPopupAdvanceAllowed(true,true,true,false,true,true,1),"transitioning current popup cannot advance");
        Check(!BridgeMod.PendingPopupAdvanceAllowed(true,true,true,false,true,false,null),"unknown pending count cannot advance");
        Check(!BridgeMod.PendingPopupAdvanceAllowed(true,true,true,false,true,false,0),"empty queue cannot advance");
        Console.WriteLine("PendingRewardReadTests passed="+passed+" failed=0");
    }
}
