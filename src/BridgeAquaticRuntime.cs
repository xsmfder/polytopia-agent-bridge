// Tribe-specific public rule catalogs and owned-tile aquatic effect receipts.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal static Type DeclaredCatalogEnum(Type host, string nested)
        {
            if (host == null) return null;
            if (nested == null) return host.IsEnum ? host : null;
            Type result = host.GetNestedType(nested, BindingFlags.Public | BindingFlags.NonPublic);
            return result != null && result.IsEnum ? result : null;
        }

        private static string NativeEnumCatalog()
        {
            var result = new JsonObj().Str("scope","built-in CLR enum constants only; dynamic rule identifiers are reported by tribeCatalog and native legal menus")
                .Str("registeredRuleIdentifierEncoding","native numeric string, with registeredName and registeredId in tribeCatalog");
            foreach (var item in new[] {
                new[] {"UnitType", "UnitData", "Type"}, new[] {"ResourceType", "ResourceData", "Type"},
                new[] {"ImprovementType", "ImprovementData", "Type"}, new[] {"TechType", "TechData", "Type"},
                new[] {"TerrainType", "TerrainData", "Type"}, new[] {"CityReward", "CityReward", null},
                new[] {"UnitEffect", "UnitEffect", null}, new[] {"TileEffect", "TileData", "EffectType"} })
            {
                Type host = FindType("Il2CppPolytopia.Data." + item[1])
                    ?? FindType("Il2Cpp." + item[1]) ?? FindGameType(item[1]);
                Type type = DeclaredCatalogEnum(host, item[2]);
                var names = new JsonArr();
                if (type != null) foreach (string name in Enum.GetNames(type)) names.Raw(CRQ(name));
                result.Raw(item[0], names.Close())
                    .Raw(item[0] + "_candidates", type == null ? "[]" : new JsonArr().Raw(CRQ(type.FullName)).Close())
                    .Str(item[0] + "_status", type == null ? "unavailable" : "ok");
            }
            return result.Close();
        }

        private static object TribeCatalogCall(object logic, string name, string[] signature, object[] args)
        {
            string seen;
            MethodInfo method = logic == null ? null : FindInstanceMethodUnique(logic.GetType(), name, signature, null, out seen);
            if (method == null) throw new InvalidOperationException("native catalog method unavailable: " + name);
            return method.Invoke(logic, args);
        }

        private static object AddTribeCatalogData(SortedDictionary<string, object> result, object data,
            object logic, object tribe, string kind, bool includeHidden = false)
        {
            if (data == null) throw new InvalidOperationException("null catalog data");
            if (kind != null) data = TribeCatalogCall(logic, "GetOverride", new[] {kind, "TribeData"}, new[] {data, tribe});
            string type = SafeScalar(MemberWithAccessor(data, "type"));
            if (string.IsNullOrEmpty(type)) throw new InvalidOperationException("catalog type unreadable");
            if (kind == "UnitData")
            {
                bool? hidden = ReadBoolMember(data, "hidden");
                if (!hidden.HasValue) throw new InvalidOperationException("unit hidden flag unreadable");
                if (hidden.Value && !includeHidden) return data;
            }
            result[type] = data;
            return data;
        }

        private static string TribeCatalogNames(SortedDictionary<string, object> data)
        {
            var names = new JsonArr();
            foreach (string name in data.Keys) names.Raw(CRQ(name));
            return names.Close();
        }

        // These supplementary rule fields are nullable. A failed optional field must
        // not erase a technology/unit/building roster already established above it.
        private static string CatalogOptionalScalars(object data, string name)
        {
            try
            {
                object value;
                return TryReadNativeMember(data, name, out value) ? URScalars(value) : "null";
            }
            catch { return "null"; }
        }

        private static string CatalogGrowthRule(object data, Dictionary<string, object> targets)
        {
            string status = "unavailable", nextStage = null;
            int? period = null;
            try
            {
                object raw;
                var abilities = TryReadNativeMember(data, "unitAbilities", out raw) ? ReadNativeList(raw) : null;
                if (abilities != null)
                {
                    bool grows = false;
                    foreach (object ability in abilities) if (SafeScalar(ability) == "Grow") grows = true;
                    if (!grows) status = "not_growing";
                    else
                    {
                        object value;
                        int? readPeriod = TryReadNativeMember(data, "growthRate", out value) ? CRInt(value) : null;
                        if (readPeriod.HasValue && readPeriod.Value > 0) period = readPeriod;
                        string type = SafeScalar(MemberWithAccessor(data, "type"));
                        object target;
                        if (type != null && targets != null && targets.TryGetValue(type, out target))
                        {
                            nextStage = SafeScalar(MemberWithAccessor(target, "type"));
                            if (nextStage != null && nextStage != type && period.HasValue) status = "growing";
                        }
                    }
                }
            }
            catch { /* Optional metadata remains unknown, not an empty/false rule. */ }
            return new JsonObj().Str("schema", "unit-growth-rule-1").Str("status", status)
                .NullOrStr("nextStage", nextStage).Raw("growthPeriodTurns", CRN(period))
                .Str("authority", "native UnitData.unitAbilities/growthRate and GameLogicData.GetUnitWillGrowInto; rule period, not remaining turns")
                .Close();
        }

        private static string TribeCatalogRows(SortedDictionary<string, object> data, bool units,
            SortedDictionary<string, string> unlocks = null, Dictionary<string, string> sources = null,
            Dictionary<string, object> growthTargets = null)
        {
            var rows = new JsonArr();
            foreach (var entry in data)
            {
                var row = new JsonObj().Str("type", entry.Key)
                    .NullOrStr("displayName", SafeScalar(MemberWithAccessor(entry.Value, "displayName")))
                    .Raw("baseCost", ResultsNumber(MemberWithAccessor(entry.Value, "cost")));
                AddRegisteredRuleIdentity(row,MemberWithAccessor(entry.Value,"type"));
                string source;
                if (sources == null || !sources.TryGetValue(entry.Key, out source)) source = "tribe-native-rule";
                row.Str("source", source).Str("tribeRuleReachability", source == "tribe-native-rule" ? "native-rule" : "unproven");
                if (units) row.Raw("abilities", CatalogOptionalScalars(entry.Value, "unitAbilities"))
                    .NullableBool("hiddenFromTraining", ReadBoolMember(entry.Value, "hidden"))
                    .Raw("growthRule", CatalogGrowthRule(entry.Value, growthTargets));
                if (!units && unlocks == null)
                {
                    var created = new SortedDictionary<string, object>(StringComparer.Ordinal);
                    foreach (object unit in CatalogCreatedUnits(entry.Value))
                        created[SafeScalar(MemberWithAccessor(unit, "type"))] = unit;
                    row.Raw("createsUnits", TribeCatalogNames(created))
                        .Raw("improvementAbilities", CatalogOptionalScalars(entry.Value, "improvementAbilities"));
                }
                if (unlocks != null) row.Raw("unlocks", unlocks[entry.Key])
                    .Raw("playerAbilities", CatalogOptionalScalars(entry.Value, "abilityUnlocks"));
                rows.Raw(row.Close());
            }
            return rows.Close();
        }

        // The caller supplies only UnitData from its existing disclosed own-unit pass.
        // Observed foreign/conversion units are knowledge, not tribe training rules.
        internal static string ProjectTribeCatalog(object logic, object player, object gs,
            IEnumerable<object> observedOwnedUnitData = null)
        {
            try
            {
                object tribeId = MemberWithAccessor(player, "tribe");
                if (tribeId == null) throw new InvalidOperationException("controlled tribe unavailable");
                object tribe = InvokeOn(logic, "GetTribeData", new[] {tribeId});
                if (tribe == null) throw new InvalidOperationException("tribe rule data unavailable");
                var techs = ReadNativeList(TribeCatalogCall(logic, "GetAllTechForTribe", new[] {"TribeData"}, new[] {tribe}));
                if (techs == null) throw new InvalidOperationException("native tribe technology list unavailable");
                var starting = ReadNativeList(MemberWithAccessor(tribe, "startingTech"));
                if (starting == null) throw new InvalidOperationException("native starting technology list unavailable");
                techs.AddRange(starting);
                var technologies = new SortedDictionary<string, object>(StringComparer.Ordinal);
                var units = new SortedDictionary<string, object>(StringComparer.Ordinal);
                var improvements = new SortedDictionary<string, object>(StringComparer.Ordinal);
                var unlocks = new SortedDictionary<string, string>(StringComparer.Ordinal);
                var growthTargets = new Dictionary<string, object>(StringComparer.Ordinal);
                object overrides = MemberWithAccessor(tribe, "unitOverrides");
                var overrideUnits = URTypedKeys(MemberWithAccessor(overrides, "Values"));
                int? overrideCount = NativeInt32(MemberWithAccessor(overrides, "Count"));
                if (overrideUnits == null || !overrideCount.HasValue || overrideCount.Value != overrideUnits.Count)
                    throw new InvalidOperationException("native tribe unit overrides unavailable or incomplete");
                foreach (object unit in overrideUnits) AddTribeCatalogData(units, unit, logic, tribe, "UnitData", true);
                object startingUnit = MemberWithAccessor(tribe, "startingUnit");
                if (startingUnit != null) AddTribeCatalogData(units, startingUnit, logic, tribe, "UnitData", true);
                foreach (object originalTech in techs)
                {
                    object tech = AddTribeCatalogData(technologies, originalTech, logic, tribe, "TechData");
                    var nativeUnits = ReadNativeList(MemberWithAccessor(tech, "unitUnlocks"));
                    var nativeImprovements = ReadNativeList(TribeCatalogCall(logic, "GetImprovementsFromTech",
                        new[] {"TechData", "TribeData"}, new[] {tech, tribe}));
                    if (nativeUnits == null || nativeImprovements == null) throw new InvalidOperationException("native unlock list unavailable");
                    var techUnits = new SortedDictionary<string, object>(StringComparer.Ordinal);
                    var techBuildings = new SortedDictionary<string, object>(StringComparer.Ordinal);
                    foreach (object unit in nativeUnits)
                    {
                        object resolved = AddTribeCatalogData(units, unit, logic, tribe, "UnitData");
                        if (ReadBoolMember(resolved, "hidden") == false)
                            techUnits[SafeScalar(MemberWithAccessor(resolved, "type"))] = resolved;
                    }
                    foreach (object building in nativeImprovements)
                    {
                        object resolved = AddTribeCatalogData(improvements, building, logic, tribe, "ImprovementData");
                        techBuildings[SafeScalar(MemberWithAccessor(resolved, "type"))] = resolved;
                        foreach (object created in CatalogCreatedUnits(resolved))
                            AddTribeCatalogData(units, created, logic, tribe, "UnitData", true);
                    }
                    unlocks[SafeScalar(MemberWithAccessor(tech, "type"))] = new JsonObj()
                        .Raw("units", TribeCatalogNames(techUnits)).Raw("improvements", TribeCatalogNames(techBuildings)).Close();
                }
                // Follow only native growth rules of units already reachable for this tribe.
                // Hidden generic units remain excluded unless an actual creation/growth rule names them.
                var pending = new Queue<object>(units.Values);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (pending.Count > 0)
                {
                    object unit = pending.Dequeue();
                    string type = SafeScalar(MemberWithAccessor(unit, "type"));
                    if (!visited.Add(type)) continue;
                    if (visited.Count > 256) throw new InvalidOperationException("native growth graph exceeds bound");
                    var abilities = ReadNativeList(MemberWithAccessor(unit, "unitAbilities"));
                    if (abilities == null) throw new InvalidOperationException("native unit abilities unavailable");
                    bool grows = false;
                    foreach (object ability in abilities) if (SafeScalar(ability) == "Grow") grows = true;
                    if (!grows) continue;
                    object next = TribeCatalogCall(logic, "GetUnitWillGrowInto", new[] {"UnitData"}, new[] {unit});
                    growthTargets[type] = next;
                    if (next == null) continue;
                    pending.Enqueue(AddTribeCatalogData(units, next, logic, tribe, "UnitData", true));
                }
                var sources = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string type in units.Keys) sources[type] = "tribe-native-rule";
                string observedStatus = observedOwnedUnitData == null ? "not_requested" : "ok";
                if (observedOwnedUnitData != null)
                {
                    // Never consult the full map/global unit table. The observed data is
                    // not overridden to this tribe: a converted unit retains its rules.
                    var observedPending = new Queue<object>();
                    try
                    {
                        foreach (object data in observedOwnedUnitData)
                        {
                            string type = SafeScalar(MemberWithAccessor(data, "type"));
                            if (data == null || string.IsNullOrEmpty(type)) { observedStatus = "partial"; continue; }
                            if (units.ContainsKey(type)) continue;
                            units[type] = data;
                            sources[type] = "observed-owned-unit";
                            observedPending.Enqueue(data);
                        }
                    }
                    catch { observedStatus = "partial"; }
                    var observedVisited = new HashSet<string>(StringComparer.Ordinal);
                    while (observedPending.Count > 0)
                    {
                        object data = observedPending.Dequeue();
                        string type = SafeScalar(MemberWithAccessor(data, "type"));
                        if (!observedVisited.Add(type)) continue;
                        if (observedVisited.Count > 256) { observedStatus = "partial"; break; }
                        try
                        {
                            object raw;
                            var abilities = TryReadNativeMember(data, "unitAbilities", out raw) ? ReadNativeList(raw) : null;
                            if (abilities == null) { observedStatus = "partial"; continue; }
                            bool grows = false;
                            foreach (object ability in abilities) if (SafeScalar(ability) == "Grow") grows = true;
                            if (!grows) continue;
                            object next = TribeCatalogCall(logic, "GetUnitWillGrowInto", new[] {"UnitData"}, new[] {data});
                            growthTargets[type] = next;
                            if (next == null) continue;
                            string nextType = SafeScalar(MemberWithAccessor(next, "type"));
                            if (string.IsNullOrEmpty(nextType)) { observedStatus = "partial"; continue; }
                            if (units.ContainsKey(nextType)) continue;
                            units[nextType] = next;
                            sources[nextType] = "observed-owned-unit-growth";
                            observedPending.Enqueue(next);
                        }
                        catch { observedStatus = "partial"; }
                    }
                }
                return new JsonObj().Str("schema", "tribe-catalog-1").Str("status", "ok")
                    .NullOrStr("tribe", SafeScalar(tribeId))
                    .Raw("tribeAbilities", CatalogOptionalScalars(tribe, "tribeAbilities"))
                    .Str("observedOwnedUnitDataStatus", observedStatus)
                    .Raw("techs", TribeCatalogRows(technologies, false, unlocks))
                    .Raw("units", TribeCatalogRows(units, true, null, sources, growthTargets))
                    .Raw("improvements", TribeCatalogRows(improvements, false))
                    .Str("scope", "controlled tribe native rules plus explicitly sourced currently observed own units and their native growth rules; observed ownership does not prove tribe training/generation reachability; hidden units marked; not current action legality")
                    .Str("authority", "active GameLogicData.GetAllTechForTribe, GetImprovementsFromTech, GetOverride, ImprovementData.creates.unit, GetUnitWillGrowInto, tribeAbilities, abilityUnlocks, improvementAbilities and caller-filtered owned UnitData")
                    .Close();
            }
            catch (Exception error)
            {
                return new JsonObj().Str("schema", "tribe-catalog-1").Str("status", "unavailable")
                    .Raw("techs", "null").Raw("units", "null").Raw("improvements", "null")
                    .Str("reason", error.GetType().Name + ": " + error.Message).Close();
            }
        }

        private static List<object> CatalogCreatedUnits(object improvement)
        {
            object raw;
            if (!TryReadNativeMember(improvement, "creates", out raw))
                throw new InvalidOperationException("native improvement creations unreadable");
            var result = new List<object>();
            if (raw == null) return result;
            var creates = ReadNativeList(raw);
            if (creates == null) throw new InvalidOperationException("native improvement creations unavailable");
            foreach (object creation in creates)
            {
                object unit;
                if (!TryReadNativeMember(creation, "unit", out unit))
                    throw new InvalidOperationException("native creation unit unreadable");
                if (unit != null) result.Add(unit);
            }
            return result;
        }

        internal static string AquaticTileState(object tile)
        {
            string terrain = SafeScalar(MemberWithAccessor(tile, "terrain"));
            string effects = URScalars(MemberWithAccessor(tile, "effects"));
            if (terrain == null || effects == "null") return null;
            return new JsonObj().Str("terrain", terrain).Raw("effects", effects).Close();
        }

        internal sealed class PublicFloodView
        {
            internal string Terrain;
            internal bool Flooded;
            internal string Json() { return new JsonObj().NullOrStr("terrain", Terrain).Bool("flooded", Flooded).Close(); }
        }

        // Only the rendered Flooded flag is public on visible foreign terrain.
        // Do not publish the foreign tile's remaining mechanical effect list.
        internal static PublicFloodView PublicFloodState(object tile)
        {
            string terrain = SafeScalar(MemberWithAccessor(tile, "terrain"));
            var effects = ReadNativeList(MemberWithAccessor(tile, "effects"));
            if (terrain == null || effects == null) return null;
            bool flooded = false;
            foreach (object effect in effects)
            {
                string name = SafeScalar(effect);
                if (string.IsNullOrEmpty(name)) return null;
                if (name == "Flooded") flooded = true;
            }
            return new PublicFloodView { Terrain = terrain, Flooded = flooded };
        }

        private static bool CRCanalContextMatches(CombatTicket ticket, CombatFrame frame)
        {
            if (ticket == null || frame == null || ticket.Before == null || !frame.Complete || !ticket.Before.Complete
                || ticket.Accepted == false || ticket.FinalFrame != null || ticket.Action != "build" || ticket.Arg != "Canal"
                || frame.Episode != ticket.Before.Episode || frame.Me != ticket.Before.Me || frame.Turn != ticket.Before.Turn
                || !frame.PublicFloodTiles.ContainsKey(ticket.Coord ?? "")) return false;
            CombatUnitView initial, actor;
            return ticket.Before.Units.TryGetValue(ticket.UnitId ?? "", out initial)
                && frame.Units.TryGetValue(ticket.UnitId ?? "", out actor)
                && initial.Owner == frame.Me && actor.Owner == frame.Me
                && initial.Coord == ticket.Coord && actor.Coord == ticket.Coord;
        }

        internal static bool CRCanalActionMatches(CombatTicket ticket, CombatFrame frame, object action)
        {
            return CRCanalContextMatches(ticket, frame)
                && SafeScalar(MemberWithAccessor(action, "Type")) == "Canal"
                && SafeScalar(MemberWithAccessor(action, "PlayerId")) == frame.Me
                && SafeScalar(MemberWithAccessor(action, "Coordinates")) == ticket.Coord;
        }

        internal static bool CRRecordCanal(CombatTicket ticket, object action, CombatFrame before, CombatFrame after)
        {
            if (!CRCanalActionMatches(ticket, before, action) || !CRCanalActionMatches(ticket, after, action)) return false;
            PublicFloodView previous = before.PublicFloodTiles[ticket.Coord], current = after.PublicFloodTiles[ticket.Coord];
            if (previous.Flooded || !current.Flooded) return false;
            ticket.NativeCanalBefore = previous.Json(); ticket.NativeCanalAfter = current.Json();
            ticket.NativeCanalBuildObserved = true;
            return true;
        }

        internal static bool CRRecordCanalChildren(CombatTicket ticket, object action, CombatFrame before,
            CombatFrame after, List<object> children)
        {
            if (!CRCanalActionMatches(ticket, before, action) || !CRCanalActionMatches(ticket, after, action)) return false;
            ticket.NativeCanalBuildObserved = true;
            if (children != null) foreach (object child in children)
                if (child != null && !CRContainsNative(ticket.NativeCanalFloodActions, child)) ticket.NativeCanalFloodActions.Add(child);
            return true;
        }

        internal static bool CRCanalFloodActionMatches(CombatTicket ticket, CombatFrame frame, object action)
        {
            return CRCanalContextMatches(ticket, frame) && ticket.NativeCanalBuildObserved
                && CRContainsNative(ticket.NativeCanalFloodActions, action)
                && SafeScalar(MemberWithAccessor(action, "PlayerId")) == frame.Me
                && SafeScalar(MemberWithAccessor(action, "Coordinates")) == ticket.Coord;
        }

        internal static bool CRRecordCanalFlood(CombatTicket ticket, object action, CombatFrame before, CombatFrame after)
        {
            if (!CRCanalFloodActionMatches(ticket, before, action) || !CRCanalFloodActionMatches(ticket, after, action)) return false;
            PublicFloodView previous = before.PublicFloodTiles[ticket.Coord], current = after.PublicFloodTiles[ticket.Coord];
            if (previous.Flooded || !current.Flooded) return false;
            ticket.NativeCanalBefore = previous.Json(); ticket.NativeCanalAfter = current.Json();
            return true;
        }

        private static string CRCanalOutcome(CombatTicket ticket)
        {
            if (ticket.Action != "build" || ticket.Arg != "Canal") return "null";
            return new JsonObj().Str("status", ticket.NativeCanalAfter != null ? "flood-executed" : ticket.NativeCanalBuildObserved ? "build-observed-flood-unconfirmed" : "unobserved")
                .Bool("nativeBuildObserved", ticket.NativeCanalBuildObserved)
                .Raw("matchedFloodChildren", ticket.NativeCanalFloodActions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Raw("before", ticket.NativeCanalBefore ?? "null").Raw("after", ticket.NativeCanalAfter ?? "null")
                .Str("authority", "own Canal BuildAction and reference-matched appended FloodTileAction; public terrain before and after").Close();
        }

        internal static bool CRClearFloodActionMatches(CombatTicket ticket, CombatFrame frame, object action)
        {
            if (ticket == null || frame == null || ticket.Before == null || !frame.Complete || !ticket.Before.Complete
                || ticket.Accepted == false || ticket.FinalFrame != null || ticket.Action != "clear_tile_effect" || ticket.Arg != "Flooded"
                || frame.Episode != ticket.Before.Episode || frame.Me != ticket.Before.Me || frame.Turn != ticket.Before.Turn)
                return false;
            CombatUnitView initial, actor;
            PublicFloodView requested;
            return ticket.Before.Units.TryGetValue(ticket.UnitId ?? "", out initial)
                && frame.Units.TryGetValue(ticket.UnitId ?? "", out actor)
                && initial.Owner == frame.Me && actor.Owner == frame.Me
                && initial.Coord == ticket.Coord && actor.Coord == ticket.Coord
                && ticket.Before.PublicFloodTiles.TryGetValue(ticket.Coord ?? "", out requested) && requested.Flooded
                && frame.PublicFloodTiles.ContainsKey(ticket.Coord ?? "")
                && SafeScalar(MemberWithAccessor(action,"PlayerId")) == frame.Me
                && SafeScalar(MemberWithAccessor(action,"Target")) == ticket.Coord
                && SafeScalar(MemberWithAccessor(action,"Effect")) == "Flooded";
        }

        internal static bool CRRecordClearFlood(CombatTicket ticket, object action, CombatFrame before, CombatFrame after)
        {
            if (ticket == null || ticket.NativeClearFloodAction == null || !CRSameNative(ticket.NativeClearFloodAction,action)
                || !CRClearFloodActionMatches(ticket,before,action) || !CRClearFloodActionMatches(ticket,after,action)) return false;
            var previous=before.PublicFloodTiles[ticket.Coord]; var current=after.PublicFloodTiles[ticket.Coord];
            if (!previous.Flooded || current.Flooded || previous.Terrain != current.Terrain) return false;
            ticket.NativeClearFloodBefore=previous.Json(); ticket.NativeClearFloodAfter=current.Json();
            return true;
        }

        internal static bool CRBindClearFloodAction(CombatTicket ticket, CombatFrame frame, object action)
        {
            if (!CRClearFloodActionMatches(ticket,frame,action)) return false;
            if (ticket.NativeClearFloodAction != null && !CRSameNative(ticket.NativeClearFloodAction,action)) return false;
            ticket.NativeClearFloodAction=action;
            return true;
        }

        private static string CRClearTileEffectOutcome(CombatTicket ticket)
        {
            if (ticket.Action != "clear_tile_effect") return "null";
            return new JsonObj().NullOrStr("effect",ticket.Arg)
                .Str("status",ticket.NativeClearFloodAfter != null ? "cleared" : "unconfirmed")
                .Bool("nativeActionObserved",ticket.NativeClearFloodAction != null)
                .Raw("before",ticket.NativeClearFloodBefore??"null").Raw("after",ticket.NativeClearFloodAfter??"null")
                .Str("authority","matched own ClearTileEffectAction Target/Effect and own unit position; public Flooded true-to-false state, no foreign effect list").Close();
        }

        internal static bool CRFloodActionMatches(CombatTicket ticket, CombatFrame frame, object action)
        {
            if (ticket == null || frame == null || ticket.Before == null || !frame.Complete || !ticket.Before.Complete
                || ticket.Accepted == false || ticket.FinalFrame != null || ticket.Action != "flood"
                || ticket.Before.Episode != frame.Episode || ticket.Before.Me != frame.Me || ticket.Before.Turn != frame.Turn
                || !frame.OwnTileStates.ContainsKey(ticket.Coord ?? "")) return false;
            return SafeScalar(MemberWithAccessor(action, "PlayerId")) == frame.Me
                && SafeScalar(MemberWithAccessor(action, "Coordinates")) == ticket.Coord;
        }

        internal static bool CRRecordFlood(CombatTicket ticket, object action, CombatFrame before, CombatFrame after)
        {
            if (!CRFloodActionMatches(ticket, before, action) || !CRFloodActionMatches(ticket, after, action)) return false;
            string oldState, newState;
            if (!before.OwnTileStates.TryGetValue(ticket.Coord, out oldState)
                || !after.OwnTileStates.TryGetValue(ticket.Coord, out newState)
                || oldState == null || newState == null || oldState == newState) return false;
            ticket.NativeFloodBefore = oldState; ticket.NativeFloodAfter = newState;
            return true;
        }
    }
}
