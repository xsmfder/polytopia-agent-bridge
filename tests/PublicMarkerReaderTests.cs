// Tests compile the production reader source into this executable; all assertions call Collector.Build.
using System;
using System.Collections;
using PolytopiaPublicMarkers;
using Reader = PolytopiaPublicMarkers.PublicMarkerReader;

internal class UnityObject
{
    internal bool destroyed;
    public static bool operator !=(UnityObject a, UnityObject b) { return !(a == b); }
    public static bool operator ==(UnityObject a, UnityObject b)
    {
        bool an = object.ReferenceEquals(a, null) || a.destroyed, bn = object.ReferenceEquals(b, null) || b.destroyed;
        return an && bn || !an && !bn && object.ReferenceEquals(a, b);
    }
    public override bool Equals(object o) { return object.ReferenceEquals(this, o); }
    public override int GetHashCode() { return base.GetHashCode(); }
}
internal class GameObject : UnityObject { public bool activeInHierarchy { get; set; } public GameObject() { activeInHierarchy = true; } }
internal class Component : UnityObject { public GameObject gameObject { get; set; } public Component() { gameObject = new GameObject(); } }
internal class ParticleSystem : UnityObject
{
    internal bool alive, fail;
    internal bool withChildrenSeen;
    public bool IsAlive(bool withChildren) { withChildrenSeen = withChildren; if (fail) throw new InvalidOperationException(); return alive; }
}
internal enum TribeType { None = 0, Nature = 1, Bardur = 4, Elyrion = 5 }
internal enum ImprovementType { None = 0, City = 1, Ruin = 2 }
internal enum UnrelatedEnum { Elyrion = 5 }
internal class GameState { }
internal class ImprovementState { public ImprovementType type; public string reward = "PRIVATE_REWARD"; }
internal class TileData
{
    public ImprovementState improvement;
    internal bool captured;
    internal int calls;
    internal bool throwCapture;
    public string owner = "PRIVATE_OWNER";
    internal GameState expected;
    internal Action duringCapture;
    public bool IsBeingCaptured(GameState state)
    {
        calls++; if (state != expected) throw new Exception("Wrong real state");
        if (duringCapture != null) duringCapture(); if (throwCapture) throw new InvalidOperationException(); return captured;
    }
}
internal struct WorldCoordinates
{
    private int x, y;
    public int X { get { return x; } }
    public int Y { get { return y; } }
    internal WorldCoordinates(int a, int b) { x = a; y = b; }
}
internal class ParticleController : Component
{
    private int poolId = 7;
    public ParticleSystem[] particles;
    public bool IsUsed { get; set; }
    private bool isPlaying = true;
    private bool isStopping;
    internal ParticleController() { IsUsed = true; particles = new ParticleSystem[] { new ParticleSystem() }; }
    internal void Stop(bool stopping) { isPlaying = false; isStopping = stopping; }
    internal void Lease(int value) { poolId = value; }
    internal class ObjectPoolWrapper
    {
        private int poolId = 7;
        private ParticleController pooledParticleController;
        internal ObjectPoolWrapper(ParticleController c) { pooledParticleController = c; }
    }
}
internal class Tile : Component
{
    public TileData Data { get; set; }
    public WorldCoordinates Coordinates { get; set; }
    private ParticleController.ObjectPoolWrapper fireFx;
    private ParticleController.ObjectPoolWrapper rainbowFireFx;
    internal void Fire(ParticleController c) { fireFx = c == null ? null : new ParticleController.ObjectPoolWrapper(c); }
    internal void Rainbow(ParticleController c) { rainbowFireFx = c == null ? null : new ParticleController.ObjectPoolWrapper(c); }
}
// All exact active fields exist except isPlaying: absence must be unknown, never false.
internal class MissingBoolController : Component
{
    private int poolId = 7;
    public bool IsUsed { get { return true; } }
    private bool isStopping;
    public ParticleSystem[] particles = new ParticleSystem[0];
}
internal class MissingBoolWrapper
{
    private int poolId = 7;
    private MissingBoolController pooledParticleController = new MissingBoolController();
}
internal class MissingBoolTile : Component
{
    private MissingBoolWrapper fireFx = new MissingBoolWrapper();
    private MissingBoolWrapper rainbowFireFx;
    public TileData Data { get; set; }
    public WorldCoordinates Coordinates { get { return new WorldCoordinates(91, 92); } }
}
internal class MissingFxTile : Component
{
    public TileData Data { get; set; }
    public WorldCoordinates Coordinates { get { return new WorldCoordinates(91, 92); } }
}

