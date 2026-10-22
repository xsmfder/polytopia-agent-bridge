// Offline regression harness for the bridge's JSON formatting seams (codex-dsh-001).
//
// It calls the SAME functions the live bridge uses (BridgeMod.Render*/PlanUiInvocation) and
// replays the exact frames that were observed broken in out.jsonl, so a fix cannot pass by
// only matching hand-written JSON fixtures.
//
// Run:  csc /out:FormatterTests.exe ... src\BridgeMod.cs tests\FormatterTests.cs
//       FormatterTests.exe   (exit 0 = all pass)

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PolytopiaAgent;

internal static class FormatterTests
{
    private static int _pass;
    private static int _fail;

    private static void Check(bool cond, string label, string detail)
    {
        if (cond) { _pass++; Console.WriteLine("PASS  " + label); }
        else { _fail++; Console.WriteLine("FAIL  " + label + (detail == null ? "" : "  :: " + detail)); }
    }

    /// <summary>The real defect class: a comma immediately before } or ] , or doubled ,,</summary>
    private static string BadJsonReason(string s)
    {
        if (s == null) { return "null"; }
        if (s.Contains("{,")) { return "comma before property name (\"{,\")"; }
        if (s.Contains(",}")) { return "trailing comma before }"; }
        if (s.Contains(",]")) { return "trailing comma before ]"; }
        if (s.Contains(",,")) { return "doubled comma"; }
        return null;
    }

    private static BridgeMod.ButtonInfo Btn(string name, string label, string active, string pos)
    {
        var b = new BridgeMod.ButtonInfo();
        b.Name = name;
        b.Label = label;
        b.Active = active;
        b.Pos = pos;
        return b;
    }

