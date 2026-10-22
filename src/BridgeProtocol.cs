using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PolytopiaAgent
{
    // Dependency-free strict JSON envelope reader. IL2CPP hosts do not necessarily ship
    // System.Web.Extensions; no game state or native code is involved in this seam.
    internal static class BridgeJsonCodec
    {
        internal sealed class Number
        {
            internal string Text;
            internal Number(string text) { Text = text; }
            public override string ToString() { return Text; }
        }

        internal static Dictionary<string, object> Object(string json)
        {
            var p = new Parser(json);
            object value = p.Read(0);
            p.Space();
            if (!p.End) { throw new FormatException("trailing JSON content"); }
            var result = value as Dictionary<string, object>;
            if (result == null) { throw new FormatException("envelope must be an object"); }
            return result;
        }

        internal static string Scalar(object value)
        {
            if (value == null) { return ""; }
            if (value is bool) { return (bool)value ? "true" : "false"; }
            if (value is string || value is Number) { return value.ToString(); }
            return "";
        }

        // Only trees produced by this strict parser; never reflect native objects.
        // Number tokens retain their original precision and strings remain escaped.
        internal static string ParsedValueJson(object value, int depth = 0)
        {
            if (depth > 64) throw new FormatException("JSON depth");
            if (value == null) return "null";
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is string) return "\"" + Escape((string)value) + "\"";
            var number = value as Number;
            if (number != null) return number.Text;
            var map = value as Dictionary<string, object>;
            if (map != null)
            {
                var result = new JsonObj();
                foreach (var item in map) result.Raw(item.Key, ParsedValueJson(item.Value, depth + 1));
                return result.Close();
            }
            var list = value as List<object>;
            if (list != null)
            {
                var result = new JsonArr();
                foreach (var item in list) result.Raw(ParsedValueJson(item, depth + 1));
                return result.Close();
            }
            throw new FormatException("not a parsed JSON value");
        }

        internal static string Escape(string text)
        {
            if (text == null) { return ""; }
            var b = new StringBuilder();
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\b': b.Append("\\b"); break;
                    case '\f': b.Append("\\f"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < 32 || char.IsSurrogate(c))
                        { b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); }
                        else { b.Append(c); }
                        break;
                }
            }
            return b.ToString();
        }

        internal static string NormalizeResponse(string json)
        {
            try
            {
                var obj = Object(json);
                object ok;
                if (obj.TryGetValue("ok", out ok) && ok is bool && !(bool)ok
                    && !obj.ContainsKey("errorCode"))
                {
                    object error;
                    string text = obj.TryGetValue("error", out error) ? Scalar(error) : "";
                    string code = text.StartsWith("bad ", StringComparison.OrdinalIgnoreCase)
                        ? "INVALID_ARGUMENT" : "REQUEST_FAILED";
                    return json.TrimEnd().Substring(0, json.TrimEnd().Length - 1)
                        + ",\"errorCode\":\"" + code + "\"}";
                }
                return json.Trim();
            }
            catch (Exception)
            {
                // Never append an invalid frame and force the consumer to time out. Raw source
                // text is withheld: a serialization failure must not dump private diagnostics.
                return "{\"ok\":false,\"errorCode\":\"INVALID_RESPONSE_JSON\","
                    + "\"error\":\"bridge could not serialize its response\",\"outcome\":\"unknown\"}";
            }
        }

        private sealed class Parser
        {
            private readonly string s;
            private int pos;
            internal Parser(string input)
            {
                if (input == null || input.Length > 16777216) { throw new FormatException("JSON size"); }
                s = input;
            }
            internal bool End { get { return pos == s.Length; } }
            internal void Space()
            { while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\r' || s[pos] == '\n')) { pos++; } }
            private bool Eat(char c)
            { Space(); if (pos < s.Length && s[pos] == c) { pos++; return true; } return false; }
            private void Need(char c)
            { if (!Eat(c)) { throw new FormatException("expected delimiter"); } }
            internal object Read(int depth)
            {
                if (depth > 64) { throw new FormatException("JSON depth"); }
                Space();
                if (End) { throw new FormatException("unexpected JSON end"); }
                char c = s[pos];
                if (c == '"') { return String(); }
                if (c == '{')
                {
                    pos++;
                    var obj = new Dictionary<string, object>(StringComparer.Ordinal);
                    if (Eat('}')) { return obj; }
                    do
                    {
                        Space();
                        string key = String(); Need(':');
                        if (obj.ContainsKey(key)) { throw new FormatException("duplicate JSON property"); }
                        obj.Add(key, Read(depth + 1));
                        if (Eat('}')) { return obj; }
                        Need(',');
                    } while (true);
                }
                if (c == '[')
                {
                    pos++;
                    var items = new List<object>();
                    if (Eat(']')) { return items; }
                    do
                    {
                        items.Add(Read(depth + 1));
                        if (Eat(']')) { return items; }
                        Need(',');
                    } while (true);
                }
                if (c == 't') { Literal("true"); return true; }
                if (c == 'f') { Literal("false"); return false; }
                if (c == 'n') { Literal("null"); return null; }
                if (c == '-' || (c >= '0' && c <= '9')) { return Numeric(); }
                throw new FormatException("invalid JSON value");
            }
            private void Literal(string literal)
            {
                if (pos + literal.Length > s.Length || s.Substring(pos, literal.Length) != literal)
                { throw new FormatException("invalid JSON literal"); }
                pos += literal.Length;
            }
            private string String()
            {
                if (pos >= s.Length || s[pos++] != '"') { throw new FormatException("expected JSON string"); }
                var b = new StringBuilder();
                while (pos < s.Length)
                {
                    char c = s[pos++];
                    if (c == '"') { return b.ToString(); }
                    if (c < 32) { throw new FormatException("unescaped JSON control character"); }
                    if (c != '\\') { b.Append(c); continue; }
                    if (pos >= s.Length) { throw new FormatException("invalid JSON escape"); }
                    c = s[pos++];
                    switch (c)
                    {
                        case '"': case '\\': case '/': b.Append(c); break;
                        case 'b': b.Append('\b'); break;
                        case 'f': b.Append('\f'); break;
                        case 'n': b.Append('\n'); break;
                        case 'r': b.Append('\r'); break;
                        case 't': b.Append('\t'); break;
                        case 'u':
                            if (pos + 4 > s.Length) { throw new FormatException("short unicode escape"); }
                            ushort u;
                            if (!ushort.TryParse(s.Substring(pos, 4), NumberStyles.AllowHexSpecifier,
                                CultureInfo.InvariantCulture, out u)) { throw new FormatException("bad unicode escape"); }
                            b.Append((char)u); pos += 4; break;
                        default: throw new FormatException("invalid JSON escape");
                    }
                }
                throw new FormatException("unterminated JSON string");
            }
            private Number Numeric()
            {
                int begin = pos;
                if (s[pos] == '-') { pos++; }
                if (pos >= s.Length) { throw new FormatException("short number"); }
                if (s[pos] == '0') { pos++; }
                else
                {
                    if (s[pos] < '1' || s[pos] > '9') { throw new FormatException("invalid number"); }
                    Digits();
                }
                if (pos < s.Length && s[pos] == '.') { pos++; Digits(); }
                if (pos < s.Length && (s[pos] == 'e' || s[pos] == 'E'))
                {
                    pos++;
                    if (pos < s.Length && (s[pos] == '+' || s[pos] == '-')) { pos++; }
                    Digits();
                }
                return new Number(s.Substring(begin, pos - begin));
            }
            private void Digits()
            {
                int begin = pos;
                while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') { pos++; }
                if (pos == begin) { throw new FormatException("missing digit"); }
            }
        }
    }

    public partial class BridgeMod
    {
        internal static string InvalidEnumResponse(string argument, string value, Type enumType)
        {
            return InvalidEnumDetails(new JsonObj().Bool("ok", false), argument, value, enumType);
        }

        private static string ActionInvalidEnumResponse(string argument, string value, Type enumType)
        {
            return InvalidEnumDetails(PreDispatchRejection().Str("verdict", "rejected"), argument, value, enumType);
        }

        private static string InvalidEnumDetails(JsonObj output, string argument, string value, Type enumType)
        {
            var o = output.Str("errorCode", "INVALID_ENUM")
                .Str("argument", argument).NullOrStr("value", value)
                .Str("error", "unsupported " + argument + " value");
            o.NullOrStr("resolvedEnum", enumType == null ? null : enumType.FullName);
            var names = new JsonArr();
            if (enumType != null && enumType.IsEnum)
            { foreach (var n in Enum.GetNames(enumType)) { names.Raw(Quote(n)); } }
            return o.Raw("values", names.Close()).Close();
        }

        internal static string SchemaInfoJson()
        {
            return new JsonObj().Bool("ok", true).Str("schema", "bridge-schema-3")
                .Str("myCityTiles", "own explored improvements of exact type City")
                .Str("myImprovementCount", "own explored improvements of any type")
                .Str("enemyCities", "explored City tiles owned by another real player; excludes neutral/Nature")
                .Str("neutralVillages", "explored City tiles with owner zero")
                .Str("inSight", "legacy explored alias; not a separate transient visibility predicate")
                .Str("currentlyVisible", "native unit visibility when available; tile current sight may be unknown")
                .Str("observationRevision", "monotonic read sequence, not a simulation state version")
                .Str("stateVersion", "visible-state change version within the event stream")
                .Str("damageReceivedMinusOne", "native sentinel: negative damage is not a HP change; inspect retaliation status")
                .Str("buildingCatalog", "unlocked catalog is global; tile legality, affordability and future tech associations are separate")
                .Str("handoff", "native-hotseat-handoff-1: handoff_status public phases; handoff_start requires configured playerId and expectedHandoffId")
                .Str("hotseatResume", "audit-only management: hotseat_games op=list/open, hotseat_resume gameId from the current native menu, hotseat_resume_status; native Task result is not map readiness")
                .Str("actionContext", "optional all-four expectedEpisodeId/expectedPlayerId/expectedStreamId/expectedStateVersion checked on the game main thread before dispatch")
                .Close();
        }

        private static Dictionary<string, string> GetArgs(string raw)
        {
            var output = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in BridgeJsonCodec.Object(raw)) { output[p.Key] = BridgeJsonCodec.Scalar(p.Value); }
            return output;
        }
    }
}
