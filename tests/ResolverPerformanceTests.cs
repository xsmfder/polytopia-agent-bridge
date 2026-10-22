// codex-dsh-073 resolver-cache tests: differential against the real uncached resolvers, generation
// invalidation, concurrency and a cold-vs-warm benchmark.
//
// Run:  ResolverPerformanceTests.exe <path-to-PolytopiaAgentBridge.dll>
//
// The production DLL is loaded by reflection. The cache entry points and the UNCACHED reference
// implementations are bound to delegates so the measured loops carry no harness reflection.
// When the pre-wiring fixture is in the build (run_perf_tests.py adds it until the supervisor wires
// BridgeMod), the fixture is first verified against the genuine FindType / FindGameType /
// FindStaticMethod implementations that still exist in that build.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

internal static class ResolverPerformanceTests
{
    private static int passed, failed;
    private static readonly List<string> failures = new List<string>();

    private static void Check(bool value, string name)
    {
        Check(value, name, null);
    }

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

    // ------------------------------------------------------------------ probe methods (the test assembly is scanned too)
    internal static string ResolverProbe0() { return "p0"; }
    internal static string ResolverProbe1(int a) { return "p1"; }
    internal static string ResolverProbe2(int a, int b) { return "p2"; }

    // ------------------------------------------------------------------ reflection plumbing
    private static Type bridge;
    private static Func<string, Type> cachedFindType, cachedFindGameType;
    private static Func<string, int, MethodInfo> cachedFindStaticMethod;
    private static Func<string, Type> uncachedFindType, uncachedFindGameType;
    private static Func<string, int, MethodInfo> uncachedFindStaticMethod;
    private static Func<string, Type> realFindType, realFindGameType;
    private static Func<string, int, MethodInfo> realFindStaticMethod;
    private static Action reset;
    private static PropertyInfo pBuilds, pHits, pMisses, pStale, pUncached, pRetries, pUnstable, pCount,
        pGeneration, pHook;

    private static bool SameParams(MethodInfo m, Type[] ps)
    {
        var have = m.GetParameters();
        if (have.Length != ps.Length) { return false; }
        for (int i = 0; i < ps.Length; i++) { if (have[i].ParameterType != ps[i]) { return false; } }
        return true;
    }

