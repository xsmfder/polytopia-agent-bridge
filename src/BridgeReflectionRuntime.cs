// Cached member-access plans (codex-dsh-072).
//
// MemberWithAccessor is the formal single-read entry point using TryReadNativeMember.
// CachedMemberWithAccessor/UncachedMemberWithAccessor retain the legacy fallback behaviour
// for diagnostic differential tests. Their fallback never calls the formal entry point.
//
// Both readers reuse metadata only. The legacy accessor replays the original order below;
// the formal reader selects the first step, and invocation failure stays unknown:
//
//   get_<Pascal> -> get_<name> -> field <name> -> property <name> -> field <Pascal> -> property <Pascal>
//
// Legacy differential invariants kept identical to the uncached accessor:
//   * only MethodInfo / FieldInfo / PropertyInfo are cached - never an instance, never a value, so a
//     changed HP/owner/tech read is always the current one and two instances of one Type stay apart;
//   * a parameterised getter and an INDEXED property are skipped, never invoked (no side effects);
//   * an accessor that throws is skipped and the next candidate is tried;
//   * a miss (no candidate at all) is cached as an empty plan and still returns null;
//   * the cache is keyed by the runtime Type, so no Type can see another Type's plan;
//   * a metadata lookup that throws AmbiguousMatchException becomes an "ambiguous" step that throws
//     the SAME exception type when the search actually reaches it. Note the deliberate difference:
//     the message is prefixed with "cached metadata lookup: ". Any OTHER lookup exception makes the
//     whole key a fallback that delegates to UncachedMemberWithAccessor, so the original throw is
//     preserved exactly.
//
// Deliberate non-goals: no eviction beyond a soft cap (past the cap plans are built per call and not
// cached), no ready/transport cache, no new protocol, and nothing here constructs a native object or
// touches the target instance while building a plan.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal const int MaxReflectionPlans = 8192;      // soft cap; past it, resolve without caching

        private sealed class NativeBooleanPlan
        {
            internal PropertyInfo Property;
            internal FieldInfo Field;
            internal MethodInfo Method;
        }
        private static readonly ConcurrentDictionary<Tuple<Type, string>, NativeBooleanPlan> _nativeBooleanPlans =
            new ConcurrentDictionary<Tuple<Type, string>, NativeBooleanPlan>();

        // Only metadata is cached. Choose one accessor before reading; a failure must not
        // retry a getter or fall through to another spelling and fabricate a known flag.
        private static NativeBooleanPlan BuildNativeBooleanPlan(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            string alternate = char.IsUpper(name[0]) ? char.ToLowerInvariant(name[0]) + name.Substring(1)
                : char.ToUpperInvariant(name[0]) + name.Substring(1);
            foreach (string candidate in name == alternate ? new[] { name } : new[] { name, alternate })
                for (Type current = type; current != null; current = current.BaseType)
                {
                    var property = current.GetProperty(candidate, flags);
                    if (property != null && property.GetIndexParameters().Length == 0)
                        return new NativeBooleanPlan { Property = property };
                    var field = current.GetField(candidate, flags);
                    if (field != null) return new NativeBooleanPlan { Field = field };
                    foreach (string methodName in new[] { "get_" + candidate, candidate })
                    {
                        MethodInfo selected = null;
                        foreach (var method in current.GetMethods(flags))
                        {
                            if (method.Name != methodName || method.ContainsGenericParameters
                                || method.GetParameters().Length != 0 || method.ReturnType != typeof(bool)) continue;
                            if (selected != null) return new NativeBooleanPlan();
                            selected = method;
                        }
                        if (selected != null) return new NativeBooleanPlan { Method = selected };
                    }
                }
            return new NativeBooleanPlan();
        }

        internal static bool? ReadNativeBooleanMember(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                var key = Tuple.Create(target.GetType(), name);
                NativeBooleanPlan plan;
                if (!_nativeBooleanPlans.TryGetValue(key, out plan))
                {
                    plan = BuildNativeBooleanPlan(key.Item1, key.Item2);
                    if (_nativeBooleanPlans.Count < MaxReflectionPlans) plan = _nativeBooleanPlans.GetOrAdd(key, plan);
                }
                if (plan.Property != null) return NativeBool(plan.Property.GetValue(target, null));
                if (plan.Field != null) return NativeBool(plan.Field.GetValue(target));
                return plan.Method == null ? null : NativeBool(plan.Method.Invoke(target, null));
            }
            catch (Exception error) { RecordNativeReadFailure(target.GetType(), name, error); return null; }
        }

        private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance;

        // Native metadata uses exact simple names, including nested enums named Type.
        // A substring (Byte/SByte or Int32/UInt32) is not a compatible signature.
        // These are metadata plans only; callers still validate their actual argument objects.
        internal static bool NativeMethodSignatureMatches(MethodInfo method, string name,
            string[] parameters, string returns)
        {
            if (method == null || parameters == null || method.Name != name || method.ContainsGenericParameters
                || method.ReturnType.IsByRef || method.ReturnType.IsPointer
                || (returns != null && method.ReturnType.Name != returns)) return false;
            var args = method.GetParameters();
            if (args.Length != parameters.Length) return false;
            for (int i = 0; i < args.Length; i++)
            {
                Type actual = args[i].ParameterType;
                if (actual.IsByRef || actual.IsPointer || args[i].IsOut
                    || string.IsNullOrEmpty(parameters[i]) || actual.Name != parameters[i]) return false;
            }
            return true;
        }

        internal static MethodInfo FindUniqueNativeMethod(Type type, string name,
            string[] parameters, string returns, bool isStatic)
        {
            if (type == null || string.IsNullOrEmpty(name) || parameters == null) return null;
            // Runtime Type metadata is immutable after creation. Retain only the exact signature's
            // resolution, including misses/ambiguity; never retain a receiver or invocation result.
            var signature = new System.Text.StringBuilder();
            foreach (string parameter in parameters)
            {
                if (string.IsNullOrEmpty(parameter)) return null;
                signature.Append(parameter.Length).Append(':').Append(parameter);
            }
            var key = Tuple.Create(type, name, signature.ToString(), returns, isStatic);
            Lazy<NativeMethodPlan> plan;
            if (!_nativeMethodPlans.TryGetValue(key, out plan))
            {
                if (_nativeMethodPlans.Count >= MaxReflectionPlans)
                    return BuildNativeMethodPlan(type, name, parameters, returns, isStatic).Method;
                // The caller may reuse or mutate its array after this lookup.
                var ownedParameters = (string[])parameters.Clone();
                plan = _nativeMethodPlans.GetOrAdd(key, new Lazy<NativeMethodPlan>(delegate
                    { return BuildNativeMethodPlan(type, name, ownedParameters, returns, isStatic); },
                    LazyThreadSafetyMode.ExecutionAndPublication));
            }
            try { return plan.Value.Method; }
            catch
            {
                // A transient reflection failure must not become a permanently faulted entry.
                ((ICollection<KeyValuePair<Tuple<Type, string, string, string, bool>, Lazy<NativeMethodPlan>>>)_nativeMethodPlans)
                    .Remove(new KeyValuePair<Tuple<Type, string, string, string, bool>, Lazy<NativeMethodPlan>>(key, plan));
                throw;
            }
        }

        private sealed class NativeMethodPlan { internal MethodInfo Method; }
        private static readonly ConcurrentDictionary<Tuple<Type, string, string, string, bool>, Lazy<NativeMethodPlan>> _nativeMethodPlans
            = new ConcurrentDictionary<Tuple<Type, string, string, string, bool>, Lazy<NativeMethodPlan>>();
        private static long _nativeMethodPlanBuilds;

        internal static string NativeMethodPlanStatsJson()
        {
            return new JsonObj().Raw("entries", _nativeMethodPlans.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Raw("cap", MaxReflectionPlans.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Raw("builds", Interlocked.Read(ref _nativeMethodPlanBuilds).ToString(System.Globalization.CultureInfo.InvariantCulture)).Close();
        }

        private static NativeMethodPlan BuildNativeMethodPlan(Type type, string name,
            string[] parameters, string returns, bool isStatic)
        {
            Interlocked.Increment(ref _nativeMethodPlanBuilds);
            MethodInfo found = null;
            var flags = BindingFlags.Public | BindingFlags.NonPublic
                | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            foreach (var method in type.GetMethods(flags))
            {
                if (!NativeMethodSignatureMatches(method, name, parameters, returns)) continue;
                if (found != null) return new NativeMethodPlan();
                found = method;
            }
            return new NativeMethodPlan { Method = found };
        }

        // Verified native authority: MapDataExtensions.GetPlayerVisibleTiles
        // (MapData, byte, List<TileData>) -> void. Do not search unrelated owners,
        // coerce player types or accept another List<T> with the same simple name.
        private static MethodInfo FindVisibleTilesMethod(Type provider, object map, Type tileType)
        {
            if (provider == null || map == null || tileType == null) return null;
            MethodInfo found = null;
            foreach (var method in provider.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (!NativeMethodSignatureMatches(method, "GetPlayerVisibleTiles",
                    new[] { "MapData", "Byte", "List`1" }, "Void") || method.ReturnType != typeof(void)) continue;
                var args = method.GetParameters();
                Type list = args[2].ParameterType;
                if (!args[0].ParameterType.IsInstanceOfType(map) || args[1].ParameterType != typeof(byte)
                    || !list.IsGenericType || list.ContainsGenericParameters) continue;
                string definition = list.GetGenericTypeDefinition().FullName;
                if (definition != "System.Collections.Generic.List`1"
                    && definition != "Il2CppSystem.Collections.Generic.List`1") continue;
                var items = list.GetGenericArguments();
                if (items.Length != 1 || items[0] != tileType) continue;
                if (found != null) return null;
                found = method;
            }
            return found;
        }

        // Resolve before invoking. These shared UI/data callers must never probe overloads by
        // execution: a method can have taken effect before throwing. No target or result is cached.
        private static bool InvocableArgumentsMatch(ParameterInfo[] parameters, object[] args)
        {
            int count = args == null ? 0 : args.Length;
            if (parameters.Length != count) return false;
            for (int i = 0; i < count; i++)
            {
                Type wanted = parameters[i].ParameterType;
                if (wanted.IsByRef || wanted.IsPointer || parameters[i].IsOut) return false;
                Type nullable = Nullable.GetUnderlyingType(wanted);
                if (args[i] == null)
                {
                    if (wanted.IsValueType && nullable == null) return false;
                }
                else if (!wanted.IsInstanceOfType(args[i])
                    && (nullable == null || !nullable.IsInstanceOfType(args[i]))) return false;
            }
            return true;
        }

        private static ConstructorInfo UniqueInvocableConstructor(Type type, object[] args)
        {
            // A missing argument plan must not silently request a zero/default command.
            if (type == null || args == null || type.IsAbstract || type.ContainsGenericParameters) return null;
            ConstructorInfo found = null;
            foreach (var constructor in type.GetConstructors())
            {
                if (!InvocableArgumentsMatch(constructor.GetParameters(), args)) continue;
                if (found != null) return null;
                found = constructor;
            }
            return found;
        }

        private static MethodInfo UniqueInvocableMethod(Type type, string name, object[] args, bool isStatic)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            var flags = BindingFlags.Public | BindingFlags.NonPublic
                | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            MethodInfo found = null;
            foreach (var method in type.GetMethods(flags))
            {
                if (method.Name != name || method.ContainsGenericParameters) continue;
                if (!InvocableArgumentsMatch(method.GetParameters(), args)) continue;
                if (found != null) return null;
                found = method;
            }
            return found;
        }

        // Known UI lifecycle callbacks are void instance methods with no arguments.
        // Resolve completely before entering a callback; never probe another overload.
        private static MethodInfo FindVoidUiCallback(Type type, string name)
        {
            var method = FindUniqueNativeMethod(type, name, new string[0], "Void", false);
            return method != null && method.ReturnType == typeof(void) ? method : null;
        }

        private enum MemberStepKind { Getter, Field, Property, Ambiguous }

        private sealed class MemberStep
        {
            internal MemberStepKind Kind;
            internal MethodInfo Getter;
            internal FieldInfo Field;
            internal PropertyInfo Property;
            internal string Label;                          // diagnostics, e.g. "getter:get_Hp"
            internal string AmbiguityMessage;
        }

        private sealed class MemberPlan
        {
            internal Type Type;
            internal string Name;
            internal MemberStep[] Steps = new MemberStep[0];
            internal bool Fallback;                         // metadata lookup threw: delegate per call
            internal string FallbackReason;
        }

        private struct MemberKey : IEquatable<MemberKey>
        {
            internal Type Type;
            internal string Name;
            internal bool Static;

            public bool Equals(MemberKey other)
            {
                return ReferenceEquals(Type, other.Type)
                    && Static == other.Static
                    && string.Equals(Name, other.Name, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is MemberKey && Equals((MemberKey)obj);
            }

            public override int GetHashCode()
            {
                int t = Type == null ? 0
                    : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Type);
                int n = Name == null ? 0 : Name.GetHashCode();
                return ((t * 397) ^ n) * 397 ^ (Static ? 1 : 0);
            }
        }

        private static readonly ConcurrentDictionary<MemberKey, Lazy<MemberPlan>> MemberPlans =
            new ConcurrentDictionary<MemberKey, Lazy<MemberPlan>>();

        private static int _reflectionPlanBuilds;
        private static int _reflectionPlanHits;
        private static int _reflectionFallbacks;
        private static int _reflectionUncachedBuilds;
        private static int _reflectionAmbiguityThrows;

        internal static int ReflectionPlanBuilds { get { return _reflectionPlanBuilds; } }
        internal static int ReflectionPlanHits { get { return _reflectionPlanHits; } }
        internal static int ReflectionFallbacks { get { return _reflectionFallbacks; } }
        internal static int ReflectionUncachedBuilds { get { return _reflectionUncachedBuilds; } }
        internal static int ReflectionAmbiguityThrows { get { return _reflectionAmbiguityThrows; } }
        internal static int ReflectionPlanCount { get { return MemberPlans.Count; } }

        internal static string ReflectionPlanDiagnostics()
        {
            return "{\"plans\":" + MemberPlans.Count
                + ",\"builds\":" + _reflectionPlanBuilds
                + ",\"hits\":" + _reflectionPlanHits
                + ",\"fallbacks\":" + _reflectionFallbacks
                + ",\"uncachedBuilds\":" + _reflectionUncachedBuilds
                + ",\"ambiguityThrows\":" + _reflectionAmbiguityThrows
                + ",\"cap\":" + MaxReflectionPlans + "}";
        }

        /// <summary>Test seam: drop every plan and reset the counters. Never called in production.</summary>
        internal static void ResetReflectionCacheForTests()
        {
            MemberPlans.Clear();
            Interlocked.Exchange(ref _reflectionPlanBuilds, 0);
            Interlocked.Exchange(ref _reflectionPlanHits, 0);
            Interlocked.Exchange(ref _reflectionFallbacks, 0);
            Interlocked.Exchange(ref _reflectionUncachedBuilds, 0);
            Interlocked.Exchange(ref _reflectionAmbiguityThrows, 0);
        }

        /// <summary>
        /// The cached plan for one (Type, name) as a readable step list - diagnostics and tests only.
        /// </summary>
        internal static string ReflectionPlanShape(Type type, string clrName)
        {
            if (type == null || string.IsNullOrEmpty(clrName)) { return "none"; }
            MemberPlan plan = PlanFor(type, clrName);
            if (plan.Fallback) { return "fallback(" + plan.FallbackReason + ")"; }
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < plan.Steps.Length; i++)
            {
                if (i > 0) { sb.Append(','); }
                sb.Append(plan.Steps[i].Label);
            }
            return plan.Steps.Length == 0 ? "miss" : sb.ToString();
        }

        /// <summary>
        /// MemberWithAccessor with per-(Type, name) metadata caching: same semantics, same accessor
        /// order, same swallow-and-continue on accessor exceptions, same null on a miss - only the
        /// reflection lookup is not repeated.
        /// </summary>
        internal static object CachedMemberWithAccessor(object o, string clrName)
        {
            if (o == null || string.IsNullOrEmpty(clrName)) { return null; }
            Type type = o.GetType();
            var key = new MemberKey();
            key.Type = type;
            key.Name = clrName;

            // Fast path: one TryGetValue (no Count, no lock on the other dictionary stripes). Only a
            // miss pays for the soft-cap check and for publishing a new plan.
            Lazy<MemberPlan> existing;
            MemberPlan plan;
            if (MemberPlans.TryGetValue(key, out existing))
            {
                plan = existing.Value;
            }
            else
            {
                if (MemberPlans.Count >= MaxReflectionPlans)
                {
                    // Soft cap reached: stay correct, just do not grow the cache any further. A
                    // fallback plan must still delegate to the UNCACHED accessor (never to
                    // MemberWithAccessor, which is the cached entry point after the wiring).
                    Interlocked.Increment(ref _reflectionUncachedBuilds);
                    MemberPlan fresh = BuildPlan(type, clrName);
                    if (fresh.Fallback)
                    {
                        Interlocked.Increment(ref _reflectionFallbacks);
                        return UncachedMemberWithAccessor(o, clrName);
                    }
                    return ExecutePlan(fresh, o);
                }
                var created = new Lazy<MemberPlan>(delegate { return BuildPlan(type, clrName); },
                    LazyThreadSafetyMode.ExecutionAndPublication);
                plan = MemberPlans.GetOrAdd(key, created).Value;
            }
            if (plan.Fallback)
            {
                // A metadata lookup threw in a way this cache cannot replay (not an ambiguity), so
                // reproduce the ORIGINAL uncached call - including its exception - instead of caching
                // a fabricated value. MemberWithAccessor is deliberately NOT used here: after the
                // wiring it is CachedMemberWithAccessor itself.
                Interlocked.Increment(ref _reflectionFallbacks);
                return UncachedMemberWithAccessor(o, clrName);
            }
            Interlocked.Increment(ref _reflectionPlanHits);
            return ExecutePlan(plan, o);
        }

        // Formal data readers select one metadata step. Unlike the legacy diagnostic
        // accessor, an invocation failure must not retry the same getter through its
        // property or reinterpret another spelling as a successful zero/null value.
        internal static bool TryReadNativeMember(object target, string name, out object value)
        {
            bool exists;
            return TryReadNativeMemberValue(target, target == null ? null : target.GetType(), name, false,
                out value, out exists);
        }

        internal static bool TryReadNativeStaticMember(Type type, string name, out object value)
        {
            bool exists;
            return TryReadNativeMemberValue(null, type, name, true, out value, out exists);
        }

        // Aliases are version/layout metadata alternatives, never value fallbacks.
        // Stop on a real null, an unreadable existing member or ambiguous metadata.
        private static object NativeAliasedMember(object target, Type type, bool isStatic, string[] names)
        {
            if (type == null || names == null) return null;
            foreach (string name in names)
            {
                object value; bool exists;
                if (TryReadNativeMemberValue(target, type, name, isStatic, out value, out exists)) return value;
                if (exists) return null;
                if (!isStatic && !string.IsNullOrEmpty(name))
                {
                    // The old InstanceField also supported all-lowercase field metadata.
                    string lower = name.ToLowerInvariant();
                    if (lower != name)
                    {
                        if (TryReadNativeMemberValue(target, type, lower, false, out value, out exists)) return value;
                        if (exists) return null;
                    }
                }
            }
            return null;
        }

        private static bool TryReadNativeMemberValue(object target, Type type, string name, bool isStatic,
            out object value, out bool exists)
        {
            value = null;
            exists = false;
            if (type == null || (!isStatic && target == null) || string.IsNullOrEmpty(name)) return false;
            try
            {
                var plan = PlanFor(type, name, isStatic);
                // Upper/lower initial spellings are metadata alternatives only. A
                // real null or failed invocation must never trigger an alias read.
                if (!plan.Fallback && plan.Steps.Length == 0 && (isStatic || char.IsUpper(name[0])))
                {
                    char initial = char.IsUpper(name[0]) ? char.ToLowerInvariant(name[0]) : char.ToUpperInvariant(name[0]);
                    plan = PlanFor(type, initial + name.Substring(1), isStatic);
                }
                exists = plan.Fallback || plan.Steps.Length > 0;
                if (plan.Fallback) { RecordNativeReadFailure(type, name, null); return false; }
                if (plan.Steps.Length == 0) return false;
                var step = plan.Steps[0];
                if (step.Kind == MemberStepKind.Getter)
                {
                    if (step.Getter.ContainsGenericParameters || step.Getter.ReturnType == typeof(void)) return false;
                    value = step.Getter.Invoke(target, null);
                }
                else if (step.Kind == MemberStepKind.Field) value = step.Field.GetValue(target);
                else if (step.Kind == MemberStepKind.Property) value = step.Property.GetValue(target, null);
                else
                {
                    if (step.Kind == MemberStepKind.Ambiguous) RecordNativeReadFailureKind(type, name, "AmbiguousMatchException");
                    return false;
                }
                return true; // A readable native null is distinct from an unreadable member.
            }
            catch (Exception error)
            { RecordNativeReadFailure(type, name, error); value = null; exists = true; return false; }
        }

        /// <summary>The cached (or newly built) plan; built once per key even under concurrency.</summary>
        private static MemberPlan PlanFor(Type type, string clrName, bool isStatic = false)
        {
            var key = new MemberKey();
            key.Type = type;
            key.Name = clrName;
            key.Static = isStatic;
            Lazy<MemberPlan> existing;
            if (MemberPlans.TryGetValue(key, out existing)) { return existing.Value; }
            if (MemberPlans.Count >= MaxReflectionPlans)
            {
                Interlocked.Increment(ref _reflectionUncachedBuilds);
                return BuildPlan(type, clrName, isStatic);
            }
            var created = new Lazy<MemberPlan>(delegate { return BuildPlan(type, clrName, isStatic); },
                LazyThreadSafetyMode.ExecutionAndPublication);
            return MemberPlans.GetOrAdd(key, created).Value;
        }

        private static MemberStep AmbiguousStep(string label, string message)
        {
            var step = new MemberStep();
            step.Kind = MemberStepKind.Ambiguous;
            step.Label = "ambiguous:" + label;
            step.AmbiguityMessage = message;
            return step;
        }

        /// <summary>
        /// Resolve the metadata for one (Type, name) in exactly MemberWithAccessor's order. Runs once
        /// per key; it reads metadata only - the target instance is never touched and no value is read.
        /// </summary>
        private static MemberPlan BuildPlan(Type type, string clrName, bool isStatic = false)
        {
            Interlocked.Increment(ref _reflectionPlanBuilds);
            var plan = new MemberPlan();
            plan.Type = type;
            plan.Name = clrName;
            BindingFlags flags = isStatic ? BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.FlattenHierarchy : MemberFlags;
            var steps = new List<MemberStep>();
            string pascal = char.ToUpperInvariant(clrName[0]) + clrName.Substring(1);
            // Static singleton fields and public properties can be distinct native
            // authorities (GameManager.instance versus GameManager.Instance).
            // Prefer exact metadata; only the caller may consider a missing alias.
            string[] getters = isStatic ? new[] { "get_" + clrName } : new[] { "get_" + pascal, "get_" + clrName };
            for (int i = 0; i < getters.Length; i++)
            {
                MethodInfo m;
                try { m = type.GetMethod(getters[i], flags); }
                catch (AmbiguousMatchException ex)
                {
                    steps.Add(AmbiguousStep("getter:" + getters[i], ex.Message));
                    continue;
                }
                catch (Exception ex) { return Fallback(plan, ex); }
                if (m == null) { continue; }
                int parameters;
                try { parameters = m.GetParameters().Length; }
                catch (Exception ex) { return Fallback(plan, ex); }
                // A getter with parameters is never invoked (same rule as MemberWithAccessor).
                if (parameters != 0) { continue; }
                var step = new MemberStep();
                step.Kind = MemberStepKind.Getter;
                step.Getter = m;
                step.Label = "getter:" + getters[i];
                steps.Add(step);
            }
            string[] names = isStatic ? new[] { clrName } : new[] { clrName, pascal };
            for (int i = 0; i < names.Length; i++)
            {
                FieldInfo f;
                try { f = type.GetField(names[i], flags); }
                catch (AmbiguousMatchException ex)
                {
                    steps.Add(AmbiguousStep("field:" + names[i], ex.Message));
                    f = null;
                }
                catch (Exception ex) { return Fallback(plan, ex); }
                if (f != null)
                {
                    var fs = new MemberStep();
                    fs.Kind = MemberStepKind.Field;
                    fs.Field = f;
                    fs.Label = "field:" + names[i];
                    steps.Add(fs);
                }
                PropertyInfo p;
                try { p = type.GetProperty(names[i], flags); }
                catch (AmbiguousMatchException ex)
                {
                    steps.Add(AmbiguousStep("property:" + names[i], ex.Message));
                    continue;
                }
                catch (Exception ex) { return Fallback(plan, ex); }
                if (p == null) { continue; }
                int indexParameters;
                try { indexParameters = p.GetIndexParameters().Length; }
                catch (Exception ex) { return Fallback(plan, ex); }
                // An indexed property is never invoked (same rule as MemberWithAccessor).
                if (indexParameters != 0) { continue; }
                var ps = new MemberStep();
                ps.Kind = MemberStepKind.Property;
                ps.Property = p;
                ps.Label = "property:" + names[i];
                steps.Add(ps);
            }
            plan.Steps = steps.ToArray();
            return plan;
        }

        private static MemberPlan Fallback(MemberPlan plan, Exception ex)
        {
            plan.Fallback = true;
            plan.FallbackReason = ex.GetType().Name + ": " + ex.Message;
            plan.Steps = new MemberStep[0];
            return plan;
        }

        /// <summary>
        /// Replay a plan against ONE instance: the first accessor that returns wins, a throwing
        /// accessor is skipped and the search continues, a miss returns null. An "ambiguous" step
        /// throws AmbiguousMatchException exactly where MemberWithAccessor would have thrown it.
        /// </summary>
        private static object ExecutePlan(MemberPlan plan, object o)
        {
            if (plan == null) { return null; }
            MemberStep[] steps = plan.Steps;
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i].Kind == MemberStepKind.Ambiguous)
                {
                    Interlocked.Increment(ref _reflectionAmbiguityThrows);
                    throw new AmbiguousMatchException("cached metadata lookup: "
                        + steps[i].AmbiguityMessage);
                }
                try
                {
                    if (steps[i].Kind == MemberStepKind.Getter) { return steps[i].Getter.Invoke(o, null); }
                    if (steps[i].Kind == MemberStepKind.Field) { return steps[i].Field.GetValue(o); }
                    if (steps[i].Kind == MemberStepKind.Property)
                    { return steps[i].Property.GetValue(o, null); }
                }
                catch (Exception) { }
            }
            return null;
        }
    }
}
