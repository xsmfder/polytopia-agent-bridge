using System;
using System.Collections.Generic;
using System.Reflection;
using PolytopiaAgent;

public sealed class MapData { public int Mode; }
public sealed class TileData
{
    public string Value;
    public string coordinates { get { if (Value == "throw") throw new Exception("coordinate unavailable"); return Value; } }
}
public sealed class OtherTile { }
// Same name/arity in an unrelated owner must never become the fog authority.
public static class DecoyVisibleRules
{
    public static int Calls;
    public static void GetPlayerVisibleTiles(MapData map, byte player, List<TileData> result)
    { Calls++; result.Add(new TileData { Value = "(99,99)" }); }
}
public static class MapDataExtensions
{
    public static int Calls, WrongCalls;
    public static void GetPlayerVisibleTiles(MapData map, byte player, List<TileData> result)
    {
        Calls++;
        if (map.Mode == 4) throw new Exception("SECRET_FOG_LOCATION_(99,99)");
        if (map.Mode == 1) return;
        result.Add(new TileData { Value = "(1,1)" });
        result.Add(map.Mode == 2 ? null : new TileData {
            Value = map.Mode == 3 ? "throw" : map.Mode == 5 ? "(1,1)" : map.Mode == 6 ? null : "(2,1)" });
    }
    public static void GetPlayerVisibleTiles(MapData map, int player, List<TileData> result) { WrongCalls++; }
    public static void GetPlayerVisibleTiles<T>(MapData map, byte player, List<TileData> result) { WrongCalls++; }
    public static void GetPlayerVisibleTiles(MapData map, byte player, List<OtherTile> result) { WrongCalls++; }
}
internal static class VisibleTileSourceTests
{
    private static int passed, failed;
    private static void Check(bool ok, string label)
    { Console.WriteLine((ok ? "PASS " : "FAIL ") + label); if (ok) passed++; else failed++; }
    public static int Main()
    {
        var bridge = new BridgeMod();
        var method = typeof(BridgeMod).GetMethod("GameVisibleTiles", BindingFlags.Instance | BindingFlags.NonPublic);
        for (int mode = 0; mode <= 6; mode++)
        {
            int before = MapDataExtensions.Calls;
            object[] args = { new MapData { Mode = mode }, (byte)2, null };
            var result = (List<string>)method.Invoke(bridge, args);
            Check(mode == 0 ? result != null && result.Count == 2 && result[1] == "(2,1)"
                : mode == 1 ? result != null && result.Count == 0 : result == null,
                "mode " + mode + " distinguishes complete, empty, null slot, bad getter, throw, duplicate and null coordinate");
            Check(MapDataExtensions.Calls == before + 1 && MapDataExtensions.WrongCalls == 0 && DecoyVisibleRules.Calls == 0,
                "mode " + mode + " invokes only the exact approved native provider once");
            if (mode == 4) Check(args[2] != null && !args[2].ToString().Contains("SECRET_FOG_LOCATION"),
                "native exception payload never becomes a public fog error");
        }
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
