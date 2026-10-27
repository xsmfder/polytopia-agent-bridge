using System;
using PolytopiaAgent;
internal static class RegisteredRuleIdentityTests
{
    public class TechData {public enum Type{Basic=0,Climbing=20}}
    public class UnitData {public enum Type{Warrior=2}}
    public enum UnitEffect{None=0}
    static int count;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
    static void Main()
    {
        var t=typeof(TechData.Type);object value=(TechData.Type)1000;
        Check(BridgeMod.ValidateRegisteredRuleIdentity(t,value,"bridge_probe_tech",value,true)=="1000","registered public rule preserves numeric identity");
        Check(BridgeMod.ValidateRegisteredRuleIdentity(t,value,"bridge_probe_tech",value,false)==null,"untrusted assembly/cache is rejected");
        Check(BridgeMod.ValidateRegisteredRuleIdentity(t,value,"bridge_probe_tech",(TechData.Type)1001,true)==null,"different reverse registration is rejected");
        Check(BridgeMod.ValidateRegisteredRuleIdentity(t,value,"bridge_probe_tech",(UnitData.Type)1000,true)==null,"same number in another enum is rejected");
        Check(BridgeMod.ValidateRegisteredRuleIdentity(t,1000,"bridge_probe_tech",value,true)==null,"raw integer is not a native enum");
        Check(BridgeMod.ValidateRegisteredRuleIdentity(t,value,"1000",value,true)==null,"numeric fallback name is not registration evidence");
        Check(BridgeMod.ValidateRegisteredRuleIdentity(t,value,"",value,true)==null,"empty name remains unknown");
        Check(BridgeMod.ValidateRegisteredRuleIdentity(t,(TechData.Type)999,"name",(TechData.Type)999,true)==null,"undefined built-in range does not become a dynamic rule");
        Check(BridgeMod.ValidateRegisteredRuleIdentity(typeof(UnitEffect),(UnitEffect)1000,"custom_effect",(UnitEffect)1000,true)==null,"public rule support does not relax private status enums");
        string why;
        Check(BridgeMod.EnumNameOfExpected(value,t,out why)==null,"old strict enum reader remains strict");
        Check(BridgeMod.EnumRuleIdentityOfExpected(value,t,out why)==null,"missing real trusted runtime registry stays unknown");
        Check(BridgeMod.EnumRuleIdentityOfExpected(TechData.Type.Climbing,t,out why)=="Climbing","built-in names keep their existing encoding");
        var parse=typeof(BridgeMod).GetMethod("TryNamedNativeEnum",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
        foreach(string input in new[]{"1000","999999","01000","+1000","0","not_registered"})
        {
            object[] args={t,input,null};
            Check(!(bool)parse.Invoke(null,args) && args[2]==null,"command rejects unregistered or noncanonical value "+input);
        }
        object[] builtinArgs={t,"Climbing",null};
        Check((bool)parse.Invoke(null,builtinArgs) && builtinArgs[2].Equals(TechData.Type.Climbing),"command keeps named built-in support");
        Console.WriteLine("RegisteredRuleIdentityTests: "+count+" passed");
    }
}
