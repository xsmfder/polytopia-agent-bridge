// Decompose marks an existing owned improvement; it is not Destroy.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal sealed class CymantiDecomposeProof
        {
            internal object Root;
            internal bool Executed;
            internal string Before, After;
        }

        // Caller must already have filtered this tile as controlled-player-owned and
        // explored. TileData.owner is the ownership authority. Current native samples
        // have ImprovementState.owner == 0 even on owned City/Mycelium/Fungi; that
        // value cannot veto a certified own tile or establish ownership by itself.
        internal static string CymantiOwnedBuildingState(object tile)
        {
            object improvement = MemberWithAccessor(tile, "improvement");
            if (improvement == null) return null;
            int? owner = NativeInt32(MemberWithAccessor(tile, "owner"));
            int? improvementOwner = NativeInt32(MemberWithAccessor(improvement, "owner"));
            if (!owner.HasValue || owner <= 0 || owner > Byte.MaxValue
                || !improvementOwner.HasValue || improvementOwner < 0 || improvementOwner > Byte.MaxValue
                || improvementOwner != 0 && improvementOwner != owner) return null;
            object rawType = MemberWithAccessor(improvement, "type");
            if (rawType == null || !rawType.GetType().IsEnum || rawType.GetType().Name != "Type"
                || rawType.GetType().DeclaringType == null || rawType.GetType().DeclaringType.Name != "ImprovementData"
                || !KnownRuleEnumValue(rawType.GetType(), rawType)) return null;
            string type = SafeScalar(rawType);
            int? level = NativeInt32(MemberWithAccessor(improvement, "level"));
            if (level < 0 || level > UInt16.MaxValue) level = null;
            var rawEffects = ReadNativeList(MemberWithAccessor(improvement, "effects"));
            SortedSet<string> effects = rawEffects == null ? null : new SortedSet<string>(StringComparer.Ordinal);
            if (rawEffects != null) foreach (object effect in rawEffects)
            {
                if (effect == null || !effect.GetType().IsEnum || effect.GetType().Name != "ImprovementEffect"
                    || !Enum.IsDefined(effect.GetType(), effect)) { effects = null; break; }
                string name = SafeScalar(effect);
                if (!CymantiEffectIdentifier(name) || !effects.Add(name)) { effects = null; break; }
            }
            var rows = new JsonArr();
            if (effects != null) foreach (string name in effects) rows.Raw(Quote(name));
            bool? decomposing = effects == null ? (bool?)null : effects.Contains("decomposing");
            return new JsonObj().Str("schema", "cymanti-owned-building-1")
                .Str("owner", owner.Value.ToString(CultureInfo.InvariantCulture)).Str("type", type)
                .Raw("level", level.HasValue ? level.Value.ToString(CultureInfo.InvariantCulture) : "null")
                .Raw("effects", effects == null ? "null" : rows.Close()).NullableBool("decomposing", decomposing)
                .Str("authority", "owned-TileData.owner+improvement.type/level/effects").Close();
        }

        private static Dictionary<string,object> CymantiDecomposeState(string json, string me)
        {
            if (json == null) return null;
            try
            {
                var row = BridgeJsonCodec.Object(json); object schema, owner, type, level, effectsRaw, flag, authority;
                if (!row.TryGetValue("schema", out schema) || !object.Equals(schema, "cymanti-owned-building-1")
                    || !row.TryGetValue("owner", out owner) || !object.Equals(owner, me)
                    || !row.TryGetValue("type", out type) || !(type is string) || !CymantiEffectIdentifier((string)type)
                    || !row.TryGetValue("level", out level) || !(level is BridgeJsonCodec.Number)
                    || !row.TryGetValue("effects", out effectsRaw) || !row.TryGetValue("decomposing", out flag) || !(flag is bool)
                    || !row.TryGetValue("authority", out authority) || !object.Equals(authority, "owned-TileData.owner+improvement.type/level/effects")) return null;
                int parsedLevel;
                if (!Int32.TryParse(((BridgeJsonCodec.Number)level).Text, NumberStyles.None, CultureInfo.InvariantCulture, out parsedLevel)
                    || parsedLevel > UInt16.MaxValue) return null;
                row["level"] = parsedLevel;
                var effects = effectsRaw as IList; if (effects == null) return null;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (object value in effects)
                    if (!(value is string) || !CymantiEffectIdentifier((string)value) || !seen.Add((string)value)) return null;
                return (bool)flag == seen.Contains("decomposing") ? row : null;
            }
            catch { return null; }
        }

        private static bool CymantiDecomposeContext(CombatTicket ticket, CombatFrame frame)
        {
            byte me;
            return ticket != null && ticket.Action == "decompose" && ticket.Before != null && ticket.Before.Complete
                && ticket.Accepted != false && ticket.FinalFrame == null && frame != null && frame.Complete
                && !string.IsNullOrEmpty(frame.Episode) && !string.IsNullOrEmpty(frame.Turn)
                && byte.TryParse(frame.Me, NumberStyles.None, CultureInfo.InvariantCulture, out me) && me > 0
                && frame.Episode == ticket.Before.Episode && frame.Me == ticket.Before.Me && frame.Turn == ticket.Before.Turn
                && !string.IsNullOrEmpty(ticket.Coord);
        }

        internal static bool CymantiDecomposeBind(CombatTicket ticket, CombatFrame frame, object action, string kind)
        {
            if (!CymantiDecomposeContext(ticket, frame) || action == null || kind != "DecomposeAction"
                || action.GetType().Name != "DecomposeAction"
                || SafeScalar(MemberWithAccessor(action, "PlayerId")) != frame.Me
                || SafeScalar(MemberWithAccessor(action, "Coordinates")) != ticket.Coord) return false;
            string initial, current;
            if (!ticket.Before.OwnBuildingStates.TryGetValue(ticket.Coord, out initial)
                || !frame.OwnBuildingStates.TryGetValue(ticket.Coord, out current) || initial != current) return false;
            var before = CymantiDecomposeState(initial, frame.Me);
            if (before == null || !object.Equals(before["decomposing"], false)) return false;
            var proof = ticket.CymantiDecompose;
            if (proof.Root != null && !CRSameNative(proof.Root, action)) return false;
            proof.Root = action; proof.Before = initial;
            return true;
        }

        internal static bool CymantiDecomposeRecord(CombatTicket ticket, CombatFrame before, CombatFrame after, object action)
        {
            if (!CymantiDecomposeContext(ticket, before) || !CymantiDecomposeContext(ticket, after)
                || action == null || !CRSameNative(ticket.CymantiDecompose.Root, action)
                || action.GetType().Name != "DecomposeAction"
                || SafeScalar(MemberWithAccessor(action, "PlayerId")) != before.Me
                || SafeScalar(MemberWithAccessor(action, "Coordinates")) != ticket.Coord) return false;
            string beforeState, afterState;
            if (!before.OwnBuildingStates.TryGetValue(ticket.Coord, out beforeState)
                || beforeState != ticket.CymantiDecompose.Before) return false;
            var original = CymantiDecomposeState(beforeState, before.Me);
            if (original == null || !object.Equals(original["decomposing"], false)) return false;
            ticket.CymantiDecompose.Executed = true;
            if (!after.OwnBuildingStates.TryGetValue(ticket.Coord, out afterState)) return false;
            var applied = CymantiDecomposeState(afterState, after.Me);
            if (applied == null || !object.Equals(applied["decomposing"], true)
                || !object.Equals(original["type"], applied["type"]) || !object.Equals(original["level"], applied["level"])) return false;
            ticket.CymantiDecompose.After = afterState;
            return true;
        }

        internal static string CymantiDecomposeConfirmation(CombatTicket ticket, CombatFrame current, out string basis)
        {
            basis = null;
            if (ticket == null || ticket.Action != "decompose") return null;
            basis = "Decompose requires the exact executed own DecomposeAction and the same owned improvement's native decomposing false-to-true effect; direct removal is not covered";
            var proof = ticket.CymantiDecompose; string state;
            if (ticket.Before == null || current == null || !current.Complete || ticket.Accepted != true
                || !ticket.Dispatched || current.Me != ticket.Before.Me || current.Episode != ticket.Before.Episode
                || current.Turn != ticket.Before.Turn || !proof.Executed || proof.Before == null || proof.After == null
                || !current.OwnBuildingStates.TryGetValue(ticket.Coord ?? "", out state) || state != proof.After) return "unknown";
            var original = CymantiDecomposeState(proof.Before, current.Me);
            var applied = CymantiDecomposeState(proof.After, current.Me);
            return original != null && applied != null && object.Equals(original["decomposing"], false)
                && object.Equals(applied["decomposing"], true) && object.Equals(original["type"], applied["type"])
                && object.Equals(original["level"], applied["level"]) ? "confirmed" : "unknown";
        }

        internal static string CymantiDecomposeOutcome(CombatTicket ticket, CombatFrame current)
        {
            if (ticket == null || ticket.Action != "decompose") return "null";
            string basis; string confirmation = CymantiDecomposeConfirmation(ticket, current, out basis);
            var proof = ticket.CymantiDecompose;
            return new JsonObj().Str("schema", "cymanti-decompose-1").Str("coords", ticket.Coord)
                .Bool("nativeExecuted", proof.Executed).Str("effectConfirmation", confirmation)
                .Raw("before", proof.Before ?? "null").Raw("after", confirmation == "confirmed" ? proof.After : "null")
                .Str("authority", basis).Close();
        }
    }
}
