// Records natural rendering only. No render invocation, context construction, game action or thread.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Reader = PolytopiaPublicMarkers.PublicMarkerReader;

namespace PolytopiaPublicMarkers
{
    internal sealed class PublicMarkerHooks
    {
        private static PublicMarkerHooks active;
        internal void Detach()
        {
            installed=false;collector.Invalidate();
            lastContext=null;lastRenderer=null;completedView=null;
            if(object.ReferenceEquals(active,this))active=null;
        }

        private readonly Reader.Collector collector;
        private readonly NativeView views;
        private readonly Func<object, string> episodeOf;
        private readonly Func<object, int?> controlledOf;
        private readonly Func<long> revision;
        private object lastContext;
        private object lastRenderer;
        private bool installed;
        private int renderDepth;
        private int renderThreadId;
        private NativeView.Scene completedView;
        private long fullCompleted, fullRejected, tileContextChanged, tileContextAdopted, tileContextRejected;
        private long defaultContextDeferred, fullPassTileChecksReused;

        // Bounded counters only: no map coordinates, object pointers or private state.
        internal string DiagnosticsJson()
        {
            return "{\"fullCompleted\":" + fullCompleted.ToString(CultureInfo.InvariantCulture)
                + ",\"fullRejected\":" + fullRejected.ToString(CultureInfo.InvariantCulture)
                + ",\"tileContextChanged\":" + tileContextChanged.ToString(CultureInfo.InvariantCulture)
                + ",\"tileContextAdopted\":" + tileContextAdopted.ToString(CultureInfo.InvariantCulture)
                + ",\"tileContextRejected\":" + tileContextRejected.ToString(CultureInfo.InvariantCulture)
                + ",\"defaultContextDeferred\":" + defaultContextDeferred.ToString(CultureInfo.InvariantCulture)
                + ",\"fullPassTileChecksReused\":" + fullPassTileChecksReused.ToString(CultureInfo.InvariantCulture)
                + ",\"rosterSnapshots\":" + (views == null ? 0L : views.RosterSnapshots).ToString(CultureInfo.InvariantCulture) + "}";
        }

        internal sealed class Domains
        {
            internal readonly string Siege, Rainbow;
            internal Domains(string siege, string rainbow) { Siege = siege; Rainbow = rainbow; }
        }

        internal sealed class Pass
        {
            internal long Token;
            internal object Renderer, Context, Map;
            internal Reader.Sample Started;
            internal bool Closed;
        }

        private PublicMarkerHooks(NativeView nativeView, Reader.Bindings bindings,
            Func<object, string> episode, Func<object, int?> controlled, Func<long> currentRevision)
        { views = nativeView; collector = new Reader.Collector(bindings); episodeOf = episode; controlledOf = controlled; revision = currentRevision; }

        internal static PublicMarkerHooks CreateNative(Func<string, Type> exactResolver,
            Func<object, string> episode, Func<object, int?> controlled, Func<long> currentRevision)
        {
            NativeView nativeView = NativeView.CreateNative(exactResolver);
            Reader.Bindings bindings = nativeView == null ? null : Reader.Bindings.CreateNative(
                delegate(string name) { return ResolveExact(exactResolver, name); }, StableNativeIdentity);
            return new PublicMarkerHooks(nativeView, bindings, episode, controlled, currentRevision);
        }

        internal static PublicMarkerHooks CreateForTests(NativeView view, Reader.Bindings bindings,
            Func<object, string> episode, Func<object, int?> controlled, Func<long> currentRevision)
        {
            PublicMarkerHooks h = new PublicMarkerHooks(view, bindings, episode, controlled, currentRevision);
            h.installed = true; active = h; return h;
        }

