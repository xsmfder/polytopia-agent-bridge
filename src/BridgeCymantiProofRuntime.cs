// Executed native Cymanti roots and exact child references. Known visible effects only.
using System;
using System.Collections.Generic;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal sealed class CymantiPoisonObservation
        {
            internal CombatUnitView Before, After;
            internal string Association="executed-native-poison-child";
        }
        internal sealed class CymantiProof
        {
            internal bool SwarmExecuted, ExplodeRootBound, ExplodeExecuted, ExploderKilled;
            internal string ExploderConsumptionSource;
            internal int EatExecutions;
            internal readonly HashSet<string> EatActorIds=new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<string> EatObservations=new List<string>();
            internal readonly List<object> ReconciledEatRoots=new List<object>();
            internal readonly List<object> PoisonSpreadChildren=new List<object>();
            internal readonly List<object> OwnOriginPoisonActions=new List<object>();
            internal readonly Dictionary<string,List<object>> Children=new Dictionary<string,List<object>>(StringComparer.Ordinal);
            internal readonly Dictionary<string,CymantiPoisonObservation> Poison=new Dictionary<string,CymantiPoisonObservation>(StringComparer.Ordinal);
            internal readonly Dictionary<string,CymantiPoisonObservation> ExplosionDamage=new Dictionary<string,CymantiPoisonObservation>(StringComparer.Ordinal);
            internal readonly Dictionary<string,string> ExplosionDamageSource=new Dictionary<string,string>(StringComparer.Ordinal);
            internal readonly Dictionary<string,CombatUnitView> Generated=new Dictionary<string,CombatUnitView>(StringComparer.Ordinal);
            internal readonly Dictionary<string,string> GeneratedAssociation=new Dictionary<string,string>(StringComparer.Ordinal);
            internal readonly List<object> EatTrains=new List<object>();
        }
        private readonly List<CombatHookPass> _cymantiActivePasses=new List<CombatHookPass>();
        private static string[] CymantiChildKinds(string root)
        {
            if(root=="SwarmCommand")return new[]{"BoostOthersAction","BoostAction"};
            if(root=="BoostOthersAction")return new[]{"BoostAction"};
            if(root=="PoisonUnitAction")return new[]{"PoisonUnitAction"};
            if(root=="EatAction")return new[]{"TrainAction"};
            if(root=="KillUnitAction")return new[]{"EatAction"};
            if(root=="InfiltrateAction" || root=="InfiltrationRewardAction")return new[]{"PoisonUnitAction"};
            if(root=="ExplodeUnitAction")return new[]{"AttackAction","PoisonUnitAction","KillUnitAction"};
            return new[]{"PoisonUnitAction","EatAction","KillUnitAction"};
        }

        private static List<object> CymantiChildren(CymantiProof proof,string kind)
        {
            List<object> list;
            if(!proof.Children.TryGetValue(kind,out list)){list=new List<object>();proof.Children[kind]=list;}
            return list;
        }
        private static bool CymantiIsRoot(string kind)
        { return kind=="SwarmCommand" || kind=="BoostOthersAction" || kind=="PoisonUnitAction" || kind=="ExplodeUnitAction" || kind=="AttackAction" || kind=="KillUnitAction" || kind=="EatAction" || kind=="InfiltrateAction" || kind=="InfiltrationRewardAction"; }

        private bool CymantiNested(CombatTicket ticket,object action,string kind)
        {
            for(int i=_cymantiActivePasses.Count-1;i>=0;i--)
            {
                var parent=_cymantiActivePasses[i];
                if(parent.Closed || !parent.ProofTickets.Contains(ticket))continue;
                if(parent.CandidateKind=="SwarmCommand" && (kind=="BoostOthersAction" || kind=="BoostAction"))return true;
                if(parent.CandidateKind=="BoostOthersAction" && kind=="BoostAction")return true;
                if(parent.CandidateKind=="PoisonUnitAction" && kind=="PoisonUnitAction")
                {
                    if(!CRContainsNative(ticket.Cymanti.PoisonSpreadChildren,action))ticket.Cymanti.PoisonSpreadChildren.Add(action);
                    return true;
                }
                if(parent.CandidateKind=="ExplodeUnitAction" && kind=="AttackAction")return true;
                if(parent.CandidateKind=="EatAction" && kind=="TrainAction")return true;
                if((parent.CandidateKind=="AttackAction" || parent.CandidateKind=="KillUnitAction" || parent.CandidateKind=="ExplodeUnitAction" || parent.CandidateKind=="InfiltrateAction" || parent.CandidateKind=="InfiltrationRewardAction")
                    && (kind=="PoisonUnitAction" || kind=="EatAction" || kind=="KillUnitAction"))return true;
            }
            return false;
        }

        private bool CymantiBind(CombatTicket ticket,CombatFrame frame,object action,string kind)
        {
            if(!PolarisContext(ticket,frame))return false;
            string player=SafeScalar(MemberWithAccessor(action,"PlayerId"));
            // A precise own parent can emit neutral children; an explicitly foreign player is rejected.
            if(player!=frame.Me && player!="0")return false;
            var proof=ticket.Cymanti;
            CombatUnitView actor,initial;
            frame.Units.TryGetValue(ticket.UnitId??"",out actor);
            ticket.Before.Units.TryGetValue(ticket.UnitId??"",out initial);
            bool own=initial!=null && initial.Owner==frame.Me;
            string coord=SafeScalar(MemberWithAccessor(action,"Coordinates"));
            if(kind=="SwarmCommand")
                return own && player==frame.Me && ticket.Action=="swarm" && initial.Swarm==true
                    && actor!=null && actor.Owner==frame.Me && actor.Coord==coord && coord==ticket.Coord;
            if(kind=="ExplodeUnitAction")
            {
                bool root=own && player==frame.Me && ticket.Action=="explode" && initial.Explode==true
                    && actor!=null && actor.Owner==frame.Me && actor.Coord==coord && coord==ticket.Coord;
                if(root)proof.ExplodeRootBound=true;
                return root;
            }
            if(kind=="AttackAction")
            {
                var target=CRUnitAt(frame,SafeScalar(MemberWithAccessor(action,"Target")));
                if(ticket.Action=="explode")
                {
                    bool explosionChild=own && initial.Explode==true && proof.ExplodeRootBound
                        && target!=null && (CRExplosionRootAttackChild(ticket,action) || CymantiNested(ticket,action,kind));
                    if(explosionChild && !CRExplosionRootAttackChild(ticket,action))CymantiChildren(proof,kind).Add(action);
                    return explosionChild;
                }
                return own && (initial.PoisonAttack==true || initial.Eat==true || initial.DoubleAttack==true)
                    && CRVisibleAttackActorMatches(ticket,actor,target,player,target==null?null:target.Coord);
            }
            if(kind=="InfiltrateAction")return CRInfiltrationActorMatches(ticket,actor,player,
                SafeScalar(MemberWithAccessor(action,"Target")??MemberWithAccessor(action,"Coordinates")));
            // Current native Exida emits separate Poison actions with its own Origin.
            // Associate that explicit source only after the requested primary hit;
            // this is not an inferred radius or a fabricated child reference.
            if(kind=="PoisonUnitAction" && ticket.Accepted==true && ticket.Dispatched && own
                && ticket.Action=="attack" && initial.PoisonAttack==true && player==frame.Me
                && actor!=null && actor.Owner==frame.Me && actor.Id==initial.Id && actor.Coord==initial.Coord
                && SafeScalar(MemberWithAccessor(action,"Origin"))==actor.Coord
                && CRPrimaryAttackDamageObserved(ticket,CRUnitAt(ticket.Before,ticket.Coord))
                && ticket.NativeAttackTargets.Contains(ticket.NativePrimaryBefore.Id))
            {
                var poisonTarget=CRUnitAt(frame,SafeScalar(MemberWithAccessor(action,"Target")));
                if(poisonTarget!=null && poisonTarget.Owner!=null && poisonTarget.Owner!="0"
                    && poisonTarget.Owner!=frame.Me && poisonTarget.Coord!=ticket.Coord)
                {
                    foreach(var other in _combatTickets.Values)
                        if(!object.ReferenceEquals(other,ticket) && PolarisContext(other,frame)
                            && other.Accepted==true && other.Dispatched && other.Action=="attack" && other.UnitId==ticket.UnitId
                            && CRPrimaryAttackDamageObserved(other,CRUnitAt(other.Before,other.Coord)))return false;
                    if(!CRContainsNative(proof.OwnOriginPoisonActions,action))proof.OwnOriginPoisonActions.Add(action);
                    return true;
                }
            }
            var children=CymantiChildren(proof,kind);
            bool bound=CRContainsNative(children,action) || CymantiNested(ticket,action,kind);
            if(!bound || !own)return false;
            if(kind=="EatAction")
            {
                string eaterId=SafeScalar(MemberWithAccessor(action,"Eater"));
                bound=initial.Eat==true && actor!=null && actor.Owner==frame.Me && ticket.Action=="attack"
                    && (eaterId==initial.Id || CymantiOwnChainContains(ticket.Before,frame,actor,eaterId));
                // An exact own Eat child may append a tail into previously unseen land.
                // Its Coordinates are not disclosed until the new own unit is observed.
            }
            else if(kind=="PoisonUnitAction")
            {
                string target=SafeScalar(MemberWithAccessor(action,"Target"));
                bound=(initial.PoisonAttack==true || ticket.Action=="explode" && initial.Explode==true)
                    && target!=null && CRUnitAt(frame,target)!=null;
            }
            else if(kind=="TrainAction")
                bound=CRContainsNative(proof.EatTrains,action) || CymantiNested(ticket,action,kind);
            else if(kind=="KillUnitAction")bound=ticket.Action=="explode" && coord==initial.Coord
                    || ticket.Action=="attack" && initial.Eat==true && coord==ticket.Coord;
            else if(kind=="BoostOthersAction")bound=ticket.Action=="swarm" && initial.Swarm==true
                    && actor!=null && actor.Owner==frame.Me;
            else if(kind=="BoostAction")bound=ticket.Action=="swarm" && initial.Swarm==true
                    && CRUnitAt(frame,coord)!=null && CRUnitAt(frame,coord).Owner==frame.Me;
            else return false;
            if(bound && !CRContainsNative(children,action))children.Add(action);
            return bound;
        }

        private void CymantiRecord(CombatHookPass pass,CombatFrame after,CombatTicket ticket)
        {
            if(!PolarisContext(ticket,pass.Before) || !PolarisContext(ticket,after))return;
            CombatUnitView original;
            if(!ticket.Before.Units.TryGetValue(ticket.UnitId??"",out original) || original.Owner!=ticket.Before.Me
                || !(original.Swarm==true || original.Explode==true || original.Eat==true || original.PoisonAttack==true))return;
            var proof=ticket.Cymanti;string kind=pass.CandidateKind;
            if(CymantiIsRoot(kind) && !CRContainsNative(proof.ReconciledEatRoots,pass.Action))foreach(string childKind in CymantiChildKinds(kind))
                foreach(object child in CRAppendedActions(pass.State,pass.BeforeStackCount,childKind))
                {
                    var list=CymantiChildren(proof,childKind);
                    if(!CRContainsNative(list,child))list.Add(child);
                    if(kind=="PoisonUnitAction" && childKind=="PoisonUnitAction" && !CRContainsNative(proof.PoisonSpreadChildren,child))proof.PoisonSpreadChildren.Add(child);
                    if(kind=="EatAction" && childKind=="TrainAction" && !CRContainsNative(proof.EatTrains,child))proof.EatTrains.Add(child);
                }
            if(kind=="SwarmCommand")proof.SwarmExecuted=true;
            if(kind=="ExplodeUnitAction")
            {
                proof.ExplodeExecuted=true;
                if(pass.Before.Units.ContainsKey(ticket.UnitId??"") && !after.Units.ContainsKey(ticket.UnitId??""))
                {proof.ExploderKilled=true;proof.ExploderConsumptionSource="executed-ExplodeUnitAction-before-after";}
                foreach(var previous in pass.Before.Units.Values)
                {
                    CombatUnitView now;
                    if(previous.Id!=ticket.UnitId && previous.Health.HasValue
                        && after.Units.TryGetValue(previous.Id,out now) && now.Owner==previous.Owner && now.Coord==previous.Coord
                        && now.Health.HasValue && now.Health.Value<previous.Health.Value)
                    {
                        proof.ExplosionDamage[previous.Id]=new CymantiPoisonObservation{Before=previous,After=now};
                        proof.ExplosionDamageSource[previous.Id]="executed-ExplodeUnitAction-before-after";
                    }
                }
            }
            if(kind=="AttackAction" && ticket.Action=="explode" && CRExplosionRootAttackChild(ticket,pass.Action)
                && !CRContainsNative(ticket.NativeAreaActions,pass.Action))
            {
                var old=CRUnitAt(pass.Before,pass.CandidateCoord);CombatUnitView now;
                if(old!=null && old.Health.HasValue && after.Units.TryGetValue(old.Id,out now)
                    && old.Owner==now.Owner && old.Coord==now.Coord && now.Health.HasValue && now.Health<old.Health)
                {
                    proof.ExplosionDamage[old.Id]=new CymantiPoisonObservation{Before=old,After=now};
                    proof.ExplosionDamageSource[old.Id]="exact-ExplodeUnitAction-AttackAction-child";
                }
            }
            if(kind=="EatAction")
            {
                proof.EatExecutions++;
                string nativeEater=SafeScalar(MemberWithAccessor(pass.Action,"Eater"));
                CombatUnitView nativeEaterUnit;
                if(nativeEater!=null && (pass.Before.Units.TryGetValue(nativeEater,out nativeEaterUnit) && nativeEaterUnit.Owner==after.Me
                    || after.Units.TryGetValue(nativeEater,out nativeEaterUnit) && nativeEaterUnit.Owner==after.Me))proof.EatActorIds.Add(nativeEater);
                CombatUnitView eater;
                if(after.Units.TryGetValue(ticket.UnitId??"",out eater) && eater.Owner==after.Me)
                    foreach(var unit in after.Units.Values)
                        if(unit.Owner==after.Me && !pass.Before.Units.ContainsKey(unit.Id) && !ticket.Before.Units.ContainsKey(unit.Id)
                            && CymantiOwnChainContains(pass.Before,after,eater,unit.Id))
                        {
                            proof.Generated[unit.Id]=unit;
                            proof.GeneratedAssociation[unit.Id]=CRContainsNative(proof.ReconciledEatRoots,pass.Action)
                                ? "executed-EatAction-own-chain-after-confirmed-primary-kill" : "executed-EatAction-reciprocal-own-chain";
                        }
            }
            if(kind=="KillUnitAction" && ticket.Action=="explode" && proof.ExplodeRootBound
                && SafeScalar(MemberWithAccessor(pass.Action,"Coordinates"))==ticket.Coord
                && pass.Before.Units.ContainsKey(ticket.UnitId??"") && !after.Units.ContainsKey(ticket.UnitId??""))
            {proof.ExploderKilled=true;proof.ExploderConsumptionSource="exact-or-nested-executed-KillUnitAction";}
            if(kind=="BoostAction" && ticket.Action=="swarm")
            {
                var old=CRUnitAt(pass.Before,pass.CandidateCoord);var now=CRUnitAt(after,pass.CandidateCoord);
                if(old!=null && now!=null && old.Owner==after.Me && now.Owner==after.Me && old.Id==now.Id
                    && CRBoostEffectAdded(old.Effects,now.Effects,ticket.NativeVersion))ticket.NativeBoostTargets.Add(now.Id);
            }
            if(kind=="PoisonUnitAction")
            {
                string target=SafeScalar(MemberWithAccessor(pass.Action,"Target"));
                var old=CRUnitAt(pass.Before,target);var now=CRUnitAt(after,target);
                if(old!=null && now!=null && old.Id==now.Id && old.Owner==now.Owner)
                {
                    CymantiPoisonObservation record;
                    if(!proof.Poison.TryGetValue(old.Id,out record))proof.Poison[old.Id]=new CymantiPoisonObservation{Before=old,After=now};
                    else record.After=now;
                    if(CRContainsNative(proof.PoisonSpreadChildren,pass.Action))proof.Poison[old.Id].Association="executed-native-poison-spread-child";
                    if(CRContainsNative(proof.OwnOriginPoisonActions,pass.Action))proof.Poison[old.Id].Association="executed-native-PoisonUnitAction-own-Origin-after-matched-primary";
                }
            }
            if(kind=="TrainAction")
            {
                string type;var unit=CRUnitAt(after,pass.CandidateCoord);
                if(unit!=null && unit.Owner==after.Me && !pass.Before.Units.ContainsKey(unit.Id)
                    && !ticket.Before.Units.ContainsKey(unit.Id) && CRTryAppliedTrainType(pass.Action,out type) && type==unit.Type
                    && (CRContainsNative(proof.EatTrains,pass.Action) || CymantiNested(ticket,pass.Action,kind)))
                {proof.Generated[unit.Id]=unit;proof.GeneratedAssociation[unit.Id]="exact-EatAction-TrainAction-child";}
            }
        }

        private static string CymantiLink(string json,string flag,string id)
        {
            try
            {
                object authority,present,value;
                var row=BridgeJsonCodec.Object(json);
                if(!row.TryGetValue("linkAuthority",out authority) || !object.Equals(authority,"owned-native-HasLeader/HasFollower")
                    || !row.TryGetValue(flag,out present) || !object.Equals(present,true) || !row.TryGetValue(id,out value))return null;
                uint parsed;
                return value is string && UInt32.TryParse((string)value,out parsed) && parsed>0 ? (string)value : null;
            }
            catch{return null;}
        }

        // An Eat can append after an existing tail. Walk only disclosed own reciprocal
        // links; an unknown/foreign link or cycle cannot prove a newly generated segment.
        private static bool CymantiOwnChainContains(CombatFrame before,CombatFrame after,CombatUnitView eater,string generatedId)
        {
            var seen=new HashSet<string>(StringComparer.Ordinal);
            var leader=eater;
            while(leader!=null && leader.Owner==after.Me && seen.Add(leader.Id))
            {
                string followerId=CymantiLink(leader.Links,"hasFollower","followerId");
                CombatUnitView follower;
                if(followerId==null || !after.Units.TryGetValue(followerId,out follower)
                    || follower.Owner!=after.Me || CymantiLink(follower.Links,"hasLeader","leaderId")!=leader.Id)return false;
                if(follower.Id==generatedId)return true;
                CombatUnitView previous;
                if(!before.Units.TryGetValue(follower.Id,out previous) || previous.Owner!=after.Me)return false;
                leader=follower;
            }
            return false;
        }

        private static List<string> CymantiAffected(CombatTicket ticket,CombatFrame current)
        {
            var rows=new List<string>();if(current==null)return rows;
            foreach(var entry in ticket.Cymanti.ExplosionDamage)
            {
                var effect=entry.Value;
                rows.Add(new JsonObj().Str("role","explosion_damage_target")
                    .Str("source",ticket.Cymanti.ExplosionDamageSource[entry.Key])
                    .Raw("before",effect.Before.Json()).Raw("after",effect.After.Json()).Close());
            }
            foreach(var entry in ticket.Cymanti.Poison)
            {
                var before=entry.Value.Before;var after=entry.Value.After;CombatUnitView latest;
                if(current.Units.TryGetValue(entry.Key,out latest) && latest.Owner==before.Owner && latest.Coord==before.Coord)after=latest;
                bool? newly=before.Poisoned.HasValue && after.Poisoned.HasValue ? (bool?)(!before.Poisoned.Value && after.Poisoned.Value) : null;
                rows.Add(new JsonObj().Str("role","poison_target").Str("source",entry.Value.Association)
                    .NullableBool("newlyPoisoned",newly).Raw("before",before.Json()).Raw("after",after.Json()).Close());
            }
            foreach(var entry in ticket.Cymanti.Generated)
            {
                CombatUnitView currentUnit;
                if(current.Units.TryGetValue(entry.Key,out currentUnit) && currentUnit.Owner==current.Me)
                    rows.Add(new JsonObj().Str("role","eat_generated_unit").Str("source",ticket.Cymanti.GeneratedAssociation[entry.Key])
                        .Raw("before","null").Raw("after",currentUnit.Json()).Close());
            }
            if(ticket.Dispatched && ticket.Accepted==true && CRInfiltrationConfirmed(ticket,current))
                foreach(string id in ticket.NativeInfiltrationSpawnIds)
                {
                    CombatUnitView unit;
                    if(!ticket.Before.Units.ContainsKey(id) && current.Units.TryGetValue(id,out unit) && unit.Owner==current.Me)
                        rows.Add(new JsonObj().Str("role","infiltration_generated_unit")
                            .Str("source","exact-Infiltrate-InfiltrationReward-TrainAction-chain")
                            .Raw("before","null").Raw("after",unit.Json()).Close());
                }
            return rows;
        }

        private static string CymantiConfirmation(CombatTicket ticket,CombatFrame current,out string basis)
        {
            basis=null;
            if(ticket.Action!="explode")return null;
            if(ticket.Cymanti.ExplodeExecuted && ticket.Cymanti.ExploderKilled && current!=null && !current.Units.ContainsKey(ticket.UnitId??""))
            {basis="named own ExplodeUnitAction executed; original unit consumed at its native execution boundary or by matched KillUnitAction; known public child effects reported separately";return "confirmed";}
            basis="explosion requires named own native root execution and consumption at that boundary or matched kill child; later disappearance alone is insufficient";
            return "unknown";
        }
        private static string CymantiOutcome(CombatTicket ticket,CombatFrame current)
        {
            var proof=ticket.Cymanti;
            if(!proof.SwarmExecuted && !proof.ExplodeExecuted && proof.EatExecutions==0 && proof.Poison.Count==0 && proof.EatObservations.Count==0)return "null";
            var generated=new JsonArr();foreach(var unit in proof.Generated.Values)generated.Raw(unit.Json());
            var eatActors=new JsonArr();foreach(string id in proof.EatActorIds)eatActors.Raw(Quote(id));
            var eatObservations=new JsonArr();foreach(string row in proof.EatObservations)eatObservations.Raw(row);
            var poisoned=new JsonArr();foreach(string row in CymantiAffected(ticket,current))poisoned.Raw(row);
            return new JsonObj().Str("schema","cymanti-effects-1").Bool("swarmExecuted",proof.SwarmExecuted)
                .Bool("explodeExecuted",proof.ExplodeExecuted).Bool("exploderConsumed",proof.ExploderKilled)
                .NullOrStr("exploderConsumptionSource",proof.ExploderConsumptionSource)
                .Raw("eatExecutions",proof.EatExecutions.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Raw("nativeEaterIds",eatActors.Close())
                .Raw("nativeEatObservations",eatObservations.Close())
                .Raw("nativeAreaChildCount",ticket.NativeAreaActions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Raw("knownEffects",poisoned.Close()).Raw("generatedUnits",generated.Close()).Bool("completeAffectedSet",false)
                .Str("authority","executed native roots with own execution-boundary effects or exact appended/nested children; disclosed units only; unknown hidden effects excluded").Close();
        }
    }
}
