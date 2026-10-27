// BattlePreviewSnapshot - parameterless entry point for the PUBLIC pre-attack preview.
//
// Why parameterless: the bridge `call` channel can invoke a static method by (type, method)
// with no object arguments, so this file needs NO new channel:
//
//     call  type="PolytopiaBattlePreview.BattlePreviewSnapshot"  method="Snapshot"  audit="1"
//
// Everything it needs already exists in this assembly and is REUSED, not re-derived:
//   BridgeMod.FindGameType / FindStaticUnique          - the same unambiguous lookups the bridge uses
//   BridgeMod.TileUnitVisible(tile, gs, player, false) - the same per-tile visibility filter the
//                                                        world observation uses (fog + invisible)
//   UnitDataExtensions.GetRange(UnitState, GameState) -> Int32          (the UI's own range)
//   UnitDataExtensions.GetAttackOptions(UnitState, GameState, Int32 range, Boolean) -> List<WorldCoordinates>
//                                                        (the UI highlight chain)
//   BattleHelpers.GetBattleResults(GameState, UnitState attacker, UnitState defender,
//                                  Boolean accountForPeace, Boolean simpleData) -> BattleResults
//       BattleResults.attackDamage / .retaliationDamage / .shouldMoveToDefeatedEnemyTile
//
// This is the "how much would this attack do" number the PLAYER SEES in the attack menu, plus the
// two derived lethal tags (the player sees the damage and the resulting health bars).
//
// Fail-closed rules kept from the previous readers:
//   * a unit that already attacked / cannot attack gets an EMPTY target list (the UI would not
//     highlight anything) - an illegal attack is never presented as an option;
//   * a defender is resolved through the game's own visibility call, never the raw tile field;
//   * a number that cannot be read is null, never 0, and it raises an explicit reason code;
//   * the preview sweep restores BridgeMod.UnitFilter counters, so reading a preview can never
//     change what the standard observation reports about its own visibility filter.
//
// Read-only: no game state is mutated and no command is issued.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using PolytopiaAgent;

namespace PolytopiaBattlePreview
{
    public static class BattlePreviewSnapshot
    {
        public const string Schema = "attack-previews-3";

        /// <summary>Explicit accessor spellings only, exactly like BridgeMod.MemberWithAccessor.</summary>
        private static object M(object o, string clrName)
        {
            object value;
            return BridgeMod.TryReadNativeMember(o, clrName, out value) ? value : null;
        }

        /// <summary>No-argument bool read (method, then field, then property).</summary>
        private static bool? MB(object o, string clrName)
        { return BridgeMod.ReadNativeBooleanMember(o, clrName); }

        /// <summary>Integers only; a bool, string or unreadable value is null (never 0).</summary>
        private static int? MI(object o, string clrName)
        {
            return BridgeMod.NativeInt32(M(o, clrName));
        }

        /// <summary>Provably-safe scalar: primitives/enums/strings, and WorldCoordinates as "(x,y)".</summary>
        private static string S(object v)
        {
            if (v == null) { return null; }
            var t = v.GetType();
            if (t.IsPrimitive || t.IsEnum || t == typeof(string)) { return v.ToString(); }
            if (t.Name == "WorldCoordinates" || t.FullName == "WorldCoordinates"
                || t.FullName == "Il2Cpp.WorldCoordinates")
            {
                var x = S(M(v, "x"));
                var y = S(M(v, "y"));
                if (x == null || y == null) { return null; }
                return "(" + x + "," + y + ")";
            }
            return null;
        }

        private static bool ValidCoords(string c)
        {
            return !string.IsNullOrEmpty(c) && c != "null" && c.Length >= 3
                && c[0] == '(' && c[c.Length - 1] == ')';
        }

        private static string Q(string s)
        {
            if (s == null) { return "null"; }
            var b = new StringBuilder("\"");
            foreach (char ch in s)
            {
                if (ch == '"' || ch == '\\') { b.Append('\\').Append(ch); }
                else if (ch < ' ') { b.Append("\\u").Append(((int)ch).ToString("x4")); }
                else { b.Append(ch); }
            }
            return b.Append("\"").ToString();
        }

        private static string Un(string reason)
        {
            return "{\"schema\":" + Q(Schema) + ",\"status\":\"unavailable\",\"reasonCodes\":["
                 + Q(reason) + "],\"attackers\":[],\"counts\":{\"myUnits\":0,\"targets\":0}}";
        }

        private static List<object> AsList(object raw)
        {
            return BridgeMod.ReadNativeList(raw);
        }

