// Native ice-area receipts and public Frozen display. No hidden terrain or enemy effect-list reads.
using System;
using System.Collections;
using System.Collections.Generic;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal static bool? PolarisOwnFrozen(object unit)
        {
            var effects = ReadNativeList(MemberWithAccessor(unit, "effects"));
            if (effects == null) return null;
            bool frozen = false;
            foreach (object effect in effects)
            {
                if(effect==null || !effect.GetType().IsEnum || effect.GetType().Name!="UnitEffect") return null;
                string name = SafeScalar(effect);
                if (string.IsNullOrEmpty(name)) return null;
                if (name == "Frozen") frozen = true;
            }
            return frozen;
        }

        internal static bool? PolarisVisibleFrozen(string visualJson)
        {
            try
            {
                object particlesRaw, status, typesRaw;
                var visual = BridgeJsonCodec.Object(visualJson);
                object schema,visualStatus;
                if(!visual.TryGetValue("schema",out schema) || !object.Equals(schema,"unit-visual-effects-1")
                    || !visual.TryGetValue("status",out visualStatus)
                    || !(object.Equals(visualStatus,"ok") || object.Equals(visualStatus,"partial")))return null;
                if (!visual.TryGetValue("particles", out particlesRaw)) return null;
                var particles = particlesRaw as Dictionary<string, object>;
                if (particles == null || !particles.TryGetValue("status", out status) || !object.Equals(status, "ok")
                    || !particles.TryGetValue("types", out typesRaw)) return null;
                var types = typesRaw as IList;
                if (types == null) return null;
                bool frozen = false;
                foreach (object name in types) { if (!(name is string)) return null; if ((string)name == "Frozen") frozen = true; }
                return frozen;
            }
            catch { return null; }
        }

        private static bool? PolarisVisibleFrozenFromPublicInfo(string publicInfo)
        {
            try
            {
                object raw;
                if (!BridgeJsonCodec.Object(publicInfo).TryGetValue("visualEffects", out raw)) return null;
                return PolarisVisibleFrozen(BridgeJsonCodec.ParsedValueJson(raw));
            }
            catch { return null; }
        }

        internal sealed class PolarisIceChange
        {
            internal string Kind, Coord, Before, After, Association="native-child-reference";
            internal string Json() { return new JsonObj().Str("kind", Kind).Str("coords", Coord)
                .Str("beforeTerrain", Before).Str("afterTerrain", After).Str("association",Association).Close(); }
        }

        internal sealed class PolarisProof
        {
            internal bool NamedMoveObserved, AutoFreezeMoveObserved;
            internal int ExecutedFreezeAreas,ExecutedBreakAreas;
            internal readonly List<object> FreezeAreas = new List<object>(), BreakAreas = new List<object>();
            internal readonly List<object> FreezeTiles = new List<object>(), BreakTiles = new List<object>(), FreezeUnits = new List<object>();
            internal readonly HashSet<string> ExecutedFreezeTargets = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<PolarisIceChange> IceChanges = new List<PolarisIceChange>();
        }

        private readonly List<CombatHookPass> _polarisActivePasses = new List<CombatHookPass>();

        internal static bool PolarisContext(CombatTicket ticket, CombatFrame frame)
        {
            return ticket != null && ticket.Before != null && ticket.Before.Complete && frame != null && frame.Complete
                && ticket.Accepted != false && ticket.FinalFrame == null && frame.Episode == ticket.Before.Episode
                && frame.Me == ticket.Before.Me && frame.Turn == ticket.Before.Turn;
        }

        internal static bool PolarisMoveMatches(CombatTicket ticket, CombatFrame frame, object action)
        {
            string nativePlayer=SafeScalar(MemberWithAccessor(action,"PlayerId"));
            if (!PolarisContext(ticket, frame) || ticket.Action != "move"
                || (nativePlayer!=frame.Me && nativePlayer!="0")
                || SafeScalar(MemberWithAccessor(action, "UnitId")) != ticket.UnitId
                || SafeScalar(MemberWithAccessor(action,"Reason"))!="Command") return false;
            CombatUnitView actor,initial;
            // Path order/boxing is not an execution guarantee. Bind native actor identity
            // here, then require actual displacement to the requested tile after Execute.
            return frame.Units.TryGetValue(ticket.UnitId ?? "", out actor) && actor.Owner == frame.Me
                && ticket.Before.Units.TryGetValue(ticket.UnitId ?? "",out initial)
                && initial.Owner==frame.Me && initial.Coord!=ticket.Coord;
        }

        private bool PolarisNestedParent(CombatTicket ticket, string kind, object action, CombatFrame frame)
        {
            if (!PolarisContext(ticket, frame) || SafeScalar(MemberWithAccessor(action,"PlayerId")) != frame.Me) return false;
            for (int i=_polarisActivePasses.Count-1;i>=0;i--)
            {
                var parent=_polarisActivePasses[i];
                if (!parent.ProofTickets.Contains(ticket) || parent.Closed) continue;
                if (parent.CandidateKind == "MoveAction" && kind == "FreezeAreaAction")
                {
                    CombatUnitView moved;
                    if (frame.Units.TryGetValue(ticket.UnitId ?? "",out moved) && moved.Owner==frame.Me
                        && moved.AutoFreeze==true && moved.Coord==ticket.Coord
                        && SafeScalar(MemberWithAccessor(action,"Coordinates"))==moved.Coord) return true;
                }
                if ((parent.CandidateKind=="FreezeAreaAction" && (kind=="FreezeTileAction" || kind=="FreezeUnitAction"))
                    || (parent.CandidateKind=="BreakIceAreaAction" && kind=="BreakIceAction"))
                {
                    string center=SafeScalar(MemberWithAccessor(parent.Action,"Coordinates"));
                    int? radius=CRInt(MemberWithAccessor(parent.Action,"Radius"));
                    string target=SafeScalar(MemberWithAccessor(action,kind=="FreezeUnitAction"?"Target":"Coordinates"));
                    if (PolarisInArea(center,target,radius)) return true;
                }
            }
            return false;
        }

        internal static bool PolarisInArea(string center,string target,int? radius)
        {
            int x,y,tx,ty;
            if (!radius.HasValue || radius.Value<0 || !PolarisCoords(center,out x,out y) || !PolarisCoords(target,out tx,out ty)) return false;
            return Math.Abs((long)x-tx)<=radius.Value && Math.Abs((long)y-ty)<=radius.Value;
        }

        private static bool PolarisCoords(string value,out int x,out int y)
        {
            x=y=0;
            if (string.IsNullOrEmpty(value) || value[0]!='(' || value[value.Length-1]!=')') return false;
            string[] fields=value.Substring(1,value.Length-2).Split(',');
            return fields.Length==2 && Int32.TryParse(fields[0],out x) && Int32.TryParse(fields[1],out y);
        }

        private static bool? PolarisHasAbility(object data,string wanted)
        {
            var abilities=ReadNativeList(MemberWithAccessor(data,"unitAbilities"));
            if (abilities==null) return null;
            bool found=false;
            foreach(object ability in abilities)
            {
                string value=SafeScalar(ability);if(string.IsNullOrEmpty(value))return null;
                if(value==wanted)found=true;
            }
            return found;
        }

        private bool PolarisBind(CombatTicket ticket,CombatFrame frame,object action,string kind)
        {
            if (!PolarisContext(ticket,frame) || SafeScalar(MemberWithAccessor(action,"PlayerId"))!=frame.Me) return false;
            var proof=ticket.Polaris;
            if (kind=="MoveAction") return PolarisMoveMatches(ticket,frame,action);
            string coord=SafeScalar(MemberWithAccessor(action,"Coordinates"));
            CombatUnitView actor;
            frame.Units.TryGetValue(ticket.UnitId ?? "",out actor);
            bool ownActor=actor!=null && actor.Owner==frame.Me;
            bool nested=PolarisNestedParent(ticket,kind,action,frame);
            if (kind=="FreezeAreaAction")
            {
                bool matches=CRContainsNative(proof.FreezeAreas,action) || nested || (ownActor && coord==actor.Coord
                    && ((ticket.Action=="freeze_area" && actor.FreezeArea==true && ticket.Coord==coord)
                    || (ticket.Action=="move" && actor.AutoFreeze==true && ticket.Coord==coord && proof.NamedMoveObserved)));
                if (matches && !CRContainsNative(proof.FreezeAreas,action)) proof.FreezeAreas.Add(action);
                return matches;
            }
            if (kind=="BreakIceAreaAction")
            {
                bool matches=ownActor && ticket.Action=="break_ice" && coord==actor.Coord && coord==ticket.Coord;
                if (matches && !CRContainsNative(proof.BreakAreas,action)) proof.BreakAreas.Add(action);
                return matches;
            }
            if (kind=="FreezeTileAction" || kind=="BreakIceAction")
            {
                var children=kind=="FreezeTileAction"?proof.FreezeTiles:proof.BreakTiles;
                bool matches=(CRContainsNative(children,action) || nested) && coord!=null && frame.PublicTerrains.ContainsKey(coord);
                if (matches && !CRContainsNative(children,action)) children.Add(action);
                return matches;
            }
            if (kind=="FreezeUnitAction")
            {
                string origin=SafeScalar(MemberWithAccessor(action,"Origin"));
                string target=SafeScalar(MemberWithAccessor(action,"Target"));
                bool matches=CRContainsNative(proof.FreezeUnits,action) || nested
                    || (ticket.Action=="attack" && ownActor && actor.FreezeAttack==true
                        && origin==actor.Coord && target==ticket.Coord);
                var visible=CRUnitAt(frame,target);
                matches=matches && visible!=null;
                if (matches && !CRContainsNative(proof.FreezeUnits,action)) proof.FreezeUnits.Add(action);
                return matches;
            }
            return false;
        }

        private void PolarisRecord(CombatHookPass pass, CombatFrame after, CombatTicket ticket)
        {
            if (!PolarisContext(ticket,pass.Before) || !PolarisContext(ticket,after)) return;
            var proof=ticket.Polaris;
            if(pass.CandidateKind=="FreezeAreaAction")proof.ExecutedFreezeAreas++;
            if(pass.CandidateKind=="BreakIceAreaAction")proof.ExecutedBreakAreas++;
            if (pass.CandidateKind=="MoveAction" && PolarisMoveMatches(ticket,pass.Before,pass.Action))
            {
                CombatUnitView moved;
                if (after.Units.TryGetValue(ticket.UnitId ?? "",out moved) && moved.Owner==after.Me && moved.Coord==ticket.Coord)
                {
                    proof.NamedMoveObserved=true;
                    proof.AutoFreezeMoveObserved=moved.AutoFreeze==true;
                    if(proof.AutoFreezeMoveObserved)
                        foreach(var tile in after.PublicTerrains)
                        {
                            string old;
                            if(pass.Before.PublicTerrains.TryGetValue(tile.Key,out old)
                                && (old=="Water" || old=="Ocean") && tile.Value=="Ice")
                                PolarisAddIceChange(proof,new PolarisIceChange{Kind="freeze_tile",Coord=tile.Key,Before=old,After="Ice",
                                    Association="own-native-AutoFreeze-move-visible-transition"});
                        }
                }
            }
            if (pass.CandidateKind=="MoveAction" || pass.CandidateKind=="FreezeAreaAction")
                foreach(object child in CRAppendedActions(pass.State,pass.BeforeStackCount,"FreezeAreaAction"))
                    if (!CRContainsNative(proof.FreezeAreas,child)) proof.FreezeAreas.Add(child);
            if (pass.CandidateKind=="FreezeAreaAction")
            {
                foreach(object child in CRAppendedActions(pass.State,pass.BeforeStackCount,"FreezeTileAction"))
                    if (!CRContainsNative(proof.FreezeTiles,child)) proof.FreezeTiles.Add(child);
                foreach(object child in CRAppendedActions(pass.State,pass.BeforeStackCount,"FreezeUnitAction"))
                    if (!CRContainsNative(proof.FreezeUnits,child)) proof.FreezeUnits.Add(child);
            }
            if (pass.CandidateKind=="BreakIceAreaAction")
                foreach(object child in CRAppendedActions(pass.State,pass.BeforeStackCount,"BreakIceAction"))
                    if (!CRContainsNative(proof.BreakTiles,child)) proof.BreakTiles.Add(child);
            if (pass.CandidateKind=="FreezeTileAction" || pass.CandidateKind=="BreakIceAction")
                PolarisRecordIce(ticket,pass.Action,pass.Before,after,pass.CandidateKind);
            if (pass.CandidateKind=="FreezeUnitAction" && CRContainsNative(proof.FreezeUnits,pass.Action))
            {
                var target=CRUnitAt(after,SafeScalar(MemberWithAccessor(pass.Action,"Target")));
                var old=CRUnitAt(pass.Before,SafeScalar(MemberWithAccessor(pass.Action,"Target")));
                if (old!=null && target!=null && target.Id==old.Id && target.Owner==old.Owner)
                    proof.ExecutedFreezeTargets.Add(target.Id);
            }
        }

        internal static bool PolarisRecordIce(CombatTicket ticket,object action,CombatFrame before,CombatFrame after,string kind)
        {
            if (!PolarisContext(ticket,before) || !PolarisContext(ticket,after)
                || SafeScalar(MemberWithAccessor(action,"PlayerId"))!=after.Me) return false;
            bool freeze=kind=="FreezeTileAction";
            if (!freeze && kind!="BreakIceAction") return false;
            var refs=freeze?ticket.Polaris.FreezeTiles:ticket.Polaris.BreakTiles;
            string coord=SafeScalar(MemberWithAccessor(action,"Coordinates")),old,now;
            if (!CRContainsNative(refs,action) || coord==null || !before.PublicTerrains.TryGetValue(coord,out old)
                || !after.PublicTerrains.TryGetValue(coord,out now)
                || !(freeze ? (old=="Water" || old=="Ocean") && now=="Ice"
                            : old=="Ice" && (now=="Water" || now=="Ocean"))) return false;
            PolarisAddIceChange(ticket.Polaris,new PolarisIceChange{Kind=freeze?"freeze_tile":"break_ice",Coord=coord,Before=old,After=now});
            return true;
        }

        private static void PolarisAddIceChange(PolarisProof proof,PolarisIceChange change)
        {
            foreach(var old in proof.IceChanges)
                if(old.Coord==change.Coord && old.Kind==change.Kind && old.Before==change.Before && old.After==change.After)
                {
                    if(change.Association=="native-child-reference")old.Association=change.Association;
                    return;
                }
            proof.IceChanges.Add(change);
        }

        private static string PolarisConfirmation(CombatTicket ticket,CombatFrame current,List<string> affected,out string basis)
        {
            basis=null;
            if (ticket==null || current==null || ticket.Before==null || !current.Complete
                || current.Episode!=ticket.Before.Episode || current.Me!=ticket.Before.Me || current.Turn!=ticket.Before.Turn) return null;
            int broken=0,frozen=0,units=0;
            foreach(var change in ticket.Polaris.IceChanges)
            {
                string now;
                if (!current.PublicTerrains.TryGetValue(change.Coord,out now) || now!=change.After) continue;
                affected.Add(new JsonObj().Str("role","ice_terrain").Raw("effect",change.Json()).Close());
                if (change.Kind=="break_ice") broken++;else frozen++;
            }
            var targetIds=new HashSet<string>(ticket.Polaris.ExecutedFreezeTargets,StringComparer.Ordinal);
            if(ticket.Polaris.AutoFreezeMoveObserved)
                foreach(var unit in current.Units.Values)if(unit.Owner!=current.Me)targetIds.Add(unit.Id);
            foreach(string id in targetIds)
            {
                CombatUnitView old,now;
                if (!ticket.Before.Units.TryGetValue(id,out old) || !current.Units.TryGetValue(id,out now)
                    || old.Owner!=now.Owner || old.Frozen!=false || now.Frozen!=true) continue;
                affected.Add(new JsonObj().Str("role","frozen_unit")
                    .Str("association",ticket.Polaris.ExecutedFreezeTargets.Contains(id)?"executed-native-freeze-child":"own-native-AutoFreeze-move-visible-transition")
                    .Raw("before",old.Json()).Raw("after",now.Json()).Close());units++;
            }
            if (ticket.Action=="break_ice")
            {
                basis=broken>0?"matched own BreakIceAreaAction and exact executed ice children; affected visible ice changed to water, independently of the actor's ground terrain"
                    :"native affected ice children and visible ice-to-water changes are required; actor tile alone is insufficient";
                return broken>0?"confirmed":"unknown";
            }
            if (ticket.Action=="freeze_area" || (ticket.Action=="attack" && ticket.Polaris.FreezeUnits.Count>0))
            {
                basis=frozen+units>0?"matched own native freeze action and executed child; visible water-to-ice or public Frozen false-to-true verified"
                    :"native freeze execution alone does not prove a new visible freeze; require a known target transition";
                return frozen+units>0?"confirmed":"unknown";
            }
            return null; // Move completion remains its named displacement; native ice effects are separately reported.
        }

        private static string PolarisOutcome(CombatTicket ticket,CombatFrame current)
        {
            if(ticket==null || current==null)return "null";
            var proof=ticket.Polaris;
            if(proof.ExecutedFreezeAreas==0 && proof.ExecutedBreakAreas==0 && proof.ExecutedFreezeTargets.Count==0 && !proof.AutoFreezeMoveObserved
                && ticket.Action!="break_ice" && ticket.Action!="freeze_area")return "null";
            var changes=new JsonArr();
            foreach(var change in proof.IceChanges)
            {
                string now;
                if(current.PublicTerrains.TryGetValue(change.Coord,out now) && now==change.After)changes.Raw(change.Json());
            }
            var targets=new JsonArr();
            var targetIds=new HashSet<string>(proof.ExecutedFreezeTargets,StringComparer.Ordinal);
            if(proof.AutoFreezeMoveObserved)
                foreach(var unit in current.Units.Values)if(unit.Owner!=current.Me)targetIds.Add(unit.Id);
            foreach(string id in targetIds)
            {
                CombatUnitView before,after;
                if(ticket.Before.Units.TryGetValue(id,out before) && current.Units.TryGetValue(id,out after)
                    && before.Owner==after.Owner && before.Frozen==false && after.Frozen==true)
                    targets.Raw(new JsonObj().Str("unitId",id).Str("owner",after.Owner).Str("coords",after.Coord)
                        .Str("association",proof.ExecutedFreezeTargets.Contains(id)?"executed-native-freeze-child":"own-native-AutoFreeze-move-visible-transition")
                        .Bool("frozenBefore",false).Bool("frozenAfter",true).Close());
            }
            return new JsonObj().Str("schema","polaris-effects-1")
                .Bool("ownAutoFreezeMoveObserved",proof.AutoFreezeMoveObserved)
                .Raw("executedFreezeAreas",proof.ExecutedFreezeAreas.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Raw("executedBreakIceAreas",proof.ExecutedBreakAreas.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Raw("terrainChanges",changes.Close()).Raw("newlyFrozenUnits",targets.Close())
                .Bool("completeAffectedSet",false)
                .Str("authority","own executed area/direct freeze actions and reference-bound or nested executed children; AutoFreeze may instead be reported as an observed visible transition during the named own native move with that ability, explicitly distinguished from child-reference evidence; known revealed terrain/public Frozen only")
                .Close();
        }
    }
}
