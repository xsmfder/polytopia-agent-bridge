using System;
using PolytopiaAgent;

internal static class BlockingPopupSettlementTests
{
    private static int passed, failed;
    private static void Check(bool condition, string name)
    { if (condition) passed++; else { failed++; Console.WriteLine("FAIL " + name); } }
    private static BridgeMod.CombatPipelineView Waiting()
    {
        // The live first-meeting popup left End Turn pending. These variants
        // exercise the native busy flags a modal reaction can retain; they are
        // not claimed to be captured audit flags from that historical run.
        return new BridgeMod.CombatPipelineView {
            PropertyProcessing = true, RawProcessing = true, CurrentReaction = true,
            Recapping = false, Simulating = false, Paused = true, Pausing = false,
            Holding = true, Reacting = true, ReactionCount = 1, Queued = true,
            PendingMine = 0, Popup = true, LocalTurn = true, HookDepth = 0,
            StableFrames = 2, QuietSeconds = 0.5 };
    }
    public static int Main()
    {
        var p = Waiting();
        Check(BridgeMod.CRPipelineSettlementPolicy(p) == "needs_resolution", "stable controlled-turn modal cannot wait for its own reaction to finish");
        Check(BridgeMod.CRGameplayPipelineIdle(p) == false, "needs resolution never invents idle pipeline");
        Check(BridgeMod.CRCompletionAuthority(p, "needs_resolution") == null, "modal blocking is not action completion");
        p = Waiting(); p.Popup = false;
        Check(BridgeMod.CRPipelineSettlementPolicy(p) == "pending", "busy pipeline without modal stays pending");
        p = Waiting(); p.LocalTurn = false;
        Check(BridgeMod.CRPipelineSettlementPolicy(p) == "pending", "another player's busy turn is not controlled resolution");
        p = Waiting(); p.Recapping = true;
        Check(BridgeMod.CRPipelineSettlementPolicy(p) == "pending", "recap continues before modal resolution");
        p = Waiting(); p.Simulating = true;
        Check(BridgeMod.CRPipelineSettlementPolicy(p) == "pending", "simulation remains pending");
        p = Waiting(); p.Pausing = true;
        Check(BridgeMod.CRPipelineSettlementPolicy(p) == "pending", "pause transition remains pending");
        p = Waiting(); p.HookDepth = 1;
        Check(BridgeMod.CRPipelineSettlementPolicy(p) == "pending", "executing native hook remains pending");
        p = Waiting(); p.StableFrames = 1;
        Check(BridgeMod.CRPipelineSettlementPolicy(p) == "pending", "busy modal still needs two stable samples");
        p = Waiting(); p.QuietSeconds = 0.39;
        Check(BridgeMod.CRPipelineSettlementPolicy(p) == "pending", "busy modal still needs quiet interval");
        p = Waiting(); p.RawProcessing = null;
        Check(BridgeMod.CRPipelineSettlementPolicy(p) == "unknown", "unreadable pipeline not hidden by popup");
        p = Waiting(); p.Popup = null;
        Check(BridgeMod.CRPipelineSettlementPolicy(p) == "unknown", "unknown modal state is not resolution proof");
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
