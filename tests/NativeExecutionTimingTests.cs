using System;
using System.Reflection;
using System.Threading;
using PolytopiaAgent;

internal static class NativeExecutionTimingTests
{
    private static int passed;
    private static void Check(bool ok,string name)
    { if(!ok) throw new Exception(name); passed++; Console.WriteLine("PASS "+name); }
    private sealed class Manager
    {
        internal int Calls;
        internal readonly Exception Failure=new InvalidOperationException("native failure");
        public bool Execute(object command,out string error)
        { Calls++; using(BridgeMod.MeasureCommandCallback()) {} error="original validation"; return false; }
        public bool Throw(object command,out string error) { Calls++; error=null; throw Failure; }
    }
    public static int Main()
    {
        try { return Run(); }
        catch(Exception error) { Console.WriteLine("FAIL "+error); return 1; }
    }
    private static int Run()
    {
        var stats=new BridgeMod.NativeExecutionTimings(); long now=0;
        var execution=new BridgeMod.NativeExecutionScope(stats,delegate {return now;},1000);
        now=10; var first=BridgeMod.MeasureCommandCallback();
        now=15; var nested=BridgeMod.MeasureCommandCallback();
        now=20; nested.Dispose(); nested.Dispose();
        now=30; first.Dispose(); first.Dispose();
        now=100; execution.Dispose(); execution.Dispose();
        Check(stats.Samples==1 && stats.InvokeMs==100,"deterministic executor window publishes once");
        Check(stats.CallbackCalls==2 && stats.CallbackMs==20 && stats.MaxCallbackMs==20,
            "nested callbacks count their wall interval union without double counting");
        var idle=BridgeMod.MeasureCommandCallback(); idle.Dispose();
        Check(stats.CallbackCalls==2 && object.ReferenceEquals(BridgeMod.MeasureCommandCallback(),BridgeMod.MeasureCommandCallback()),
            "outside command callbacks reuse the same no-op scope");

        now=200; using(var scope=new BridgeMod.NativeExecutionScope(stats,delegate{return now;},1000))
        {
            var thread=new Thread(delegate(){using(BridgeMod.MeasureCommandCallback()) {}}); thread.Start(); thread.Join();
            now=210;
        }
        Check(stats.Samples==2 && stats.CallbackCalls==2 && stats.InvokeMs==110,
            "other threads cannot enter this command callback window");
        var innerStats=new BridgeMod.NativeExecutionTimings();
        now=300; using(var outer=new BridgeMod.NativeExecutionScope(stats,delegate{return now;},1000))
        {
            now=310; using(var inner=new BridgeMod.NativeExecutionScope(innerStats,delegate{return now;},1000))
            { now=320; using(BridgeMod.MeasureCommandCallback()) { now=325; } now=330; }
            now=340; using(BridgeMod.MeasureCommandCallback()) {now=350;} now=400;
        }
        Check(innerStats.InvokeMs==20 && innerStats.CallbackMs==5 && innerStats.CallbackCalls==1,
            "nested executor window records only its callbacks");
        Check(stats.InvokeMs==210 && stats.CallbackMs==35 && stats.CallbackCalls==4,
            "parent window includes nested callbacks and resumes after inner execution");
        now=500; try
        {
            using(var scope=new BridgeMod.NativeExecutionScope(stats,delegate{return now;},1000))
            using(BridgeMod.MeasureCommandCallback()) {now=510; throw new InvalidOperationException("body");}
        }
        catch(InvalidOperationException) {}
        Check(stats.Samples==4 && stats.InvokeMs==220 && stats.CallbackMs==45,
            "exceptional execution records elapsed time while preserving the exception");
        now=600; using(var scope=new BridgeMod.NativeExecutionScope(stats,delegate{return now;},1000)) {now=590;}
        now=700; using(var scope=new BridgeMod.NativeExecutionScope(stats,delegate{return now;},0)) {now=710;}
        Check(stats.InvalidSamples==2 && stats.Samples==4,"invalid clocks are not reported as zero execution cost");
        for(int i=0;i<1000;i++)
        { now=1000+i*10; using(var scope=new BridgeMod.NativeExecutionScope(stats,delegate{return now;},1000)) {now++;} }
        string json=stats.Json();
        Check(stats.Samples==1004 && json.Length<1000 && json==stats.Json(),
            "many commands retain only scalar totals and nonmutating diagnostics");

        var flags=BindingFlags.NonPublic|BindingFlags.Static;
        var hostField=typeof(BridgeMod).GetField("_performanceHost",flags); object old=hostField.GetValue(null);
        var host=new BridgeMod(); var manager=new Manager(); hostField.SetValue(null,host);
        try
        {
            var method=typeof(BridgeMod).GetMethod("InvokeCommandExecutor",flags);
            object[] args={typeof(Manager).GetMethod("Execute"),manager,new object(),null};
            object result=method.Invoke(null,args);
            Check(result is bool && !(bool)result && (string)args[3]=="original validation" && manager.Calls==1,
                "instrumented actual executor preserves return, out value and single invocation");
            var summary=BridgeJsonCodec.Object(BridgeMod.PerformanceCountersJson());
            var native=(System.Collections.Generic.Dictionary<string,object>)summary["nativeExecution"];
            Check(BridgeJsonCodec.Scalar(native["samples"])=="1" && BridgeJsonCodec.Scalar(native["callbackCalls"])=="1",
                "actual executor publishes measured callback evidence in diagnostics");
            args=new object[]{typeof(Manager).GetMethod("Throw"),manager,new object(),null}; bool original=false;
            try {method.Invoke(null,args);} catch(TargetInvocationException error)
            {original=error.InnerException is TargetInvocationException && object.ReferenceEquals(error.InnerException.InnerException,manager.Failure);}
            Check(original && manager.Calls==2,"instrumentation preserves native failure wrapping and never retries");
            summary=BridgeJsonCodec.Object(BridgeMod.PerformanceCountersJson());
            native=(System.Collections.Generic.Dictionary<string,object>)summary["nativeExecution"];
            Check(BridgeJsonCodec.Scalar(native["samples"])=="2","failed native invocation still closes its measurement window");
        }
        finally {hostField.SetValue(null,old);}
        Console.WriteLine("TOTAL "+passed+" passed"); return 0;
    }
}