    private static MethodInfo TryFind(Type t, string name, Type[] ps)
    {
        foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name == name && SameParams(m, ps)) { return m; }
        }
        return null;
    }

    private static Delegate Bind(Type t, string name, Type[] ps, Type del)
    {
        var m = TryFind(t, name, ps);
        if (m == null) { return null; }
        return Delegate.CreateDelegate(del, m);
    }

    private static PropertyInfo TryProp(Type t, string name)
    {
        return t.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static int Stat(PropertyInfo p)
    {
        // The diagnostics include a bool (hook active) as well as ints.
        return p == null ? -1 : Convert.ToInt32(p.GetValue(null, null));
    }

    private static bool Same(object a, object b)
    {
        if (a == null || b == null) { return a == null && b == null; }
        return ReferenceEquals(a, b);
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
        if (args.Length < 1) { Console.WriteLine("usage: ResolverPerformanceTests <dll>"); return 2; }
        var assembly = Assembly.LoadFrom(args[0]);
        bridge = assembly.GetType("PolytopiaAgent.BridgeMod", true);

        cachedFindType = (Func<string, Type>)Bind(bridge, "CachedFindType", new[] { typeof(string) },
            typeof(Func<string, Type>));
        cachedFindGameType = (Func<string, Type>)Bind(bridge, "CachedFindGameType",
            new[] { typeof(string) }, typeof(Func<string, Type>));
        cachedFindStaticMethod = (Func<string, int, MethodInfo>)Bind(bridge, "CachedFindStaticMethod",
            new[] { typeof(string), typeof(int) }, typeof(Func<string, int, MethodInfo>));
        uncachedFindType = (Func<string, Type>)Bind(bridge, "UncachedFindType", new[] { typeof(string) },
            typeof(Func<string, Type>));
        uncachedFindGameType = (Func<string, Type>)Bind(bridge, "UncachedFindGameType",
            new[] { typeof(string) }, typeof(Func<string, Type>));
        uncachedFindStaticMethod = (Func<string, int, MethodInfo>)Bind(bridge, "UncachedFindStaticMethod",
            new[] { typeof(string), typeof(int) }, typeof(Func<string, int, MethodInfo>));
        realFindType = (Func<string, Type>)Bind(bridge, "FindType", new[] { typeof(string) },
            typeof(Func<string, Type>));
        realFindGameType = (Func<string, Type>)Bind(bridge, "FindGameType", new[] { typeof(string) },
            typeof(Func<string, Type>));
        realFindStaticMethod = (Func<string, int, MethodInfo>)Bind(bridge, "FindStaticMethod",
            new[] { typeof(string), typeof(int) }, typeof(Func<string, int, MethodInfo>));
        reset = (Action)Bind(bridge, "ResolverResetForTests", Type.EmptyTypes, typeof(Action));
        pBuilds = TryProp(bridge, "ResolverBuilds");
        pHits = TryProp(bridge, "ResolverHits");
        pMisses = TryProp(bridge, "ResolverMisses");
        pStale = TryProp(bridge, "ResolverStaleHits");
        pUncached = TryProp(bridge, "ResolverUncachedBuilds");
        pRetries = TryProp(bridge, "ResolverGenerationRetries");
        pUnstable = TryProp(bridge, "ResolverUnstableResults");
        pCount = TryProp(bridge, "ResolverCacheCount");
        pGeneration = TryProp(bridge, "ResolverGeneration");
        pHook = TryProp(bridge, "ResolverAssemblyHookActive");

        if (cachedFindType == null || cachedFindGameType == null || cachedFindStaticMethod == null
            || uncachedFindType == null || uncachedFindGameType == null || uncachedFindStaticMethod == null)
        {
            Console.WriteLine("FAIL: cache/uncached entry points not found in the DLL");
            return 1;
        }
        Check(reset != null, "the test reset seam is present", null);
        Check(pBuilds != null && pHits != null && pGeneration != null,
            "the diagnostics properties are present", null);

        // ------------- 1. the fixture (when present) really matches the old implementations
        Console.WriteLine("=== reference check: Uncached* vs the genuine Find* bodies ===");
        string[] typeNames = new string[] { "PolytopiaAgent.BridgeMod", "System.String",
            "System.Collections.Generic.List`1", "BridgeMod", "Type", "GameLogicData",
            "Il2CppPolytopia.Data.GameLogicData", "JavaScriptSerializer", "NoSuchType073",
            "ResolverPerformanceTests", "System.Int32" };
        int[][] methodProbes = new int[][] { new int[] { 0 }, new int[] { 1 }, new int[] { 2 } };
        string[] methodNames = new string[] { "ResolverProbe0", "ResolverProbe1", "ResolverProbe2",
            "NoSuchMethod073", "ToString" };
        if (realFindType != null && realFindGameType != null && realFindStaticMethod != null)
        {
            int same = 0, probes = 0;
            for (int i = 0; i < typeNames.Length; i++)
            {
                probes++; if (Same(realFindType(typeNames[i]), uncachedFindType(typeNames[i]))) { same++; }
                probes++; if (Same(realFindGameType(typeNames[i]), uncachedFindGameType(typeNames[i]))) { same++; }
            }
            for (int i = 0; i < methodNames.Length; i++)
            {
                for (int a = 0; a < methodProbes.Length; a++)
                {
                    int arity = methodProbes[a][0];
                    probes++;
                    if (Same(realFindStaticMethod(methodNames[i], arity),
                        uncachedFindStaticMethod(methodNames[i], arity))) { same++; }
                }
            }
            Console.WriteLine("      genuine Find* entry points are present; compared " + probes + " probes");
            CheckEq(probes, same, "the Uncached* functions reproduce the genuine Find* results exactly");
        }
        else
        {
            Console.WriteLine("      NOTE: FindType/FindGameType/FindStaticMethod are already renamed away "
                + "(wired build) - comparing Cached* against Uncached* only");
            Check(true, "reference implementations present as Uncached* (wired build)", null);
        }

        // ------------- 2. differential: cached == uncached, identity for identity
        Console.WriteLine("=== differential: Cached* vs Uncached* (identity) ===");
        reset();
        int same2 = 0, total2 = 0;
        for (int i = 0; i < typeNames.Length; i++)
        {
            total2++; if (Same(cachedFindType(typeNames[i]), uncachedFindType(typeNames[i]))) { same2++; }
            total2++;
            if (Same(cachedFindGameType(typeNames[i]), uncachedFindGameType(typeNames[i]))) { same2++; }
        }
        for (int i = 0; i < methodNames.Length; i++)
        {
            for (int a = 0; a < methodProbes.Length; a++)
            {
                int arity = methodProbes[a][0];
                total2++;
                if (Same(cachedFindStaticMethod(methodNames[i], arity),
                    uncachedFindStaticMethod(methodNames[i], arity))) { same2++; }
            }
        }
        CheckEq(total2, same2, "every differential probe returned the same Type/MethodInfo (or null)");
        // repeat the whole battery: the cached path must now agree from cache
        int same3 = 0;
        for (int i = 0; i < typeNames.Length; i++)
        {
            if (Same(cachedFindType(typeNames[i]), uncachedFindType(typeNames[i]))) { same3++; }
            if (Same(cachedFindGameType(typeNames[i]), uncachedFindGameType(typeNames[i]))) { same3++; }
        }
        for (int i = 0; i < methodNames.Length; i++)
        {
            for (int a = 0; a < methodProbes.Length; a++)
            {
                if (Same(cachedFindStaticMethod(methodNames[i], methodProbes[a][0]),
                    uncachedFindStaticMethod(methodNames[i], methodProbes[a][0]))) { same3++; }
            }
        }
        CheckEq(total2, same3, "the second (cache-warm) pass agrees just as well");

        // ------------- 3. known-shape cases: null, ambiguity, first match, arity
        Console.WriteLine("=== semantics: null/empty, ambiguity, first match, arity ===");
        reset();
        CheckEq(null, cachedFindType(null), "CachedFindType(null) is null");
        CheckEq(null, cachedFindType(""), "CachedFindType(\"\") is null");
        CheckEq(null, cachedFindGameType(null), "CachedFindGameType(null) is null");
        CheckEq(null, cachedFindGameType(""), "CachedFindGameType(\"\") is null");
        CheckEq(null, cachedFindStaticMethod(null, 0), "CachedFindStaticMethod(null, 0) is null");
        CheckEq(null, cachedFindStaticMethod("ResolverProbe0", -1),
            "a negative arity is null (no method can match)");
        CheckEq(null, cachedFindStaticMethod("", 0), "an empty method name is null");
        Check(Same(cachedFindGameType("NoSuchType073"), null),
            "an unknown simple name resolves to null (unknown, not a guess)", null);
        Check(Same(cachedFindGameType("Type"), uncachedFindGameType("Type")),
            "the ambiguous leaf name \"Type\" keeps the uncached answer ("
            + (uncachedFindGameType("Type") == null ? "null" : "a unique candidate") + ")", null);
        Check(Same(cachedFindGameType("BridgeMod"), uncachedFindGameType("BridgeMod")),
            "a unique non-game candidate resolves like the uncached resolver", null);
        if (uncachedFindGameType("BridgeMod") != null)
        {
            CheckEq("PolytopiaAgent.BridgeMod", cachedFindGameType("BridgeMod").FullName,
                "the unique candidate is the expected type");
        }
        var probe0 = cachedFindStaticMethod("ResolverProbe0", 0);
        var probe1 = cachedFindStaticMethod("ResolverProbe1", 1);
        var probe2 = cachedFindStaticMethod("ResolverProbe2", 2);
        Check(probe0 != null && probe0.Name == "ResolverProbe0" && probe0.GetParameters().Length == 0,
            "arity 0 finds the 0-parameter probe", null);
        Check(probe1 != null && probe1.Name == "ResolverProbe1" && probe1.GetParameters().Length == 1,
            "arity 1 finds the 1-parameter probe", null);
        Check(probe2 != null && probe2.Name == "ResolverProbe2" && probe2.GetParameters().Length == 2,
            "arity 2 finds the 2-parameter probe", null);
        Check(probe1 != null && probe2 != null && !ReferenceEquals(probe1, probe2),
            "different arities are not mixed in the cache", null);
        Check(Same(cachedFindStaticMethod("ResolverProbe0", 1),
            uncachedFindStaticMethod("ResolverProbe0", 1)),
            "an arity with no match agrees with the uncached resolver", null);

        // ------------- 4. key separation: domain and arity are part of the key
        Console.WriteLine("=== key separation: domain + arity ===");
        reset();
        cachedFindType("System.String");
        cachedFindGameType("String");
        int afterTwoDomains = Stat(pBuilds);
        CheckEq(2, afterTwoDomains, "the exact-name domain and the game-type domain use separate keys");
        reset();
        cachedFindStaticMethod("ResolverProbe0", 0);
        cachedFindStaticMethod("ResolverProbe0", 1);
        cachedFindStaticMethod("ResolverProbe0", 2);
        CheckEq(3, Stat(pBuilds), "three arities of one name use three keys");
        reset();
        for (int i = 0; i < 50; i++) { cachedFindType("System.String"); }
        CheckEq(1, Stat(pBuilds), "50 identical lookups build exactly one entry");
        CheckEq(49, Stat(pHits), "the other 49 were fast-path hits");

        // ------------- 5. concurrency: one expensive scan per key
        Console.WriteLine("=== concurrency: one scan per key under 8 threads ===");
        reset();
        const int concurrentKeys = 3;
        int threads = 8, perThread = 2000;
        int mismatches = 0;
        var errors = new List<string>();
        var workers = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            workers[t] = new Thread(delegate ()
            {
                try
                {
                    for (int i = 0; i < perThread; i++)
                    {
                        if (!Same(cachedFindGameType("BridgeMod"), uncachedFindGameType("BridgeMod")))
                        { Interlocked.Increment(ref mismatches); }
                        if (!Same(cachedFindType("System.String"), uncachedFindType("System.String")))
                        { Interlocked.Increment(ref mismatches); }
                        if (cachedFindStaticMethod("ResolverProbe2", 2) == null)
                        { Interlocked.Increment(ref mismatches); }
                    }
                }
                catch (Exception ex) { lock (errors) { errors.Add(ex.GetType().Name + ": " + ex.Message); } }
            });
        }
        for (int t = 0; t < threads; t++) { workers[t].Start(); }
        for (int t = 0; t < threads; t++) { workers[t].Join(); }
        CheckEq(0, mismatches, "every concurrent result matched the uncached resolver");
        Check(errors.Count == 0, "no thread saw an exception", errors.Count == 0 ? null : errors[0]);
        CheckEq(concurrentKeys, Stat(pBuilds), "8 threads x 3 keys built exactly 3 entries");
        // Every call is either a hit or a miss; only the cold-start race may produce a few extra
        // misses (GetOrAdd may create losing Lazy objects), but the scans stay at one per key.
        int hits = Stat(pHits), misses = Stat(pMisses);
        CheckEq(threads * perThread * concurrentKeys, hits + misses, "every concurrent call was a hit or a miss");
        // Misses is shared across all three independent keys, not a per-key count.
        // Each key incurs at least its first miss; each thread can race before its
        // publication once. Contenders share the published Lazy, so scans stay 3.
        int maxColdMisses = threads * concurrentKeys;
        Check(misses >= concurrentKeys && misses <= maxColdMisses,
            "cold-start misses across all keys stay within [" + concurrentKeys + "," + maxColdMisses
            + "] (misses=" + misses + ")", null);

        // ------------- 6. AssemblyLoad invalidates positive AND negative entries
        Console.WriteLine("=== AssemblyLoad: generation invalidation ===");
        Check(Stat(pHook) == 1, "the AssemblyLoad hook is installed", null);
        var frameworkDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        // (dll, one of its type names) probes whose assembly is normally NOT loaded yet.
        string[][] probePairs = new string[][] {
            new string[] { "System.Web.Extensions.dll", "System.Web.Script.Serialization.JavaScriptSerializer" },
            new string[] { "System.Web.dll", "System.Web.HttpContext" },
            new string[] { "System.Data.dll", "System.Data.DataTable" },
            new string[] { "System.Xml.Linq.dll", "System.Xml.Linq.XDocument" },
            new string[] { "System.Numerics.dll", "System.Numerics.BigInteger" },
            new string[] { "System.Drawing.dll", "System.Drawing.Point" } };
        var loadedNames = new List<string>();
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { loadedNames.Add(a.GetName().Name); } catch (Exception) { }
        }

        // ---- NEGATIVE invalidation first: cache a miss, then load the assembly that fixes it
        bool negativeDone = false;
        for (int i = 0; i < probePairs.Length && !negativeDone; i++)
        {
            string dll = probePairs[i][0], typeName = probePairs[i][1];
            string simple = dll.Replace(".dll", "");
            string path = System.IO.Path.Combine(frameworkDir, dll);
            if (loadedNames.Contains(simple) || !System.IO.File.Exists(path)) { continue; }
            if (uncachedFindType(typeName) != null) { continue; }   // already resolvable: not a probe
            reset();
            CheckEq(null, cachedFindType(typeName), "the type is unknown before the load (cached miss)");
            int buildsNeg = Stat(pBuilds), genNeg = Stat(pGeneration);
            bool loaded = false;
            try { Assembly.LoadFrom(path); loaded = true; }
            catch (Exception ex) { Console.WriteLine("      (could not load " + dll + ": "
                + ex.GetType().Name + ")"); }
            if (!loaded) { continue; }
            bool resolvableNow = uncachedFindType(typeName) != null;
            if (!resolvableNow)
            {
                Console.WriteLine("      (" + typeName + " is not in " + dll + " after loading)");
                continue;
            }
            Check(Stat(pGeneration) > genNeg, "loading " + dll + " incremented the generation ("
                + genNeg + " -> " + Stat(pGeneration) + ")");
            Check(cachedFindType(typeName) != null,
                "a cached NEGATIVE result is re-resolved after an assembly load (" + typeName + " found)");
            Check(Stat(pBuilds) > buildsNeg, "the negative entry really was rebuilt (builds "
                + buildsNeg + " -> " + Stat(pBuilds) + ")");
            negativeDone = true;
        }
        if (!negativeDone)
        {
            Console.WriteLine("      NOTE: no unloaded framework assembly could serve as a negative "
                + "probe in this process; negative invalidation was not observable here");
        }

        // ---- POSITIVE invalidation: a cached hit must be re-resolved after any assembly load
        reset();
        var positive1 = cachedFindGameType("BridgeMod");
        // The scan itself can load referenced assemblies, so the first resolution may legitimately
        // take more than one attempt; let the process settle before asserting pure hits.
        int stableBuilds = Stat(pBuilds);
        for (int guard = 0; guard < 10; guard++)
        {
            cachedFindGameType("BridgeMod");
            if (Stat(pBuilds) == stableBuilds) { break; }
            stableBuilds = Stat(pBuilds);
        }
        int buildsPos = Stat(pBuilds), hitsPos = Stat(pHits);
        Check(buildsPos >= 1, "the positive result was resolved (builds=" + buildsPos + ")", null);
        cachedFindGameType("BridgeMod");
        CheckEq(buildsPos, Stat(pBuilds), "with a stable generation the entry keeps hitting");
        CheckEq(hitsPos + 1, Stat(pHits), "the settled call was a fast-path hit");
        int genAfterCache = Stat(pGeneration);
        bool loadedOne = false;
        loadedNames.Clear();
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { loadedNames.Add(a.GetName().Name); } catch (Exception) { }
        }
        for (int i = 0; i < probePairs.Length && !loadedOne; i++)
        {
            string dll = probePairs[i][0];
            string simple = dll.Replace(".dll", "");
            if (loadedNames.Contains(simple)) { continue; }
            string path = System.IO.Path.Combine(frameworkDir, dll);
            if (!System.IO.File.Exists(path)) { continue; }
            try { Assembly.LoadFrom(path); loadedOne = true; }
            catch (Exception ex) { Console.WriteLine("      (could not load " + dll + ": "
                + ex.GetType().Name + ")"); }
        }
        if (loadedOne)
        {
            int genAfterLoad = Stat(pGeneration);
            Check(genAfterLoad > genAfterCache, "loading an assembly incremented the generation ("
                + genAfterCache + " -> " + genAfterLoad + ")");
            int buildsBefore = Stat(pBuilds);
            int staleBefore = Stat(pStale);
            var positive2 = cachedFindGameType("BridgeMod");
            Check(Same(positive1, positive2), "the re-resolved positive result is the same Type");
            // The scan itself can load referenced assemblies, so one query may legitimately resolve
            // more than once; the invariant is that the stale entry caused at least one re-resolve.
            Check(Stat(pBuilds) >= buildsBefore + 1,
                "the positive entry was re-resolved after the load (builds " + buildsBefore + " -> "
                + Stat(pBuilds) + ")");
            Check(Stat(pStale) > staleBefore, "the stale hit was counted", null);
        }
        else
        {
            Console.WriteLine("      NOTE: no extra framework assembly could be loaded; the positive "
                + "invalidation check is limited to the counter assertions");
        }

        // ------------- 7. known limitation: a type created INSIDE an already loaded dynamic assembly
        Console.WriteLine("=== limitation: dynamic type creation raises no AssemblyLoad event ===");
        reset();
        var an = new AssemblyName("Resolver073DynAsm");
        var ab = AppDomain.CurrentDomain.DefineDynamicAssembly(an, AssemblyBuilderAccess.Run);
        // Defining the assembly raised AssemblyLoad, so the generation already moved once here.
        int genDynamic = Stat(pGeneration);
        CheckEq(null, cachedFindType("Resolver073DynType"),
            "the not-yet-created dynamic type is cached as a miss");
        var mb = ab.DefineDynamicModule("M");
        var tb = mb.DefineType("Resolver073DynType", TypeAttributes.Public);
        tb.CreateType();
        Check(genDynamic == Stat(pGeneration),
            "creating a type inside a loaded dynamic assembly raises NO AssemblyLoad event");
        Check(uncachedFindType("Resolver073DynType") != null,
            "the uncached resolver DOES see the created dynamic type");
        CheckEq(null, cachedFindType("Resolver073DynType"),
            "KNOWN LIMITATION: the cached miss is still served (the generation did not move)");
        if (reset != null) { reset(); }
        Check(cachedFindType("Resolver073DynType") != null,
            "after an explicit invalidation the same lookup finds the dynamic type (cache, not resolver)");

        // ------------- 8. diagnostics
        Console.WriteLine("=== diagnostics ===");
        reset();
        cachedFindGameType("BridgeMod");
        cachedFindGameType("BridgeMod");
        cachedFindType("NoSuchType073");
        string diag = (string)Bind(bridge, "ResolverDiagnosticsJson", Type.EmptyTypes,
            typeof(Func<string>)).DynamicInvoke();
        Check(diag != null && diag.StartsWith("{") && diag.Contains("\"hits\"")
            && diag.Contains("\"generation\""), "the diagnostics JSON is well formed", diag);
        Check(Stat(pBuilds) == 2 && Stat(pHits) == 1 && Stat(pMisses) == 2,
            "build/hit/miss counters match the two misses and one hit (builds=" + Stat(pBuilds)
            + " hits=" + Stat(pHits) + " misses=" + Stat(pMisses) + ")", null);

        // ------------- 9. benchmark: cold uncached scan vs warm cache
        Console.WriteLine("=== benchmark: cold scan vs warm cache (NOT a Unity FPS claim) ===");
        reset();
        int rounds = 3, warm = 200000;
        double coldTypeUs = double.MaxValue, coldGameUs = double.MaxValue, coldMethodUs = double.MaxValue;
        var sw = new Stopwatch();
        for (int r = 0; r < rounds; r++)
        {
            sw.Restart(); uncachedFindType("PolytopiaAgent.BridgeMod"); sw.Stop();
            coldTypeUs = Math.Min(coldTypeUs, sw.Elapsed.TotalMilliseconds * 1000.0);
            sw.Restart(); uncachedFindGameType("BridgeMod"); sw.Stop();
            coldGameUs = Math.Min(coldGameUs, sw.Elapsed.TotalMilliseconds * 1000.0);
            sw.Restart(); uncachedFindStaticMethod("ResolverProbe2", 2); sw.Stop();
            coldMethodUs = Math.Min(coldMethodUs, sw.Elapsed.TotalMilliseconds * 1000.0);
        }
        reset();
        // warm the three keys first, then measure
        cachedFindType("PolytopiaAgent.BridgeMod");
        cachedFindGameType("BridgeMod");
        cachedFindStaticMethod("ResolverProbe2", 2);
        int buildsAfterWarm = Stat(pBuilds);
        int warmCalls = warm * 3;
        sw.Restart();
        for (int i = 0; i < warm; i++)
        {
            cachedFindType("PolytopiaAgent.BridgeMod");
            cachedFindGameType("BridgeMod");
            cachedFindStaticMethod("ResolverProbe2", 2);
        }
        sw.Stop();
        double warmUs = sw.Elapsed.TotalMilliseconds * 1000.0 / warmCalls;
        CheckEq(3, buildsAfterWarm, "only three entries were built for the warm loop");
        Check(Stat(pHits) == warmCalls, "every warm call was a fast-path hit (" + Stat(pHits) + ")", null);
        Console.WriteLine("      cold UNCACHED scan: FindType " + coldTypeUs.ToString("F1")
            + "us, FindGameType " + coldGameUs.ToString("F1") + "us, FindStaticMethod "
            + coldMethodUs.ToString("F1") + "us (per call, best of " + rounds + ")");
        Console.WriteLine("      warm CACHED lookup: " + warmUs.ToString("F2") + "us/call over "
            + warmCalls + " calls (3 entries, " + Stat(pHits) + " hits)");
        double coldTotal = coldTypeUs + coldGameUs + coldMethodUs;
        Console.WriteLine("      one request resolving those three names: " + coldTotal.ToString("F1")
            + "us uncached vs " + (warmUs * 3).ToString("F2") + "us cached  (x"
            + (warmUs > 0 ? (coldTotal / (warmUs * 3)).ToString("F0") : "?") + ")");
        Console.WriteLine("      NOTE: low-level per-call cost on this machine, not Unity frame time.");

        Console.WriteLine("=".PadRight(70, '='));
        return failed == 0 ? 0 : 1;
    }
}
