using System;
using System.Globalization;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        // Shared native value adapters. Wrong CLR types stay unknown; readers must not turn
        // decimals into rounded HP/versions or numbers/text into actionable boolean flags.
        internal static int? NativeInt32(object value)
        {
            if (value == null || value.GetType().IsEnum) return null;
            var code = Type.GetTypeCode(value.GetType());
            if (code < TypeCode.SByte || code > TypeCode.UInt64) return null;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static bool? NativeBool(object value)
        {
            return value is bool ? (bool?)value : null;
        }

        internal static byte? NativePlayerId(object value, bool allowNature)
        {
            int? id = NativeInt32(value);
            return id.HasValue && id.Value > 0 && id.Value < (allowNature ? 256 : NaturePlayerId)
                ? (byte?)id.Value : null;
        }

        private static uint? NativeUInt32(object value)
        {
            if (value == null || value.GetType().IsEnum) return null;
            var code = Type.GetTypeCode(value.GetType());
            if (code < TypeCode.SByte || code > TypeCode.UInt64) return null;
            try { return Convert.ToUInt32(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }
    }
}
