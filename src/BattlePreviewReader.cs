// BattlePreviewReader - the PUBLIC pre-attack preview a player sees in the UI (codex-dsh-050).
//
// Source of truth (verified from the current game's metadata, not guessed):
//   GameLogicAssembly/BattleHelpers.cs
//     public static BattleResults GetBattleResults(GameState gs, UnitState attackingUnit,
//                                                  UnitState defendingUnit,
//                                                  bool accountForPeace = true, bool simpleData = false)
//   BattleResults (struct): attackDamage @0x0, retaliationDamage @0x4,
//                           shouldMoveToDefeatedEnemyTile @0x8, shouldResetPacifistTask @0x9
//
// This is a pure calculation (the same one the UI uses to draw the preview); it issues no command and
// mutates nothing. Only the two numbers a player can read are projected:
//     {coords, damageDealt, damageReceived}
// Lethality is deliberately NOT published: the client can derive it from damageDealt and the target's
// already-visible HP. No owner/unit-id/name/position beyond the target coordinate is emitted.
//
// Fail-closed: an unreadable unit, an unreadable result, a null defender or a failed call makes the
// entry `unavailable` (or the whole domain `partial`); a number is NEVER invented or defaulted to 0.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace PolytopiaBattlePreview
{
    public static class BattlePreviewReader
    {
        public const string DomainSchema = "attack-previews-1.1";

        private static object Member(object o, string name)
        {
            if (o == null) return null;
            try
            {
                Type t = o.GetType();
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance);
                if (p != null) return p.GetValue(o, null);
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance);
                if (f != null) return f.GetValue(o);
            }
            catch (Exception) { }
            return null;
        }

        private static Type FindType(string simpleOrFull)
        {
            try
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = a.GetType(simpleOrFull, false);
                    if (t != null) return t;
                }
            }
            catch (Exception) { }
            return null;
        }

        private static MethodInfo FindStatic(Type type, string name, int paramCount)
        {
            if (type == null) return null;
            try
            {
                MethodInfo found = null;
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Static))
                {
                    if (m.Name != name) continue;
                    if (m.GetParameters().Length != paramCount) continue;
                    if (found != null) return null;          // ambiguous -> refuse to bind
                    found = m;
                }
                return found;
            }
            catch (Exception) { return null; }
        }

        private static int? StrictInt(object v)
        {
            if (v == null || v is bool || v is string) return null;
            if (v is int) return (int)v;
            if (v is byte) return (int)(byte)v;
            if (v is short) return (int)(short)v;
            if (v is uint) { uint u = (uint)v; return u > int.MaxValue ? (int?)null : (int)u; }
            if (v is long) { long l = (long)v; return (l < int.MinValue || l > int.MaxValue) ? (int?)null : (int)l; }
            return null;
        }

        /// <summary>
        /// damageDealt / damageReceived for every real attack option of MY units against a currently
        /// VISIBLE enemy. `attackOptionsOf` and `visibleEnemyAt` are supplied by the caller so this
        /// reader never enumerates hidden state on its own.
        /// </summary>
        public static string Build(object gs,
                                   List<object> myUnits,                    // (executionId, unitState, from)
                                   List<object> attackTargetsPerUnit,       // same order: list of (coords, tile)
                                   List<object> visibleEnemyUnitsPerUnit)   // same order, aligned per target
        {
            LastUnknown = null;
            Type helpers = FindType("Il2CppPolytopia.BattleHelpers")
                           ?? FindType("Il2Cpp.BattleHelpers") ?? FindType("BattleHelpers");
            MethodInfo results = FindStatic(helpers, "GetBattleResults", 5);
            StringBuilder sb = new StringBuilder("{");
            sb.Append("\"schema\":").Append(Q(DomainSchema)).Append(",");
            if (results == null)
            {
                sb.Append("\"status\":\"unavailable\",\"reasonCodes\":[\"battlehelpers-not-bound\"],");
                sb.Append("\"units\":[]}");
                return sb.ToString();
            }
            sb.Append("\"status\":\"ok\",\"units\":[");
            bool partial = false;
            for (int i = 0; i < myUnits.Count; i++)
            {
                if (i > 0) sb.Append(",");
                object unit = myUnits[i];
                string uid = Convert.ToString(Member(unit, "executionId"), CultureInfo.InvariantCulture);
                string from = Convert.ToString(Member(unit, "from"), CultureInfo.InvariantCulture);
                sb.Append("{\"executionId\":").Append(Q(uid))
                  .Append(",\"from\":").Append(Q(from)).Append(",\"targets\":[");
                List<object> targets = (List<object>)attackTargetsPerUnit[i];
                List<object> defenders = (List<object>)visibleEnemyUnitsPerUnit[i];
                bool firstTarget = true;
                for (int k = 0; k < targets.Count; k++)
                {
                    object tile = targets[k];
                    object attacker = Member(unit, "unitState");
                    object defender = defenders[k];              // null -> cannot preview
                    if (attacker == null || defender == null)
                    {
                        partial = true;
                        LogUnknown(uid, "attacker-or-defender-unreadable");
                        continue;
                    }
                    object res;
                    try
                    {
                        res = results.Invoke(null, new object[] { gs, attacker, defender, true, false });
                    }
                    catch (Exception exc)
                    {
                        partial = true;
                        LogUnknown(uid, "getbattleresults-" + exc.GetType().Name);
                        continue;
                    }
                    int? dealt = StrictInt(Member(res, "attackDamage"));
                    int? received = StrictInt(Member(res, "retaliationDamage"));
                    int? rawReceived = received;
                    if (received.HasValue && received.Value < 0) received = null;
                    if (!dealt.HasValue || !rawReceived.HasValue)
                    {
                        partial = true;
                        LogUnknown(uid, "damage-unreadable");
                        continue;
                    }
                    // The two lethality labels are part of the same player-visible preview: they are
                    // derived from the published numbers plus each side's already-VISIBLE hp. An
                    // unreadable hp yields null (unknown) - never a fabricated false.
                    int? attackerHp = StrictInt(Member(attacker, "health"));
                    if (!attackerHp.HasValue) attackerHp = StrictInt(Member(attacker, "hp"));
                    int? defenderHp = StrictInt(Member(defender, "health"));
                    if (!defenderHp.HasValue) defenderHp = StrictInt(Member(defender, "hp"));
                    bool? defeatsTarget = defenderHp.HasValue
                        ? (bool?)(dealt.Value >= defenderHp.Value) : null;
                    bool? diesToRetaliation = attackerHp.HasValue && received.HasValue
                        ? (bool?)(received.Value >= attackerHp.Value) : null;
                    if (!firstTarget) sb.Append(",");
                    firstTarget = false;
                    sb.Append("{\"coords\":").Append(Q(Convert.ToString(Member(tile, "coords"),
                            CultureInfo.InvariantCulture)))
                      .Append(",\"damageDealt\":").Append(dealt.Value.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"damageReceived\":").Append(received.HasValue ? received.Value.ToString(CultureInfo.InvariantCulture) : "null")
                      .Append(",\"nativeDamageReceived\":").Append(rawReceived.HasValue ? rawReceived.Value.ToString(CultureInfo.InvariantCulture) : "null")
                      .Append(",\"retaliationStatus\":").Append(Q(rawReceived.HasValue && rawReceived.Value < 0 ? "native-negative-sentinel" : received.HasValue ? "known" : "unknown"))
                      .Append(",\"wouldDefeatTarget\":").Append(defeatsTarget.HasValue
                            ? (defeatsTarget.Value ? "true" : "false") : "null")
                      .Append(",\"wouldDieToRetaliation\":").Append(diesToRetaliation.HasValue
                            ? (diesToRetaliation.Value ? "true" : "false") : "null")
                      .Append("}");
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            string json = sb.ToString();
            if (partial)
                json = json.Replace("\"status\":\"ok\"", "\"status\":\"partial\"");
            if (LastUnknown != null)
                json = json.Substring(0, json.Length - 1) + ",\"lastUnknown\":"
                       + Q(LastUnknown) + "}";
            return json;
        }

        private static string LastUnknown;

        private static void LogUnknown(string uid, string code)
        {
            LastUnknown = code;                 // a fixed code, never a message or a private value
        }

        private static string Q(string s)
        {
            if (s == null) return "null";
            StringBuilder b = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4"));
                else b.Append(c);
            }
            return b.Append("\"").ToString();
        }
    }
}
