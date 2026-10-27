using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using PolytopiaAgent;
internal static class LoaderHostTests
{
    static int passed;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    static void Main()
    {
        Check(typeof(BridgeMod).BaseType==typeof(object),"shared core does not inherit a loader");
        int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
        var one=BridgeMod.AcquireRuntimeLease(pid);
        Check(one!=null,"first host acquires process lease");
        var two=BridgeMod.AcquireRuntimeLease(pid);
        Check(two==null,"second host on the same thread cannot share the process lease");
        one.Release();one.Dispose();
        var three=BridgeMod.AcquireRuntimeLease(pid);
        Check(three!=null,"released process lease can be acquired after unload");three.Release();three.Dispose();
        Check(BridgeMod.PreserveProcessSeat(1)==1,"initial controlled seat captured once");
        Check(BridgeMod.PreserveProcessSeat(null)==1,"temporary configuration restoration does not erase seat after reload");
        bool changed=false;try{BridgeMod.PreserveProcessSeat(2);}catch(InvalidOperationException){changed=true;}
        Check(changed && BridgeMod.PreserveProcessSeat(null)==1,"reload cannot switch another human seat");
        var core=new BridgeMod();
        var f=BindingFlags.NonPublic|BindingFlags.Instance;
        var tick=typeof(BridgeMod).GetField("_tick",f);
        long before=(long)tick.GetValue(core);core.TickRuntime();
        Check((long)tick.GetValue(core)==before,"inactive core never polls or updates native state");
        Check(!core.RuntimeHasPendingWork(),"new host has no pending operation");
        var tickets=(Dictionary<long,BridgeMod.CombatTicket>)typeof(BridgeMod).GetField("_combatTickets",f).GetValue(core);
        tickets.Add(1,new BridgeMod.CombatTicket{Dispatched=true,Accepted=true});
        Check(core.RuntimeHasPendingWork(),"dispatched unsettled ticket blocks unload");
        typeof(BridgeMod).GetField("_runtimeRunning",f).SetValue(core,true);
        Check(!core.StopRuntime(),"unload is refused before losing pending evidence");
        Check((bool)typeof(BridgeMod).GetField("_runtimeRunning",f).GetValue(core),"refused unload preserves running host");
        tickets[1].FinalFrame=new BridgeMod.CombatFrame();
        Check(!core.RuntimeHasPendingWork(),"frozen final ticket does not block unload");
        typeof(BridgeMod).GetField("_runtimeRunning",f).SetValue(core,false);
        Check(core.StopRuntime(),"already stopped host unload is idempotent");
        Console.WriteLine("LoaderHostTests: "+passed+" passed");
    }
}
