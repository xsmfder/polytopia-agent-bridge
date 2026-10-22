using System;
using System.Globalization;
using System.Reflection;
using PolytopiaAgent;

internal static class ValueSemanticsTests
{
    private enum NumericLooking { Zero, One }
    private static int passed;
    private static void Check(bool value, string label)
    {
        if (!value) { Console.WriteLine("FAIL " + label); throw new Exception("FAIL " + label); }
        passed++;
        Console.WriteLine("PASS " + label);
    }
    private static string UnitNumber(object value)
    {
        var output = new JsonObj();
        typeof(BridgeMod).GetMethod("URNumber", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { output, "value", value });
        return output.Close();
    }
    public static int Main()
    {
        Check(BridgeMod.ResultsNumber(9007199254740993L) == "9007199254740993", "result integers retain precision beyond double's exact range");
        Check(BridgeMod.ResultsNumber(ulong.MaxValue) == "18446744073709551615", "unsigned native scalar retains its full range");
        Check(BridgeMod.ResultsNumber(0.1234567890123456789012345678m) == "0.1234567890123456789012345678", "decimal value is not rounded through double");
        Check(BridgeMod.ResultsNumber(0.1f) == "0.1", "float emits its own round-trip representation rather than widened binary noise");
        Check(BridgeMod.ResultsNumber(78.75) == "78.75", "rating percent-points retain fractional values");
        Check(BridgeMod.ResultsNumber(0) == "0" && BridgeMod.ResultsNumber(0f) == "0", "actual zero scores are numeric zero");
        Check(BridgeMod.ResultsNumber(-1) == "-1", "raw negative value is preserved without an invented global sentinel rule");
        foreach (object value in new object[] { null, true, false, "0", '0', NumericLooking.Zero, DateTime.MinValue, new object() })
            Check(BridgeMod.ResultsNumber(value) == "null", "non-numeric score type is unknown: " + (value == null ? "null" : value.GetType().Name));
        foreach (object value in new object[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Check(BridgeMod.ResultsNumber(value) == "null", "non-finite score cannot become invalid JSON: " + value.GetType().Name);
        Check(UnitNumber(0) == "{\"value\":0}", "real zero native unit attribute remains readable zero");
        Check(UnitNumber((ushort)65535) == "{\"value\":65535}", "native ushort HP remains raw and unscaled");
        Check(UnitNumber(-1) == "{\"value\":-1}", "raw integral linkage sentinel is preserved");
        Check(UnitNumber(ulong.MaxValue) == "{\"value\":18446744073709551615}", "integral unit reader does not overflow through signed Int64");
        foreach (object value in new object[] { null, false, true, "0", "150", '0', NumericLooking.One, 0.5f, 1.5, 2m, double.NaN })
            Check(UnitNumber(value) == "{\"value\":null}", "unreadable or wrong-type unit value stays unknown: " + (value == null ? "null" : value.GetType().Name));
        var hp = typeof(BridgeMod).GetMethod("URHPDisplayValue", BindingFlags.Static | BindingFlags.NonPublic);
        foreach (object value in new object[] { NumericLooking.Zero, NumericLooking.One })
            Check(hp.Invoke(null, new object[] { value, true }) == null,
                "enum value cannot become displayed HP: " + value);
        var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Check(BridgeMod.ResultsNumber(1.5f) == "1.5" && BridgeMod.ResultsNumber(1.5m) == "1.5", "numeric JSON remains invariant under comma-decimal culture");
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = previous; }
        Console.WriteLine("passed=" + passed);
        return 0;
    }
}
