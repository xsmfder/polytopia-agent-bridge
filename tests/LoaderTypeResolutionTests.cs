using System;
using System.Reflection;
using System.Reflection.Emit;
using PolytopiaAgent;
internal static class LoaderTypeResolutionTests
{
    static int passed;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    static Type Emit(string assembly,string fullName)
    {
        var a=AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName(assembly),AssemblyBuilderAccess.Run);
        return a.DefineDynamicModule(assembly).DefineType(fullName,TypeAttributes.Public).CreateType();
    }
    static void Main()
    {
        Check(BridgeMod.IsGameInteropAssemblyName("PolytopiaAssembly"),"BepInEx native game assembly recognized");
        Check(BridgeMod.IsGameInteropAssemblyName("Il2CppPolytopiaAssembly"),"Melon native game assembly remains recognized");
        Check(!BridgeMod.IsGameInteropAssemblyName("BepInEx.Core"),"loader assembly is not a game assembly");
        Check(!BridgeMod.IsGameInteropAssemblyName("PolytopiaAssembly.OtherMod"),"prefix alone does not grant game type authority");
        Emit("LoaderNoise"+Guid.NewGuid().ToString("N"),"LoaderNoise.Paths");
        var game=Emit("PolytopiaAssembly","GameNamespace.Paths");
        BridgeMod.ResetReflectionCacheForTests();
        Check(BridgeMod.FindGameType("Paths")==game,"native Paths wins over loader Paths by actual assembly identity");
        Emit("GameLogicAssembly","OtherNativeNamespace.Paths");
        BridgeMod.ResetReflectionCacheForTests();
        Check(BridgeMod.FindGameType("Paths")==null,"two native Paths candidates remain ambiguous rather than guessed");
        Console.WriteLine("LoaderTypeResolutionTests: "+passed+" passed");
    }
}