        internal bool Install(HarmonyLib.Harmony harmony)
        {
            installed = false; collector.Invalidate(); active = this;
            if (harmony == null || views == null) return false;
            try
            {
                MethodInfo renderMap = views.ExactMethod("MapRendererInner", "RenderMap", typeof(bool), "MapRenderContext", "MapData");
                MethodInfo rerender = views.ExactMethod("MapRendererInner", "ReRenderAllTiles", typeof(void), "MapRenderContext");
                MethodInfo renderTile = views.ExactMethod("Tile", "Render", typeof(void), "MapRenderContext");
                MethodInfo renderTileSkinned = views.ExactMethod("Tile", "Render", typeof(void), "MapRenderContext", "SkinVisualsTransientData");
                MethodInfo renderImprovement = views.ExactMethod("Tile", "RenderImprovement", typeof(void), "MapRenderContext", "SkinVisualsTransientData");
                if (renderMap == null || rerender == null || renderTile == null || renderTileSkinned == null || renderImprovement == null) return false;
                if (harmony.Patch(renderMap, Hook("MapPrefix"), Hook("MapPostfix"), null, Hook("FullFinalizer"), null) == null) return false;
                if (harmony.Patch(rerender, Hook("FullPrefix"), Hook("AllPostfix"), null, Hook("FullFinalizer"), null) == null) return false;
                // Render(null) constructs its context inside native code. The actual resolved
                // context is observed by RenderImprovement, which both Render overloads invoke.
                if (harmony.Patch(renderTile, Hook("TileDefaultPrefix"), null, null, Hook("TileFinalizer"), null) == null) return false;
                if (harmony.Patch(renderTileSkinned, Hook("TilePrefix"), null, null, Hook("TileFinalizer"), null) == null) return false;
                if (harmony.Patch(renderImprovement, Hook("TilePrefix"), null, null, Hook("TileFinalizer"), null) == null) return false;
                installed = true; return true;
            }
            catch (Exception) { collector.Invalidate(); return false; }
        }

        private static HarmonyMethod Hook(string name)
        { return new HarmonyMethod(typeof(PublicMarkerHooks).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)); }

        internal static void FullPrefix(object __instance, object __0, out Pass __state)
        { using (PolytopiaAgent.BridgeMod.MeasureCommandCallback()) FullPrefixCore(__instance, __0, out __state); }
        private static void FullPrefixCore(object __instance, object __0, out Pass __state)
        {
            __state = null;
            PublicMarkerHooks h = active;
            if (h == null || !h.installed) return;
            try
            {
                int thread = Thread.CurrentThread.ManagedThreadId;
                if (h.renderThreadId == 0) h.renderThreadId = thread;
                if (h.renderThreadId != thread) { h.collector.Invalidate(); return; }
                h.lastContext = __0; h.lastRenderer = __instance;
                h.completedView = null;
                Reader.Sample s = h.Capture(__instance, __0, null, null, null, null, h.revision());
                __state = new Pass(); __state.Renderer = __instance; __state.Context = __0;
                __state.Started = s; __state.Token = h.collector.NoteRenderStarted(s); h.renderDepth++;
            }
            catch (Exception) { h.collector.Invalidate(); }
        }

        internal static void MapPrefix(object __instance, object __0, object __1, out Pass __state)
        { using (PolytopiaAgent.BridgeMod.MeasureCommandCallback()) MapPrefixCore(__instance, __0, __1, out __state); }
        private static void MapPrefixCore(object __instance, object __0, object __1, out Pass __state)
        {
            FullPrefix(__instance, __0, out __state);
            PublicMarkerHooks h = active;
            if (h == null || __state == null) return;
            __state.Map = __1;
            if (__state.Started == null || !h.views.IncomingMapMatches(__0, __1, __state.Started.GameState)) h.collector.Invalidate();
        }
        internal static void MapPostfix(object __instance, object __0, object __1, bool __result, Pass __state)
        { using (PolytopiaAgent.BridgeMod.MeasureCommandCallback()) MapPostfixCore(__instance, __0, __1, __result, __state); }
        private static void MapPostfixCore(object __instance, object __0, object __1, bool __result, Pass __state)
        {
            PublicMarkerHooks h = active;
            bool mapValid = h != null && __state != null && __state.Started != null
                && h.views.SameNative(__state.Map, __1) && h.views.IncomingMapMatches(__0, __1, __state.Started.GameState);
            // RenderMap returns whether it allocated the roster or instantiated a Tile,
            // not whether rendering succeeded. Its normal return (also false) follows
            // the full Tile.Render loop. The finalizer invalidates genuine exceptions.
            Complete(__instance, __0, mapValid, __state);
        }
        internal static void AllPostfix(object __instance, object __0, Pass __state)
        { using (PolytopiaAgent.BridgeMod.MeasureCommandCallback()) Complete(__instance, __0, true, __state); }

