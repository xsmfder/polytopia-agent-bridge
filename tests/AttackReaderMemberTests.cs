// Exercise the exact production reflection helpers with the actual game member shapes.
using System;
using System.Reflection;

public class UnitState
{
    public bool attacked;
    public bool CanAttack() { return !attacked; }
}
public struct WorldCoordinates
{
    public int x;
    public int y;
}
internal static class AttackReaderMemberTests
{
    private static int passed;
    private static void Check(bool ok, string label)
    {
        if (!ok) { throw new Exception(label); }
        passed++;
        Console.WriteLine("PASS " + label);
    }
    private static int Main(string[] args)
    {
        var assembly = Assembly.LoadFrom(args[0]);
        var type = assembly.GetType("PolytopiaAgent.BridgeMod", true);
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var member = type.GetMethod("ReadBoolMember", flags);
        var getter = type.GetMethod("ReadBoolPropertyOrGetter", flags);
        var scalar = type.GetMethod("SafeScalar", flags);
        var unit = new UnitState();
        Check(object.Equals(member.Invoke(null, new object[] { unit, "attacked" }), false), "real bool field false");
        Check(object.Equals(getter.Invoke(null, new object[] { unit, "CanAttack" }), true), "bare no-arg bool method true");
        unit.attacked = true;
        Check(object.Equals(member.Invoke(null, new object[] { unit, "attacked" }), true), "real bool field true");
        Check(object.Equals(getter.Invoke(null, new object[] { unit, "CanAttack" }), false), "bare no-arg bool method false");
        Check(getter.Invoke(null, new object[] { unit, "HasAttacked" }) == null, "missing member stays unknown");
        Check((string)scalar.Invoke(null, new object[] { new WorldCoordinates { x = -2, y = 3 } }) == "(-2,3)", "WorldCoordinates itself formats canonically");
        Console.WriteLine("passed=" + passed + " failed=0");
        return 0;
    }
}