        /// <summary>Short exception name: unwraps TargetInvocationException to the real cause.</summary>
        private static string ExName(Exception ex)
        {
            if (ex == null) { return "unknown"; }
            if (ex is TargetInvocationException && ex.InnerException != null) { ex = ex.InnerException; }
            return ex.GetType().Name;
        }

        private static object StaticMember(Type t, string name)
        {
            object value;
            return BridgeMod.TryReadNativeStaticMember(t, name, out value) ? value : null;
        }

        private static bool? Ability(object unit, string name)
        {
            var method = BridgeMod.FindStaticUnique(BridgeMod.FindGameType("UnitDataExtensions"), "HasAbility", new[] { "UnitState", "Type" }, "Boolean");
            if (method == null) return null;
            try { var e = method.GetParameters()[1].ParameterType; if (!Enum.IsDefined(e, name)) return null; return (bool)method.Invoke(null, new[] { unit, Enum.Parse(e, name) }); } catch { return null; }
        }

        private static bool Revealed(object tile, byte me)
        {
            if (tile == null) return false;
            try { var m = tile.GetType().GetMethod("GetExplored", new[] { typeof(byte) }); return m != null && (bool)m.Invoke(tile, new object[] { me }); } catch { return false; }
        }

        private static object ClonePreviewUnit(object original, object coordinates)
        {
            // Native clone creates an independent UnitState. Never write to the live state.
            try
            {
                var t = original.GetType(); var copy = Activator.CreateInstance(t);
                var clone = t.GetMethod("cloneState", new[] { t });
                if (clone == null) return null;
                clone.Invoke(copy, new[] { original });
                // Native cloneState copies transition fields, not a complete unit: in this
                // build it omits owner/type/HP/promotion/XP and UnitData. HasAbility reads
                // UnitData directly. Fill these fields on the independent preview object;
                // never call UnitState.Create(GameState), which can allocate a live unit ID.
                var required = new[] { "owner", "type", "health", "promotionLevel", "xp", "UnitData" };
                foreach (var name in required)
                    if (!CopyPreviewMember(original, copy, name, true)) return null;
                foreach (var name in new[] { "leader", "birthClimate", "birthClimateSkinType",
                    "previousTurnEndCoordinates", "createdTurn", "isTemporaryExplorerUnit" })
                    if (!CopyPreviewMember(original, copy, name, false)) return null;
                var field = t.GetField("coordinates"); var property = t.GetProperty("coordinates");
                if (field != null) field.SetValue(copy, coordinates);
                else if (property != null && property.CanWrite) property.SetValue(copy, coordinates, null);
                else return null;
                return copy;
            }
            catch { return null; }
        }