        private static void Complete(object renderer, object context, bool successful, Pass pass)
        {
            PublicMarkerHooks h = active;
            if (h == null || pass == null || pass.Closed) return;
            try
            {
                if (!h.installed || h.renderThreadId != Thread.CurrentThread.ManagedThreadId || !successful || !h.views.SameNative(pass.Renderer, renderer)
                    || !h.views.SameNative(pass.Context, context)) { h.collector.Invalidate(); return; }
                NativeView.Scene scene;
                Reader.Sample s = h.Capture(renderer, context, null, null, null, null, h.revision(), out scene);
                if (pass.Started == null || s == null || pass.Started.NormalDefaultView != true
                    || !h.views.SameNative(pass.Started.GameState, s.GameState) || pass.Started.ObservedTurn != s.ObservedTurn
                    || !object.Equals(pass.Started.ControlledTribe, s.ControlledTribe)
                    || pass.Started.NativeRainbowVision != s.NativeRainbowVision)
                { h.collector.Invalidate(); return; }
                if (!h.collector.NoteRenderCompleted(pass.Token, s)) h.collector.Invalidate();
                else { h.completedView = scene; h.fullCompleted++; }
            }
            catch (Exception) { h.collector.Invalidate(); }
            finally { if (h.completedView == null) h.fullRejected++; pass.Closed = true; if (h.renderDepth > 0) h.renderDepth--; }
        }

        // Return the original exception unchanged; never suppress or replace the game's failure.
        internal static Exception FullFinalizer(Exception __exception, Pass __state)
        { using (PolytopiaAgent.BridgeMod.MeasureCommandCallback()) return FullFinalizerCore(__exception, __state); }
        private static Exception FullFinalizerCore(Exception __exception, Pass __state)
        {
            PublicMarkerHooks h = active;
            if (h != null)
            {
                if (__exception != null || (__state != null && !__state.Closed)) h.collector.Invalidate();
                if (__state != null && !__state.Closed) { __state.Closed = true; if (h.renderDepth > 0) h.renderDepth--; }
            }
            return __exception;
        }

        internal static void TileDefaultPrefix(object __0)
        { using (PolytopiaAgent.BridgeMod.MeasureCommandCallback()) TileDefaultPrefixCore(__0); }
        private static void TileDefaultPrefixCore(object __0)
        {
            if (__0 != null) { TilePrefix(__0); return; }
            // No construction or replacement by the bridge; the real native body resolves it.
            if (active != null && active.installed) active.defaultContextDeferred++;
        }

        internal static void TilePrefix(object __0)
        { using (PolytopiaAgent.BridgeMod.MeasureCommandCallback()) TilePrefixCore(__0); }
        private static void TilePrefixCore(object __0)
        {
            PublicMarkerHooks h = active;
            if (h == null || !h.installed) return;
            try
            {
                if (h.renderThreadId != Thread.CurrentThread.ManagedThreadId) { h.collector.Invalidate(); return; }
                bool changed = !h.views.SameNative(h.lastContext, __0);
                if (changed) h.tileContextChanged++;
                // A genuine full render already opened an unusable in-progress epoch.
                // Its postfix validates the complete scene again before publishing it.
                // The native loop passes the same context to every tile. Avoid rebuilding
                // that scene for each tile; different contexts and all partial renders
                // still use the full checks below. An exception finalizer still invalidates.
                if (h.renderDepth > 0 && !changed) { h.fullPassTileChecksReused++; return; }
                NativeView.Scene scene;
                Reader.Sample s = h.Capture(h.lastRenderer, __0, null, null, null, null, h.revision(), false, out scene);
                if (s == null || s.NormalDefaultView != true || s.ControlledId != s.ActualLocalId || s.ControlledId != s.ContextLocalId
                    || s.StillCurrent == null || !s.StillCurrent())
                { if (changed) h.tileContextRejected++; h.collector.Invalidate(); return; }
                if (changed)
                {
                    // Native partial rendering constructs fresh contexts. Retain a genuine full
                    // epoch only when all scene identities/settings except the disposable context
                    // object are unchanged. A tile call cannot establish or revive an epoch.
                    Reader.RenderEpoch epoch = h.collector.CurrentEpoch();
                    if (h.renderDepth != 0 || !epoch.IsUsable() || !epoch.Matches(s)
                        || !NativeView.Scene.SameView(h.completedView, scene))
                    { h.tileContextRejected++; h.collector.Invalidate(); return; }
                    h.lastContext = __0; h.tileContextAdopted++;
                }
                // During a real RenderMap, its roster can legitimately be allocated by the body.
                if (h.renderDepth == 0) h.collector.InvalidateFor(s);
            }
            catch (Exception) { h.collector.Invalidate(); }
        }
        internal static Exception TileFinalizer(Exception __exception)
        { using (PolytopiaAgent.BridgeMod.MeasureCommandCallback()) { if (__exception != null && active != null) active.collector.Invalidate(); return __exception; } }

