// CityOptionsSnapshot - parameterless read-only entry point for the player-visible development
// options of the current position.
//
//     call  type="PolytopiaCityOps.CityOptionsSnapshot"  method="Snapshot"  audit="1"
//
// Everything is read from THIS game's own data objects through the verified metadata of this build
// (dumped from MelonLoader/Il2CppAssemblies/Il2CppGameLogicAssembly.dll with Mono.Cecil, not guessed):
//
//   GameLogicData (the instance belonging to THIS GameState, via BridgeMod.GameLogicDataOf)
//     GetUnlockableImprovements(PlayerState, GameState)      -> List<ImprovementData>
//     GetActionableImprovementForResource(ResourceData.Type, PlayerState) -> ImprovementData
//     GetImprovementForResource(ResourceData.Type)           -> List<ImprovementData>
//     CanBuild(GameState, TileData, PlayerState, ImprovementData) -> bool   (the game's own rule)
//     GetImprovementData(ImprovementData.Type)               -> ImprovementData
//     GetUnitData(UnitData.Type)                             -> UnitData
//     GetUnlockedUpgradesForUnit(PlayerState, GameState, UnitData) -> List<UnitData>
//   ImprovementData: type / displayName / cost / work / maxLevel / growthRate / rewards / creates
//   UnitData:        type / cost
//   CityRewardData.GetRewardName/GetPopupTitle/GetPopupDescription(CityReward)  (the UI's own text)
//   GameState.CurrentState : State {Unknown, Lobby, Started, FinalTurn, Ended}
//
// Costs come from ImprovementData.cost / UnitData.cost - never from a remembered number. A value
// that cannot be read is null with a reason code, never 0. Nothing is constructed or mutated.
//
// This is the read side only: it plans nothing and issues no command.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using PolytopiaAgent;

namespace PolytopiaCityOps
{
    public static class CityOptionsSnapshot
    {
        public const string Schema = "city-options-6";

        private static object M(object o, string clrName)
        {
            object value;
            return BridgeMod.TryReadNativeMember(o, clrName, out value) ? value : null;
        }

        private static int? MI(object o, string clrName)
        {
            return BridgeMod.NativeInt32(M(o, clrName));
        }

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

        private static bool? MB(object o, string clrName)
        { return BridgeMod.ReadNativeBooleanMember(o, clrName); }

        /// <summary>Orthogonal neighbours only: "(x,y)" text comparison, no pathfinding.</summary>
        private static bool Adjacent(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) { return false; }
            var pa = a.Trim('(', ')').Split(',');
            var pb = b.Trim('(', ')').Split(',');
            if (pa.Length != 2 || pb.Length != 2) { return false; }
            int ax, ay, bx, by;
            if (!int.TryParse(pa[0], out ax) || !int.TryParse(pa[1], out ay)
                || !int.TryParse(pb[0], out bx) || !int.TryParse(pb[1], out by)) { return false; }
            return Math.Abs(ax - bx) + Math.Abs(ay - by) == 1;
        }