    private static void Main(string[] args)
    {
        Console.WriteLine("=== ui: read-only listing (empty name) ===");
        var empty = new List<BridgeMod.ButtonInfo>();
        string readNone = BridgeMod.RenderButtonsReadOnly(empty, "", false, 64);
        Check(BadJsonReason(readNone) == null, "read-only, zero buttons is valid JSON", readNone);
        Check(readNone.Contains("\"buttons\":[]"), "read-only, zero buttons has empty array", readNone);
        Check(BridgeMod.PlanIsReadOnly(readNone), "read-only frame is classified read-only", readNone);

        var two = new List<BridgeMod.ButtonInfo>
        {
            Btn("WorkshopButton", "Workshop", "true", "(0,0,0)"),
            Btn("ExplorerButton", "Explorer", "true", "(1,0,0)"),
            Btn("Hidden", "Explorer", "false", null),
        };
        string readTwo = BridgeMod.RenderButtonsReadOnly(two, "", false, 64);
        Check(BadJsonReason(readTwo) == null, "read-only, two buttons valid", readTwo);
        Check(readTwo.Contains("\"label\":\"Workshop\""), "labels present", readTwo);

        Console.WriteLine("=== ui: invocation planning ===");
        string planExact = BridgeMod.PlanUiInvocation(two, "Explorer", false, 64);
        Check(BridgeMod.PlanTarget(planExact) == 1, "exact label match picks the active Explorer (index 1)", planExact);
        Check(!BridgeMod.PlanIsReadOnly(planExact) && !BridgeMod.PlanIsRefusal(planExact), "plan is an invoke plan", planExact);

        string planNone = BridgeMod.PlanUiInvocation(two, "Nope", false, 64);
        Check(BridgeMod.PlanIsRefusal(planNone) && planNone.Contains("no button matches query"),
            "no match is an explicit refusal (no blind click)", planNone);
        Check(BadJsonReason(planNone) == null, "no-match frame valid JSON", planNone);

        var dupes = new List<BridgeMod.ButtonInfo>
        {
            Btn("A", "OK", "true", null),
            Btn("B", "OK", "true", null),
        };
        string planAmbig = BridgeMod.PlanUiInvocation(dupes, "OK", false, 64);
        Check(BridgeMod.PlanIsRefusal(planAmbig) && planAmbig.Contains("ambiguous"),
            "multiple candidates is an ambiguity error, not a blind click", planAmbig);
        Check(BadJsonReason(planAmbig) == null, "ambiguity frame valid JSON", planAmbig);
        Check(planAmbig.Contains("candidates"), "ambiguity lists candidates", planAmbig);

        Console.WriteLine("=== ui: result frames (the frame that used to be malformed) ===");
        string okInvoke = BridgeMod.RenderUiResult(two, "Explorer", false, 64, "ExplorerButton/Explorer", null);
        Check(BadJsonReason(okInvoke) == null, "success frame valid JSON (was: property after closing brace)", okInvoke);
        Check(okInvoke.Contains("\"invoked\":\"ExplorerButton/Explorer\""), "invoked present on success", okInvoke);

        string errInvoke = BridgeMod.RenderUiResult(two, "Explorer", false, 64, null, "TargetInvocationException: boom");
        Check(BadJsonReason(errInvoke) == null, "failure frame valid JSON (was: property after closing brace)", errInvoke);
        Check(!errInvoke.Contains("\"invoked\""), "invoked absent when the call did not return", errInvoke);
        Check(errInvoke.Contains("invokeError"), "invokeError present on failure", errInvoke);

        Console.WriteLine("=== triggers: the exact broken shape ===");
        // Replay of the observed bad frame: properties empty, fields non-empty.
        var wl = new Dictionary<string, string>
        {
            { "playerId", "1" }, { "opponentId", "0" }, { "type", "CityLevelUp" }, { "coordinates", "(4,8)" }
        };
        string one = BridgeMod.RenderTrigger(true, "CommandTrigger", wl, true);
        Check(BadJsonReason(one) == null, "trigger with fields is valid JSON (was: \"{,\\\"fields\\\"\")", one);
        Check(one.Contains("\"fields\":{") && one.Contains("\"coordinates\":\"(4,8)\""), "whitelisted fields present", one);

        string noneReadable = BridgeMod.RenderTrigger(true, "CommandTrigger", new Dictionary<string, string>(), false);
        Check(BadJsonReason(noneReadable) == null, "trigger with zero readable members is valid JSON", noneReadable);
        Check(noneReadable.Contains("\"unsupported\":true"), "zero readable members reported as unsupported", noneReadable);

        string unsupportedType = BridgeMod.RenderTrigger(false, "SomethingElse", new Dictionary<string, string>(), false);
        Check(BadJsonReason(unsupportedType) == null, "unsupported type frame valid JSON", unsupportedType);
        Check(unsupportedType.Contains("unsupported_type"), "unsupported type is explicit", unsupportedType);

        Console.WriteLine("=== triggers: whole-frame frames ===");
        string emptyList = BridgeMod.RenderTriggers(true, null, "Il2CppSystem.Collections.Generic.List`1[Il2Cpp.CommandTrigger]", "CommandTrigger", new List<string>(), false, 8);
        Check(BadJsonReason(emptyList) == null, "empty trigger list is valid JSON", emptyList);
        Check(emptyList.Contains("\"triggers\":[]"), "empty trigger list has empty array", emptyList);

        string many = BridgeMod.RenderTriggers(true, null, "rawTypeName", "CommandTrigger",
            new List<string> { one, one, one }, true, 8);
        Check(BadJsonReason(many) == null, "multiple triggers valid JSON", many);
        Check(many.Contains("\"truncated\":true"), "truncation flag surfaced", many);

        string errFrame = BridgeMod.RenderTriggers(false, "phase read: NullReferenceException: x", null, null,
            new List<string>(), false, 8);
        Check(BadJsonReason(errFrame) == null, "error frame valid JSON", errFrame);

        Console.WriteLine("=== ui: activity semantics (task-002) ===");
        var activ = new List<BridgeMod.ButtonInfo>
        {
            Btn("A", "OK", "true", null),
            Btn("B", "OK", "unknown", null),
            Btn("C", "Other", "unknown", null),
        };
        string readActiv = BridgeMod.RenderButtonsReadOnly(activ, "", false, 64);
        Check(BadJsonReason(readActiv) == null, "read-only keeps unknown rows for diagnosis", readActiv);
        Check(readActiv.Contains("\"active\":\"unknown\""), "unknown activity is surfaced, not coerced", readActiv);

        string planUnknownOnly = BridgeMod.PlanUiInvocation(
            new List<BridgeMod.ButtonInfo> { Btn("B", "OK", "unknown", null) }, "OK", false, 64);
        Check(BridgeMod.PlanIsRefusal(planUnknownOnly) && planUnknownOnly.Contains("no button matches query"),
            "activity unknown is NOT clickable", planUnknownOnly);
        Check(BadJsonReason(planUnknownOnly) == null, "unknown-activity refusal valid JSON", planUnknownOnly);

        string planTrueUnique = BridgeMod.PlanUiInvocation(
            new List<BridgeMod.ButtonInfo> { Btn("A", "OK", "true", null) }, "OK", false, 64);
        Check(BridgeMod.PlanTarget(planTrueUnique) == 0, "complete snapshot + unique active true IS clickable", planTrueUnique);

        Console.WriteLine("=== ui: snapshot completeness (task-002) ===");
        string planTrunc = BridgeMod.PlanUiInvocation(
            new List<BridgeMod.ButtonInfo> { Btn("A", "OK", "true", null) }, "OK", true, 64);
        Check(BridgeMod.PlanIsRefusal(planTrunc) && planTrunc.Contains("snapshot_incomplete"),
            "truncated snapshot refuses named click even with a single match", planTrunc);
        Check(BadJsonReason(planTrunc) == null, "snapshot_incomplete frame valid JSON", planTrunc);

        string readTrunc = BridgeMod.PlanUiInvocation(
            new List<BridgeMod.ButtonInfo> { Btn("A", "OK", "true", null) }, "", true, 64);
        Check(BridgeMod.PlanIsReadOnly(readTrunc) && readTrunc.Contains("\"truncated\":true"),
            "truncated snapshot is still fine for read-only listing", readTrunc);

        Console.WriteLine("=== coordinates: no fake values (task-002) ===");
        Check(BridgeMod.RenderCoordinates("4", "8") == "(4,8)", "both components => (x,y)",
            BridgeMod.RenderCoordinates("4", "8"));
        Check(BridgeMod.RenderCoordinates("4", null) == null, "missing y => null (no \"(4,)\")",
            BridgeMod.RenderCoordinates("4", null));
        Check(BridgeMod.RenderCoordinates(null, "8") == null, "missing x => null (no \"(,8)\")",
            BridgeMod.RenderCoordinates(null, "8"));
        Check(BridgeMod.RenderCoordinates(null, null) == null, "both missing => null",
            BridgeMod.RenderCoordinates(null, null));

        var wlCoords = new Dictionary<string, string> { { "coordinates", BridgeMod.RenderCoordinates("4", "8") } };
        string trigCoords = BridgeMod.RenderTrigger(true, "CommandTrigger", wlCoords, true);
        Check(BadJsonReason(trigCoords) == null && trigCoords.Contains("\"coordinates\":\"(4,8)\""),
            "real coordinate pair reaches the frame", trigCoords);

        var wlPartial = new Dictionary<string, string>
        {
            { "playerId", "1" }, { "coordinates", BridgeMod.RenderCoordinates("4", null) }
        };
        string trigPartial = BridgeMod.RenderTrigger(true, "CommandTrigger", wlPartial, true);
        Check(!trigPartial.Contains("\"coordinates\""), "unreadable coordinate is omitted entirely", trigPartial);
        Check(BadJsonReason(trigPartial) == null, "partial-coordinate frame valid JSON", trigPartial);

        Console.WriteLine("=== escaping ===");
        string esc = BridgeMod.RenderButton(Btn("a\"b", "c\\d\ne", "true", null));
        Check(BadJsonReason(esc) == null, "quotes/backslashes/newlines escaped", esc);

        Console.WriteLine("=== decision rules: canAct (codex-dsh-004) ===");
        string why;
        Check(BridgeMod.CanActOf(null, "Started", 0, false, false, false, out why) == null,
            "unknown myTurn => canAct unknown", why);
        Check(BridgeMod.CanActOf(false, "Started", 0, false, false, false, out why) == "false",
            "not my turn => cannot act", why);
        Check(BridgeMod.CanActOf(true, "Started", 2, false, false, false, out why) == "false",
            "pending trigger blocks acting", why);
        Check(BridgeMod.CanActOf(true, "Started", 0, true, false, false, out why) == "false",
            "an open popup blocks acting", why);
        Check(BridgeMod.CanActOf(true, "Started", 0, false, true, false, out why) == "false",
            "a popup needing a choice blocks acting", why);
        Check(BridgeMod.CanActOf(true, "Started", 0, false, false, true, out why) == "false",
            "a busy pipeline blocks acting", why);
        Check(BridgeMod.CanActOf(true, "Ended", 0, false, false, false, out why) == "false",
            "Ended phase cannot act", why);
        Check(BridgeMod.CanActOf(true, "Lobby", 0, false, false, false, out why) == "false",
            "Lobby phase cannot act", why);
        Check(BridgeMod.CanActOf(true, "Unknown", 0, false, false, false, out why) == null,
            "Unknown phase stays unknown", why);
        Check(BridgeMod.CanActOf(true, "Started", null, false, false, false, out why) == null,
            "unreadable trigger count stays unknown", why);
        Check(BridgeMod.CanActOf(true, "Started", 0, null, false, false, out why) == null,
            "unreadable popup presence stays unknown", why);
        Check(BridgeMod.CanActOf(true, "Started", 0, false, null, false, out why) == null,
            "unreadable popup choice stays unknown", why);
        Check(BridgeMod.CanActOf(true, "Started", 0, false, false, null, out why) == null,
            "unreadable pipeline state stays unknown", why);
        Check(BridgeMod.CanActOf(true, "Started", 0, false, false, false, out why) == "true",
            "own turn, clear phase, nothing pending => true", why);

        Console.WriteLine("=== decision rules: phase + terminal (codex-dsh-006) ===");
        Check(BridgeMod.IsPlayingPhase("Started") == true, "Started is a playing phase", null);
        Check(BridgeMod.IsPlayingPhase("FinalTurn") == true, "FinalTurn is a playing phase", null);
        Check(BridgeMod.IsPlayingPhase("Ended") == false, "Ended is not playing", null);
        Check(BridgeMod.IsPlayingPhase("Lobby") == false, "Lobby is not playing", null);
        Check(BridgeMod.IsPlayingPhase("Unknown") == null, "Unknown phase is not guessed", null);
        Check(BridgeMod.IsPlayingPhase(null) == null, "missing phase is not guessed", null);

        Check(BridgeMod.GameTerminalOf(null, false) == null, "unreadable winner => gameTerminal unknown", null);
        Check(BridgeMod.GameTerminalOf(true, false) == null,
            "TryGetWinner=false does NOT prove non-terminal", null);
        Check(BridgeMod.GameTerminalOf(true, true) == true, "a definite winner => gameTerminal true", null);

        Check(BridgeMod.ControlledPlayerResult(true, true, "1", "1", false) == "win", "winner is me => win", null);
        Check(BridgeMod.ControlledPlayerResult(true, true, "2", "1", false) == "loss",
            "definite other winner => loss", null);
        Check(BridgeMod.ControlledPlayerResult(true, true, null, "1", false) == "unknown",
            "winner id unreadable => unknown, NEVER loss", null);
        Check(BridgeMod.ControlledPlayerResult(true, true, "2", null, false) == "unknown",
            "controlled id unreadable (identity unverified) => unknown", null);
        Check(BridgeMod.ControlledPlayerResult(true, false, null, "1", null) == "unknown",
            "no winner + unknown wiped => unknown, never 'ongoing'", null);
        Check(BridgeMod.ControlledPlayerResult(false, false, null, "1", true) == "loss",
            "a confirmed elimination is a per-player loss even with no global winner", null);

        Console.WriteLine("=== winner response assembly: no duplicate keys, no false verdicts ===");
        var winFrames = new List<string>();
        var wNoSig = new BridgeMod.WinnerInput();
        wNoSig.SignatureOk = false;
        wNoSig.Signature = "none";
        string wNoSigJson = BridgeMod.BuildWinnerResponse(wNoSig);
        winFrames.Add(wNoSigJson);
        Check(KeyCount(wNoSigJson, "ok") == 1, "unsupported-signature frame writes ok exactly once", wNoSigJson);

        string wWin = BridgeMod.BuildWinnerResponse(new BridgeMod.WinnerInput
        { SignatureOk = true, Signature = "Boolean TryGetWinner(out PlayerState)",
          WinnerReadable = true, WinnerFound = true, WinnerId = "1", ControlledId = "1", Wiped = false,
          Phase = "Ended", GameVersion = "123", GameType = "SinglePlayer",
          ControlledIdentityVerified = true, ControlledIdentitySource = "GameManager.LocalPlayer",
          RulesSummary = "TurnLimit=30,ScoreLimit=0,WinByCapital=True,WinByExtermination=False",
          SideEffectFree = false, SideEffectNote = "writes capitalCountCache" });
        winFrames.Add(wWin);
        Check(KeyCount(wWin, "ok") == 1, "winner frame writes ok exactly once", wWin);
        Check(wWin.Contains("\"controlledPlayerResult\":\"win\"") && wWin.Contains("\"gameTerminal\":true"),
            "definite own win reported", wWin);
        Check(wWin.Contains("\"sideEffectFree\":false")
              && wWin.Contains("\"planningUse\":\"audit_only_side_effects_confirmed\""),
            "a confirmed side effect keeps the command out of planning", wWin);
        Check(wWin.Contains("\"controlledIdentityVerified\":true")
              && wWin.Contains("\"controlledIdentitySource\":\"GameManager.LocalPlayer\""),
            "the identity basis is part of the record", wWin);

        string wUnverified = BridgeMod.BuildWinnerResponse(new BridgeMod.WinnerInput
        { SignatureOk = true, Signature = "Boolean TryGetWinner(out PlayerState)",
          WinnerReadable = true, WinnerFound = false, ControlledId = null, Wiped = null,
          Phase = "Started", ControlledIdentityVerified = false });
        winFrames.Add(wUnverified);
        Check(wUnverified.Contains("\"controlledPlayerResult\":\"unknown\"")
              && wUnverified.Contains("\"controlledIdentityVerified\":false"),
            "unverified identity cannot produce a result", wUnverified);

        Console.WriteLine("=== action confirmation: target association only ===");
        // Synthetic entities. Tokens (not coordinate-looking) keep the fixtures free of anything
        // that could be mistaken for a real map position.
        const string U_ME = "7:1:Warrior:T1:100:False:False;";
        const string U_TARGET = "9:2:Warrior:T2:100:False:False;";
        const string U_OTHER_MINE = "8:1:Warrior:T3:100:False:False;";
        const string I_MINE = "T1:1:City:2:1;";
        const string I_TARGET = "T2:2:City:1:0;";
        string fpBase = Fp("1", "10", "1", "0", "5", "Farming,Riding", "false",
            U_ME + U_TARGET + U_OTHER_MINE, I_MINE + I_TARGET);
        string basis;

        string fpMoved = Fp("1", "10", "1", "0", "5", "Farming,Riding", "false",
            "7:1:Warrior:T2:100:True:False;9:2:Warrior:T2:100:False:False;8:1:Warrior:T3:100:False:False;",
            I_MINE + I_TARGET);
        Check(BridgeMod.ConfirmAction("1", "move", "7", "T2", null, fpBase, fpMoved, out basis) == "confirmed",
            "move confirmed only when the NAMED unit reaches the NAMED tile", basis);

        string fpOtherMoved = Fp("1", "10", "1", "0", "5", "Farming,Riding", "false",
            U_ME + U_TARGET + "8:1:Warrior:T4:100:True:False;", I_MINE + I_TARGET);
        Check(BridgeMod.ConfirmAction("1", "move", "7", "T2", null, fpBase, fpOtherMoved, out basis) == "not_confirmed",
            "a different own unit moving does not confirm this move", basis);

        string fpReordered = Fp("1", "10", "1", "0", "5", "Farming,Riding", "false",
            U_TARGET + U_ME + U_OTHER_MINE, I_TARGET + I_MINE);
        Check(BridgeMod.ConfirmAction("1", "move", "7", "T2", null, fpBase, fpReordered, out basis) == "not_confirmed",
            "list reordering alone never confirms anything", basis);

        string fpUnchanged = Fp("1", "10", "1", "0", "5", "Farming,Riding", "true",
            U_ME + U_TARGET + U_OTHER_MINE, I_MINE + I_TARGET);
        Check(BridgeMod.ConfirmAction("1", "move", "7", "T2", null, fpBase, fpUnchanged, out basis) == "not_confirmed",
            "an unrelated popup change does not confirm a move", basis);

        string fpAttack = Fp("1", "10", "1", "0", "5", "Farming,Riding", "false",
            "7:1:Warrior:T1:100:False:True;9:2:Warrior:T2:70:False:False;8:1:Warrior:T3:100:False:False;",
            I_MINE + I_TARGET);
        Check(BridgeMod.ConfirmAction("1", "attack", "7", "T2", null, fpBase, fpAttack, out basis) == "confirmed",
            "attack confirmed by attacker flag AND target damage", basis);
        string fpAttackFlagOnly = Fp("1", "10", "1", "0", "5", "Farming,Riding", "false",
            "7:1:Warrior:T1:100:False:True;" + U_TARGET + U_OTHER_MINE, I_MINE + I_TARGET);
        Check(BridgeMod.ConfirmAction("1", "attack", "7", "T2", null, fpBase, fpAttackFlagOnly, out basis) == "not_confirmed",
            "a flipped flag without a target change is not confirmation", basis);

        string fpTech = Fp("1", "10", "1", "0", "5", "Farming,Riding,Sailing", "false",
            U_ME + U_TARGET + U_OTHER_MINE, I_MINE + I_TARGET);
        Check(BridgeMod.ConfirmAction("1", "research", null, null, "Sailing", fpBase, fpTech, out basis) == "confirmed",
            "research confirmed by the NAMED tech appearing", basis);
        string fpStarsOnly = Fp("1", "10", "1", "0", "2", "Farming,Riding", "false",
            U_ME + U_TARGET + U_OTHER_MINE, I_MINE + I_TARGET);
        Check(BridgeMod.ConfirmAction("1", "research", null, null, "Sailing", fpBase, fpStarsOnly, out basis) == "not_confirmed",
            "spending stars does not confirm research", basis);
        string fpOtherTech = Fp("1", "10", "1", "0", "5", "Archery,Farming,Riding", "false",
            U_ME + U_TARGET + U_OTHER_MINE, I_MINE + I_TARGET);
        Check(BridgeMod.ConfirmAction("1", "research", null, null, "Sailing", fpBase, fpOtherTech, out basis) == "not_confirmed",
            "a different tech being owned does not confirm this research", basis);

        string fpUnreadableTechs = Fp("1", "10", "1", "0", "5", null, "false",
            U_ME + U_TARGET + U_OTHER_MINE, I_MINE + I_TARGET);
        Check(BridgeMod.ConfirmAction("1", "research", null, null, "Sailing", fpUnreadableTechs, fpTech, out basis) == "unknown",
            "unreadable before tech list cannot prove newly owned research", basis);
        Check(BridgeMod.ConfirmAction("1", "research", null, null, "Sailing", fpBase, fpUnreadableTechs, out basis) == "unknown",
            "unreadable after tech list cannot prove research failure", basis);
        string fpEmptyTechs = Fp("1", "10", "1", "0", "5", "", "false",
            U_ME + U_TARGET + U_OTHER_MINE, I_MINE + I_TARGET);
        Check(BridgeMod.ConfirmAction("1", "research", null, null, "Sailing", fpEmptyTechs, fpTech, out basis) == "confirmed",
            "proved empty owned-tech list remains distinct from unreadable", basis);

        string fpTrained = Fp("1", "10", "1", "0", "5", "Farming,Riding", "false",
            "21:1:Archer:T1:100:False:False;" + U_TARGET + U_OTHER_MINE, I_MINE + I_TARGET);
        Check(BridgeMod.ConfirmAction("1", "train", null, "T1", "Archer", fpBase, fpTrained, out basis) == "confirmed",
            "train confirmed by the named kind appearing at the named city", basis);
        string fpTrainedElsewhere = Fp("1", "10", "1", "0", "5", "Farming,Riding", "false",
            "21:1:Archer:T3:100:False:False;" + U_ME + U_TARGET, I_MINE + I_TARGET);
        Check(BridgeMod.ConfirmAction("1", "train", null, "T1", "Archer", fpBase, fpTrainedElsewhere, out basis)
              == "not_confirmed",
            "a new unit in a different city does not confirm this train", basis);

        string fpBuilt = Fp("1", "10", "1", "0", "5", "Farming,Riding", "false",
            U_ME + U_TARGET + U_OTHER_MINE, "T1:1:Hunting:1:0;" + I_TARGET);
        Check(BridgeMod.ConfirmAction("1", "build", null, "T1", "Hunting", fpBase, fpBuilt, out basis) == "confirmed",
            "build confirmed by the NAMED tile's improvement changing", basis);
        string fpBuiltElsewhere = Fp("1", "10", "1", "0", "5", "Farming,Riding", "false",
            U_ME + U_TARGET + U_OTHER_MINE, I_MINE + "T2:2:City:2:1;");
        Check(BridgeMod.ConfirmAction("1", "build", null, "T1", "Hunting", fpBase, fpBuiltElsewhere, out basis)
              == "not_confirmed",
            "a different city changing does not confirm this build", basis);
        string fpCaptured = Fp("1", "10", "1", "0", "5", "Farming,Riding", "false",
            U_ME + U_TARGET + U_OTHER_MINE, I_MINE + "T2:1:City:1:0;");
        Check(BridgeMod.ConfirmAction("1", "capture", "7", "T2", null, fpBase, fpCaptured, out basis) == "confirmed",
            "capture confirmed by the NAMED tile changing owner to me", basis);

        string fpTurn = Fp("1", "11", "1", "0", "5", "Farming,Riding", "false",
            U_ME + U_TARGET + U_OTHER_MINE, I_MINE + I_TARGET);
        Check(BridgeMod.ConfirmAction("1", "end_turn", null, null, null, fpBase, fpTurn, out basis) == "confirmed",
            "end_turn confirmed by the turn number", basis);
        string fpActing = Fp("1", "10", "2", "0", "5", "Farming,Riding", "false",
            U_ME + U_TARGET + U_OTHER_MINE, I_MINE + I_TARGET);
        Check(BridgeMod.ConfirmAction("1", "end_turn", null, null, null, fpBase, fpActing, out basis) == "confirmed",
            "end_turn confirmed by the acting player switching (turn may not change)", basis);

        Check(BridgeMod.ConfirmAction("1", "city_reward", null, "T1", "Workshop", fpBase, fpUnchanged, out basis)
              == "unknown",
            "a reward with no readable effect evidence stays unknown", basis);
        // A city change plus a popup close is LIMITED evidence only: the specific reward is
        // unreadable, so even that combination must not be reported as completed.
        Check(BridgeMod.ConfirmAction("1", "city_reward", null, "T1", "Workshop", fpBase, fpBuilt, out basis)
              == "unknown",
            "a city change plus popup close still cannot confirm WHICH reward applied", basis);
        Check(BridgeMod.ConfirmAction("1", "promote", "7", "T1", null, fpBase, fpMoved, out basis) == "unknown",
            "an action without a predicate stays unknown", basis);
        Check(BridgeMod.ConfirmAction("1", "move", "7", "T2", null, null, fpMoved, out basis) == "unknown",
            "a missing fingerprint stays unknown", basis);

        Console.WriteLine("=== fingerprint helpers + coarse error codes ===");
        Check(BridgeMod.FpUnitRecord(U_ME + U_TARGET, "9") != null, "unit record looked up by id", null);
        Check(BridgeMod.FpUnitRecord(U_ME + U_TARGET, "404") == null, "unknown unit id yields null", null);
        Check(BridgeMod.FpUnitAt(U_ME + U_TARGET, "T2") != null, "unit record looked up by tile", null);
        Check(BridgeMod.FpFieldAt(U_ME.TrimEnd(';'), 2) == "Warrior", "unit field index 2 is the type", null);
        Check(BridgeMod.FpImpRecord(I_MINE + I_TARGET, "T2") != null, "improvement looked up by tile", null);
        Check(BridgeMod.FpHasTech("Farming,Riding", "Riding"), "tech membership found", null);
        Check(!BridgeMod.FpHasTech("Farming,Riding", "Sailing"), "absent tech is not a member", null);
        Check(BridgeMod.FpField(fpBase, "acting") == "1", "acting player is readable from the fingerprint", null);

        Check(BridgeMod.CoarseErrorCode("build", "NOT_UNLOCKED") == "BUILD_REFUSED",
            "build refusal collapses tech-vs-resource into one code", null);
        Check(BridgeMod.CoarseErrorCode("build", "CANT_AFFORD") == "BUILD_REFUSED",
            "affordability is part of the same coarse refusal", null);
        Check(BridgeMod.CoarseErrorCode("build", "TILE_OCCUPIED") == "TILE_OCCUPIED",
            "occupancy is not hidden behind the coarse code", null);
        Check(BridgeMod.CoarseErrorCode("move", "NOT_UNLOCKED") == "NOT_UNLOCKED",
            "non-build actions keep their precise code", null);
        Check(BridgeMod.CoarseErrorCode("upgrade", "CANT_AFFORD") == BridgeMod.CoarseErrorCode("upgrade_unit", "CANT_AFFORD"),
            "unit upgrade aliases share the same refusal semantics", null);
        Check(BridgeMod.ErrorCodeOf("with error Pending command trigger exists") == "COMMAND_TRIGGER_EXISTS",
            "trigger error maps to a stable code", null);
        Check(BridgeMod.ErrorCodeOf("something nobody predicted") == "UNMAPPED",
            "unknown message is UNMAPPED, not guessed", null);

        Console.WriteLine("=== action response assembly: accepted != completed ===");
        var actFrames = new List<string>();
        string aPending = BridgeMod.BuildActionResponse(new BridgeMod.ActionEcho
        { ActionName = "move", ActionLabel = "MoveCommand", Via = "ExecuteCommand(2)",
          Manager = "ClientActionManager", Accepted = true, AcceptedRaw = "True", ValidationError = "",
          Before = fpBase, After = fpOtherMoved, ControlledId = "1",
          TargetUnitId = "7", TargetCoord = "T2", PendingMineAfter = 0, PopupAfter = false });
        actFrames.Add(aPending);
        Check(KeyCount(aPending, "ok") == 1, "action frame writes ok exactly once", aPending);
        Check(aPending.Contains("\"verdict\":\"accepted_pending_confirmation\""),
            "accepted but unassociated change => accepted_pending_confirmation", aPending);
        Check(!aPending.Contains("\"actionCompleted\":true"),
            "accepted-but-unconfirmed is never marked completed", aPending);
        Check(aPending.Contains("\"accepted\":true"), "accepted is a real JSON boolean", aPending);
        Check(aPending.Contains("\"targetUnitId\":\"7\"") && aPending.Contains("\"targetCoord\":\"T2\""),
            "the response records what the command targeted", aPending);

        string aConfirmed = BridgeMod.BuildActionResponse(new BridgeMod.ActionEcho
        { ActionName = "move", ActionLabel = "MoveCommand", Via = "ExecuteCommand(2)",
          Manager = "ClientActionManager", Accepted = true, AcceptedRaw = "True", ValidationError = "",
          Before = fpBase, After = fpMoved, ControlledId = "1",
          TargetUnitId = "7", TargetCoord = "T2", PendingMineAfter = 0, PopupAfter = false });
        actFrames.Add(aConfirmed);
        Check(aConfirmed.Contains("\"verdict\":\"accepted_pending_confirmation\"") && aConfirmed.Contains("\"actionCompleted\":false"),
            "an immediate destination snapshot still awaits the original native completion ticket", aConfirmed);

        string aTrigger = BridgeMod.BuildActionResponse(new BridgeMod.ActionEcho
        { ActionName = "build", ActionLabel = "BuildCommand", Via = "ExecuteCommand(2)",
          Manager = "ClientActionManager", Accepted = true, AcceptedRaw = "True", ValidationError = "",
          Before = fpBase, After = fpBuilt, ControlledId = "1",
          TargetCoord = "T1", TargetArg = "Hunting", PendingMineAfter = 1, PopupAfter = true });
        actFrames.Add(aTrigger);
        Check(aTrigger.Contains("\"verdict\":\"needs_resolution\""),
            "an open trigger/popup outranks completion", aTrigger);

        string aRejected = BridgeMod.BuildActionResponse(new BridgeMod.ActionEcho
        { ActionName = "build", ActionLabel = "BuildCommand", Via = "ExecuteCommand(2)",
          Manager = "ClientActionManager", Accepted = false, AcceptedRaw = "False",
          ValidationError = "with error Not unlocked", Before = fpBase, After = fpBase,
          ControlledId = "1", TargetCoord = "T1", TargetArg = "Hunting",
          PendingMineAfter = 0, PopupAfter = false });
        actFrames.Add(aRejected);
        Check(aRejected.Contains("\"verdict\":\"rejected\"")
              && aRejected.Contains("\"errorCode\":\"BUILD_REFUSED\""),
            "a refused build reports one coarse code, not tech-vs-resource", aRejected);
        Check(aRejected.Contains("\"accepted\":false"), "rejected accepted flag is a real boolean false", aRejected);

        string aUnknown = BridgeMod.BuildActionResponse(new BridgeMod.ActionEcho
        { ActionName = "attack", ActionLabel = "AttackCommand", Via = "ExecuteCommand(2)",
          Manager = "ClientActionManager", Accepted = null, AcceptedRaw = "null", ValidationError = "",
          Before = fpBase, After = fpAttack, ControlledId = "1", TargetUnitId = "7", TargetCoord = "T2",
          PendingMineAfter = null, PopupAfter = null });
        actFrames.Add(aUnknown);
        Check(aUnknown.Contains("\"verdict\":\"unknown\""), "unreadable follow-up state => unknown", aUnknown);

        string aEndTurn = BridgeMod.BuildActionResponse(new BridgeMod.ActionEcho
        { ActionName = "end_turn", ActionLabel = "EndTurnCommand(player=1)", Via = "ExecuteCommand(2)",
          Manager = "ClientActionManager", Accepted = true, AcceptedRaw = "True", ValidationError = "",
          Before = fpBase, After = fpActing, ControlledId = "1", PendingMineAfter = 0, PopupAfter = false });
        actFrames.Add(aEndTurn);
        Check(aEndTurn.Contains("\"controlledPlayerId\":\"1\""),
            "end_turn still reports the controlled player it acted for", aEndTurn);
        Check(aEndTurn.Contains("\"verdict\":\"accepted_pending_confirmation\""),
            "end_turn awaits the original ticket despite an immediate acting-player switch", aEndTurn);

        FilterEntryTests(winFrames);
        BindingTests();
        EnumBindingTests();

        Console.WriteLine("=== visibility filters are constant, never existence-conditioned ===");
        string vf1 = BridgeMod.VisibilityFilterStatusJson();
        string vf2 = BridgeMod.VisibilityFilterStatusJson();
        Check(vf1 == vf2, "filter status is a constant", vf1);
        Check(vf1.Contains("\"unit\":{") && vf1.Contains("\"resource\":{")
              && vf1.Contains("\"snapshotStatus\""),
            "filter status is per-accessor with a snapshot verdict, not one OR-ed flag", vf1);
        winFrames.Add(vf1);
        Check(BridgeMod.FogSampleJson(false, false, "T9", "7", true) == null,
            "an unexplored tile can never be sampled", null);
        string visSample = BridgeMod.FogSampleJson(true, true, "T4", "1", false);
        Check(BadJsonReason(visSample) == null && visSample.Contains("\"c\":\"T4\""),
            "an explored tile is sampled normally", visSample);
        string policyA = BridgeMod.HiddenEntityPolicyJson();
        Check(policyA == BridgeMod.HiddenEntityPolicyJson(), "hidden-entity policy marker is constant", policyA);
        Check(policyA.Contains("\"countsReported\":false") && policyA.Contains("\"existenceReported\":false"),
            "policy marker reports neither counts nor existence", policyA);
        winFrames.Add(policyA);

        Console.WriteLine("=== unit action fields are real booleans ===");
        var uoFrag = new JsonObj();
        BridgeMod.UnitActionFields(uoFrag, false, false, true, false, "3", null);
        string unitJson = uoFrag.Close();
        Check(BadJsonReason(unitJson) == null, "unit action fields are valid JSON", unitJson);
        Check(unitJson.Contains("\"moved\":false") && unitJson.Contains("\"attacked\":false")
              && unitJson.Contains("\"canMove\":true") && unitJson.Contains("\"canAttack\":false")
              && unitJson.Contains("\"maxHealth\":null") && unitJson.Contains("\"promotionLevel\":\"3\""),
            "unit action fields are true booleans, nulls stay null", unitJson);
        winFrames.Add(unitJson);

        Console.WriteLine("=== adapter filter: the real isolation point ===");
        Check(BridgeMod.AdapterDecision("world", null, null, null, null) == "allow", "world allowed", null);
        Check(BridgeMod.AdapterDecision("action", null, null, "move", null) == "allow",
            "a named action is allowed", null);
        Check(BridgeMod.AdapterDecision("world", null, "1", null, null)
              == "deny:audit_override_not_accepted_by_adapter",
            "the adapter refuses the audit override instead of honouring it", null);
        Check(BridgeMod.AdapterDecision("players", null, null, null, null) == "deny:not_in_adapter_allowlist"
              && BridgeMod.AdapterDecision("get", null, null, null, null) == "deny:not_in_adapter_allowlist"
              && BridgeMod.AdapterDecision("call", null, null, null, null) == "deny:not_in_adapter_allowlist"
              && BridgeMod.AdapterDecision("set", null, null, null, null) == "deny:not_in_adapter_allowlist"
              && BridgeMod.AdapterDecision("fog", null, null, null, null) == "deny:not_in_adapter_allowlist",
            "raw dumps and generic reflection are never forwarded", null);
        Check(BridgeMod.AdapterDecision("popup", "components", null, null, null)
              == "deny:popup_subop_not_allowed:components"
              && BridgeMod.AdapterDecision("popup", "tree", null, null, null)
              == "deny:popup_subop_not_allowed:tree"
              && BridgeMod.AdapterDecision("popup", "native", null, null, null)
              == "deny:popup_subop_not_allowed:native",
            "generic object dumps inside popup are refused, not grandfathered in", null);
        Check(BridgeMod.AdapterDecision("popup", "hide", null, null, null)
              == "deny:popup_subop_not_allowed:hide"
              && BridgeMod.AdapterDecision("popup", "closeall", null, null, null)
              == "deny:popup_subop_not_allowed:closeall"
              && BridgeMod.AdapterDecision("popup", "back", null, null, null)
              == "deny:popup_subop_not_allowed:back",
            "forced popup dismissal is refused", null);
        Check(BridgeMod.AdapterDecision("popup", "choose", null, null, "Workshop") == "allow"
              && BridgeMod.AdapterDecision("popup", "choose", null, null, null)
              == "deny:popup_choose_requires_explicit_choice",
            "a reward choice must be explicit", null);
        Check(BridgeMod.AdapterDecision("action", null, null, "city_reward", null)
              == "deny:reward_action_requires_explicit_choice",
            "the reward action requires the agent to name the reward", null);
        Check(!BridgeMod.IsDecisionCommand("winner") && !BridgeMod.IsDecisionCommand("players"),
            "winner and raw dumps stay audit-only", null);

        Console.WriteLine("=== privacy: hidden aggregates stay out of the decision response ===");
        foreach (var forbidden in BridgeMod.ForbiddenDecisionKeys)
        {
            Check(!BridgeMod.IsDecisionWorldKey(forbidden),
                "forbidden aggregate is not a decision key: " + forbidden, null);
        }
        string srcPath = FindSourceFile(args.Length > 0 ? args[0] : null);
        Check(srcPath != null, "BridgeMod.cs located for the emission scan", srcPath);
        if (srcPath != null)
        {
            string all = File.ReadAllText(srcPath);
            string body = ExtractBody(all, "private string WorldDump()");
            Check(!string.IsNullOrEmpty(body), "WorldDump body extracted", null);
            var emitted = TopLevelEmittedKeys(body);
            // WorldDump delegates these top-level fields to the shared owned-tech
            // formatter. Inspect its real output so extraction does not silently
            // drop whitelist coverage when the production method is split.
            var ownedFields = new JsonObj();
            BridgeMod.AppendOwnedTechFields(ownedFields, null);
            var ownedKeys = new System.Web.Script.Serialization.JavaScriptSerializer()
                .Deserialize<Dictionary<string, object>>(ownedFields.Close());
            emitted.AddRange(ownedKeys.Keys);
            Check(emitted.Count > 10, "emission scan found world keys (" + emitted.Count + ")", null);
            foreach (var k in emitted)
            {
                Check(BridgeMod.IsDecisionWorldKey(k), "world emits only whitelisted keys: " + k, null);
            }
            foreach (var forbidden in BridgeMod.ForbiddenDecisionKeys)
            {
                Check(!emitted.Contains(forbidden), "world does not emit " + forbidden, null);
            }
            string worldCode = StripComments(body);
            Check(!worldCode.Contains("hiddenUnitCount") && !worldCode.Contains("hiddenCount"),
                "world no longer even computes a hidden-entity count", null);
            Check(!worldCode.Contains("TotalPendingTriggers"),
                "world never reads the global (all players') trigger count", null);
            Check(!worldCode.Contains("MemberWithAccessor(tile, \"unit\")"),
                "world never reads tile.unit directly (fail-closed filter only)", null);
            Check(worldCode.Contains("TileUnitVisible") && worldCode.Contains("TileResourceVisible"),
                "world resolves units and resources through the game's own accessors", null);
            Check(worldCode.Contains("controlledIdentityVerified"),
                "world states whether the controlled identity was verified", null);

            string unitFn = StripComments(ExtractBody(all, "internal static object TileUnitVisible("));
            Check(!string.IsNullOrEmpty(unitFn), "TileUnitVisible body extracted", null);
            Check(!unitFn.Contains("MemberWithAccessor(tile, \"unit\")")
                  && !unitFn.Contains("MemberWithAccessor(tile, \"Unit\")"),
                "TileUnitVisible has no raw-property fallback", null);
            string signatureCode = unitFn;
            if (unitFn.Contains("FindTileVisibilityMethod(tile.GetType(), true)"))
            {
                string metadataPath = Path.Combine(Path.GetDirectoryName(srcPath), "BridgeVisibilityMetadataRuntime.cs");
                signatureCode = File.Exists(metadataPath) ? StripComments(ExtractBody(File.ReadAllText(metadataPath),
                    "private static MethodInfo BuildTileVisibilityMethod(")) : "";
            }
            Check(signatureCode.Contains("ParameterType != typeof(byte)") && signatureCode.Contains("ParameterType != typeof(bool)")
                && signatureCode.Contains("ParameterType.Name.IndexOf(\"GameState\""),
                "TileUnitVisible validates parameter types, not just name+arity", null);
            string resFn = StripComments(ExtractBody(all, "internal static object TileResourceVisible("));
            Check(resFn != null && !resFn.Contains("MemberWithAccessor(tile, \"resource\")"),
                "TileResourceVisible has no raw-property fallback", null);

            string viewBody = StripComments(ExtractBody(all, "private ControlledIdentity ResolveControlledIdentity("));
            Check(!string.IsNullOrEmpty(viewBody) && viewBody.Contains("GameManager.LocalPlayer"),
                "identity comes from the game's own LocalPlayer", null);
            Check(!viewBody.Contains("IsHuman"),
                "no some-human fallback remains in the identity resolution", null);
            string execCode = StripComments(ExtractBody(all, "private string ExecuteGameAction("));
            Check(execCode.Contains("ActionFingerprint(gs, identity)"),
                "both fingerprints use the single frozen identity", null);
            Check(!execCode.Contains("ActionFingerprint(gs, Resolve"),
                "the after-fingerprint never re-resolves the identity", null);
            // The episode id must be a local random token: no seed read, no seed field.
            string episodeBody = StripComments(ExtractBody(all, "private string EpisodeId(object gs)"));
            Check(!string.IsNullOrEmpty(episodeBody) && episodeBody.Contains("Guid.NewGuid"),
                "episode id is locally generated", null);
            Check(episodeBody != null && !episodeBody.Contains("Seed"),
                "episode id never reads the map seed", null);
            string worldAll = StripComments(all);
            Check(!worldAll.Contains("getProperty(\"Seed\")") && !worldAll.Contains("Property(\"Seed\")"),
                "no code path reads the map seed into the response", null);

            string fogBody = StripComments(ExtractBody(all, "private string FogReport()"));
            Check(fogBody.Contains("controlledIdentityVerified"),
                "fog withholds the audit view when the identity is unverified", null);
            Check(fogBody.Contains("HiddenEntityPolicyJson") && !fogBody.Contains("unitsInFogWithheld")
                  && !fogBody.Contains("unitsNotExplored"),
                "FogReport keeps a constant policy marker and no hidden-entity marker", null);
        }
        // Emit every generated frame so an independent JSON parser (python json) can validate
        // the actual generator output, not hand-written fixtures.
        var dump = new List<string>();
        dump.AddRange(winFrames);
        dump.AddRange(actFrames);
        dump.Add(readNone); dump.Add(readTwo); dump.Add(planExact); dump.Add(planNone); dump.Add(planAmbig);
        dump.Add(okInvoke); dump.Add(errInvoke); dump.Add(one); dump.Add(noneReadable); dump.Add(unsupportedType);
        dump.Add(emptyList); dump.Add(many); dump.Add(errFrame); dump.Add(esc);
        dump.Add(readActiv); dump.Add(planUnknownOnly); dump.Add(planTrueUnique);
        dump.Add(planTrunc); dump.Add(readTrunc); dump.Add(trigCoords); dump.Add(trigPartial);
        dump.Add(visSample);
        var dumpPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "frames.jsonl");
        File.WriteAllText(dumpPath, string.Join("\n", dump) + "\n", new UTF8Encoding(false));
        Console.WriteLine("frames written: " + dumpPath + " (" + dump.Count + " frames)");

