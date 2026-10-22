// codex-dsh-072 reflection-cache tests: differential behaviour against the production
// MemberWithAccessor, plus a metadata-resolution benchmark.
//
// Run:  ReflectionPerformanceTests.exe <path-to-PolytopiaAgentBridge.dll>
// The production DLL is loaded by reflection; the two member accessors are bound to delegates so the
// measured loop contains no harness reflection. Test types live in THIS assembly, so the production
// cache is exercised against real types it has never seen.
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

internal static class ReflectionPerformanceTests
{
    private static int passed, failed;
    private static readonly System.Collections.Generic.List<string> failures =
        new System.Collections.Generic.List<string>();

    private static void Check(bool value, string name, string detail)
    {
        if (value) { passed++; Console.WriteLine("PASS  " + name); }
        else
        {
            failed++;
            failures.Add(name + (detail == null ? "" : "  :: " + detail));
            Console.WriteLine("FAIL  " + name + (detail == null ? "" : "  :: " + detail));
        }
    }

    private static void CheckEq(object expected, object actual, string name)
    {
        bool ok = expected == null ? actual == null : expected.Equals(actual);
        Check(ok, name, ok ? null : "expected " + (expected ?? "null") + " got " + (actual ?? "null"));
    }

    // ------------------------------------------------------------------ test types
    internal class Alpha
    {
        public int Hp = 10;
        public string Name = "alpha";
        public int Level = 99;                                  // the getter must win over this
        public int get_Level() { return 7; }
        private int Secret = 3;
        private int _value = 1;
        public int Value { get { return _value; } set { _value = value; } }
        public int Throwing = 42;                               // reached after the throwing getter
        public int get_Throwing() { throw new InvalidOperationException("getter-throws"); }
        public int Param = 5;                                   // the parameterised getter is skipped
        public int get_Param(int i) { throw new InvalidOperationException("param-getter-must-not-run"); }
        [System.Runtime.CompilerServices.IndexerName("IndexedThing")]
        public int this[int i] { get { throw new InvalidOperationException("indexer-must-not-run"); } }
    }

    internal class Beta                                          // same names, different Type
    {
        public int Hp = 200;
        public string Name = "beta";
        public int get_Level() { return 77; }
        public int Item = 90;
    }

    internal class BaseSetOnly
    {
        private int _v;
        public int Y { set { _v = value; } }                     // set-only: no get_Y exists
    }

    internal class DerivedField : BaseSetOnly
    {
        public new int Y = 333;                                  // field must be tried before the property
    }

    internal class BaseSetOnlyAmb
    {
        public int Q { set { } }
    }

    internal class DerivedSetOnlyAmb : BaseSetOnlyAmb
    {
        public new string Q { set { } }                          // hiding with a different type
    }

    // ------------------------------------------------------------------ harness
    private static Func<object, string, object> original, cached;
    private static Action reset;
    private static Func<Type, string, string> shape;
    private static PropertyInfo pBuilds, pHits, pFallbacks, pPlans;

