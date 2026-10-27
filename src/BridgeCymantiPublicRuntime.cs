// Own native status versus already public, actually rendered status indicators.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal static bool? CymantiOwnEffect(object unit, string wanted)
        {
            if (wanted != "Poisoned" && wanted != "Swift" && wanted != "DoubleReady") return null;
            var effects = ReadNativeList(MemberWithAccessor(unit, "effects"));
            if (effects == null) return null;
            bool found = false;
            foreach (object effect in effects)
            {
                if (effect == null || !effect.GetType().IsEnum || effect.GetType().Name != "UnitEffect"
                    || !Enum.IsDefined(effect.GetType(), effect)) return null;
                if (SafeScalar(effect) == wanted) found = true;
            }
            return found;
        }

        // This does not read UnitState.effects or infer mechanics from an overlay color.
        internal static bool? CymantiVisibleEffect(string visualJson, string wanted)
        {
            if (wanted != "Poisoned" && wanted != "Swift") return null;
            try
            {
                object schema, status, raw;
                var visual = BridgeJsonCodec.Object(visualJson);
                if (!visual.TryGetValue("schema", out schema) || !object.Equals(schema, "unit-visual-effects-1")
                    || !visual.TryGetValue("status", out status)
                    || !(object.Equals(status, "ok") || object.Equals(status, "partial"))
                    || !visual.TryGetValue("particles", out raw)) return null;
                var particles = raw as Dictionary<string, object>;
                if (particles == null || !particles.TryGetValue("status", out status) || !object.Equals(status, "ok")
                    || !particles.TryGetValue("types", out raw)) return null;
                var names = raw as IList;
                if (names == null) return null;
                var unique = new HashSet<string>(StringComparer.Ordinal);
                foreach (object item in names)
                {
                    string name = item as string;
                    if (!CymantiEffectIdentifier(name) || !unique.Add(name)) return null;
                }
                return unique.Contains(wanted);
            }
            catch { return null; }
        }

        private static bool CymantiEffectIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c == '_' || i > 0 && c >= '0' && c <= '9')) return false;
            }
            return true;
        }

        internal static bool? CymantiPublicEffect(string publicInfo, string wanted)
        {
            try
            {
                object visual;
                if (!BridgeJsonCodec.Object(publicInfo).TryGetValue("visualEffects", out visual)) return null;
                return CymantiVisibleEffect(BridgeJsonCodec.ParsedValueJson(visual), wanted);
            }
            catch { return null; }
        }

        private static void CymantiAppendStatus(JsonObj output, object unit, string publicInfo, bool own)
        {
            output.NullableBool("poisoned", own ? CymantiOwnEffect(unit, "Poisoned") : CymantiPublicEffect(publicInfo, "Poisoned"))
                .Str("poisonedAuthority", own ? "owned-native-effects" : "visible-renderer-Poisoned-particle")
                .NullableBool("swift", own ? CymantiOwnEffect(unit, "Swift") : CymantiPublicEffect(publicInfo, "Swift"))
                .Str("swiftAuthority", own ? "owned-native-effects" : "visible-renderer-Swift-particle");
            if (own) output.NullableBool("doubleReady", CymantiOwnEffect(unit, "DoubleReady"))
                .Str("doubleReadyAuthority", "owned-native-effects");
        }

        internal static string CymantiOwnRelations(object unit)
        {
            bool? hasLeader = ReadBoolInvoke(unit, "HasLeader"), hasFollower = ReadBoolInvoke(unit, "HasFollower");
            uint? leader = hasLeader == true ? NativeUInt32(MemberWithAccessor(unit, "leader")) : null;
            uint? follower = hasFollower == true ? NativeUInt32(MemberWithAccessor(unit, "follower")) : null;
            uint? self = NativeUInt32(MemberWithAccessor(unit, "id"));
            if (leader == 0 || leader.HasValue && self.HasValue && leader == self) leader = null;
            if (follower == 0 || follower.HasValue && self.HasValue && follower == self) follower = null;
            bool complete = hasLeader.HasValue && hasFollower.HasValue
                && (hasLeader != true || leader.HasValue) && (hasFollower != true || follower.HasValue);
            var output = new JsonObj().Str("authority", "UnitState.passengerUnit/leader/follower (owned units only)")
                .Str("linkAuthority", "owned-native-HasLeader/HasFollower")
                .Str("status", complete ? "ok" : "partial")
                .NullableBool("hasLeader", hasLeader).NullableBool("hasFollower", hasFollower)
                .NullOrStr("leaderId", leader.HasValue ? leader.Value.ToString(CultureInfo.InvariantCulture) : null)
                .NullOrStr("followerId", follower.HasValue ? follower.Value.ToString(CultureInfo.InvariantCulture) : null);
            URNumber(output, "leaderRaw", URMember(unit, "leader"));
            URNumber(output, "followerRaw", URMember(unit, "follower"));
            object passenger = URMember(unit, "passengerUnit");
            return output.Raw("passenger", passenger == null ? "null" : URPassenger(passenger))
                .Str("conversionSemantics", "passenger identity is native; structured own links require actual HasLeader/HasFollower; zero and self links are not valid IDs; raw fields retained for compatibility").Close();
        }
    }
}