        internal Domains BuildWorld(object gs, string episode, int? controlled, int? observedTurn, long observationRevision)
        {
            Reader.Sample s = null;
            try
            {
                if (installed && views != null)
                    s = Capture(lastRenderer, lastContext, gs, episode, controlled, observedTurn, observationRevision);
            }
            catch (Exception) { collector.Invalidate(); }
            if (s == null) s = UnavailableSample(gs, episode, controlled, observedTurn, observationRevision);
            long epochVersion = collector.CurrentEpoch().Version;
            string siege = collector.Build(Reader.Siege, s), rainbow = collector.Build(Reader.RuinRainbow, s);
            // A change while sampling either domain withholds both; an earlier domain must not
            // escape in an outer world frame that changed during the second domain's sampling.
            bool stable = false;
            try { stable = s.StillCurrent != null && s.StillCurrent() && collector.CurrentEpoch().Version == epochVersion; }
            catch (Exception) { }
            if (installed && views != null && !stable)
            { collector.Invalidate(); return UnavailableDomains(gs, episode, controlled, observedTurn, observationRevision, "observation-context-unavailable"); }
            return new Domains(siege, rainbow);
        }

        internal static Domains UnavailableDomains(object gs, string episode, int? controlled, int? turn, long rev,
            string reason = "native-bindings-unavailable")
        {
            Reader.Sample s = UnavailableSample(gs, episode, controlled, turn, rev);
            return new Domains(Reader.UnavailableDomain(Reader.Siege, s, reason), Reader.UnavailableDomain(Reader.RuinRainbow, s, reason));
        }
        private static Reader.Sample UnavailableSample(object gs, string episode, int? controlled, int? turn, long rev)
        { return new Reader.Sample(episode, controlled, null, null, null, null, null, turn, rev, gs, null, null, null, delegate { return false; }); }

        private Reader.Sample Capture(object renderer, object context, object worldGs, string worldEpisode,
            int? worldControlled, int? worldTurn, long observationRevision)
        {
            NativeView.Scene ignored;
            return Capture(renderer, context, worldGs, worldEpisode, worldControlled, worldTurn, observationRevision, out ignored);
        }

        private Reader.Sample Capture(object renderer, object context, object worldGs, string worldEpisode,
            int? worldControlled, int? worldTurn, long observationRevision, out NativeView.Scene frozen)
        { return Capture(renderer, context, worldGs, worldEpisode, worldControlled, worldTurn, observationRevision, true, out frozen); }

        private Reader.Sample Capture(object renderer, object context, object worldGs, string worldEpisode,
            int? worldControlled, int? worldTurn, long observationRevision, bool readRoster, out NativeView.Scene frozen)
        {
            frozen = null;
            if (views == null || renderer == null || context == null || renderThreadId == 0
                || renderThreadId != Thread.CurrentThread.ManagedThreadId) return null;
            frozen = views.ReadScene(renderer, context, readRoster);
            if (frozen == null) return null;
            NativeView.Scene captured = frozen;
            object gs = worldGs ?? frozen.GameState;
            string episode = worldEpisode ?? episodeOf(gs);
            int? controlled = worldControlled ?? controlledOf(gs);
            int? turn = worldTurn ?? frozen.Turn;
            bool expected = views.SameNative(gs, frozen.GameState) && turn == frozen.Turn;
            Func<bool> stillCurrent = delegate
            {
                if (!expected || revision() != observationRevision || episodeOf(gs) != episode || controlledOf(gs) != controlled) return false;
                NativeView.Scene now = views.ReadScene(renderer, context, false);
                return NativeView.Scene.Same(captured, now) && views.SameNative(gs, now == null ? null : now.GameState);
            };
            return new Reader.Sample(episode, controlled, frozen.LocalId, frozen.ContextLocalId,
                frozen.RendererIdentity, frozen.RosterIdentity, frozen.Roster, turn, observationRevision, gs,
                frozen.Tribe, frozen.Ability, expected ? frozen.Normal : null, stillCurrent);
        }