        /// <summary>8-way (Chebyshev) distance between two "(x,y)" texts, -1 when unreadable.</summary>
        private static int Cheb(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) { return -1; }
            var pa = a.Trim('(', ')').Split(',');
            var pb = b.Trim('(', ')').Split(',');
            if (pa.Length != 2 || pb.Length != 2) { return -1; }
            int ax, ay, bx, by;
            if (!int.TryParse(pa[0], out ax) || !int.TryParse(pa[1], out ay)
                || !int.TryParse(pb[0], out bx) || !int.TryParse(pb[1], out by)) { return -1; }
            return Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));
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

        private static string ExName(Exception ex)
        {
            if (ex == null) { return "unknown"; }
            BridgeMod.RecordNativeReadFailure(typeof(CityOptionsSnapshot), "city_native", ex);
            if (ex is TargetInvocationException && ex.InnerException != null) { ex = ex.InnerException; }
            return ex.GetType().Name;
        }

        private static List<object> AsList(object raw)
        {
            return BridgeMod.ReadNativeList(raw, true);
        }

        /// <summary>The single instance method with this name and parameter count, or null.</summary>
        private static MethodInfo Inst(Type t, string name, int argc, string[] paramTypeNames)
        {
            if (paramTypeNames == null && argc == 0) paramTypeNames = new string[0];
            if (paramTypeNames == null || paramTypeNames.Length != argc) return null;
            return BridgeMod.FindUniqueNativeMethod(t, name, paramTypeNames, null, false);
        }

        private static object LogicOf(object gs)
        {
            try { return BridgeMod.GameLogicDataOf(gs); }
            catch (Exception) { return null; }
        }

        // Lives only on one synchronous snapshot's stack. Cache failures as well as successes:
        // another output section must not reinterpret an unreadable visibility sample as empty.
        private sealed class SnapshotReads
        {
            private sealed class ReferenceComparer : IEqualityComparer<object>
            {
                public new bool Equals(object a, object b) { return object.ReferenceEquals(a, b); }
                public int GetHashCode(object value) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value); }
            }
            private sealed class ResourceSample { public object Value; public bool Filtered; }
            private readonly object state;
            private readonly byte player;
            private readonly Dictionary<object, bool?> explored = new Dictionary<object, bool?>(new ReferenceComparer());
            private readonly Dictionary<object, ResourceSample> resources = new Dictionary<object, ResourceSample>(new ReferenceComparer());
            public SnapshotReads(object gs, byte me) { state = gs; player = me; }
            public bool? Explored(object tile)
            {
                if (tile == null) { return null; }
                bool? value;
                if (!explored.TryGetValue(tile, out value))
                { value = ExploredOf(tile, player); explored.Add(tile, value); }
                return value;
            }
            public object Resource(object tile, out bool filtered)
            {
                filtered = false;
                if (tile == null) { return null; }
                ResourceSample sample;
                if (!resources.TryGetValue(tile, out sample))
                {
                    sample = new ResourceSample();
                    try { sample.Value = BridgeMod.TileResourceVisible(tile, state, player, out sample.Filtered); }
                    catch { sample.Filtered = false; sample.Value = null; }
                    resources.Add(tile, sample);
                }
                filtered = sample.Filtered;
                return sample.Value;
            }
        }

        public static string Snapshot()
        {
            using (var timing = BridgeMod.BeginCityReadTiming()) return SnapshotCore(timing);
        }

        private static string SnapshotCore(BridgeMod.CityReadTiming timing)
        {
            var reasons = new List<string>();
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

            var logic = LogicOf(gs);
            if (logic == null) { return Un("gamelogicdata-unavailable"); }
            var logicType = logic.GetType();

            // The controlled player's own PlayerState, matched by Id on the live list: never "some
            // player" and never the current player index (which is not an Id in this build).
            object ps = null;
            var players = AsList(M(gs, "PlayerStates"));
            if (players != null)
            {
                foreach (var p in players)
                {
                    if (p == null) { continue; }
                    if (MI(p, "Id") == me) { ps = p; break; }
                }
            }
            if (ps == null) { return Un("playerstate-unavailable"); }
            var stars = MI(ps, "Currency");

            timing.Next(BridgeMod.CityReadPhase.Bindings);

            var getUnlockableImprovements = Inst(logicType, "GetUnlockableImprovements", 2,
                new[] { "PlayerState", "GameState" });
            var getActionable = Inst(logicType, "GetActionableImprovementForResource", 2,
                new[] { "Type", "PlayerState" });
            var getImprovementsForResource = Inst(logicType, "GetImprovementForResource", 1,
                new[] { "Type" });
            var canBuild = Inst(logicType, "CanBuild", 4,
                new[] { "GameState", "TileData", "PlayerState", "ImprovementData" });
            var getImprovementData = Inst(logicType, "GetImprovementData", 1, new[] { "Type" });
            var getUnitData = Inst(logicType, "GetUnitData", 1, new[] { "Type" });
            var getUnitUpgrades = Inst(logicType, "GetUnlockedUpgradesForUnit", 3,
                new[] { "PlayerState", "GameState", "UnitData" });
            // ---- city reward / city value bindings (codex-dsh-070) --------------------------------
            // The game's OWN numbers for a city reward and for a unit's effective defence. All are
            // static, unique-signature lookups; a binding that fails leaves the corresponding value
            // null and is named in reasonCodes.
            var rewardProdMod = StaticMethod("CityRewardData", "GetRewardProductionModifier",
                new[] { "CityReward", "GameState" }, "Int32");
            var rewardListGetter = StaticMethod("CityRewardData", "get_cityRewards",
                new string[0], null);
            var parkScoreGetter = StaticMethod("CityRewardData", "get_PARK_SCORE",
                new string[0], "Int32");
            var populationRewardGetter = StaticMethod("CityRewardData", "get_POPULATION_REWARD",
                new string[0], "Int32");
            var resourceRewardGetter = StaticMethod("CityRewardData", "get_RESOURCE_REWARD",
                new string[0], "Int32");
            var getDefence = StaticMethod("UnitDataExtensions", "GetDefence",
                new[] { "UnitState", "GameState" }, "Int32");
            var getDefenceBonus = StaticMethod("UnitDataExtensions", "GetDefenceBonus",
                new[] { "UnitState", "GameState" }, "Int32");
            var getAttack = StaticMethod("UnitDataExtensions", "GetAttack",
                new[] { "UnitState", "GameState" }, "Int32");
            var getMaxHealth = StaticMethod("UnitDataExtensions", "GetMaxHealth",
                new[] { "UnitState", "GameState" }, "Int32");
            var playerTerrainBonus = StaticMethod("PlayerExtensions", "GetDefenceBonus",
                new[] { "PlayerState", "Type", "GameState" }, "Int32");
            // The game's own per-turn star income for a player, and the raw production of a tile.
            var incomeFor = StaticMethod("ResourceDataUtils", "CalculateIncomeFor",
                new[] { "GameState", "Byte" }, "Int32");
            var rawProduction = StaticMethod("TileDataExtensions", "CalculateRawProduction",
                new[] { "TileData", "GameState" }, "Int32");
            // The game's own score numbers for a city tile: what a level-up is worth and what the
            // improvement itself scores. Reported so a score change is never guessed.
            var levelUpScore = StaticMethod("ScoreExtensions", "CalculateLevelUpScore",
                new[] { "TileData" }, "Int32");
            var improvementScore = StaticMethod("ScoreExtensions", "CalculateImprovementScore",
                new[] { "GameState", "TileData" }, "Int32");
            if (incomeFor == null) { reasons.Add("resourcedatautils-calculateincomefor-not-bound"); }
            if (rewardProdMod == null) { reasons.Add("cityrewarddata-getrewardproductionmodifier-not-bound"); }
            if (rewardListGetter == null) { reasons.Add("cityrewarddata-cityrewards-not-bound"); }
            if (getDefence == null || getDefenceBonus == null)
            { reasons.Add("unitdataextensions-defence-not-bound"); }
            timing.Next(BridgeMod.CityReadPhase.BindingConstants);
            var parkScore = CallGetter(parkScoreGetter);
            var populationReward = CallGetter(populationRewardGetter);
            var resourceReward = CallGetter(resourceRewardGetter);
            if (getUnlockableImprovements == null) { reasons.Add("getunlockableimprovements-not-bound"); }
            if (getActionable == null) { reasons.Add("getactionableimprovementforresource-not-bound"); }
            if (canBuild == null) { reasons.Add("canbuild-not-bound"); }
            if (getImprovementData == null) { reasons.Add("getimprovementdata-not-bound"); }
            if (getUnitData == null) { reasons.Add("getunitdata-not-bound"); }

            timing.Next(BridgeMod.CityReadPhase.MapContext);
            var map = M(gs, "Map") ?? M(gs, "map");
            var tiles = AsList(M(map, "Tiles") ?? M(map, "tiles"));
            if (tiles == null) { return Un("tiles-unavailable"); }
            var reads = new SnapshotReads(gs, me);

            // ---- per-tile scan: my resources (with the game's actionable improvement) + my cities
            timing.Next(BridgeMod.CityReadPhase.Cities);
            var resourceOptions = new List<string>();
            var cities = new List<string>();
            int ownTiles = 0;
            foreach (var tile in tiles)
            {
                if (tile == null) { continue; }
                var owner = MI(tile, "owner");
                if (owner.HasValue && owner.Value == me) { ownTiles++; }
                else { continue; }

                bool filtered;
                object resource = null;
                try { resource = reads.Resource(tile, out filtered); }
                catch (Exception) { resource = null; filtered = false; }
                if (resource != null && filtered)
                {
                    var ro = new StringBuilder();
                    var resEnum = M(resource, "type");
                    ro.Append("{\"coords\":").Append(Q(S(M(tile, "coordinates"))))
                      .Append(",\"resource\":").Append(Q(S(resEnum)));
                    object actionable = null;
                    if (getActionable != null && resEnum != null)
                    {
                        try { actionable = getActionable.Invoke(logic, new object[] { resEnum, ps }); }
                        catch (Exception ex) { reasons.Add("getactionable-threw:" + ExName(ex)); }
                    }
                    if (actionable != null)
                    {
                        var impType = M(actionable, "type");
                        ro.Append(",\"actionableImprovement\":").Append(Q(S(impType)))
                          .Append(",\"cost\":").Append(Num(MI(actionable, "cost")));
                        if (canBuild != null)
                        {
                            try
                            {
                                var ok = canBuild.Invoke(logic, new object[] { gs, tile, ps, actionable });
                                ro.Append(",\"canBuild\":").Append(NativeBooleanJson(ok, reasons, "canbuild-returned-non-boolean"));
                            }
                            catch (Exception ex)
                            {
                                ro.Append(",\"canBuild\":null");
                                reasons.Add("canbuild-threw:" + ExName(ex));
                            }
                        }
                        else { ro.Append(",\"canBuild\":null"); }
                    }
                    else
                    {
                        ro.Append(",\"actionableImprovement\":null,\"cost\":null,\"canBuild\":null");
                    }
                    if (getImprovementsForResource != null && resEnum != null)
                    {
                        try
                        {
                            var all = AsList(getImprovementsForResource.Invoke(logic,
                                new object[] { resEnum }));
                            if (all != null)
                            {
                                var sb2 = new StringBuilder("[");
                                for (int i = 0; i < all.Count; i++)
                                {
                                    if (i > 0) { sb2.Append(","); }
                                    sb2.Append("{\"improvement\":").Append(Q(S(M(all[i], "type"))))
                                       .Append(",\"cost\":").Append(Num(MI(all[i], "cost")))
                                       .Append("}");
                                }
                                ro.Append(",\"improvementsForResource\":").Append(sb2.Append("]").ToString());
                            }
                        }
                        catch (Exception error)
                        { ro.Append(",\"improvementsForResource\":null"); reasons.Add("improvementsforresource-threw:" + ExName(error)); }
                    }
                    ro.Append("}");
                    resourceOptions.Add(ro.ToString());
                }

                var imp = M(tile, "improvement");
                if (imp != null && S(M(imp, "type")) == "City")
                {
                    var co = new StringBuilder();
                    co.Append("{\"coords\":").Append(Q(S(M(tile, "coordinates"))))
                      .Append(",\"level\":").Append(Num(MI(imp, "level")))
                      .Append(",\"population\":").Append(Num(MI(imp, "population")));
                    object cityData = null;
                    if (getImprovementData != null)
                    {
                        try { cityData = getImprovementData.Invoke(logic, new object[] { M(imp, "type") }); }
                        catch (Exception ex) { reasons.Add("getimprovementdata-threw:" + ExName(ex)); }
                    }
                    // Both values remain native. PopulationNeededToUpgradeCity is a population
                    // requirement; CanCityBeUpgraded is cached construction potential, not an
                    // executable upgrade or immediate population-readiness predicate. Its native
                    // cache may differ after save/resume; never overwrite it with a derived bool.
                    var needed = Inst(tile.GetType(), "PopulationNeededToUpgradeCity", 0, null);
                    if (needed != null)
                    {
                        try
                        {
                            int? n = BridgeMod.NativeInt32(needed.Invoke(tile, null));
                            co.Append(",\"populationNeededToUpgrade\":").Append(Num(n));
                            if (!n.HasValue) reasons.Add("populationneeded-returned-non-integer");
                        }
                        catch (Exception error)
                        { co.Append(",\"populationNeededToUpgrade\":null"); reasons.Add("populationneeded-threw:" + ExName(error)); }
                    }
                    else { co.Append(",\"populationNeededToUpgrade\":null"); }
                    var canUp = Inst(tile.GetType(), "CanCityBeUpgraded", 1, new[] { "GameState" });
                    if (canUp != null)
                    {
                        try
                        {
                            var r = canUp.Invoke(tile, new object[] { gs });
                            co.Append(",\"canCityBeUpgraded\":")
                              .Append(NativeBooleanJson(r, reasons, "cancitybeupgraded-returned-non-boolean"));
                        }
                        catch (Exception error)
                        { co.Append(",\"canCityBeUpgraded\":null"); reasons.Add("cancitybeupgraded-threw:" + ExName(error)); }
                    }
                    else { co.Append(",\"canCityBeUpgraded\":null"); }
                    if (cityData != null)
                    {
                        co.Append(",\"maxLevel\":").Append(Num(MI(cityData, "maxLevel")))
                          .Append(",\"growthRate\":").Append(Num(MI(cityData, "growthRate")))
                          .Append(",\"maxHealth\":null");
                        // The rewards the city offers, named and described by the game's own UI text.
                        var rewards = AsList(M(cityData, "rewards"));
                        if (rewards != null)
                        {
                            var rb = new StringBuilder("[");
                            for (int i = 0; i < rewards.Count; i++)
                            {
                                if (i > 0) { rb.Append(","); }
                                var reward = rewards[i];
                                rb.Append("{\"reward\":").Append(Q(S(reward)))
                                  .Append(",\"name\":").Append(Q(CityRewardText("GetRewardName", reward)))
                                  .Append(",\"title\":").Append(Q(CityRewardText("GetPopupTitle", reward)))
                                  .Append(",\"description\":")
                                  .Append(Q(CityRewardText("GetPopupDescription", reward)))
                                  .Append("}");
                            }
                            co.Append(",\"rewards\":").Append(rb.Append("]").ToString());
                        }
                        else { co.Append(",\"rewards\":null"); }
                    }
                    // ---- the city's OWN native state, the rewards it ALREADY has, and the effective
                    // defence values of MY unit standing on it (codex-dsh-070). `rewards` above is the
                    // city TYPE's reward pool; `cityState.chosenRewards` is what THIS city actually took.
                    bool? hasWall;
                    var chosenJson = ChosenRewardsJson(M(imp, "rewards"), rewardProdMod, gs, out hasWall,
                        parkScore, populationReward, resourceReward);
                    co.Append(",\"cityState\":{\"production\":").Append(Num(MI(imp, "production")))
                      .Append(",\"rawProduction\":").Append(Num(CallInt(rawProduction, tile, gs)))
                      .Append(",\"levelUpScore\":").Append(Num(CallGetter1(levelUpScore, tile)))
                      .Append(",\"improvementScore\":").Append(Num(CallInt(improvementScore, gs, tile)))
                      .Append(",\"baseScore\":").Append(Num(MI(imp, "baseScore")))
                      .Append(",\"xp\":").Append(Num(MI(imp, "xp")))
                      .Append(",\"borderSize\":").Append(Num(MI(imp, "borderSize")))
                      .Append(",\"upgrade\":").Append(Num(MI(imp, "upgrade")))
                      .Append(",\"connectedToCapitalOfPlayer\":")
                      .Append(Num(MI(imp, "connectedToCapitalOfPlayer")))
                      .Append(",\"effects\":").Append(NamesJson(M(imp, "effects")))
                      .Append(",\"chosenRewards\":").Append(chosenJson)
                      .Append(",\"hasCityWall\":").Append(BJ(hasWall))
                      .Append(",\"wallAuthority\":").Append(Q("ImprovementState.rewards contains "
                          + "CityWall; the wall acts through the GARRISON unit's own effective defence "
                          + "numbers (cityState.garrison), which are the game's values for MY unit on "
                          + "this tile - no wall multiplier is invented here"))
                      .Append(",\"garrison\":").Append(GarrisonJson(tile, gs, me, ps, logic, getDefence,
                          getDefenceBonus, getAttack, getMaxHealth, getUnitData, playerTerrainBonus))
                      .Append("}");
                    co.Append("}");
                    cities.Add(co.ToString());
                }
            }

            // ---- improvements this player may build anywhere (cost from the game's own data)
            timing.Next(BridgeMod.CityReadPhase.FutureBuildings);
            var buildable = new List<string>();
            if (getUnlockableImprovements != null)
            {
                try
                {
                    var list = AsList(getUnlockableImprovements.Invoke(logic, new object[] { ps, gs }));
                    if (list != null)
                    {
                        foreach (var imp2 in list)
                        {
                            if (imp2 == null) { continue; }
                            var candidateCost = MI(imp2, "cost");
                            var bo = new StringBuilder();
                            bo.Append("{\"improvement\":").Append(Q(S(M(imp2, "type"))))
                              .Append(",\"cost\":").Append(Num(candidateCost))
                              .Append(",\"displayName\":").Append(Q(S(M(imp2, "displayName"))))
                              .Append(",\"affordable\":")
                              .Append(stars.HasValue && candidateCost.HasValue
                                  ? (stars.Value >= candidateCost.Value ? "true" : "false") : "null")
                              .Append("}");
                            buildable.Add(bo.ToString());
                        }
                    }
                }
                catch (Exception ex) { reasons.Add("getunlockableimprovements-threw:" + ExName(ex)); }
            }

            // ---- unit growth/upgrade options for MY units
            timing.Next(BridgeMod.CityReadPhase.UnitUpgrades);
            var unitOptions = new List<string>();
            if (getUnitUpgrades != null || getUnitData != null)
            {
                foreach (var tile in tiles)
                {
                    if (tile == null) { continue; }
                    bool filtered;
                    object unit = null;
                    try { unit = BridgeMod.TileUnitVisible(tile, gs, me, out filtered); }
                    catch (Exception) { unit = null; filtered = false; }
                    if (unit == null || !filtered) { continue; }
                    if (MI(unit, "owner") != me) { continue; }
                    var uo = new StringBuilder();
                    uo.Append("{\"unitId\":").Append(Q(S(M(unit, "id"))))
                      .Append(",\"type\":").Append(Q(S(M(unit, "type"))))
                      .Append(",\"coords\":").Append(Q(S(M(tile, "coordinates"))))
                      .Append(",\"xp\":").Append(Num(MI(unit, "xp")));
                    object unitData = null;
                    if (getUnitData != null)
                    {
                        var ut = M(unit, "type");
                        if (ut != null)
                        {
                            try { unitData = getUnitData.Invoke(logic, new object[] { ut }); }
                            catch (Exception ex) { reasons.Add("getunitdata-threw:" + ExName(ex)); }
                        }
                    }
                    var grow = new List<string>();
                    string growthStatus = "unavailable";
                    if (getUnitUpgrades != null && unitData != null)
                    {
                        try
                        {
                            var ups = AsList(getUnitUpgrades.Invoke(logic,
                                new object[] { ps, gs, unitData }));
                            if (ups != null)
                            {
                                growthStatus = "ok";
                                Type unitEnum = getUnitData.GetParameters()[0].ParameterType;
                                foreach (var u3 in ups)
                                {
                                    if (u3 == null) { growthStatus = "partial"; continue; }
                                    string why;
                                    string upgradeType = BridgeMod.EnumRuleIdentityOfExpected(M(u3, "type"), unitEnum, out why);
                                    if (upgradeType == null) { growthStatus = "partial"; continue; }
                                    int? cost = MI(u3, "cost");
                                    if (!cost.HasValue || cost.Value < 0) { cost = null; growthStatus = "partial"; }
                                    var go = new StringBuilder();
                                    go.Append("{\"growsInto\":").Append(Q(upgradeType))
                                      .Append(",\"cost\":").Append(Num(cost))
                                      .Append("}");
                                    grow.Add(go.ToString());
                                }
                            }
                        }
                        catch (Exception ex) { reasons.Add("getunlockedupgradesforunit-threw:" + ExName(ex)); }
                    }
                    uo.Append(",\"growsIntoStatus\":").Append(Q(growthStatus))
                      .Append(",\"growsInto\":").Append(growthStatus == "unavailable" ? "null"
                          : "[" + string.Join(",", grow.ToArray()) + "]").Append("}");
                    unitOptions.Add(uo.ToString());
                }
            }

            // ---- special actions of my visible units: capture (game's own CanCapture) and ruins
            timing.Next(BridgeMod.CityReadPhase.UnitActions);
            //      (the ruin tiles a unit is on or next to). No candidate is invented: only tiles my
            //      own units can actually see are considered, and legality comes from the game.
            // ---- water / Port / embark reads (codex-dsh-063). Every answer comes from THIS build's own
            //      rules: GameLogicData.CanBuild for a Port on a tile, TileDataExtensions.
            //      HasEmbarkImprovement, ActionUtils.CanPlayerEmbark, ActionUtils.WillUnitEmbark and
            //      UnitDataExtensions.CanDisembark. Only tiles this player has EXPLORED are considered
            //      (TileData.GetExplored), so a no-bot match is not an excuse to read the whole map.
            var actionUtilsType = BridgeMod.FindGameType("ActionUtils");
            var tileExtType = BridgeMod.FindGameType("TileDataExtensions");
            var unitExtType = BridgeMod.FindGameType("UnitDataExtensions");
            var canPlayerEmbarkM = BridgeMod.FindStaticUnique(actionUtilsType, "CanPlayerEmbark",
                new[] { "GameState", "PlayerState" }, "Boolean");
            var willUnitEmbarkM = BridgeMod.FindStaticUnique(actionUtilsType, "WillUnitEmbark",
                new[] { "UnitState", "TileData", "GameState" }, null);
            var canDisembarkM = BridgeMod.FindStaticUnique(unitExtType, "CanDisembark",
                new[] { "UnitState", "GameState" }, "Boolean");
            var hasEmbarkM = BridgeMod.FindStaticUnique(tileExtType, "HasEmbarkImprovement",
                new[] { "TileData", "GameState" }, "Boolean");
            if (canPlayerEmbarkM == null) { reasons.Add("canplayerembark-not-bound"); }
            if (willUnitEmbarkM == null) { reasons.Add("willunitembark-not-bound"); }
            if (canDisembarkM == null) { reasons.Add("candisembark-not-bound"); }
            if (hasEmbarkM == null) { reasons.Add("hasembarkimprovement-not-bound"); }

            string playerCanEmbark = null;
            if (canPlayerEmbarkM != null)
            {
                try
                {
                    var r = canPlayerEmbarkM.Invoke(null, new object[] { gs, ps });
                    playerCanEmbark = r is bool ? ((bool)r ? "true" : "false") : "null";
                }
                catch (Exception ex) { reasons.Add("canplayerembark-threw:" + ExName(ex)); }
            }

            // The Port improvement's own data (cost) for THIS build, and the enum value to test with.
            Type impEnumType = BridgeMod.FindGameType("ImprovementData+Type");
            object portImp = null;
            if (impEnumType == null) { reasons.Add("improvementdatatype-not-found"); }
            else if (getImprovementData != null)
            {
                try
                {
                    portImp = getImprovementData.Invoke(logic,
                        new object[] { Enum.Parse(impEnumType, "Port", true) });
                }
                catch (Exception ex) { reasons.Add("port-improvementdata-threw:" + ExName(ex)); }
            }

            // My own land tiles, from the tiles list itself (owner == me and not water): used only to
            // say which explored water tiles lie on my coast, never as a substitute for CanBuild.
            var myLandCoords = new List<string>();
            foreach (var tile in tiles)
            {
                if (tile == null) { continue; }
                var ow = MI(tile, "owner");
                if (!ow.HasValue || ow.Value != me) { continue; }
                var isWaterOwn = MB(tile, "IsWater");
                if (isWaterOwn.HasValue && !isWaterOwn.Value)
                {
                    var cc = S(M(tile, "coordinates"));
                    if (cc != null) { myLandCoords.Add(cc); }
                }
            }

            // Explored water tiles (fog-gated by the game's own GetExplored).
            var exploredWater = new List<object>();
            var waterMemo = new Dictionary<string, bool?>(StringComparer.Ordinal);
            foreach (var tile in tiles)
            {
                if (tile == null) { continue; }
                var w = MB(tile, "IsWater");
                if (!w.HasValue || !w.Value) { continue; }
                var c1 = S(M(tile, "coordinates"));
                if (c1 == null) { continue; }
                bool? explored1;
                if (!waterMemo.TryGetValue(c1, out explored1))
                {
                    var exw = Inst(tile.GetType(), "GetExplored", 1, new[] { "Byte" });
                    if (exw == null) { reasons.Add("getexplored-not-bound"); explored1 = null; }
                    else { explored1 = reads.Explored(tile); }
                    waterMemo[c1] = explored1;
                }
                if (explored1 == true) { exploredWater.Add(tile); }
            }

            var unitActions = new List<string>();
            {
                var ext = BridgeMod.FindGameType("UnitDataExtensions");
                // CanCapture is STATIC in this build (Mono.Cecil: static Boolean CanCapture(UnitState,
                // GameState, TileData, Boolean includeNextTurn, Boolean allowOnlyOnTile)).
                var canCapture = BridgeMod.FindStaticUnique(ext, "CanCapture",
                    new[] { "UnitState", "GameState", "TileData", "Boolean", "Boolean" }, "Boolean");
                if (canCapture == null) { reasons.Add("cancapture-not-bound"); }
                var cityTiles = new List<object>();
                var ruinTiles = new List<object>();
                // Visibility is decided by the GAME's own explored rule for this player - the same
                // authority the world observation uses (fog.exploredSource = GetExplored(Byte)) - and
                // never by "it happens to be next to one of my units". A tile whose explored flag
                // cannot be read is skipped entirely (fail closed).
                var exploredMemo = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var tile in tiles)
                {
                    if (tile == null) { continue; }
                    var imp0 = M(tile, "improvement");
                    if (imp0 == null) { continue; }
                    var itype0 = S(M(imp0, "type"));
                    if (itype0 != "City" && itype0 != "Ruin") { continue; }
                    var c0 = S(M(tile, "coordinates"));
                    bool explored = false;
                    if (c0 != null && exploredMemo.TryGetValue(c0, out explored))
                    { /* memoised */ }
                    else
                    {
                        var ex = Inst(tile.GetType(), "GetExplored", 1, new[] { "Byte" });
                        if (ex == null) { reasons.Add("getexplored-not-bound"); explored = false; }
                        else { explored = reads.Explored(tile) == true; }
                        if (c0 != null) { exploredMemo[c0] = explored; }
                    }
                    if (!explored) { continue; }
                    if (itype0 == "City") { cityTiles.Add(tile); }
                    else { ruinTiles.Add(tile); }
                }
                foreach (var tile in tiles)
                {
                    if (tile == null) { continue; }
                    bool filtered;
                    object unit = null;
                    try { unit = BridgeMod.TileUnitVisible(tile, gs, me, out filtered); }
                    catch (Exception) { unit = null; filtered = false; }
                    if (unit == null || !filtered || MI(unit, "owner") != me) { continue; }
                    var coordText = S(M(tile, "coordinates"));
                    var ua = new StringBuilder();
                    var movedFlag = MB(unit, "moved");
                    var attackedFlag = MB(unit, "attacked");
                    // The game's OWN predicates, not "!moved" (which misses e.g. Frozen and any other
                    // rule the game applies) and not the attacked flag alone.
                    var canMoveFlag = MB(unit, "CanMove");
                    var canAttackFlag = MB(unit, "CanAttack");
                    ua.Append("{\"unitId\":").Append(Q(S(M(unit, "id"))))
                      .Append(",\"type\":").Append(Q(S(M(unit, "type"))))
                      .Append(",\"from\":").Append(Q(coordText))
                      .Append(",\"moved\":").Append(movedFlag.HasValue
                            ? (movedFlag.Value ? "true" : "false") : "null")
                      .Append(",\"attacked\":").Append(attackedFlag.HasValue
                            ? (attackedFlag.Value ? "true" : "false") : "null")
                      .Append(",\"canMove\":").Append(canMoveFlag.HasValue
                            ? (canMoveFlag.Value ? "true" : "false") : "null")
                      .Append(",\"canAttack\":").Append(canAttackFlag.HasValue
                            ? (canAttackFlag.Value ? "true" : "false") : "null");

                    var captures = new List<string>();
                    foreach (var cityTile in cityTiles)
                    {
                        var cityOwner = MI(cityTile, "owner");
                        var cityCoord = S(M(cityTile, "coordinates"));
                        bool standing = cityCoord != null && cityCoord == coordText;
                        bool adjacent = Adjacent(cityCoord, coordText);
                        if (!standing && !adjacent) { continue; }
                        bool? legal = null;
                        if (canCapture != null)
                        {
                            try
                            {
                                var r = canCapture.Invoke(null, new object[] { unit, gs, cityTile,
                                                                             false, true });
                                legal = r is bool ? (bool)r : (bool?)null;
                            }
                            catch (Exception ex) { reasons.Add("cancapture-threw:" + ExName(ex)); }
                        }
                        var blocked = new List<string>();
                        if (standing == false) { blocked.Add("unit-not-on-the-target-tile"); }
                        if (movedFlag.HasValue && movedFlag.Value) { blocked.Add("moved-this-turn"); }
                        if (attackedFlag.HasValue && attackedFlag.Value) { blocked.Add("already-attacked"); }
                        var capSb = new StringBuilder();
                        capSb.Append("{\"at\":").Append(Q(cityCoord))
                            .Append(",\"cityOwner\":").Append(cityOwner.HasValue
                                ? cityOwner.Value.ToString(CultureInfo.InvariantCulture) : "null")
                            .Append(",\"standingOnIt\":").Append(standing ? "true" : "false")
                            .Append(",\"canCapture\":").Append(legal.HasValue
                                ? (legal.Value ? "true" : "false") : "null")
                            .Append(",\"blockedBy\":[").Append(string.Join(",", blocked.ConvertAll(
                                x => Q(x)).ToArray())).Append("]}");
                        captures.Add(capSb.ToString());
                    }
                    ua.Append(",\"capture\":[").Append(string.Join(",", captures.ToArray())).Append("]");

                    var ruinsHere = new List<string>();
                    foreach (var ruinTile in ruinTiles)
                    {
                        var ruinCoord = S(M(ruinTile, "coordinates"));
                        bool standing = ruinCoord != null && ruinCoord == coordText;
                        bool adjacent = Adjacent(ruinCoord, coordText);
                        if (!standing && !adjacent) { continue; }
                        var ruinSb = new StringBuilder();
                        ruinSb.Append("{\"at\":").Append(Q(ruinCoord))
                            .Append(",\"standingOnIt\":").Append(standing ? "true" : "false")
                            .Append(",\"adjacent\":").Append(adjacent ? "true" : "false")
                            .Append(",\"terrain\":").Append(Q(S(M(ruinTile, "terrain"))))
                            .Append("}");
                        ruinsHere.Add(ruinSb.ToString());
                    }
                    ua.Append(",\"ruins\":[").Append(string.Join(",", ruinsHere.ToArray())).Append("]");

                    // Disembark legality from the game's own predicate, and where this unit would
                    // embark: ActionUtils.WillUnitEmbark on each explored water tile within 2 tiles.
                    if (canDisembarkM != null)
                    {
                        try
                        {
                            var rd = canDisembarkM.Invoke(null, new object[] { unit, gs });
                            ua.Append(",\"canDisembark\":").Append(rd is bool && (bool)rd ? "true" : "false");
                        }
                        catch (Exception ex)
                        {
                            ua.Append(",\"canDisembark\":null");
                            reasons.Add("candisembark-threw:" + ExName(ex));
                        }
                    }
                    else { ua.Append(",\"canDisembark\":null"); }
                    var embarkList = new List<string>();
                    int waterNear = 0;
                    if (willUnitEmbarkM != null)
                    {
                        for (int wi = 0; wi < exploredWater.Count; wi++)
                        {
                            var wt = exploredWater[wi];
                            var wc = S(M(wt, "coordinates"));
                            int dist = Cheb(wc, coordText);
                            if (dist < 0 || dist > 2) { continue; }
                            waterNear++;
                            string status = null;
                            try
                            {
                                var rs = willUnitEmbarkM.Invoke(null, new object[] { unit, wt, gs });
                                status = rs == null ? "null" : rs.ToString();
                            }
                            catch (Exception ex) { reasons.Add("willunitembark-threw:" + ExName(ex)); }
                            if (status == null || status == "None" || embarkList.Count >= 8) { continue; }
                            var hasPortTxt = "null";
                            if (hasEmbarkM != null)
                            {
                                try
                                {
                                    var rp = hasEmbarkM.Invoke(null, new object[] { wt, gs });
                                    hasPortTxt = rp is bool && (bool)rp ? "true" : "false";
                                }
                                catch (Exception) { hasPortTxt = "null"; }
                            }
                            embarkList.Add("{\"at\":" + Q(wc)
                                + ",\"cheb\":" + dist.ToString(CultureInfo.InvariantCulture)
                                + ",\"status\":" + Q(status)
                                + ",\"hasEmbarkImprovement\":" + hasPortTxt + "}");
                        }
                    }
                    ua.Append(",\"embarkTargets\":[").Append(string.Join(",", embarkList.ToArray()))
                      .Append("],\"exploredWaterWithin2\":")
                      .Append(waterNear.ToString(CultureInfo.InvariantCulture)).Append("}");
                    unitActions.Add(ua.ToString());
                }
            }

            // ---- Port build options per explored coastal water tile (codex-dsh-063). The resource
            timing.Next(BridgeMod.CityReadPhase.Ports);
            //      list above only covers tiles that carry a resource; a Port stands on open water,
            //      so the legality here comes from GameLogicData.CanBuild on this tile and the Port
            //      improvement's own cost. Tiles far from my land are skipped to keep the read small.
            var portOptions = new List<string>();
            bool? portUnlocked = null;
            var portUnlockMethod = Inst(logicType, "GetUnlockedImprovements", 1, new[] { "PlayerState" });
            List<object> snapshotUnlocked = null;
            string unlockedReadError = null;
            try
            {
                snapshotUnlocked = portUnlockMethod == null ? null : AsList(portUnlockMethod.Invoke(logic, new object[] { ps }));
                portUnlocked = TaskImprovementMembership(snapshotUnlocked, "Port", true);
            }
            catch (Exception ex) { unlockedReadError = "getunlockedimprovements-threw:" + ExName(ex); }
            var portCost = MI(portImp, "cost");
            if (impEnumType != null)
            {
                foreach (var wt in exploredWater)
                {
                    if (wt == null) { continue; }
                    var wc = S(M(wt, "coordinates"));
                    if (wc == null) { continue; }
                    bool nearMine = false;
                    for (int i = 0; i < myLandCoords.Count; i++)
                    {
                        if (Cheb(wc, myLandCoords[i]) == 1) { nearMine = true; break; }
                    }
                    var ownW = MI(wt, "owner");
                    int ownVal = ownW.HasValue ? ownW.Value : -1;
                    if (!nearMine && ownVal != me) { continue; }
                    var impW = M(wt, "improvement");
                    var impTypeW = impW == null ? null : S(M(impW, "type"));
                    var wo = new StringBuilder();
                    wo.Append("{\"coords\":").Append(Q(wc))
                      .Append(",\"owner\":").Append(ownVal >= 0
                            ? ownVal.ToString(CultureInfo.InvariantCulture) : "null")
                      .Append(",\"terrain\":").Append(Q(S(M(wt, "terrain"))))
                      .Append(",\"existingImprovement\":").Append(Q(impTypeW))
                      .Append(",\"adjacentToMyLand\":").Append(nearMine ? "true" : "false");
                    if (hasEmbarkM != null)
                    {
                        try
                        {
                            var rp = hasEmbarkM.Invoke(null, new object[] { wt, gs });
                            wo.Append(",\"hasEmbarkImprovement\":")
                              .Append(rp is bool && (bool)rp ? "true" : "false");
                        }
                        catch (Exception ex)
                        {
                            wo.Append(",\"hasEmbarkImprovement\":null");
                            reasons.Add("hasembarkimprovement-threw:" + ExName(ex));
                        }
                    }
                    else { wo.Append(",\"hasEmbarkImprovement\":null"); }
                    wo.Append(",\"portCost\":").Append(Num(portCost));
                    wo.Append(",\"currentlyUnlocked\":").Append(BJ(portUnlocked));
                    if (portUnlocked == false) { wo.Append(",\"canBuildPort\":false"); }
                    else if (portUnlocked == true && canBuild != null && portImp != null)
                    {
                        try
                        {
                            var rb = canBuild.Invoke(logic, new object[] { gs, wt, ps, portImp });
                            wo.Append(",\"canBuildPort\":")
                              .Append(rb is bool && (bool)rb ? "true" : "false");
                        }
                        catch (Exception ex)
                        {
                            wo.Append(",\"canBuildPort\":null");
                            reasons.Add("canbuild-port-threw:" + ExName(ex));
                        }
                    }
                    else { wo.Append(",\"canBuildPort\":null"); }
                    wo.Append("}");
                    portOptions.Add(wo.ToString());
                }
            }

            // ---- what can be built on each of MY explored tiles (codex-dsh-064; leak fixed in 065).
            timing.Next(BridgeMod.CityReadPhase.TileOptions);
            //      Only the rows a player's own build menu would offer are emitted:
            //        * the improvement must be in GameLogicData.GetUnlockedImprovements(PlayerState)
            //          (that is the owned-tech condition, NOT "the tech is still researchable"), and
            //        * GameLogicData.CanBuild(gs, tile, ps, improvementData) must hold (tile rules),
            //          and
            //        * for a resource-dependent building (Mine<-Metal, Farm<-Crop) the tile's resource
            //          must be VISIBLE through BridgeMod.TileResourceVisible. Raw CanBuild would
            //          otherwise reveal where hidden (tech-gated) resources are: 064 listed Mine on
            //          three Metal tiles before Climbing/Mining were researched.
            //      If the unlocked list cannot be read, NO rows are emitted and a concise reason is
            //      reported - an unreadable answer is never turned into "nothing is unlocked".
            var getUnlockedImprovements = portUnlockMethod;
            var tileOptions = new List<string>();
            var unlockedNames = new List<string>();
            var unlockedData = new List<object>();
            var unlockedCatalog = new List<string>();
            bool tileOptionsAvailable = true;
            if (getUnlockedImprovements == null)
            {
                reasons.Add("tileoptions-unavailable:getunlockedimprovements-not-bound");
                tileOptionsAvailable = false;
            }
            else if (impEnumType == null)
            {
                reasons.Add("tileoptions-unavailable:improvementdatatype-not-found");
                tileOptionsAvailable = false;
            }
            else if (getImprovementData == null)
            {
                reasons.Add("tileoptions-unavailable:getimprovementdata-not-bound");
                tileOptionsAvailable = false;
            }
            else
            {
                var unlockedList = snapshotUnlocked;
                if (unlockedReadError != null) { reasons.Add(unlockedReadError); }
                if (unlockedList == null)
                {
                    reasons.Add("tileoptions-unavailable:getunlockedimprovements-unreadable");
                    tileOptionsAvailable = false;
                }
                else
                {
                    foreach (var imp3 in unlockedList)
                    {
                        // Preserve unreadable entries for the task membership projection: an
                        // incomplete native list must not prove that a reward is locked.
                        unlockedData.Add(imp3);
                        if (imp3 != null) { unlockedNames.Add(S(M(imp3, "type"))); }
                    }
                }
            }
            bool? unlockedCatalogComplete = tileOptionsAvailable
                ? (bool?)ImprovementListComplete(unlockedData) : null;
            bool tileOptionsComplete = tileOptionsAvailable && unlockedCatalogComplete == true && canBuild != null;
            if (unlockedCatalogComplete == false) { reasons.Add("getunlockedimprovements-members-unreadable"); }
            // Adjacency helpers for the emitted rows (same native pieces the standalone query uses).
            var actionUtilsType2 = BridgeMod.FindGameType("ActionUtils");
            var getBonus2 = BridgeMod.FindStaticUnique(actionUtilsType2, "GetAdjacencyBonusAt",
                new[] { "GameState", "TileData", "ImprovementData" }, "Int32");
            var calcLevel2 = BridgeMod.FindStaticUnique(actionUtilsType2, "CalculateImprovementLevel",
                new[] { "GameState", "TileData" }, "Int32");
            var calcWork2 = BridgeMod.FindStaticUnique(BridgeMod.FindGameType("TileDataExtensions"),
                "CalculateWork", new[] { "TileData", "GameState", "PlayerState", "Int32" }, "Int32");
            var byCoordAll = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var t9 in tiles)
            {
                if (t9 == null) { continue; }
                var c9 = S(M(t9, "coordinates"));
                if (c9 != null && !byCoordAll.ContainsKey(c9)) { byCoordAll[c9] = t9; }
            }
            if (tileOptionsAvailable)
            {
                foreach (var tile in tiles)
                {
                    if (tile == null) { tileOptionsComplete = false; continue; }
                    var ow2 = MI(tile, "owner");
                    if (!ow2.HasValue) { tileOptionsComplete = false; continue; }
                    if (ow2.Value != me) { continue; }
                    var c2 = S(M(tile, "coordinates"));
                    if (c2 == null) { tileOptionsComplete = false; continue; }
                    bool? explored2;
                    if (!waterMemo.TryGetValue(c2, out explored2))
                    {
                        explored2 = reads.Explored(tile);
                        waterMemo[c2] = explored2;
                    }
                    if (!explored2.HasValue) { tileOptionsComplete = false; }
                    if (explored2 != true) { continue; }
                    bool filteredRes;
                    object visibleResource = null;
                    try { visibleResource = reads.Resource(tile, out filteredRes); }
                    catch (Exception) { visibleResource = null; filteredRes = false; tileOptionsComplete = false; }
                    if (!filteredRes) { tileOptionsComplete = false; }
                    string visibleRes = (visibleResource != null && filteredRes)
                        ? S(M(visibleResource, "type")) : null;
                    var impHere = M(tile, "improvement");
                    string existing = impHere == null ? null : S(M(impHere, "type"));
                    foreach (var candImp in unlockedData)
                    {
                        var candName = S(M(candImp, "type"));
                        if (candName == null || MB(candImp, "hidden") == true) { continue; }
                        // Not in the player's own build list -> not a legal option to report, and its
                        // absence must not be turned into a per-tile guess.
                        if (!unlockedNames.Contains(candName)) { continue; }
                        // Resource-dependent candidates are derived from the FILTERED visible resource
                        // only, so a hidden resource never shows up through CanBuild.
                        // Requirements come from this improvement's data, including special tribes.
                        // Check before CanBuild: a successful hidden-resource predicate would itself
                        // reveal a location even if the resource field were subsequently omitted.
                        bool? resourceGate = ResourceRequirementsVisible(candImp,
                            S(M(tile, "terrain")), visibleRes);
                        if (resourceGate != true)
                        { if (!resourceGate.HasValue) { reasons.Add("resource-requirements-unreadable:" + candName); tileOptionsComplete = false; } continue; }
                        string canB2 = "null";
                        if (canBuild != null)
                        {
                            try
                            {
                                var rb = canBuild.Invoke(logic, new object[] { gs, tile, ps, candImp });
                                canB2 = NativeBooleanJson(rb, reasons, "canbuild-tile-returned-non-boolean");
                            }
                            catch (Exception ex) { reasons.Add("canbuild-tile-threw:" + ExName(ex)); }
                        }
                        if (canB2 == "null") { tileOptionsComplete = false; }
                        if (canB2 != "true") { continue; }
                        int? cst2 = MI(candImp, "cost");
                        var to = new StringBuilder();
                        to.Append("{\"coords\":").Append(Q(c2))
                          .Append(",\"terrain\":").Append(Q(S(M(tile, "terrain"))))
                          .Append(",\"existingImprovement\":").Append(Q(existing))
                          .Append(",\"improvement\":").Append(Q(candName))
                          .Append(",\"cost\":").Append(cst2.HasValue
                                ? cst2.Value.ToString(CultureInfo.InvariantCulture) : "null")
                          .Append(",\"canBuild\":").Append(canB2)
                          .Append(",\"currentlyUnlocked\":true")
                          .Append(",\"unlockAuthority\":\"native GetUnlockedImprovements; may include task rewards\"")
                          .Append(",\"buildableNow\":").Append(stars.HasValue && cst2.HasValue
                                ? (stars.Value >= cst2.Value ? "true" : "false") : "null")
                          .Append(",\"rulingCityCoordinates\":").Append(Q(S(M(tile, "rulingCityCoordinates"))))
                          .Append(",\"hasRoad\":").Append(BJ(MB(tile, "HasRoad")))
                          .Append(",\"constructionRestrictions\":").Append(ConstructionRestrictions(candImp))
                          .Append(",\"populationGainNative\":").Append(PopulationGain(gs, tile, candImp))
                          .Append(",\"visibleResource\":").Append(Q(visibleRes))
                          .Append(",\"affordable\":").Append(stars.HasValue && cst2.HasValue
                                ? (stars.Value >= cst2.Value ? "true" : "false") : "null")
                          .Append(",\"adjacency\":")
                          .Append(AdjacencyBlock(gs, me, byCoordAll, tile, candImp,
                                getBonus2, calcLevel2, calcWork2, ps, reasons, false, reads))
                          .Append("}");
                        tileOptions.Add(to.ToString());
                    }
                }
            }

            // The catalog is current unlocked data, not a per-tile permission claim. Task unlocks
            // remain native: GetUnlockedImprovements + CanBuild; no monument is granted by us.
            foreach (var data in unlockedData)
            {
                if (data == null || S(M(data, "type")) == null || MB(data, "hidden") == true) { continue; }
                unlockedCatalog.Add("{\"improvement\":" + Q(S(M(data, "type")))
                    + ",\"name\":" + Q(S(M(data, "displayName")))
                    + ",\"cost\":" + NJ(MI(data, "cost"))
                    + ",\"constructionRestrictions\":" + ConstructionRestrictions(data)
                    + ",\"perTilePermission\":\"consult tileOptions\"}");
            }
            timing.Next(BridgeMod.CityReadPhase.EconomicTiles);
            var economicTiles = new List<string>();
            foreach (var tile in tiles)
            {
                if (tile == null || MI(tile, "owner") != me || reads.Explored(tile) != true) { continue; }
                var imp = M(tile, "improvement");
                economicTiles.Add("{\"coords\":" + Q(S(M(tile, "coordinates")))
                    + ",\"rulingCityCoordinates\":" + Q(S(M(tile, "rulingCityCoordinates")))
                    + ",\"hasRoad\":" + BJ(MB(tile, "HasRoad"))
                    + ",\"hasRoute\":" + BJ(MB(tile, "hasRoute"))
                    // The native getter is defined only for city tiles; non-city
                    // connection is unknown rather than its warning-path value.
                    + ",\"isConnected\":" + BJ(S(M(imp, "type")) == "City" ? MB(tile, "IsConnected") : null)
                    + ",\"improvement\":" + Q(S(M(imp, "type")))
                    + ",\"population\":" + NJ(MI(imp, "population"))
                    + ",\"growth\":" + BridgeMod.BuildingGrowthSnapshot(gs, tile, ps) + "}");
            }
            // ---- the player's own score, this build's reward table, and any pending reward choice
            timing.Next(BridgeMod.CityReadPhase.Rewards);
            // (codex-dsh-070). `stars` stays the current star STOCK: score, per-turn city production
            // and a reward's production modifier are separate quantities and are never mixed.
            var playerScore = MI(ps, "score");
            var getPlayerScore = StaticMethod("PlayerExtensions", "GetScore",
                new[] { "PlayerState" }, "UInt32");
            var cityRewardJson = CityRewardDataJson(rewardListGetter, rewardProdMod, gs, parkScore,
                populationReward, resourceReward);
            var pendingRewardJson = PendingCityRewardJson(gs, me, rewardProdMod, parkScore,
                populationReward, resourceReward, reasons);

            timing.Next(BridgeMod.CityReadPhase.Serialization);
            var sb = new StringBuilder();
            sb.Append("{\"schema\":").Append(Q(Schema))
              .Append(",\"status\":").Append(Q(reasons.Count == 0 ? "ok" : "partial"))
              .Append(",\"controlledPlayerId\":").Append(me.ToString(CultureInfo.InvariantCulture))
              .Append(",\"stars\":").Append(stars.HasValue
                    ? stars.Value.ToString(CultureInfo.InvariantCulture) : "null")
              .Append(",\"currentState\":").Append(Q(S(M(gs, "CurrentState")) ?? CurrentStateName(gs)))
              .Append(",\"authority\":")
              .Append(Q("GameLogicData of THIS GameState: GetUnlockableImprovements / "
                        + "GetActionableImprovementForResource / CanBuild (resources and Port) / "
                        + "GetImprovementData.cost / GetUnitData.cost / GetUnlockedUpgradesForUnit / "
                        + "CityRewardData text; ActionUtils.CanPlayerEmbark / ActionUtils.WillUnitEmbark / "
                        + "UnitDataExtensions.CanDisembark / TileDataExtensions.HasEmbarkImprovement; "
                        + "tileOptions = GameLogicData.GetUnlockedImprovements(PlayerState) AND "
                        + "GameLogicData.CanBuild, with resource-dependent rows (Mine/Farm) additionally "
                        + "requiring BridgeMod.TileResourceVisible; TileData.GetExplored for visibility; "
                        + "cityState/chosenRewards = ImprovementState (production/baseScore/xp/borderSize/"
                        + "rewards/effects) with CityRewardData.GetRewardProductionModifier for each "
                        + "already-taken reward; cityState.garrison = UnitDataExtensions.GetDefence/"
                        + "GetDefenceBonus/GetAttack/GetMaxHealth for MY unit on that city tile; "
                        + "cityRewardData = CityRewardData.get_cityRewards + ProductionModifier + "
                        + "PARK_SCORE/POPULATION_REWARD/RESOURCE_REWARD; pendingCityReward = "
                        + "GameState.pendingCommandTriggers[CityLevelUp, mine] + the open RewardPopup's "
                        + "cityRewards; playerScore = PlayerState.score"))
              .Append(",\"counts\":{\"tiles\":").Append(tiles.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\"ownTiles\":").Append(ownTiles.ToString(CultureInfo.InvariantCulture))
              .Append(",\"cities\":").Append(cities.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\"resourceTiles\":").Append(resourceOptions.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\"myUnits\":").Append(unitOptions.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\"improvementsFromUnlockableTechs\":")
              .Append(buildable.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\"myUnitActions\":").Append(unitActions.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\"exploredWaterTiles\":").Append(exploredWater.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\"portOptions\":").Append(portOptions.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\"tileOptions\":").Append(tileOptions.Count.ToString(CultureInfo.InvariantCulture))
              .Append("},\"playerCanEmbark\":").Append(playerCanEmbark == null ? "null" : playerCanEmbark)
              .Append(",\"currentUnlockedImprovements\":").Append(tileOptionsAvailable
                    ? "[" + string.Join(",", unlockedCatalog.ToArray()) + "]" : "null")
              .Append(",\"currentUnlockedImprovementsComplete\":").Append(BJ(unlockedCatalogComplete))
              .Append(",\"tileOptionsComplete\":").Append(tileOptionsAvailable ? BJ(tileOptionsComplete) : "null")
              .Append(",\"ownedEconomicTiles\":[").Append(string.Join(",", economicTiles.ToArray())).Append("]")
              .Append(",\"tasks\":").Append(TasksJson(gs, ps, logic,
                    tileOptionsAvailable ? unlockedData : null))
              .Append(",\"populationSettlement\":{\"status\":\"unknown\",\"reason\":\"snapshot alone does not prove pending command reactions settled; consult settlement\"}")
              .Append(",\"cityUpgradeSemantics\":{\"canCityBeUpgradedRole\":\"cached-native-construction-potential\","
                    + "\"canCityBeUpgradedPersistence\":\"not-guaranteed-across-save-resume\","
                    + "\"populationRequirementField\":\"populationNeededToUpgrade\","
                    + "\"executableChoiceField\":\"pendingCityReward\","
                    + "\"authority\":\"TileData.CanCityBeUpgraded(GameState) uses native lastPopulationCheck/availablePopulation caches; "
                    + "TileData.PopulationNeededToUpgradeCity supplies the native population requirement; "
                    + "only current pendingCityReward choices authorize a reward response\"}")
              .Append(",\"portOptions\":[").Append(string.Join(",", portOptions.ToArray()))
              .Append("],\"tileOptions\":[").Append(string.Join(",", tileOptions.ToArray()))
              .Append("],\"resourceOptions\":[").Append(string.Join(",", resourceOptions.ToArray()))
              .Append("],\"myCities\":[").Append(string.Join(",", cities.ToArray()))
              // Future-tech options keep an explicit name. Schema 6 corrects the historical
              // buildableImprovements alias to refer to CURRENT unlocked catalog data instead.
              .Append("],\"improvementsFromUnlockableTechs\":[")
              .Append(string.Join(",", buildable.ToArray()))
              .Append("],\"buildableImprovementsDeprecated\":true")
              .Append(",\"buildableImprovementsSemantics\":\"schema 6: deprecated alias of currentUnlockedImprovements; per-tile eligibility and affordability are in tileOptions\"")
              .Append(",\"buildableImprovements\":").Append(tileOptionsAvailable
                    ? "[" + string.Join(",", unlockedCatalog.ToArray()) + "]" : "null")
              .Append(",\"myUnitOptions\":[").Append(string.Join(",", unitOptions.ToArray()))
              .Append("],\"myUnitActions\":[").Append(string.Join(",", unitActions.ToArray()))
              // ---- player score + the reward table + any pending reward choice (codex-dsh-070).
              // `stars` above is the current star STOCK: score, a city's per-turn production and a
              // reward's production modifier are separate quantities and are never mixed here.
              .Append("],\"playerScore\":").Append(Num(playerScore))
              .Append(",\"playerScoreViaGetScore\":").Append(Num(CallGetter1(getPlayerScore, ps)))
              // The game's own per-turn star income for me (ResourceDataUtils.CalculateIncomeFor).
              // This is a DIFFERENT quantity from `stars` (the current stock) and from a city's
              // production value: the three are reported separately and never mixed.
              .Append(",\"incomePerTurn\":").Append(Num(CallInt(incomeFor, gs, me)))
              .Append(",\"incomeAuthority\":").Append(Q("ResourceDataUtils.CalculateIncomeFor("
                  + "GameState, playerId) - the game's own per-turn star income; NOT the current star "
                  + "stock in `stars` and not the sum of cityState.production"))
              .Append(",\"cityRewardData\":").Append(cityRewardJson)
              .Append(",\"pendingCityReward\":").Append(pendingRewardJson)
              .Append(",\"reasonCodes\":[");
            for (int i = 0; i < reasons.Count; i++)
            {
                if (i > 0) { sb.Append(","); }
                sb.Append(Q(reasons[i]));
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ---- adjacency query (codex-dsh-066, visibility/fail-closed fixes codex-dsh-067) ------
        // Input  : "(x,y):ImprovementType"   (also accepts '|' or '/' as the separator)
        // Output : how many of the EIGHT neighbours (diagonals included, centre excluded) are inside
        //          MY territory and count as valid buildings for that improvement, with the counted
        //          coordinates and why each one counts.
        // Visibility rules kept: the TARGET must be my own tile and EXPLORED, otherwise the query is
        // simply unavailable and nothing about that tile is read or printed. Neighbour tiles are only
        // inspected when they are explored; unexplored or unreadable neighbours make the count null
        // (never 0), and foreign neighbours are reported as "not-my-territory" WITHOUT printing their
        // buildings or resources. Map-edge coordinates outside the map are excluded normally.

        /// <summary>Parse "(x,y)" into two ints.</summary>
        private static bool TryParsePair(string text, out int x, out int y)
        {
            x = 0; y = 0;
            if (string.IsNullOrEmpty(text)) { return false; }
            var p = text.Trim('(', ')').Split(',');
            if (p.Length != 2) { return false; }
            return int.TryParse(p[0].Trim(), out x) && int.TryParse(p[1].Trim(), out y);
        }

        /// <summary>Explored flag of a tile, or null when the game cannot answer.</summary>
        private static bool? ExploredOf(object tile, byte me)
        {
            if (tile == null) { return null; }
            var ex = Inst(tile.GetType(), "GetExplored", 1, new[] { "Byte" });
            if (ex == null) { return null; }
            try
            {
                var r = ex.Invoke(tile, new object[] { me });
                return r is bool ? (bool?)r : null;
            }
            catch (Exception) { return null; }
        }

        public static string AdjacencyQuery(string spec)
        {
            var reasons = new List<string>();
            string coord = null, impName = null;
            if (!string.IsNullOrEmpty(spec))
            {
                var parts = spec.Split(new[] { ':', '|', '/' });
                if (parts.Length >= 2) { coord = parts[0].Trim(); impName = parts[1].Trim(); }
            }
            if (coord == null || impName == null || impName.Length == 0)
            {
                reasons.Add("spec must be \"(x,y):ImprovementType\"");
                return QueryUnavailable(coord, impName, reasons);
            }

            var gm = BridgeMod.FindGameType("GameManager");
            object gs = null, lp = null;
            try
            {
                if (gm != null)
                {
                    BridgeMod.TryReadNativeStaticMember(gm, "GameState", out gs);
                    BridgeMod.TryReadNativeStaticMember(gm, "LocalPlayer", out lp);
                }
            }
            catch (Exception) { }
            if (gs == null) { reasons.Add("gamestate-unavailable"); return QueryUnavailable(coord, impName, reasons); }
            var idv = MI(lp, "Id");
            if (!idv.HasValue || idv.Value == 255) { reasons.Add("controlled-identity-unavailable"); return QueryUnavailable(coord, impName, reasons); }
            byte me = (byte)idv.Value;
            var logic = LogicOf(gs);
            if (logic == null) { reasons.Add("gamelogicdata-unavailable"); return QueryUnavailable(coord, impName, reasons); }
            var logicType = logic.GetType();
            object ps = null;
            var players = AsList(M(gs, "PlayerStates"));
            if (players != null)
            {
                foreach (var p in players) { if (p != null && MI(p, "Id") == me) { ps = p; break; } }
            }
            if (ps == null) { reasons.Add("playerstate-unavailable"); return QueryUnavailable(coord, impName, reasons); }

            Type impEnumType = BridgeMod.FindGameType("ImprovementData+Type");
            var getImprovementData = Inst(logicType, "GetImprovementData", 1, new[] { "Type" });
            var canBuild = Inst(logicType, "CanBuild", 4,
                new[] { "GameState", "TileData", "PlayerState", "ImprovementData" });
            var getUnlockedImprovements = Inst(logicType, "GetUnlockedImprovements", 1, new[] { "PlayerState" });
            object impEnum = null;
            if (impEnumType == null) { reasons.Add("improvementdatatype-not-found"); }
            else
            {
                try { impEnum = Enum.Parse(impEnumType, impName, true); }
                catch (Exception exBad) { reasons.Add("bad improvement name '" + impName + "': " + exBad.Message); }
            }
            object impData = null;
            if (impEnum != null && getImprovementData != null)
            {
                try { impData = getImprovementData.Invoke(logic, new object[] { impEnum }); }
                catch (Exception exGid) { reasons.Add("getimprovementdata-threw:" + ExName(exGid)); }
            }
            if (impData == null) { reasons.Add("improvement-data-unavailable"); return QueryUnavailable(coord, impName, reasons); }

            var map = M(gs, "Map") ?? M(gs, "map");
            var tiles = AsList(M(map, "Tiles") ?? M(map, "tiles"));
            var byCoord = new Dictionary<string, object>(StringComparer.Ordinal);
            if (tiles != null)
            {
                foreach (var t in tiles)
                {
                    if (t == null) { continue; }
                    var c = S(M(t, "coordinates"));
                    if (c != null && !byCoord.ContainsKey(c)) { byCoord[c] = t; }
                }
            }
            object tile = null;
            byCoord.TryGetValue(coord, out tile);
            if (tile == null)
            {
                reasons.Add("target-tile-not-in-this-player-view");
                return QueryUnavailable(coord, impName, reasons);
            }
            // Visibility gate FIRST: an unexplored or unreadable target is simply unavailable, and no
            // field of that tile (improvement, CanBuild, native numbers) is read or printed.
            var explored = ExploredOf(tile, me);
            if (explored == null) { reasons.Add("target-tile-explored-state-unreadable"); return QueryUnavailable(coord, impName, reasons); }
            if (!explored.Value) { reasons.Add("target-tile-not-explored"); return QueryUnavailable(coord, impName, reasons); }
            var targetOwner = MI(tile, "owner");
            if (!targetOwner.HasValue) { reasons.Add("target-tile-owner-unreadable"); return QueryUnavailable(coord, impName, reasons); }
            if (targetOwner.Value != me) { reasons.Add("target-tile-not-mine"); return QueryUnavailable(coord, impName, reasons); }

            // Owned-tech condition: unreadable stays UNKNOWN (null), never false.
            string unlockedTxt = "null";
            if (getUnlockedImprovements == null)
            {
                reasons.Add("getunlockedimprovements-not-bound");
            }
            else
            {
                List<object> ul = null;
                try { ul = AsList(getUnlockedImprovements.Invoke(logic, new object[] { ps })); }
                catch (Exception exUl) { reasons.Add("getunlockedimprovements-threw:" + ExName(exUl)); }
                if (ul == null) { reasons.Add("getunlockedimprovements-unreadable"); }
                else
                {
                    unlockedTxt = BJ(TaskImprovementMembership(ul, S(impEnum), true));
                    if (!ImprovementListComplete(ul)) { reasons.Add("getunlockedimprovements-members-unreadable"); }
                }
            }
            var stars = MI(ps, "Currency");
            var ext = BridgeMod.FindGameType("ActionUtils");
            var getBonus = BridgeMod.FindStaticUnique(ext, "GetAdjacencyBonusAt",
                new[] { "GameState", "TileData", "ImprovementData" }, "Int32");
            var calcLevel = BridgeMod.FindStaticUnique(ext, "CalculateImprovementLevel",
                new[] { "GameState", "TileData" }, "Int32");
            var workExt = BridgeMod.FindGameType("TileDataExtensions");
            var calcWork = BridgeMod.FindStaticUnique(workExt, "CalculateWork",
                new[] { "TileData", "GameState", "PlayerState", "Int32" }, "Int32");

            string canB2 = "null";
            if (canBuild != null)
            {
                try { var r = canBuild.Invoke(logic, new object[] { gs, tile, ps, impData }); canB2 = NativeBooleanJson(r, reasons, "canbuild-returned-non-boolean"); }
                catch (Exception exCb) { reasons.Add("canbuild-threw:" + ExName(exCb)); }
            }
            var placementCost = MI(impData, "cost");
            var existingImprovement = M(tile, "improvement");
            var placement = "{\"unlockedByTech\":" + unlockedTxt
                + ",\"canBuild\":" + canB2
                + ",\"cost\":" + Num(placementCost)
                + ",\"affordable\":" + (stars.HasValue && placementCost.HasValue
                    ? (stars.Value >= placementCost.Value ? "true" : "false") : "null")
                + ",\"existingImprovement\":"
                + Q(S(M(existingImprovement, "type"))) + "}";

            var block = AdjacencyBlock(gs, me, byCoord, tile, impData, getBonus, calcLevel, calcWork,
                ps, reasons, true);
            return QueryReply(coord, impName, impData, placementCost, placement, block, reasons);
        }

        /// <summary>Bind AdjacencyImprovements.HasType for one of its two enum parameter types.</summary>
        private static MethodInfo FindHasType(Type t, string enumOwner)
        {
            if (t == null) { return null; }
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance))
            {
                if (m.Name != "HasType") { continue; }
                var fps = m.GetParameters();
                if (fps.Length != 1) { continue; }
                var fn = fps[0].ParameterType.FullName;
                if (fn != null && fn.IndexOf(enumOwner, StringComparison.Ordinal) >= 0) { return m; }
            }
            return null;
        }

        private static string QueryUnavailable(string coord, string impName, List<string> reasons)
        {
            return "{\"schema\":\"adjacency-1\",\"status\":\"unavailable\",\"query\":{\"coords\":"
                + Q(coord) + ",\"improvement\":" + Q(impName) + "},\"reasonCodes\":["
                + string.Join(",", reasons.ConvertAll(x => Q(x)).ToArray()) + "]}";
        }

        private static string QueryReply(string coord, string impName, object impData, int? cost, string placement,
            string adjacencyBlock, List<string> reasons)
        {
            var sb = new StringBuilder();
            sb.Append("{\"schema\":\"adjacency-1\",\"status\":")
              .Append(Q(reasons.Count == 0 ? "ok" : "partial"))
              .Append(",\"query\":{\"coords\":").Append(Q(coord))
              .Append(",\"improvement\":").Append(Q(impName)).Append("}");
            if (impData != null)
            {
                sb.Append(",\"improvementData\":{\"cost\":")
                  .Append(Num(cost))
                  .Append(",\"work\":").Append(Num(MI(impData, "work")))
                  .Append(",\"maxLevel\":").Append(Num(MI(impData, "maxLevel")))
                  .Append(",\"borderSize\":").Append(Num(MI(impData, "borderSize")))
                  .Append(",\"range\":").Append(Num(MI(impData, "range")))
                  .Append(",\"displayName\":").Append(Q(S(M(impData, "displayName")))).Append("}");
            }
            if (placement != null) { sb.Append(",\"placement\":").Append(placement); }
            if (adjacencyBlock != null) { sb.Append(",\"adjacency\":").Append(adjacencyBlock); }
            sb.Append(",\"reasonCodes\":[")
              .Append(string.Join(",", reasons.ConvertAll(x => Q(x)).ToArray())).Append("]}");
            return sb.ToString();
        }

        private static string AdjacencyBlock(object gs, byte me, Dictionary<string, object> byCoord,
            object tile, object impData, MethodInfo getBonus, MethodInfo calcLevel, MethodInfo calcWork,
            object ps, List<string> reasons, bool withDetail, SnapshotReads reads = null)
        {
            var entries = AsList(M(impData, "adjacencyImprovements"));
            Type entryType = null;
            if (entries != null)
            {
                foreach (var e0 in entries) { if (e0 != null) { entryType = e0.GetType(); break; } }
            }
            if (entryType == null) { entryType = BridgeMod.FindGameType("AdjacencyImprovements"); }
            var hasTypeImp = FindHasType(entryType, "ImprovementData");
            var hasTypeRes = FindHasType(entryType, "ResourceData");
            bool predicateErrorReported = false;

            int borderSize = 1;
            var bs = MI(impData, "borderSize");
            if (bs.HasValue && bs.Value > 0 && bs.Value <= 8) { borderSize = bs.Value; }
            string coord = S(M(tile, "coordinates"));

            var counted = new List<string>();
            var rejected = new List<string>();
            int withinBorder = 0;
            bool complete = true;          // every neighbour's visibility could be decided
            bool anyHiddenNeighbour = false;
            int px, py;
            var map = M(gs, "Map") ?? M(gs, "map");
            int width = -1, height = -1;
            var w1 = MI(map, "Width"); if (w1.HasValue) { width = w1.Value; } else { var w2 = MI(map, "width"); if (w2.HasValue) { width = w2.Value; } }
            var h1 = MI(map, "Height"); if (h1.HasValue) { height = h1.Value; } else { var h2 = MI(map, "height"); if (h2.HasValue) { height = h2.Value; } }

            if (entries == null) { complete = false; reasons.Add("adjacency-rule-list-unreadable"); }
            if (!TryParsePair(coord, out px, out py)) { complete = false; reasons.Add("target-coordinate-unreadable"); }
            if (width <= 0 || height <= 0) { complete = false; reasons.Add("map-size-unreadable"); }

            if (complete)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0) { continue; }
                        int nx = px + dx, ny = py + dy;
                        string nc = "(" + nx.ToString(CultureInfo.InvariantCulture) + ","
                            + ny.ToString(CultureInfo.InvariantCulture) + ")";
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        {
                            // Off the map: not a hidden building, simply not a tile.
                            rejected.Add("{\"coords\":" + Q(nc) + ",\"counted\":false,\"reason\":\"outside-map\"}");
                            continue;
                        }
                        object nt;
                        if (!byCoord.TryGetValue(nc, out nt) || nt == null)
                        {
                            complete = false;
                            anyHiddenNeighbour = true;
                            reasons.Add("neighbour-tile-missing:" + nc);
                            rejected.Add("{\"coords\":" + Q(nc) + ",\"counted\":false,\"reason\":\"not-in-my-view\"}");
                            continue;
                        }
                        var nExplored = reads == null ? ExploredOf(nt, me) : reads.Explored(nt);
                        if (nExplored == null)
                        {
                            complete = false;
                            anyHiddenNeighbour = true;
                            reasons.Add("neighbour-explored-state-unreadable:" + nc);
                            rejected.Add("{\"coords\":" + Q(nc) + ",\"counted\":false,\"reason\":\"visibility-unknown\"}");
                            continue;
                        }
                        if (!nExplored.Value)
                        {
                            // The neighbour itself is still fog: report that, read NOTHING else of it,
                            // and make the COUNT unknown - a hidden tile could hold a valid building,
                            // so "nothing visible" must never be reported as 0.
                            complete = false;
                            anyHiddenNeighbour = true;
                            reasons.Add("neighbour-not-in-my-view:" + nc);
                            rejected.Add("{\"coords\":" + Q(nc) + ",\"counted\":false,\"reason\":\"not-in-my-view\"}");
                            continue;
                        }
                        var nOwner = MI(nt, "owner");
                        if (!nOwner.HasValue)
                        {
                            complete = false;
                            reasons.Add("neighbour-owner-unreadable:" + nc);
                            rejected.Add("{\"coords\":" + Q(nc) + ",\"counted\":false,\"reason\":\"owner-unknown\"}");
                            continue;
                        }
                        if (nOwner.Value != me)
                        {
                            // Foreign territory: no building/resource detail is printed at all.
                            rejected.Add("{\"coords\":" + Q(nc) + ",\"counted\":false,\"reason\":\"not-my-territory\"}");
                            continue;
                        }
                        var nImp = M(nt, "improvement");
                        string nImpName = nImp == null ? null : S(M(nImp, "type"));
                        object nRes = null;
                        bool nResFiltered = false;
                        try { nRes = reads == null ? BridgeMod.TileResourceVisible(nt, gs, me, out nResFiltered)
                            : reads.Resource(nt, out nResFiltered); }
                        catch (Exception) { nRes = null; nResFiltered = false; }
                        if (!nResFiltered)
                        {
                            complete = false;
                            reasons.Add("neighbour-resource-unreadable:" + nc);
                            rejected.Add("{\"coords\":" + Q(nc) + ",\"counted\":false,\"reason\":\"resource-unknown\"}");
                            continue;
                        }
                        string nResName = (nRes != null && nResFiltered) ? S(M(nRes, "type")) : null;
                        string matchedBy = null;
                        string predicateAgrees = "null";
                        if (entries != null)
                        {
                            for (int i = 0; i < entries.Count; i++)
                            {
                                if (entries[i] == null) { continue; }
                                var eImp = M(entries[i], "improvement");
                                var eRes = M(entries[i], "resources");
                                string eImpName = eImp == null ? null : S(M(eImp, "type"));
                                string eResName = eRes == null ? null : S(M(eRes, "type"));
                                if (matchedBy == null && eImpName != null && nImpName != null
                                    && string.Equals(eImpName, nImpName, StringComparison.OrdinalIgnoreCase))
                                {
                                    matchedBy = "improvement";
                                    predicateAgrees = ProbeHasType(hasTypeImp, entries[i], M(nImp, "type"), reasons, ref predicateErrorReported);
                                }
                                else if (matchedBy == null && eResName != null && nResName != null
                                    && string.Equals(eResName, nResName, StringComparison.OrdinalIgnoreCase))
                                {
                                    matchedBy = "resource";
                                    predicateAgrees = ProbeHasType(hasTypeRes, entries[i], M(nRes, "type"), reasons, ref predicateErrorReported);
                                }
                            }
                        }
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) <= borderSize)
                        {
                            if (matchedBy != null) { withinBorder++; }
                        }
                        if (matchedBy != null)
                        {
                            counted.Add("{\"coords\":" + Q(nc) + ",\"improvement\":" + Q(nImpName)
                                + ",\"resource\":" + Q(nResName) + ",\"matchedBy\":" + Q(matchedBy)
                                + ",\"nativeHasType\":" + predicateAgrees + "}");
                        }
                        else
                        {
                            string why = (nImp == null && nResName == null) ? "empty-neighbour" : "no-adjacency-rule-match";
                            rejected.Add("{\"coords\":" + Q(nc) + ",\"counted\":false,\"reason\":" + Q(why)
                                + ",\"owner\":" + nOwner.Value.ToString(CultureInfo.InvariantCulture)
                                + ",\"improvement\":" + Q(nImpName) + ",\"resource\":" + Q(nResName) + "}");
                        }
                    }
                }
            }

            bool includeDetails = complete;
            var sb = new StringBuilder();
            sb.Append("{\"eligibleNeighborCount\":")
              .Append(includeDetails ? counted.Count.ToString(CultureInfo.InvariantCulture) : "null")
              .Append(",\"countStatus\":").Append(Q(includeDetails ? "ok" : "incomplete"))
              .Append(",\"borderSize\":").Append(borderSize.ToString(CultureInfo.InvariantCulture))
              .Append(",\"eligibleWithinBorderSize\":")
              .Append(includeDetails ? withinBorder.ToString(CultureInfo.InvariantCulture) : "null")
              .Append(",\"neighbourhoodFullyVisible\":").Append(anyHiddenNeighbour ? "false" : "true")
              .Append(",\"counted\":[").Append(string.Join(",", counted.ToArray())).Append("]");
            if (withDetail)
            {
                sb.Append(",\"notCounted\":[").Append(string.Join(",", rejected.ToArray())).Append("]");
            }
            // Native numbers are also visibility-bound: with an incomplete neighbourhood scan they are
            // withheld (null) rather than letting a hidden neighbour show up through a bonus value.
            string nb = "null", lvl = "null", work = "null";
            bool built = false;
            if (!includeDetails)
            {
                if (getBonus != null || calcLevel != null) { reasons.Add("native-adjacency-withheld-incomplete-neighbourhood"); }
            }
            else
            {
                if (getBonus != null)
                {
                    try
                    {
                        var r = getBonus.Invoke(null, new object[] { gs, tile, impData });
                        nb = r == null ? "null" : Convert.ToInt32(r, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                    }
                    catch (Exception exGb) { reasons.Add("getadjacencybonusat-threw:" + ExName(exGb)); }
                }
                var existing = M(tile, "improvement");
                string existingName = existing == null ? null : S(M(existing, "type"));
                built = string.Equals(existingName, S(M(impData, "type")), StringComparison.OrdinalIgnoreCase);
                if (built && calcLevel != null)
                {
                    try
                    {
                        var r = calcLevel.Invoke(null, new object[] { gs, tile });
                        lvl = r == null ? "null" : Convert.ToInt32(r, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                    }
                    catch (Exception exCl) { reasons.Add("calculateimprovementlevel-threw:" + ExName(exCl)); }
                }
                if (built && calcWork != null && lvl != "null")
                {
                    try
                    {
                        var r = calcWork.Invoke(null, new object[] { tile, gs, ps, Convert.ToInt32(lvl, CultureInfo.InvariantCulture) });
                        work = r == null ? "null" : Convert.ToInt32(r, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                    }
                    catch (Exception exCw) { reasons.Add("calculatework-threw:" + ExName(exCw)); }
                }
            }
            sb.Append(",\"nativeAdjacencyBonus\":").Append(nb)
              .Append(",\"nativeLevel\":").Append(lvl).Append(",\"nativeWork\":").Append(work)
              .Append(",\"built\":").Append(built ? "true" : "false");
            if (!withDetail) { sb.Append("}"); return sb.ToString(); }
            sb.Append(",\"adjacencyImprovements\":[");
            var ai = new List<string>();
            if (entries != null)
            {
                foreach (var e in entries)
                {
                    if (e == null) { continue; }
                    var eImp = M(e, "improvement");
                    var eRes = M(e, "resources");
                    ai.Add("{\"improvement\":" + Q(eImp == null ? null : S(M(eImp, "type")))
                        + ",\"resource\":" + Q(eRes == null ? null : S(M(eRes, "type"))) + "}");
                }
            }
            sb.Append(string.Join(",", ai.ToArray())).Append("],\"adjacencyRequirements\":[");
            var ar = new List<string>();
            var reqs = AsList(M(impData, "adjacencyRequirements"));
            if (reqs != null)
            {
                foreach (var q in reqs)
                {
                    if (q == null) { continue; }
                    var t1 = M(q, "terrain");
                    var i1 = M(q, "improvement");
                    var r1 = M(q, "resource");
                    var n1 = M(q, "notImprovement");
                    ar.Add("{\"terrain\":" + Q(t1 == null ? null : S(M(t1, "type")))
                        + ",\"improvement\":" + Q(i1 == null ? null : S(M(i1, "type")))
                        + ",\"resource\":" + Q(r1 == null ? null : S(M(r1, "type")))
                        + ",\"notImprovement\":" + Q(n1 == null ? null : S(M(n1, "type"))) + "}");
                }
            }
            sb.Append(string.Join(",", ar.ToArray())).Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Small, honest cross-check of the native predicate: the enum argument is converted to the
        /// parameter's own enum type before the call, and a result that is not a Boolean is reported
        /// as "unreadable" instead of being treated as a native false.
        /// </summary>
        private static string ProbeHasType(MethodInfo m, object entry, object arg, List<string> reasons,
            ref bool reported)
        {
            if (m == null || entry == null || arg == null) { return "null"; }
            object val = arg;
            try
            {
                var want = m.GetParameters()[0].ParameterType;
                if (val.GetType() != want && want.IsEnum)
                {
                    val = Enum.ToObject(want, Convert.ToInt64(val, CultureInfo.InvariantCulture));
                }
            }
            catch (Exception exConv)
            {
                if (!reported) { reported = true; reasons.Add("native-hastype-arg-conversion-failed:" + ExName(exConv)); }
                return "null";
            }
            try
            {
                var r = m.Invoke(entry, new object[] { val });
                if (r is bool) { return (bool)r ? "true" : "false"; }
                if (!reported) { reported = true; reasons.Add("native-hastype-returned-non-boolean"); }
                return "null";
            }
            catch (Exception exInv)
            {
                if (!reported) { reported = true; reasons.Add("native-hastype-threw:" + ExName(exInv)); }
                return "null";
            }
        }

        /// <summary>GameState.CurrentState as a readable name (the enum's own ToString).</summary>
        private static string CurrentStateName(object gs)
        {
            try
            {
                var v = M(gs, "CurrentState");
                return v == null ? null : v.ToString();
            }
            catch (Exception) { return null; }
        }

        // ---- city reward / city value reads (codex-dsh-070) --------------------------------------
        // All numbers come from the game's own data for THIS game state:
        //   ImprovementState   : production / baseScore / xp / borderSize / upgrade /
        //                        connectedToCapitalOfPlayer / effects / rewards (the rewards the city
        //                        ALREADY has - this is where CityWall / Park / Workshop show up)
        //   CityRewardData     : get_cityRewards() (this build's reward list),
        //                        GetRewardProductionModifier(CityReward, GameState) (the game's own
        //                        production number for a reward), PARK_SCORE / POPULATION_REWARD /
        //                        RESOURCE_REWARD constants, GetRewardName/Title/Description
        //   UnitDataExtensions : GetDefence / GetDefenceBonus / GetAttack / GetMaxHealth, i.e. the
        //                        EFFECTIVE values of MY unit on its tile, plus UnitData.defence (base)
        //   PlayerExtensions   : GetDefenceBonus(PlayerState, terrain, GameState) - terrain-only part
        // Nothing is derived here: a quantity the game does not expose stays null with a reason.

        private static string Num(int? v)
        {
            return v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "null";
        }

        private static MethodInfo StaticMethod(string typeName, string method, string[] paramNames,
            string returnName)
        {
            return BridgeMod.FindStaticUnique(BridgeMod.FindGameType(typeName), method, paramNames,
                returnName);
        }

        private static int? CallGetter(MethodInfo m)
        {
            if (m == null) { return null; }
            try { return BridgeMod.NativeInt32(m.Invoke(null, null)); }
            catch (Exception error) { BridgeMod.RecordNativeReadFailure(m.DeclaringType, m.Name, error); return null; }
        }

        private static int? CallGetter1(MethodInfo m, object a)
        {
            if (m == null || a == null) { return null; }
            try
            {
                return BridgeMod.NativeInt32(m.Invoke(null, new object[] { a }));
            }
            catch (Exception error) { BridgeMod.RecordNativeReadFailure(m.DeclaringType, m.Name, error); return null; }
        }

        private static int? CallInt(MethodInfo m, object a, object b)
        {
            if (m == null || a == null) { return null; }
            try
            {
                return BridgeMod.NativeInt32(m.Invoke(null, new object[] { a, b }));
            }
            catch (Exception error) { BridgeMod.RecordNativeReadFailure(m.DeclaringType, m.Name, error); return null; }
        }

        private static int? CallInt3(MethodInfo m, object a, object b, object c)
        {
            if (m == null || a == null || c == null) { return null; }
            try
            {
                return BridgeMod.NativeInt32(m.Invoke(null, new object[] { a, b, c }));
            }
            catch (Exception error) { BridgeMod.RecordNativeReadFailure(m.DeclaringType, m.Name, error); return null; }
        }

        private static string NamesJson(object raw)
        {
            var list = AsList(raw);
            if (list == null) { return "null"; }
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var item in list)
            {
                if (item == null) { continue; }
                if (!first) { sb.Append(","); }
                first = false;
                sb.Append(Q(S(item)));
            }
            return sb.Append("]").ToString();
        }

        /// <summary>
        /// The numbers THIS build exposes for ONE city reward. `productionModifier` is the game's own
        /// GetRewardProductionModifier output for that reward; each static constant is attached only
        /// to the reward the game's own naming ties it to, never spread across other rewards.
        /// </summary>
        private static string RewardEffectJson(object reward, MethodInfo prodMod, object gs,
            int? parkScore, int? populationReward, int? resourceReward)
        {
            string name = S(reward);
            int? pm = CallInt(prodMod, reward, gs);
            var sb = new StringBuilder("{\"reward\":").Append(Q(name))
              .Append(",\"productionModifier\":").Append(Num(pm))
              .Append(",\"scoreConstant\":")
              .Append(name == "Park"
                  ? Num(parkScore) : "null")
              .Append(",\"populationConstant\":")
              .Append(name == "PopulationGrowth"
                  ? Num(populationReward) : "null")
              .Append(",\"resourceConstant\":")
              .Append(name == "Resources"
                  ? Num(resourceReward) : "null")
              .Append(",\"name\":").Append(Q(CityRewardText("GetRewardName", reward)))
              .Append(",\"title\":").Append(Q(CityRewardText("GetPopupTitle", reward)))
              .Append(",\"description\":").Append(Q(CityRewardText("GetPopupDescription", reward)))
              .Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// The rewards a city ALREADY has (ImprovementState.rewards) with their numbers. hasWall is
        /// set only when the list was actually readable and contains CityWall - an unreadable list
        /// returns null, never "no wall".
        /// </summary>
        private static string ChosenRewardsJson(object raw, MethodInfo prodMod, object gs,
            out bool? hasWall, int? parkScore, int? populationReward, int? resourceReward)
        {
            hasWall = null;
            var list = AsList(raw);
            if (list == null) { return "null"; }
            bool wall = false, complete = true;
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var r in list)
            {
                string n = S(r);
                if (n == "CityWall")
                { wall = true; }
                if (!first) { sb.Append(","); }
                first = false;
                if (n == null) { complete = false; sb.Append("null"); }
                else sb.Append(RewardEffectJson(r, prodMod, gs, parkScore, populationReward, resourceReward));
            }
            hasWall = wall ? (bool?)true : complete ? (bool?)false : null;
            return sb.Append("]").ToString();
        }

        /// <summary>
        /// MY visible unit standing on this tile, with the game's OWN effective defence numbers for it
        /// on THIS tile. An empty garrison is null - a unit's numbers are never invented, and the
        /// building-side values (cityState) are reported separately.
        /// </summary>
        internal static bool IsOwnedGarrison(object unit, byte me)
        {
            return unit != null && MI(unit, "owner") == me;
        }

        private static string GarrisonJson(object tile, object gs, byte me, object ps, object logic,
            MethodInfo getDefence, MethodInfo getDefenceBonus, MethodInfo getAttack,
            MethodInfo getMaxHealth, MethodInfo getUnitData, MethodInfo playerTerrainBonus)
        {
            object unit = null;
            try
            {
                bool filtered;
                unit = BridgeMod.TileUnitVisible(tile, gs, me, out filtered);
                if (!filtered) { unit = null; }
            }
            catch (Exception) { unit = null; }
            if (!IsOwnedGarrison(unit, me)) { return "null"; }
            int? baseDefence = null;
            if (getUnitData != null && logic != null)
            {
                try
                {
                    // GetUnitData is an INSTANCE method of this GameState's GameLogicData
                    // (UnitDataExtensions.Read* elsewhere in this file do the same).
                    var ud = getUnitData.Invoke(logic, new object[] { M(unit, "type") });
                    baseDefence = MI(ud, "defence");
                }
                catch (Exception) { baseDefence = null; }
            }
            var sb = new StringBuilder("{\"unitId\":").Append(Q(S(M(unit, "id"))))
              .Append(",\"type\":").Append(Q(S(M(unit, "type"))))
              .Append(",\"health\":").Append(Num(MI(unit, "health")))
              .Append(",\"maxHealth\":").Append(Num(CallInt(getMaxHealth, unit, gs)))
              .Append(",\"attack\":").Append(Num(CallInt(getAttack, unit, gs)))
              .Append(",\"defenceEffective\":").Append(Num(CallInt(getDefence, unit, gs)))
              .Append(",\"defenceBonusEffective\":").Append(Num(CallInt(getDefenceBonus, unit, gs)))
              .Append(",\"defenceBaseUnitData\":").Append(Num(baseDefence))
              .Append(",\"playerTerrainDefenceBonus\":")
              .Append(Num(CallInt3(playerTerrainBonus, ps, M(tile, "terrain"), gs)))
              .Append(",\"authority\":").Append(Q("UnitDataExtensions.GetDefence/GetDefenceBonus/GetAttack/"
                  + "GetMaxHealth(UnitState, GameState) are the EFFECTIVE values of this unit on this "
                  + "tile; UnitData.defence is its base value; PlayerExtensions.GetDefenceBonus(player, "
                  + "terrain, gameState) is the terrain-only part and contains no unit"))
              .Append("}");
            return sb.ToString();
        }

        /// <summary>This build's whole reward list with the numbers the game exposes per reward.</summary>
        private static string CityRewardDataJson(MethodInfo listGetter, MethodInfo prodMod, object gs,
            int? parkScore, int? populationReward, int? resourceReward)
        {
            var list = new List<object>();
            bool readable = false;
            if (listGetter != null)
            {
                try
                {
                    var got = AsList(listGetter.Invoke(null, null));
                    if (got != null) { list = got; readable = true; }
                }
                catch (Exception) { readable = false; }
            }
            bool complete = readable;
            var known = new List<object>();
            foreach (var reward in list)
            {
                if (reward == null || S(reward) == null) { complete = false; continue; }
                known.Add(reward);
            }
            var sb = new StringBuilder();
            sb.Append("{\"status\":").Append(Q(!readable ? "unavailable" : complete ? "ok" : "partial"))
              .Append(",\"authority\":").Append(Q("CityRewardData.get_cityRewards() = this build's reward "
                  + "list; productionModifier = CityRewardData.GetRewardProductionModifier(reward, GameState) "
                  + "- the game's own number, never a guess; PARK_SCORE / POPULATION_REWARD / "
                  + "RESOURCE_REWARD are the game's own constants"))
              .Append(",\"constants\":{\"parkScore\":").Append(Num(parkScore))
              .Append(",\"populationReward\":").Append(Num(populationReward))
              .Append(",\"resourceReward\":").Append(Num(resourceReward))
              .Append("},\"rewards\":[");
            bool first = true;
            foreach (var r in known)
            {
                if (!first) { sb.Append(","); }
                first = false;
                sb.Append(RewardEffectJson(r, prodMod, gs, parkScore, populationReward, resourceReward));
            }
            return sb.Append("],\"rewardCount\":")
                     .Append(Num(readable ? (int?)known.Count : null))
                     .Append(",\"nativeRewardCount\":").Append(Num(readable ? (int?)list.Count : null))
                     .Append("}").ToString();
        }

        /// <summary>
        /// The reward choice a city is CURRENTLY waiting for: the city from GameState's own
        /// pendingCommandTriggers (CityLevelUp, MINE only) and the choices from the open RewardPopup's
        /// own cityRewards list, each with its readable numbers. Status is explicit, including
        /// "trigger-present-popup-unreadable" - an unreadable offer list is never an empty offer.
        /// </summary>
        private static string PendingCityRewardJson(object gs, byte me, MethodInfo prodMod,
            int? parkScore, int? populationReward, int? resourceReward, List<string> reasons)
        {
            var list = BridgeMod.ReadPendingCommandTriggers(gs);
            if (list == null)
            {
                reasons.Add("pendingcommandtriggers-not-enumerable");
                return "{\"status\":\"unavailable\",\"city\":null,\"choices\":[],"
                     + "\"reasonCodes\":[\"pendingcommandtriggers-not-enumerable\"]}";
            }
            string city = null;
            if (list != null)
            {
                foreach (var trig in list)
                {
                    if (trig == null)
                    {
                        reasons.Add("city-reward-trigger-unreadable");
                        return "{\"status\":\"unavailable\",\"city\":null,\"choices\":[],"
                             + "\"reasonCodes\":[\"city-reward-trigger-unreadable\"]}";
                    }
                    var tn = trig.GetType().Name;
                    if (tn.IndexOf("CommandTrigger", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        reasons.Add("city-reward-trigger-unreadable");
                        return "{\"status\":\"unavailable\",\"city\":null,\"choices\":[],"
                             + "\"reasonCodes\":[\"city-reward-trigger-unreadable\"]}";
                    }
                    var typeVal = S(M(trig, "type"));
                    if (typeVal == null)
                    {
                        reasons.Add("city-reward-trigger-type-unreadable");
                        return "{\"status\":\"unavailable\",\"city\":null,\"choices\":[],"
                             + "\"reasonCodes\":[\"city-reward-trigger-type-unreadable\"]}";
                    }
                    if (typeVal != "CityLevelUp") { continue; }
                    var owner = MI(trig, "playerId");
                    if (!owner.HasValue)
                    {
                        // Do not read city coordinates until this reward is proven mine.
                        reasons.Add("city-reward-trigger-owner-unreadable");
                        return "{\"status\":\"unavailable\",\"city\":null,\"choices\":[],"
                             + "\"reasonCodes\":[\"city-reward-trigger-owner-unreadable\"]}";
                    }
                    if (owner.Value != me) { continue; }   // never another player's
                    city = S(M(trig, "Coordinates")) ?? S(M(trig, "coordinates"));
                    if (string.IsNullOrEmpty(city))
                    {
                        reasons.Add("city-reward-trigger-city-unreadable");
                        return "{\"status\":\"unavailable\",\"city\":null,\"choices\":[],"
                             + "\"reasonCodes\":[\"city-reward-trigger-city-unreadable\"]}";
                    }
                    break;
                }
            }
            if (city == null) { return "{\"status\":\"none\",\"city\":null,\"choices\":[]}"; }
            object popup = BridgeMod.CurrentCityRewardPopupComponent();
            return CityRewardOfferJson(gs, city, popup, prodMod, parkScore, populationReward, resourceReward);
        }

        private static string CityRewardOfferJson(object gs, string city, object popup, MethodInfo prodMod,
            int? parkScore, int? populationReward, int? resourceReward)
        {
            // Match the displayed reward's city before inspecting any offered choices.
            string shownCity = popup == null ? null : S(M(M(popup, "tile"), "coordinates"));
            var offered = !string.IsNullOrEmpty(city) && shownCity == city ? AsList(M(popup, "cityRewards")) : null;
            var known = new List<object>();
            if (offered != null) foreach (var reward in offered)
                if (reward != null && S(reward) != null) known.Add(reward);
            var sb = new StringBuilder();
            sb.Append("{\"status\":")
              .Append(Q(offered == null ? "trigger-present-popup-unreadable" : known.Count == offered.Count ? "offered" : "offered-partial"))
              .Append(",\"city\":").Append(Q(city))
              .Append(",\"authority\":").Append(Q("city = GameState.pendingCommandTriggers"
                  + "[type=CityLevelUp, mine]; choices = the open RewardPopup's own cityRewards; "
                  + "numbers = CityRewardData.GetRewardProductionModifier(reward, GameState) plus the "
                  + "PARK_SCORE/POPULATION_REWARD/RESOURCE_REWARD constants"))
              .Append(",\"choices\":[");
            if (offered != null)
            {
                bool first = true;
                foreach (var r in known)
                {
                    if (r == null) { continue; }
                    if (!first) { sb.Append(","); }
                    first = false;
                    sb.Append(RewardEffectJson(r, prodMod, gs, parkScore, populationReward, resourceReward));
                }
            }
            sb.Append("],\"choiceCount\":")
              .Append(offered == null ? "null" : known.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\"nativeChoiceCount\":").Append(offered == null ? "null" : offered.Count.ToString(CultureInfo.InvariantCulture));
            return sb.Append("}").ToString();
        }

        /// <summary>CityRewardData's own UI text for one reward value; null when unbindable.</summary>
        private static string CityRewardText(string method, object reward)
        {
            if (reward == null || string.IsNullOrEmpty(method)) { return null; }
            var t = BridgeMod.FindGameType("CityRewardData");
            var m = BridgeMod.FindStaticUnique(t, method, new[] { "CityReward" }, "String");
            if (m == null || !m.GetParameters()[0].ParameterType.IsInstanceOfType(reward)) return null;
            try { return S(m.Invoke(null, new object[] { reward })); }
            catch (Exception) { return null; }
        }

        private static string BJ(bool? value) { return value.HasValue ? (value.Value ? "true" : "false") : "null"; }
        private static string NJ(int? value) { return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null"; }

        // OR clauses from native TerrainRequirements, never inspect tile.resource here.
        internal static bool? ResourceRequirementsVisible(object improvement, string terrain, string visibleResource)
        {
            var requirements = AsList(M(improvement, "terrainRequirements"));
            if (requirements == null || terrain == null) { return null; }
            if (requirements.Count == 0) { return true; }
            bool unknown = false;
            foreach (var requirement in requirements)
            {
                if (requirement == null || !HasDataMember(requirement, "terrain") || !HasDataMember(requirement, "resource"))
                { unknown = true; continue; }
                var requiredTerrainData = M(requirement, "terrain");
                var requiredTerrain = S(M(requiredTerrainData, "type"));
                if (requiredTerrainData != null && requiredTerrain == null) { unknown = true; continue; }
                if (requiredTerrain != null && requiredTerrain != "None" && requiredTerrain != terrain) { continue; }
                var resource = M(requirement, "resource");
                if (resource == null) { return true; }
                var resourceName = S(M(resource, "type"));
                if (resourceName == null) { unknown = true; continue; }
                if (resourceName == "None" || resourceName == visibleResource) { return true; }
            }
            return unknown ? (bool?)null : false;
        }

        private static bool HasDataMember(object data, string name)
        {
            if (data == null) { return false; }
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var t = data.GetType();
            return t.GetField(name, flags) != null || t.GetProperty(name, flags) != null || t.GetMethod("get_" + name, flags) != null;
        }

        private static string ConstructionRestrictions(object data)
        {
            var abilities = AsList(M(data, "improvementAbilities"));
            var rows = new List<string>();
            if (abilities != null) { foreach (var a in abilities) { rows.Add(Q(S(a))); } }
            return "{\"nativeAbilities\":" + (abilities == null ? "null" : "[" + string.Join(",", rows.ToArray()) + "]")
                + ",\"limitedPerCity\":" + (abilities == null ? "null" : (rows.Contains(Q("Limited")) ? "true" : "false"))
                + ",\"uniquePerPlayer\":" + (abilities == null ? "null" : (rows.Contains(Q("Unique")) ? "true" : "false"))
                + ",\"remainingBuildCount\":null,\"authority\":\"native abilities; exact placement checked by CanBuild\"}";
        }

        private static string PopulationGain(object gs, object tile, object data)
        {
            var method = StaticMethod("ActionUtils", "GetPopulationAt", new[] { "GameState", "TileData", "ImprovementData" }, "Int32");
            if (method == null) { return "null"; }
            try { return Convert.ToInt32(method.Invoke(null, new object[] { gs, tile, data }), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture); }
            catch { return "null"; }
        }

        // Positive membership is usable even when another entry is unreadable. A missing
        // entry proves false only when the entire native list was read successfully.
        private static bool ImprovementListComplete(List<object> values)
        {
            if (values == null) { return false; }
            foreach (var value in values) { if (S(M(value, "type")) == null) { return false; } }
            return true;
        }

        private static string NativeBooleanJson(object value, List<string> reasons, string reason)
        {
            if (value is bool) { return (bool)value ? "true" : "false"; }
            if (!reasons.Contains(reason)) { reasons.Add(reason); }
            return "null";
        }

        private static bool? TaskImprovementMembership(List<object> values, string name, bool dataObjects)
        {
            if (values == null || name == null) { return null; }
            bool complete = true;
            foreach (var value in values)
            {
                string candidate = S(dataObjects ? M(value, "type") : value);
                if (candidate == name) { return true; }
                if (candidate == null) { complete = false; }
            }
            return complete ? (bool?)false : null;
        }

        private static string TasksJson(object gs, object player, object logic, List<object> unlockedImprovements)
        {
            var tasks = AsList(M(player, "tasks"));
            if (tasks == null) { return "null"; }
            var builtUnique = AsList(M(player, "builtUniqueImprovements"));
            var rows = new List<string>();
            var getData = Inst(logic == null ? null : logic.GetType(), "GetTaskData", 1, new[] { "Type" });
            foreach (var task in tasks)
            {
                if (task == null) { continue; }
                object taskType = null, taskData = null;
                try
                {
                    var typeMethod = Inst(task.GetType(), "GetTaskType", 0, new string[0]);
                    if (typeMethod != null) { taskType = typeMethod.Invoke(task, null); }
                    if (getData != null && taskType != null) { taskData = getData.Invoke(logic, new object[] { taskType }); }
                }
                catch { }
                var unlocks = AsList(M(taskData, "improvementUnlocks"));
                var names = new List<string>();
                var rewardStates = new List<string>();
                if (unlocks != null)
                {
                    foreach (var imp in unlocks)
                    {
                        string name = S(M(imp, "type"));
                        names.Add(Q(name));
                        rewardStates.Add("{\"improvement\":" + Q(name)
                            + ",\"currentlyUnlocked\":" + BJ(TaskImprovementMembership(unlockedImprovements, name, true))
                            + ",\"uniqueBuildRecorded\":" + BJ(TaskImprovementMembership(builtUnique, name, false)) + "}");
                    }
                }
                bool? completed = MB(task, "IsCompleted");
                string completionText = null;
                // PopulateTasks uses the virtual completion string for unfinished tasks.
                // Completed rows use a localized UI label instead; do not compute extra progress.
                if (completed == false && gs != null && player != null)
                {
                    try
                    {
                        var statusMethod = Inst(task.GetType(), "GetCompletionStatus", 2, new[] { "GameState", "PlayerState" });
                        if (statusMethod != null && statusMethod.ReturnType == typeof(string))
                            completionText = statusMethod.Invoke(task, new[] { gs, player }) as string;
                    }
                    catch { }
                }
                rows.Add("{\"task\":" + Q(S(taskType)) + ",\"name\":" + Q(S(M(taskData, "displayName")))
                    + ",\"description\":" + Q(S(M(taskData, "description")))
                    + ",\"started\":" + BJ(MB(task, "IsStarted")) + ",\"completed\":" + BJ(completed)
                    + ",\"completionText\":" + Q(completionText)
                    + ",\"completionTextStatus\":" + Q(completed == true ? "completed" : completionText == null ? "unavailable" : "ok")
                    + ",\"completionTextAuthority\":" + Q("native TaskBase.GetCompletionStatus(GameState, PlayerState); unfinished own task UI text")
                    + ",\"improvementUnlocks\":" + (unlocks == null ? "null" : "[" + string.Join(",", names.ToArray()) + "]")
                    + ",\"rewardImprovements\":" + (unlocks == null ? "null" : "[" + string.Join(",", rewardStates.ToArray()) + "]")
                    + ",\"rewardImprovementsAuthority\":" + Q("own TaskData.improvementUnlocks; currentlyUnlocked = native GetUnlockedImprovements membership, not per-tile permission; uniqueBuildRecorded = own PlayerState.builtUniqueImprovements membership, not current map presence") + "}");
            }
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static string Un(string reason)
        {
            return "{\"schema\":" + Q(Schema) + ",\"status\":\"unavailable\",\"reasonCodes\":["
                 + Q(reason) + "],\"resourceOptions\":[],\"myCities\":[],"
                 + "\"buildableImprovements\":[],\"myUnitOptions\":[]}";
        }
    }
}
