// Exercise real wrapping callers and appended-action discovery; no native code runs.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using PolytopiaAgent;

public class FutureCommand { public IntPtr Pointer; }
public class OtherCommand { }
public class BadReturnCaster
{
    public int Calls;
    public object TryCast<T>() { Calls++; return new OtherCommand(); }
}
public class PointerCaster
{
    public IntPtr Pointer = new IntPtr(11); public FutureCommand Value; public int Calls;
    public T TryCast<T>() where T:class { Calls++; return Value as T; }
}
public class ActionStackFixture
{
    public object[] Items; public int Reads;
    public int Count { get {return Items.Length;} }
    public object this[int i] {get {Reads++;return Items[i];} }
}
public class ValueStackFixture
{
    public int Reads; public int Count {get{return 1;}}
    public int this[int i] {get{Reads++;return 0;}}
}
public class CastState { public object ActionStack; }
public class NullCaster
{
    public IntPtr Pointer=new IntPtr(11); public int Calls;
    public T TryCast<T>() where T:class {Calls++;return null;}
}
public class ThrowingCaster
{
    public IntPtr Pointer=new IntPtr(11);public int Calls;
    public T TryCast<T>() {Calls++;throw new Exception("native cast failed after entry");}
}
public class ConstrainedCaster
{
    public int Calls;
    public T TryCast<T>() where T:struct {Calls++;return default(T);}
}
public class NativeWithoutCast {public IntPtr Pointer=new IntPtr(11);}
public class ZeroPointerCaster
{
    public IntPtr Pointer=IntPtr.Zero;public int Calls;
    public T TryCast<T>() where T:class {Calls++;return null;}
}
public class UnreadablePointerCaster
{
    public IntPtr Pointer {get{throw new Exception("pointer inaccessible");}}public int Calls;
    public T TryCast<T>() where T:class {Calls++;return null;}
}
public class MixedCaster
{
    public IntPtr Pointer=new IntPtr(11);public FutureCommand Value;public int Calls,Wrong;
    public T TryCast<T>() where T:class {Calls++;return Value as T;}
    public object TryCast() {Wrong++;throw new Exception("unrelated overload");}
    public T TryCast<T,U>() {Wrong++;throw new Exception("wrong generic arity");}
}
public class GrowingStackFixture
{
    public List<object> Items=new List<object>();public int Reads;
    public int Count {get{return Items.Count;}}
    public object this[int i] {get{Reads++;var item=Items[i];Items.Add(new OtherCommand());return item;}}
}