        // Exact generated aliases verified by Cecil; no leaf/suffix search.
        internal static Type ResolveExact(Func<string, Type> resolver, string name)
        {
            if (resolver == null) return null;
            string generated = name;
            if (name == "PolytopiaBackendBase.Common.TribeType") generated = "Il2CppPolytopiaBackendBase.Common.TribeType";
            else if (name.StartsWith("Polytopia.Data.", StringComparison.Ordinal)) generated = "Il2Cpp" + name;
            else if (!name.StartsWith("UnityEngine.", StringComparison.Ordinal) && !name.StartsWith("Il2CppInterop.", StringComparison.Ordinal)) generated = "Il2Cpp." + name;
            return resolver(generated) ?? resolver(name);
        }

        private sealed class NativePointerPlan
        {
            internal readonly Type BaseType;
            internal readonly PropertyInfo Pointer;
            internal NativePointerPlan(Type type, PropertyInfo property) { BaseType=type; Pointer=property; }
        }
        private static volatile NativePointerPlan nativePointerPlan;
        private static readonly object nativePointerPlanGate=new object();

        internal static object StableNativeIdentity(object value)
        {
            if (value == null) return null;
            try
            {
                NativePointerPlan plan=nativePointerPlan;
                if(plan==null) lock(nativePointerPlanGate)
                {
                    plan=nativePointerPlan;
                    if(plan==null)
                    {
                        Type baseType=null;
                        foreach(Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            if(a.GetName().Name!="Il2CppInterop.Runtime") continue;
                            baseType=a.GetType("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase",false); break;
                        }
                        if(baseType==null) return null; // Early missing runtime is not cached.
                        PropertyInfo pointer=baseType.GetProperty("Pointer",BindingFlags.DeclaredOnly|BindingFlags.Public|BindingFlags.Instance);
                        if(pointer==null || pointer.PropertyType!=typeof(IntPtr) || pointer.GetGetMethod()==null) return null;
                        plan=new NativePointerPlan(baseType,pointer); nativePointerPlan=plan;
                    }
                }
                if(!plan.BaseType.IsInstanceOfType(value)) return null;
                object raw=plan.Pointer.GetValue(value,null); // Always read the live pointer, including disposal.
                return raw is IntPtr && (IntPtr)raw != IntPtr.Zero ? raw : null;
            }
            catch (Exception) { return null; }
        }

        internal sealed class NativeView
        {
            internal long RosterSnapshots;
            private readonly Dictionary<string, Type> types;
            private readonly Func<object, object> identity;
            private const BindingFlags Own = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            private const BindingFlags OwnStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            private NativeView(Dictionary<string, Type> knownTypes, Func<object, object> stableIdentity) { types = knownTypes; identity = stableIdentity; }

