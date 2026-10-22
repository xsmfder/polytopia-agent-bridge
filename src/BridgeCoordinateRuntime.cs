using System;
using System.Globalization;
using System.Reflection;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        // The native array is indexed y*width+x. Check BOTH dimensions before the
        // multiplication: a valid-looking linear index can still name a different tile.
        private bool TryMapIndex(object gs, int x, int y, out object map, out int index, out int width)
        {
            map = null; index = -1; width = 0;
            if (gs == null || x < 0 || y < 0 || !PrivateSamplingAllowed(gs)) return false;
            try
            {
                map = Val(_mapProp, gs);
                if (map == null) return false;
                int? w = NativeInt32(MemberWithAccessor(map, "Width"));
                int? h = NativeInt32(MemberWithAccessor(map, "Height"));
                if (!w.HasValue || !h.HasValue || w.Value <= 0 || h.Value <= 0
                    || x >= w.Value || y >= h.Value) return false;
                long linear = (long)y * w.Value + x;
                if (linear > Int32.MaxValue) return false;
                width = w.Value; index = (int)linear; return true;
            }
            catch (Exception ex)
            { Log("coordinate bounds unavailable: " + (ex.InnerException ?? ex).GetType().Name); return false; }
        }

        // UpgradeCommand changes a unit on ONE tile; it never upgrades a city.
        // Return arguments only, so the common dispatcher binds metadata before construction.
        private bool TryPrepareUnitUpgrade(object gs, string from, string at, string kind,
            out object[] arguments, out string errorCode)
        {
            arguments = null; errorCode = "INVALID_COORDINATES";
            int x, y, fx, fy;
            string target = string.IsNullOrEmpty(at) ? from : at;
            if (!TryParseCoords(target, out x, out y)) return false;
            if (!string.IsNullOrEmpty(from)
                && (!TryParseCoords(from, out fx, out fy) || fx != x || fy != y))
            { errorCode = "UPGRADE_COORDINATE_MISMATCH"; return false; }
            errorCode = "UNIT_ACTOR_UNAVAILABLE";
            if (FindUnitAt(gs, target) == null) return false;
            errorCode = "UNIT_TYPE_UNAVAILABLE";
            Type unitType = FindType("UnitData+Type") ?? FindTypeBySuffix("UnitData+Type");
            if (unitType == null || !unitType.IsEnum) return false;
            // Enum.Parse alone accepts undefined numbers and comma combinations.
            // Accept current native names, including future native upgrade branches.
            errorCode = "INVALID_UNIT_TYPE";
            object nativeKind;
            if (!TryNamedNativeEnum(unitType, kind, out nativeKind)) return false;
            errorCode = "INVALID_COORDINATES";
            object coordinates = BuildCoordinates(x, y, gs);
            if (coordinates == null) return false;
            byte player = CurrentPlayerId(gs);
            if (player == 0 || player == NaturePlayerId)
            { errorCode = "UNIT_ACTOR_UNAVAILABLE"; return false; }
            arguments = new object[] {player, nativeKind, coordinates};
            errorCode = null; return true;
        }

        // Used by the legacy public action branch as well as its read-only offline
        // preparation tests. Native eligibility/dispatch still belongs to ActionManager.
        private bool TryPrepareCaptureCommand(object gs, string from, string at,
            out object command, out string errorCode)
        {
            command = null; errorCode = "UNIT_ACTOR_UNAVAILABLE";
            object unit = FindUnitAt(gs, from);
            if (unit == null) return false;
            uint? id = NativeUInt32(MemberWithAccessor(unit, "id"));
            if (!id.HasValue) { errorCode = "UNIT_ID_UNAVAILABLE"; return false; }
            int x, y;
            if (!TryParseCoords(at, out x, out y)) { errorCode = "INVALID_COORDINATES"; return false; }
            object coordinates = BuildCoordinates(x, y, gs);
            if (coordinates == null) { errorCode = "INVALID_COORDINATES"; return false; }
            byte player = CurrentPlayerId(gs);
            if (player == 0 || player == NaturePlayerId) return false;
            errorCode = "CAPTURE_COMMAND_UNAVAILABLE";
            var type = FindGameType("CaptureCommand");
            var constructor = type == null ? null : type.GetConstructor(new[] {typeof(byte), typeof(uint), coordinates.GetType()});
            if (constructor == null) return false;
            try
            {
                command = constructor.Invoke(new object[] {player, id.Value, coordinates});
                errorCode = null; return true;
            }
            catch (Exception ex)
            {
                errorCode = "CAPTURE_CONSTRUCTION_FAILED";
                Log("capture preparation failed: " + (ex.InnerException ?? ex).GetType().Name); return false;
            }
        }
    }
}
