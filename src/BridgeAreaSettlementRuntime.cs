using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal sealed class AreaDamageObservation
        {
            internal object Action;
            internal string ActorId, Animation;
            internal CombatUnitView Before, After;
        }
        private sealed class AreaDamagePass
        {
            internal object State;
            internal CombatTicket Ticket;
            internal int? BeforeCount;
            internal bool Closed;
        }
        private bool _combatAreaHookInstalled;

        // This exact, audited helper only appends native effects. No pending target fields
        // are read: their objects become receipts only when a visible AttackAction executes.
        private void InstallAreaDamageHook()
        {
            var method = FindStaticUnique(FindGameType("ActionUtils"), "AreaDamage",
                new[] { "GameState", "UnitState", "WorldCoordinates" }, "Void");
            if (method == null || HarmonyInstance == null) return;
            try
            {
                _combatAreaHookInstalled = HarmonyInstance.Patch(method, CRHook("AreaDamagePrefix"),
                    CRHook("AreaDamagePostfix"), null, CRHook("AreaDamageFinalizer"), null) != null;
                Log("combat area hook: " + (_combatAreaHookInstalled ? "installed" : "unavailable"));
            }
            catch(Exception ex) { Log("combat area hook failed: " + ex.GetType().Name); }
        }

        internal static bool CRAreaActorMatches(CombatTicket ticket, string owner, string unitId, string center)
        {
            CombatUnitView original;
            return ticket != null && ticket.Before != null && ticket.Before.Complete && ticket.FinalFrame == null
                && ticket.Accepted != false && (ticket.Action == "attack" || ticket.Action == "move" || ticket.Action == "disembark"
                    || ticket.Action == "explode" && ticket.Cymanti.ExplodeRootBound)
                && owner == ticket.Before.Me && center == ticket.Coord && !string.IsNullOrEmpty(unitId)
                && ticket.Before.Units.TryGetValue(ticket.UnitId ?? "", out original) && original.Owner == owner
                && (unitId == original.Id || unitId == original.PassengerId);
        }

        private static void AreaDamagePrefix(object __0, object __1, object __2, out AreaDamagePass __state)
        {
            __state = null;
            var h = _combatHookHost;
            if (h == null || _combatSamplingDepth > 0) return;
            try
            {
                if (!CRSameNative(__0, h.GameStateInstance()) || !h.PrivateSamplingAllowed(__0)) return;
                var identity = h.ResolveControlledIdentity(__0);
                string me = identity.Id.ToString(CultureInfo.InvariantCulture);
                // Do not read an opponent's unit ID, center or pending children.
                if (!identity.Verified || SafeScalar(MemberWithAccessor(__1, "owner")) != me) return;
                string unitId = SafeScalar(MemberWithAccessor(__1, "id")), center = SafeScalar(__2);
                CombatTicket selected = null;
                foreach (var ticket in h._combatTickets.Values)
                    if (ticket.Stream == h._combatStream && ticket.Before != null && ticket.Before.Episode == h.EpisodeId(__0)
                        && CRAreaActorMatches(ticket, me, unitId, center))
                    { if (selected != null) return; selected = ticket; }
                if (selected != null) __state = new AreaDamagePass { State = __0, Ticket = selected, BeforeCount = CRStackCount(__0) };
            }
            catch { }
        }

        private static void AreaDamagePostfix(AreaDamagePass __state) { CloseAreaDamage(__state, false); }
        private static Exception AreaDamageFinalizer(Exception __exception, AreaDamagePass __state)
        { CloseAreaDamage(__state, __exception != null); return __exception; }

        private static void CloseAreaDamage(AreaDamagePass pass, bool failed)
        {
            if (pass == null || pass.Closed) return;
            pass.Closed = true;
            var h = _combatHookHost;
            try
            {
                if (failed || h == null || !h.PrivateSamplingAllowed(pass.State)
                    || pass.Ticket.Accepted == false || pass.Ticket.FinalFrame != null
                    || pass.Ticket.Stream != h._combatStream || pass.Ticket.Before.Episode != h.EpisodeId(pass.State)) return;
                var identity = h.ResolveControlledIdentity(pass.State);
                if (!identity.Verified || identity.Id.ToString(CultureInfo.InvariantCulture) != pass.Ticket.Before.Me) return;
                foreach (var action in CRAppendedActions(pass.State, pass.BeforeCount, "AttackAction"))
                    if (!CRContainsNative(pass.Ticket.NativeAreaActions, action)) pass.Ticket.NativeAreaActions.Add(action);
            }
            catch { }
        }

        internal static bool CRExplosionRootAttackChild(CombatTicket ticket,object action)
        {
            List<object> children;
            return ticket!=null && ticket.Action=="explode" && ticket.Cymanti.ExplodeRootBound
                && ticket.Cymanti.Children.TryGetValue("AttackAction",out children) && CRContainsNative(children,action);
        }

        internal static CombatUnitView CRKnownExplosionActor(CombatTicket ticket,object action,CombatUnitView projected)
        {
            if(ticket==null || ticket.Before==null || !ticket.Before.Complete || ticket.Accepted==false
                || ticket.FinalFrame!=null || ticket.Action!="explode" || !ticket.Cymanti.ExplodeRootBound
                || !(CRContainsNative(ticket.NativeAreaActions,action) || CRExplosionRootAttackChild(ticket,action)))return projected;
            if(projected!=null && projected.Id==ticket.UnitId && projected.Owner==ticket.Before.Me)return projected;
            CombatUnitView original;
            return ticket.Before.Units.TryGetValue(ticket.UnitId??"",out original) && original.Owner==ticket.Before.Me ? original : projected;
        }

        internal static bool CRAreaAttackMatches(CombatTicket ticket, object action, CombatUnitView actor,
            CombatUnitView target, string player, string coord)
        {
            return ticket != null && ticket.Before != null && ticket.Before.Complete && ticket.Accepted != false && ticket.FinalFrame == null
                && CRContainsNative(ticket.NativeAreaActions, action) && player == ticket.Before.Me
                && actor != null && actor.Owner == ticket.Before.Me && target != null
                && !string.IsNullOrEmpty(target.Owner) && target.Owner != ticket.Before.Me && target.Coord == coord;
        }

        internal static bool CRRecordAreaDamage(CombatTicket ticket, object action, CombatFrame before,
            CombatFrame after, CombatCause cause, string coord, string animation)
        {
            if (ticket == null || ticket.Before == null || before == null || after == null
                || !before.Complete || !after.Complete || cause == null || cause.ActionType != "AttackAction"
                || before.Episode != ticket.Before.Episode || after.Episode != ticket.Before.Episode
                || before.Me != ticket.Before.Me || after.Me != ticket.Before.Me) return false;
            CombatUnitView actor, victim, damaged;
            before.Units.TryGetValue(cause.ActorId ?? "", out actor);
            if(actor==null && ticket.Action=="explode" && ticket.Cymanti.ExplodeRootBound
                && CRContainsNative(ticket.NativeAreaActions,action))ticket.Before.Units.TryGetValue(ticket.UnitId??"",out actor);
            before.Units.TryGetValue(cause.TargetId ?? "", out victim);
            if (!CRAreaAttackMatches(ticket, action, actor, victim, cause.ActorOwner, coord)
                || !after.Units.TryGetValue(victim.Id, out damaged) || damaged.Owner != victim.Owner || damaged.Coord != victim.Coord
                || !victim.Health.HasValue || !damaged.Health.HasValue || damaged.Health < 0 || damaged.Health >= victim.Health) return false;
            foreach (var receipt in ticket.NativeAreaDamage) if (CRSameNative(receipt.Action, action)) return false;
            ticket.NativeAreaDamage.Add(new AreaDamageObservation { Action = action, ActorId = actor.Id,
                Animation = animation, Before = victim, After = damaged });
            return true;
        }

        internal static bool CRAreaKillMatches(CombatTicket ticket, CombatUnitView victim)
        {
            if (ticket == null || ticket.Accepted == false || ticket.FinalFrame != null || victim == null || victim.Health != 0) return false;
            foreach (var receipt in ticket.NativeAreaDamage)
                if (receipt.After.Id == victim.Id && receipt.After.Owner == victim.Owner
                    && receipt.After.Coord == victim.Coord && receipt.After.Health == 0) return true;
            return false;
        }

        internal static List<string> CRAreaDamageJson(CombatTicket ticket)
        {
            var rows = new List<string>();
            foreach (var receipt in ticket.NativeAreaDamage)
                rows.Add("{\"role\":" + CRQ(receipt.Animation == "Splash" ? "splash" : receipt.Animation == "Passive" ? "movement_area" : "area_damage")
                    + ",\"source\":\"native_AreaDamage_appended_AttackAction\",\"actorId\":" + CRQ(receipt.ActorId)
                    + ",\"nativeAnimation\":" + CRQ(receipt.Animation)
                    + ",\"death\":" + (ticket.NativeAreaKilledIds.Contains(receipt.After.Id) ? "true" : receipt.After.Health > 0 ? "false" : "null")
                    + ",\"before\":" + receipt.Before.Json() + ",\"after\":" + receipt.After.Json() + "}");
            return rows;
        }
    }
}
