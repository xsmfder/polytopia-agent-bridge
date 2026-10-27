using System;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        // The caller certifies owned/own-unit/currently-visible explored terrain.
        // Disclose only the named public effect, never a foreign tile's effect list.
        internal static bool? CymantiPublicAlgae(object tile, bool disclosed)
        {
            if (!disclosed || tile == null) return null;
            var effects=ReadNativeList(MemberWithAccessor(tile,"effects"));
            if(effects==null)return null;
            bool algae=false;
            foreach(object effect in effects)
            {
                Type type=effect==null?null:effect.GetType();
                if(type==null || !type.IsEnum || type.Name!="EffectType" || type.DeclaringType==null
                    || type.DeclaringType.Name!="TileData" || !Enum.IsDefined(type,effect))return null;
                if(SafeScalar(effect)=="Algae")algae=true;
            }
            return algae;
        }
    }
}