        // The contract frames are also written separately so an independent checker can parse
        // them with a duplicate-key-rejecting object_pairs_hook and assert JSON value TYPES
        // (a string "False" would pass a naive parse and then behave as truthy in Python).
        var contractPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "frames-006.jsonl");
        var contract = new List<string>();
        contract.AddRange(winFrames);
        contract.AddRange(actFrames);
        File.WriteAllText(contractPath, string.Join("\n", contract) + "\n", new UTF8Encoding(false));
        Console.WriteLine("contract frames written: " + contractPath + " (" + contract.Count + " frames)");

        Console.WriteLine("passed=" + _pass + " failed=" + _fail);
        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    /// <summary>
    /// Fake tiles for the filter entry points. They exist so the fail-closed rule is tested on the
    /// ACTUAL production accessors with a raw entity present, rather than by string-matching the
    /// source: no fallback may ever hand the raw entity to a caller.
    /// </summary>
    private sealed class FakeGameState { }

    private sealed class TileNoFilter
    {
        public object unit = "RAW-UNIT-SECRET";              // present, must never be disclosed
    }

    private sealed class TileThrows
    {
        public object unit = "RAW-UNIT-SECRET";
        public object GetUnit(FakeGameState gs, byte player, bool includeHidden)
        { throw new InvalidOperationException("filter blew up"); }
    }

    private sealed class TileWrongParamTypes
    {
        public object unit = "RAW-UNIT-SECRET";
        public object GetUnit(string gs, int player, bool includeHidden) { return unit; }
    }

    private sealed class TileFilteredAway
    {
        public object unit = "RAW-UNIT-SECRET";
        public object GetUnit(FakeGameState gs, byte player, bool includeHidden) { return null; }
    }

    private sealed class TileRevealsUnit
    {
        public object unit = "RAW-UNIT-SECRET";
        public object visible = "FILTERED-UNIT";
        public object GetUnit(FakeGameState gs, byte player, bool includeHidden) { return visible; }
    }

    private sealed class ResNoFilter
    {
        public object resource = "RAW-RES-SECRET";
    }

    private sealed class ResFilteredAway
    {
        public object resource = "RAW-RES-SECRET";
        public object GetResource(FakeGameState gs, byte player) { return null; }
    }

    private sealed class ResReveals
    {
        public object resource = "RAW-RES-SECRET";
        public object visible = "FILTERED-RES";
        public object GetResource(FakeGameState gs, byte player) { return visible; }
    }

    private static void FilterEntryTests(List<string> frames)
    {
        var gs = new FakeGameState();
        bool filtered;
        object got;

        Console.WriteLine("=== filter entry points: fail closed on the REAL accessors (codex-dsh-007) ===");
        got = BridgeMod.TileUnitVisible(new TileNoFilter(), gs, 1, out filtered);
        Check(got == null && !filtered, "no GetUnit on the tile => raw unit withheld", null);
        got = BridgeMod.TileUnitVisible(new TileThrows(), gs, 1, out filtered);
        Check(got == null && !filtered, "GetUnit throwing => raw unit withheld", null);
        got = BridgeMod.TileUnitVisible(new TileWrongParamTypes(), gs, 1, out filtered);
        Check(got == null && !filtered, "wrong parameter types => overload not used, raw withheld", null);
        got = BridgeMod.TileUnitVisible(new TileFilteredAway(), gs, 1, out filtered);
        Check(got == null && filtered, "the filter ran and hid the unit => nothing returned", null);
        got = BridgeMod.TileUnitVisible(new TileRevealsUnit(), gs, 1, out filtered);
        Check(got != null && filtered && (string)got == "FILTERED-UNIT",
            "the filter ran and revealed a unit => that unit is returned", null);

        got = BridgeMod.TileResourceVisible(new ResNoFilter(), gs, 1, out filtered);
        Check(got == null && !filtered, "no GetResource => raw resource withheld", null);
        got = BridgeMod.TileResourceVisible(new ResFilteredAway(), gs, 1, out filtered);
        Check(got == null && filtered, "the resource filter hid it => nothing returned", null);
        got = BridgeMod.TileResourceVisible(new ResReveals(), gs, 1, out filtered);
        Check(got != null && filtered && (string)got == "FILTERED-RES",
            "the resource filter revealed it => that resource is returned", null);

        Console.WriteLine("=== filter completeness is a sweep verdict, never OR-ed ===");
        BridgeMod.ResetFilterStats();
        Check(BridgeMod.FilterSnapshotStatus() == "not_attempted",
            "no calls at all => not_attempted", null);
        BridgeMod.TileUnitVisible(new TileNoFilter(), gs, 1, out filtered);
        BridgeMod.TileResourceVisible(new ResNoFilter(), gs, 1, out filtered);
        Check(BridgeMod.FilterSnapshotStatus() == "signature_unavailable",
            "no matching overload => signature_unavailable, not complete", null);

        BridgeMod.ResetFilterStats();
        BridgeMod.TileUnitVisible(new TileFilteredAway(), gs, 1, out filtered);
        BridgeMod.TileUnitVisible(new TileRevealsUnit(), gs, 1, out filtered);
        BridgeMod.TileResourceVisible(new ResFilteredAway(), gs, 1, out filtered);
        Check(BridgeMod.FilterSnapshotStatus() == "complete", "every call succeeded => complete", null);

        BridgeMod.ResetFilterStats();
        BridgeMod.TileUnitVisible(new TileFilteredAway(), gs, 1, out filtered);
        BridgeMod.TileUnitVisible(new TileThrows(), gs, 1, out filtered);      // exactly one failure
        BridgeMod.TileUnitVisible(new TileRevealsUnit(), gs, 1, out filtered);
        BridgeMod.TileResourceVisible(new ResFilteredAway(), gs, 1, out filtered);
        Check(BridgeMod.FilterSnapshotStatus() == "failed",
            "a single failed tile makes the whole sweep failed, not complete", null);
        string stat = BridgeMod.VisibilityFilterStatusJson();
        Check(stat.Contains("\"failed\":1") && stat.Contains("\"succeeded\":2"),
            "the failure is counted, not hidden by the successes", stat);
        frames.Add(stat);
    }

    // --- production binding regressions (codex-dsh-010): the new instance/method binding logic ---
    private sealed class PlayerStateLike { }
    private static class BindingNamespaceA { public sealed class PlayerState { } }
    private static class BindingNamespaceB { public sealed class PlayerState { } }
    private sealed class BindingOne
    {
        public bool X(PlayerStateLike a, GameStateLike2 b) { return true; }
    }
    private sealed class GameStateLike2 { }
    private sealed class BindingAmbiguous
    {
        public bool X(BindingNamespaceA.PlayerState a) { return true; }
        public bool X(BindingNamespaceB.PlayerState a) { return true; }
    }
    private sealed class BindingNone { public bool Y(PlayerStateLike a) { return true; } }

    private static void BindingTests()
    {
        Console.WriteLine("=== instance/method binding: unique or unknown, never a fuzzy hit ===");
        string seen;
        Check(BridgeMod.FindInstanceMethodUnique(typeof(BindingOne), "X",
                  new[] { "PlayerStateLike", "GameStateLike2" }, "Boolean", out seen) != null,
              "a unique exact-name/parameter/return match binds", seen);
        Check(BridgeMod.FindInstanceMethodUnique(typeof(BindingAmbiguous), "X",
                  new[] { "PlayerState" }, null, out seen) == null,
              "two candidates matching the same signature resolve to UNKNOWN, not first-hit", seen);
        Check(BridgeMod.FindInstanceMethodUnique(typeof(BindingNone), "X",
                  new[] { "PlayerState" }, null, out seen) == null,
              "an absent method resolves to UNKNOWN", seen);
        Check(BridgeMod.FindInstanceMethodUnique(typeof(BindingOne), "X",
                  new[] { "PlayerStateLike", "GameStateLike2" }, "Int32", out seen) == null,
              "a return-type mismatch resolves to UNKNOWN", seen);
        Check(BridgeMod.GameLogicDataOf(null) == null, "no GameState yields no data instance", null);
        Check(BridgeMod.InvokeBool(null, null, null) == null,
              "a missing/invocation-failed probe is UNKNOWN, never false", null);
    }
    // --- codex-dsh-014: expected-enum identity is fail-closed, and train costs must be real ints ---
    private enum FakeTech { Alpha, Beta }
    private enum FakeUnit { Warrior }

    private static void EnumBindingTests()
    {
        Console.WriteLine("=== expected-enum identity fails closed (codex-dsh-014) ===");
        string why;
        Check(BridgeMod.EnumNameOfExpected(FakeTech.Alpha, null, out why) == null,
            "expected=null is unknown, never 'some enum'", why);
        Check(BridgeMod.EnumNameOfExpected(FakeTech.Alpha, typeof(FakeUnit), out why) == null,
            "an enum from a different family is refused", why);
        Check(BridgeMod.EnumNameOfExpected(FakeTech.Alpha, typeof(FakeTech), out why) == "Alpha",
            "the matching enum resolves to its stable name", why);
        Check(BridgeMod.EnumNameOfExpected(3, typeof(FakeTech), out why) == null,
            "a raw number is not an enum name", why);
        Check(BridgeMod.EnumNameOfExpected("Alpha", typeof(FakeTech), out why) == null,
            "a string is not an enum name", why);
    }
    /// <summary>Strip // line comments so a source scan cannot be satisfied or fooled by prose.</summary>
    private static string StripComments(string text)
    {
        if (text == null) { return null; }
        var lines = text.Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            int at = line.IndexOf("//", StringComparison.Ordinal);
            sb.Append(at >= 0 ? line.Substring(0, at) : line).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>How many times a top-level-looking key appears: catches duplicate keys in C# too.</summary>
    private static int KeyCount(string json, string key)
    {
        if (json == null || key == null) { return 0; }
        int n = 0, at = 0;
        string needle = "\"" + key + "\":";
        while ((at = json.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
        return n;
    }

    /// <summary>Build a v2 fingerprint through the production helper.</summary>
    private static string Fp(string player, string turn, string acting, string pend, string stars,
        string techs, string popup, string units, string imps)
    {
        return BridgeMod.Fingerprint(player, turn, acting, pend, stars, techs, popup, units, imps, null, null, null);
    }

    /// <summary>
    /// Locate src/BridgeMod.cs by walking up from the test executable. The privacy regression
    /// scans the compiled candidate's source. An explicit path pins snapshot-based
    /// verification; legacy invocations can still discover source beside the build.
    /// </summary>
    private static string FindSourceFile(string explicitPath)
    {
        if (explicitPath != null) { return File.Exists(explicitPath) ? explicitPath : null; }
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++)
        {
            var cand = Path.Combine(dir.FullName, "src", "BridgeMod.cs");
            if (File.Exists(cand)) { return cand; }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Body of a method, found by signature then brace-matched. Null when absent.</summary>
    private static string ExtractBody(string text, string signature)
    {
        int at = text.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) { return null; }
        int open = text.IndexOf('{', at);
        if (open < 0) { return null; }
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') { depth++; }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) { return text.Substring(open, i - open + 1); }
            }
        }
        return null;
    }

    /// <summary>
    /// Keys emitted on the top-level JSON object of a method body. Nested builders (uo/co/ro/to)
    /// carry their own keys and are deliberately not part of the decision-key whitelist.
    /// </summary>
    private static List<string> TopLevelEmittedKeys(string body)
    {
        var keys = new List<string>();
        var re = new System.Text.RegularExpressions.Regex(
            "\\bo\\.(?:Raw|Str|Bool|NullOrStr|Nested)\\(\"([A-Za-z][A-Za-z0-9_]*)\"");
        foreach (System.Text.RegularExpressions.Match m in re.Matches(body))
        {
            keys.Add(m.Groups[1].Value);
        }
        return keys;
    }
}
