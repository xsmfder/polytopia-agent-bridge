using System;
using System.Reflection;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        // Only the native UI's boolean is exposed, never its hidden-neighbor scan.
        internal static string AdjacentInvisibleHint(object unit, object gs, bool own)
        {
            bool? present = null;
            if (own && unit != null && gs != null)
            {
                var method = HintMethod(unit.GetType(), "IsDetectingHiddenUnits", new[] { "GameState" }, "Boolean");
                try { if (method != null) present = method.Invoke(unit, new[] { gs }) as bool?; }
                catch (Exception error) { RecordNativeReadFailure(unit.GetType(), "IsDetectingHiddenUnits", error); }
            }
            return new JsonObj().NullableBool("present", present)
                .Str("status", !own ? "withheld" : present.HasValue ? "ok" : "unavailable")
                .Str("authority", "UnitState.IsDetectingHiddenUnits; own unit's native adjacent invisible non-peace threat indicator")
                .Close();
        }

        private static MethodInfo HintMethod(Type type, string name, string[] parameters, string returns)
        {
            return FindUniqueNativeMethod(type, name, parameters, returns, false);
        }

        // Actual renderer inputs, not owner/skin guesses. Native SetupForTile
        // takes climate from TileData and skin from Tile.GetVisualSkinTypeForTile.
        // Method metadata is resolved once per world read; no values are cached.
        internal sealed class VisibleStyleContext
        {
            internal object Renderer;
            internal MethodInfo GetTile, GetSkin;

            internal string Read(object tile, bool explored)
            {
                if (!explored) return "null";
                string tribe = null, skin = null;
                try
                {
                    if (Renderer != null && tile != null && GetTile != null && GetSkin != null)
                    {
                        object coordinates = MemberWithAccessor(tile, "coordinates");
                        object rendered = coordinates == null ? null : GetTile.Invoke(Renderer, new[] { coordinates });
                        if (rendered != null && CRSameNative(MemberWithAccessor(rendered, "Data"), tile))
                        {
                            tribe = SafeScalar(MemberWithAccessor(tile, "climate"));
                            skin = SafeScalar(GetSkin.Invoke(rendered, null));
                        }
                    }
                }
                catch (Exception error)
                { RecordNativeReadFailure(tile == null ? null : tile.GetType(), "visual_style", error); tribe = null; skin = null; }
                return new JsonObj().NullOrStr("tribe", tribe).NullOrStr("skin", skin)
                    .Str("status", tribe != null && skin != null ? "ok" : "unavailable")
                    .Str("authority", "TileData.climate + Tile.GetVisualSkinTypeForTile; verified SkinVisualsTransientData and TerrainRenderer inputs, independent of owner")
                    .Close();
            }
        }

        private static VisibleStyleContext CreateVisibleStyleContext(object gs)
        {
            var context = new VisibleStyleContext();
            try
            {
                Type renderer = FindGameType("MapRenderer"), tile = FindGameType("Tile");
                if (gs == null || renderer == null || tile == null) return context;
                object current;
                context.Renderer = TryReadNativeStaticMember(renderer, "Current", out current) ? current : null;
                if (context.Renderer == null) return context;
                context.GetTile = HintMethod(renderer, "GetTileInstance", new[] { "WorldCoordinates" }, "Tile");
                context.GetSkin = HintMethod(tile, "GetVisualSkinTypeForTile", new string[0], "SkinType");
            }
            catch (Exception error)
            { RecordNativeReadFailure(typeof(VisibleStyleContext), "bind_renderer", error); context.Renderer = null; }
            return context;
        }
    }
}
