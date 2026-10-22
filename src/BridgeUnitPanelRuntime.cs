// Player-visible UnitPopup / UnitStatusDisplay projection. Native evidence is tied
// to the same exact GameAssembly as the HP rule. Call only after TileUnitVisible.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private static object URPanelData(object unit, object gs)
        {
            var data = URMember(unit, "UnitData");
            var logic = data == null ? URLogic(gs) : null;
            var kind = logic == null ? null : URMember(unit, "type");
            return data ?? (logic == null || kind == null ? null : InvokeOn(logic, "GetUnitData", new[] { kind }));
        }

        // Preserve successful native null (no passenger) separately from a missing
        // or throwing accessor. Reuse metadata plans, never cache a unit's value.
        private static bool URPanelMember(object target, string name, out object value)
        { return TryReadNativeMember(target, name, out value); }

        private static bool? URPanelStatic(object data)
        {
            var type = FindGameType("UnitDataExtensions") ?? FindTypeBySuffix("UnitDataExtensions");
            var method = type == null ? null : FindStaticUnique(type, "HasAbility", new[] { "UnitData", "Type" }, "Boolean");
            if (method != null && method.GetParameters()[1].ParameterType.IsEnum)
            {
                try { return (bool)method.Invoke(null, new[] { data, Enum.Parse(method.GetParameters()[1].ParameterType, "Static") }); }
                catch { return null; }
            }
            var abilities = ReadNativeList(URMember(data, "unitAbilities"));
            if (abilities == null) return null;
            try
            {
                foreach (var ability in abilities)
                {
                    if (ability == null) return null;
                    if (!ability.GetType().IsEnum) return null;
                    if (SafeScalar(ability) == "Static") return true;
                }
                return false;
            }
            catch { return null; }
        }

        private static void URPanelProgress(object unit, object data, bool verified,
            out int? kills, out bool? veteran, out string killsStatus, out string veteranStatus)
        {
            kills = null; veteran = null; killsStatus = veteranStatus = "unavailable";
            if (!verified) return;
            bool? stationary = URPanelStatic(data);
            if (stationary == true) { killsStatus = veteranStatus = "not-displayed"; return; }
            if (stationary != false) return;
            int? promotion = URPanelInteger(URMember(unit, "promotionLevel"));
            if (!promotion.HasValue || promotion.Value < 0) return;
            veteran = promotion.Value > 0; veteranStatus = "ok";
            if (veteran.Value) { killsStatus = "not-displayed"; return; }
            kills = URPanelInteger(URMember(unit, "xp"));
            if (kills.HasValue && kills.Value >= 0) killsStatus = "ok";
            else kills = null;
        }

        internal static int? URPublicKills(object unit, object gs)
        {
            int? kills; bool? veteran; string ks, vs;
            URPanelProgress(unit, URPanelData(unit, gs), URHPCurrentBuildVerified(), out kills, out veteran, out ks, out vs);
            return kills;
        }

        private void URPanelProgressDetails(object unit, object gs, object data, string killsStatus,
            out string kind, out int? limit, out string target)
        {
            kind = target = null; limit = null;
            if (killsStatus != "ok") return;
            object promotesTo;
            if (URPanelMember(data, "promotesTo", out promotesTo))
            {
                kind = promotesTo == null ? "veteran" : "evolve";
                if (promotesTo != null) target = SafeScalar(URMember(promotesTo, "type"));
            }
            byte owner;
            if (!byte.TryParse(SafeScalar(URMember(unit, "owner")), out owner)) return;
            object player = URPlayer(gs, owner);
            if (player == null)
            {
                var players = ReadNativeList(URMember(gs, "Players"));
                if (players != null) foreach (var candidate in players)
                    if (SafeScalar(URMember(candidate, "Id")) == owner.ToString(CultureInfo.InvariantCulture))
                    { player = candidate; break; }
            }
            if (player == null || data == null) return;
            limit = URPanelInteger(InvokeOn(data, "getPromotionLimit", new[] { player, gs }));
            if (limit.HasValue && limit.Value < 0) limit = null;
        }

        private static decimal? URPanelScaled(object value, decimal divisor)
        {
            int? n = URPanelInteger(value);
            return n.HasValue && n.Value >= 0 ? (decimal?)(n.Value / divisor) : null;
        }

        private static int? URPanelInteger(object value)
        {
            return NativeInt32(value);
        }

        private static object URPanelRule(Dictionary<string, object> rules, string name, object unit, object gs)
        {
            object value;
            return rules != null && rules.TryGetValue(name, out value) ? value : URUnitRule(name, unit, gs);
        }

        private string URPublicInfo(object unit, object gs, object data, bool verified,
            Dictionary<string, object> rules)
        { return URPublicInfoCore(unit, gs, data, verified, rules, false); }

        private string URPublicInfoCore(object unit, object gs, object data, bool verified,
            Dictionary<string, object> rules, bool includeVisual)
        {
            var info = new JsonObj().Str("schema", "unit-public-info-1");
            int? kills; bool? veteran; string ks, vs;
            URPanelProgress(unit, data, verified, out kills, out veteran, out ks, out vs);
            string progressKind, progressTarget; int? progressLimit;
            URPanelProgressDetails(unit, gs, data, ks, out progressKind, out progressLimit, out progressTarget);
            string passengerType = null, ps = "unavailable", homeName = null, hs = "unavailable";
            object passenger;
            if (verified && URPanelMember(unit, "passengerUnit", out passenger))
            {
                if (passenger == null) ps = "none";
                else
                {
                    passengerType = SafeScalar(URMember(passenger, "type"));
                    if (!string.IsNullOrEmpty(passengerType)) ps = "ok";
                }
            }
            if (verified) URPanelHomeName(unit, gs, out homeName, out hs);
            var values = new Dictionary<string, object>();
            object hp = verified ? URMember(unit, "health") : null;
            object max = verified ? URPanelRule(rules, "GetMaxHealth", unit, gs) : null;
            values["health"] = URHPDisplayValue(hp, verified);
            values["maxHealth"] = URHPDisplayValue(max, verified);
            values["attack"] = verified ? URPanelScaled(URMember(data, "attack"), 10m) : null;
            values["defence"] = verified ? URPanelScaled(URMember(data, "defence"), 10m) : null;
            values["range"] = verified ? URPanelScaled(URMember(data, "range"), 1m) : null;
            values["movement"] = verified ? URPanelScaled(URMember(data, "movement"), 1m) : null;
            values["defenceMultiplier"] = verified ? URPanelScaled(URPanelRule(rules, "GetDefenceBonus", unit, gs), 10m) : null;
            int? attack = verified ? URPanelInteger(URPanelRule(rules, "GetAttack", unit, gs)) : null;
            int? baseAttack = verified ? URPanelInteger(URNative("UnitDataExtensions", "GetAttack", new[] { "UnitData" }, new[] { data })) : null;
            values["attackBonus"] = attack.HasValue && attack.Value >= 0 && baseAttack.HasValue && baseAttack.Value >= 0
                ? (object)(Math.Max(0L, (long)attack.Value - baseAttack.Value) / 100m) : null;
            int? movement = verified ? URPanelInteger(URPanelRule(rules, "GetMovement", unit, gs)) : null;
            int? baseMovement = verified ? URPanelInteger(URMember(data, "movement")) : null;
            values["movementBonus"] = movement.HasValue && movement.Value >= 0 && baseMovement.HasValue && baseMovement.Value >= 0
                ? (object)((decimal)movement.Value - baseMovement.Value) : null;
            bool complete = ks != "unavailable" && vs != "unavailable" && ps != "unavailable" && hs != "unavailable";
            if (ks == "ok" && (progressKind == null || !progressLimit.HasValue
                || progressKind == "evolve" && string.IsNullOrEmpty(progressTarget))) complete = false;
            var stats = new JsonObj();
            foreach (var pair in values)
            {
                if (pair.Value == null) complete = false;
                stats.Raw(pair.Key, ResultsNumber(pair.Value));
            }
            // Combat sampling uses the five-argument wrapper above. Pure visual changes
            // must not rotate action stateVersion or flood the combat journal.
            if (includeVisual) info.Raw("visualEffects", URVisibleEffects(unit, true, verified));
            return info.Str("status", !verified ? "unavailable" : complete ? "ok" : "partial")
                .Raw("kills", CRN(kills)).Str("killsStatus", ks)
                .NullOrStr("progressKind", progressKind).Raw("progressLimit", CRN(progressLimit))
                .NullOrStr("progressTarget", progressTarget)
                .NullableBool("veteran", veteran).Str("veteranStatus", vs)
                .NullOrStr("passengerType", passengerType).Str("passengerStatus", ps)
                .NullOrStr("homeCityName", homeName).Str("homeCityStatus", hs)
                .Raw("stats", stats.Close()).Close();
        }

        private static void URPanelHomeName(object unit, object gs, out string name, out string status)
        {
            name = null; status = "unavailable";
            // Exactly the UnitPopup chain, not a search for enemy cities. Only the
            // final displayed name survives; home coordinates never enter JSON.
            object home;
            if (!URPanelMember(unit, "home", out home) || home == null) return;
            object map = URMember(gs, "Map");
            var type = FindGameType("MapDataExtensions") ?? FindTypeBySuffix("MapDataExtensions");
            var get = type == null ? null : FindStaticUnique(type, "GetTile", new[] { "MapData", "WorldCoordinates" }, null);
            if (map == null || get == null) return;
            try
            {
                object tile = get.Invoke(null, new[] { map, home });
                if (tile == null) { status = "none"; return; }
                object improvement;
                if (!URPanelMember(tile, "improvement", out improvement)) return;
                if (improvement == null) { status = "none"; return; }
                string kind = SafeScalar(URMember(improvement, "type"));
                if (kind == null) return;
                if (kind != "City") { status = "none"; return; }
                object rawName;
                if (!URPanelMember(improvement, "name", out rawName)) return;
                name = SafeScalar(rawName);
                status = string.IsNullOrEmpty(name) ? "none" : "ok";
                if (status == "none") name = null;
            }
            catch { name = null; status = "unavailable"; }
        }
    }
}
