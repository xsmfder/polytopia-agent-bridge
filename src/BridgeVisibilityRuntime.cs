using System;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private static string VisibilitySemanticsJson()
        {
            return new JsonObj().Str("schema", "visibility-semantics-1")
                .Str("explored", "native TileData.GetExplored; unvisited tiles are absent")
                .Str("inSight", "deprecated alias of explored in this game's tile renderer")
                .Str("tileCurrentlyVisible", "unknown: no separate transient sight predicate was established")
                .Str("unitCurrentlyVisible", "native TileData.GetVisibleUnit, filtered before publication")
                .Str("lastObservedRevision", "this observation's sequence; not a future guarantee")
                .Str("lastObservedTurn", "turn of the last published read")
                .Str("disappearance", "lost visibility or removed; no kill attribution without an observed native effect")
                .Str("blockedMove", "actual position is public; hidden blocking identity/reason is withheld")
                .Close();
        }
    }
}
