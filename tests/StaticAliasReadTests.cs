using System;
using System.Reflection;
using PolytopiaAgent;

internal static class StaticAliasReadTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static int passed, failed;
    private static void Check(bool ok, string label)
    { Console.WriteLine((ok ? "PASS " : "FAIL ") + label); if (ok) passed++; else failed++; }
    public sealed class FlakyStatic
    {
        public static int Reads;
        public static bool IsActive { get { if (++Reads == 1) throw new Exception("unreadable"); return false; } }
    }
    public sealed class StaticNull
    {
        public static int Reads, BackupReads;
        public static object Primary { get { Reads++; return null; } }
        public static object Backup { get { BackupReads++; return new object(); } }
    }
    public sealed class StaticMethodOnly { public static bool get_IsActive() { return true; } }
    public sealed class StaticAlias
    {
        public static int Reads, BackupReads;
        public static object Primary { get { Reads++; throw new Exception("unreadable"); } }
        public static object Backup { get { BackupReads++; return new object(); } }
    }
    public sealed class InstanceAlias
    {
        public int Reads, BackupReads;
        public object Primary { get { Reads++; throw new Exception("unreadable"); } }
        public object Backup { get { BackupReads++; return new object(); } }
    }
    public sealed class InstanceNull
    {
        public int BackupReads;
        public object Primary { get { return null; } }
        public object Backup { get { BackupReads++; return new object(); } }
    }
    public class InheritedStatic { public static int Inherited = 7; }
    public sealed class DerivedStatic : InheritedStatic { }
    public class InstanceBase { public int Value = 5; }
    public sealed class StaticDerived : InstanceBase { public new static int Value = 9; }
    public sealed class FlakySingleton
    {
        public static int Reads;
        public static object instance { get { if (++Reads == 1) throw new Exception("unreadable singleton"); return new object(); } }
    }
    public sealed class MethodSingleton { public static object get_instance() { return new object(); } }
    public sealed class NativePlayer { public byte Id = 2; }
    public sealed class MethodLocalPlayer { public static NativePlayer get_LocalPlayer() { return new NativePlayer(); } }
    public sealed class DualSingleton
    {
        public static int GetterCalls;
        public static object instance = new object();
        public static object Instance { get { GetterCalls++; return new object(); } }
    }
    private static object Static(string consumer, Type type, string name)
    {
        Type holder = consumer == "preview" ? typeof(PolytopiaBattlePreview.BattlePreviewSnapshot) : typeof(BridgeMod);
        string method = consumer == "bool" ? "HandoffStaticBool" : consumer == "try" ? "HandoffTryStatic" : "StaticMember";
        object[] args = consumer == "try" ? new object[] { type, name, null }
            : consumer == "world" ? new object[] { type, new[] { name } } : new object[] { type, name };
        object result = holder.GetMethod(method, F).Invoke(null, args);
        return consumer == "try" ? new object[] { result, args[2] } : result;
    }
    private static bool Known(string consumer, object result, object expected)
    { return consumer == "try" ? Equals(((object[])result)[0], true) && Equals(((object[])result)[1], expected) : Equals(result, expected); }
    public static int Main()
    {
        foreach (string consumer in new[] { "world", "bool", "try", "preview" })
        {
            FlakyStatic.Reads = 0;
            object result = Static(consumer, typeof(FlakyStatic), "IsActive");
            Check((consumer == "try" ? Equals(((object[])result)[0], false) : result == null) && FlakyStatic.Reads == 1,
                consumer + " static getter failure is unknown and invoked once");
            Check(Known(consumer, Static(consumer, typeof(FlakyStatic), "IsActive"), false) && FlakyStatic.Reads == 2,
                consumer + " next explicit query reads false without caching values");
            Check(Known(consumer, Static(consumer, typeof(StaticMethodOnly), "IsActive"), true),
                consumer + " supports native get_ method metadata without a CLR property");
        }
        var staticAliases = typeof(BridgeMod).GetMethod("StaticMember", F);
        StaticAlias.Reads = StaticAlias.BackupReads = 0;
        Check(staticAliases.Invoke(null, new object[] { typeof(StaticAlias), new[] { "Primary", "Backup" } }) == null
            && StaticAlias.Reads == 1 && StaticAlias.BackupReads == 0,
            "failed static primary never probes a backup member");
        StaticNull.Reads = StaticNull.BackupReads = 0;
        Check(staticAliases.Invoke(null, new object[] { typeof(StaticNull), new[] { "Primary", "Backup" } }) == null
            && StaticNull.Reads == 1 && StaticNull.BackupReads == 0,
            "known null static primary does not probe a backup");
        Check(staticAliases.Invoke(null, new object[] { typeof(StaticNull), new[] { "Missing", "Backup" } }) != null
            && StaticNull.BackupReads == 1, "absent static metadata can select an existing alternate");
        var instanceAliases = typeof(BridgeMod).GetMethod("InstanceField", F);
        var bad = new InstanceAlias();
        Check(instanceAliases.Invoke(null, new object[] { bad, new[] { "Primary", "Backup" } }) == null
            && bad.Reads == 1 && bad.BackupReads == 0, "failed instance alias never probes another getter");
        var empty = new InstanceNull();
        Check(instanceAliases.Invoke(null, new object[] { empty, new[] { "Primary", "Backup" } }) == null
            && empty.BackupReads == 0, "known null instance alias is not absent metadata");
        Check(instanceAliases.Invoke(null, new object[] { empty, new[] { "Missing", "Backup" } }) != null
            && empty.BackupReads == 1, "missing instance metadata still supports a real alternate");
        Check(Equals(staticAliases.Invoke(null, new object[] { typeof(DerivedStatic), new[] { "Inherited" } }), 7),
            "inherited static public field remains readable");
        var instance = new StaticDerived();
        var reader = typeof(BridgeMod).GetMethod("MemberWithAccessor", F);
        Check(Equals(reader.Invoke(null, new object[] { instance, "Value" }), 5), "instance plan ignores hidden static field");
        Check(Equals(staticAliases.Invoke(null, new object[] { typeof(StaticDerived), new[] { "Value" } }), 9),
            "static plan is isolated from instance metadata with the same type and name");
        ((InstanceBase)instance).Value = 10;
        Check(Equals(reader.Invoke(null, new object[] { instance, "Value" }), 10), "static plan cannot replace instance plan on a warm cache");
        var bridge = new BridgeMod();
        var managerType = typeof(BridgeMod).GetField("_gameManagerType", BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (string methodName in new[] { "InstanceOf", "SafeInstance" })
        {
            var method = typeof(BridgeMod).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            managerType.SetValue(bridge, typeof(FlakySingleton)); FlakySingleton.Reads = 0;
            object[] args = methodName == "InstanceOf" ? new object[] { typeof(FlakySingleton) } : null;
            Check(method.Invoke(bridge, args) == null && FlakySingleton.Reads == 1,
                methodName + " does not retry a failed singleton getter");
            Check(method.Invoke(bridge, args) != null && FlakySingleton.Reads == 2,
                methodName + " later explicit query can recover the native singleton");
            managerType.SetValue(bridge, typeof(MethodSingleton));
            Check(method.Invoke(bridge, methodName == "InstanceOf" ? new object[] { typeof(MethodSingleton) } : null) != null,
                methodName + " supports a native method-only singleton");
        }
        managerType.SetValue(bridge, typeof(MethodLocalPlayer));
        object identity = typeof(BridgeMod).GetMethod("ResolveControlledIdentity", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(bridge, new object[] { null });
        Check((bool)identity.GetType().GetField("Verified").GetValue(identity)
            && (byte)identity.GetType().GetField("Id").GetValue(identity) == 2,
            "controlled identity uses the same native static member adapter");
        managerType.SetValue(bridge, typeof(DualSingleton)); DualSingleton.GetterCalls = 0;
        Check(ReferenceEquals(typeof(BridgeMod).GetMethod("SafeInstance", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(bridge, null), DualSingleton.instance) && DualSingleton.GetterCalls == 0,
            "SafeInstance preserves exact lowercase field without entering a distinct public singleton getter");
        DualSingleton.GetterCalls = 0;
        Check(typeof(BridgeMod).GetMethod("InstanceOf", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(bridge, new object[] { typeof(DualSingleton) }) != null && DualSingleton.GetterCalls == 1,
            "InstanceOf retains its explicit uppercase singleton getter authority");
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
