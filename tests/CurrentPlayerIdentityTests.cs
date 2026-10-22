// Exercise the real current-seat reader: unreadable native indices/IDs must not
// silently select player one, and a null slot must not shift subsequent indices.
using System;
using System.Collections;
using System.Reflection;
using PolytopiaAgent;

public sealed class IdentityState
{
    public object Index, Players;
    public bool ThrowIndex;
    public object CurrentPlayerIndex { get { if (ThrowIndex) throw new Exception("index unreadable"); return Index; } }
    public object PlayerStates { get { return Players; } }
}
public sealed class IdentityPlayer { public object Id; }
public enum InvalidIdentityNumber { One = 1 }

internal static class CurrentPlayerIdentityTests
{
    private static int passed, failed;
    private static readonly BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BridgeMod Host = new BridgeMod();
    private static readonly MethodInfo Read = typeof(BridgeMod).GetMethod("CurrentPlayerId", F);
    private static void Check(bool condition, string name)
    { if (condition) passed++; else { failed++; Console.WriteLine("FAIL " + name); } }
    private static byte Seat(object index, params object[] players)
    { return (byte)Read.Invoke(Host, new object[] { new IdentityState { Index = index, Players = players } }); }

    public static int Main()
    {
        typeof(BridgeMod).GetField("_playersProp", F).SetValue(Host, typeof(IdentityState).GetProperty("PlayerStates"));
        typeof(BridgeMod).GetField("_currentPlayerIndexProp", F).SetValue(Host, typeof(IdentityState).GetProperty("CurrentPlayerIndex"));
        var first = new IdentityPlayer { Id = (byte)1 };
        var second = new IdentityPlayer { Id = 7 };
        Check(Seat(0, first, second) == 1, "native index zero resolves actual first ID");
        Check(Seat((byte)1, first, second) == 7, "native second index resolves ID, not index");
        foreach (var index in new object[] { null, true, false, "0", "1", 0.4, 1.0m, InvalidIdentityNumber.One, -1, (long)int.MaxValue + 1 })
            Check(Seat(index, first, second) == 0, "invalid index cannot select any player: " + (index == null ? "null" : index.GetType().Name + " " + index));
        Check(Seat(2, first, second) == 0, "out of range index is unknown");
        Check(Seat(1, null, second) == 7, "null first slot does not shift later native index");
        Check(Seat(0, null, second) == 0, "null selected slot cannot select next player");
        foreach (var id in new object[] { null, true, false, "7", 7.0, 7.0m, InvalidIdentityNumber.One, -1, 0, 256, (long)int.MaxValue + 1 })
            Check(Seat(1, first, new IdentityPlayer { Id = id }) == 0, "invalid ID cannot fall back to player index: " + (id == null ? "null" : id.GetType().Name + " " + id));
        foreach (var id in new object[] { (byte)254, (ushort)254, 254, 254L })
            Check(Seat(1, first, new IdentityPlayer { Id = id }) == 254, "native integral ID remains compatible: " + id.GetType().Name);
        Check(Seat(1, first, new IdentityPlayer { Id = (byte)255 }) == 255, "native Nature ID remains distinct from unknown and is never a controlled seat");
        var state = new IdentityState { ThrowIndex = true, Players = new[] { first } };
        Check((byte)Read.Invoke(Host, new object[] { state }) == 0, "failed index getter cannot select first player");
        state = new IdentityState { Index = 0, Players = null };
        Check((byte)Read.Invoke(Host, new object[] { state }) == 0, "unreadable player collection is unknown");
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