            internal static NativeView CreateNative(Func<string, Type> resolver)
            {
                string[] names = { "GameManager", "GameState", "PlayerState", "MapRenderer", "MapRendererInner", "MapRenderContext",
                    "MapRenderContext+FogVisibilitySettings", "MapData", "ClientBase", "Polytopia.Data.GameLogicData", "Polytopia.Data.TribeData",
                    "Polytopia.Data.TribeAbility+Type", "TribeDataExtensions", "PolytopiaBackendBase.Common.TribeType", "Tile", "SkinVisualsTransientData", "UnityEngine.Object" };
                string[] assemblies = { "PolytopiaAssembly", "GameLogicAssembly", "GameLogicAssembly", "PolytopiaAssembly", "PolytopiaAssembly",
                    "PolytopiaAssembly", "PolytopiaAssembly", "GameLogicAssembly", "PolytopiaAssembly", "GameLogicAssembly", "GameLogicAssembly",
                    "GameLogicAssembly", "GameLogicAssembly", "PolytopiaBackendBase", "PolytopiaAssembly", "PolytopiaAssembly", "UnityEngine.CoreModule" };
                try
                {
                    Dictionary<string, Type> known = new Dictionary<string, Type>();
                    for (int i = 0; i < names.Length; i++)
                    {
                        Type t = ResolveExact(resolver, names[i]);
                        string generated = names[i].StartsWith("Polytopia.Data.", StringComparison.Ordinal) || names[i].StartsWith("PolytopiaBackendBase.", StringComparison.Ordinal)
                            ? "Il2Cpp" + names[i] : "Il2Cpp." + names[i];
                        if (t == null || (t.FullName != generated && t.FullName != names[i])) return null;
                        string a = t.Assembly.GetName().Name;
                        if (a != assemblies[i] && a != "Il2Cpp" + assemblies[i]) return null;
                        known[names[i]] = t;
                    }
                    return new NativeView(known, StableNativeIdentity);
                }
                catch (Exception) { return null; }
            }
            internal static NativeView CreateForTests(Dictionary<string, Type> known, Func<object, object> identity)
            { return new NativeView(known, identity); }

            private Type T(string name) { Type t; return types.TryGetValue(name, out t) ? t : null; }
            internal MethodInfo ExactMethod(string typeName, string methodName, Type result, params string[] arguments)
            {
                try
                {
                    Type declaring = T(typeName); if (declaring == null) return null;
                    Type[] args = new Type[arguments.Length];
                    for (int i = 0; i < args.Length; i++) { args[i] = T(arguments[i]); if (args[i] == null) return null; }
                    MethodInfo m = declaring.GetMethod(methodName, Own, null, args, null);
                    return m != null && !m.IsStatic && m.ReturnType == result ? m : null;
                }
                catch (Exception) { return null; }
            }

            private object Get(object target, string declaringName, string memberName, Type expected, bool isStatic)
            {
                Type declaring = T(declaringName);
                if (declaring == null || expected == null || (!isStatic && (target == null || !declaring.IsInstanceOfType(target)))) return null;
                try
                {
                    BindingFlags flags = isStatic ? OwnStatic : Own;
                    PropertyInfo p = declaring.GetProperty(memberName, flags);
                    if (p != null && p.PropertyType == expected && p.GetIndexParameters().Length == 0 && p.GetGetMethod(true) != null)
                        return p.GetValue(target, null);
                    FieldInfo f = declaring.GetField(memberName, flags);
                    if (f != null && f.FieldType == expected) return f.GetValue(target);
                    MethodInfo m = declaring.GetMethod("get_" + memberName, flags, null, Type.EmptyTypes, null);
                    if (m != null && m.ReturnType == expected && m.IsStatic == isStatic) return m.Invoke(target, null);
                }
                catch (Exception) { }
                return null;
            }

            private object Id(object value) { try { return value == null ? null : identity(value); } catch (Exception) { return null; } }
            internal bool SameNative(object a, object b) { object x = Id(a), y = Id(b); return x != null && y != null && object.Equals(x, y); }
            internal bool IncomingMapMatches(object context, object incomingMap, object gs)
            {
                Type map = T("MapData");
                return map != null && incomingMap != null && map.IsInstanceOfType(incomingMap)
                    && SameNative(incomingMap, Get(context, "MapRenderContext", "mapData", map, false))
                    && SameNative(incomingMap, Get(gs, "GameState", "Map", map, false));
            }
            private bool? UnityLive(object value)
            {
                try
                {
                    Type t = T("UnityEngine.Object"); if (value == null || t == null || !t.IsInstanceOfType(value)) return null;
                    MethodInfo m = t.GetMethod("op_Inequality", OwnStatic, null, new Type[] { t, t }, null);
                    return m == null || m.ReturnType != typeof(bool) ? null : Reader.StrictBool(m.Invoke(null, new object[] { value, null }));
                }
                catch (Exception) { return null; }
            }

