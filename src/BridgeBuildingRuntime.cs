using System;
using System.Collections.Generic;
using System.Globalization;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private object BuildingOwnTile(object gs, byte me, string coordinates)
        {
            int x, y;
            if (!TryParseCoords(coordinates, out x, out y)) return null;
            var map = Val(_mapProp, gs);
            var raw = MemberWithAccessor(map, "Tiles");
            var tiles = AsEnumerable(raw);
            if (tiles == null) return null;
            var fog = BuildFogGate(map, raw, me);
            if (!fog.HasExplored) return null;
            foreach (var tile in tiles)
            {
                if (tile == null || !fog.Explored(tile)) continue;
                int tx, ty;
                if (!TryParseCoords(SafeScalar(MemberWithAccessor(tile, "coordinates")), out tx, out ty) || tx != x || ty != y) continue;
                return SafeScalar(MemberWithAccessor(tile, "owner")) == me.ToString(CultureInfo.InvariantCulture) ? tile : null;
            }
            return null;
        }

        private List<object> BuildingMenu(object gs, object player, object tile)
        {
            var raw = URNative("CommandUtils", "GetImprovementAbilities",
                new[] { "GameState", "PlayerState", "TileData", "Boolean" }, new[] { gs, player, tile, (object)false });
            return ReadNativeCommandList(raw);
        }

        private string TileActionsPublic(Dictionary<string, string> args, bool execute)
        {
            var gs = GameStateInstance();
            if (gs == null) return execute ? ActionPreparationError("NO_ACTIVE_GAME", "native tile action could not be prepared") : URError("NO_ACTIVE_GAME");
            var identity = ResolveControlledIdentity(gs);
            if (!identity.Verified) return execute ? ActionPreparationError("UNVERIFIED_PLAYER", "native tile action could not be prepared") : URError("UNVERIFIED_PLAYER");
            if (CurrentPlayerId(gs) != identity.Id) return execute ? ActionPreparationError("NOT_YOUR_TURN", "native tile action could not be prepared") : URError("NOT_YOUR_TURN");
            string at = URArg(args, "at"), wanted = URArg(args, "action_id");
            var tile = BuildingOwnTile(gs, identity.Id, at);
            if (tile == null) return execute ? ActionPreparationError("OWN_EXPLORED_TILE_REQUIRED", "native tile action could not be prepared") : URError("OWN_EXPLORED_TILE_REQUIRED");
            var menu = BuildingMenu(gs, URPlayer(gs, identity.Id), tile);
            if (menu == null) return execute ? ActionPreparationError("NATIVE_MENU_UNAVAILABLE", "native tile action could not be prepared") : URError("NATIVE_MENU_UNAVAILABLE");
            var rows = new JsonArr();
            object selected = null;
            string selectedType = null;
            foreach (var command in menu)
            {
                string type = URCommandType(command);
                if (type == null) return execute ? ActionPreparationError("NATIVE_COMMAND_TYPE_UNAVAILABLE", "native tile action could not be prepared") : URError("NATIVE_COMMAND_TYPE_UNAVAILABLE");
                var typed = URConcreteCommand(command, type);
                bool readable;
                string parameters = URCommandParameters(typed, out readable);
                string kind = URUpgradeType(typed, type);
                bool? valid = URValid(typed, gs);
                if (!execute)
                {
                    // The native menu is still the candidate source. Share adapter and
                    // proof diagnostics with units instead of maintaining a second projection.
                    rows.Raw(URMenuActionDescriptor(type, kind, parameters, readable, valid, true, menu)
                        .NullableBool("nativeAlwaysAskForConfirmation", ReadBoolInvoke(typed, "ShouldAlwaysAskForConfirmation"))
                        .Raw("cost", "null").Raw("refund", "null").Close());
                    continue;
                }
                if (wanted != URMenuActionId(type, kind, parameters, menu)) continue;
                if (selected != null) return execute ? ActionPreparationError("AMBIGUOUS_ACTION_ID", "native tile action could not be prepared") : URError("AMBIGUOUS_ACTION_ID");
                if (!readable) return execute ? ActionPreparationError("NATIVE_COMMAND_PARAMETERS_UNAVAILABLE", "native tile action could not be prepared") : URError("NATIVE_COMMAND_PARAMETERS_UNAVAILABLE");
                if (type == "Upgrade" && string.IsNullOrEmpty(kind)) return execute ? ActionPreparationError("UPGRADE_TYPE_UNAVAILABLE", "native tile action could not be prepared") : URError("UPGRADE_TYPE_UNAVAILABLE");
                selected = typed; selectedType = type;
            }
            if (execute)
            {
                if (selected == null) return execute ? ActionPreparationError("ACTION_NOT_IN_CURRENT_MENU", "native tile action could not be prepared") : URError("ACTION_NOT_IN_CURRENT_MENU");
                return ExecuteGameAction(UnitRuntimeActionName(selectedType), "", "", "", "", at, "", "", selected);
            }
            return new JsonObj().Bool("ok", true).Str("schema", "tile-actions-1").Str("coords", at)
                .Str("authority", "native CommandUtils.GetImprovementAbilities; current owned explored tile")
                .Raw("actions", rows.Close()).Close();
        }

        // The caller supplies only an already filtered owned/explored tile. These native
        // getters calculate current facts; no LevelUp or end-turn simulation is invoked.
        internal static string BuildingGrowthSnapshot(object gs, object tile, object player)
        {
            var imp = MemberWithAccessor(tile, "improvement");
            if (imp == null) return "null";
            var logic = GameLogicDataOf(gs);
            var kind = MemberWithAccessor(imp, "type");
            var data = logic == null || kind == null ? null : InvokeOn(logic, "GetImprovementData", new[] { kind });
            var rewards = ReadNativeList(MemberWithAccessor(data, "growthRewards"), true);
            bool complete = rewards != null;
            var rewardRows = new JsonArr();
            if (rewards != null) foreach (var reward in rewards)
            {
                if (reward == null) { complete = false; rewardRows.Raw("null"); continue; }
                string score = ResultsNumber(MemberWithAccessor(reward, "score"));
                string population = ResultsNumber(MemberWithAccessor(reward, "population"));
                if (score == "null" || population == "null") complete = false;
                rewardRows.Raw(new JsonObj().Raw("score", score).Raw("population", population).Close());
            }
            return new JsonObj().Str("schema", "building-growth-1")
                .NullOrStr("type", SafeScalar(kind))
                .Raw("foundedTurn", ResultsNumber(MemberWithAccessor(imp, "founded")))
                .Raw("ageTurns", ResultsNumber(InvokeOn(imp, "GetAge", new[] { gs })))
                .Raw("level", ResultsNumber(MemberWithAccessor(imp, "level")))
                .Raw("growthRateTurns", ResultsNumber(MemberWithAccessor(data, "growthRate")))
                .Raw("growthRewards", rewards == null ? "null" : rewardRows.Close())
                .Str("growthRewardsStatus", rewards == null ? "unavailable" : complete ? "ok" : "partial")
                .Raw("maximumLevel", ResultsNumber(data == null || player == null ? null : URNative("ImprovementExtensions", "MaxLevel",
                    new[] { "ImprovementData", "PlayerState", "GameState" }, new[] { data, player, gs })))
                .Raw("currentScore", ResultsNumber(URNative("ScoreExtensions", "CalculateImprovementScore",
                    new[] { "GameState", "TileData" }, new[] { gs, tile })))
                .Str("authority", "native ImprovementState.GetAge, active ImprovementData, ImprovementExtensions.MaxLevel, ScoreExtensions.CalculateImprovementScore")
                .Str("growthSemantics", "current native state and reward table; level changes occur only through normal turn settlement")
                .Close();
        }
    }
}
