using System;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PolytopiaAgent;

internal static class NativeMethodPlanTests
{
    private sealed class CountingType : TypeDelegator
    {
        internal int Scans;
        internal bool ThrowNext;
        internal CountingType(Type type) : base(type) { }
        public override MethodInfo[] GetMethods(BindingFlags flags)
        {
            Interlocked.Increment(ref Scans);
            if (ThrowNext) { ThrowNext = false; throw new InvalidOperationException("transient metadata read"); }
            return base.GetMethods(flags);
        }
    }
    public sealed class A { public enum Type { X } }
    public sealed class B { public enum Type { X } }
    public sealed class Fixture
    {
        public int Reads;
        public int Read(int value) { Reads++; return value + Reads; }
        public bool Read(bool value) { Reads++; return value; }
        public static int Static(int value) { return value; }
        public bool HasType(A.Type value) { return true; }
        public bool HasType(B.Type value) { return false; }
    }
    public class RetryFixture { public int Read(int value) { return value; } }
    public class ConcurrentFixture : RetryFixture { }
    private static int passed, failed;
    private static void Check(bool value, string name)
    { Console.WriteLine((value ? "PASS " : "FAIL ") + name); if (value) passed++; else failed++; }
    public static int Main()
    {
        var type = new CountingType(typeof(Fixture));
        var first = BridgeMod.FindUniqueNativeMethod(type, "Read", new[] { "Int32" }, "Int32", false);
        int scanned = type.Scans;
        bool identical = true;
        for (int i = 0; i < 100; i++)
            identical &= BridgeMod.FindUniqueNativeMethod(type, "Read", new[] { "Int32" }, "Int32", false) == first;
        Check(identical, "one hundred complete-signature lookups retain the same exact method");
        Check(first != null && type.Scans == scanned, "warm native method plans perform no repeated GetMethods scans");
        int before = type.Scans;
        for (int i = 0; i < 20; i++) BridgeMod.FindUniqueNativeMethod(type, "Absent", new string[0], "Void", false);
        Check(type.Scans == before + 1, "missing method metadata is cached without repeated scans");
        before = type.Scans;
        bool unavailable = true;
        for (int i = 0; i < 20; i++)
            unavailable &= BridgeMod.FindUniqueNativeMethod(type, "HasType", new[] { "Type" }, "Boolean", false) == null;
        Check(unavailable, "twenty ambiguous nested-enum lookups stay unavailable");
        Check(type.Scans == before + 1, "ambiguous method result is cached without selecting a first overload");
        var parameters = new[] { "Int32" };
        var iMethod = BridgeMod.FindUniqueNativeMethod(type, "Read", parameters, "Int32", false);
        parameters[0] = "Boolean";
        var bMethod = BridgeMod.FindUniqueNativeMethod(type, "Read", parameters, "Boolean", false);
        Check(iMethod != null && bMethod != null && iMethod != bMethod, "caller-owned parameter array mutation cannot change an existing metadata key");
        Check(BridgeMod.FindUniqueNativeMethod(type, "Read", new[] { "Int32" }, "Boolean", false) == null,
            "return type participates in key and binding");
        Check(BridgeMod.FindUniqueNativeMethod(type, "Static", new[] { "Int32" }, "Int32", false) == null
            && BridgeMod.FindUniqueNativeMethod(type, "Static", new[] { "Int32" }, "Int32", true) != null,
            "static and instance binding plans remain separate");
        var x = new Fixture(); var y = new Fixture();
        Check((int)first.Invoke(x, new object[] { 1 }) == 2 && (int)first.Invoke(x, new object[] { 1 }) == 3
            && (int)first.Invoke(y, new object[] { 1 }) == 2,
            "cached method metadata never caches target instances or their changing results");
        var transient = new CountingType(typeof(RetryFixture)) { ThrowNext = true };
        bool threw = false;
        try { BridgeMod.FindUniqueNativeMethod(transient, "Read", new[] { "Int32" }, "Int32", false); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw && BridgeMod.FindUniqueNativeMethod(transient, "Read", new[] { "Int32" }, "Int32", false) != null
            && transient.Scans == 2, "metadata exception does not poison the next explicit lookup");
        var concurrent = new CountingType(typeof(ConcurrentFixture));
        var workers = new Task<MethodInfo>[8];
        for (int i = 0; i < workers.Length; i++) workers[i] = Task.Factory.StartNew(delegate
            { return BridgeMod.FindUniqueNativeMethod(concurrent, "Read", new[] { "Int32" }, "Int32", false); });
        Task.WaitAll(workers); bool same = workers[0].Result != null;
        foreach (var worker in workers) same &= worker.Result == workers[0].Result;
        Check(same && concurrent.Scans == 1, "concurrent first lookup publishes one metadata scan");
        for (int i = 0; i < BridgeMod.MaxReflectionPlans + 5; i++)
            BridgeMod.FindUniqueNativeMethod(type, "Missing" + i, new string[0], "Void", false);
        var statsMethod = typeof(BridgeMod).GetMethod("NativeMethodPlanStatsJson", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var stats = statsMethod == null ? null : BridgeJsonCodec.Object((string)statsMethod.Invoke(null, null));
        before = type.Scans;
        BridgeMod.FindUniqueNativeMethod(type, "BeyondCapacity", new string[0], "Void", false);
        BridgeMod.FindUniqueNativeMethod(type, "BeyondCapacity", new string[0], "Void", false);
        Check(stats != null && int.Parse(BridgeJsonCodec.Scalar(stats["entries"])) == BridgeMod.MaxReflectionPlans
            && type.Scans == before + 2, "metadata cache stops growing at soft cap and resolves later keys without retaining them");
        before = type.Scans;
        Check(BridgeMod.FindUniqueNativeMethod(type, "Read", new[] { "Int32" }, "Int32", false) == first && type.Scans == before,
            "capacity does not evict existing warm methods");
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
