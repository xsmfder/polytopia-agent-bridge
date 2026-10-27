// Native Elyrion effect attribution. No hidden resource/map access or inferred child identity.
using System;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private static bool CREnchantContext(CombatTicket ticket, CombatFrame frame)
        {
            return ticket != null && ticket.Before != null && ticket.Before.Complete
                && frame != null && frame.Complete && ticket.Accepted != false && ticket.FinalFrame == null
                && ticket.Action == "build" && ticket.Arg == "EnchantAnimal" && ticket.Coord != null
                && frame.Episode == ticket.Before.Episode && frame.Me == ticket.Before.Me
                && frame.Turn == ticket.Before.Turn;
        }

        internal static bool CREnchantBuildMatches(CombatTicket ticket, CombatFrame frame, object action)
        {
            string resource;
            return CREnchantContext(ticket, frame)
                && ticket.Before.OwnResources.TryGetValue(ticket.Coord, out resource) && resource == "Game"
                && SafeScalar(MemberWithAccessor(action, "PlayerId")) == frame.Me
                && SafeScalar(MemberWithAccessor(action, "Type")) == "EnchantAnimal"
                && SafeScalar(MemberWithAccessor(action, "Coordinates")) == ticket.Coord;
        }

        internal static bool CRRecordEnchantedUnit(CombatTicket ticket, object action, CombatFrame before, CombatFrame after)
        {
            string oldResource, resource, nativeType;
            if (!CREnchantContext(ticket, before) || !CREnchantContext(ticket, after)
                || !ticket.NativeEnchantBuildObserved || !CRContainsNative(ticket.NativeEnchantTrains, action)
                || SafeScalar(MemberWithAccessor(action, "PlayerId")) != after.Me
                || SafeScalar(MemberWithAccessor(action, "Coordinates")) != ticket.Coord
                || !CRTryAppliedTrainType(action, out nativeType)
                || !ticket.Before.OwnResources.TryGetValue(ticket.Coord, out oldResource) || oldResource != "Game"
                || !after.OwnResources.TryGetValue(ticket.Coord, out resource) || resource != "none") return false;
            CombatUnitView created = CRUnitAt(after, ticket.Coord);
            if (created == null || created.Owner != after.Me || created.Type != nativeType
                || before.Units.ContainsKey(created.Id) || ticket.Before.Units.ContainsKey(created.Id)) return false;
            // CR snapshots construct fresh value records; retaining this record freezes executed proof.
            ticket.NativeEnchantedUnit = created;
            return true;
        }

        internal static bool CREnchantmentSettled(CombatTicket ticket, CombatFrame current)
        {
            if (ticket == null || current == null || ticket.Before == null || !current.Complete
                || current.Episode != ticket.Before.Episode || current.Me != ticket.Before.Me
                || current.Turn != ticket.Before.Turn || ticket.NativeEnchantedUnit == null) return false;
            CombatUnitView now; string resource;
            var proof = ticket.NativeEnchantedUnit;
            return current.Units.TryGetValue(proof.Id, out now) && now.Owner == proof.Owner
                && now.Type == proof.Type && now.Coord == proof.Coord
                && current.OwnResources.TryGetValue(ticket.Coord, out resource) && resource == "none";
        }

        internal static bool CRAttractObserved(CombatFrame before, CombatFrame after, object action)
        {
            if (before == null || after == null || !before.Complete || !after.Complete
                || before.Episode != after.Episode || before.Me != after.Me
                || SafeScalar(MemberWithAccessor(action, "Reason")) != "Attract"
                || SafeScalar(MemberWithAccessor(action, "Type")) != "Game") return false;
            string coord = SafeScalar(MemberWithAccessor(action, "Coordinates")), old, now;
            return coord != null && before.OwnResources.TryGetValue(coord, out old) && old == "none"
                && after.OwnResources.TryGetValue(coord, out now) && now == "Game";
        }
    }
}
