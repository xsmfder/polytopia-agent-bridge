using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public class GameState : AlternateValidation.GameState { }
public sealed class ValidCommand
{
    public int Calls;
    public bool IsValid(GameState state, out string error) { Calls++; error = null; return false; }
}
public sealed class OverloadedValidation
{
    public int WrongCalls, Calls;
    public bool IsValid(object state, out int error) { WrongCalls++; error = 0; return true; }
    public bool IsValid(GameState state, out string error) { Calls++; error = null; return false; }
}
public sealed class GenericValidation
{
    public int Calls;
    public bool IsValid<T>(GameState state, out string error) { error = null; throw new Exception("open generic"); }
    public bool IsValid(GameState state, out string error) { Calls++; error = null; return true; }
}
public sealed class RefValidation
{
    public int Calls;
    public bool IsValid(GameState state, ref string error) { Calls++; return true; }
}
public sealed class WrongResultValidation
{
    public int Calls;
    public string IsValid(GameState state, out string error) { Calls++; error = null; return "true"; }
}
public sealed class ThrowValidation
{
    public int Calls;
    public bool IsValid(GameState state, out string error) { Calls++; error = null; throw new Exception("failed read"); }
}
namespace AlternateValidation { public class GameState { } }
public sealed class AmbiguousValidation
{
    public int Calls;
    public bool IsValid(GameState state, out string error) { Calls++; error = null; return true; }
    public bool IsValid(AlternateValidation.GameState state, out string error) { Calls++; error = null; return true; }
}
public sealed class ValidationDictionary
{
    public object Count { get; set; }
    public object Keys { get; set; }
}
public enum ValidationCount { One = 1 }

internal static class UnitCommandValidationTests
{
    private static int passed, failed;
    private static readonly BindingFlags F = BindingFlags.NonPublic | BindingFlags.Static;
    private static object Call(string name, params object[] args)
    { try { return typeof(BridgeMod).GetMethod(name, F).Invoke(null, args); } catch { return "THREW"; } }
    private static void Check(bool condition, string name)
    { if (condition) passed++; else { failed++; Console.WriteLine("FAIL " + name); } }
    public static int Main()
    {
        var gs = new GameState();
        var valid = new ValidCommand();
        Check(object.Equals(Call("URValid", valid, gs), false) && valid.Calls == 1, "native false is retained with one validation");
        var overloaded = new OverloadedValidation();
        Check(object.Equals(Call("URValid", overloaded, gs), false) && overloaded.Calls == 1 && overloaded.WrongCalls == 0,
            "bind exact native validation before invoking any unrelated overload");
        var generic = new GenericValidation();
        Check(object.Equals(Call("URValid", generic, gs), true) && generic.Calls == 1,
            "open generic cannot hide actual native validation");
        var reference = new RefValidation();
        Check(Call("URValid", reference, gs) == null && reference.Calls == 0, "ref input is not native out validation error");
        var wrong = new WrongResultValidation();
        Check(Call("URValid", wrong, gs) == null && wrong.Calls == 0, "wrong return type never invoked");
        var throwing = new ThrowValidation();
        Check(Call("URValid", throwing, gs) == null && throwing.Calls == 1, "throwing validation is unknown and never retried");
        Check(Call("URValid", null, gs) == null, "missing native command is unknown");
        Check(Call("URValid", valid, null) == null && valid.Calls == 1, "missing state cannot invoke validation with null");
        var publicGeneric = new GenericValidation();
        Check(object.Equals(Call("PublicCommandValid", publicGeneric, gs, null), true) && publicGeneric.Calls == 1,
            "diplomacy validation skips open generic exactly like unit validation");
        var publicRef = new RefValidation();
        Check(Call("PublicCommandValid", publicRef, gs, null) == null && publicRef.Calls == 0,
            "diplomacy cannot execute ref-string overload as native out validation");
        var publicFalse = new ValidCommand();
        Check(object.Equals(Call("PublicCommandValid", publicFalse, gs, null), false) && publicFalse.Calls == 1,
            "diplomacy retains native false without retry");
        var publicThrow = new ThrowValidation();
        Check(Call("PublicCommandValid", publicThrow, gs, null) == null && publicThrow.Calls == 1,
            "diplomacy validation exception is sampled once");
        var ambiguous = new AmbiguousValidation();
        Check(Call("PublicCommandValid", ambiguous, gs, null) == null && ambiguous.Calls == 0
            && Call("URValid", ambiguous, gs) == null && ambiguous.Calls == 0,
            "both consumers refuse ambiguous native validation before invoking anything");
        var json = new JavaScriptSerializer();
        foreach (object count in new object[] { true, "1", 1.1m, 1.1d, ValidationCount.One })
        {
            var row = json.Deserialize<Dictionary<string, object>>((string)Call("URUnitDataCatalog",
                new ValidationDictionary { Count = count, Keys = new[] { "Warrior" } }));
            Check((bool)row["ok"] == false && row["count"] == null && row["types"] == null,
                "malformed catalog count cannot prove complete definitions: " + count.GetType().Name);
        }
        var empty = json.Deserialize<Dictionary<string, object>>((string)Call("URUnitDataCatalog",
            new ValidationDictionary { Count = 0, Keys = new string[0] }));
        Check((bool)empty["ok"] && Convert.ToInt32(empty["count"]) == 0, "native empty catalog stays a proven empty catalog");
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