    private static MethodInfo Find(Type t, string name, Type[] ps)
    {
        foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != name) { continue; }
            var have = m.GetParameters();
            if (have.Length != ps.Length) { continue; }
            bool ok = true;
            for (int i = 0; i < ps.Length; i++) { if (have[i].ParameterType != ps[i]) { ok = false; break; } }
            if (ok) { return m; }
        }
        throw new Exception("method not found: " + name);
    }

    private static PropertyInfo Prop(Type t, string name)
    {
        var p = t.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (p == null) { throw new Exception("property not found: " + name); }
        return p;
    }

    private static int Stat(PropertyInfo p) { return (int)p.GetValue(null, null); }

    private static string Invoke(Delegate d, params object[] args) { return (string)d.DynamicInvoke(args); }

    private static string TryCall(Func<object, string, object> fn, object o, string name, out object value)
    {
        value = null;
        try { value = fn(o, name); return null; }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    private static void Main(string[] args)
    {
        int exit = Run(args);
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        for (int i = 0; i < failures.Count; i++) { Console.WriteLine("  FAILED " + failures[i]); }
        Environment.ExitCode = exit;
    }

    private static int Run(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("usage: ReflectionPerformanceTests <dll>"); return 2; }
        var assembly = Assembly.LoadFrom(args[0]);
        var type = assembly.GetType("PolytopiaAgent.BridgeMod", true);
        // The reference implementation is the UNCACHED accessor. After the supervisor's wiring,
        // MemberWithAccessor delegates to CachedMemberWithAccessor, so binding MemberWithAccessor
        // here would compare the cache with itself (and recurse). Prefer the uncached name; fall
        // back only if it is not present in this build.
        MethodInfo referenceMethod = null;
        try { referenceMethod = Find(type, "UncachedMemberWithAccessor",
            new[] { typeof(object), typeof(string) }); }
        catch (Exception) { }
        if (referenceMethod == null)
        {
            referenceMethod = Find(type, "MemberWithAccessor", new[] { typeof(object), typeof(string) });
            Console.WriteLine("NOTE: UncachedMemberWithAccessor not found; comparing against "
                + "MemberWithAccessor (pre-wiring build)");
        }
        else { Console.WriteLine("NOTE: reference implementation = UncachedMemberWithAccessor"); }
        original = (Func<object, string, object>)Delegate.CreateDelegate(
            typeof(Func<object, string, object>), referenceMethod);
        cached = (Func<object, string, object>)Delegate.CreateDelegate(
            typeof(Func<object, string, object>),
            Find(type, "CachedMemberWithAccessor", new[] { typeof(object), typeof(string) }));
        reset = (Action)Delegate.CreateDelegate(typeof(Action),
            Find(type, "ResetReflectionCacheForTests", Type.EmptyTypes));
        shape = (Func<Type, string, string>)Delegate.CreateDelegate(typeof(Func<Type, string, string>),
            Find(type, "ReflectionPlanShape", new[] { typeof(Type), typeof(string) }));
        pBuilds = Prop(type, "ReflectionPlanBuilds");
        pHits = Prop(type, "ReflectionPlanHits");
        pFallbacks = Prop(type, "ReflectionFallbacks");
        pPlans = Prop(type, "ReflectionPlanCount");

        // ------------- 1. differential sweep: same result for the same type/name -------------
        Console.WriteLine("=== differential sweep: cached == original, value for value ===");
        reset();
        var alpha = new Alpha();
        var beta = new Beta();
        var derived = new DerivedField();
        var amb = new DerivedSetOnlyAmb();
        string[] names = new string[] { "Hp", "hp", "Name", "name", "Level", "level", "Secret", "secret",
            "Value", "value", "Throwing", "throwing", "Param", "param", "IndexedThing", "indexedthing",
            "Missing", "missing" };
        object[] objects = new object[] { alpha, beta, derived, amb };
        int comparisons = 0, valueMatches = 0, exceptionMatches = 0;
        for (int oi = 0; oi < objects.Length; oi++)
        {
            for (int ni = 0; ni < names.Length; ni++)
            {
                object ov, cv;
                string oEx = TryCall(original, objects[oi], names[ni], out ov);
                string cEx = TryCall(cached, objects[oi], names[ni], out cv);
                comparisons++;
                if (oEx == null && cEx == null && Equals(ov, cv)) { valueMatches++; }
                else if ((oEx != null || cEx != null) && oEx == cEx) { exceptionMatches++; }
                else
                {
                    Check(false, "differential " + objects[oi].GetType().Name + "." + names[ni],
                        "original=" + (oEx ?? (ov == null ? "null" : ov.ToString()))
                        + " cached=" + (cEx ?? (cv == null ? "null" : cv.ToString())));
                }
            }
        }
        Check(comparisons == objects.Length * names.Length,
            comparisons + " differential lookups ran", null);
        Check(valueMatches + exceptionMatches == comparisons,
            "cached and original agree on every lookup (" + valueMatches + " values, "
            + exceptionMatches + " exceptions)", null);

        // ------------- 2. the accessor ORDER and the duplicate attempts -------------
        Console.WriteLine("=== resolution order (getter -> field -> property), duplicates kept ===");
        CheckEq("getter:get_Level,getter:get_Level,field:Level,field:Level",
            shape(typeof(Alpha), "Level"),
            "Level: both getter spellings win over the field (identical spellings tried twice)");
        CheckEq("getter:get_Level,field:Level",
            shape(typeof(Alpha), "level"),
            "level: pascal getter first, then the pascal field - field before property");
        CheckEq("field:Y,property:Y", shape(typeof(DerivedField), "y"),
            "y: the derived field is tried before the inherited set-only property");
        CheckEq("field:Param,field:Param",
            shape(typeof(Alpha), "Param"),
            "Param: the parameterised getter is skipped, never invoked");
        CheckEq("miss", shape(typeof(Alpha), "IndexedThing"),
            "IndexedThing: only an indexer exists, so it is skipped and the plan is a miss");
        CheckEq("miss", shape(typeof(Alpha), "Missing"), "a miss is recorded as an empty plan");

        // ------------- 3. live values: no instance and no value is cached -------------
        Console.WriteLine("=== live values: dynamic HP, several instances, several Types ===");
        int before = Stat(pBuilds);
        alpha.Hp = 1234;
        CheckEq(1234, cached(alpha, "Hp"), "a field changed after the plan was built is read fresh");
        alpha.Hp = 4321;
        CheckEq(4321, cached(alpha, "Hp"), "and again for the second change");
        CheckEq(200, cached(beta, "Hp"), "the other instance of another Type keeps its own value");
        CheckEq("beta", cached(beta, "name"), "the other Type's pascal field is separate");
        alpha.Value = 555;
        CheckEq(555, cached(alpha, "Value"), "a property value changed after caching is read fresh");
        CheckEq(7, cached(alpha, "Level"), "the getter still wins after caching");
        CheckEq(42, cached(alpha, "Throwing"), "a throwing getter still falls through to the field");
        CheckEq(5, cached(alpha, "Param"), "a parameterised getter is still never invoked");
        CheckEq(null, cached(alpha, "IndexedThing"), "an indexer is skipped, never invoked (null)");
        CheckEq("alpha", cached(alpha, "Name"), "a plain field still resolves");
        CheckEq(null, cached(alpha, "Missing"), "a miss still returns null");
        CheckEq(null, cached(null, "Hp"), "a null target returns null");
        CheckEq(null, cached(alpha, ""), "an empty member name returns null");
        Check(before > 0, "plans were built lazily on first use", null);

        // ------------- 4. one plan per (Type, name), including misses -------------
        Console.WriteLine("=== one plan per (Type, name), misses included ===");
        reset();
        for (int i = 0; i < 100; i++) { cached(alpha, "Hp"); cached(alpha, "Missing"); cached(beta, "Hp"); }
        CheckEq(3, Stat(pBuilds), "300 lookups over 3 (Type, name) pairs built exactly 3 plans");
        CheckEq(3, Stat(pPlans), "all three plans (including the miss) are cached");
        CheckEq(297, Stat(pHits) >= 297 ? 297 : Stat(pHits),
            "the remaining lookups were served from the cache");
        CheckEq(0, Stat(pFallbacks), "no fallback was needed for ordinary members");

        // ------------- 5. ambiguity and other lookup failures stay faithful -------------
        Console.WriteLine("=== metadata ambiguity: same behaviour as MemberWithAccessor ===");
        reset();
        object ambOut, ambCached;
        string ao = TryCall(original, amb, "Q", out ambOut);
        string co = TryCall(cached, amb, "Q", out ambCached);
        CheckEq(ao, co, "an ambiguous metadata lookup behaves identically (both "
            + (ao == null ? "resolve" : "throw " + ao) + ")");
        CheckEq(ao == null ? null : ambOut, co == null ? null : ambCached, "and yields the same value");
        string ao2 = TryCall(original, amb, "q", out ambOut);
        string co2 = TryCall(cached, amb, "q", out ambCached);
        CheckEq(ao2, co2, "the pascal spelling behaves identically too");

        // ------------- 6. concurrency: one plan, no torn reads -------------
        Console.WriteLine("=== concurrent cache access ===");
        reset();
        int threads = 8, perThread = 5000;
        int wrong = 0;
        var errors = new System.Collections.Generic.List<string>();
        var workers = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            workers[t] = new Thread(delegate ()
            {
                try
                {
                    for (int i = 0; i < perThread; i++)
                    {
                        object v = cached(alpha, "Hp");
                        if (!Equals(v, 4321)) { Interlocked.Increment(ref wrong); }
                        object w = cached(alpha, "Missing");
                        if (w != null) { Interlocked.Increment(ref wrong); }
                    }
                }
                catch (Exception ex) { lock (errors) { errors.Add(ex.GetType().Name + ": " + ex.Message); } }
            });
        }
        for (int t = 0; t < threads; t++) { workers[t].Start(); }
        for (int t = 0; t < threads; t++) { workers[t].Join(); }
        CheckEq(0, wrong, "every concurrent read returned the current value");
        Check(errors.Count == 0, "no thread saw an exception", errors.Count == 0 ? null : errors[0]);
        CheckEq(2, Stat(pBuilds), "8x5000 concurrent lookups over 2 keys built exactly 2 plans");
        Check(Stat(pHits) >= threads * perThread * 2 - 4, "the rest were cache hits (" + Stat(pHits) + ")", null);

        // ------------- 7. benchmark: metadata resolutions and wall time -------------
        Console.WriteLine("=== metadata resolution + wall time (NOT a Unity FPS claim) ===");
        reset();
        const int ROUNDS = 4, PER = 50000;
        var types = new Type[] { typeof(Alpha), typeof(Beta) };
        string[] benchNames = new string[] { "Hp", "Level", "Value", "Missing" };
        object[] instances = new object[] { alpha, beta };
        double origUs = double.MaxValue, cachedUs = double.MaxValue;
        long origOk = 0, cachedOk = 0;
        var sw = new Stopwatch();
        for (int round = 0; round < ROUNDS; round++)
        {
            sw.Restart();
            for (int i = 0; i < PER; i++)
            {
                int idx = i % (instances.Length * benchNames.Length);
                object inst = instances[idx % instances.Length];
                string name = benchNames[idx / instances.Length];
                if (original(inst, name) != null) { origOk++; }
            }
            sw.Stop();
            origUs = Math.Min(origUs, sw.Elapsed.TotalMilliseconds * 1000.0 / PER);
            sw.Restart();
            for (int i = 0; i < PER; i++)
            {
                int idx = i % (instances.Length * benchNames.Length);
                object inst = instances[idx % instances.Length];
                string name = benchNames[idx / instances.Length];
                if (cached(inst, name) != null) { cachedOk++; }
            }
            sw.Stop();
            cachedUs = Math.Min(cachedUs, sw.Elapsed.TotalMilliseconds * 1000.0 / PER);
        }
        CheckEq(origOk, cachedOk, "both implementations returned the same number of non-null values");
        int distinctKeys = types.Length * benchNames.Length;
        CheckEq(distinctKeys, Stat(pBuilds),
            "the cached run built exactly one plan per distinct (Type, name): " + distinctKeys);
        Check(Stat(pHits) >= ROUNDS * PER, "the cached run served every lookup from a plan ("
            + Stat(pHits) + " hits)", null);
        Console.WriteLine("      original " + origUs.ToString("F3") + "us/lookup, cached "
            + cachedUs.ToString("F3") + "us/lookup over " + (ROUNDS * PER) + " lookups (best of "
            + ROUNDS + "x" + PER + ")");
        Console.WriteLine("      metadata resolutions: original = every lookup ("
            + (ROUNDS * PER) + "), cached = " + Stat(pBuilds) + " plan builds");
        Console.WriteLine("      speedup " + (cachedUs > 0 ? (origUs / cachedUs).ToString("F1") : "?") + "x");

        Console.WriteLine("=".PadRight(70, '='));
        return failed == 0 ? 0 : 1;
    }
}
