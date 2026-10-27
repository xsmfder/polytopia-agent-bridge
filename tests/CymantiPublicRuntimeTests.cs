using System;
using System.Collections.Generic;
using System.Reflection;
using PolytopiaAgent;

namespace CymantiPublicFixture
{
    public enum UnitEffect { Frozen=0, Poisoned=1, Boosted=2, Swift=6, DoubleReady=7 }
    public enum UnrelatedEffect { Poisoned=1 }
    public class Unit
    {
        public object effects = new List<UnitEffect>();
        public object id=(uint)8, leader=(uint)0, follower=(uint)0;
        public bool Leader, Follower;
        public bool HasLeader() { return Leader; }
        public bool HasFollower() { return Follower; }
    }
    public class UnknownLinks { public uint id=8, leader=9, follower=10; }
    public class BadLinks { public uint id=8, leader=9, follower=10; public int HasLeader(){return 1;} public bool HasFollower(){throw new Exception();} }
    public class SecretEnemy
    {
        public int Reads;
        public object effects {get {Reads++;throw new Exception("private effects read");}}
    }
}

internal static class CymantiPublicRuntimeTests
{
    const BindingFlags F=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int count;
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);count++;}
    static Dictionary<string,object> Parse(string json){return BridgeJsonCodec.Object(json);}
    static string Visual(string names,string status="ok",string overall="ok")
    { return "{\"schema\":\"unit-visual-effects-1\",\"status\":\""+overall+"\",\"particles\":{\"status\":\""+status+"\",\"types\":"+names+"}}"; }
    static Dictionary<string,object> Status(object unit,string visual,bool own)
    {
        var output=new JsonObj();
        typeof(BridgeMod).GetMethod("CymantiAppendStatus",F).Invoke(null,new object[]{output,unit,"{\"visualEffects\":"+visual+"}",own});
        return Parse(output.Close());
    }
    static Dictionary<string,object> Links(object unit){return Parse(BridgeMod.CymantiOwnRelations(unit));}
    public static int Main(){try{Run();Console.WriteLine("PASS CymantiPublicRuntimeTests assertions="+count);return 0;}catch(Exception e){Console.WriteLine(e);return 1;}}
    static void Run()
    {
        var unit=new CymantiPublicFixture.Unit();
        foreach(string name in new[]{"Poisoned","Swift","DoubleReady"})
            Check(BridgeMod.CymantiOwnEffect(unit,name)==false,"complete own empty effect list clears "+name);
        unit.effects=new List<CymantiPublicFixture.UnitEffect>{CymantiPublicFixture.UnitEffect.Poisoned,CymantiPublicFixture.UnitEffect.Swift,CymantiPublicFixture.UnitEffect.DoubleReady};
        foreach(string name in new[]{"Poisoned","Swift","DoubleReady"})
            Check(BridgeMod.CymantiOwnEffect(unit,name)==true,"actual own enum positive "+name);
        unit.effects=new List<CymantiPublicFixture.UnitEffect>{CymantiPublicFixture.UnitEffect.Boosted};
        Check(BridgeMod.CymantiOwnEffect(unit,"Swift")==false,"old Boosted is not silently Swift");
        unit.effects=new List<CymantiPublicFixture.UnitEffect>{CymantiPublicFixture.UnitEffect.Frozen};
        Check(BridgeMod.CymantiOwnEffect(unit,"Poisoned")==false && BridgeMod.PolarisOwnFrozen(unit)==true,"Frozen zero and existing boundary remain intact");
        foreach(object bad in new object[]{null,new[]{"Poisoned"},new[]{1},new[]{CymantiPublicFixture.UnrelatedEffect.Poisoned},new[]{(CymantiPublicFixture.UnitEffect)99}})
        { unit.effects=bad;Check(BridgeMod.CymantiOwnEffect(unit,"Poisoned")==null,"malformed/unknown owned enum never proves clear"); }
        Check(BridgeMod.CymantiOwnEffect(unit,"FutureGlow")==null,"unsupported queried effect not guessed");
        Check(BridgeMod.CymantiVisibleEffect(Visual("[\"Poisoned\",\"Swift\"]"),"Poisoned")==true,"actual poison particle is public positive");
        Check(BridgeMod.CymantiVisibleEffect(Visual("[\"Poisoned\",\"Swift\"]"),"Swift")==true,"actual Swift particle is public positive");
        Check(BridgeMod.CymantiVisibleEffect(Visual("[]"),"Poisoned")==false,"complete empty public list is clear");
        Check(BridgeMod.CymantiVisibleEffect(Visual("[\"Boosted\"]"),"Swift")==false,"visual Boosted is not mechanically Swift");
        Check(BridgeMod.CymantiVisibleEffect(Visual("[\"DoubleReady\"]"),"DoubleReady")==null,"enemy mechanical DoubleReady cannot be queried");
        foreach(string bad in new[]{"null","[1]","[\"\"]","[\"Poisoned\",\"Poisoned\"]","[\"Il2Cpp.Poisoned\"]"})
            Check(BridgeMod.CymantiVisibleEffect(Visual(bad),"Poisoned")==null,"malformed list is unknown: "+bad);
        foreach(string bad in new[]{"partial","unavailable","withheld"})
            Check(BridgeMod.CymantiVisibleEffect(Visual("[\"Poisoned\"]",bad),"Poisoned")==null,"incomplete renderer never asserts poison: "+bad);
        Check(BridgeMod.CymantiVisibleEffect(Visual("[]","ok","partial"),"Swift")==false,"unreadable overlay does not invalidate complete particles");
        Check(BridgeMod.CymantiVisibleEffect(Visual("[]","ok","withheld"),"Swift")==null,"withheld visual cannot disclose clear");
        Check(BridgeMod.CymantiPublicEffect("{\"visualEffects\":"+Visual("[\"Poisoned\"]")+"}","Poisoned")==true,"publicInfo wrapper uses same actual cues");
        Check(BridgeMod.CymantiPublicEffect("{}","Swift")==null,"missing publicInfo visual remains unknown");
        var enemy=new CymantiPublicFixture.SecretEnemy();var enemyStatus=Status(enemy,Visual("[\"Poisoned\"]"),false);
        Check(enemy.Reads==0 && (bool)enemyStatus["poisoned"] && !(bool)enemyStatus["swift"],"enemy status reads no private effect list");
        Check(!enemyStatus.ContainsKey("doubleReady") && !enemyStatus.ContainsKey("doubleReadyAuthority"),"no enemy DoubleReady field or authority");
        Check((string)enemyStatus["poisonedAuthority"]=="visible-renderer-Poisoned-particle","enemy poison authority is precise");
        unit.effects=new List<CymantiPublicFixture.UnitEffect>{CymantiPublicFixture.UnitEffect.DoubleReady};var ownStatus=Status(unit,Visual("[]"),true);
        Check((bool)ownStatus["doubleReady"] && (string)ownStatus["doubleReadyAuthority"]=="owned-native-effects","own DoubleReady has native authority");
        var links=Links(unit);
        Check((bool)links["hasLeader"]==false && links["leaderId"]==null && (string)links["status"]=="ok","native absence ignores zero sentinel");
        unit.leader=(uint)9;links=Links(unit);Check(links["leaderId"]==null,"nonzero raw without native presence is not a link");
        unit.Leader=true;unit.Follower=true;unit.follower=(uint)10;links=Links(unit);
        Check((string)links["leaderId"]=="9" && (string)links["followerId"]=="10" && (string)links["status"]=="ok","true native links publish exact IDs");
        Check(links.ContainsKey("leaderRaw") && links.ContainsKey("followerRaw") && links.ContainsKey("passenger"),"old raw/passenger fields remain compatible");
        unit.leader=(uint)0;links=Links(unit);Check((bool)links["hasLeader"] && links["leaderId"]==null && (string)links["status"]=="partial","present but zero link ID is unknown");
        unit.leader=(uint)8;Check(Links(unit)["leaderId"]==null,"self link is not an ID");
        unit.leader="9";Check(Links(unit)["leaderId"]==null,"string wrapper not coerced to native UInt32");
        foreach(object unknown in new object[]{new CymantiPublicFixture.UnknownLinks(),new CymantiPublicFixture.BadLinks()})
        {links=Links(unknown);Check(links["hasLeader"]==null && links["hasFollower"]==null && links["leaderId"]==null && (string)links["status"]=="partial","unreadable presence methods withhold raw identity");}
    }
}
