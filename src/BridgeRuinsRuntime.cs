using System;
using System.Reflection;
using System.Collections.Generic;
using System.Globalization;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal sealed class RuinsObservation
        {
            internal string Episode, Me, UnitId, Coord, Reward, Tech;
            internal bool CurrencyChildrenComplete;
            internal readonly List<object> CurrencyChildren = new List<object>();
            internal readonly List<RuinsCurrencyReceipt> CurrencyReceipts = new List<RuinsCurrencyReceipt>();
            internal bool TechChildrenComplete;
            internal readonly List<object> TechChildren = new List<object>();
            internal readonly List<object> AppliedTechChildren = new List<object>();
            internal bool PopulationChildrenComplete, UnitChildrenComplete;
            internal readonly List<object> PopulationChildren = new List<object>(), UnitChildren = new List<object>();
            internal readonly List<RuinsPopulationReceipt> PopulationReceipts = new List<RuinsPopulationReceipt>();
            internal readonly List<RuinsUnitReceipt> UnitReceipts = new List<RuinsUnitReceipt>();
            internal string CreatedCity;
            internal RuinsExplorerTrace Explorer;
        }

        internal sealed class RuinsCurrencyReceipt
        {
            internal object Action;
            internal int Amount;
        }

        // Only a previously bound own child enters the expensive frame sampler.
        // Unrelated income and opponent actions never acquire ruins receipts.
        private bool CRHasPendingRuinsCurrency(object action)
        {
            foreach (var t in _combatTickets.Values)
                if (t.Stream == _combatStream && t.FinalFrame == null && t.Accepted != false
                    && t.NativeRuins != null && CRContainsNative(t.NativeRuins.CurrencyChildren, action)) return true;
            return false;
        }

        private bool CRHasPendingRuinsResearch(object action)
        {
            foreach (var t in _combatTickets.Values)
                if (t.Stream == _combatStream && t.FinalFrame == null && t.Accepted != false
                    && t.NativeRuins != null && CRContainsNative(t.NativeRuins.TechChildren, action)) return true;
            return false;
        }

        internal static bool CRRuinsResearchMatches(CombatTicket t, object action)
        {
            var p = t == null ? null : t.NativeRuins;
            return p != null && t.Before != null && t.FinalFrame == null && t.Accepted != false
                && IsRuinsAction(t.Action) && p.Reward == "FreeTech" && p.Episode == t.Before.Episode
                && p.Me == t.Before.Me && p.UnitId == t.UnitId && p.Coord == t.Coord
                && CRContainsNative(p.TechChildren, action)
                && SafeScalar(MemberWithAccessor(action, "PlayerId")) == p.Me;
        }

        private HashSet<string> CRReadRuinsOwnTechs(object state, CombatFrame frame)
        {
            byte player;
            if (frame == null || !frame.Complete || !byte.TryParse(frame.Me, out player)) return null;
            return ReadPlayerOwnedTechNames(PlayerStateOf(state, player));
        }

        internal static bool CRRecordRuinsResearch(CombatTicket t, object action, CombatFrame before, CombatFrame after,
            HashSet<string> beforeTechs, HashSet<string> afterTechs)
        {
            if (!CRRuinsResearchMatches(t, action) || before == null || after == null || !before.Complete || !after.Complete
                || before.Episode != t.Before.Episode || after.Episode != t.Before.Episode
                || before.Me != t.Before.Me || after.Me != t.Before.Me
                || beforeTechs == null || afterTechs == null || CRContainsNative(t.NativeRuins.AppliedTechChildren, action)) return false;
            // These fields are inspected only after this exact child executed successfully.
            string tech = SafeScalar(MemberWithAccessor(action, "Type"));
            var cost = CRInt(MemberWithAccessor(action, "Cost"));
            if (string.IsNullOrEmpty(t.NativeRuins.Tech) || tech != t.NativeRuins.Tech || cost != 0
                || beforeTechs.Contains(tech) || !afterTechs.Contains(tech)) return false;
            t.NativeRuins.AppliedTechChildren.Add(action);
            return true;
        }

        internal static bool CRRuinsCurrencyMatches(CombatTicket t, object action)
        {
            var p = t == null ? null : t.NativeRuins;
            return p != null && t.Before != null && t.FinalFrame == null && t.Accepted != false
                && IsRuinsAction(t.Action) && p.Reward == "Resources" && p.Episode == t.Before.Episode
                && p.Me == t.Before.Me && p.UnitId == t.UnitId && p.Coord == t.Coord
                && CRContainsNative(p.CurrencyChildren, action)
                && SafeScalar(MemberWithAccessor(action, "PlayerId")) == p.Me
                && SafeScalar(MemberWithAccessor(action, "Source")) == p.Coord;
        }

        internal static bool CRRecordRuinsCurrency(CombatTicket t, object action, CombatFrame before, CombatFrame after)
        {
            if (!CRRuinsCurrencyMatches(t, action) || before == null || after == null || !before.Complete || !after.Complete
                || before.Episode != t.Before.Episode || after.Episode != t.Before.Episode
                || before.Me != t.Before.Me || after.Me != t.Before.Me) return false;
            foreach (var r in t.NativeRuins.CurrencyReceipts) if (CRSameNative(r.Action, action)) return false;
            // Read Amount only after this exact native child executed successfully.
            int a, b; var amount = CRInt(MemberWithAccessor(action, "Amount"));
            if (!amount.HasValue || amount.Value <= 0
                || !int.TryParse(before.Stars, NumberStyles.Integer, CultureInfo.InvariantCulture, out a)
                || !int.TryParse(after.Stars, NumberStyles.Integer, CultureInfo.InvariantCulture, out b)
                || a < 0 || b < 0 || (long)b - a != amount.Value) return false;
            t.NativeRuins.CurrencyReceipts.Add(new RuinsCurrencyReceipt { Action = action, Amount = amount.Value });
            return true;
        }

        internal static bool IsRuinsAction(string action)
        { return action == "examine_ruins" || action == "ruins"; }

        // Read only on a tile occupied by a currently observed own unit. Distinguish
        // an actually absent improvement from a missing/throwing native accessor.
        internal static bool? ReadRuinsOnOwnTile(object tile)
        {
            string type;
            return TryReadOwnTileImprovement(tile, out type) ? (bool?)(type == "Ruin") : null;
        }

        private static bool TryReadOwnTileImprovement(object tile, out string type)
        {
            type = null;
            if (tile == null) return false;
            try
            {
                const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                object improvement;
                var prop = tile.GetType().GetProperty("improvement", f);
                var field = tile.GetType().GetField("improvement", f);
                var getter = tile.GetType().GetMethod("get_improvement", f, null, Type.EmptyTypes, null);
                if (prop != null) improvement = prop.GetValue(tile, null);
                else if (field != null) improvement = field.GetValue(tile);
                else if (getter != null) improvement = getter.Invoke(tile, null);
                else return false;
                if (improvement == null) return true;
                type = SafeScalar(MemberWithAccessor(improvement, "type"));
                return !string.IsNullOrEmpty(type);
            }
            catch { return false; }
        }

        internal static bool CRRuinsActorMatches(CombatTicket ticket, CombatUnitView actor, string player, string coord)
        {
            bool ruin;
            CombatUnitView original;
            return ticket != null && ticket.Before != null && ticket.Before.Complete && IsRuinsAction(ticket.Action)
                && ticket.Accepted != false && ticket.FinalFrame == null && actor != null && actor.Owner == ticket.Before.Me
                && player == ticket.Before.Me && actor.Id == ticket.UnitId && actor.Coord == coord && coord == ticket.Coord
                && ticket.Before.Units.TryGetValue(actor.Id, out original) && original.Owner == ticket.Before.Me && original.Coord == coord
                && ticket.Before.RuinsUnderOwnUnits.TryGetValue(coord, out ruin) && ruin;
        }

        internal static bool CRRecordRuins(CombatTicket ticket, object action, CombatFrame before, CombatFrame after, CombatCause cause)
        {
            if (ticket == null || ticket.NativeRuins != null || ticket.Before == null || before == null || after == null || !before.Complete || !after.Complete
                || before.Episode != ticket.Before.Episode || after.Episode != ticket.Before.Episode
                || before.Me != ticket.Before.Me || after.Me != ticket.Before.Me || cause == null || cause.ActionType != "ExamineRuinsAction") return false;
            CombatUnitView actor; bool hadRuins, hasRuins;
            before.Units.TryGetValue(cause.ActorId ?? "", out actor);
            if (!CRRuinsActorMatches(ticket, actor, cause.ActorOwner, cause.TargetCoord)
                || !before.RuinsUnderOwnUnits.TryGetValue(ticket.Coord, out hadRuins) || !hadRuins
                || !after.RuinsUnderOwnUnits.TryGetValue(ticket.Coord, out hasRuins) || hasRuins) return false;
            // No pending reward is read at prefix time. Only a successful, target-bound
            // Execute followed by observed removal may disclose its native reward selection.
            string reward = SafeScalar(MemberWithAccessor(action, "Reward"));
            string tech = reward == "FreeTech" ? SafeScalar(MemberWithAccessor(MemberWithAccessor(action, "Tech"), "type")) : null;
            ticket.NativeRuins = new RuinsObservation { Episode=before.Episode, Me=before.Me,
                UnitId=actor.Id, Coord=ticket.Coord, Reward=reward, Tech=tech };
            CRRecordRuinsCity(ticket, before, after);
            return true;
        }

        internal static bool CRRuinsConfirmed(CombatTicket ticket, CombatFrame current)
        {
            var p = ticket == null ? null : ticket.NativeRuins;
            return p != null && ticket.Accepted == true && ticket.Before != null && current != null && current.Complete
                && p.Episode == ticket.Before.Episode && p.Episode == current.Episode && p.Me == ticket.Before.Me && p.Me == current.Me
                && IsRuinsAction(ticket.Action) && p.Coord == ticket.Coord && p.UnitId == ticket.UnitId;
        }

        internal static string CRRuinsJson(CombatTicket ticket, CombatFrame current)
        {
            if (!CRRuinsConfirmed(ticket,current)) return "null";
            var p=ticket.NativeRuins;
            long applied = 0;
            foreach (var r in p.CurrencyReceipts) applied += r.Amount;
            bool resources = p.Reward == "Resources";
            bool techApplied = p.Reward == "FreeTech" && p.TechChildrenComplete && p.TechChildren.Count > 0
                && p.AppliedTechChildren.Count == p.TechChildren.Count;
            bool populationApplied = p.Reward == "PopulationGrowth" && p.PopulationChildrenComplete && p.PopulationChildren.Count > 0
                && p.PopulationReceipts.Count == p.PopulationChildren.Count;
            bool unitsApplied = IsRuinsUnitReward(p.Reward) && p.UnitChildrenComplete && p.UnitChildren.Count > 0
                && p.UnitReceipts.Count == p.UnitChildren.Count;
            bool cityApplied = p.Reward == "City" && p.CreatedCity != null;
            bool explorerApplied = p.Reward == "Explorer" && CRRuinsExplorerApplied(p.Explorer);
            bool complete = techApplied || (resources && p.CurrencyChildrenComplete && p.CurrencyChildren.Count > 0
                && p.CurrencyReceipts.Count == p.CurrencyChildren.Count) || populationApplied || unitsApplied || cityApplied || explorerApplied;
            bool population = p.Reward == "PopulationGrowth";
            long visibleAmount = p.Reward == "Explorer" && p.Explorer != null ? p.Explorer.NewlyExplored.Count
                : population ? p.PopulationReceipts.Count : IsRuinsUnitReward(p.Reward) ? p.UnitReceipts.Count : cityApplied ? 1 : applied;
            bool partial = applied > 0 || p.AppliedTechChildren.Count > 0 || p.PopulationReceipts.Count > 0 || p.UnitReceipts.Count > 0
                || (p.Explorer != null && p.Explorer.CompletedExplores.Count > 0);
            return new JsonObj().Bool("ruinConsumed", true).Str("unitId", p.UnitId).Str("coords", p.Coord)
                .NullOrStr("nativeReward", p.Reward).NullOrStr("selectedTech", p.Tech)
                .Str("rewardStatus", complete ? "applied" : partial ? "partially_applied" : p.Reward == null ? "unavailable" : "native_selection_observed")
                .Raw("rewardApplied", complete ? "true" : "null")
                .Raw("rewardAmount", complete && !techApplied ? visibleAmount.ToString(CultureInfo.InvariantCulture) : "null")
                .Raw("observedAppliedAmount", visibleAmount > 0 || explorerApplied ? visibleAmount.ToString(CultureInfo.InvariantCulture) : "null")
                .NullOrStr("rewardUnit", resources ? "stars" : p.Reward == "FreeTech" ? "technology" : population ? "population" : IsRuinsUnitReward(p.Reward) ? "units" : p.Reward == "City" ? "cities" : p.Reward == "Explorer" ? "tiles" : null)
                .NullOrStr("appliedTech", techApplied ? p.Tech : null)
                .Raw("populationGains", CRRuinsPopulationJson(p)).Raw("generatedUnits", CRRuinsUnitsJson(p))
                .Raw("createdCity", p.CreatedCity ?? "null")
                .Raw("exploration", CRRuinsExplorerJson(p.Explorer))
                .Str("authority", "matched own executed ExamineRuinsAction and visible consumption; Resources/FreeTech require exact executed children and own-state changes; PopulationGrowth requires each appended own source-bound population child and +1 on its own city; unit rewards require appended zero-cost nonaborted TrainAction and a new own unit of its actual type; City requires a newly visible city under the own unit, not ownership; Explorer follows exact executed scout/explore children and own GetExplored changes, never queued routes; future rewards remain unconfirmed").Close();
        }
    }
}
