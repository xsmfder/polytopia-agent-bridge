using System;
using System.Collections;
using System.Reflection;
using PolytopiaAgent;

public static class GameManager
{
    public static int Reads;
    public static object GameState { get { Reads++; throw new Exception("diagnostics read game state"); } }
}

internal static class FrameTimingTests
{
    private static int passed;
    private static void Check(bool ok, string name)
    { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    private static double Number(IDictionary value, string key)
    { return Double.Parse(BridgeJsonCodec.Scalar(value[key]), System.Globalization.CultureInfo.InvariantCulture); }
    private static long Sum(IList values)
    { long sum=0; foreach(object value in values) sum+=Int64.Parse(BridgeJsonCodec.Scalar(value)); return sum; }
    public static int Main()
    {
        try { return Run(); }
        catch(Exception error) { Console.WriteLine("FAIL " + error); return 1; }
    }
    private static int Run()
    {
        var timings=new BridgeMod.UpdateFrameTimings();
        var value=(IDictionary)BridgeJsonCodec.Object(timings.Json());
        Check(Number(value,"frames")==0 && Number(value,"intervalCount")==0,
            "empty counters do not invent a first frame interval");
        timings.Record(0,2,1000); timings.Record(16,19,1000); timings.Record(50,55,1000);
        value=(IDictionary)BridgeJsonCodec.Object(timings.Json());
        Check(Number(value,"frames")==3 && Number(value,"intervalCount")==2,
            "zero timestamp is valid and first callback has no predecessor");
        Check(Number(value,"intervalMs")==50 && Number(value,"bridgeWorkMs")==10
            && Number(value,"maxIntervalMs")==34 && Number(value,"maxBridgeWorkMs")==5,
            "elapsed intervals and bridge work are separately measured");
        IList gaps=(IList)value["intervalHistogram"], work=(IList)value["bridgeWorkHistogram"];
        Check(gaps.Count==14 && work.Count==14 && BridgeJsonCodec.Scalar(gaps[4])=="1"
            && BridgeJsonCodec.Scalar(gaps[6])=="1" && Sum(gaps)==2,
            "interval buckets include exact upper boundaries without overlap");
        Check(BridgeJsonCodec.Scalar(work[1])=="1" && BridgeJsonCodec.Scalar(work[2])=="1"
            && BridgeJsonCodec.Scalar(work[3])=="1" && Sum(work)==3,
            "work histogram counts each completed callback once");
        timings.Record(40,41,1000); timings.Record(60,59,1000); timings.Record(70,71,0);
        timings.Record(100,101,1000);
        value=(IDictionary)BridgeJsonCodec.Object(timings.Json());
        Check(Number(value,"invalidSamples")==3 && Number(value,"intervalCount")==2
            && Number(value,"bridgeWorkMs")==11 && Sum((IList)value["bridgeWorkHistogram"])==4,
            "invalid clock samples break continuity without becoming zero measurements");
        timings.Record(1100,1700,1000);
        value=(IDictionary)BridgeJsonCodec.Object(timings.Json());
        Check(BridgeJsonCodec.Scalar(((IList)value["intervalHistogram"])[13])=="1"
            && BridgeJsonCodec.Scalar(((IList)value["bridgeWorkHistogram"])[13])=="1",
            "long stalls remain visible in the overflow bucket");
        string before=timings.Json(); timings.Json();
        Check(timings.Json()==before, "serializing counters does not change measurements");
        for(int i=0;i<10000;i++) timings.Record(2000+i*16,2001+i*16,1000);
        value=(IDictionary)BridgeJsonCodec.Object(timings.Json());
        Check(((IList)value["intervalHistogram"]).Count==14 && timings.Json().Length<1200
            && Sum((IList)value["intervalHistogram"])==Number(value,"intervalCount"),
            "many callbacks retain only fixed buckets and scalar totals");

        var flags=BindingFlags.NonPublic|BindingFlags.Static;
        var performance=typeof(BridgeMod).GetField("_performanceHost",flags);
        var combat=typeof(BridgeMod).GetField("_combatHookHost",flags);
        object previousPerformance=performance.GetValue(null), previousCombat=combat.GetValue(null);
        try
        {
            performance.SetValue(null,new BridgeMod()); combat.SetValue(null,null);
            value=(IDictionary)BridgeJsonCodec.Object(BridgeMod.PerformanceCountersJson());
            Check(BridgeJsonCodec.Scalar(value["ok"])=="true"
                && BridgeJsonCodec.Scalar(value["combatRuntimeAvailable"])=="false"
                && value["updateFrames"] is IDictionary, "diagnostic host works before combat runtime initialization");
            using(var scope=BridgeMod.BeginCityReadTiming()) {}
            value=(IDictionary)BridgeJsonCodec.Object(BridgeMod.PerformanceCountersJson());
            Check(Number((IDictionary)value["cityReadPhases"],"calls")==1 && GameManager.Reads==0,
                "early diagnostics and city phase timing never invoke native game getters");
            performance.SetValue(null,null);
            value=(IDictionary)BridgeJsonCodec.Object(BridgeMod.PerformanceCountersJson());
            Check(BridgeJsonCodec.Scalar(value["ok"])=="false" && !value.Contains("updateFrames"),
                "absent runtime stays unavailable rather than reporting zero frame evidence");
        }
        finally { performance.SetValue(null,previousPerformance); combat.SetValue(null,previousCombat); }
        Console.WriteLine("TOTAL " + passed + " passed"); return 0;
    }
}
