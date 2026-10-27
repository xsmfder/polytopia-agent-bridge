using System;
using System.Collections;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal sealed class CymantiAlgaeBuildProof
        {
            internal object Root;
            internal bool Executed;
            internal string Before;
        }
        private static bool? CymantiOwnedAlgae(CombatFrame frame,string coords)
        {
            string raw;
            if(frame==null || !frame.Complete || !frame.OwnTileStates.TryGetValue(coords??"",out raw))return null;
            try
            {
                object rawEffects;
                if(!BridgeJsonCodec.Object(raw).TryGetValue("effects",out rawEffects))return null;
                var effects=rawEffects as IList;if(effects==null)return null;
                bool algae=false;
                foreach(object effect in effects)
                {
                    var name=effect as string;if(!CymantiEffectIdentifier(name))return null;
                    if(name=="Algae")algae=true;
                }
                return algae;
            }
            catch{return null;}
        }
        private static bool CymantiAlgaeBuildContext(CombatTicket ticket,CombatFrame frame)
        {
            return PolarisContext(ticket,frame) && ticket.Action=="build" && ticket.Arg=="Algae"
                && ticket.Coord!=null && CymantiOwnedAlgae(ticket.Before,ticket.Coord)==false;
        }
        private static bool CymantiAlgaeBuildAction(CombatTicket ticket,CombatFrame frame,object action)
        {
            if(action==null || action.GetType().Name!="BuildAction"
                || SafeScalar(MemberWithAccessor(action,"PlayerId"))!=frame.Me
                || SafeScalar(MemberWithAccessor(action,"Coordinates"))!=ticket.Coord)return false;
            object kind=MemberWithAccessor(action,"Type");Type type=kind==null?null:kind.GetType();
            return type!=null && type.IsEnum && type.Name=="Type" && type.DeclaringType!=null
                && type.DeclaringType.Name=="ImprovementData" && Enum.IsDefined(type,kind) && SafeScalar(kind)=="Algae";
        }
        internal static bool CymantiAlgaeBuildBind(CombatTicket ticket,CombatFrame frame,object action,string kind)
        {
            if(kind!="BuildAction" || !CymantiAlgaeBuildContext(ticket,frame)
                || CymantiOwnedAlgae(frame,ticket.Coord)!=false || !CymantiAlgaeBuildAction(ticket,frame,action))return false;
            var proof=ticket.CymantiAlgaeBuild;
            if(proof.Root!=null && !CRSameNative(proof.Root,action))return false;
            proof.Root=action;proof.Before=frame.OwnTileStates[ticket.Coord];return true;
        }
        internal static bool CymantiAlgaeBuildRecord(CombatTicket ticket,CombatFrame before,object action)
        {
            if(!CymantiAlgaeBuildContext(ticket,before) || !CRSameNative(ticket.CymantiAlgaeBuild.Root,action)
                || !CymantiAlgaeBuildAction(ticket,before,action))return false;
            ticket.CymantiAlgaeBuild.Executed=true;return true;
        }
        internal static string CymantiAlgaeBuildConfirmation(CombatTicket ticket,CombatFrame current,out string basis)
        {
            basis=null;if(ticket==null || ticket.Action!="build" || ticket.Arg!="Algae")return null;
            basis="Algae requires the exact executed own BuildAction(Type=Algae) and the named owned tile's native Algae false-to-true effect; resource or star changes alone are insufficient";
            return ticket.Before!=null && ticket.Before.Complete && current!=null && current.Complete
                && current.Me==ticket.Before.Me && current.Episode==ticket.Before.Episode && current.Turn==ticket.Before.Turn
                && CymantiOwnedAlgae(ticket.Before,ticket.Coord)==false && ticket.Dispatched && ticket.Accepted==true
                && ticket.CymantiAlgaeBuild.Executed && ticket.CymantiAlgaeBuild.Before!=null
                && CymantiOwnedAlgae(current,ticket.Coord)==true ? "confirmed" : "unknown";
        }
        internal static string CymantiAlgaeBuildOutcome(CombatTicket ticket,CombatFrame current)
        {
            string basis;string confirmation=CymantiAlgaeBuildConfirmation(ticket,current,out basis);
            if(confirmation==null)return "null";
            return new JsonObj().Str("schema","cymanti-algae-build-1").Str("coords",ticket.Coord)
                .Bool("nativeBuildExecuted",ticket.CymantiAlgaeBuild.Executed).Str("effectConfirmation",confirmation)
                .Raw("before",ticket.CymantiAlgaeBuild.Before??"null")
                .Raw("after",confirmation=="confirmed" ? current.OwnTileStates[ticket.Coord] : "null")
                .Bool("completeAffectedSet",false).Str("authority",basis).Close();
        }
    }
}
