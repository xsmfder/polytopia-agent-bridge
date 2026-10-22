using System;
using System.Reflection;

public enum CityReward { Park }
public enum OtherCityReward { Park }
public static class CityRewardData
{
    public static int Reads, WrongReads;
    public static string GetRewardName(OtherCityReward reward) { WrongReads++; return "wrong"; }
    public static string GetRewardName(CityReward reward) { Reads++; return "native reward"; }
    public static string GetPopupTitle<T>(CityReward reward) { WrongReads++; return "unbound"; }
    public static string GetPopupTitle(CityReward reward) { Reads++; return "native title"; }
}

// Regression for overload selection at the native visibility authority boundary.
internal static class ReflectionBindingTests
{
    public sealed class GameState { }
    public sealed class BackupGameState { }
    public static class NativeRules
    {
        public static int Read(GameState state, int amount) { return amount; }
        public static int Read(BackupGameState state, int amount) { throw new Exception("wrong state"); }
        public static int Read(GameState state, uint amount) { throw new Exception("wrong unsigned argument"); }
        public static int Generic<T>(GameState state) { throw new Exception("unbound generic"); }
        public static int Generic(GameState state) { return 7; }
        public static uint WrongReturn(GameState state) { return 0; }
        public static int RefOnly(ref GameState state) { throw new Exception("reference input"); }
    }
    public sealed class NativeCity
    {
        public bool GetExplored(byte player) { return true; }
        public bool GetExplored(sbyte player) { throw new Exception("wrong signed seat"); }
        public int Query<T>(GameState state) { throw new Exception("unbound generic"); }
        public int Query(GameState state) { return 3; }
        public string Rule(GameState state) { return "current state"; }
        public string Rule(BackupGameState state) { throw new Exception("wrong state"); }
        public int RefOnly(ref GameState state) { throw new Exception("reference input"); }
        public void OutOnly(out GameState state) { state = null; throw new Exception("out input"); }
    }
    public sealed class ActualGameState { }
    public sealed class AlternateGameState { }
    public class AmbiguousTile
    {
        public object GetUnit(AlternateGameState state, byte player, bool hidden) { throw new Exception("wrong overload"); }
        public object GetUnit(ActualGameState state, byte player, bool hidden) { return new object(); }
        public object GetResource(AlternateGameState state, byte player) { throw new Exception("wrong overload"); }
        public object GetResource(ActualGameState state, byte player) { return new object(); }
    }
    public class ExactTile
    {
        public object Result = new object();
        public object GetUnit(ActualGameState state, byte player, bool hidden) { return Result; }
        public object GetResource(ActualGameState state, byte player) { return Result; }
    }
    public sealed class ExtraPrimitiveOverloads : ExactTile
    {
        public object GetUnit(ActualGameState state, sbyte player, bool hidden) { throw new Exception("signed player"); }
        public object GetResource(ActualGameState state, int player) { throw new Exception("integer player"); }
    }
    public sealed class WrongBooleanTile
    {
        public object GetUnit(ActualGameState state, byte player, byte hidden) { throw new Exception(); }
    }
    public sealed class GenericTile : ExactTile
    {
        public object GetUnit<T>(ActualGameState state, byte player, bool hidden) { throw new Exception("unbound generic"); }
        public object GetResource<T>(ActualGameState state, byte player) { throw new Exception("unbound generic"); }
    }
    public sealed class VoidTile
    {
        public void GetUnit(ActualGameState state, byte player, bool hidden) { throw new Exception("not a getter"); }
        public void GetResource(ActualGameState state, byte player) { throw new Exception("not a getter"); }
    }
    private static int passed;
    private static Type bridge;
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static MethodInfo Find(Type tile, bool unit)
    { return (MethodInfo)bridge.GetMethod("FindTileVisibilityMethod",Flags).Invoke(null,new object[] {tile,unit}); }
    private static object Read(string name, object tile, out bool filtered)
    {
        var args=new object[] {tile,new ActualGameState(),(byte)2,false};
        var result=bridge.GetMethod(name,Flags).Invoke(null,args); filtered=(bool)args[3];return result;
    }
    private static void Check(bool value,string label)
    {if(!value){Console.WriteLine("FAIL "+label);throw new Exception(label);}passed++;Console.WriteLine("PASS "+label);}
    public static int Main(string[] args)
    {
        bridge=typeof(PolytopiaAgent.BridgeMod);
        Check(Find(typeof(AmbiguousTile),true)==null,"two compatible unit filters have no authoritative winner");
        Check(Find(typeof(AmbiguousTile),false)==null,"two compatible resource filters have no authoritative winner");
        bool filtered;
        Check(Read("TileUnitVisible",new AmbiguousTile(),out filtered)==null && !filtered,"ambiguous unit is withheld without claiming a successful filter");
        Check(Read("TileResourceVisible",new AmbiguousTile(),out filtered)==null && !filtered,"ambiguous resource is withheld without claiming a successful filter");
        Check(Find(typeof(AmbiguousTile),true)==null && Find(typeof(AmbiguousTile),false)==null,"warm metadata retains the ambiguity rejection");
        var exact=new ExtraPrimitiveOverloads();
        Check(Object.ReferenceEquals(Read("TileUnitVisible",exact,out filtered),exact.Result) && filtered,"inherited byte filter survives unrelated signed-byte overload");
        Check(Object.ReferenceEquals(Read("TileResourceVisible",exact,out filtered),exact.Result) && filtered,"inherited resource filter survives unrelated integer overload");
        Check(Find(typeof(WrongBooleanTile),true)==null,"non-Boolean includeHidden parameter remains unsupported");
        Check(Find(null,true)==null,"absent type remains unavailable");
        var generic=new GenericTile();
        Check(Object.ReferenceEquals(Read("TileUnitVisible",generic,out filtered),generic.Result) && filtered,"open generic overload does not hide callable unit filter");
        Check(Object.ReferenceEquals(Read("TileResourceVisible",generic,out filtered),generic.Result) && filtered,"open generic overload does not hide callable resource filter");
        Check(Read("TileUnitVisible",new VoidTile(),out filtered)==null && !filtered,"void method cannot prove absence of a unit");
        Check(Read("TileResourceVisible",new VoidTile(),out filtered)==null && !filtered,"void method cannot prove absence of a resource");

        var method = PolytopiaAgent.BridgeMod.FindStaticUnique(typeof(NativeRules), "Read", new[] {"GameState", "Int32"}, "Int32");
        Check(method != null && (int)method.Invoke(null, new object[] {new GameState(), 4}) == 4,
            "exact native signature excludes backup state and unsigned argument overloads");
        Check(PolytopiaAgent.BridgeMod.FindStaticUnique(typeof(NativeRules), "WrongReturn", new[] {"GameState"}, "Int32") == null,
            "unsigned return is not a signed native integer by substring");
        method = PolytopiaAgent.BridgeMod.FindStaticUnique(typeof(NativeRules), "Generic", new[] {"GameState"}, "Int32");
        Check(method != null && (int)method.Invoke(null, new object[] {new GameState()}) == 7,
            "unbound generic does not hide the callable static rule");
        Check(PolytopiaAgent.BridgeMod.FindStaticUnique(typeof(NativeRules), "RefOnly", new[] {"GameState"}, "Int32") == null,
            "byref inputs are not native value parameters");
        var inst = typeof(PolytopiaCityOps.CityOptionsSnapshot).GetMethod("Inst", Flags);
        method = (MethodInfo)inst.Invoke(null, new object[] {typeof(NativeCity), "GetExplored", 1, new[] {"Byte"}});
        Check(method != null && (bool)method.Invoke(new NativeCity(), new object[] {(byte)1}),
            "city visibility lookup separates Byte from SByte");
        method = (MethodInfo)inst.Invoke(null, new object[] {typeof(NativeCity), "Rule", 1, new[] {"GameState"}});
        Check(method != null && (string)method.Invoke(new NativeCity(), new object[] {new GameState()}) == "current state",
            "city instance rules use exact parameter names");
        method = (MethodInfo)inst.Invoke(null, new object[] {typeof(NativeCity), "Query", 1, new[] {"GameState"}});
        Check(method != null && (int)method.Invoke(new NativeCity(), new object[] {new GameState()}) == 3,
            "city instance rules ignore unbound generic overloads");
        string seen;
        method = PolytopiaAgent.BridgeMod.FindInstanceMethodUnique(typeof(NativeCity), "Query", new[] {"GameState"}, "Int32", out seen);
        Check(method != null && (int)method.Invoke(new NativeCity(), new object[] {new GameState()}) == 3
            && seen.Contains("Query/1*"), "research instance resolver shares exact callable signature and diagnostics");
        method = PolytopiaAgent.BridgeMod.FindInstanceMethodUnique(typeof(NativeCity), "Rule", new[] {"GameState"}, "String", out seen);
        Check(method != null && (string)method.Invoke(new NativeCity(), new object[] {new GameState()}) == "current state",
            "research instance resolver excludes a state suffix match");
        Check(inst.Invoke(null, new object[] {typeof(NativeCity), "Query", 1, new string[0]}) == null,
            "incomplete parameter schema cannot select an instance method");
        var hint = bridge.GetMethod("HintMethod", Flags);
        method = (MethodInfo)hint.Invoke(null, new object[] {typeof(NativeCity), "Query", new[] {"GameState"}, "Int32"});
        Check(method != null && (int)method.Invoke(new NativeCity(), new object[] {new GameState()}) == 3,
            "visible hints share the same generic exclusion");
        foreach (string name in new[] {"RefOnly", "OutOnly"})
            Check(inst.Invoke(null, new object[] {typeof(NativeCity), name, 1, new[] {"GameState"}}) == null,
                "city value lookup excludes " + name);
        var text = typeof(PolytopiaCityOps.CityOptionsSnapshot).GetMethod("CityRewardText", Flags);
        Check((string)text.Invoke(null, new object[] {"GetRewardName", CityReward.Park}) == "native reward"
            && CityRewardData.Reads == 1 && CityRewardData.WrongReads == 0,
            "reward text resolves actual CityReward before calling any candidate");
        Check((string)text.Invoke(null, new object[] {"GetPopupTitle", CityReward.Park}) == "native title"
            && CityRewardData.Reads == 2 && CityRewardData.WrongReads == 0,
            "reward title ignores an unbound generic candidate");
        Check(text.Invoke(null, new object[] {"GetRewardName", OtherCityReward.Park}) == null
            && CityRewardData.Reads == 2 && CityRewardData.WrongReads == 0,
            "wrong reward enum is rejected before invocation rather than coerced");
        Console.WriteLine("passed="+passed+" failed=0");return 0;
    }
}
