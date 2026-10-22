// Production integer reader and native action-stack consumer; no live game.
using System;
using System.Reflection;
using PolytopiaAgent;

public enum CombatScalarEnum { Zero = 0, Two = 2 }
public sealed class CombatScalarStack
{
    public object Count;
    public int Reads;
    public object get_Item(int index) { Reads++; throw new Exception("must not inspect unknown stack"); }
}
public sealed class CombatScalarState { public CombatScalarStack ActionStack; }

public static class CombatScalarTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static int passed;
    private static readonly Type Host = typeof(BridgeMod);
    private static object Read(object value)
    { return Host.GetMethod("CRInt", F).Invoke(null, new[] { value }); }
    private static void Check(bool good, string label)
    { if (!good) throw new Exception("FAIL " + label); passed++; Console.WriteLine("PASS " + label); }
    public static int Main()
    {
        try
        {
            foreach (object value in new object[] { (sbyte)0, (byte)0, (short)0, (ushort)0, 0, 0u, 0L, 0UL })
                Check(object.Equals(Read(value), 0), "real zero " + value.GetType().Name);
            foreach (object value in new object[] { (sbyte)2, (byte)2, (short)2, (ushort)2, 2, 2u, 2L, 2UL })
                Check(object.Equals(Read(value), 2), "real positive " + value.GetType().Name);
            Check(object.Equals(Read(-1), -1), "negative integer sentinel retained");
            Check(object.Equals(Read(Int32.MinValue), Int32.MinValue), "Int32 lower boundary");
            Check(object.Equals(Read((ulong)Int32.MaxValue), Int32.MaxValue), "unsigned Int32 upper boundary");
            foreach (object value in new object[] { null, false, true, "0", "2", 0.4m, 2.6m, 2m,
                    2f, 2d, Double.NaN, Double.PositiveInfinity, CombatScalarEnum.Zero, CombatScalarEnum.Two,
                    (uint)Int32.MaxValue + 1u, (long)Int32.MinValue - 1L, UInt64.MaxValue })
                Check(Read(value) == null, "wrong type or overflow stays unknown " + (value == null ? "null" : value.GetType().Name));
            var stack = new CombatScalarStack();
            var state = new CombatScalarState { ActionStack = stack };
            foreach (object count in new object[] { 0.4m, 2.6m, CombatScalarEnum.Zero, "2", false, -1 })
            {
                stack.Count = count;
                Check(Host.GetMethod("CRStackCount", F).Invoke(null, new object[] { state }) == null,
                    "invalid native Count cannot become known " + count.GetType().Name);
                object[] args = { state, (int?)0, "IncreaseCurrencyAction", null };
                Check(object.Equals(Host.GetMethod("CRTryAppendedActions", F).Invoke(null, args), false)
                    && stack.Reads == 0, "unknown Count cannot associate child actions");
            }
            stack.Count = 0;
            Check(object.Equals(Host.GetMethod("CRStackCount", F).Invoke(null, new object[] { state }), 0),
                "actual empty action stack remains known zero");
            Console.WriteLine("PASS total " + passed); return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }
}
