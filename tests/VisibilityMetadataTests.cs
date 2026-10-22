using System;
using System.Reflection;
using System.Threading;

internal static class VisibilityMetadataTests
{
    public sealed class TestGameState { public bool Hidden; public object Resource, Unit; }
    public class Tile
    {
        public int UnitCalls, ResourceCalls; public byte LastPlayer; public bool LastIncludeHidden;
        public object unit = new object(), resource = new object();
        public object GetUnit(TestGameState state, byte player, bool includeHidden)
        { UnitCalls++; LastPlayer = player; LastIncludeHidden = includeHidden; return state.Hidden ? null : state.Unit; }
        public object GetResource(TestGameState state, byte player)
        { ResourceCalls++; LastPlayer = player; return state.Hidden ? null : state.Resource; }
    }
    public sealed class ThrowTile
    {
        public object unit = new object(), resource = new object();
        public object GetUnit(TestGameState state, byte player, bool hidden) { throw new Exception(); }
        public object GetResource(TestGameState state, byte player) { throw new Exception(); }
    }
    public sealed class WrongTile
    {
        public object unit = new object(), resource = new object();
        public object GetUnit(TestGameState state, int player, bool hidden) { throw new Exception(); }
        public object GetResource(object state, byte player) { throw new Exception(); }
    }
    public sealed class OtherTile : Tile { }
    public sealed class ConcurrentTile : Tile { }
    private static int passed;
    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static MethodInfo unit, resource, finder;
    private static object Read(MethodInfo method, object tile, object state, byte player, out bool filtered)
    {
        var args = new object[] { tile, state, player, false };
        object result = method.Invoke(null, args); filtered = (bool)args[3]; return result;
    }
    public static int Main(string[] args)
    {
        var type = Assembly.LoadFrom(args[0]).GetType("PolytopiaAgent.BridgeMod", true);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        unit = type.GetMethod("TileUnitVisible", flags); resource = type.GetMethod("TileResourceVisible", flags);
        finder = type.GetMethod("FindTileVisibilityMethod", flags);
        var builds = type.GetField("_visibilityMethodBuilds", flags);
        var tile = new Tile(); var state = new TestGameState { Unit = new object(), Resource = new object() }; bool filtered;
        Check(Object.ReferenceEquals(Read(unit, tile, state, 2, out filtered), state.Unit) && filtered, "native unit filter executes");
        Check(!tile.LastIncludeHidden && tile.LastPlayer == 2, "includeHidden=false and current seat are preserved");
        Check(Object.ReferenceEquals(Read(resource, tile, state, 2, out filtered), state.Resource) && filtered, "native resource filter executes");
        long initial = (long)builds.GetValue(null);
        state.Hidden = true;
        Check(Read(unit, tile, state, 2, out filtered) == null && filtered, "cached metadata does not retain visible unit");
        Check(Read(resource, tile, state, 2, out filtered) == null && filtered, "cached metadata does not retain revealed resource");
        state.Hidden = false; state.Resource = new object(); state.Unit = new object();
        Check(Object.ReferenceEquals(Read(unit, tile, state, 1, out filtered), state.Unit) && tile.LastPlayer == 1, "updated unit and player queried each time");
        Check(Object.ReferenceEquals(Read(resource, tile, state, 1, out filtered), state.Resource), "new tech resource is disclosed immediately by native filter");
        Check((long)builds.GetValue(null) == initial && tile.UnitCalls == 3 && tile.ResourceCalls == 3, "warm hits skip discovery but never native visibility invocation");
        Check(Read(unit, new ThrowTile(), state, 2, out filtered) == null && !filtered, "throwing unit filter withholds raw unit");
        Check(Read(resource, new ThrowTile(), state, 2, out filtered) == null && !filtered, "throwing resource filter withholds raw resource");
        Check(Read(unit, new WrongTile(), state, 2, out filtered) == null && !filtered, "wrong unit overload fails closed");
        Check(Read(resource, new WrongTile(), state, 2, out filtered) == null && !filtered, "wrong resource overload fails closed");
        long missing = (long)builds.GetValue(null);
        Read(unit, new WrongTile(), state, 2, out filtered); Read(resource, new WrongTile(), state, 2, out filtered);
        Check((long)builds.GetValue(null) == missing, "missing metadata is cached independently for units and resources");
        Check(Read(unit, null, state, 2, out filtered) == null && !filtered, "null tile stays unknown");
        Check(Object.ReferenceEquals(Read(unit, new OtherTile(), state, 2, out filtered), state.Unit), "inherited native overload is retained");
        long before = (long)builds.GetValue(null); var threads = new Thread[8]; Exception failure = null;
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(() => { try { for (int j = 0; j < 1000; j++) finder.Invoke(null, new object[] { typeof(ConcurrentTile), true }); } catch (Exception e) { failure = e; } });
            threads[i].Start();
        }
        foreach (var thread in threads) thread.Join();
        Check(failure == null && (long)builds.GetValue(null) == before + 1, "concurrent cold discovery builds one immutable plan");
        Console.WriteLine("passed=" + passed + " failed=0"); return 0;
    }
}