            internal sealed class Scene
            {
                internal object GameState, StateIdentity, MapIdentity, ClientIdentity, PlayerIdentity, ContextIdentity,
                    OuterIdentity, RendererIdentity, RosterIdentity, GldIdentity, Tribe;
                internal int? LocalId, ContextLocalId, Turn, TribeNumber, Fog;
                internal bool? Replay, Spectating, Ability, Normal;
                internal IEnumerable Roster;
                internal static bool Same(Scene a, Scene b)
                { return SameView(a, b) && Equal(a.ContextIdentity, b.ContextIdentity); }
                internal static bool SameView(Scene a, Scene b)
                {
                    return SameRenderScope(a, b) && Equal(a.StateIdentity, b.StateIdentity)
                        && Equal(a.MapIdentity, b.MapIdentity) && Equal(a.PlayerIdentity, b.PlayerIdentity);
                }
                internal static bool SameRenderScope(Scene a, Scene b)
                {
                    if (a == null || b == null) return false;
                    return Equal(a.ClientIdentity, b.ClientIdentity) && Equal(a.OuterIdentity, b.OuterIdentity)
                        && Equal(a.RendererIdentity, b.RendererIdentity) && Equal(a.RosterIdentity, b.RosterIdentity) && Equal(a.GldIdentity, b.GldIdentity)
                        && a.LocalId == b.LocalId && a.ContextLocalId == b.ContextLocalId && a.Turn == b.Turn && a.TribeNumber == b.TribeNumber
                        && a.Fog == b.Fog && a.Replay == b.Replay && a.Spectating == b.Spectating && a.Ability == b.Ability && a.Normal == b.Normal;
                }
                private static bool Equal(object a, object b) { return a != null && b != null && object.Equals(a, b); }
            }

            internal Scene ReadScene(object renderer, object context, bool readRoster)
            {
                Scene s = new Scene();
                try
                {
                    if (renderer == null || context == null || !T("MapRendererInner").IsInstanceOfType(renderer) || !T("MapRenderContext").IsInstanceOfType(context)) return null;
                    object gs = Get(null, "GameManager", "GameState", T("GameState"), true);
                    object local = Get(null, "GameManager", "LocalPlayer", T("PlayerState"), true);
                    object client = Get(null, "GameManager", "Client", T("ClientBase"), true);
                    object clientGs = Get(client, "ClientBase", "GameState", T("GameState"), false);
                    object map = Get(gs, "GameState", "Map", T("MapData"), false);
                    object contextMap = Get(context, "MapRenderContext", "mapData", T("MapData"), false);
                    object outer = Get(null, "MapRenderer", "Current", T("MapRenderer"), true);
                    object actualInner = Get(outer, "MapRenderer", "mapRenderer", T("MapRendererInner"), false);
                    object gld = Get(context, "MapRenderContext", "gld", T("Polytopia.Data.GameLogicData"), false);
                    object roster = ReadRosterField(renderer);
                    s.GameState = gs; s.StateIdentity = Id(gs); s.PlayerIdentity = Id(local); s.ClientIdentity = Id(client);
                    s.MapIdentity = Id(map); s.ContextIdentity = Id(context); s.OuterIdentity = Id(outer); s.RendererIdentity = Id(renderer);
                    s.RosterIdentity = Id(roster); s.GldIdentity = Id(gld);
                    s.LocalId = Reader.StrictInt(Get(local, "PlayerState", "Id", typeof(byte), false));
                    s.ContextLocalId = Reader.StrictInt(Get(context, "MapRenderContext", "localPlayerId", typeof(byte), false));
                    s.Turn = Reader.StrictInt(Get(gs, "GameState", "CurrentTurn", typeof(uint), false));
                    s.Tribe = Get(local, "PlayerState", "tribe", T("PolytopiaBackendBase.Common.TribeType"), false);
                    s.TribeNumber = Reader.StrictEnumInt(s.Tribe, T("PolytopiaBackendBase.Common.TribeType"));
                    s.Fog = Reader.StrictEnumInt(Get(context, "MapRenderContext", "fogVisibilitySettings", T("MapRenderContext+FogVisibilitySettings"), false), T("MapRenderContext+FogVisibilitySettings"));
                    s.Replay = Reader.StrictBool(Get(client, "ClientBase", "IsReplay", typeof(bool), false));
                    s.Spectating = Reader.StrictBool(Get(client, "ClientBase", "IsSpectating", typeof(bool), false));
                    if (s.TribeNumber == Reader.ElyrionTribe) s.Ability = ReadAbility(gld, s.Tribe);
                    bool identities = s.StateIdentity != null && s.PlayerIdentity != null && s.ClientIdentity != null && s.MapIdentity != null
                        && s.ContextIdentity != null && s.OuterIdentity != null && s.RendererIdentity != null && s.GldIdentity != null
                        && SameNative(gs, clientGs) && SameNative(map, contextMap) && SameNative(renderer, actualInner) && UnityLive(outer) == true;
                    s.Normal = identities && s.Fog == 0 && s.Replay == false && s.Spectating == false ? true : (bool?)null;
                    if (readRoster && roster != null) s.Roster = SnapshotRoster(roster);
                    return s;
                }
                catch (Exception) { return null; }
            }