internal static class PublicMarkerReaderTests
{
    private static int passed;
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    private static bool Status(string json, string status) { return json.Contains("\"status\":\"" + status + "\""); }
    private static bool Empty(string json) { return json.Contains("\"markers\":[]") && !json.Contains("\"coords\""); }
    private static Type[] Shapes(Type tile, Type wrapper, Type controller)
    {
        return new Type[] { tile, wrapper, controller, typeof(TileData), typeof(GameState), typeof(WorldCoordinates),
            typeof(ImprovementState), typeof(ImprovementType), typeof(TribeType), typeof(UnityObject), typeof(Component), typeof(GameObject), typeof(ParticleSystem) };
    }
    private static Reader.Bindings Bindings()
    { return Reader.Bindings.CreateForTests(Shapes(typeof(Tile), typeof(ParticleController.ObjectPoolWrapper), typeof(ParticleController)), delegate(object value) { return value; }); }
    private sealed class Fixture
    {
        internal readonly Reader.Collector collector;
        internal readonly GameState state = new GameState();
        internal readonly Tile tile = new Tile();
        internal readonly ParticleController controller = new ParticleController();
        internal object renderer = "renderer-A", rosterId = "roster-A";
        internal IEnumerable tiles;
        internal int controlled = 2, actual = 2, context = 2;
        internal string episode = "episode-A";
        internal object tribe = TribeType.Elyrion;
        internal bool? ability = true;
        internal bool current = true;
        internal Fixture()
        {
            collector = new Reader.Collector(Bindings());
            tile.Data = new TileData(); tile.Data.captured = true; tile.Data.expected = state;
            tile.Data.improvement = new ImprovementState(); tile.Data.improvement.type = ImprovementType.Ruin;
            tile.Coordinates = new WorldCoordinates(8, 13); tile.Fire(controller); tile.Rainbow(controller);
            tiles = new Tile[] { tile };
        }
        internal Reader.Sample Sample()
        { return new Reader.Sample(episode, controlled, actual, context, renderer, rosterId, tiles, 9, 12345678901L, state, tribe, ability, true, delegate { return current; }); }
        internal void Complete()
        { Reader.Sample s = Sample(); long token = collector.NoteRenderStarted(s); if (!collector.NoteRenderCompleted(token, s)) throw new Exception("Completion failed"); }
        internal string Build(string type) { return collector.Build(type, Sample()); }
    }
    public static int Main()
    {
        Fixture f = new Fixture();
        Check(Status(f.Build(Reader.Siege), "unavailable") && Empty(f.Build(Reader.Siege)), "no natural epoch withholds coords");
        f.Complete(); string j = f.Build(Reader.Siege);
        Check(Status(j, "ok") && j.Contains("\"coords\":\"(8,13)\"") && j.Contains("\"displayState\":\"displayed\"")
            && j.Contains("12345678901") && f.tile.Data.calls == 1 && !j.Contains("PRIVATE") && !j.Contains("owner")
            && !j.Contains("reward") && !j.Contains("poolId"), "active production siege uses Data instance method, real state and whitelist");
        f.tile.Data.captured = false; j = f.Build(Reader.Siege);
        Check(Status(j, "ok") && Empty(j), "active noncapture fire is not siege");
        j = f.Build(Reader.RuinRainbow);
        Check(Status(j, "ok") && j.Contains("(8,13)"), "native enum Elyrion and active Ruin rainbow");
        f.tile.Data.improvement.type = ImprovementType.City; j = f.Build(Reader.RuinRainbow);
        Check(Status(j, "ok") && Empty(j), "nonRuin rainbow is not a ruin marker");
        f.tribe = TribeType.Bardur; j = f.Build(Reader.RuinRainbow);
        Check(Status(j, "unsupported") && Empty(j), "verified other tribe unsupported");
        f.tribe = (TribeType)999; j = f.Build(Reader.RuinRainbow);
        f.tribe = UnrelatedEnum.Elyrion; string wrongEnum = f.Build(Reader.RuinRainbow);
        f.tribe = TribeType.Elyrion; f.ability = null; string unknownAbility = f.Build(Reader.RuinRainbow);
        Check(Status(j, "unavailable") && Empty(j) && Status(wrongEnum, "unavailable") && Status(unknownAbility, "unavailable"), "unknown enum, unrelated enum and ability withhold coords");
        f = new Fixture(); f.Complete(); f.rosterId = "roster-B"; j = f.Build(Reader.Siege);
        Check(Status(j, "unavailable") && Empty(j), "replaced actual renderer array invalidates epoch");
        f = new Fixture(); f.Complete(); f.renderer = "renderer-B"; j = f.Build(Reader.Siege);
        f = new Fixture(); f.Complete(); f.episode = "episode-B"; string otherEpisode = f.Build(Reader.Siege);
        f = new Fixture(); f.Complete(); f.context = 3; string wrongContext = f.Build(Reader.Siege);
        Check(Status(j, "unavailable") && Status(otherEpisode, "unavailable") && Status(wrongContext, "unavailable")
            && Empty(j) && Empty(otherEpisode) && Empty(wrongContext), "renderer, episode and real context identities required");
        f = new Fixture(); f.Complete(); f.tiles = null; j = f.Build(Reader.Siege);
        Check(Status(j, "unavailable") && Empty(j), "missing render collection is not successful empty scan");
        f = new Fixture(); f.Complete(); f.controller.Lease(8); j = f.Build(Reader.Siege);
        Check(Status(j, "ok") && Empty(j) && f.tile.Data.calls == 0, "known expired lease is inactive without classification");
        f = new Fixture(); f.Complete(); f.controller.IsUsed = false; j = f.Build(Reader.Siege);
        Check(Status(j, "ok") && Empty(j) && f.tile.Data.calls == 0, "known unused controller is inactive not partial");
        f = new Fixture(); f.Complete(); f.controller.destroyed = true; j = f.Build(Reader.Siege);
        Check(Status(j, "ok") && Empty(j) && f.tile.Data.calls == 0, "Unity fake-null controller cannot display");
        f = new Fixture(); f.Complete(); f.tile.Fire(null); j = f.Build(Reader.Siege);
        Check(Status(j, "ok") && Empty(j), "readable null fx is successful empty");
        Reader.Bindings broken = Reader.Bindings.CreateForTests(Shapes(typeof(MissingBoolTile), typeof(MissingBoolWrapper), typeof(MissingBoolController)), delegate(object value) { return value; });
        Reader.Collector brokenCollector = new Reader.Collector(broken);
        Reader.Sample brokenSample = new Reader.Sample("a", 2, 2, 2, "r", "a", new MissingBoolTile[] { new MissingBoolTile() }, 1, 1, new GameState(), TribeType.Elyrion, true, true, delegate { return true; });
        long bt = brokenCollector.NoteRenderStarted(brokenSample); brokenCollector.NoteRenderCompleted(bt, brokenSample); j = brokenCollector.Build(Reader.Siege, brokenSample);
        Check(Status(j, "partial") && Empty(j), "missing exact boolean member is unknown not false");
        Reader.Collector missingFx = new Reader.Collector(Reader.Bindings.CreateForTests(Shapes(typeof(MissingFxTile), typeof(ParticleController.ObjectPoolWrapper), typeof(ParticleController)), delegate(object value) { return value; }));
        Reader.Sample missingFxSample = new Reader.Sample("a", 2, 2, 2, "r", "a", new MissingFxTile[] { new MissingFxTile() }, 1, 1, new GameState(), TribeType.Elyrion, true, true, delegate { return true; });
        long mf = missingFx.NoteRenderStarted(missingFxSample); missingFx.NoteRenderCompleted(mf, missingFxSample); j = missingFx.Build(Reader.Siege, missingFxSample);
        Check(Status(j, "partial") && Empty(j), "missing native fx field differs from proven null field");
        f = new Fixture(); f.Complete(); f.Build(Reader.Siege); f.controller.Stop(true); f.controller.particles[0].alive = true;
        f.tile.Data.captured = false; int calls = f.tile.Data.calls; j = f.Build(Reader.Siege);
        Check(Status(j, "ok") && j.Contains("\"displayState\":\"fading\"") && f.controller.particles[0].withChildrenSeen
            && f.tile.Data.calls == calls, "same player and lease known trail fades with IsAlive(true), without new private classification");
        f.controller.particles[0].fail = true; j = f.Build(Reader.Siege);
        Check(Status(j, "partial") && Empty(j), "unknown stopped-particle getter withholds entire domain");
        f = new Fixture(); f.Complete(); f.Build(Reader.Siege); f.controlled = f.actual = f.context = 3; j = f.Build(Reader.Siege);
        f.Complete(); f.controller.Stop(true); f.controller.particles[0].alive = true; string afterSwitch = f.Build(Reader.Siege);
        Check(Status(j, "unavailable") && Empty(j) && Status(afterSwitch, "partial") && Empty(afterSwitch), "player change clears history, unknown stop origin cannot report empty ok");
        f = new Fixture(); f.Complete(); f.tile.Data.duringCapture = delegate { f.collector.NoteRenderStarted(f.Sample()); }; j = f.Build(Reader.Siege);
        Check(Status(j, "unavailable") && Empty(j), "epoch changes during production sampling withhold built coords");
        f = new Fixture(); f.Complete(); f.tile.Data.duringCapture = delegate { f.current = false; }; j = f.Build(Reader.Siege);
        Check(Status(j, "unavailable") && Empty(j), "state/revision mutation callback withholds built coords");
        f = new Fixture(); Reader.Sample s1 = f.Sample(); long t1 = f.collector.NoteRenderStarted(s1); long t2 = f.collector.NoteRenderStarted(s1);
        bool stale = f.collector.NoteRenderCompleted(t1, s1);
        Check(!stale && !f.collector.CurrentEpoch().IsUsable(), "stale render completion cannot bless newer prefix");
        Reader.Bindings nativeFake = Reader.Bindings.CreateNative(delegate(string name) { return typeof(Tile); }, delegate(object value) { return value; });
        Check(nativeFake == null, "production native factory rejects fake assembly/type names");
        f = new Fixture(); f.Complete(); f.controller.Stop(false); f.controller.particles[0].alive = false; j = f.Build(Reader.Siege);
        Check(Status(j, "ok") && Empty(j), "known stopped particles dead allows complete empty scan");
        f = new Fixture(); f.Complete(); f.tile.Data.throwCapture = true; j = f.Build(Reader.Siege);
        Check(Status(j, "partial") && Empty(j), "pure category method exception is unknown");
        f = new Fixture(); f.Complete(); Tile unknownTile = new Tile(); unknownTile.Coordinates = new WorldCoordinates(3, 5);
        unknownTile.Fire(new ParticleController()); unknownTile.Data = new TileData(); unknownTile.Data.expected = f.state; unknownTile.Data.throwCapture = true;
        f.tiles = new Tile[] { f.tile, unknownTile }; j = f.Build(Reader.Siege);
        Check(Status(j, "partial") && Empty(j) && !j.Contains("(8,13)"), "one unknown candidate withholds other successfully collected coords");
        f = new Fixture(); f.rosterId = null; Reader.Sample noRosterIdentity = f.Sample();
        long noIdentityToken = f.collector.NoteRenderStarted(noRosterIdentity);
        Check(!f.collector.NoteRenderCompleted(noIdentityToken, noRosterIdentity) && !f.collector.CurrentEpoch().IsUsable(), "null native array identity cannot complete render epoch");
        f = new Fixture(); f.rosterId = null; f.tiles = null; Reader.Sample beforeRenderMap = f.Sample();
        long renderMapToken = f.collector.NoteRenderStarted(beforeRenderMap); f.rosterId = "new-real-renderedTiles"; f.tiles = new Tile[] { f.tile };
        bool mapCompleted = f.collector.NoteRenderCompleted(renderMapToken, f.Sample()); j = f.Build(Reader.Siege);
        Check(mapCompleted && Status(j, "ok") && j.Contains("(8,13)"), "RenderMap naturally creates roster between genuine prefix and postfix");
        Check(Reader.Bindings.NativeTypeNameMatches("Polytopia.Data.ImprovementData+Type", "Il2CppPolytopia.Data.ImprovementData+Type")
            && Reader.Bindings.NativeTypeNameMatches("PolytopiaBackendBase.Common.TribeType", "Il2CppPolytopiaBackendBase.Common.TribeType")
            && !Reader.Bindings.NativeTypeNameMatches("PolytopiaBackendBase.Common.TribeType", "Other.Common.TribeType")
            && !Reader.Bindings.NativeTypeNameMatches("Polytopia.Data.ImprovementData+Type", "Il2Cpp.Polytopia.Data.ImprovementData+Type"), "exact Cecil-verified generated enum aliases accepted without suffix matching");
        Reader.Sample zeroRevision = new Reader.Sample(f.episode, f.controlled, f.actual, f.context, f.renderer, f.rosterId, f.tiles,
            9, 0, f.state, f.tribe, true, true, delegate { return true; });
        j = f.collector.Build(Reader.Siege, zeroRevision);
        Check(Status(j, "unavailable") && Empty(j), "revision zero rejected consistently with production normalizer");
        Console.WriteLine("TOTAL " + passed + " passed"); return 0;
    }
}
