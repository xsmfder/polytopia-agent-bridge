using System;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public abstract class CommandBase { }
public sealed class FutureCommand : CommandBase { }

internal static class CommandInvocationTests
{
    private sealed class HookState { public sealed class GameState { } }
    private sealed class OtherHookState { public sealed class GameState { } }
    private sealed class NativeAction
    {
        public int Calls;
        public void Execute<T>(HookState.GameState state) { throw new Exception("unbound hook"); }
        public void Execute(HookState.GameState state) { Calls++; }
        public void Execute(ref OtherHookState.GameState state) { throw new Exception("ref hook"); }
    }
    private sealed class AmbiguousAction
    {
        public void Execute(HookState.GameState state) { throw new Exception("ambiguous"); }
        public void Execute(OtherHookState.GameState state) { throw new Exception("ambiguous"); }
    }
    private class ParentAction { public void Execute(HookState.GameState state) { } }
    private sealed class InheritedAction : ParentAction { }
    private sealed class GenericAction { public void Execute<T>(HookState.GameState state) { } }
    private sealed class BooleanAction { public bool Execute(HookState.GameState state) { return true; } }
    private const BindingFlags F = BindingFlags.Static | BindingFlags.NonPublic;
    private static int passed, failed;
    private static object Call(string name, params object[] args)
    { return typeof(BridgeMod).GetMethod(name, F).Invoke(null, args); }
    private static void Check(bool ok, string label)
    { if (ok) passed++; else { failed++; Console.WriteLine("FAIL " + label); } }
    private sealed class NumericCommand
    {
        public static int Calls, Defaults;
        public uint Value;
        public NumericCommand() { Defaults++; }
        public NumericCommand(uint value) { Calls++; Value = value; }
        public NumericCommand(string a, string b) { Calls++; }
    }
    private sealed class ThrowingCommand
    {
        public static int Calls, Defaults;
        public ThrowingCommand() { Defaults++; }
        public ThrowingCommand(int value) { Calls++; throw new Exception("constructor entered"); }
    }
    private sealed class AmbiguousCommand
    {
        public static int Calls;
        public AmbiguousCommand(object value) { Calls++; }
        public AmbiguousCommand(string value) { Calls++; }
    }
    private sealed class NullableCommand { public NullableCommand(int? value) { } }
    private sealed class ReferenceCommand { public ReferenceCommand(ref int value) { } }
    private sealed class GenericCommand<T> { public GenericCommand() { } }
    private sealed class OneArgument
    {
        public int Calls;
        public bool ExecuteCommand(CommandBase command) { Calls++; return false; }
    }
    private sealed class NativeExecutor
    {
        public int Calls, WrongCalls; public bool Accept;
        public bool ExecuteCommand(CommandBase command, out int error) { WrongCalls++; error = 0; return true; }
        public bool ExecuteCommand(CommandBase command, out string error)
        { Calls++; error = Accept ? null : "native refusal"; return Accept; }
    }
    private sealed class GenericExecutor
    {
        public int Calls;
        public bool ExecuteCommand<T>(CommandBase command, out string error) { throw new Exception("unbound"); }
        public bool ExecuteCommand(CommandBase command, out string error) { Calls++; error = null; return true; }
    }
    private sealed class AmbiguousExecutor
    {
        public int Calls;
        public bool ExecuteCommand(CommandBase command) { Calls++; return true; }
        public bool ExecuteCommand(CommandBase command, out string error) { Calls++; error = null; return true; }
    }
    private sealed class NumberExecutor { public int ExecuteCommand(CommandBase command) { throw new Exception(); } }
    private sealed class TextExecutor { public string ExecuteCommand(CommandBase command) { throw new Exception(); } }
    private sealed class NullableExecutor { public bool? ExecuteCommand(CommandBase command) { throw new Exception(); } }
    private sealed class BroadExecutor { public bool ExecuteCommand(object command) { throw new Exception(); } }
    private sealed class RefExecutor { public bool ExecuteCommand(CommandBase command, ref string error) { throw new Exception(); } }
    private sealed class ExtraExecutor { public bool ExecuteCommand(CommandBase command, string error, int extra) { throw new Exception(); } }
    private sealed class StaticExecutor { public static bool ExecuteCommand(CommandBase command) { throw new Exception(); } }
    private sealed class ThrowingExecutor
    {
        public int Calls;
        public bool ExecuteCommand(CommandBase command, out string error)
        { Calls++; error = "effect before failure"; throw new Exception("native entered"); }
    }
    private static MethodInfo Executor(Type manager, Type command)
    { return (MethodInfo)Call("FindCommandExecutor", manager, command); }
    private static object Invoke(object manager, MethodInfo method, out string error)
    {
        object[] args = {method, manager, new FutureCommand(), null};
        var result = typeof(BridgeMod).GetMethod("InvokeCommandExecutor", F).Invoke(null, args);
        error = (string)args[3]; return result;
    }
    public static int Main()
    {
        var hook = (MethodInfo)Call("CRFindExecuteHook", typeof(NativeAction));
        var nativeAction = new NativeAction();
        Check(hook != null && nativeAction.Calls == 0, "native hook ignores unbound generic/ref without entering action");
        hook.Invoke(nativeAction, new object[] {new HookState.GameState()});
        Check(nativeAction.Calls == 1, "selected native Execute signature remains invocable once");
        foreach (Type bad in new[] {typeof(AmbiguousAction), typeof(InheritedAction), typeof(GenericAction), typeof(BooleanAction)})
            Check(Call("CRFindExecuteHook", bad) == null, "invalid/ambiguous/inherited hook rejected: " + bad.Name);
        foreach (object bad in new object[] {null, 0, -1, true, "0", 1m, 1d})
        {
            // string is incompatible with the only one-argument constructor, not
            // an excuse to run the parameterless constructor as a fallback.
            var constructor = Call("UniqueInvocableConstructor", typeof(NumericCommand), new object[] {bad});
            Check(constructor == null && NumericCommand.Calls == 0 && NumericCommand.Defaults == 0,
                "constructor binding never coerces/null-defaults an argument or invokes a fallback");
        }
        var selected = (ConstructorInfo)Call("UniqueInvocableConstructor", typeof(NumericCommand), new object[] {UInt32.MaxValue});
        Check(selected != null && ((NumericCommand)selected.Invoke(new object[] {UInt32.MaxValue})).Value == UInt32.MaxValue
            && NumericCommand.Calls == 1 && NumericCommand.Defaults == 0,
            "same-arity incompatible constructors do not hide exact uint constructor");
        Check(Call("UniqueInvocableConstructor", typeof(NumericCommand), null) == null,
            "missing constructor plan cannot mean parameterless/default construction");
        selected = (ConstructorInfo)Call("UniqueInvocableConstructor", typeof(NumericCommand), new object[0]);
        Check(selected != null && selected.Invoke(new object[0]) is NumericCommand && NumericCommand.Defaults == 1,
            "an explicitly empty argument plan retains valid parameterless construction");
        Check(Call("UniqueInvocableConstructor", typeof(AmbiguousCommand), new object[] {"value"}) == null
            && AmbiguousCommand.Calls == 0, "ambiguous assignable constructors rejected before either runs");
        Check(Call("UniqueInvocableConstructor", typeof(NullableCommand), new object[] {null}) != null,
            "explicit null remains valid for a nullable parameter");
        Check(Call("UniqueInvocableConstructor", typeof(ReferenceCommand), new object[] {7}) == null,
            "constructor cannot acquire unrequested ref storage");
        Check(Call("UniqueInvocableConstructor", typeof(GenericCommand<>), new object[0]) == null,
            "open generic type cannot be constructed");
        Check(Call("UniqueInvocableConstructor", typeof(CommandBase), new object[0]) == null,
            "abstract command type cannot be constructed");
        selected = (ConstructorInfo)Call("UniqueInvocableConstructor", typeof(ThrowingCommand), new object[] {7});
        try { selected.Invoke(new object[] {7}); Check(false, "throwing constructor must fail"); }
        catch (TargetInvocationException) { Check(ThrowingCommand.Calls == 1 && ThrowingCommand.Defaults == 0,
            "selected constructor failure never invokes a different/default constructor"); }
        var target = new NativeExecutor(); string error;
        var exec = Executor(target.GetType(), typeof(FutureCommand));
        Check(exec != null && exec.GetParameters()[1].IsOut && exec.GetParameters()[1].ParameterType == typeof(string).MakeByRefType(),
            "native executor binds exact out string without invoking an unrelated overload");
        Check(object.Equals(Invoke(target, exec, out error), false) && error == "native refusal"
            && target.Calls == 1 && target.WrongCalls == 0, "native false and exact validation text survive one invocation");
        target.Accept = true;
        Check(object.Equals(Invoke(target, exec, out error), true) && error == "" && target.Calls == 2,
            "native true and no error are retained without bool coercion");
        var one = new OneArgument();
        exec = Executor(one.GetType(), typeof(FutureCommand));
        Check(exec != null && object.Equals(Invoke(one, exec, out error), false) && error == "" && one.Calls == 1,
            "unique one-argument legacy bool executor remains supported");
        var generic = new GenericExecutor(); exec = Executor(generic.GetType(), typeof(FutureCommand));
        Check(exec != null && object.Equals(Invoke(generic, exec, out error), true) && generic.Calls == 1,
            "open generic overload does not hide the actual native executor");
        var ambiguous = new AmbiguousExecutor();
        Check(Executor(ambiguous.GetType(), typeof(FutureCommand)) == null && ambiguous.Calls == 0,
            "multiple supported executor shapes remain ambiguous without probing either");
        foreach (Type type in new[] {typeof(NumberExecutor),typeof(TextExecutor),typeof(NullableExecutor),typeof(BroadExecutor),
                typeof(RefExecutor),typeof(ExtraExecutor),typeof(StaticExecutor)})
            Check(Executor(type, typeof(FutureCommand)) == null, "unsupported executor is never selected: " + type.Name);
        Check(Executor(typeof(NativeExecutor), typeof(object)) == null, "arbitrary object cannot masquerade as native CommandBase");
        Check(Executor(null, typeof(FutureCommand)) == null && Executor(typeof(NativeExecutor), null) == null,
            "missing native metadata has no executor");
        var throwing = new ThrowingExecutor(); exec = Executor(throwing.GetType(), typeof(FutureCommand));
        try { Invoke(throwing, exec, out error); Check(false, "native exception must propagate"); }
        catch (TargetInvocationException) { Check(throwing.Calls == 1, "native exception is not swallowed/retried or converted to false"); }
        var unknown = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>((string)Call("CommandConstructionUnknown", "COMMAND_CONSTRUCTION_FAILED"));
        Check((string)unknown["outcome"] == "unknown" && (string)unknown["verdict"] == "unknown"
            && !unknown.ContainsKey("accepted") && !unknown.ContainsKey("dispatched"),
            "entered constructor failure cannot claim pre-dispatch nonexecution");
        var host = new BridgeMod();
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(BridgeMod).GetField("_combatStream", members).SetValue(host, "stream");
        var tickets = (IDictionary)typeof(BridgeMod).GetField("_combatTickets", members).GetValue(host);
        var ticket = new BridgeMod.CombatTicket {Id=8, RequestId=1234, Stream="stream", Dispatched=true,
            Before=new BridgeMod.CombatFrame {Episode="ep-0123456789abcdef",Me="2"}};
        tickets.Add(8L, ticket);
        var failureBuilder = typeof(BridgeMod).GetMethod("BuildCommandInvocationFailure", members);
        foreach (bool audit in new[] {false, true})
        {
            var response = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>((string)failureBuilder.Invoke(host,
                new object[] {null,8L,"FutureCommand","ExecuteCommand(2)",(byte)2,new Exception("private-native-marker"),audit}));
            var settlement = (Dictionary<string,object>)response["settlement"];
            Check(Convert.ToInt64(response["settlementTicket"]) == 8 && Convert.ToInt64(settlement["requestId"]) == 1234
                && (string)settlement["settlementStatus"] == "unknown", "execution exception retains the original allocated ticket and request");
            Check((string)response["outcome"] == "unknown" && !response.ContainsKey("accepted") && !response.ContainsKey("dispatched")
                && (string)response["errorCode"] == "INVOKE_FAILED", "execution exception never claims nonexecution or a completed effect");
            Check(response.ContainsKey("invokeDetail") == audit,
                "private native exception text appears only in explicit audit output");
        }
        Console.WriteLine("passed=" + passed + " failed=" + failed); return failed == 0 ? 0 : 1;
    }
}
