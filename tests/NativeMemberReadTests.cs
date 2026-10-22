// Exercise the real public-data consumers, preserving unknown vs native null/zero.
using System;
using System.Reflection;
using PolytopiaAgent;

public sealed class FlakyNativeValue
{
    public int Reads;
    public object Value;
    public object health { get { if (++Reads == 1) throw new InvalidOperationException("unreadable"); return Value; } }
}
public sealed class StableNativeValue
{
    public int Reads;
    public object Value;
    public object health { get { Reads++; return Value; } }
}
public sealed class NativeValueFields { public int health = 0; }
public sealed class LowercaseNativeField { public int health = 9; }
public sealed class FlakyCaseNativeValue
{
    public int Reads;
    public int health = 9;
    public int get_Health() { Reads++; throw new Exception("unreadable primary"); }
}
public sealed class TechData
{
    public enum Type { Basic, Future_NativeTech }
    public Type type = Type.Future_NativeTech;
}
public sealed class FlakyTechData
{
    public int Reads;
    public TechData.Type type { get { if (++Reads == 1) throw new Exception("unreadable type"); return TechData.Type.Future_NativeTech; } }
}

internal static class NativeMemberReadTests
{
    private const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
    private static int passed, failed;
    private static void Check(bool ok, string label)
    { if (ok) { passed++; Console.WriteLine("PASS " + label); } else { failed++; Console.WriteLine("FAIL " + label); } }
    private static object Read(string consumer, object target)
    {
        Type type = consumer == "city" ? typeof(PolytopiaCityOps.CityOptionsSnapshot)
            : consumer == "preview" ? typeof(PolytopiaBattlePreview.BattlePreviewSnapshot) : typeof(BridgeMod);
        string name = consumer == "unit" ? "URMember" : consumer == "panel" ? "URPanelMember"
            : consumer == "world" ? "MemberWithAccessor" : "M";
        var args = consumer == "panel" ? new object[] { target, "health", null } : new[] { target, (object)"health" };
        var result = type.GetMethod(name, F).Invoke(null, args);
        return consumer == "panel" ? new object[] { result, args[2] } : result;
    }
    private static bool Unknown(string consumer, object result)
    { return consumer == "panel" ? object.Equals(((object[])result)[0], false) : result == null; }
    private static bool Known(string consumer, object result, object wanted)
    { return consumer == "panel" ? object.Equals(((object[])result)[0], true) && object.Equals(((object[])result)[1], wanted) : object.Equals(result, wanted); }
    public static int Main()
    {
        foreach (string consumer in new[] { "city", "preview", "unit", "panel", "world" })
        {
            foreach (object value in new object[] { 0, null })
            {
                var flaky = new FlakyNativeValue { Value = value };
                Check(Unknown(consumer, Read(consumer, flaky)) && flaky.Reads == 1,
                    consumer + " failed getter is not retried into a known " + (value == null ? "null" : "zero"));
                Check(Known(consumer, Read(consumer, flaky), value) && flaky.Reads == 2,
                    consumer + " next explicit query reads the current native value");
            }
            var stable = new StableNativeValue { Value = 0 };
            Check(Known(consumer, Read(consumer, stable), 0) && stable.Reads == 1, consumer + " actual zero is read once and retained");
            stable.Value = 7;
            Check(Known(consumer, Read(consumer, stable), 7) && stable.Reads == 2, consumer + " changed native value is not cached");
            var empty = new StableNativeValue { Value = null };
            Check(Known(consumer, Read(consumer, empty), null) && empty.Reads == 1, consumer + " known null is not a getter failure");
            Check(Known(consumer, Read(consumer, new NativeValueFields()), 0), consumer + " native field zero remains supported");
        }
        string why;
        Check(BridgeMod.TechEnumNameOf(new TechData(), out why) == "Future_NativeTech",
            "native research data field uses the shared member adapter and actual enum type");
        var tech = new FlakyTechData();
        Check(BridgeMod.TechEnumNameOf(tech, out why) == null && tech.Reads == 1,
            "failed research type getter cannot become a known candidate by retry");
        Check(BridgeMod.TechEnumNameOf(tech, out why) == "Future_NativeTech" && tech.Reads == 2,
            "next explicit research name read can recover without cached values");
        var accessor = typeof(BridgeMod).GetMethod("MemberWithAccessor", F);
        Check(object.Equals(accessor.Invoke(null, new object[] { new LowercaseNativeField(), "Health" }), 9),
            "formal reader resolves lowercase metadata when uppercase member is absent");
        var casing = new FlakyCaseNativeValue();
        Check(accessor.Invoke(null, new object[] { casing, "Health" }) == null && casing.Reads == 1,
            "formal reader never tries a lowercase field after the selected getter fails");
        BridgeMod.ResetReflectionCacheForTests();
        object ignored;
        var fields = new NativeValueFields();
        for (int i = 0; i < BridgeMod.MaxReflectionPlans + 20; i++)
            BridgeMod.TryReadNativeMember(fields, "absent_" + i, out ignored);
        Check(BridgeMod.ReflectionPlanCount <= BridgeMod.MaxReflectionPlans,
            "strict reader miss metadata respects the shared cache capacity");
        Check(BridgeMod.TryReadNativeMember(fields, "health", out ignored) && object.Equals(ignored, 0),
            "a full cache does not prevent reading an uncached real native field");
        fields.health = 8;
        Check(BridgeMod.TryReadNativeMember(fields, "health", out ignored) && object.Equals(ignored, 8),
            "uncached native fields still read changed values after capacity is reached");
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