            private object ReadRosterField(object renderer)
            {
                try
                {
                    Type declaring = T("MapRendererInner");
                    PropertyInfo p = declaring.GetProperty("renderedTiles", Own); FieldInfo f = declaring.GetField("renderedTiles", Own);
                    Type arrayType = p != null ? p.PropertyType : (f == null ? null : f.FieldType);
                    if (!RosterType(arrayType)) return null;
                    return Get(renderer, "MapRendererInner", "renderedTiles", arrayType, false);
                }
                catch (Exception) { return null; }
            }
            private bool RosterType(Type type)
            {
                if (type == null) return false;
                if (type.IsArray) return type.GetElementType() == T("Tile");
                return type.IsGenericType && type.GetGenericTypeDefinition().FullName == "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1"
                    && type.GetGenericArguments()[0] == T("Tile");
            }
            private IEnumerable SnapshotRoster(object raw)
            {
                RosterSnapshots++;
                try
                {
                    List<object> roster = new List<object>(); IEnumerable iterable = raw as IEnumerable;
                    if (iterable != null) { foreach (object tile in iterable) roster.Add(tile); return roster; }
                    // Exact generated array fallback: failure is all-or-nothing, never a truncated roster.
                    Type t = raw.GetType(); PropertyInfo length = t.GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
                    PropertyInfo item = t.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance, null, T("Tile"), new Type[] { typeof(int) }, null);
                    if (length == null || length.PropertyType != typeof(int) || item == null) return null;
                    int? n = Reader.StrictInt(length.GetValue(raw, null)); if (!n.HasValue || n.Value < 0) return null;
                    for (int i = 0; i < n.Value; i++) roster.Add(item.GetValue(raw, new object[] { i }));
                    return roster;
                }
                catch (Exception) { return null; }
            }

            private bool? ReadAbility(object gld, object tribe)
            {
                try
                {
                    Type gd = T("Polytopia.Data.GameLogicData"), td = T("Polytopia.Data.TribeData"), tt = T("PolytopiaBackendBase.Common.TribeType"),
                        abilityType = T("Polytopia.Data.TribeAbility+Type"), extensions = T("TribeDataExtensions");
                    if (gld == null || !gd.IsInstanceOfType(gld) || tribe == null || tribe.GetType() != tt || !abilityType.IsEnum) return null;
                    MethodInfo get = gd.GetMethod("GetTribeData", Own, null, new Type[] { tt }, null);
                    MethodInfo has = extensions.GetMethod("HasAbility", OwnStatic, null, new Type[] { td, abilityType }, null);
                    if (get == null || get.ReturnType != td || get.IsStatic || has == null || has.ReturnType != typeof(bool) || !has.IsStatic) return null;
                    object data = get.Invoke(gld, new object[] { tribe }); if (data == null || !td.IsInstanceOfType(data)) return null;
                    object ability = Enum.ToObject(abilityType, Reader.RainbowVisionAbility);
                    if (!Enum.IsDefined(abilityType, ability) || Enum.GetName(abilityType, ability) != "RainbowVision") return null;
                    return Reader.StrictBool(has.Invoke(null, new object[] { data, ability }));
                }
                catch (Exception) { return null; }
            }
        }
    }
}
