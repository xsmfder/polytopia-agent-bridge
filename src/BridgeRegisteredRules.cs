using System;
using System.Globalization;
using System.Reflection;
namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private static bool PublicRuleEnum(Type type)
        {
            if(type==null || !type.IsEnum || type.Name!="Type" || type.DeclaringType==null)return false;
            string owner=type.DeclaringType.Name;
            return owner=="TechData" || owner=="UnitData" || owner=="ImprovementData"
                || owner=="ResourceData" || owner=="TerrainData";
        }
        // Pure identity seam. Actual authority additionally requires the trusted native
        // EnumCache<T>'s current GetName/GetType round trip, never arbitrary numeric parsing.
        internal static string ValidateRegisteredRuleIdentity(Type expected,object value,string name,object roundTrip,bool trusted)
        {
            if(!trusted || !PublicRuleEnum(expected) || value==null || value.GetType()!=expected
                || roundTrip==null || roundTrip.GetType()!=expected || !value.Equals(roundTrip)
                || string.IsNullOrWhiteSpace(name))return null;
            try
            {
                long number=Convert.ToInt64(value,CultureInfo.InvariantCulture);
                long numericName;
                if(number<1000 || number>Int32.MaxValue || Int64.TryParse(name,out numericName))return null;
                return number.ToString(CultureInfo.InvariantCulture);
            }
            catch{return null;}
        }
        private static string RegisteredRuleName(object value,Type expected)
        {
            if(!PublicRuleEnum(expected) || value==null || value.GetType()!=expected
                || !IsGameInteropAssemblyName(expected.Assembly.GetName().Name))return null;
            try
            {
                var definition=FindGameType("EnumCache`1");
                if(definition==null || !definition.IsGenericTypeDefinition
                    || !IsGameInteropAssemblyName(definition.Assembly.GetName().Name))return null;
                var cache=definition.MakeGenericType(expected);
                const BindingFlags flags=BindingFlags.Public|BindingFlags.Static;
                var getName=cache.GetMethod("GetName",flags,null,new[]{expected},null);
                var getType=cache.GetMethod("GetType",flags,null,new[]{typeof(string)},null);
                if(getName==null || getName.ReturnType!=typeof(string) || getType==null || getType.ReturnType!=expected)return null;
                string name=getName.Invoke(null,new[]{value}) as string;
                if(string.IsNullOrWhiteSpace(name))return null;
                var back=getType.Invoke(null,new object[]{name});
                return ValidateRegisteredRuleIdentity(expected,value,name,back,true)==null ? null : name;
            }
            catch{return null;}
        }
        internal static string EnumRuleIdentityOfExpected(object value,Type expected,out string why)
        {
            string builtin=EnumNameOfExpected(value,expected,out why);
            if(builtin!=null)return builtin;
            if(RegisteredRuleName(value,expected)==null)return null;
            why=null;
            // Keep the native numeric identifier canonical across observation and commands.
            return Convert.ToInt64(value,CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }
        private static bool TryRegisteredRuleValue(Type expected,string encoded,out object value)
        {
            value=null;
            int number;
            if(!PublicRuleEnum(expected) || !Int32.TryParse(encoded,NumberStyles.None,CultureInfo.InvariantCulture,out number)
                || number<1000 || number.ToString(CultureInfo.InvariantCulture)!=encoded)return false;
            object candidate=Enum.ToObject(expected,number);
            if(RegisteredRuleName(candidate,expected)==null)return false;
            value=candidate;return true;
        }
        private static bool KnownRuleEnumValue(Type expected,object value)
        {string why;return EnumRuleIdentityOfExpected(value,expected,out why)!=null;}
        private static void AddRegisteredRuleIdentity(JsonObj row,object value)
        {
            if(value==null || !value.GetType().IsEnum || Enum.IsDefined(value.GetType(),value))return;
            string name=RegisteredRuleName(value,value.GetType());
            if(name==null)return;
            row.Str("registeredName",name)
                .Raw("registeredId",Convert.ToInt64(value,CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture))
                .Str("identityAuthority","native EnumCache<T> name/type round-trip; exact native rule enum and trusted game assembly");
        }
    }
}
