using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using PolytopiaPublicMarkers;

public static class PointerGetterProbe
{
    public static int Reads;
    public static IntPtr Value;
    public static bool Fail;
    public static IntPtr Read()
    { Interlocked.Increment(ref Reads); if(Fail) throw new InvalidOperationException("disposed read failed"); return Value; }
}
internal static class NativePointerIdentityTests
{
    private sealed class Lookalike { public IntPtr Pointer {get{return new IntPtr(123);}} }
    private static int passed;
    private static void Check(bool ok,string message)
    { if(!ok) throw new Exception(message); passed++; Console.WriteLine("PASS "+message); }
    public static int Main()
    {
        try {return Run();}
        catch(Exception error) {Console.WriteLine("FAIL "+error);return 1;}
    }
    private static int Run()
    {
        Check(PublicMarkerHooks.StableNativeIdentity(null)==null
            && PublicMarkerHooks.StableNativeIdentity(new Lookalike())==null,"missing runtime and unrelated Pointer property never establish identity");
        var assembly=AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("Il2CppInterop.Runtime"),AssemblyBuilderAccess.Run);
        var module=assembly.DefineDynamicModule("main");
        var builder=module.DefineType("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase",TypeAttributes.Public);
        builder.DefineDefaultConstructor(MethodAttributes.Public);
        var property=builder.DefineProperty("Pointer",PropertyAttributes.None,typeof(IntPtr),Type.EmptyTypes);
        var getter=builder.DefineMethod("get_Pointer",MethodAttributes.Public|MethodAttributes.SpecialName|MethodAttributes.HideBySig,typeof(IntPtr),Type.EmptyTypes);
        var il=getter.GetILGenerator(); il.Emit(OpCodes.Call,typeof(PointerGetterProbe).GetMethod("Read")); il.Emit(OpCodes.Ret);
        property.SetGetMethod(getter);
        object value=Activator.CreateInstance(builder.CreateType());
        Check(PointerGetterProbe.Reads==0,"binding type and property metadata does not execute pointer getter");
        PointerGetterProbe.Value=new IntPtr(123);
        Check(object.Equals(PublicMarkerHooks.StableNativeIdentity(value),new IntPtr(123)) && PointerGetterProbe.Reads==1,
            "runtime loaded after an initial miss can establish native identity");
        PointerGetterProbe.Value=new IntPtr(456);
        Check(object.Equals(PublicMarkerHooks.StableNativeIdentity(value),new IntPtr(456)) && PointerGetterProbe.Reads==2,
            "metadata reuse never caches a prior pointer value");
        PointerGetterProbe.Value=IntPtr.Zero;
        Check(PublicMarkerHooks.StableNativeIdentity(value)==null && PointerGetterProbe.Reads==3,
            "disposed zero pointer invalidates a previously valid identity");
        PointerGetterProbe.Fail=true; Check(PublicMarkerHooks.StableNativeIdentity(value)==null,"getter failure remains unknown identity");
        PointerGetterProbe.Fail=false; PointerGetterProbe.Value=new IntPtr(789);
        Check(object.Equals(PublicMarkerHooks.StableNativeIdentity(value),new IntPtr(789)),"pointer reads recover after a transient getter failure");
        int before=PointerGetterProbe.Reads;
        Check(PublicMarkerHooks.StableNativeIdentity(new Lookalike())==null && PointerGetterProbe.Reads==before,
            "cached native base type still rejects a lookalike object without reading it");
        var errors=new Exception[8]; var threads=new Thread[8];
        for(int i=0;i<threads.Length;i++)
        {
            int slot=i;
            threads[i]=new Thread(delegate(){try{
                for(int j=0;j<128;j++) if(!object.Equals(PublicMarkerHooks.StableNativeIdentity(value),new IntPtr(789))) throw new Exception("wrong pointer");
            }catch(Exception error){errors[slot]=error;}}); threads[i].Start();
        }
        foreach(var thread in threads) thread.Join();
        bool clean=true; foreach(var error in errors) if(error!=null) clean=false;
        Check(clean && PointerGetterProbe.Reads==before+1024,"concurrent reuse keeps every native pointer getter live");
        Console.WriteLine("TOTAL "+passed+" passed");return 0;
    }
}
