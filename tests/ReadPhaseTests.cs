using System;
using System.Collections;
using System.Reflection;
using PolytopiaAgent;
using PolytopiaCityOps;

public static class GameManager
{
    public static int Reads;
    public static object GameState { get { Reads++; return null; } }
}
internal static class ReadPhaseTests
{
    private static int passed;
    private static void Check(bool ok, string name)
    { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    private static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static object Call(object target, string name, params object[] args)
    { return target.GetType().GetMethod(name, Flags).Invoke(target, args); }
    public static int Main()
    {
        try { return Run(); }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
    }
    private static int Run()
    {
        long now=10; long[] saved=null; bool[] visited=null; int published=0;
        var timing = new BridgeMod.CityReadTiming(delegate(long[] ticks, bool[] used) { saved=ticks; visited=used; published++; }, delegate { return now; });
        now=20; timing.Next(BridgeMod.CityReadPhase.Bindings);
        now=35; timing.Next(BridgeMod.CityReadPhase.Cities);
        now=41; timing.Dispose(); timing.Dispose(); timing.Next(BridgeMod.CityReadPhase.Serialization);
        Check(published==1 && saved[0]==10 && saved[1]==15 && saved[2]==6, "ordered phase timings are disjoint and disposal publishes once");
        Check(visited[0] && visited[1] && visited[2] && !visited[10], "unvisited phases remain absent from sample counts");
        int caught=0; var expected=new Exception("query failure"); now=100;
        try
        {
            using(var scope=new BridgeMod.CityReadTiming(delegate(long[] ticks, bool[] used) { saved=ticks; published++; },delegate { return now; }))
            { now=120; throw expected; }
        }
        catch(Exception error) { if(object.ReferenceEquals(error,expected)) caught++; }
        Check(caught==1 && published==2 && saved[0]==20, "query exception still propagates while its elapsed phase is recorded");
        now=30; using(var scope=new BridgeMod.CityReadTiming(delegate(long[] ticks, bool[] used) { saved=ticks; },delegate { return now; })) { now=20; }
        Check(saved[0]==0, "backward diagnostic clock never produces negative elapsed time");

        var host = new BridgeMod();
        typeof(BridgeMod).GetField("_combatHookHost",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,host);
        using(var scope=BridgeMod.BeginCityReadTiming()) { scope.Next(BridgeMod.CityReadPhase.Bindings); }
        Check(GameManager.Reads==0, "starting and ending diagnostic phases never reads native game state");
        string response=CityOptionsSnapshot.Snapshot();
        Check(response.Contains("gamestate-unavailable") && GameManager.Reads==1, "actual snapshot early return retains its original unavailable result");
        var summary=BridgeJsonCodec.Object((string)Call(host,"CityReadPhasesJson"));
        var phases=(System.Collections.Generic.Dictionary<string,object>)summary["phases"];
        var identity=(System.Collections.Generic.Dictionary<string,object>)phases["identity"];
        var bindings=(System.Collections.Generic.Dictionary<string,object>)phases["bindings"];
        Check(phases.Count==13 && BridgeJsonCodec.Scalar(identity["samples"])=="2"
            && BridgeJsonCodec.Scalar(bindings["samples"])=="1", "early return does not count later phases or lose earlier samples");
        for(int i=0;i<2000;i++) using(var scope=BridgeMod.BeginCityReadTiming()) { scope.Next(BridgeMod.CityReadPhase.Serialization); }
        summary=BridgeJsonCodec.Object((string)Call(host,"CityReadPhasesJson"));
        phases=(System.Collections.Generic.Dictionary<string,object>)summary["phases"];
        Check(phases.Count==13 && BridgeJsonCodec.Scalar(summary["calls"])=="2002", "many reads retain a fixed phase summary without per-request history");
        Check(((string)Call(host,"CityReadPhasesJson")).Length<2000 && GameManager.Reads==1,
            "diagnostic serialization remains bounded and cannot trigger a query");
        Console.WriteLine("TOTAL "+passed+" passed"); return 0;
    }
}
