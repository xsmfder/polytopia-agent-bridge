using System;
using System.Collections;
using System.Collections.Generic;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        // availableTech contains owned enum names, not research candidates. A partial
        // enumeration must never look like a player owning fewer (or zero) techs.
        internal static HashSet<string> ReadOwnedTechNames(object raw)
        {
            var values = ReadNativeList(raw);
            if (values == null) return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (value == null || (!(value is string) && !value.GetType().IsEnum)) return null;
                string name = SafeScalar(value);
                if (string.IsNullOrEmpty(name)) return null;
                if (!(char.IsLetter(name[0]) || name[0] == '_'))
                {
                    Type type=value.GetType();
                    if(!type.IsEnum || type.DeclaringType==null || type.DeclaringType.Name!="TechData"
                        || RegisteredRuleName(value,type)==null)return null;
                }
                foreach (char c in name) if (!(char.IsLetterOrDigit(c) || c == '_')) return null;
                if (!names.Add(name)) return null;
            }
            return names;
        }

        internal static string OwnedTechFingerprint(object raw)
        {
            return OwnedTechFingerprint(ReadOwnedTechNames(raw));
        }
        // Already validated owned names must not be re-parsed as untrusted raw strings.
        internal static string OwnedTechFingerprint(HashSet<string> names)
        {
            if (names == null) return null;
            var sorted = new List<string>(names);
            sorted.Sort(StringComparer.Ordinal);
            return string.Join(",", sorted.ToArray());
        }

        // One complete owned-tech sample feeds both public state and research filtering.
        // A failed getter/list is unknown, never a fabricated empty owned set.
        internal static HashSet<string> ReadPlayerOwnedTechNames(object player)
        {
            object raw;
            return TryReadNativeMember(player, "availableTech", out raw) ? ReadOwnedTechNames(raw) : null;
        }

        internal static HashSet<string> AppendOwnedTechFields(JsonObj output, object player)
        {
            var names = ReadPlayerOwnedTechNames(player);
            string json = "null";
            if (names != null)
            {
                var sorted = new List<string>(names);
                sorted.Sort(StringComparer.Ordinal);
                var array = new JsonArr();
                foreach (string name in sorted) array.Raw(Quote(name));
                json = array.Close();
            }
            output.Raw("ownedTechs", json).Str("ownedTechsStatus", names == null ? "unavailable" : "ok")
                .Raw("availableTechs", json).Bool("availableTechsDeprecated", true);
            return names;
        }

        // Executable menus need every slot. Unknown/missing entries must not become
        // a shorter complete menu; retain native objects and valid empty menus.
        internal static List<object> ReadNativeCommandList(object raw)
        {
            var commands = ReadNativeList(raw);
            if (commands == null) return null;
            foreach (var command in commands) if (command == null) return null;
            return commands;
        }

        // Snapshot the current collection; do not cache native objects or leave a lazy
        // enumerator to throw later while composing a response. Target/path readers need
        // an all-or-nothing list. Candidate catalogs can retain explicit unreadable slots
        // so their callers preserve known members while reporting incompleteness.
        internal static List<object> ReadNativeList(object raw, bool preserveUnreadableSlots = false)
        {
            if (raw == null || raw is string) return null;
            try
            {
                var result = new List<object>();
                var list = raw as IList;
                if (list != null)
                {
                    int count = list.Count;
                    if (count < 0) return null;
                    for (int i = 0; i < count; i++)
                    {
                        try { result.Add(list[i]); }
                        catch (Exception error)
                        { RecordNativeReadFailure(raw.GetType(), "Item", error); if (!preserveUnreadableSlots) return null; result.Add(null); }
                    }
                    return result;
                }
                var sequence = raw as IEnumerable;
                if (sequence != null)
                {
                    foreach (var value in sequence) result.Add(value);
                    return result;
                }
                var type = raw.GetType();
                var item = type.GetProperty("Item", new[] { typeof(int) });
                var countProperty = type.GetProperty("Count");
                if (item == null || countProperty == null) return null;
                int? size = NativeInt32(countProperty.GetValue(raw, null));
                if (!size.HasValue || size.Value < 0) return null;
                for (int i = 0; i < size.Value; i++)
                {
                    try { result.Add(item.GetValue(raw, new object[] { i })); }
                    catch (Exception error)
                    { RecordNativeReadFailure(raw.GetType(), "Item", error); if (!preserveUnreadableSlots) return null; result.Add(null); }
                }
                return result;
            }
            catch (Exception error) { RecordNativeReadFailure(raw.GetType(), "collection", error); return null; }
        }
    }
}