internal static class NativeCastTests
{
    private const BindingFlags F=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static int passed,failed;
    private static object Call(string name,params object[] args)
    { return typeof(BridgeMod).GetMethod(name,F).Invoke(null,args); }
    private static void Check(bool ok,string label)
    {if(ok)passed++;else{failed++;Console.WriteLine("FAIL "+label);} }
    private static bool Appended(object stack,int? start,out List<object> result)
    {
        object[] args={new CastState {ActionStack=stack},start,"FutureCommand",null};
        bool ok=(bool)typeof(BridgeMod).GetMethod("CRTryAppendedActions",F).Invoke(null,args);result=(List<object>)args[3];return ok;
    }
    private static bool Cast(object value,Type type,out object result,out string error)
    {
        object[] args={value,type,null,null};
        bool readable=(bool)Call("TryNativeCast",args);result=args[2];error=(string)args[3];return readable;
    }
    public static int Main()
    {
        var wrong=new BadReturnCaster();
        Check(Call("CRTryNativeCast",wrong,typeof(FutureCommand))==null&&wrong.Calls==0,
            "wrong-return generic method never invoked or accepted as target");
        wrong=new BadReturnCaster();
        Check(Call("URConcreteCommand",wrong,"Future")==null&&wrong.Calls==0,
            "unit menu cannot fall back to invalid base after failed typed wrapping");
        var source=new PointerCaster {Value=new FutureCommand {Pointer=new IntPtr(12)}};
        Check(Call("CRTryNativeCast",source,typeof(FutureCommand))==null&&source.Calls==1,
            "rewrap with changed native identity rejected without trying again");
        var good=new FutureCommand();List<object> result;
        var mixed=new ActionStackFixture {Items=new object[]{good,new BadReturnCaster()}};
        Check(!Appended(mixed,0,out result)&&result.Count==1&&ReferenceEquals(result[0],good),
            "unknown cast keeps suffix partial and retains only typed positive");
        var scalar=new ValueStackFixture();
        Check(!Appended(scalar,0,out result)&&result.Count==0&&scalar.Reads==0,
            "numeric indexer cannot silently prove empty native action suffix");
        mixed=new ActionStackFixture {Items=new object[]{good}};
        var legacy=(IList)Call("CRAppendedActions",new CastState {ActionStack=mixed},(int?)-1,"FutureCommand");
        Check(legacy.Count==0&&mixed.Reads==0,"legacy appended reader rejects negative prefix before any index read");
        object typed;string error;
        Check(Cast(good,typeof(FutureCommand),out typed,out error)&&ReferenceEquals(typed,good)&&error==null,
            "already typed value preserves same object without rewrapping");
        Check(Cast(new OtherCommand(),typeof(FutureCommand),out typed,out error)&&typed==null&&error==null,
            "concretely different managed type is known nonmatch");
        Check(Cast(null,typeof(FutureCommand),out typed,out error)&&typed==null,"absent value is known absent");
        Check(!Cast(good,null,out typed,out error)&&error=="CAST_TARGET_UNAVAILABLE","missing target metadata remains unavailable");
        Check(!Cast(good,typeof(int),out typed,out error)&&typed==null,"primitive target is not a native wrapper type");
        source=new PointerCaster {Value=new FutureCommand {Pointer=new IntPtr(11)}};
        Check(Cast(source,typeof(FutureCommand),out typed,out error)&&ReferenceEquals(typed,source.Value)&&source.Calls==1,
            "actual generic T wrapper preserves nonzero native identity once");
        var noMatch=new NullCaster();
        Check(Cast(noMatch,typeof(FutureCommand),out typed,out error)&&typed==null&&noMatch.Calls==1&&error==null,
            "successful native null cast is known nonmatch");
        Check(object.Equals(Call("NativeCastIs",new NullCaster(),typeof(FutureCommand)),false),
            "known nonmatch projects false for reaction facts");
        var throws=new ThrowingCaster();
        Check(!Cast(throws,typeof(FutureCommand),out typed,out error)&&typed==null&&throws.Calls==1&&error=="CAST_UNAVAILABLE",
            "throwing cast is unavailable and never retried");
        Check(Call("NativeCastIs",new ThrowingCaster(),typeof(FutureCommand))==null,
            "reaction cast failure remains unknown rather than false");
        var constraint=new ConstrainedCaster();
        Check(!Cast(constraint,typeof(FutureCommand),out typed,out error)&&constraint.Calls==0,
            "unmet native generic constraint rejected before invocation");
        Check(!Cast(new NativeWithoutCast(),typeof(FutureCommand),out typed,out error)&&error=="CAST_METHOD_UNAVAILABLE",
            "opaque native wrapper without cast metadata is not known nonmatch");
        var zero=new ZeroPointerCaster();
        Check(!Cast(zero,typeof(FutureCommand),out typed,out error)&&zero.Calls==0&&error=="CAST_IDENTITY_UNAVAILABLE",
            "zero source pointer never reaches cast method");
        var unreadable=new UnreadablePointerCaster();
        Check(!Cast(unreadable,typeof(FutureCommand),out typed,out error)&&unreadable.Calls==0,
            "unreadable source pointer is not defaulted or invoked");
        var mixedCast=new MixedCaster {Value=new FutureCommand {Pointer=new IntPtr(11)}};
        Check(Cast(mixedCast,typeof(FutureCommand),out typed,out error)&&mixedCast.Calls==1&&mixedCast.Wrong==0,
            "actual T/one-generic shape selected without probing unrelated overloads");
        mixed=new ActionStackFixture {Items=new object[]{good,0,"not an action"}};
        Check(!Appended(mixed,0,out result)&&result.Count==1,"scalar/label stack slots remain incomplete rather than known empty");
        var grow=new GrowingStackFixture();grow.Items.Add(good);
        Check(!Appended(grow,0,out result)&&result.Count==1&&grow.Reads==1,
            "growing suffix cannot certify completeness or read beyond captured range");
        mixed=new ActionStackFixture {Items=new object[]{new PointerCaster {Value=new FutureCommand {Pointer=new IntPtr(11)}}}};
        Check(Appended(mixed,0,out result)&&result.Count==1&&result[0]is FutureCommand,
            "legitimate typed native wrapper survives complete appended discovery");
        Check(ReferenceEquals(Call("URConcreteCommand",good,"Future"),good),"concrete menu command retains identity");
        var future=new OtherCommand();
        Check(ReferenceEquals(Call("URConcreteCommand",future,"NotDefinedYet"),future),
            "future concrete native menu types do not require a maintained name table");
        Check(BridgeMod.CRResolutionPolicy(false,false,true,false,0,true,null,true,true,1,2,0.5)==null,
            "unknown choice-reaction cast remains unknown at real resolution policy");
        Check(BridgeMod.CRResolutionPolicy(false,false,true,false,1,true,null,true,true,1,2,0.5)==false,
            "known queued blocker stays false independently of unknown reaction type");
        Console.WriteLine("passed="+passed+" failed="+failed);return failed==0?0:1;
    }
}