        private static bool CopyPreviewMember(object original, object copy, string name, bool required)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var type = original.GetType();
            var field = type.GetField(name, flags);
            if (field != null)
            {
                var value = field.GetValue(original);
                if (required && value == null) return false;
                field.SetValue(copy, value); return true;
            }
            var property = type.GetProperty(name, flags);
            if (property == null) return !required;
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0) return false;
            var propertyValue = property.GetValue(original, null);
            if (required && propertyValue == null) return false;
            property.SetValue(copy, propertyValue, null); return true;
        }

        private static string AreaUnavailable(string reason)
        { return "{\"status\":\"unavailable\",\"reason\":" + Q(reason) + ",\"coverage\":\"visible-units-only\",\"affectedUnits\":[]}"; }

        private static string AreaEffects(object gs, object lp, object attacker, object targetCoordinates, byte me, string kind)
        {
            bool moving = kind == "move";
            bool? splash = Ability(attacker, "Splash"), stomp = Ability(attacker, "Stomp"), tentacles = Ability(attacker, "Tentacles");
            bool? active = moving ? (stomp == null || tentacles == null ? (bool?)null : stomp.Value || tentacles.Value) : splash;
            if (active == null) return AreaUnavailable("native-area-ability-unreadable");
            if (!active.Value) return "{\"status\":\"not-applicable\",\"coverage\":\"visible-units-only\",\"affectedUnits\":[]}";
            int? version = MI(gs, "Version");
            // Native dispatch at 4D90A2 and 49FE98 selects Default only from Version 121.
            // Older versions use a different rounding and are not silently substituted.
            if (!version.HasValue || version.Value < 121) return AreaUnavailable("native-area-version-not-supported");
            bool poisonOnly = false;
            if (!moving)
            {
                bool? poison = Ability(attacker, "Poison");
                if (poison == null) return AreaUnavailable("native-poison-ability-unreadable");
                // AttackCommand.ExecuteDefault 4D3DF8 branches on Poison (26):
                // neighbors receive PoisonUnitAction, NOT half-damage AttackAction.
                // Never borrow the ordinary Splash damage formula for Exida.
                poisonOnly = poison.Value;
                var range = BridgeMod.FindStaticUnique(BridgeMod.FindGameType("UnitDataExtensions"), "GetRange", new[] { "UnitState", "GameState" }, "Int32");
                if (range == null) return AreaUnavailable("native-range-unreadable");
                try { if ((int)range.Invoke(null, new[] { attacker, gs }) <= 1) return "{\"status\":\"not-applicable\",\"affectedUnits\":[]}"; } catch { return AreaUnavailable("native-range-read-failed"); }
            }
            var neighbors = BridgeMod.FindStaticUnique(BridgeMod.FindGameType("MapDataExtensions"), "GetTileNeighbors", new[] { "MapData", "WorldCoordinates" }, null);
            var battle = BridgeMod.FindStaticUnique(BridgeMod.FindGameType("BattleHelpers"), "GetBattleResults", new[] { "GameState", "UnitState", "UnitState", "Boolean", "Boolean" }, "BattleResults");
            if (neighbors == null || (!poisonOnly && battle == null)) return AreaUnavailable("native-area-helpers-unbound");
            object raw;
            try { raw = neighbors.Invoke(null, new[] { M(gs, "Map"), targetCoordinates }); } catch { return AreaUnavailable("native-neighbors-failed"); }
            var tiles = AsList(raw); if (tiles == null) return AreaUnavailable("native-neighbors-unreadable");
            bool partial = false; var effects = new List<string>();
            foreach (var tile in tiles)
            {
                if (!Revealed(tile, me)) continue;
                bool filtered; var defender = BridgeMod.TileUnitVisible(tile, gs, me, out filtered);
                if (!filtered) { partial = true; continue; }
                if (defender == null) continue;
                bool? peace = null;
                try
                {
                    var method = defender.GetType().GetMethod("HasActivePeaceTreaty", new[] { gs.GetType(), lp.GetType() });
                    if (method != null) peace = (bool)method.Invoke(defender, new[] { gs, lp });
                }
                catch { }
                if (peace == null) { partial = true; continue; }
                if (peace.Value) continue;
                int? damage = poisonOnly ? (int?)0 : null; object br = null;
                try
                {
                    if (!poisonOnly)
                    {
                        br = battle.Invoke(null, new object[] { gs, attacker, defender, true, false });
                        if (moving && tentacles == true) damage = MI(br, "attackDamage");
                        else
                        {
                            var half = br == null ? null : br.GetType().GetMethod("GetFlooredHalfAttackDamage", Type.EmptyTypes);
                            if (half != null) damage = BridgeMod.NativeInt32(half.Invoke(br, null));
                        }
                    }
                }
                catch { }
                if (!damage.HasValue || damage.Value < 0) { partial = true; damage = null; }
                int? hp = MI(defender, "health");
                effects.Add("{\"unitId\":" + Q(S(M(defender, "id"))) + ",\"owner\":" + (MI(defender, "owner").HasValue ? MI(defender, "owner").Value.ToString(CultureInfo.InvariantCulture) : "null")
                    + ",\"type\":" + Q(S(M(defender, "type"))) + ",\"coords\":" + Q(S(M(tile, "coordinates")))
                    + ",\"healthBefore\":" + (hp.HasValue ? hp.Value.ToString(CultureInfo.InvariantCulture) : "null")
                    + ",\"damage\":" + (damage.HasValue ? damage.Value.ToString(CultureInfo.InvariantCulture) : "null")
                    + ",\"wouldDefeat\":" + (hp.HasValue && damage.HasValue ? damage.Value >= hp.Value ? "true" : "false" : "null")
                    + (poisonOnly ? ",\"wouldApplyPoison\":true,\"newlyPoisoned\":null,\"poisonAuthority\":\"native PoisonUnitAction branch; no private enemy effects read\"" : "")
                    + ",\"retaliation\":false,\"wouldMoveToVictim\":false}");
            }
            return "{\"status\":" + Q(partial || moving || poisonOnly ? "partial" : "ok") + ",\"nativeGameVersion\":" + version.Value
                + ",\"effect\":" + Q(moving ? "Stomp-or-Tentacles-at-destination" : poisonOnly ? "PoisonSplash" : "Splash") + ",\"coverage\":\"visible-units-only\",\"authority\":\"native neighbors and peace; native AttackCommand Poison branch or BattleResults/half-damage helper; Default dispatch verified in GameAssembly\""
                + (moving ? ",\"conditional\":\"uses copied actor at destination; terrain effects, damage and identity conversion during movement are not simulated\""
                    : poisonOnly ? ",\"conditional\":\"current visible targets only; no command executed, private effects and passenger changes are not inspected\"" : "")
                + ",\"affectedUnits\":[" + string.Join(",", effects.ToArray()) + "]}";
        }

        // Native MoveAction.ExecuteDefault 4CA12D..4CA298: Algae ability queues
        // BuildAction(AlgaeSpawn=55) on every water path tile without Algae.
        // The native path includes the origin. Publish queue predictions only;
        // never execute BuildAction or promise its complete population/growth effects.
        private static string MoveTerrainEffects(object gs, object actor, List<object> path, byte me)
        {
            bool? algae = Ability(actor, "Algae");
            if (algae == false) return "{\"status\":\"not-applicable\",\"tiles\":[]}";
            int? version = MI(gs, "Version");
            if (algae == null || !version.HasValue || version.Value < 121)
                return "{\"status\":\"unavailable\",\"reason\":\"native-algae-rule-unavailable\",\"tiles\":[]}";
            var map = M(gs, "Map");
            var all = AsList(M(map, "Tiles"));
            if (all == null) return "{\"status\":\"unavailable\",\"tiles\":[]}";
            var rows = new List<string>();
            foreach (object coordinate in path)
            {
                string coords = S(coordinate);
                object tile = null;
                foreach (object candidate in all) if (S(M(candidate, "coordinates")) == coords) { tile = candidate; break; }
                bool? water = null, hasAlgae = null, spawn = null;
                // A legal move coordinate is not permission to inspect hidden terrain.
                bool disclosed = Revealed(tile, me);
                if (disclosed)
                {
                    water = MB(tile, "IsWater");
                    if (water == false) spawn = false;
                    if (water == true)
                    {
                        var has = BridgeMod.FindUniqueNativeMethod(tile.GetType(), "HasEffect", new[] { "EffectType" }, "Boolean", false);
                        try
                        {
                            if (has != null && Enum.IsDefined(has.GetParameters()[0].ParameterType, "Algae"))
                            {
                                hasAlgae = (bool)has.Invoke(tile, new[] { Enum.Parse(has.GetParameters()[0].ParameterType, "Algae") });
                                spawn = !hasAlgae.Value;
                            }
                        }
                        catch { }
                    }
                }
                rows.Add(new PolytopiaAgent.JsonObj().Str("coords", coords).Bool("explored", disclosed)
                    .NullableBool("water", water).NullableBool("hasAlgae", hasAlgae)
                    .NullableBool("wouldQueueAlgaeSpawn", spawn).Close());
            }
            return "{\"status\":\"partial\",\"effect\":\"AlgaeSpawn\",\"includesOrigin\":true,"
                + "\"authority\":\"native MoveAction Algae branch, IsWater and HasEffect(Algae) on explored path tiles\","
                + "\"conditional\":\"queue prediction only; no future BuildAction, growth, population or hidden terrain simulated\","
                + "\"tiles\":[" + string.Join(",", rows.ToArray()) + "]}";
        }

        /// <summary>Explicit read-only move/Stomp preview. All native movement legality uses MY unit.</summary>
        public static string CompositeSnapshot(string unitId, string targetCoord, string kind)
        {
            var uf = BridgeMod.UnitFilter; bool sig = uf.SignatureAvailable;
            int la = uf.LookupAttempted, at = uf.Attempted, sc = uf.Succeeded, fail = uf.Failed;
            try { return CompositeCore(unitId, targetCoord, kind); }
            finally { uf.SignatureAvailable = sig; uf.LookupAttempted = la; uf.Attempted = at; uf.Succeeded = sc; uf.Failed = fail; }
        }

        private static string CompositeCore(string unitId, string targetCoord, string kind)
        {
            var gm = BridgeMod.FindGameType("GameManager"); var gs = StaticMember(gm, "GameState"); var lp = StaticMember(gm, "LocalPlayer");
            int? me = MI(lp, "Id");
            if (gs == null || lp == null || !me.HasValue || me.Value == 255) return AreaUnavailable("controlled-state-unavailable");
            var map = M(gs, "Map"); var tiles = AsList(M(map, "Tiles"));
            if (tiles == null) return AreaUnavailable("tiles-unreadable");
            object actor = null, target = null;
            foreach (var tile in tiles)
            {
                // Movement may legally target uncharted coordinates, but this preview never reads
                // their terrain/units. Coordinates alone cannot leak a hidden target footprint.
                if (S(M(tile, "coordinates")) == targetCoord) target = M(tile, "coordinates");
                if (!Revealed(tile, (byte)me.Value)) continue;
                bool filtered; var u = BridgeMod.TileUnitVisible(tile, gs, (byte)me.Value, out filtered);
                if (filtered && u != null && MI(u, "owner") == me && S(M(u, "id")) == unitId) actor = u;
            }
            if (actor == null || target == null) return AreaUnavailable("own-unit-or-target-coordinate-unavailable");
            if (kind != "move") return AreaUnavailable("only-move-composite-query-supported-use-Snapshot-for-attack");
            if (MB(actor, "CanMove") != true) return AreaUnavailable("unit-cannot-move");
            var movement = BridgeMod.FindStaticUnique(BridgeMod.FindGameType("UnitDataExtensions"), "GetMovement", new[] { "UnitState", "GameState" }, "Int32");
            var path = BridgeMod.FindStaticUnique(BridgeMod.FindGameType("PathFinder"), "GetPath", new[] { "GameState", "WorldCoordinates", "WorldCoordinates", "Int32", "UnitState" }, null);
            var options = BridgeMod.FindStaticUnique(BridgeMod.FindGameType("PathFinder"), "GetMoveOptions", new[] { "GameState", "WorldCoordinates", "Int32", "UnitState" }, null);
            if (movement == null || path == null || options == null) return AreaUnavailable("native-movement-helpers-unbound");
            List<object> steps;
            try
            {
                int cost = (int)movement.Invoke(null, new[] { actor, gs });
                var from = M(actor, "coordinates");
                var legal = AsList(options.Invoke(null, new object[] { gs, from, cost, actor }));
                bool found = false;
                if (legal != null) foreach (var c in legal) if (S(c) == targetCoord) found = true;
                if (!found) return AreaUnavailable("destination-not-in-native-move-options");
                steps = AsList(path.Invoke(null, new object[] { gs, from, target, cost, actor }));
                if (steps == null || steps.Count < 2)
                    return AreaUnavailable("native-path-order-or-endpoints-unverified");
                // Native ReconstructPath appends destination and its parents through origin;
                // GetPath returns that list unchanged. AsList owns this managed copy, so
                // normalize it without modifying the native path or live game state.
                if (S(steps[0]) == targetCoord && S(steps[steps.Count - 1]) == S(from))
                    steps.Reverse();
                if (S(steps[0]) != S(from) || S(steps[steps.Count - 1]) != targetCoord)
                    return AreaUnavailable("native-path-order-or-endpoints-unverified");
            }
            catch { return AreaUnavailable("native-path-read-failed"); }
            var outputs = new List<string>();
            for (int i = 1; i < steps.Count; i++)
            {
                // ExecuteDefault (4C9AA0) reads path[0] as destination and calls
                // AreaDamage once at 4CA03B, outside the later path iteration.
                // Travel coordinates do not each trigger Stomp/Tentacles damage.
                if (i < steps.Count - 1)
                {
                    outputs.Add("{\"pathStep\":" + i + ",\"coords\":" + Q(S(steps[i]))
                        + ",\"effects\":{\"status\":\"not-applicable\",\"reason\":\"area-damage-triggers-at-destination\",\"affectedUnits\":[]}}");
                    continue;
                }
                var copy = ClonePreviewUnit(actor, steps[i]);
                if (copy == null) return AreaUnavailable("independent-native-unit-copy-unavailable");
                outputs.Add("{\"pathStep\":" + i + ",\"coords\":" + Q(S(steps[i])) + ",\"effects\":" + AreaEffects(gs, lp, copy, steps[i], (byte)me.Value, "move") + "}");
            }
            return "{\"schema\":\"move-composite-preview-1\",\"status\":\"partial\",\"unitId\":" + Q(unitId) + ",\"destination\":" + Q(targetCoord)
                + ",\"terrainEffects\":" + MoveTerrainEffects(gs, actor, steps, (byte)me.Value)
                + ",\"authority\":\"native PathFinder legal options and path; copied UnitState and area damage at destination\",\"steps\":[" + string.Join(",", outputs.ToArray()) + "]}";
        }

        /// <summary>The single parameterless audit entry point for the preview domain.</summary>
        public static string Snapshot()
        { return Query("", ""); }

        /// <summary>Read only matching own attackers and their actual native legal targets.</summary>
        public static string Query(string unitId, string targetCoord)
        {
            var gm = BridgeMod.FindGameType("GameManager");
            object gs = null;
            try
            {
                if (gm != null)
                {
                    BridgeMod.TryReadNativeStaticMember(gm, "GameState", out gs);
                }
            }
            catch (Exception) { }
            if (gs == null) { return Un("gamestate-unavailable"); }

            // The controlled player is the SAME verified identity the bridge uses for actions
            // (GameManager.LocalPlayer). A guess (first player / current player) is never used:
            // a wrong pick would present another player's private units as attackable.
            object lp = null;
            try
            {
                if (gm != null)
                {
                    BridgeMod.TryReadNativeStaticMember(gm, "LocalPlayer", out lp);
                }
            }
            catch (Exception) { }
            if (lp == null) { return Un("controlled-identity-unavailable"); }
            var idv = MI(lp, "Id");
            if (!idv.HasValue) { return Un("controlled-identity-id-unreadable"); }
            if (idv.Value == 255) { return Un("controlled-identity-nature-sentinel"); }
            byte me = (byte)idv.Value;

            var ext = BridgeMod.FindGameType("UnitDataExtensions");
            var bh = BridgeMod.FindGameType("BattleHelpers");
            var getRange = BridgeMod.FindStaticUnique(ext, "GetRange",
                new[] { "UnitState", "GameState" }, "Int32");
            var getOptions = BridgeMod.FindStaticUnique(ext, "GetAttackOptions",
                new[] { "UnitState", "GameState", "Int32", "Boolean" }, null);
            // accountForPeace:true is the UI/peace-aware computation; simpleData:false is the full
            // result struct (the same call the attack menu makes).
            var getResults = BridgeMod.FindStaticUnique(bh, "GetBattleResults",
                new[] { "GameState", "UnitState", "UnitState", "Boolean", "Boolean" }, "BattleResults");

            var reasons = new List<string>();
            if (getRange == null) { reasons.Add("getrange-not-bound"); }
            if (getOptions == null) { reasons.Add("getattackoptions-not-bound"); }
            if (getResults == null) { reasons.Add("getbattleresults-not-bound"); }

            var map = M(gs, "Map") ?? M(gs, "map");
            var tiles = AsList(M(map, "Tiles") ?? M(map, "tiles"));
            if (tiles == null) { return Un("tiles-unavailable"); }

            // coordinate -> tile index, so a returned target coordinate can be resolved to its tile
            var byCoord = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var t in tiles)
            {
                if (t == null) { continue; }
                var c = S(M(t, "coordinates") ?? M(t, "Coordinates"));
                if (!ValidCoords(c)) { continue; }
                if (!byCoord.ContainsKey(c)) { byCoord[c] = t; }
            }

            var attackers = new List<string>();
            int myUnits = 0, optionsTotal = 0, targetsOut = 0, unknown = 0;
            int attempted = 0, succeeded = 0, failed = 0, lookupAttempted = 0;

            // The preview sweep reuses the observation's own visibility accessor, so its counters
            // must be restored afterwards: a read must not change what the observation reports.
            var uf = BridgeMod.UnitFilter;
            bool sig = uf.SignatureAvailable;
            int la = uf.LookupAttempted, at = uf.Attempted, sc = uf.Succeeded, fa = uf.Failed;
            try
            {
                foreach (var tile in tiles)
                {
                    if (tile == null || !Revealed(tile, me)) { continue; }
                    bool filtered;
                    var unit = BridgeMod.TileUnitVisible(tile, gs, me, out filtered);
                    lookupAttempted++;
                    if (unit == null || !filtered) { continue; }
                    var owner = MI(unit, "owner");
                    if (!owner.HasValue || owner.Value != me) { continue; }
                    if (!string.IsNullOrEmpty(unitId) && S(M(unit, "id")) != unitId) { continue; }
                    myUnits++;

                    var from = S(M(tile, "coordinates") ?? M(tile, "Coordinates"));
                    bool? attacked = MB(unit, "attacked");
                    bool? canAttack = MB(unit, "CanAttack");
                    var uo = new StringBuilder();
                    uo.Append("{\"unitId\":").Append(Q(S(M(unit, "id"))))
                      .Append(",\"from\":").Append(Q(from))
                      .Append(",\"health\":").Append(MI(unit, "health").HasValue
                            ? MI(unit, "health").Value.ToString(CultureInfo.InvariantCulture) : "null")
                      .Append(",\"xp\":").Append(MI(unit, "xp").HasValue
                            ? MI(unit, "xp").Value.ToString(CultureInfo.InvariantCulture) : "null")
                      .Append(",\"attacked\":").Append(attacked.HasValue
                            ? (attacked.Value ? "true" : "false") : "null")
                      .Append(",\"canAttack\":").Append(canAttack.HasValue
                            ? (canAttack.Value ? "true" : "false") : "null");

                    if (attacked == null || canAttack == null)
                    {
                        unknown++;
                        reasons.Add("attack-state-unreadable");
                        uo.Append(",\"targets\":[],\"status\":\"unavailable\"}");
                        attackers.Add(uo.ToString());
                        continue;
                    }
                    if (attacked.Value || !canAttack.Value)
                    {
                        // Legitimately empty: the UI highlights nothing for this unit.
                        uo.Append(",\"targets\":[],\"status\":\"no-attack\"}");
                        attackers.Add(uo.ToString());
                        continue;
                    }

                    int? range = null;
                    if (getRange != null)
                    {
                        try { range = BridgeMod.NativeInt32(getRange.Invoke(null, new object[] { unit, gs })); }
                        catch (Exception ex) { range = null; reasons.Add("getrange-threw:" + ExName(ex)); }
                    }
                    if (!range.HasValue || range.Value < 0)
                    {
                        unknown++;
                        if (getRange == null) { reasons.Add("getrange-unreadable"); }
                        else { reasons.Add("getrange-value-unreadable"); }
                        uo.Append(",\"targets\":[],\"status\":\"unavailable\"}");
                        attackers.Add(uo.ToString());
                        continue;
                    }
                    object rawOptions = null;
                    if (getOptions != null)
                    {
                        try
                        {
                            rawOptions = getOptions.Invoke(null,
                                new object[] { unit, gs, range.Value, false });
                        }
                        catch (Exception ex)
                        {
                            rawOptions = null;
                            reasons.Add("getattackoptions-threw:" + ExName(ex));
                        }
                    }
                    var options = AsList(rawOptions);
                    if (options == null)
                    {
                        unknown++;
                        if (getOptions == null) { reasons.Add("getattackoptions-unbound"); }
                        else if (rawOptions == null) { reasons.Add("getattackoptions-null"); }
                        else { reasons.Add("getattackoptions-not-enumerable:" + rawOptions.GetType().Name); }
                        uo.Append(",\"range\":").Append(range.Value.ToString(CultureInfo.InvariantCulture))
                           .Append(",\"targets\":[],\"status\":\"unavailable\"}");
                        attackers.Add(uo.ToString());
                        continue;
                    }
                    optionsTotal += options.Count;

                    int myHp = MI(unit, "health") ?? -1;
                    var targets = new List<string>();
                    bool clean = true;
                    foreach (var opt in options)
                    {
                        if (opt == null) { clean = false; continue; }
                        var tc = S(M(opt, "coordinates") ?? M(opt, "Coordinates")) ?? S(opt);
                        if (!ValidCoords(tc)) { clean = false; continue; }
                        if (!string.IsNullOrEmpty(targetCoord) && tc != targetCoord) { continue; }
                        object targetTile;
                        if (!byCoord.TryGetValue(tc, out targetTile) || targetTile == null)
                        { clean = false; continue; }
                        if (!Revealed(targetTile, me)) { clean = false; continue; }
                        bool tFiltered;
                        var defender = BridgeMod.TileUnitVisible(targetTile, gs, me, out tFiltered);
                        attempted++;
                        if (defender == null || !tFiltered) { clean = false; failed++; continue; }
                        succeeded++;
                        var defOwner = MI(defender, "owner");
                        var defHp = MI(defender, "health");
                        // The preview shares UnitPopup's public progress rule;
                        // raw XP is not displayed for Static or veteran enemies.
                        var defXp = BridgeMod.URPublicKills(defender, gs);
                        object br = null;
                        if (getResults != null)
                        {
                            try
                            {
                                br = getResults.Invoke(null,
                                    new object[] { gs, unit, defender, true, false });
                            }
                            catch (Exception ex)
                            {
                                br = null;
                                reasons.Add("getbattleresults-threw:" + ExName(ex));
                            }
                        }
                        int? dealt = br == null ? null : MI(br, "attackDamage");
                        int? received = br == null ? null : MI(br, "retaliationDamage");
                        int? rawReceived = received;
                        // Native negative values are sentinels, never negative HP loss or healing.
                        if (received.HasValue && received.Value < 0) received = null;
                        bool? advance = br == null ? null : MB(br, "shouldMoveToDefeatedEnemyTile");
                        if (!dealt.HasValue || !received.HasValue)
                        {
                            clean = false;
                            if (getResults == null) { reasons.Add("getbattleresults-unbound"); }
                            else if (br == null) { reasons.Add("battleresults-null"); }
                            else { reasons.Add("battleresults-fields-unreadable"); }
                        }
                        var to = new StringBuilder();
                        to.Append("{\"coords\":").Append(Q(tc))
                          .Append(",\"defenderId\":").Append(Q(S(M(defender, "id"))))
                          .Append(",\"defenderOwner\":").Append(defOwner.HasValue
                                ? defOwner.Value.ToString(CultureInfo.InvariantCulture) : "null")
                          .Append(",\"defenderHealth\":").Append(defHp.HasValue
                                ? defHp.Value.ToString(CultureInfo.InvariantCulture) : "null")
                          .Append(",\"defenderXp\":").Append(defXp.HasValue
                                ? defXp.Value.ToString(CultureInfo.InvariantCulture) : "null")
                          .Append(",\"damageDealt\":").Append(dealt.HasValue
                                ? dealt.Value.ToString(CultureInfo.InvariantCulture) : "null")
                          .Append(",\"damageReceived\":").Append(received.HasValue
                                ? received.Value.ToString(CultureInfo.InvariantCulture) : "null")
                          .Append(",\"nativeDamageReceived\":").Append(rawReceived.HasValue ? rawReceived.Value.ToString(CultureInfo.InvariantCulture) : "null")
                          .Append(",\"retaliationStatus\":").Append(Q(rawReceived.HasValue && rawReceived.Value < 0 ? "native-negative-sentinel" : received.HasValue ? "known" : "unknown"))
                          .Append(",\"wouldDefeatTarget\":").Append(
                                dealt.HasValue && defHp.HasValue
                                    ? (dealt.Value >= defHp.Value ? "true" : "false") : "null")
                          .Append(",\"wouldDieToRetaliation\":").Append(
                                received.HasValue && myHp >= 0
                                    ? (received.Value >= myHp ? "true" : "false") : "null")
                          .Append(",\"wouldAdvanceToTargetTile\":").Append(advance.HasValue
                                ? (advance.Value ? "true" : "false") : "null")
                          .Append(",\"compositeEffects\":").Append(AreaEffects(gs, lp, unit,
                                M(targetTile, "coordinates") ?? M(targetTile, "Coordinates"), me, "attack"))
                          .Append("}");
                        targets.Add(to.ToString());
                    }
                    targetsOut += targets.Count;
                    if (!clean) { unknown++; }
                    uo.Append(",\"range\":").Append(range.Value.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"targets\":[")
                      .Append(string.Join(",", targets.ToArray()))
                      .Append("],\"status\":").Append(Q(!clean ? "partial" : targets.Count == 0 ? "no-target" : "ok")).Append("}");
                    attackers.Add(uo.ToString());
                }
            }
            finally
            {
                uf.SignatureAvailable = sig;
                uf.LookupAttempted = la; uf.Attempted = at; uf.Succeeded = sc; uf.Failed = fa;
            }

            if (!string.IsNullOrEmpty(unitId) && attackers.Count == 0) reasons.Add("own-unit-not-observed");
            string status;
            if (reasons.Count > 0 && attackers.Count == 0) { status = "unavailable"; }
            else if (reasons.Count > 0 || unknown > 0) { status = "partial"; }
            else { status = "ok"; }

            var sb = new StringBuilder();
            sb.Append("{\"schema\":").Append(Q(Schema))
              .Append(",\"status\":").Append(Q(status))
              .Append(",\"controlledPlayerId\":").Append(me.ToString(CultureInfo.InvariantCulture))
              .Append(",\"authority\":")
              .Append(Q("UnitDataExtensions.GetRange/GetAttackOptions (UI highlight chain) + "
                        + "BattleHelpers.GetBattleResults (attack menu) + TileData.GetUnit visibility"))
              .Append(",\"helpers\":{\"getRange\":").Append(Q(getRange == null ? "unbound" : "ok"))
              .Append(",\"getAttackOptions\":").Append(Q(getOptions == null ? "unbound" : "ok"))
              .Append(",\"battleResults\":").Append(Q(getResults == null ? "unbound" : "ok"))
              .Append(",\"visibleUnitAccessor\":").Append(Q(sig ? "ok" : "unbound"))
              .Append("},\"counts\":{\"tiles\":").Append(tiles.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\"visibleLookups\":").Append(lookupAttempted.ToString(CultureInfo.InvariantCulture))
              .Append(",\"myUnits\":").Append(myUnits.ToString(CultureInfo.InvariantCulture))
              .Append(",\"optionsTotal\":").Append(optionsTotal.ToString(CultureInfo.InvariantCulture))
              .Append(",\"defenderLookups\":").Append(attempted.ToString(CultureInfo.InvariantCulture))
              .Append(",\"defenderResolved\":").Append(succeeded.ToString(CultureInfo.InvariantCulture))
              .Append(",\"defenderFailed\":").Append(failed.ToString(CultureInfo.InvariantCulture))
              .Append(",\"targets\":").Append(targetsOut.ToString(CultureInfo.InvariantCulture))
              .Append(",\"unknowns\":").Append(unknown.ToString(CultureInfo.InvariantCulture))
              .Append("},\"attackers\":[")
              .Append(string.Join(",", attackers.ToArray()))
              .Append("],\"reasonCodes\":[");
            for (int i = 0; i < reasons.Count; i++)
            {
                if (i > 0) { sb.Append(","); }
                sb.Append(Q(reasons[i]));
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
