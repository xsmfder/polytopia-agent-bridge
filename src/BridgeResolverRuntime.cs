// Cached metadata resolution (codex-dsh-073).
//
// The bridge resolves game types/methods by scanning every loaded assembly
// (AppDomain.CurrentDomain.GetAssemblies() + Assembly.GetTypes()). Those scans dominate the cost of a
// `world` request. This file adds a cache in front of the three resolvers:
//
//     CachedFindType(name)          -> UncachedFindType(name)
//     CachedFindGameType(simpleName)-> UncachedFindGameType(simpleName)
//     CachedFindStaticMethod(name, arity) -> UncachedFindStaticMethod(name, arity)
//
// WIRING CONTRACT (the supervisor applies this in BridgeMod.cs):
//   * FindType / FindGameType / FindStaticMethod keep their names and become one-line delegations to
//     the matching Cached* entry point;
//   * the original bodies are renamed UncachedFindType / UncachedFindGameType / UncachedFindStaticMethod
//     and stay exactly as they are.
// This file only ever calls the Uncached* functions - never FindType/FindGameType/FindStaticMethod,
// which after the wiring are the cached entry points themselves (calling them would recurse).
//
// Return semantics are unchanged:
//   * CachedFindType: first assembly whose GetType(name, false) yields a type, else null;
//   * CachedFindGameType: exactly ONE Il2CppPolytopia* candidate, else the single non-game candidate,
//     else null (0 candidates or ambiguous - never a guess);
//   * CachedFindStaticMethod: the FIRST static method with that name and parameter count, else null.
//
// Cached: Type objects, MethodInfo objects and "not found". NEVER cached: instances, game data,
// visibility, map values or any read result.
//
// Invalidation: every entry carries the resolver GENERATION it was resolved in. An AssemblyLoad event
// (which can fire in the middle of another thread's scan) increments the generation, so both positive
// and negative entries from an older generation are treated as misses and re-resolved. A scan whose
// generation moved while it ran is never published as current: it is retried a bounded number of
// times, and a still-unstable result is returned UNSTAMPED (generation -1) so nobody can serve it as
// up to date.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        // Resolution domains keep the three resolvers in one dictionary without any key collision.
        private const int ResolverDomainExactType = 1;      // CachedFindType
        private const int ResolverDomainGameType = 2;       // CachedFindGameType
        private const int ResolverDomainStaticMethod = 3;   // CachedFindStaticMethod

        internal const int MaxResolverEntries = 4096;       // soft cap; past it, resolve without caching
        private const int ResolverStableAttempts = 3;       // bounded retries when assemblies load
        internal const int MaxAssemblyTypeCatalogs = 512;
        private static readonly ConcurrentDictionary<Assembly, Lazy<AssemblyTypeCatalog>> AssemblyTypeCatalogs
            = new ConcurrentDictionary<Assembly, Lazy<AssemblyTypeCatalog>>();
        private static long _assemblyTypeScans, _dynamicAssemblyTypeReads, _assemblyTypeIndexes;
        private sealed class AssemblyTypeCatalog
        {
            internal readonly Type[] Types;
            internal readonly Lazy<Dictionary<string, Type[]>> Names;
            internal AssemblyTypeCatalog(Type[] types)
            {
                Types = types;
                Names = new Lazy<Dictionary<string, Type[]>>(delegate { return BuildAssemblyTypeNames(types); },
                    LazyThreadSafetyMode.ExecutionAndPublication);
            }
        }
        private static Dictionary<string, Type[]> BuildAssemblyTypeNames(Type[] types)
        {
            var grouped = new Dictionary<string, List<Type>>(StringComparer.Ordinal);
            foreach (Type type in types)
            {
                string full = type == null ? null : type.FullName;
                if (full == null) continue;
                string last = full.Substring(full.LastIndexOf('.') + 1);
                List<Type> bucket;
                if (!grouped.TryGetValue(last, out bucket)) { bucket = new List<Type>(); grouped[last] = bucket; }
                bucket.Add(type);
            }
            var result = new Dictionary<string, Type[]>(StringComparer.Ordinal);
            foreach (var pair in grouped) result[pair.Key] = pair.Value.ToArray();
            Interlocked.Increment(ref _assemblyTypeIndexes);
            return result;
        }

        // Loaded non-dynamic assemblies have an immutable type table. Reuse that table across
        // DIFFERENT requested names, but keep scanning the current assembly set per resolution.
        // Dynamic assemblies remain live, and failed reads are never retained.
        internal static Type[] ResolverTypesOf(Assembly assembly)
        { var catalog = ResolverCatalogOf(assembly); return catalog == null ? null : catalog.Types; }

        internal static Type[] ResolverGameTypeCandidates(Assembly assembly, string name)
        {
            if (assembly == null || string.IsNullOrEmpty(name)) return Type.EmptyTypes;
            AssemblyTypeCatalog catalog = ResolverCatalogOf(assembly);
            try
            {
                Type[] found;
                return catalog.Names.Value.TryGetValue(name.Substring(name.LastIndexOf('.') + 1), out found)
                    ? found : Type.EmptyTypes;
            }
            catch
            {
                Lazy<AssemblyTypeCatalog> entry;
                if (AssemblyTypeCatalogs.TryGetValue(assembly, out entry) && entry.IsValueCreated
                    && object.ReferenceEquals(entry.Value, catalog))
                    ((ICollection<KeyValuePair<Assembly, Lazy<AssemblyTypeCatalog>>>)AssemblyTypeCatalogs)
                        .Remove(new KeyValuePair<Assembly, Lazy<AssemblyTypeCatalog>>(assembly, entry));
                throw;
            }
        }

        private static AssemblyTypeCatalog ResolverCatalogOf(Assembly assembly)
        {
            if (assembly == null) return null;
            bool dynamic;
            try { dynamic = assembly.IsDynamic; }
            catch { return new AssemblyTypeCatalog(assembly.GetTypes()); }
            if (dynamic)
            { Interlocked.Increment(ref _dynamicAssemblyTypeReads); return new AssemblyTypeCatalog(assembly.GetTypes()); }
            Lazy<AssemblyTypeCatalog> catalog;
            if (!AssemblyTypeCatalogs.TryGetValue(assembly, out catalog))
            {
                lock (AssemblyTypeCatalogs)
                {
                    if (!AssemblyTypeCatalogs.TryGetValue(assembly, out catalog)
                        && AssemblyTypeCatalogs.Count < MaxAssemblyTypeCatalogs)
                    {
                        catalog = new Lazy<AssemblyTypeCatalog>(delegate
                        { Interlocked.Increment(ref _assemblyTypeScans); return new AssemblyTypeCatalog(assembly.GetTypes()); },
                            LazyThreadSafetyMode.ExecutionAndPublication);
                        AssemblyTypeCatalogs[assembly] = catalog;
                    }
                }
                if (catalog == null)
                { Interlocked.Increment(ref _assemblyTypeScans); return new AssemblyTypeCatalog(assembly.GetTypes()); }
            }
            try { return catalog.Value; }
            catch
            {
                ((ICollection<KeyValuePair<Assembly, Lazy<AssemblyTypeCatalog>>>)AssemblyTypeCatalogs)
                    .Remove(new KeyValuePair<Assembly, Lazy<AssemblyTypeCatalog>>(assembly, catalog));
                throw;
            }
        }

        internal static string AssemblyTypeCatalogStatsJson()
        {
            return "{\"entries\":" + AssemblyTypeCatalogs.Count + ",\"cap\":" + MaxAssemblyTypeCatalogs
                + ",\"scans\":" + Interlocked.Read(ref _assemblyTypeScans)
                + ",\"dynamicReads\":" + Interlocked.Read(ref _dynamicAssemblyTypeReads)
                + ",\"nameIndexesBuilt\":" + Interlocked.Read(ref _assemblyTypeIndexes) + "}";
        }

        internal static void ResetAssemblyTypeCatalogsForTests()
        { AssemblyTypeCatalogs.Clear(); Interlocked.Exchange(ref _assemblyTypeScans, 0); Interlocked.Exchange(ref _dynamicAssemblyTypeReads, 0); Interlocked.Exchange(ref _assemblyTypeIndexes, 0); }

        private struct ResolverKey : IEquatable<ResolverKey>
        {
            internal int Domain;
            internal string Name;      // exact type name / simple type name / method name
            internal int Arity;        // static methods only (0 for the type domains)

            public bool Equals(ResolverKey other)
            {
                return Domain == other.Domain && Arity == other.Arity
                    && string.Equals(Name, other.Name, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is ResolverKey && Equals((ResolverKey)obj);
            }

            public override int GetHashCode()
            {
                int h = (Domain * 397) ^ Arity;
                return (h * 397) ^ (Name == null ? 0 : Name.GetHashCode());
            }
        }

        private sealed class ResolverEntry
        {
            internal int Generation;      // -1 = never stamped (an assembly loaded during the scan)
            internal Type Type;           // Type domains
            internal MethodInfo Method;   // static-method domain
        }

        private static readonly ConcurrentDictionary<ResolverKey, Lazy<ResolverEntry>> ResolverCache =
            new ConcurrentDictionary<ResolverKey, Lazy<ResolverEntry>>();

        private static int _resolverGeneration;
        private static int _resolverBuilds;
        private static int _resolverHits;
        private static int _resolverMisses;
        private static int _resolverStaleHits;
        private static int _resolverUncachedBuilds;
        private static int _resolverGenerationRetries;
        private static int _resolverUnstableResults;

        // Installing the hook is idempotent (it happens in the class initializer) and never throws.
        private static readonly bool ResolverHookInstalled = ResolverInstallAssemblyHook();

        private static bool ResolverInstallAssemblyHook()
        {
            try
            {
                AppDomain.CurrentDomain.AssemblyLoad += ResolverOnAssemblyLoad;
                return true;
            }
            catch (Exception) { return false; }
        }

        private static void ResolverOnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            // Published entries of every older generation become misses. No table walk happens here:
            // the event can fire on any thread, so it only bumps a counter.
            Interlocked.Increment(ref _resolverGeneration);
        }

        internal static int ResolverGeneration
        {
            get { return Thread.VolatileRead(ref _resolverGeneration); }
        }

        internal static bool ResolverAssemblyHookActive { get { return ResolverHookInstalled; } }
        internal static int ResolverBuilds { get { return _resolverBuilds; } }
        internal static int ResolverHits { get { return _resolverHits; } }
        internal static int ResolverMisses { get { return _resolverMisses; } }
        internal static int ResolverStaleHits { get { return _resolverStaleHits; } }
        internal static int ResolverUncachedBuilds { get { return _resolverUncachedBuilds; } }
        internal static int ResolverGenerationRetries { get { return _resolverGenerationRetries; } }
        internal static int ResolverUnstableResults { get { return _resolverUnstableResults; } }
        internal static int ResolverCacheCount { get { return ResolverCache.Count; } }

        internal static string ResolverDiagnosticsJson()
        {
            return "{\"entries\":" + ResolverCache.Count
                + ",\"cap\":" + MaxResolverEntries
                + ",\"cacheEnabled\":" + (ResolverHookInstalled ? "true" : "false")
                + ",\"generation\":" + ResolverGeneration
                + ",\"builds\":" + _resolverBuilds
                + ",\"hits\":" + _resolverHits
                + ",\"misses\":" + _resolverMisses
                + ",\"staleHits\":" + _resolverStaleHits
                + ",\"uncachedBuilds\":" + _resolverUncachedBuilds
                + ",\"generationRetries\":" + _resolverGenerationRetries
                + ",\"unstableResults\":" + _resolverUnstableResults
                + ",\"hookActive\":" + (ResolverHookInstalled ? "true" : "false") + "}";
        }

        /// <summary>Test seam: drop the cache and reset the counters. Not called in production.</summary>
        internal static void ResolverResetForTests()
        {
            ResolverCache.Clear();
            Interlocked.Exchange(ref _resolverBuilds, 0);
            Interlocked.Exchange(ref _resolverHits, 0);
            Interlocked.Exchange(ref _resolverMisses, 0);
            Interlocked.Exchange(ref _resolverStaleHits, 0);
            Interlocked.Exchange(ref _resolverUncachedBuilds, 0);
            Interlocked.Exchange(ref _resolverGenerationRetries, 0);
            Interlocked.Exchange(ref _resolverUnstableResults, 0);
        }

        // ------------------------------------------------------------------ cached entry points
        internal static Type CachedFindType(string name)
        {
            // A null/empty name cannot match anything in the uncached resolver either (GetType(null)
            // and GetType("") both throw per assembly and are swallowed), so this is a pure shortcut.
            if (string.IsNullOrEmpty(name)) { return null; }
            return ResolverGet(ResolverDomainExactType, name, 0).Type;
        }

        internal static Type CachedFindGameType(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName)) { return null; }
            return ResolverGet(ResolverDomainGameType, simpleName, 0).Type;
        }

        internal static MethodInfo CachedFindStaticMethod(string name, int arity)
        {
            // No static method has a null/empty name or a negative parameter count.
            if (string.IsNullOrEmpty(name) || arity < 0) { return null; }
            return ResolverGet(ResolverDomainStaticMethod, name, arity).Method;
        }

        // ------------------------------------------------------------------ cache core
        private static ResolverEntry ResolverGet(int domain, string name, int arity)
        {
            var key = new ResolverKey();
            key.Domain = domain;
            key.Name = name;
            key.Arity = arity;

            // Without the AssemblyLoad hook no entry could ever be invalidated, so the cache stays
            // OFF in that case: correct results, just not faster.
            if (!ResolverHookInstalled)
            {
                Interlocked.Increment(ref _resolverUncachedBuilds);
                return ResolverResolveStable(key);
            }

            Lazy<ResolverEntry> cached;
            // Fast path: one TryGetValue. No Count, no lock over the whole table.
            if (ResolverCache.TryGetValue(key, out cached))
            {
                ResolverEntry hit = ResolverValue(key, cached);
                // Read the generation AFTER the Lazy value: building that value can itself load an
                // assembly (which bumps the generation), so a value captured before the build would
                // wrongly accept a stale entry.
                if (hit != null && hit.Generation == ResolverGeneration)
                {
                    Interlocked.Increment(ref _resolverHits);
                    return hit;
                }
                Interlocked.Increment(ref _resolverStaleHits);
                ResolverRemoveIfSame(key, cached);
            }

            Interlocked.Increment(ref _resolverMisses);
            if (ResolverCache.Count >= MaxResolverEntries)
            {
                // Soft cap: stay correct, just do not grow the table any further.
                Interlocked.Increment(ref _resolverUncachedBuilds);
                return ResolverResolveStable(key);
            }
            var created = new Lazy<ResolverEntry>(delegate { return ResolverResolveStable(key); },
                LazyThreadSafetyMode.ExecutionAndPublication);
            Lazy<ResolverEntry> published = ResolverCache.GetOrAdd(key, created);
            ResolverEntry built = ResolverValue(key, published);
            if (built.Generation == ResolverGeneration) { return built; }
            // An assembly loaded while this entry was being built or published: it must not be served
            // as current. Drop exactly the value we inspected and resolve once more without caching.
            Interlocked.Increment(ref _resolverStaleHits);
            ResolverRemoveIfSame(key, published);
            Interlocked.Increment(ref _resolverUncachedBuilds);
            return ResolverResolveStable(key);
        }

        /// <summary>
        /// Read a plan's value, but never let a THROWN build stay in the cache: Lazy caches an
        /// exception, so a failing entry would rethrow forever (even after an AssemblyLoad moved the
        /// generation). The same plan is conditionally removed first, so the next request retries;
        /// the original exception is rethrown unchanged.
        /// </summary>
        private static ResolverEntry ResolverValue(ResolverKey key, Lazy<ResolverEntry> lazy)
        {
            try { return lazy.Value; }
            catch
            {
                ResolverRemoveIfSame(key, lazy);
                throw;
            }
        }

        /// <summary>
        /// Remove a cache entry ONLY when the published value is still the one the caller inspected.
        /// ConcurrentDictionary's conditional ICollection.Remove does exactly that; TryRemove(key)
        /// would also delete a newer plan another thread has just published.
        /// </summary>
        private static bool ResolverRemoveIfSame(ResolverKey key, Lazy<ResolverEntry> expected)
        {
            var pairs = (ICollection<KeyValuePair<ResolverKey, Lazy<ResolverEntry>>>)ResolverCache;
            return pairs.Remove(new KeyValuePair<ResolverKey, Lazy<ResolverEntry>>(key, expected));
        }

        /// <summary>
        /// Resolve through the UNCACHED resolver with a stable generation. If an AssemblyLoad happens
        /// while the scan runs, the result may have missed the new assembly, so the scan is retried
        /// (bounded). A result that never stabilises is returned with generation -1: correct for the
        /// caller, but nobody may serve it as up to date.
        /// </summary>
        private static ResolverEntry ResolverResolveStable(ResolverKey key)
        {
            for (int attempt = 0; attempt < ResolverStableAttempts; attempt++)
            {
                int before = ResolverGeneration;
                object result = ResolverInvokeUncached(key);
                if (before == ResolverGeneration)
                {
                    Interlocked.Increment(ref _resolverBuilds);
                    return ResolverEntryOf(before, result);
                }
                Interlocked.Increment(ref _resolverGenerationRetries);
            }
            Interlocked.Increment(ref _resolverUnstableResults);
            Interlocked.Increment(ref _resolverBuilds);
            return ResolverEntryOf(-1, ResolverInvokeUncached(key));
        }

        /// <summary>The ONLY place the uncached resolvers are called. Never the Find* entry points.</summary>
        private static object ResolverInvokeUncached(ResolverKey key)
        {
            if (key.Domain == ResolverDomainExactType) { return UncachedFindType(key.Name); }
            if (key.Domain == ResolverDomainGameType) { return UncachedFindGameType(key.Name); }
            return UncachedFindStaticMethod(key.Name, key.Arity);
        }

        private static ResolverEntry ResolverEntryOf(int generation, object result)
        {
            var entry = new ResolverEntry();
            entry.Generation = generation;
            entry.Type = result as Type;
            entry.Method = result as MethodInfo;
            return entry;
        }
    }
}
