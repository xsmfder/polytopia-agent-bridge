// Offline core only. No render hooks are installed here; a new Collector starts unavailable.
// Candidates MUST be the actual MapRendererInner.renderedTiles roster. Nothing enumerates MapData.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace PolytopiaPublicMarkers
{
    public static class PublicMarkerReader
    {
        public const string Siege = "siege";
        public const string RuinRainbow = "ruin_rainbow";
        public const int ElyrionTribe = 5, RainbowVisionAbility = 1, RuinImprovement = 2;

        public static bool? StrictBool(object value) { return value is bool ? (bool)value : (bool?)null; }
        public static int? StrictInt(object value)
        {
            if (value is byte) return (byte)value;
            if (value is sbyte) return (sbyte)value;
            if (value is short) return (short)value;
            if (value is ushort) return (ushort)value;
            if (value is int) return (int)value;
            if (value is uint && (uint)value <= int.MaxValue) return (int)(uint)value;
            if (value is long && (long)value >= int.MinValue && (long)value <= int.MaxValue) return (int)(long)value;
            return null;
        }

        // Accept this exact enum type (including Il2Cpp enum wrappers), never an unrelated enum.
        public static int? StrictEnumInt(object value, Type enumType)
        {
            if (enumType == null || !enumType.IsEnum) return null;
            int? number;
            try
            {
                if (value != null && value.GetType().IsEnum)
                {
                    if (value.GetType() != enumType) return null;
                    number = StrictInt(Convert.ChangeType(value, Enum.GetUnderlyingType(enumType), CultureInfo.InvariantCulture));
                }
                else number = StrictInt(value);
                if (!number.HasValue || !Enum.IsDefined(enumType, Enum.ToObject(enumType, number.Value))) return null;
                return number;
            }
            catch (Exception) { return null; }
        }

        internal sealed class Read
        {
            internal readonly bool Success;
            internal readonly object Value;
            internal Read(bool success, object value) { Success = success; Value = value; }
            internal static readonly Read Unknown = new Read(false, null);
        }

        /// <summary>
        /// Exact getters for the recorded native shapes, not a name-search over arbitrary objects.
        /// Native factory checks full type/assembly names; the internal fake factory is test-only.
        /// Generated Il2Cpp fields may be properties; only the exact recorded name and type are read.
        /// A missing member and a successfully read null are distinct.
        /// </summary>
        public sealed class Bindings
        {
            internal readonly Type tile, wrapper, controller, tileData, gameState, coordinates,
                improvementState, improvementType, tribeType, unityObject, component, gameObject, particle;
            private readonly Func<object, object> stableIdentity;
            private const BindingFlags Own = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            private Bindings(Type[] types, Func<object, object> identity)
            {
                tile = types[0]; wrapper = types[1]; controller = types[2]; tileData = types[3];
                gameState = types[4]; coordinates = types[5]; improvementState = types[6];
                improvementType = types[7]; tribeType = types[8]; unityObject = types[9];
                component = types[10]; gameObject = types[11]; particle = types[12]; stableIdentity = identity;
            }

            internal static bool NativeTypeNameMatches(string nativeName, string actualName)
            {
                // Exact generated aliases checked offline with Mono.Cecil; namespace rewriting
                // is NOT universally "Il2Cpp." + nativeName for the two namespaced enum types.
                if (nativeName == "Polytopia.Data.ImprovementData+Type")
                    return actualName == nativeName || actualName == "Il2CppPolytopia.Data.ImprovementData+Type";
                if (nativeName == "PolytopiaBackendBase.Common.TribeType")
                    return actualName == nativeName || actualName == "Il2CppPolytopiaBackendBase.Common.TribeType";
                return actualName == nativeName || actualName == "Il2Cpp." + nativeName;
            }

            // resolver should resolve the exact supplied native name to its known generated alias.
            // stableIdentity must use private native identity, not managed ReferenceEquals or public pointers.
            public static Bindings CreateNative(Func<string, Type> resolver, Func<object, object> stableIdentity)
            {
                if (resolver == null || stableIdentity == null) return null;
                string[] names = { "Tile", "ParticleController+ObjectPoolWrapper", "ParticleController", "TileData", "GameState",
                    "WorldCoordinates", "ImprovementState", "Polytopia.Data.ImprovementData+Type",
                    "PolytopiaBackendBase.Common.TribeType", "UnityEngine.Object", "UnityEngine.Component",
                    "UnityEngine.GameObject", "UnityEngine.ParticleSystem" };
                string[] assemblies = { "PolytopiaAssembly", "PolytopiaAssembly", "PolytopiaAssembly", "GameLogicAssembly",
                    "GameLogicAssembly", "GameLogicAssembly", "GameLogicAssembly", "GameLogicAssembly", "PolytopiaBackendBase",
                    "UnityEngine.CoreModule", "UnityEngine.CoreModule", "UnityEngine.CoreModule", "UnityEngine.ParticleSystemModule" };
                try
                {
                    Type[] types = new Type[names.Length];
                    for (int i = 0; i < names.Length; i++)
                    {
                        Type t = resolver(names[i]);
                        if (t == null || !NativeTypeNameMatches(names[i], t.FullName)) return null;
                        string assembly = t.Assembly.GetName().Name;
                        if (assembly != assemblies[i] && assembly != "Il2Cpp" + assemblies[i]) return null;
                        types[i] = t;
                    }
                    if (types[1].DeclaringType != types[2] || !types[7].IsEnum || !types[8].IsEnum
                        || !types[9].IsAssignableFrom(types[0]) || !types[10].IsAssignableFrom(types[2])
                        || !types[9].IsAssignableFrom(types[11]) || !types[9].IsAssignableFrom(types[12])) return null;
                    return new Bindings(types, stableIdentity);
                }
                catch (Exception) { return null; }
            }

            internal static Bindings CreateForTests(Type[] types, Func<object, object> stableIdentity)
            { return new Bindings(types, stableIdentity); }

            internal Read Get(object target, Type declaring, string name, Type expected)
            {
                if (target == null || declaring == null || !declaring.IsInstanceOfType(target)) return Read.Unknown;
                try
                {
                    FieldInfo f = declaring.GetField(name, Own);
                    if (f != null) return f.FieldType == expected ? new Read(true, f.GetValue(target)) : Read.Unknown;
                    PropertyInfo p = declaring.GetProperty(name, Own);
                    if (p != null && p.PropertyType == expected && p.GetIndexParameters().Length == 0
                        && p.GetGetMethod(true) != null) return new Read(true, p.GetValue(target, null));
                    // Bare getter support uses a single exact method signature, never loose overload matching.
                    return Call(target, declaring, "get_" + name, expected, Type.EmptyTypes, new object[0]);
                }
                catch (Exception) { return Read.Unknown; }
            }

            internal Read Call(object target, Type declaring, string name, Type result, Type[] args, object[] values)
            {
                if (target == null || declaring == null || !declaring.IsInstanceOfType(target)) return Read.Unknown;
                try
                {
                    MethodInfo m = declaring.GetMethod(name, Own, null, args, null);
                    if (m == null || m.IsStatic || m.ReturnType != result || m.ContainsGenericParameters) return Read.Unknown;
                    return new Read(true, m.Invoke(target, values));
                }
                catch (Exception) { return Read.Unknown; }
            }

            internal bool? Live(object value)
            {
                if (value == null) return false;
                if (unityObject == null || !unityObject.IsInstanceOfType(value)) return null;
                try
                {
                    MethodInfo m = unityObject.GetMethod("op_Inequality", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
                        null, new Type[] { unityObject, unityObject }, null);
                    if (m == null || m.ReturnType != typeof(bool)) return null;
                    return StrictBool(m.Invoke(null, new object[] { value, null }));
                }
                catch (Exception) { return null; }
            }

            internal object Identity(object value)
            {
                if (value == null || stableIdentity == null) return null;
                try { return stableIdentity(value); } catch (Exception) { return null; }
            }

            internal int? Tribe(object value) { return StrictEnumInt(value, tribeType); }
            internal bool GameStateValid(object value) { return value != null && gameState.IsInstanceOfType(value); }
            internal Read Fx(object value, string markerType) { return Get(value, tile, markerType == Siege ? "fireFx" : "rainbowFireFx", wrapper); }
            internal Read Controller(object value) { return Get(value, wrapper, "pooledParticleController", controller); }
            internal Read WrapperLease(object value) { return Get(value, wrapper, "poolId", typeof(int)); }
            internal Read ControllerLease(object value) { return Get(value, controller, "poolId", typeof(int)); }
            internal Read Used(object value) { return Get(value, controller, "IsUsed", typeof(bool)); }
            internal Read Playing(object value) { return Get(value, controller, "isPlaying", typeof(bool)); }
            internal Read Stopping(object value) { return Get(value, controller, "isStopping", typeof(bool)); }
            internal Read GameObject(object value) { return Get(value, component, "gameObject", gameObject); }
            internal Read Active(object value) { return Get(value, gameObject, "activeInHierarchy", typeof(bool)); }
            internal Read Data(object value) { return Get(value, tile, "Data", tileData); }
            internal Read Captured(object value, object state) { return Call(value, tileData, "IsBeingCaptured", typeof(bool), new Type[] { gameState }, new object[] { state }); }
            internal Read Improvement(object value) { return Get(value, tileData, "improvement", improvementState); }
            internal Read ImprovementKind(object value) { return Get(value, improvementState, "type", improvementType); }
            internal string Coords(object value)
            {
                Read c = Get(value, tile, "Coordinates", coordinates);
                if (!c.Success || c.Value == null) return null;
                Read x = Get(c.Value, coordinates, "X", typeof(int)), y = Get(c.Value, coordinates, "Y", typeof(int));
                int? xi = x.Success ? StrictInt(x.Value) : null, yi = y.Success ? StrictInt(y.Value) : null;
                if (!xi.HasValue || !yi.HasValue) return null;
                return "(" + xi.Value.ToString(CultureInfo.InvariantCulture) + "," + yi.Value.ToString(CultureInfo.InvariantCulture) + ")";
            }

            internal bool? Alive(object value)
            {
                // Actual controller.particles array, actual ParticleSystem.IsAlive(bool withChildren).
                Read list = Get(value, controller, "particles", controller == null ? null : ParticleArrayType());
                if (!list.Success || list.Value == null) return null;
                IEnumerable items = list.Value as IEnumerable;
                if (items == null) return null;
                bool alive = false, unknown = false;
                try
                {
                    foreach (object p in items)
                    {
                        bool? live = Live(p);
                        if (!live.HasValue) { unknown = true; continue; }
                        if (!live.Value) continue;
                        Read read = Call(p, particle, "IsAlive", typeof(bool), new Type[] { typeof(bool) }, new object[] { true });
                        bool? result = read.Success ? StrictBool(read.Value) : null;
                        if (!result.HasValue) unknown = true;
                        else if (result.Value) alive = true;
                    }
                }
                catch (Exception) { return null; }
                return alive ? true : (unknown ? (bool?)null : false);
            }

            private Type ParticleArrayType()
            {
                // Native field's generated array wrapper type is validated against its exact particle element.
                try
                {
                    FieldInfo f = controller.GetField("particles", Own);
                    PropertyInfo p = controller.GetProperty("particles", Own);
                    Type t = f != null ? f.FieldType : (p != null ? p.PropertyType : null);
                    if (t == null) return null;
                    if (t.IsArray && t.GetElementType() == particle) return t;
                    if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1"
                        && t.GetGenericArguments()[0] == particle) return t;
                }
                catch (Exception) { }
                return null;
            }
        }

        /// <summary>Freeze these values from the real observation, not a fabricated render context.</summary>
        public sealed class Sample
        {
            public readonly string Episode;
            public readonly int? ControlledId, ActualLocalId, ContextLocalId, ObservedTurn;
            public readonly long? ObservationRevision;
            public readonly object RendererIdentity, RosterIdentity, GameState, ControlledTribe;
            public readonly IEnumerable RenderedTiles;
            public readonly bool? NativeRainbowVision, NormalDefaultView;
            // Bridge must re-read episode/player/context/renderer/roster/turn/revision/state identity.
            public readonly Func<bool> StillCurrent;
            public Sample(string episode, int? controlledId, int? actualLocalId, int? contextLocalId,
                object rendererIdentity, object rosterIdentity, IEnumerable renderedTiles, int? observedTurn,
                long? observationRevision, object gameState, object controlledTribe, bool? nativeRainbowVision,
                bool? normalDefaultView, Func<bool> stillCurrent)
            {
                Episode = episode; ControlledId = controlledId; ActualLocalId = actualLocalId; ContextLocalId = contextLocalId;
                RendererIdentity = rendererIdentity; RosterIdentity = rosterIdentity; RenderedTiles = renderedTiles;
                ObservedTurn = observedTurn; ObservationRevision = observationRevision; GameState = gameState;
                ControlledTribe = controlledTribe; NativeRainbowVision = nativeRainbowVision;
                NormalDefaultView = normalDefaultView; StillCurrent = stillCurrent;
            }
        }

        public sealed class RenderEpoch
        {
            public readonly long Version;
            public readonly string Episode;
            public readonly int? ControlledId, ActualLocalId, ContextLocalId;
            public readonly object RendererIdentity, RosterIdentity;
            public readonly bool Completed, InProgress;
            internal RenderEpoch(long version, Sample s, bool completed, bool progress)
            {
                Version = version; Episode = s == null ? null : s.Episode;
                ControlledId = s == null ? null : s.ControlledId; ActualLocalId = s == null ? null : s.ActualLocalId;
                ContextLocalId = s == null ? null : s.ContextLocalId; RendererIdentity = s == null ? null : s.RendererIdentity;
                RosterIdentity = s == null ? null : s.RosterIdentity; Completed = completed; InProgress = progress;
            }
            public bool IsUsable() { return Completed && !InProgress && IdentityValid(); }
            private bool IdentityValid()
            {
                return !string.IsNullOrEmpty(Episode) && ControlledId.HasValue && ControlledId.Value > 0
                    && ActualLocalId == ControlledId && ContextLocalId == ControlledId
                    && RendererIdentity != null && RosterIdentity != null;
            }
            internal bool Matches(Sample s)
            {
                return MatchesRenderContext(s) && Same(RosterIdentity, s.RosterIdentity);
            }
            internal bool MatchesRenderContext(Sample s)
            {
                return s != null && Episode == s.Episode && ControlledId == s.ControlledId && ActualLocalId == s.ActualLocalId
                    && ContextLocalId == s.ContextLocalId && Same(RendererIdentity, s.RendererIdentity);
            }
        }

        private static bool Same(object a, object b) { return a != null && b != null && object.Equals(a, b); }
        private static bool Current(Sample s)
        {
            if (s == null || s.StillCurrent == null) return false;
            try { return s.StillCurrent(); } catch (Exception) { return false; }
        }

        private sealed class Trail
        {
            internal readonly object TileIdentity, ControllerIdentity;
            internal readonly int Lease;
            internal readonly string Coords;
            internal Trail(object tileIdentity, object controllerIdentity, int lease, string coords)
            { TileIdentity = tileIdentity; ControllerIdentity = controllerIdentity; Lease = lease; Coords = coords; }
        }

        /// <summary>One Bridge-owned collector; call only on the game's observation/render thread.</summary>
        public sealed class Collector
        {
            private readonly Bindings bindings;
            private RenderEpoch epoch = new RenderEpoch(0, null, false, false);
            private long version;
            private readonly Dictionary<string, List<Trail>> trails = new Dictionary<string, List<Trail>>();
            public Collector(Bindings nativeBindings) { bindings = nativeBindings; }
            public RenderEpoch CurrentEpoch() { return epoch; }

            // Prefix token prevents an old completion from blessing a newer render.
            public long NoteRenderStarted(Sample actualRender)
            {
                if (!epoch.Matches(actualRender)) trails.Clear();
                epoch = new RenderEpoch(++version, actualRender, false, true);
                return version;
            }
            public bool NoteRenderCompleted(long prefixToken, Sample actualRender)
            {
                if (epoch.Version != prefixToken || !epoch.InProgress || !epoch.MatchesRenderContext(actualRender)
                    || actualRender == null || actualRender.RenderedTiles == null || actualRender.NormalDefaultView != true
                    || !Current(actualRender)) { Invalidate(); return false; }
                // RenderMap may allocate/replace renderedTiles inside this very pass. Its genuine
                // postfix binds the resulting roster; old classified trails may not cross that replacement.
                if (!Same(epoch.RosterIdentity, actualRender.RosterIdentity)) trails.Clear();
                RenderEpoch done = new RenderEpoch(++version, actualRender, true, false);
                if (!done.IsUsable()) { Invalidate(); return false; }
                epoch = done;
                return true;
            }
            public void Invalidate() { trails.Clear(); epoch = new RenderEpoch(++version, null, false, false); }
            public void InvalidateFor(Sample current)
            { if (!epoch.Matches(current)) Invalidate(); }

            public string Build(string markerType, Sample s)
            {
                if (markerType != Siege && markerType != RuinRainbow) return Domain(markerType, "unavailable", "marker-type-unknown", s, null);
                InvalidateFor(s);
                RenderEpoch start = epoch;
                if (s == null || bindings == null) return Domain(markerType, "unavailable", "native-bindings-unavailable", s, null);
                if (!start.IsUsable() || !start.Matches(s) || s.NormalDefaultView != true)
                    return Domain(markerType, "unavailable", "render-epoch-unavailable", s, null);
                if (s.RenderedTiles == null || !s.ObservedTurn.HasValue || s.ObservedTurn.Value < 0
                    || !s.ObservationRevision.HasValue || s.ObservationRevision.Value <= 0 || !bindings.GameStateValid(s.GameState) || !Current(s))
                    return Domain(markerType, "unavailable", "observation-context-unavailable", s, null);
                if (markerType == RuinRainbow)
                {
                    int? tribe = bindings.Tribe(s.ControlledTribe);
                    if (!tribe.HasValue || tribe.Value == 0)
                        return Domain(markerType, "unavailable", "eligibility-unknown", s, null);
                    if (tribe.Value != ElyrionTribe)
                    { trails.Remove(markerType); return Domain(markerType, "unsupported", "controlled-player-not-eligible", s, null); }
                    if (!s.NativeRainbowVision.HasValue) return Domain(markerType, "unavailable", "eligibility-unknown", s, null);
                    if (!s.NativeRainbowVision.Value)
                    { trails.Remove(markerType); return Domain(markerType, "unsupported", "controlled-player-not-eligible", s, null); }
                }

                List<string> output = new List<string>();
                List<Trail> previous;
                if (!trails.TryGetValue(markerType, out previous)) previous = new List<Trail>();
                List<Trail> next = new List<Trail>();
                HashSet<string> coordsSeen = new HashSet<string>();
                HashSet<object> tileSeen = new HashSet<object>();
                bool unknown = false;
                try
                {
                    foreach (object tile in s.RenderedTiles)
                    {
                        if (bindings.Live(tile) != true) { unknown = true; continue; }
                        object tileIdentity = bindings.Identity(tile);
                        if (tileIdentity == null || !tileSeen.Add(tileIdentity)) { unknown = true; continue; }
                        Read fx = bindings.Fx(tile, markerType);
                        if (!fx.Success) { unknown = true; continue; }
                        if (fx.Value == null) continue; // Proven null field, not a failed lookup.
                        Read controller = bindings.Controller(fx.Value);
                        if (!controller.Success) { unknown = true; continue; }
                        if (controller.Value == null) continue;
                        bool? live = bindings.Live(controller.Value);
                        if (!live.HasValue) { unknown = true; continue; }
                        if (!live.Value) continue; // Unity fake-null is a known dead lease.
                        Read a = bindings.WrapperLease(fx.Value), b = bindings.ControllerLease(controller.Value);
                        int? lease = a.Success ? StrictInt(a.Value) : null, controllerLease = b.Success ? StrictInt(b.Value) : null;
                        if (!lease.HasValue || !controllerLease.HasValue) { unknown = true; continue; }
                        if (lease.Value != controllerLease.Value) continue; // Explicit expired lease.
                        Read usedRead = bindings.Used(controller.Value), playRead = bindings.Playing(controller.Value), stopRead = bindings.Stopping(controller.Value);
                        bool? used = usedRead.Success ? StrictBool(usedRead.Value) : null;
                        bool? playing = playRead.Success ? StrictBool(playRead.Value) : null;
                        bool? stopping = stopRead.Success ? StrictBool(stopRead.Value) : null;
                        if (!used.HasValue || !playing.HasValue || !stopping.HasValue) { unknown = true; continue; }
                        Read go = bindings.GameObject(controller.Value);
                        if (!go.Success || go.Value == null) { unknown = true; continue; }
                        bool? goLive = bindings.Live(go.Value);
                        Read activeRead = bindings.Active(go.Value);
                        bool? active = activeRead.Success ? StrictBool(activeRead.Value) : null;
                        if (!goLive.HasValue || !active.HasValue) { unknown = true; continue; }
                        if (!goLive.Value || !active.Value || !used.Value) continue;
                        object controllerIdentity = bindings.Identity(controller.Value);
                        if (controllerIdentity == null) { unknown = true; continue; }
                        string coords, state;
                        if (playing.Value && !stopping.Value)
                        {
                            Read data = bindings.Data(tile);
                            if (!data.Success || data.Value == null) { unknown = true; continue; }
                            if (markerType == Siege)
                            {
                                Read capturedRead = bindings.Captured(data.Value, s.GameState);
                                bool? captured = capturedRead.Success ? StrictBool(capturedRead.Value) : null;
                                if (!captured.HasValue) { unknown = true; continue; }
                                if (!captured.Value) continue;
                            }
                            else
                            {
                                Read improvement = bindings.Improvement(data.Value);
                                if (!improvement.Success) { unknown = true; continue; }
                                if (improvement.Value == null) continue;
                                Read kind = bindings.ImprovementKind(improvement.Value);
                                int? type = kind.Success ? StrictEnumInt(kind.Value, bindings.improvementType) : null;
                                if (!type.HasValue) { unknown = true; continue; }
                                if (type.Value != RuinImprovement) continue;
                            }
                            coords = bindings.Coords(tile); state = "displayed";
                            if (coords == null) { unknown = true; continue; }
                        }
                        else if (!playing.Value)
                        {
                            bool? alive = bindings.Alive(controller.Value);
                            if (!alive.HasValue) { unknown = true; continue; }
                            if (!alive.Value) continue;
                            Trail known = previous.Find(delegate(Trail t) { return Same(t.TileIdentity, tileIdentity)
                                && Same(t.ControllerIdentity, controllerIdentity) && t.Lease == lease.Value; });
                            // Unknown origin must not become an invented marker, nor an empty successful frame.
                            if (known == null) { unknown = true; continue; }
                            coords = bindings.Coords(tile);
                            if (coords == null || coords != known.Coords) { unknown = true; continue; }
                            state = "fading"; // No new capture/ruin/private-data reads for an already classified tail.
                        }
                        else { unknown = true; continue; }
                        if (!coordsSeen.Add(coords)) { unknown = true; continue; }
                        Json marker = new Json();
                        marker.S("coords", coords); marker.S("displayState", state);
                        marker.I("observedTurn", s.ObservedTurn); marker.L("observationRevision", s.ObservationRevision);
                        output.Add(marker.Close());
                        next.Add(new Trail(tileIdentity, controllerIdentity, lease.Value, coords));
                    }
                }
                catch (Exception) { unknown = true; }
                if (epoch.Version != start.Version || !epoch.IsUsable() || !epoch.Matches(s) || !Current(s))
                    return Domain(markerType, "unavailable", "observation-changed", s, null);
                if (unknown) return Domain(markerType, "partial", "one-or-more-candidates-unknown", s, null);
                trails[markerType] = next; // Only a complete, stable scan may replace history.
                return Domain(markerType, "ok", null, s, output);
            }
        }

        internal static string UnavailableDomain(string markerType, Sample sample, string reason)
        { return Domain(markerType, "unavailable", reason, sample, null); }

        private static string Domain(string markerType, string status, string reasonCode, Sample s, List<string> markers)
        {
            Json j = new Json(); j.S("markerType", markerType); j.S("status", status);
            // Non-ok domains deliberately withhold all coords. Their [] never means disappearance.
            j.Raw("markers", markers == null ? "[]" : "[" + string.Join(",", markers.ToArray()) + "]");
            if (reasonCode != null) j.S("reasonCode", reasonCode);
            j.I("observedTurn", s == null ? null : s.ObservedTurn);
            j.L("observationRevision", s == null ? null : s.ObservationRevision);
            return j.Close();
        }

        private sealed class Json
        {
            private readonly StringBuilder buffer = new StringBuilder("{");
            private bool first = true;
            private void Key(string key) { if (!first) buffer.Append(','); first = false; buffer.Append(Quote(key)).Append(':'); }
            internal void S(string key, string value) { Key(key); buffer.Append(value == null ? "null" : Quote(value)); }
            internal void I(string key, int? value) { Key(key); buffer.Append(value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null"); }
            internal void L(string key, long? value) { Key(key); buffer.Append(value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null"); }
            internal void Raw(string key, string value) { Key(key); buffer.Append(value); }
            internal string Close() { return buffer.Append('}').ToString(); }
            private static string Quote(string value)
            {
                StringBuilder b = new StringBuilder("\"");
                foreach (char c in value)
                {
                    if (c == '"' || c == '\\') b.Append('\\').Append(c);
                    else if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4"));
                    else b.Append(c);
                }
                return b.Append('"').ToString();
            }
        }
    }
}
