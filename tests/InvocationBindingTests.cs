using System;
using System.Reflection;
using PolytopiaAgent;

internal static class InvocationBindingTests
{
    private static int passed, failed;
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
    private static object Call(string name, params object[] args)
    {
        try { return typeof(BridgeMod).GetMethod(name, Flags).Invoke(null, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static void Check(bool ok, string label)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + label);
        if (ok) passed++; else failed++;
    }
    private static void Scenario(string label, Action action)
    {
        try { action(); }
        catch (Exception ex) { Check(false, label + " threw " + ex.GetType().Name); }
    }
    private sealed class BoolFields { public object moved; }
    private sealed class BoolMethods
    {
        public bool CanMove() { return true; }
        public bool CanMove(int ignored) { throw new Exception("wrong overload"); }
    }
    private sealed class NumberMethod { public int CanMove() { return 0; } }
    private sealed class StringMethod { public string CanMove() { return "false"; } }
    private sealed class ThrowingBool { public bool CanMove() { throw new Exception("unreadable"); } }
    private sealed class GenericBool { public bool CanMove<T>() { throw new Exception("unbound"); } }
    private sealed class LowerBool { public bool canMove() { return false; } }
    private sealed class InstanceOverloads
    {
        public int Calls;
        public string SetData(object value) { Calls++; throw new Exception("effect then failure"); }
        public string SetData(string value) { Calls++; return "string"; }
    }
    private sealed class InstanceUnique
    {
        public int Calls;
        public string Show(string value) { Calls++; return value; }
        public string Hide() { Calls++; throw new Exception("effect then failure"); }
    }
    private static class StaticOverloads
    {
        public static int Calls;
        public static string Select(object value) { Calls++; return "object"; }
        public static string Select(string value) { Calls++; return "string"; }
        public static int Value(int value) { Calls++; return value; }
        public static string Ref(string value) { Calls++; return value == null ? "null" : value; }
        public static int? Nullable(int? value) { Calls++; return value; }
        public static string Generic<T>(string value) { Calls++; return value; }
        public static string Generic(int value) { Calls++; return "int"; }
    }
    private static int Main()
    {
        Scenario("strict boolean members", delegate
        {
            var fields = new BoolFields();
            foreach (var value in new object[] { 0, 1, "false", "true", 0m })
            {
                fields.moved = value;
                Check(Call("ReadBoolMember", fields, "moved") == null,
                    "non-boolean member remains unknown: " + value.GetType().Name + " " + value);
            }
            fields.moved = true;
            Check(object.Equals(Call("ReadBoolMember", fields, "moved"), true), "real true member retained");
            fields.moved = false;
            Check(object.Equals(Call("ReadBoolMember", fields, "moved"), false), "real false member retained");
        });
        Scenario("boolean overload selection", delegate
        {
            Check(object.Equals(Call("ReadBoolInvoke", new BoolMethods(), "CanMove"), true),
                "no-argument bool method survives parameterized overload");
            Check(Call("ReadBoolInvoke", new NumberMethod(), "CanMove") == null, "numeric return remains unknown");
            Check(Call("ReadBoolInvoke", new StringMethod(), "CanMove") == null, "text return remains unknown");
            Check(Call("ReadBoolInvoke", new ThrowingBool(), "CanMove") == null, "throwing bool remains unknown");
            Check(Call("ReadBoolInvoke", new GenericBool(), "CanMove") == null, "open generic bool is not invoked");
            Check(object.Equals(Call("ReadBoolInvoke", new LowerBool(), "CanMove"), false), "lowercase bool fallback retained");
            Check(Call("ReadBoolInvoke", new LowerBool(), "") == null, "empty method name remains unknown");
        });
        Scenario("ambiguous instance invocation", delegate
        {
            var target = new InstanceOverloads();
            Check(Call("InvokeOn", target, "SetData", new object[] { "value" }) == null, "ambiguous instance returns unknown");
            Check(target.Calls == 0, "no instance overload runs before ambiguity is resolved");
        });
        Scenario("unique instance invocation", delegate
        {
            var target = new InstanceUnique();
            Check(object.Equals(Call("InvokeOn", target, "Show", new object[] { "value" }), "value"), "unique compatible instance invoked");
            Check(target.Calls == 1, "unique instance invoked once");
            Check(Call("InvokeOn", target, "Hide", null) == null, "throwing unique instance returns unknown");
            Check(target.Calls == 2, "throwing instance is never retried");
            Check(Call("InvokeOn", null, "Show", null) == null, "null instance remains unknown");
        });
        Scenario("static invocation selection", delegate
        {
            StaticOverloads.Calls = 0;
            Check(Call("InvokeStatic", typeof(StaticOverloads), "Select", new object[] { "value" }) == null, "ambiguous static remains unknown");
            Check(StaticOverloads.Calls == 0, "ambiguous static has no effect");
            Check(Call("InvokeStatic", typeof(StaticOverloads), "Value", new object[] { null }) == null, "null cannot become value-type zero");
            Check(StaticOverloads.Calls == 0, "null-to-zero conversion never invoked");
            Check(object.Equals(Call("InvokeStatic", typeof(StaticOverloads), "Value", new object[] { 7 }), 7), "actual integer overload retained");
            Check(object.Equals(Call("InvokeStatic", typeof(StaticOverloads), "Ref", new object[] { null }), "null"), "null reference argument retained");
            Check(Call("InvokeStatic", typeof(StaticOverloads), "Nullable", new object[] { null }) == null, "nullable result remains null");
            Check(StaticOverloads.Calls == 3, "nullable argument actually invoked once");
            Check(Call("InvokeStatic", typeof(StaticOverloads), "Generic", new object[] { "value" }) == null, "unbound generic and incompatible overload not invoked");
            Check(StaticOverloads.Calls == 3, "invalid generic selection has no effect");
            Check(Call("InvokeStatic", null, "Any", null) == null, "null static type remains unknown");
        });
        Console.WriteLine("RESULT passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
