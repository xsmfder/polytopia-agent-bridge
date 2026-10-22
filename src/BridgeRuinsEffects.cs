// Executed, own-state receipts for ruins population, units and city rewards.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal sealed class RuinsPopulationReceipt { internal object Action; internal string City; }
        internal sealed class RuinsUnitReceipt { internal object Action; internal string UnitId, UnitJson; }

        internal static bool IsRuinsUnitReward(string reward)
        { return reward == "SuperUnit" || reward == "Battleship" || reward == "Seamonster" || reward == "Swordsman"; }

        private bool CRHasPendingRuinsPopulation(object action)
        {
            foreach (var t in _combatTickets.Values)
                if (t.Stream == _combatStream && CRRuinsEffectChildMatches(t, action, "IncreasePopulationAction")) return true;
            return false;
        }

        private static bool CRRuinsEffectScope(CombatTicket t, CombatFrame frame)
        {
            var p = t == null ? null : t.NativeRuins;
            return p != null && t.Before != null && t.Before.Complete && frame != null && frame.Complete
                && t.Accepted != false && t.FinalFrame == null && IsRuinsAction(t.Action)
                && p.Episode == t.Before.Episode && p.Episode == frame.Episode && p.Me == t.Before.Me && p.Me == frame.Me
                && p.UnitId == t.UnitId && p.Coord == t.Coord && frame.Turn == t.Before.Turn;
        }

        internal static bool CRRuinsEffectChildMatches(CombatTicket t, object action, string kind)
        {
            if (!CRRuinsEffectScope(t, t == null ? null : t.Before) || action == null) return false;
            var p = t.NativeRuins;
            var children = kind == "IncreasePopulationAction" && p.Reward == "PopulationGrowth" ? p.PopulationChildren
                : kind == "TrainAction" && IsRuinsUnitReward(p.Reward) ? p.UnitChildren : null;
            // Do not read pending reward parameters until the exact own child is linked.
            return children != null && CRContainsNative(children, action)
                && SafeScalar(MemberWithAccessor(action, "PlayerId")) == p.Me
                && (kind != "IncreasePopulationAction" || SafeScalar(MemberWithAccessor(action, "Source")) == p.Coord);
        }

        private static bool CRRuinsCityValues(CombatFrame frame, string coord, out int level, out int population)
        {
            level = population = 0;
            string city;
            if (frame == null || coord == null || !frame.OwnImprovements.TryGetValue(coord, out city) || city == null) return false;
            var fields = city.Split(':');
            return fields.Length >= 3 && fields[0] == "City"
                && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out level) && level > 0
                && int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out population) && population >= 0;
        }

        internal static bool CRRecordRuinsPopulation(CombatTicket t, object action, CombatFrame before, CombatFrame after)
        {
            if (!CRRuinsEffectChildMatches(t, action, "IncreasePopulationAction")
                || !CRRuinsEffectScope(t, before) || !CRRuinsEffectScope(t, after)) return false;
            foreach (var receipt in t.NativeRuins.PopulationReceipts) if (CRSameNative(receipt.Action, action)) return false;
            string target = SafeScalar(MemberWithAccessor(action, "Target"));
            int levelBefore, popBefore, levelAfter, popAfter;
            if (!CRRuinsCityValues(before, target, out levelBefore, out popBefore)
                || !CRRuinsCityValues(after, target, out levelAfter, out popAfter)
                || levelBefore != levelAfter || (long)popAfter - popBefore != 1) return false;
            t.NativeRuins.PopulationReceipts.Add(new RuinsPopulationReceipt {Action = action, City = target});
            return true;
        }

        internal static bool CRRecordRuinsUnit(CombatTicket t, object action, CombatFrame before, CombatFrame after)
        {
            if (!CRRuinsEffectChildMatches(t, action, "TrainAction")
                || !CRRuinsEffectScope(t, before) || !CRRuinsEffectScope(t, after)) return false;
            foreach (var receipt in t.NativeRuins.UnitReceipts) if (CRSameNative(receipt.Action, action)) return false;
            string type;
            if (!CRTryAppliedTrainType(action, out type) || CRInt(MemberWithAccessor(action, "Cost")) != 0) return false;
            string coord = SafeScalar(MemberWithAccessor(action, "Coordinates"));
            var unit = CRUnitAt(after, coord);
            if (string.IsNullOrEmpty(type) || unit == null || unit.Owner != before.Me || unit.Type != type
                || string.IsNullOrEmpty(unit.Id) || before.Units.ContainsKey(unit.Id) || t.Before.Units.ContainsKey(unit.Id)) return false;
            foreach (var old in before.Units.Values)
                if (old.Owner == before.Me && (old.PassengerId == unit.Id || unit.PassengerId == old.Id)) return false;
            foreach (var receipt in t.NativeRuins.UnitReceipts) if (receipt.UnitId == unit.Id) return false;
            t.NativeRuins.UnitReceipts.Add(new RuinsUnitReceipt {Action = action, UnitId = unit.Id, UnitJson = unit.Json()});
            return true;
        }

        private static void CRRecordRuinsCity(CombatTicket t, CombatFrame before, CombatFrame after)
        {
            if (!CRRuinsEffectScope(t, before) || !CRRuinsEffectScope(t, after) || t.NativeRuins.Reward != "City") return;
            bool hadCity, hasCity;
            if (!before.CitiesUnderOwnUnits.TryGetValue(t.Coord, out hadCity) || hadCity
                || !after.CitiesUnderOwnUnits.TryGetValue(t.Coord, out hasCity) || !hasCity) return;
            // City appearance is distinct from ownership; do not infer capture or
            // read a neutral city's population/level from raw native state.
            t.NativeRuins.CreatedCity = new JsonObj().Str("coords", t.Coord).Str("type", "City").Close();
        }

        private static string CRRuinsPopulationJson(RuinsObservation p)
        {
            var rows = new List<string>();
            foreach (var receipt in p.PopulationReceipts)
                rows.Add(new JsonObj().Str("city", receipt.City).Raw("amount", "1").Close());
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static string CRRuinsUnitsJson(RuinsObservation p)
        {
            var rows = new List<string>();
            foreach (var receipt in p.UnitReceipts) rows.Add(receipt.UnitJson);
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }
    }
}
