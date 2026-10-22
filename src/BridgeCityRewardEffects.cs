// Causal city reward receipts. Pending native objects are private links, never observations.
using System;
using System.Collections.Generic;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal sealed class CityRewardObservation
        {
            internal object Root;
            internal bool ChildrenReadable = true;
            internal readonly List<object> Children = new List<object>();
            internal readonly List<object> Completed = new List<object>();
            internal readonly List<string> Effects = new List<string>();
        }

        private static bool CRCityRewardScope(CombatTicket t, CombatFrame frame)
        {
            return t != null && t.Before != null && t.Before.Complete && frame != null && frame.Complete
                && t.Accepted != false && t.FinalFrame == null && (t.Action == "city_reward" || t.Action == "reward")
                && frame.Episode == t.Before.Episode && frame.Me == t.Before.Me && frame.Turn == t.Before.Turn;
        }

        internal static bool CRCityRewardRootMatches(CombatTicket t, CombatFrame frame, object action)
        {
            if (!CRCityRewardScope(t, frame) || t.NativeCityReward != null || action == null
                || string.IsNullOrEmpty(t.Coord) || string.IsNullOrEmpty(t.Arg)) return false;
            string city;
            // OwnImprovements includes noncities; membership alone is insufficient.
            if (!t.Before.OwnImprovements.TryGetValue(t.Coord, out city) || city == null || !city.StartsWith("City:", StringComparison.Ordinal)
                || !frame.OwnImprovements.TryGetValue(t.Coord, out city) || city == null || !city.StartsWith("City:", StringComparison.Ordinal)) return false;
            return SafeScalar(MemberWithAccessor(action, "PlayerId")) == t.Before.Me
                && SafeScalar(MemberWithAccessor(action, "Coordinates")) == t.Coord
                && string.Equals(SafeScalar(MemberWithAccessor(action, "Reward")), t.Arg, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool CRCityRewardChildMatches(CombatTicket t, CombatFrame frame, object action)
        {
            var p = t == null ? null : t.NativeCityReward;
            return p != null && CRCityRewardScope(t, frame) && action != null
                && CRContainsNative(p.Children, action) && !CRContainsNative(p.Completed, action)
                && SafeScalar(MemberWithAccessor(action, "PlayerId")) == t.Before.Me;
        }

        internal CombatTicket CRUniqueCityRewardTicket(CombatFrame frame, object action)
        {
            CombatTicket match = null;
            foreach (var t in _combatTickets.Values)
                if (t.Stream == _combatStream && CRCityRewardRootMatches(t, frame, action))
                { if (match != null) return null; match = t; }
            return match;
        }

        internal static void CRCityRewardAppend(CityRewardObservation p, List<object> children, bool readable)
        {
            p.ChildrenReadable &= readable;
            foreach (var child in children)
            {
                if (child == null || CRSameNative(child, p.Root) || CRContainsNative(p.Completed, child))
                { p.ChildrenReadable = false; continue; }
                if (!CRContainsNative(p.Children, child)) p.Children.Add(child);
            }
        }

        private void CRRecordCityReward(CombatHookPass pass, CombatFrame after, CombatTicket t)
        {
            if (t.Stream != _combatStream || !CRCityRewardScope(t, after)) return;
            if (pass.CandidateKind == "CityRewardAction")
            {
                if (!CRCityRewardRootMatches(t, pass.Before, pass.Action)) return;
                t.NativeCityReward = new CityRewardObservation { Root = pass.Action };
            }
            else if (!CRRecordCityRewardChild(t, pass.Action, pass.CandidateKind, pass.CandidateCoord, pass.Before, after)) return;
            List<object> children;
            bool readable = CRTryAppendedActions(pass.State, pass.BeforeStackCount, "ActionBase", out children);
            CRCityRewardAppend(t.NativeCityReward, children, readable);
        }

        internal static bool CRRecordCityRewardChild(CombatTicket t, object action, string kind, string coord, CombatFrame before, CombatFrame after)
        {
            if (!CRCityRewardChildMatches(t, before, action) || !CRCityRewardScope(t, after)) return false;
            var p = t.NativeCityReward;
            p.Completed.Add(action);
            // Only changes to this child's own actor/coordinate are attributed. Other
            // visible changes during Execute remain journal observations, not causal proof.
            CombatUnitView old = null, next = null;
            if (kind == "MoveAction")
            {
                string id = SafeScalar(MemberWithAccessor(action, "UnitId"));
                if (id != null) { before.Units.TryGetValue(id, out old); after.Units.TryGetValue(id, out next); }
                if (old != null && next != null && old.Owner == before.Me && next.Owner == before.Me && old.Coord != next.Coord)
                    p.Effects.Add(CRCityRewardEffect(SafeScalar(MemberWithAccessor(action, "Reason")) == "Push" ? "push" : "move", kind, old, next));
            }
            else if (kind == "TrainAction")
            {
                next = CRUnitAt(after, coord);
                string nativeType;
                if (next != null && next.Owner == before.Me && !string.IsNullOrEmpty(next.Id)
                    && !before.Units.ContainsKey(next.Id) && CRTryAppliedTrainType(action, out nativeType)
                    && next.Type == nativeType)
                {
                    bool conversion = false;
                    foreach (var u in before.Units.Values)
                        if (CRCarrierIdentityTransition(before, after, u, next)) conversion = true;
                    if (!conversion) p.Effects.Add(CRCityRewardEffect("generated", kind, null, next));
                }
            }
            else if (kind == "EmbarkAction" || kind == "DisembarkAction")
            {
                old = CRUnitAt(before, coord); next = CRUnitAt(after, coord);
                if (CRCarrierIdentityTransition(before, after, old, next))
                    p.Effects.Add(CRCityRewardEffect(kind == "EmbarkAction" ? "embark" : "disembark", kind, old, next));
            }
            else if (kind == "KillUnitAction")
            {
                old = CRUnitAt(before, coord);
                if (old != null && old.Owner == before.Me && !after.Units.ContainsKey(old.Id))
                    p.Effects.Add(CRCityRewardEffect("removed", kind, old, null));
            }
            return true;
        }

        private static string CRCityRewardEffect(string role, string kind, CombatUnitView before, CombatUnitView after)
        {
            return "{\"role\":" + CRQ(role) + ",\"source\":\"city_reward\",\"nativeAction\":" + CRQ(kind)
                + ",\"before\":" + (before == null ? "null" : before.Json()) + ",\"after\":" + (after == null ? "null" : after.Json()) + "}";
        }

        internal static string CRCityRewardJson(CombatTicket t, CombatFrame current)
        {
            var p = t == null ? null : t.NativeCityReward;
            if (p == null || t.Accepted != true || current == null || !current.Complete
                || current.Episode != t.Before.Episode || current.Me != t.Before.Me) return "null";
            return "{\"coords\":" + CRQ(t.Coord) + ",\"reward\":" + CRQ(t.Arg)
                + ",\"nativeExecuted\":true,\"observedUnitEffects\":[" + string.Join(",", p.Effects.ToArray())
                + "],\"childLinksReadable\":" + (p.ChildrenReadable ? "true" : "false")
                + ",\"coverage\":\"partial; exact appended native children observed at execution; unhooked, unreadable or unassociated effects remain unknown\"}";
        }
    }
}
