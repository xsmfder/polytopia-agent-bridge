// Additional native training locations. No hidden/global tile enumeration occurs here.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal sealed class OwnedWaterTrainSite
        {
            internal object Tile;
            internal string Coordinates, Kind;
        }

        // The boolean is supplied by the existing fog-filtered world pass. Ownership
        // is checked before touching native water/effect fields on any foreign tile.
        internal static OwnedWaterTrainSite OwnWaterTrainSite(object tile, byte me, bool explored)
        {
            if (!explored || tile == null || SafeScalar(MemberWithAccessor(tile, "owner")) != me.ToString(CultureInfo.InvariantCulture)) return null;
            string coord = SafeScalar(MemberWithAccessor(tile, "coordinates"));
            int x, y;
            if (!TryParseCoords(coord, out x, out y)) return null;
            if (NativeBool(ReadBoolPropertyOrGetter(tile, "IsWater")) == true)
                return new OwnedWaterTrainSite { Tile = tile, Coordinates = coord, Kind = "owned-water" };
            object raw;
            var effects = TryReadNativeMember(tile, "effects", out raw) ? ReadNativeList(raw) : null;
            if (effects != null) foreach (object effect in effects)
            {
                Type type = effect == null ? null : effect.GetType();
                if (type != null && type.IsEnum && type.Name == "EffectType" && type.DeclaringType != null
                    && type.DeclaringType.Name == "TileData" && Enum.IsDefined(type, effect) && SafeScalar(effect) == "Algae")
                    return new OwnedWaterTrainSite { Tile = tile, Coordinates = coord, Kind = "owned-algae" };
            }
            return null;
        }

        private static List<OwnedWaterTrainSite> TrainingSites(List<object> cityTiles, List<OwnedWaterTrainSite> waterSites)
        {
            var result = new List<OwnedWaterTrainSite>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // Preserve the existing cities view and its capacity semantics. A city
            // that is also water/algae is queried once with its original category.
            foreach (object tile in cityTiles)
            {
                if (tile == null) continue;
                string coord = SafeScalar(MemberWithAccessor(tile, "coordinates"));
                if (coord != null && seen.Add(coord))
                    result.Add(new OwnedWaterTrainSite { Tile = tile, Coordinates = coord, Kind = "city" });
            }
            if (waterSites != null) foreach (var site in waterSites)
                if (site != null && site.Tile != null && site.Coordinates != null && seen.Add(site.Coordinates)) result.Add(site);
            return result;
        }

        private static void AddTrainingSiteRow(JsonArr cities, JsonArr sites, OwnedWaterTrainSite site, string json)
        {
            sites.Raw(json);
            if (site.Kind == "city") cities.Raw(json);
        }
    }
}
