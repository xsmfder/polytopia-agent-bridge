using System;
using System.Globalization;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private string AttackPreviewPublic(string unit, string at)
        {
            if (!string.IsNullOrEmpty(unit))
            {
                uint unitNumber;
                if (!uint.TryParse(unit, NumberStyles.None, CultureInfo.InvariantCulture, out unitNumber))
                    return new JsonObj().Bool("ok", false).Str("errorCode", "INVALID_PREVIEW_UNIT").Close();
                unit = unitNumber.ToString(CultureInfo.InvariantCulture);
            }
            int x, y;
            if (!string.IsNullOrEmpty(at))
            {
                if (!TryParseCoords(at, out x, out y))
                    return new JsonObj().Bool("ok", false).Str("errorCode", "INVALID_PREVIEW_COORDINATE").Close();
                at = "(" + x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture) + ")";
            }
            var gs = GameStateInstance();
            if (!PrivateSamplingAllowed(gs)) return PublicUnavailable("attack-preview-1", "controlled game is not observable");
            var identity = ResolveControlledIdentity(gs);
            if (!identity.Verified || CurrentPlayerId(gs) != identity.Id
                || IsPlayingPhase(SafeScalar(Val(_currentStateProp, gs))) != true)
                return PublicUnavailable("attack-preview-1", "not controlled player's turn");
            CombatRuntimeTick(gs);
            if (CRSettlementStatus(gs) != "complete")
                return PublicUnavailable("attack-preview-1", "native pipeline is not settled; resolve original ticket or popup first");
            string episode = EpisodeId(gs), stream = _combatStream;
            long version = _combatVersion;
            string result = PolytopiaBattlePreview.BattlePreviewSnapshot.Query(unit ?? "", at ?? "");
            CombatRuntimeTick(gs);
            var afterIdentity = ResolveControlledIdentity(gs);
            if (!PrivateSamplingAllowed(gs) || episode != EpisodeId(gs) || stream != _combatStream
                || !afterIdentity.Verified || afterIdentity.Id != identity.Id
                || version != _combatVersion || CurrentPlayerId(gs) != identity.Id || CRSettlementStatus(gs) != "complete")
                return new JsonObj().Bool("ok", false).Str("errorCode", "PREVIEW_STATE_CHANGED")
                    .Str("recovery", "read again; no game action submitted").Close();
            return BindAttackPreviewResult(result, episode, identity.Id, stream, version, unit, at);
        }

        internal static string BindAttackPreviewResult(string result, string episode, byte player,
            string stream, long version, string unit, string at)
        {
            var parsed = BridgeJsonCodec.Object(result);
            object nativePlayer, nativeStatus;
            if (!parsed.TryGetValue("status", out nativeStatus) || !(nativeStatus is string))
                return new JsonObj().Bool("ok", false).Str("errorCode", "PREVIEW_STATUS_UNAVAILABLE").Close();
            string status = (string)nativeStatus;
            byte previewPlayer;
            if (status != "unavailable" && (!parsed.TryGetValue("controlledPlayerId", out nativePlayer)
                || !byte.TryParse(BridgeJsonCodec.Scalar(nativePlayer), NumberStyles.None, CultureInfo.InvariantCulture, out previewPlayer)
                || previewPlayer != player))
                return new JsonObj().Bool("ok", false).Str("errorCode", "PREVIEW_IDENTITY_CHANGED").Close();
            return new JsonObj().Bool("ok", status != "unavailable").Str("schema", "attack-preview-1")
                .Str("status", status).Str("episodeId", episode).Raw("controlledPlayerId", player.ToString(CultureInfo.InvariantCulture))
                .Str("streamId", stream).Raw("stateVersion", version.ToString(CultureInfo.InvariantCulture))
                .NullOrStr("unitId", string.IsNullOrEmpty(unit) ? null : unit)
                .NullOrStr("targetCoord", string.IsNullOrEmpty(at) ? null : at)
                .Str("healthScale", "native raw HP and damage; same scale as world.units[].health")
                .Str("semantics", "current native legal-target prediction; conditional composite effects are not actual damage receipts")
                .Raw("preview", result).Close();
        }
    }
}
