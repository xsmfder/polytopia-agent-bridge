using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private bool TryReadCurrentPopup(out object current)
        {
            current = null;
            var type = FindGameType("PopupManager");
            var getter = UniqueInvocableMethod(type, "GetCurrentPopup", null, true);
            if (getter == null || getter.ReturnType == typeof(void) || getter.ReturnType.IsValueType) return false;
            try
            {
                object raw = getter.Invoke(null, null);
                current = raw == null ? null : BestPopupComponent(raw);
                return raw == null || current != null;
            }
            catch (Exception error)
            {
                RecordNativeReadFailure(type, "GetCurrentPopup", error);
                return false;
            }
        }

        // Absent metadata and a throwing accessor are different. Values are never cached.
        private static bool ReadOptionalPopupMember(object target, string name, out object value, out bool exists)
        {
            value = null; exists = false;
            if (target == null) return false;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            try
            {
                for (Type type = target.GetType(); type != null; type = type.BaseType)
                    if (type.GetMember(name, flags).Length > 0 || type.GetMember("get_" + name, flags).Length > 0)
                    { exists = true; return CRTryPipelineMember(target, name, out value); }
                return true;
            }
            catch (Exception error)
            { RecordNativeReadFailure(target.GetType(), name, error); exists = true; return false; }
        }

        private sealed class PopupButtonSnapshot
        {
            internal List<object> Data = new List<object>();
            internal List<string> Rows = new List<string>();
            internal HashSet<int> Ids = new HashSet<int>();
            internal Dictionary<int, int> Positions = new Dictionary<int, int>();
            internal bool Complete = true, PartialFields, Protected;
            internal string Json(string type)
            {
                string rows = "[" + string.Join(",", Rows.ToArray()) + "]";
                var result = new JsonObj().Bool("ok", true).Str("popupType", type)
                    .Str("status", Complete && !PartialFields ? "ok" : Rows.Count > 0 ? "partial" : "unavailable")
                    .Bool("complete", Complete && !PartialFields)
                    .Raw("buttonCount", Complete ? Rows.Count.ToString(CultureInfo.InvariantCulture) : "null")
                    .Raw("observedButtonCount", Rows.Count.ToString(CultureInfo.InvariantCulture)).Raw("buttons", rows);
                if (Protected) result.Raw("m_buttonData", rows);
                return result.Close();
            }
        }

        private PopupButtonSnapshot ReadPopupButtons(object current)
        {
            var result = new PopupButtonSnapshot();
            object raw; bool exists;
            bool primary = ReadOptionalPopupMember(current, "buttonData", out raw, out exists);
            bool hadPrimary = exists;
            var data = primary && exists ? raw == null ? new List<object>() : ReadNativeList(raw, true) : null;
            if (data == null || data.Count == 0)
            {
                object fallback; bool fallbackExists;
                bool readable = ReadOptionalPopupMember(current, "m_buttonData", out fallback, out fallbackExists);
                if (fallbackExists)
                {
                    var secondary = readable ? fallback == null ? new List<object>() : ReadNativeList(fallback, true) : null;
                    if (secondary != null) { data = secondary; result.Protected = true; }
                    else result.Complete = false;
                }
            }
            if (!primary || (!hadPrimary && !result.Protected) || data == null) result.Complete = false;
            if (data != null) result.Data = data;
            foreach (var item in result.Data)
            {
                if (item == null) { result.Complete = false; continue; }
                int? id = NativeInt32(MemberWithAccessor(item, "id"));
                if (!id.HasValue || !result.Ids.Add(id.Value)) result.Complete = false;
                else result.Positions[id.Value] = result.Rows.Count;
                object callback; bool hasCallback;
                bool callbackReadable = ReadOptionalPopupMember(item, "callback", out callback, out hasCallback) && hasCallback;
                bool? closes = NativeBool(MemberWithAccessor(item, "closesPopup"));
                if (!callbackReadable || !closes.HasValue) result.PartialFields = true;
                var row = new JsonObj().Raw("index", result.Rows.Count.ToString(CultureInfo.InvariantCulture))
                    .NullOrStr("text", SafeScalar(MemberWithAccessor(item, "text")))
                    .NullOrStr("id", id.HasValue ? id.Value.ToString(CultureInfo.InvariantCulture) : null)
                    .NullOrStr("closesPopup", closes.HasValue ? closes.Value ? "True" : "False" : null)
                    .NullableBool("hasCallback", callbackReadable ? (bool?)(callback != null) : null);
                result.Rows.Add(row.Close());
            }
            if (IsIconRewardPopup(current))
            {
                var icon = ReadIconRewardChoice(current);
                if (icon == null) result.Complete = false;
                else if (result.Ids.Add(icon.Id)) result.Rows.Add(icon.Json(result.Rows.Count));
                else
                {
                    int position;
                    if (result.Positions.TryGetValue(icon.Id, out position)) result.Rows[position] = icon.Json(position);
                }
            }
            return result;
        }

        private string PopupButtons()
        {
            object current;
            if (!TryReadCurrentPopup(out current) || current == null)
                return new JsonObj().Bool("ok", false).Str("status", "unavailable")
                    .Str("errorCode", "POPUP_READ_UNAVAILABLE").Raw("buttonCount", "null").Raw("buttons", "[]").Close();
            return PopupButtonsFor(current);
        }

        private string PopupButtonsFor(object current)
        { return ReadPopupButtons(current).Json(current.GetType().Name); }

        private bool? PopupNeedsChoice()
        {
            bool? showing = PopupIsOpen();
            if (showing == false) return false;
            if (showing != true) return null;
            object current;
            if (!TryReadCurrentPopup(out current) || current == null || ReadActive(current) != "true") return null;
            object rewards; bool hasRewards;
            if (!ReadOptionalPopupMember(current, "cityRewards", out rewards, out hasRewards)) return null;
            if (hasRewards)
            {
                var offers = ReadNativeList(rewards);
                if (offers == null) return null;
                foreach (var offer in offers) if (offer != null) return true;
                if (offers.Count != 0) return null;
            }
            var buttons = ReadPopupButtons(current);
            if (buttons.Ids.Count > 1) return true;
            foreach (int id in buttons.Ids) if (id != -1) return true;
            if (!buttons.Complete || buttons.PartialFields) return null;
            if (buttons.Rows.Count == 0) return false;
            var row = BridgeJsonCodec.Object(buttons.Rows[0]);
            object closes;
            return row.TryGetValue("closesPopup", out closes) && object.Equals(closes, "True") ? false : (bool?)true;
        }
    }
}
