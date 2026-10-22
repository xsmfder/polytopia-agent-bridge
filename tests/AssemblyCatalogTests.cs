using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using PolytopiaAgent;

internal static class CatalogGetterProbe { internal static int Calls; }
internal sealed class CatalogUninitialized
{ static CatalogUninitialized() { CatalogGetterProbe.Calls++; } }
internal sealed class CatalogAssembly : Assembly
{
    internal int Reads;
    internal bool Fail;
    internal readonly Exception Error=new InvalidOperationException("temporary metadata failure");
    internal Type[] Contents=new[] { typeof(CatalogUninitialized), typeof(CatalogAssembly) };
    public override bool IsDynamic { get { return false; } }
    public override Type[] GetTypes()
    { Interlocked.Increment(ref Reads); if(Fail) throw Error; return (Type[])Contents.Clone(); }
}
namespace Catalog.SpaceA { internal class NameProbe {} internal class Outer { internal class Inner {} } }
namespace Catalog.SpaceB { internal class NameProbe {} }
internal sealed class CatalogFlakyType : TypeDelegator
{
    internal bool Fail=true;
    internal readonly Exception Error=new InvalidOperationException("temporary type name failure");
    internal CatalogFlakyType() : base(typeof(CatalogAssembly)) {}
    public override string FullName { get { if(Fail) throw Error; return base.FullName; } }
}
internal static class AssemblyCatalogTests
{
    private static int passed;
    private static void Check(bool ok, string name)
    { if(!ok) throw new Exception(name); passed++; Console.WriteLine("PASS "+name); }
    public static int Main()
    {
        try { return Run(); }
        catch(Exception error) { Console.WriteLine("FAIL "+error); return 1; }
    }
    private static int Run()
    {
        BridgeMod.ResetAssemblyTypeCatalogsForTests();
        var assembly=new CatalogAssembly();
        Type[] first=BridgeMod.ResolverTypesOf(assembly);
        bool reused=true;
        for(int i=0;i<100;i++) reused &= object.ReferenceEquals(first,BridgeMod.ResolverTypesOf(assembly));
        Check(reused,"different later resolutions reuse the same static type table");
        Check(assembly.Reads==1 && first.Length==2 && CatalogGetterProbe.Calls==0,
            "enumerating types does not execute native-style static constructors or repeat metadata reads");
        var other=new CatalogAssembly(); BridgeMod.ResolverTypesOf(other);
        Check(other.Reads==1 && assembly.Reads==1,"different assembly identities retain independent catalogs");

        var indexed=new CatalogAssembly { Contents=new[] { typeof(Catalog.SpaceA.NameProbe), typeof(Catalog.SpaceB.NameProbe),
            typeof(Catalog.SpaceA.Outer.Inner), typeof(CatalogAssembly) } };
        bool sameMatches=true;
        foreach(string name in new[] { "NameProbe", "SpaceA.NameProbe", "Catalog.SpaceA.NameProbe", "Catalog.SpaceB.NameProbe",
            "Outer+Inner", "Catalog.SpaceA.Outer+Inner", "Inner", "Unmatched", "Name", "Wrong.SpaceA.NameProbe", "spaceA.NameProbe", "." })
        {
            var expectedTypes=new List<Type>();
            foreach(Type t in indexed.Contents)
                if(t.FullName==name || t.FullName.EndsWith("."+name,StringComparison.Ordinal)) expectedTypes.Add(t);
            var actualTypes=new List<Type>();
            foreach(Type t in BridgeMod.ResolverGameTypeCandidates(indexed,name))
                if(t.FullName==name || t.FullName.EndsWith("."+name,StringComparison.Ordinal)) actualTypes.Add(t);
            if(expectedTypes.Count!=actualTypes.Count) sameMatches=false;
            else for(int i=0;i<expectedTypes.Count;i++) if(expectedTypes[i]!=actualTypes[i]) sameMatches=false;
        }
        Check(sameMatches && indexed.Reads==1,"indexed candidates match literal legacy full-name/suffix scans including namespace and nested ambiguity");
        Check(BridgeMod.ResolverGameTypeCandidates(indexed,null).Length==0 && BridgeMod.ResolverGameTypeCandidates(indexed,"").Length==0,
            "empty requested name cannot create a broad candidate list");
        var flakyType=new CatalogFlakyType(); var retryIndex=new CatalogAssembly { Contents=new Type[] { flakyType } }; bool indexError=false;
        try { BridgeMod.ResolverGameTypeCandidates(retryIndex,"CatalogAssembly"); } catch(Exception e) { indexError=object.ReferenceEquals(e,flakyType.Error); }
        flakyType.Fail=false;
        Check(indexError && BridgeMod.ResolverGameTypeCandidates(retryIndex,"CatalogAssembly").Length==1 && retryIndex.Reads==2,
            "failed index does not remain a permanently faulted lazy catalog");

        var flaky=new CatalogAssembly { Fail=true }; bool sameError=false;
        try { BridgeMod.ResolverTypesOf(flaky); } catch(Exception e) { sameError=object.ReferenceEquals(e,flaky.Error); }
        flaky.Fail=false; var recovered=BridgeMod.ResolverTypesOf(flaky);
        Check(sameError && flaky.Reads==2 && recovered.Length==2, "failed metadata load propagates unchanged and can recover on next lookup");

        var emitted=AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("CatalogLive"+Guid.NewGuid().ToString("N")),AssemblyBuilderAccess.Run);
        var module=emitted.DefineDynamicModule("main"); module.DefineType("FirstLiveType",TypeAttributes.Public).CreateType();
        var early=BridgeMod.ResolverTypesOf(emitted);
        Check(BridgeMod.ResolverGameTypeCandidates(emitted,"SecondLiveType").Length==0,"dynamic name directory starts with the actual current metadata");
        module.DefineType("SecondLiveType",TypeAttributes.Public).CreateType();
        var later=BridgeMod.ResolverTypesOf(emitted);
        Check(early.Length==1 && later.Length==2,"dynamic assembly reflects later types without an AssemblyLoad event");
        Check(BridgeMod.ResolverGameTypeCandidates(emitted,"SecondLiveType").Length==1,"dynamic name directory never reuses an obsolete missing-name index");

