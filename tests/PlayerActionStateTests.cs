// Player identity and native action flags must have the same meaning in every public reader.
using System;
using System.Reflection;
using PolytopiaAgent;

public enum ActionStateId { Named = 1 }
public sealed class ActionStatePlayer { public object Id; }
public static class ActionStateManager { public static object LocalPlayer; }
public sealed class ActionStateFlaky
{
    public int Reads;
    public bool CanMove { get { if (++Reads == 1) throw new InvalidOperationException("unreadable"); return true; } }
}
public sealed class ActionStateFlags
{
    public bool moved;
    public bool CanMove() { return !moved; }
    public bool CanMove(int ignored) { throw new Exception("wrong overload"); }
}
public sealed class ActionStateGeneric
{
    public int Calls;
    public bool CanMove<T>() { Calls++; throw new Exception("open generic"); }
}
public sealed class ActionStateWrongType { public string CanMove { get { return "true"; } } }
public class ActionStateBase { private bool CanMove { get { return false; } } }
public sealed class ActionStateInherited : ActionStateBase { }

internal static class PlayerActionStateTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static int passed, failed;
    private static void Check(bool ok, string label)
    { if (ok) { passed++; Console.WriteLine("PASS " + label); } else { failed++; Console.WriteLine("FAIL " + label); } }
    private static object Bool(string reader, object value, string name)
    {
        Type type = reader == "city" ? typeof(PolytopiaCityOps.CityOptionsSnapshot)
            : reader == "preview" ? typeof(PolytopiaBattlePreview.BattlePreviewSnapshot) : typeof(BridgeMod);
        string method = reader == "city" || reader == "preview" ? "MB" : reader;
        return type.GetMethod(method, F).Invoke(null, new[] { value, (object)name });
    }
    private static bool Verified(BridgeMod host, object id)
    {
        ActionStateManager.LocalPlayer = new ActionStatePlayer { Id = id };
        var identity = typeof(BridgeMod).GetMethod("ResolveControlledIdentity", F).Invoke(host, new object[] { new object() });
        return (bool)identity.GetType().GetField("Verified", F).GetValue(identity);
    }
    public static int Main()
    {
        var host = new BridgeMod();
        typeof(BridgeMod).GetField("_gameManagerType", F).SetValue(host, typeof(ActionStateManager));
        foreach (object id in new object[] { null, 0, -1, 255, 256, "2", 2.0, 2m, (ActionStateId)2, true })
            Check(!Verified(host, id), "unreadable/non-player local ID stays unverified: " + (id == null ? "null" : id.GetType().Name + " " + id));
        foreach (object id in new object[] { (byte)1, (ushort)2, 7, 254L })
            Check(Verified(host, id), "native integral controlled ID: " + id.GetType().Name);
        foreach (string reader in new[] { "ReadBoolPropertyOrGetter", "ReadBoolInvoke", "ReadBoolMember", "city", "preview" })
        {
            var flaky = new ActionStateFlaky();
            Check(Bool(reader, flaky, "CanMove") == null && flaky.Reads == 1,
                reader + " accessor failure is unknown and sampled once");
            Check(object.Equals(Bool(reader, flaky, "CanMove"), true) && flaky.Reads == 2,
                reader + " a later explicit read samples the current value");
            var separate = new ActionStateFlaky();
            Check(Bool(reader, separate, "CanMove") == null && separate.Reads == 1,
                reader + " metadata plan does not retain another instance or value");
            var flags = new ActionStateFlags();
            Check(object.Equals(Bool(reader, flags, "CanMove"), true), reader + " native method survives parameterized overload");
            flags.moved = true;
            Check(object.Equals(Bool(reader, flags, "CanMove"), false), reader + " current state is not cached");
            Check(object.Equals(Bool(reader, flags, "moved"), true), reader + " native bool field is consistent");
            Check(Bool(reader, new ActionStateWrongType(), "CanMove") == null, reader + " text flag is unknown");
            Check(object.Equals(Bool(reader, new ActionStateInherited(), "CanMove"), false), reader + " inherited private native member is readable");
            var generic = new ActionStateGeneric();
            Check(Bool(reader, generic, "CanMove") == null && generic.Calls == 0, reader + " open generic is never invoked");
        }
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
