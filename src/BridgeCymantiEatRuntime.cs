using System.Collections.Generic;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private static List<string> CymantiEatBoundaryUnits(CombatTicket ticket,CombatFrame before,CombatFrame after)
        {
            var ids=new List<string>();
            if(!PolarisContext(ticket,before) || !PolarisContext(ticket,after) || ticket.Action!="attack")return ids;
            CombatUnitView initial,head,oldHead;
            if(!ticket.Before.Units.TryGetValue(ticket.UnitId??"",out initial) || initial.Owner!=before.Me || initial.Eat!=true
                || !before.Units.TryGetValue(initial.Id,out oldHead) || oldHead.Owner!=before.Me
                || !after.Units.TryGetValue(initial.Id,out head) || head.Owner!=after.Me)return ids;
            foreach(var unit in after.Units.Values)
                if(unit.Owner==after.Me && !before.Units.ContainsKey(unit.Id) && !ticket.Before.Units.ContainsKey(unit.Id)
                    && CymantiOwnChainContains(before,after,head,unit.Id))ids.Add(unit.Id);
            return ids;
        }

        private static bool CymantiEatPrimaryKill(CombatTicket ticket)
        {
            var requested=CRUnitAt(ticket.Before,ticket.Coord);
            var old=ticket.NativePrimaryBefore;var now=ticket.NativePrimaryAfter;
            return requested!=null && requested.Owner!=ticket.Before.Me && old!=null && now!=null
                && ticket.NativeAttackTargets.Contains(requested.Id) && ticket.NativeAttackKilledIds.Contains(requested.Id)
                && old.Id==requested.Id && now.Id==requested.Id && old.Owner==requested.Owner && now.Owner==requested.Owner
                && old.Coord==requested.Coord && now.Coord==requested.Coord && old.Health>0 && now.Health==0;
        }

        // Deferred Eat actions need not remain inside their Kill's Execute stack.
        // Bind the executed Eat itself by its exact before/after own linked generation
        // plus the named attack's confirmed native primary kill. Interval presence,
        // an unrelated generated unit, or a later visibility loss are insufficient.
        internal static bool CymantiEatBoundaryMatches(CombatTicket ticket,CombatFrame before,CombatFrame after,object action)
        {
            if(CymantiEatBoundaryUnits(ticket,before,after).Count==0 || !CymantiEatPrimaryKill(ticket))return false;
            string player=SafeScalar(MemberWithAccessor(action,"PlayerId"));
            return player==before.Me || player=="0";
        }

        private void CymantiReconcileEatBoundary(CombatHookPass pass,CombatFrame after)
        {
            if(pass.CandidateKind!="EatAction" || pass.Before==null || after==null)return;
            foreach(var ticket in _combatTickets.Values)
            {
                if(ticket.Stream!=_combatStream)continue;
                var created=CymantiEatBoundaryUnits(ticket,pass.Before,after);
                if(created.Count==0)continue;
                string player=SafeScalar(MemberWithAccessor(pass.Action,"PlayerId"));
                string eaterId=SafeScalar(MemberWithAccessor(pass.Action,"Eater"));
                CombatUnitView eater;
                bool knownOwnEater=eaterId!=null && (pass.Before.Units.TryGetValue(eaterId,out eater) && eater.Owner==after.Me
                    || after.Units.TryGetValue(eaterId,out eater) && eater.Owner==after.Me);
                string coords=SafeScalar(MemberWithAccessor(pass.Action,"Coordinates"));
                bool publicCoords=coords!=null && (pass.Before.PublicTerrains.ContainsKey(coords) || after.PublicTerrains.ContainsKey(coords));
                var generated=new JsonArr();foreach(string id in created)generated.Raw(Quote(id));
                bool matched=CymantiEatBoundaryMatches(ticket,pass.Before,after,pass.Action);
                ticket.Cymanti.EatObservations.Add(new JsonObj().Str("source","executed-EatAction-own-chain-boundary")
                    .NullOrStr("nativeEaterId",knownOwnEater ? eaterId : null).Bool("nativeEaterFieldIsZero",eaterId=="0")
                    .Str("nativePlayerScope",player==after.Me ? "controlled" : player=="0" ? "neutral-zero" : player=="255" ? "nature" : "other-or-unreadable")
                    .NullOrStr("publicCoordinates",publicCoords ? coords : null).Raw("newOwnLinkedUnitIds",generated.Close())
                    .Bool("confirmedPrimaryKill",CymantiEatPrimaryKill(ticket)).Bool("boundaryAssociationConfirmed",matched).Close());
                if(!matched || pass.ProofTickets.Contains(ticket))continue;
                ticket.Cymanti.ReconciledEatRoots.Add(pass.Action);
                pass.ProofTickets.Add(ticket);
                pass.Cause.ActorId=ticket.UnitId;pass.Cause.ActorOwner=after.Me;
                pass.Cause.ActionType="EatAction";pass.Cause.VisibleNativeAction=true;
                if(publicCoords)pass.Cause.TargetCoord=coords;
            }
        }
    }
}