        var concurrent=new CatalogAssembly(); var errors=new Exception[8]; var threads=new Thread[8];
        for(int i=0;i<threads.Length;i++)
        { int slot=i; threads[i]=new Thread(delegate() { try { BridgeMod.ResolverGameTypeCandidates(concurrent,"CatalogAssembly"); } catch(Exception e) { errors[slot]=e; } }); threads[i].Start(); }
        foreach(var thread in threads) thread.Join();
        bool clean=true; foreach(var error in errors) if(error!=null) clean=false;
        Check(clean && concurrent.Reads==1,"concurrent lookups publish one successful static catalog");

        BridgeMod.ResetAssemblyTypeCatalogsForTests();
        for(int i=0;i<BridgeMod.MaxAssemblyTypeCatalogs;i++) BridgeMod.ResolverTypesOf(new CatalogAssembly());
        var overflow=new CatalogAssembly(); BridgeMod.ResolverTypesOf(overflow); BridgeMod.ResolverTypesOf(overflow);
        var stats=BridgeJsonCodec.Object(BridgeMod.AssemblyTypeCatalogStatsJson());
        Check(int.Parse(BridgeJsonCodec.Scalar(stats["entries"]))==BridgeMod.MaxAssemblyTypeCatalogs
            && overflow.Reads==2,"catalog cap is enforced while overflow resolutions remain correct");
        Check(BridgeMod.ResolverGameTypeCandidates(overflow,"CatalogAssembly").Length==1 && overflow.Reads==3,
            "index lookup after the cache cap still resolves the exact candidate");
        Check(CatalogGetterProbe.Calls==0,"all catalog, failure and concurrency paths leave type initializers untouched");
        Console.WriteLine("TOTAL "+passed+" passed"); return 0;
    }
}
