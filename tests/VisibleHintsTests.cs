// Exercise public-hint production readers without a running game or private-map fixtures.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public sealed class GameState { }
public sealed class WorldCoordinates { }
public enum SkinType { Default, Alternate }
public sealed class Tile
{
    public object Data;
    public SkinType Skin;
    public int Calls;
    public bool Throw;
    public SkinType GetVisualSkinTypeForTile()
    { Calls++; if (Throw) throw new Exception("unavailable skin"); return Skin; }
}
public sealed class MapRenderer
{
    public static MapRenderer Current { get; set; }
    public Tile Tile;
    public int Calls;
    public Tile GetTileInstance(WorldCoordinates coordinate) { Calls++; return Tile; }
}
internal static class VisibleHintsTests
{
    private sealed class Unit
    {
        public bool Present, Throw;
        public int Calls;
        public bool IsDetectingHiddenUnits(GameState state)
        { Calls++; if (Throw) throw new Exception("unavailable hint"); return Present; }
        // Private enemy data must never be consulted by this public indicator.
        public object Neighbors { get { throw new Exception("hidden neighbors read"); } }
    }
    private sealed class WrongUnit
    {
        public bool IsDetectingHiddenUnits(object state) { throw new Exception("wrong overload"); }
    }
    private sealed class TileData
    {
        public WorldCoordinates coordinates = new WorldCoordinates();
        public string climate = "Imperius";
        public object owner { get { throw new Exception("style must not inspect owner"); } }
    }
    private sealed class HiddenTile
    {
        public int Reads;
        public object coordinates { get { Reads++; throw new Exception("unexplored coordinates"); } }
        public object climate { get { Reads++; throw new Exception("unexplored climate"); } }
    }
    private static int passed;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static Dictionary<string, object> Parse(string value)
    { return Json.Deserialize<Dictionary<string, object>>(value); }
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    public static int Main()
    {
        var state = new GameState(); var unit = new Unit { Present = true };
        var value = Parse(BridgeMod.AdjacentInvisibleHint(unit, state, true));
        Check((bool)value["present"] && (string)value["status"] == "ok", "own native hint true is returned");
        Check(value.Count == 3 && !value.ContainsKey("coordinates") && !value.ContainsKey("owner"), "hint contains only presence and provenance");
        unit.Present = false;
        Check(!(bool)Parse(BridgeMod.AdjacentInvisibleHint(unit, state, true))["present"] && unit.Calls == 2,
              "fresh false replaces prior true without a value cache");
        value = Parse(BridgeMod.AdjacentInvisibleHint(unit, state, false));
        Check(value["present"] == null && (string)value["status"] == "withheld" && unit.Calls == 2,
              "enemy hint withheld without native invocation");
        unit.Throw = true;
        value = Parse(BridgeMod.AdjacentInvisibleHint(unit, state, true));
        Check(value["present"] == null && (string)value["status"] == "unavailable", "throwing native hint remains unknown");
        Check(Parse(BridgeMod.AdjacentInvisibleHint(new WrongUnit(), state, true))["present"] == null, "wrong native signature does not become false");
        Check(Parse(BridgeMod.AdjacentInvisibleHint(unit, null, true))["present"] == null && unit.Calls == 3, "missing game state does not invoke native hint");

        var data = new TileData(); var tile = new Tile { Data = data, Skin = SkinType.Alternate };
        var renderer = new MapRenderer { Tile = tile }; MapRenderer.Current = renderer;
        var factory = typeof(BridgeMod).GetMethod("CreateVisibleStyleContext", BindingFlags.NonPublic | BindingFlags.Static);
        var context = (BridgeMod.VisibleStyleContext)factory.Invoke(null, new object[] { state });
        value = Parse(context.Read(data, true));
        Check((string)value["tribe"] == "Imperius" && (string)value["skin"] == "Alternate" && (string)value["status"] == "ok",
              "actual renderer supplies style independently of owner");
        var hidden = new HiddenTile(); int calls = renderer.Calls;
        Check(context.Read(hidden, false) == "null" && hidden.Reads == 0 && renderer.Calls == calls,
              "unexplored tile stops before all reflection and renderer access");
        data.climate = "Elyrion"; tile.Skin = SkinType.Default;
        value = Parse(context.Read(data, true));
        Check((string)value["tribe"] == "Elyrion" && (string)value["skin"] == "Default" && tile.Calls == 2,
              "new renderer skin and climate are sampled on every read");
        tile.Data = new TileData(); calls = tile.Calls;
        value = Parse(context.Read(data, true));
        Check(value["tribe"] == null && value["skin"] == null && tile.Calls == calls,
              "mismatched rendered tile is not projected");
        tile.Data = data; tile.Throw = true;
        value = Parse(context.Read(data, true));
        Check(value["tribe"] == null && value["skin"] == null && (string)value["status"] == "unavailable",
              "native style failure remains unknown without raw-skin fallback");
        MapRenderer.Current = null;
        context = (BridgeMod.VisibleStyleContext)factory.Invoke(null, new object[] { state });
        Check(Parse(context.Read(data, true))["skin"] == null, "missing renderer does not invent a style");
        MapRenderer.Current = renderer;
        context = (BridgeMod.VisibleStyleContext)factory.Invoke(null, new object[] { null });
        Check(Parse(context.Read(data, true))["tribe"] == null, "missing state prevents renderer projection");
        Console.WriteLine("PASS total " + passed); return 0;
    }
}
