using System;
using System.Diagnostics;
using System.Globalization;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private readonly NativeExecutionTimings _nativeExecutionTimings = new NativeExecutionTimings();
        [ThreadStatic] private static NativeExecutionScope _nativeExecutionTimingScope;
        private sealed class NoExecutionTiming : IDisposable { public void Dispose() {} }
        private static readonly IDisposable NoExecutionTimingScope = new NoExecutionTiming();

        internal sealed class NativeExecutionTimings
        {
            internal long Samples, InvalidSamples, CallbackCalls;
            internal double InvokeMs, CallbackMs, MaxInvokeMs, MaxCallbackMs;
            internal void Record(long ticks, long callbackTicks, long calls, long frequency, bool invalid)
            {
                if(invalid || frequency<=0 || ticks<0 || callbackTicks<0 || callbackTicks>ticks)
                { InvalidSamples++; return; }
                double elapsed=ticks*1000.0/frequency, callbacks=callbackTicks*1000.0/frequency;
                Samples++; CallbackCalls+=calls; InvokeMs+=elapsed; CallbackMs+=callbacks;
                MaxInvokeMs=Math.Max(MaxInvokeMs,elapsed); MaxCallbackMs=Math.Max(MaxCallbackMs,callbacks);
            }
            internal string Json()
            {
                return new JsonObj().Str("schema","native-execution-timing-1")
                    .Str("scope","InvokeCommandExecutor method.Invoke wall time; callback intervals union within each invocation; combat and public-marker callbacks only; callback bodies may invoke native work; remaining time is not proof of original AI cost")
                    .Raw("samples",Samples.ToString(CultureInfo.InvariantCulture))
                    .Raw("invalidSamples",InvalidSamples.ToString(CultureInfo.InvariantCulture))
                    .Raw("callbackCalls",CallbackCalls.ToString(CultureInfo.InvariantCulture))
                    .Raw("invokeMs",InvokeMs.ToString("F3",CultureInfo.InvariantCulture))
                    .Raw("callbackBodyMs",CallbackMs.ToString("F3",CultureInfo.InvariantCulture))
                    .Raw("maxInvokeMs",MaxInvokeMs.ToString("F3",CultureInfo.InvariantCulture))
                    .Raw("maxCallbackBodyMs",MaxCallbackMs.ToString("F3",CultureInfo.InvariantCulture)).Close();
            }
        }

        internal sealed class NativeExecutionScope : IDisposable
        {
            private readonly NativeExecutionTimings destination;
            private readonly Func<long> clock;
            private readonly long frequency, started;
            private readonly NativeExecutionScope parent;
            private int callbackDepth;
            private long callbackStarted, callbackTicks, callbackCalls;
            private bool closed, invalid;
            internal NativeExecutionScope(NativeExecutionTimings receiver, Func<long> timestamp, long ticksPerSecond)
            {
                destination=receiver; clock=timestamp; frequency=ticksPerSecond; started=clock();
                parent=_nativeExecutionTimingScope; _nativeExecutionTimingScope=this;
            }
            internal IDisposable Callback()
            {
                if(closed) return NoExecutionTimingScope;
                for(var scope=this;scope!=null;scope=scope.parent) scope.StartCallback();
                return new CallbackScope(this);
            }
            private void StartCallback()
            {
                if(closed) return;
                callbackCalls++;
                if(callbackDepth++==0) callbackStarted=clock();
            }
            private void EndCallback()
            {
                if(closed) return;
                if(callbackDepth<=0) { invalid=true; return; }
                if(--callbackDepth==0)
                {
                    long ended=clock();
                    if(ended<callbackStarted) invalid=true;
                    else callbackTicks+=ended-callbackStarted;
                }
            }
            private sealed class CallbackScope : IDisposable
            {
                private NativeExecutionScope owner;
                internal CallbackScope(NativeExecutionScope value) { owner=value; }
                public void Dispose()
                {
                    if(owner==null) return; var value=owner; owner=null;
                    for(var scope=value;scope!=null;scope=scope.parent) scope.EndCallback();
                }
            }
            public void Dispose()
            {
                if(closed) return;
                long ended=clock(); closed=true;
                if(object.ReferenceEquals(_nativeExecutionTimingScope,this)) _nativeExecutionTimingScope=parent;
                else invalid=true;
                destination.Record(ended-started,callbackTicks,callbackCalls,frequency,
                    invalid || callbackDepth!=0 || ended<started);
            }
        }

        private static IDisposable BeginNativeExecutionTiming()
        {
            var host=_performanceHost ?? _combatHookHost;
            return host==null ? NoExecutionTimingScope : (IDisposable)new NativeExecutionScope(
                host._nativeExecutionTimings,Stopwatch.GetTimestamp,Stopwatch.Frequency);
        }
        internal static IDisposable MeasureCommandCallback()
        {
            var scope=_nativeExecutionTimingScope;
            return scope==null ? NoExecutionTimingScope : scope.Callback();
        }
    }
}
