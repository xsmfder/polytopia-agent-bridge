// Reveal receipts follow executed explorer children; pending routes stay private.
using System;
using System.Collections.Generic;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal sealed class RuinsExplorerTrace
        {
            internal bool ChildrenReadable = true;
            internal readonly List<object> Scouts = new List<object>(), Explores = new List<object>();
            internal readonly List<object> CompletedScouts = new List<object>(), CompletedExplores = new List<object>();
            internal readonly List<string> NewlyExplored = new List<string>();
        }

        internal static bool CRRuinsExplorerChildMatches(CombatTicket t, object action, string kind)
        {
            if (!CRRuinsEffectScope(t, t == null ? null : t.Before) || t.NativeRuins.Reward != "Explorer"
                || t.NativeRuins.Explorer == null || action == null) return false;
            var p = t.NativeRuins.Explorer;
            var all = kind == "ScoutMoveAction" ? p.Scouts : kind == "ExploreAction" ? p.Explores : null;
            var done = kind == "ScoutMoveAction" ? p.CompletedScouts : p.CompletedExplores;
            return all != null && CRContainsNative(all, action) && !CRContainsNative(done, action)
                && SafeScalar(MemberWithAccessor(action, "PlayerId")) == t.Before.Me;
        }

        private bool CRHasPendingRuinsExplorer(object action, string kind)
        {
            foreach (var t in _combatTickets.Values)
                if (t.Stream == _combatStream && CRRuinsExplorerChildMatches(t, action, kind)) return true;
            return false;
        }

        private static void CRAppendExplorerChildren(RuinsExplorerTrace trace, object gs, int? previousCount)
        {
            List<object> scouts, explores;
            bool scoutRead = CRTryAppendedActions(gs, previousCount, "ScoutMoveAction", out scouts);
            bool exploreRead = CRTryAppendedActions(gs, previousCount, "ExploreAction", out explores);
            trace.ChildrenReadable &= scoutRead && exploreRead;
            foreach (var child in scouts)
                if (CRContainsNative(trace.Scouts, child)) trace.ChildrenReadable = false;
                else trace.Scouts.Add(child);
            foreach (var child in explores)
                if (CRContainsNative(trace.Explores, child)) trace.ChildrenReadable = false;
                else trace.Explores.Add(child);
        }

        private static void CRBindRuinsExplorer(CombatTicket t, object gs, int? previousCount)
        {
            if (t.NativeRuins == null || t.NativeRuins.Reward != "Explorer") return;
            t.NativeRuins.Explorer = new RuinsExplorerTrace();
            CRAppendExplorerChildren(t.NativeRuins.Explorer, gs, previousCount);
        }

        internal static bool CRRecordRuinsScout(CombatTicket t, object action, CombatFrame before,
            CombatFrame after, object gs, int? previousCount)
        {
            if (!CRRuinsExplorerChildMatches(t, action, "ScoutMoveAction")
                || !CRRuinsEffectScope(t, before) || !CRRuinsEffectScope(t, after)) return false;
            t.NativeRuins.Explorer.CompletedScouts.Add(action);
            CRAppendExplorerChildren(t.NativeRuins.Explorer, gs, previousCount);
            return true;
        }

        // Read only this linked child's own player's explored bit. No map-wide scan,
        // terrain, resource, unit, scout route, seed or future target is inspected.
        private bool? CRReadRuinsExplorerTile(object gs, CombatFrame frame, object action)
        {
            byte player; int x, y;
            if (frame == null || !frame.Complete || !byte.TryParse(frame.Me, out player)
                || player == 0 || player == NaturePlayerId || CurrentPlayerId(gs) != player
                || !TryParseCoords(SafeScalar(MemberWithAccessor(action, "Coordinates")), out x, out y)) return null;
            var tile = TileAtCoordinates(gs, x, y);
            if (tile == null) return null;
            var method = FindUniqueNativeMethod(tile.GetType(), "GetExplored", new[] {"Byte"}, "Boolean", false);
            if (method == null) return null;
            try { return NativeBool(method.Invoke(tile, new object[] {player})); }
            catch (Exception ex)
            { Log("ruins explored read #" + _activeRequestId + " ep=" + frame.Episode + " player=" + frame.Me
                + " failed: " + (ex.InnerException ?? ex).GetType().Name); return null; }
        }

        internal static bool CRRecordRuinsExplore(CombatTicket t, object action, CombatFrame before,
            CombatFrame after, bool? wasExplored, bool? isExplored)
        {
            if (!CRRuinsExplorerChildMatches(t, action, "ExploreAction")
                || !CRRuinsEffectScope(t, before) || !CRRuinsEffectScope(t, after)
                || !wasExplored.HasValue || isExplored != true) return false;
            string coord = SafeScalar(MemberWithAccessor(action, "Coordinates")); int x, y;
            if (!TryParseCoords(coord, out x, out y) || x < 0 || y < 0) return false;
            var p = t.NativeRuins.Explorer;
            p.CompletedExplores.Add(action);
            if (!wasExplored.Value && !p.NewlyExplored.Contains(coord)) p.NewlyExplored.Add(coord);
            return true;
        }

        internal static bool CRRuinsExplorerApplied(RuinsExplorerTrace p)
        {
            return p != null && p.ChildrenReadable && p.Scouts.Count > 0 && p.CompletedScouts.Count == p.Scouts.Count
                && p.CompletedExplores.Count == p.Explores.Count;
        }

        private static string CRRuinsExplorerJson(RuinsExplorerTrace p)
        {
            if (p == null) return "null";
            var tiles = new JsonArr();
            foreach (string coord in p.NewlyExplored) tiles.Raw(CRQ(coord));
            return new JsonObj().Str("status", CRRuinsExplorerApplied(p) ? "applied" : "pending_or_unconfirmed")
                .Raw("newlyExploredCount", p.NewlyExplored.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Raw("newlyExploredTiles", tiles.Close()).Close();
        }
    }
}
