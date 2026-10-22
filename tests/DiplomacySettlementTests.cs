using System;
using System.Collections.Generic;
using System.Reflection;
using PolytopiaAgent;

public class GameState
{
    public PlayerState[] Players { get; set; }
    public bool HasEmbassy = true;
}
public class PlayerState
{
    public byte Id; public int Currency = 10; public bool Hidden;
    public List<byte> knownPlayers = new List<byte> { 2, 3 };
    public Dictionary<byte,object> Relations = new Dictionary<byte,object>();
    public object relations { get { if (Hidden) throw new Exception("opponent private relations read"); return Relations; } }
}
public class DestroyEmbassyCommand
{
    public byte PlayerId = 2, OpponentId = 3;
    public string GetCommandType() { return "DestroyEmbassy"; }
    public object Coordinates { get { throw new Exception("private coordinates read"); } }
    public bool IsValid(GameState gs, out string error) { error = null; return gs.HasEmbassy; }
}
public static class CommandUtils
{
    public static int Calls; public static object Command = new DestroyEmbassyCommand();
    public static object GetDiplomacyCommandsForOpponent(GameState gs, PlayerState ps, byte other, bool unavailable)
    { Calls++; return gs.HasEmbassy ? new object[] { Command } : new object[0]; }
}
internal static class DiplomacySettlementTests
{
    private sealed class Manager { public static PlayerState LocalPlayer { get; set; } }
    private sealed class Relation { public string State = "Peace"; }
    private sealed class Action { public byte PlayerId = 2, OpponentId = 3; }
    private sealed class ForeignAction
    { public byte PlayerId = 3; public byte OpponentId { get { throw new Exception("foreign target read"); } } }
    private static readonly BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int passed;
    private static object Call(BridgeMod h, string name, params object[] args)
    { return typeof(BridgeMod).GetMethod(name, F).Invoke(h, args); }
    private static object Get(BridgeMod h, string name) { return typeof(BridgeMod).GetField(name,F).GetValue(h); }
    private static void Set(BridgeMod h, string name, object value) { typeof(BridgeMod).GetField(name,F).SetValue(h,value); }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static string Confirm(BridgeMod h, long ticket, GameState gs)
    { return (string)Call(h,"PublicSettlementConfirmation",ticket,gs,null); }
    public static int Main()
    { try { return Run(); } catch(Exception e) { Console.WriteLine(e); return 1; } }
    private static int Run()
    {
        var h = new BridgeMod(); var own = new PlayerState { Id = 2 }; var enemy = new PlayerState { Id = 3, Hidden = true };
        var relation = new Relation(); own.Relations[3] = relation;
        var gs = new GameState { Players = new[] { own, enemy } };
        Manager.LocalPlayer = own;
        Set(h,"_gameManagerType",typeof(Manager)); Set(h,"_playersProp",typeof(GameState).GetProperty("Players"));
        Set(h,"_combatStream","test-stream");
        string episode = (string)Call(h,"EpisodeId",gs);
        var tickets = (Dictionary<long,BridgeMod.CombatTicket>)Get(h,"_combatTickets");
        var active = (List<BridgeMod.PublicDiplomacyObservation>)Get(h,"_publicDiplomacyActive");
        var proofs = (Dictionary<long,BridgeMod.PublicDiplomacyObservation>)Get(h,"_publicDiplomacyProof");
        var ticket = new BridgeMod.CombatTicket { Id = 1, RequestId = 5, Action = "break_peace", Arg = "3", Stream = "test-stream",
            Before = new BridgeMod.CombatFrame { Episode = episode, Me = "2" }, Accepted = true };
        tickets[1] = ticket;
        Check(Confirm(h,1,gs) == "unknown", "accepted break command alone does not confirm effect");
        var action = new Action();
        Call(h,"PublicNativeActionPrefix",gs,new ForeignAction(),"BreakPeaceAction");
        Check(active.Count == 0, "foreign actor ignored before target read");
        Call(h,"PublicNativeActionPrefix",gs,new Action { OpponentId = 4 },"BreakPeaceAction");
        Check(active.Count == 0, "unmet target cannot produce public proof");
        ticket.Arg = "4"; Call(h,"PublicNativeActionPrefix",gs,action,"BreakPeaceAction");
        Check(active.Count == 0, "known but different ticket target ignored"); ticket.Arg = "3";
        ticket.Accepted = false; Call(h,"PublicNativeActionPrefix",gs,action,"BreakPeaceAction");
        Check(active.Count == 0, "rejected ticket cannot acquire a later action proof"); ticket.Accepted = true;
        Call(h,"PublicNativeActionPrefix",gs,action,"BreakPeaceAction");
        Check(active.Count == 1 && active[0].OwnStateBefore == "Peace", "matching own break action records only own pre-state");
        relation.State = "CoolDown";
        Check(Confirm(h,1,gs) == "unknown", "visible relationship transition without completed action is insufficient");
        Call(h,"PublicNativeActionComplete",gs,new Action(),"BreakPeaceAction");
        Check(proofs.Count == 0, "different native action object cannot complete proof");
        Call(h,"PublicNativeActionComplete",gs,action,"BreakPeaceAction");
        Check(active.Count == 0 && Confirm(h,1,gs) == "confirmed", "completed correlated break action and Peace to CoolDown confirm");
        var breakProof = proofs[1]; string basis;
        breakProof.OwnStateAfter = "War";
        Check(BridgeMod.PublicDiplomacyProofConfirmation(breakProof,"break_peace",out basis) == "confirmed", "legacy native post-break War is recognized");
        breakProof.OwnStateAfter = "Peace";
        Check(Confirm(h,1,gs) == "not_confirmed", "unchanged Peace does not confirm break");
        breakProof.OwnStateAfter = null;
        Check(Confirm(h,1,gs) == "unknown", "unknown own relation never treated as nonpeace");
        breakProof.OwnStateAfter = "CoolDown"; breakProof.OwnStateBefore = "Neutral";
        Check(Confirm(h,1,gs) == "not_confirmed", "a relation that was not Peace cannot satisfy break proof");
        breakProof.OwnStateBefore = "Peace"; breakProof.Other = 4;
        Check(Confirm(h,1,gs) == "unknown", "stored proof must match original ticket target"); breakProof.Other = 3;
        breakProof.Me = 3; Check(Confirm(h,1,gs) == "unknown", "stored proof must match original ticket actor"); breakProof.Me = 2;
        ticket.Stream = "old-stream"; Check(Confirm(h,1,gs) == "unknown", "old stream proof rejected"); ticket.Stream = "test-stream";
        breakProof.Episode = "old-episode"; Check(Confirm(h,1,gs) == "unknown", "old episode proof rejected"); breakProof.Episode = episode;
        Check(BridgeMod.PublicDiplomacyProofConfirmation(breakProof,"destroy_embassy",out basis) == "unknown", "wrong native action kind cannot substitute another diplomacy operation");
        ticket.FinalFrame = new BridgeMod.CombatFrame();
        ticket = new BridgeMod.CombatTicket { Id = 2, RequestId = 6, Action = "destroy_embassy", Arg = "3", Stream = "test-stream",
            Before = new BridgeMod.CombatFrame { Episode = episode, Me = "2" }, Accepted = true };
        tickets[2] = ticket; var destroy = new Action();
        Call(h,"PublicNativeActionPrefix",gs,destroy,"DestroyEmbassyAction");
        Check(active.Count == 1 && active[0].QualifierBefore == true, "destroy proof captures current native command before action");
        int calls = CommandUtils.Calls; gs.HasEmbassy = false;
        Call(h,"PublicNativeActionComplete",gs,destroy,"DestroyEmbassyAction");
        Check(Confirm(h,2,gs) == "confirmed" && CommandUtils.Calls == calls, "completed destroy revalidates same command after removal without reconstructing menu");
        Check(proofs[2].OwnStateBefore == proofs[2].OwnStateAfter, "embassy removal does not require an unrelated own relation transition");
        proofs[2].QualifierAfter = true;
        Check(Confirm(h,2,gs) == "not_confirmed", "embassy command remaining valid prevents removal confirmation");
        proofs[2].QualifierAfter = null;
        Check(Confirm(h,2,gs) == "unknown", "unreadable native embassy qualification remains unknown");
        proofs[2].QualifierAfter = false; proofs[2].Complete = false;
        Check(Confirm(h,2,gs) == "unknown", "incomplete destroy action cannot confirm despite matching qualification");
        Console.WriteLine("PASS total " + passed); return 0;
    }
}
