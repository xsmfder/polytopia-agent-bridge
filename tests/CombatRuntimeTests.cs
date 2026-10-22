// Pure production journal/settlement regression tests; no native match is claimed.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;

internal static class CombatRuntimeTests
{
    private static Type host, frame, unit, cause;
    private static object instance;
    private static int passed;
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static JavaScriptSerializer json = new JavaScriptSerializer();
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception("FAIL " + label); passed++; Console.WriteLine("PASS " + label); }
    private static void Set(object x, string name, object value) { x.GetType().GetField(name, F).SetValue(x, value); }
    private static object Get(object x, string name) { return x.GetType().GetField(name, F).GetValue(x); }
    private static object Frame(string episode)
    { var x = Activator.CreateInstance(frame, true); Set(x, "Episode", episode); Set(x, "Me", "1"); Set(x, "Turn", "4"); Set(x, "Acting", "1"); Set(x, "Phase", "Started"); Set(x, "Complete", true); return x; }
    private static object Unit(string id, string owner, string coord, int hp, string passenger)
    {
        var x = Activator.CreateInstance(unit, true); Set(x, "Id", id); Set(x, "Owner", owner); Set(x, "Coord", coord);
        Set(x, "Type", passenger == null ? "Warrior" : "Transportship"); Set(x, "Health", hp); Set(x, "PassengerId", passenger); return x;
    }
    private static void Add(object f, object u) { ((IDictionary)Get(f, "Units")).Add(Get(u, "Id"), u); }
    private static List<string> Diff(object a, object b, object c)
    { return (List<string>)host.GetMethod("CRDiff", F).Invoke(null, new[] { a, b, c }); }
    private static void Observe(object f)
    { host.GetMethod("CRObserve", F).Invoke(instance, new object[] { f, null }); }
    private static Dictionary<string, object> Events(long after, int limit)
    { return json.Deserialize<Dictionary<string, object>>((string)host.GetMethod("CombatEventsJson", F).Invoke(instance, new object[] { null, after, limit })); }
    private static bool? Resolution(params object[] args)
    { return (bool?)host.GetMethod("CRResolutionPolicy", F).Invoke(null, args); }
    private static object Pipeline(bool ended, bool observed)
    {
        var p = Activator.CreateInstance(host.GetNestedType("CombatPipelineView", F), true);
        foreach (var name in new[] { "PropertyProcessing", "RawProcessing", "Recapping", "Simulating", "Paused", "Pausing", "Holding", "Reacting", "CurrentReaction", "Queued", "Popup" }) Set(p, name, false);
        Set(p, "ReactionCount", 0); Set(p, "PendingMine", 0); Set(p, "NativeEnded", ended); Set(p, "TerminalObserved", observed);
        Set(p, "Paused", true); Set(p, "Holding", true); Set(p, "LocalTurn", false); Set(p, "StableFrames", 2); Set(p, "QuietSeconds", 0.5);
        Set(p, "EndMatchReaction", false); Set(p, "Replay", false); Set(p, "ManagerMatchesState", true);
        return p;
    }
    private static object TerminalPresentation()
    {
        var p = Pipeline(true, true); Set(p, "EndMatchReaction", true); Set(p, "CurrentReaction", true);
        Set(p, "RawProcessing", true); Set(p, "PropertyProcessing", true); Set(p, "Paused", false); Set(p, "Holding", false);
        return p;
    }
    private static string PipelineStatus(object p)
    { return (string)host.GetMethod("CRPipelineSettlementPolicy", F).Invoke(null, new[] { p }); }
    private static int Main(string[] args) { try { return Run(args); } catch(Exception e) { Console.WriteLine("TEST ERROR " + e.GetType().Name + " " + e.Message); if(e.InnerException != null) Console.WriteLine("INNER " + e.InnerException.GetType().Name + " " + e.InnerException.Message); return 1; } }
    private static int Run(string[] args)
    {
        var asm = Assembly.LoadFrom(args[0]); host = asm.GetType("PolytopiaAgent.BridgeMod", true);
        frame = host.GetNestedType("CombatFrame", F); unit = host.GetNestedType("CombatUnitView", F); cause = host.GetNestedType("CombatCause", F);
        instance = Activator.CreateInstance(host);
        var before = Frame("match-a"); Add(before, Unit("8", "2", "(3,3)", 100, null));
        var after = Frame("match-a");
        var diff = Diff(before, after, null);
        Check(diff.Count == 1 && diff[0].Contains("lost_visibility_or_removed") && diff[0].Contains("\"death\":null"), "enemy disappears without invented attacker/death");
        var native = Activator.CreateInstance(cause, true); Set(native, "ActionType", "KillUnitAction"); Set(native, "TargetId", "8");
        Check(Diff(before, after, native)[0].Contains("\"death\":true"), "explicit native KillUnitAction is distinguished from snapshot loss");
        before = Frame("match-a"); after = Frame("match-a");
        Add(before, Unit("4", "1", "(1,1)", 100, null)); Add(after, Unit("5", "1", "(2,1)", 100, "4"));
        diff = Diff(before, after, null);
        Check(diff.Count == 1 && diff[0].Contains("carrier_identity_transition") && diff[0].Contains("\"death\":false"), "embark conversion uses native passenger identity");
        Check(Diff(after, before, null).Count == 1 && Diff(after, before, null)[0].Contains("carrier_identity_transition"), "landing restores passenger ID without counting a spawn");
        var differentSeat = Frame("match-a"); Set(differentSeat, "Me", "2");
        Add(differentSeat, Unit("5", "2", "(2,1)", 100, "4"));
        Check(!string.Join("", Diff(before, differentSeat, null).ToArray()).Contains("carrier_identity_transition"),
            "passenger identity cannot cross controlled-player snapshots");
        var differentGame = Frame("match-b"); Add(differentGame, Unit("5", "1", "(2,1)", 100, "4"));
        Check(!string.Join("", Diff(before, differentGame, null).ToArray()).Contains("carrier_identity_transition"),
            "reused native IDs in another match cannot establish conversion");
        after = Frame("match-a"); Add(after, Unit("9", "1", "(1,1)", 100, null));
        diff = Diff(before, after, null);
        Check(diff.Count == 2 && !string.Join("", diff.ToArray()).Contains("carrier_identity_transition"), "same tile/unit type replacement does not prove identity conversion");
        before = Frame("match-a"); after = Frame("match-a");
        Add(before, Unit("1", "1", "(1,1)", 100, null)); Add(before, Unit("2", "2", "(2,2)", 100, null));
        Add(after, Unit("1", "1", "(1,1)", 50, null)); Add(after, Unit("2", "2", "(2,2)", 30, null));
        Check(Diff(before, after, null).Count == 2, "compound HP changes include every visible affected unit");
        Observe(before); long version = (long)Get(instance, "_combatVersion");
        Observe(before); Check((long)Get(instance, "_combatVersion") == version, "repeat reads do not increment stateVersion");
        Observe(after); var first = Events(0, 1); var evs = (IList)first["events"];
        Check(evs.Count == 1 && (bool)first["hasMore"], "bounded event page exposes resumable cursor");
        long cursor = Convert.ToInt64(first["nextEventId"]); var second = Events(cursor, 256);
        Check(((IList)second["events"]).Count == 1 && Convert.ToInt64(second["nextEventId"]) > cursor, "next event page neither repeats nor loses the second effect");
        Check(((Dictionary<string, object>)evs[0])["actorId"] == null, "polling differences do not fabricate actor attribution");
        long last = (long)Get(instance, "_combatEventId"); Observe(null);
        Check((long)Get(instance, "_combatEventId") == last && !(bool)Get(instance, "_combatCaptureAvailable"), "unreadable sample invalidates readiness without false unit removals");
        string stream = (string)Get(instance, "_combatStream"); Observe(Frame("match-b"));
        Check((string)Get(instance, "_combatStream") != stream && (long)Get(instance, "_combatEventId") == 0, "new match resets journal under a new streamId");
        for (int i = 0; i < 2050; i++) { var f = Frame("match-b"); Set(f, "Turn", i.ToString()); Observe(f); }
        var gap = Events(0, 1);
        Check((bool)gap["historyGap"] && Convert.ToInt64(gap["oldestEventId"]) > 1, "bounded journal signals lost history instead of pretending full coverage");
        Check(Resolution(false, false, false, false, 0, false, false, true, true, 1, 2, 0.5) == true,
            "pending choice can resolve its own native trigger with queued commands behind it");
        Check(Resolution(true, false, true, false, 0, true, true, true, true, 1, 2, 0.5) == true,
            "known choice reaction may receive its selection without settlement deadlock");
        Check(Resolution(false, false, true, false, 0, true, false, true, true, 1, 2, 0.5) == false,
            "unrelated reaction cannot be bypassed by an outstanding choice");
        Check(Resolution(false, false, false, false, 1, false, false, false, true, 1, 2, 0.5) == false,
            "unprocessed reaction queue blocks choice dispatch");
        Check(Resolution(false, false, false, false, null, false, false, false, true, 1, 2, 0.5) == null,
            "unreadable native queue remains unknown");
        Check(Resolution(false, false, false, false, 0, false, false, false, false, 0, 1, 0.1) == false,
            "one frame or short quiet interval never claims readiness");
        var frozenFrame = Frame("match-b"); Add(frozenFrame, Unit("4", "1", "(2,2)", 90, null));
        var ticketType = host.GetNestedType("CombatTicket", F); var ticket = Activator.CreateInstance(ticketType, true);
        var startFrame = Frame("match-b"); Add(startFrame, Unit("4", "1", "(1,1)", 100, null));
        Set(ticket, "Id", 900L); Set(ticket, "Stream", Get(instance, "_combatStream")); Set(ticket, "Action", "move"); Set(ticket, "UnitId", "4"); Set(ticket, "Coord", "(2,2)"); Set(ticket, "Dispatched", true); Set(ticket, "Accepted", true); Set(ticket, "Before", startFrame); Set(ticket, "FinalFrame", frozenFrame); Set(ticket, "FinalVersion", 50L); Set(ticket, "FinalEvent", 60L);
        Set(ticket, "FinalPipelineIdle", false); Set(ticket, "FinalCompletionAuthority", "frozen natural terminal proof");
        Set(ticket, "BeforeFingerprint", "player=1|units=4:1:Warrior:(1,1):100:False:False;|imps=|");
        Set(ticket, "FinalFingerprint", "player=1|units=4:1:Warrior:(2,2):90:True:False;|imps=|");
        ((IDictionary)Get(instance, "_combatTickets")).Add(900L, ticket);
        var settled = json.Deserialize<Dictionary<string, object>>((string)host.GetMethod("CombatSettlementJson", F).Invoke(instance, new object[] { null, 900L }));
        Check((bool)settled["settlementComplete"] && Convert.ToInt64(settled["stateVersion"]) == 50 && Convert.ToInt64(settled["lastEventId"]) == 60,
            "closed settlement uses its frozen version/event cursor even after later game changes");
        Check((bool)settled["actionCompleted"], "frozen final move evidence confirms the requested actor/destination");
        Check((bool)settled["pipelineIdle"] == false && (string)settled["completionAuthority"] == "frozen natural terminal proof",
            "closed ticket preserves its completion authority and actual pipeline idle separately");
        Check(PipelineStatus(Pipeline(true, true)) == "complete", "naturally observed native Ended permits idle terminal pause and hold");
        Check(PipelineStatus(Pipeline(true, false)) == "pending", "Ended without matching natural result cannot bypass lifecycle pause");
        Check(PipelineStatus(Pipeline(false, true)) == "pending", "natural result before native Ended cannot bypass lifecycle pause");
        foreach (var blocker in new[] { "PropertyProcessing", "RawProcessing", "Recapping", "Simulating", "Pausing", "Reacting", "CurrentReaction", "Queued" })
        {
            var p = Pipeline(true, true); Set(p, blocker, true);
            Check(PipelineStatus(p) == "pending", "terminal result cannot bypass " + blocker);
        }
        foreach (var blocker in new[] { "ReactionCount", "HookDepth" })
        {
            var p = Pipeline(true, true); Set(p, blocker, 1);
            Check(PipelineStatus(p) == "pending", "terminal result cannot bypass " + blocker);
        }
        foreach (var missing in new[] { "PropertyProcessing", "RawProcessing", "Recapping", "Simulating", "Paused", "Pausing", "Holding", "Reacting", "CurrentReaction", "Queued", "Popup", "ReactionCount", "PendingMine" })
        {
            var p = Pipeline(true, true); Set(p, missing, null);
            Check(PipelineStatus(p) == "unknown", "terminal result cannot replace unreadable " + missing);
        }
        var terminal = Pipeline(true, true); Set(terminal, "PendingMine", 1);
        Check(PipelineStatus(terminal) == "needs_resolution", "terminal unresolved trigger is never complete");
        terminal = Pipeline(true, true); Set(terminal, "Popup", true);
        Check(PipelineStatus(terminal) == "needs_resolution", "terminal popup is never complete");
        terminal = Pipeline(true, true); Set(terminal, "StableFrames", 1);
        Check(PipelineStatus(terminal) == "pending", "terminal still needs two distinct stable samples");
        terminal = Pipeline(true, true); Set(terminal, "QuietSeconds", 0.39);
        Check(PipelineStatus(terminal) == "pending", "terminal still needs full 400ms quiet interval");
        var ordinary = Pipeline(false, false); Set(ordinary, "Paused", false); Set(ordinary, "Holding", false); Set(ordinary, "LocalTurn", true);
        Check(PipelineStatus(ordinary) == "complete", "ordinary local turn remains complete with fully idle pipeline");
        var diagnostic = json.Deserialize<Dictionary<string, object>>((string)terminal.GetType().GetMethod("Json", F).Invoke(terminal, null));
        Check(diagnostic.ContainsKey("rawProcessing") && diagnostic.ContainsKey("terminalResultObservedSameEpisode")
            && !diagnostic.ContainsKey("actor") && !diagnostic.ContainsKey("commandTargets"), "pipeline diagnostics contain status scalars only");
        var presentation = TerminalPresentation();
        Check(PipelineStatus(presentation) == "complete", "natural non-replay EndMatchReaction terminal presentation completes without invented idle");
        Check((bool?)host.GetMethod("CRGameplayPipelineIdle", F).Invoke(null, new[] { presentation }) == false,
            "terminal presentation truthfully reports raw pipeline not idle");
        Check(((string)host.GetMethod("CRCompletionAuthority", F).Invoke(null, new object[] { presentation, "complete" })).Contains("without onComplete"),
            "terminal presentation completion authority explains native callback omission");
        foreach (var blocker in new[] { "Recapping", "Simulating", "Pausing", "Reacting", "Queued" })
        {
            var p = TerminalPresentation(); Set(p, blocker, true);
            Check(PipelineStatus(p) == "pending", "EndMatchReaction cannot bypass " + blocker);
        }
        foreach (var blocker in new[] { "ReactionCount", "HookDepth" })
        {
            var p = TerminalPresentation(); Set(p, blocker, 1);
            Check(PipelineStatus(p) == "pending", "EndMatchReaction cannot bypass " + blocker);
        }
        presentation = TerminalPresentation(); Set(presentation, "EndMatchReaction", false);
        Check(PipelineStatus(presentation) == "pending", "other native reaction cannot use terminal exception");
        presentation = TerminalPresentation(); Set(presentation, "Replay", true);
        Check(PipelineStatus(presentation) == "pending", "native replay callback path cannot use terminal exception");
        presentation = TerminalPresentation(); Set(presentation, "ManagerMatchesState", false);
        Check(PipelineStatus(presentation) == "pending", "stale action-manager game state cannot use terminal exception");
        foreach (var unknown in new[] { "EndMatchReaction", "Replay", "ManagerMatchesState" })
        {
            var p = TerminalPresentation(); Set(p, unknown, null);
            Check(PipelineStatus(p) == "unknown", "EndMatchReaction requires readable " + unknown);
        }
        foreach (var mismatch in new[] { "NativeEnded", "TerminalObserved" })
        {
            var p = TerminalPresentation(); Set(p, mismatch, false);
            Check(PipelineStatus(p) == "pending", "EndMatchReaction still requires " + mismatch);
        }
        presentation = TerminalPresentation(); Set(presentation, "PendingMine", 1);
        Check(PipelineStatus(presentation) == "needs_resolution", "EndMatchReaction cannot bypass outstanding trigger");
        presentation = TerminalPresentation(); Set(presentation, "Popup", true);
        Check(PipelineStatus(presentation) == "needs_resolution", "EndMatchReaction cannot bypass popup");
        presentation = TerminalPresentation(); Set(presentation, "StableFrames", 1);
        Check(PipelineStatus(presentation) == "pending", "EndMatchReaction still requires stable samples");
        presentation = TerminalPresentation(); Set(presentation, "QuietSeconds", 0.39);
        Check(PipelineStatus(presentation) == "pending", "EndMatchReaction still requires 400ms quiet interval");
        Set(instance, "_activeAuditRequested", false);
        var publicState = json.Deserialize<Dictionary<string, object>>((string)host.GetMethod("CombatStateJson", F).Invoke(instance, new object[] { null }));
        Check(!publicState.ContainsKey("pipeline") && publicState.ContainsKey("pipelineIdle"), "formal decision state omits pipeline queue diagnostics");
        Set(instance, "_activeAuditRequested", true);
        var auditState = json.Deserialize<Dictionary<string, object>>((string)host.GetMethod("CombatStateJson", F).Invoke(instance, new object[] { null }));
        Check(auditState.ContainsKey("pipeline"), "explicit audit state includes detailed pipeline diagnostics");
        var boostCheck = host.GetMethod("CRBoostEffectAdded", F);
        Check((bool)boostCheck.Invoke(null, new object[] { "[]", "[\"Swift\"]", 115 }), "current native version Boost adds Swift");
        Check(!(bool)boostCheck.Invoke(null, new object[] { "[]", "[\"Boosted\"]", 115 }), "old Boosted cannot confirm current native version Boost");
        Check((bool)boostCheck.Invoke(null, new object[] { "[]", "[\"Boosted\"]", 114 }), "old native version Boost requires Boosted");
        Check(!(bool)boostCheck.Invoke(null, new object[] { "[]", "[\"Swift\"]", 114 }), "Swift cannot confirm old native version Boost");
        Check(!(bool)boostCheck.Invoke(null, new object[] { "[\"Swift\"]", "[\"Swift\"]", 115 }), "existing Swift is not a newly applied Boost");
        Check(!(bool)boostCheck.Invoke(null, new object[] { null, "[\"Swift\"]", 115 }), "unknown previous effects cannot confirm Boost");
        Check(!(bool)boostCheck.Invoke(null, new object[] { "[]", "[\"Swift\"]", null }), "unknown native version cannot confirm effect semantics");
        var poisoned = Unit("70", "1", "(1,1)", 100, null); Set(poisoned, "Effects", "[\"Poison\"]");
        var cured = Unit("70", "1", "(1,1)", 100, null); Set(cured, "Effects", "[]");
        var healCheck = host.GetMethod("CRHealingChanged", F);
        Check((bool)healCheck.Invoke(null, new[] { poisoned, cured }), "pure poison removal with unchanged full HP is healing");
        Set(poisoned, "Effects", null);
        Check(!(bool)healCheck.Invoke(null, new[] { poisoned, cured }), "unknown poison history cannot prove detox");
        Set(poisoned, "Health", 90);
        Check((bool)healCheck.Invoke(null, new[] { poisoned, cured }), "known HP increase still proves healing");
        Set(cured, "Id", "71");
        Check(!(bool)healCheck.Invoke(null, new[] { poisoned, cured }), "another unit cannot substitute for healed native identity");
        var cloakStart = Frame("match-c"); var cloak = Unit("80", "1", "(4,4)", 100, null); Set(cloak, "Type", "Cloak"); Add(cloakStart, cloak);
        var cloakEnd = Frame("match-c"); Add(cloakEnd, Unit("81", "1", "(6,4)", 100, null));
        var infiltration = Activator.CreateInstance(ticketType, true); Set(infiltration, "Before", cloakStart); Set(infiltration, "Action", "attack"); Set(infiltration, "UnitId", "80");
        var infiltrateCheck = host.GetMethod("CRInfiltrationConfirmed", F);
        ((HashSet<string>)Get(infiltration, "NativeInfiltrationSpawnIds")).Add("81");
        Check(!(bool)infiltrateCheck.Invoke(null, new[] { infiltration, cloakEnd }), "a new own unit alone does not prove Cloak infiltration");
        Set(infiltration, "NativeInfiltrateObserved", true);
        ((IList)Get(infiltration, "NativeInfiltrationRewards")).Add(new object());
        Check(!(bool)infiltrateCheck.Invoke(null, new[] { infiltration, cloakEnd }), "infiltration reward without matching TrainAction cannot confirm");
        ((IList)Get(infiltration, "NativeInfiltrationTrains")).Add(new object());
        Check((bool)infiltrateCheck.Invoke(null, new[] { infiltration, cloakEnd }), "observed own Cloak exact native reward/train chain with real generated ID confirms");
        Set(cloak, "Type", "Warrior");
        Check(!(bool)infiltrateCheck.Invoke(null, new[] { infiltration, cloakEnd }), "ordinary attacker cannot use Cloak-specific confirmation");
        Set(cloak, "Type", "Cloak"); Set(cloak, "Owner", "2");
        Check(!(bool)infiltrateCheck.Invoke(null, new[] { infiltration, cloakEnd }), "enemy Cloak cannot use own-command confirmation");
        Set(cloak, "Owner", "1");
        Check(!(bool)infiltrateCheck.Invoke(null, new[] { infiltration, Frame("match-c") }), "unobserved generated coordinates cannot be filled as expected values");
        var nativeObject = new object(); var nativeList = new List<object>(); nativeList.Add(nativeObject);
        Check((bool)host.GetMethod("CRContainsNative", F).Invoke(null, new object[] { nativeList, nativeObject })
            && !(bool)host.GetMethod("CRContainsNative", F).Invoke(null, new object[] { nativeList, new object() }),
            "native child proof binds exact object identity instead of similar coordinates/time interval");
        var proofPassType = host.GetNestedType("CombatHookPass", F);
        var boostTicket = Activator.CreateInstance(ticketType, true); Set(boostTicket, "Before", Frame("proof-match")); Set(boostTicket, "NativeVersion", 115);
        var boostBefore = Frame("proof-match"); var boostOld = Unit("90", "1", "(2,2)", 100, null); Set(boostOld, "Effects", "[]"); Add(boostBefore, boostOld);
        var boostAfter = Frame("proof-match"); var boostNow = Unit("90", "1", "(2,2)", 100, null); Set(boostNow, "Effects", "[\"Swift\"]"); Add(boostAfter, boostNow);
        var boostPass = Activator.CreateInstance(proofPassType, true); Set(boostPass, "CandidateKind", "BoostAction"); Set(boostPass, "CandidateCoord", "(2,2)"); Set(boostPass, "Before", boostBefore);
        var recordProof = host.GetMethod("CRRecordNativeProof", F);
        recordProof.Invoke(instance, new[] { boostPass, boostAfter });
        Check(((HashSet<string>)Get(boostTicket, "NativeBoostTargets")).Count == 0, "effect change without bound ticket native action does not prove Boost");
        ((IList)Get(boostPass, "ProofTickets")).Add(boostTicket);
        recordProof.Invoke(instance, new[] { boostPass, boostAfter });
        Check(((HashSet<string>)Get(boostTicket, "NativeBoostTargets")).Contains("90"), "bound native BoostAction records exact own Swift affected ID");
        var healTicket = Activator.CreateInstance(ticketType, true); Set(healTicket, "Before", Frame("proof-match"));
        var healBefore = Frame("proof-match"); var healOld = Unit("91", "1", "(3,3)", 100, null); Set(healOld, "Effects", "[\"Poison\"]"); Add(healBefore, healOld);
        var healAfter = Frame("proof-match"); var healNow = Unit("91", "1", "(3,3)", 100, null); Set(healNow, "Effects", "[]"); Add(healAfter, healNow);
        var healPass = Activator.CreateInstance(proofPassType, true); Set(healPass, "CandidateKind", "HealAction"); Set(healPass, "CandidateCoord", "(3,3)"); Set(healPass, "Before", healBefore);
        ((IList)Get(healPass, "ProofTickets")).Add(healTicket); recordProof.Invoke(instance, new[] { healPass, healAfter });
        Check(((HashSet<string>)Get(healTicket, "NativeHealTargets")).Contains("91"), "bound native HealAction records detox target without HP increase");
        Set(healNow, "Owner", "2"); ((HashSet<string>)Get(healTicket, "NativeHealTargets")).Clear(); recordProof.Invoke(instance, new[] { healPass, healAfter });
        Check(((HashSet<string>)Get(healTicket, "NativeHealTargets")).Count == 0, "native healing proof does not disclose or attribute enemy effects");
        // Reproduce the real ticket43's consumed Cloak_Boat identity. The generated IDs and
        // locations are fixture observations, not a spawn layout predicted by the bridge.
        var boatStart = Frame("boat-fixture"); var boat = Unit("30", "1", "(7,11)", 50, null); Set(boat, "Type", "Cloak_Boat"); Add(boatStart, boat);
        var boatTicket = Activator.CreateInstance(ticketType, true); Set(boatTicket, "Before", boatStart); Set(boatTicket, "Action", "attack"); Set(boatTicket, "UnitId", "30"); Set(boatTicket, "Coord", "(6,11)");
        var actorMatch = host.GetMethod("CRInfiltrationActorMatches", F);
        Check((bool)actorMatch.Invoke(null, new object[] { boatTicket, boat, "1", "(6,11)" }), "real Cloak_Boat actor30 binds to its own native infiltration ticket");
        Check(!(bool)actorMatch.Invoke(null, new object[] { boatTicket, boat, "2", "(6,11)" }), "Cloak_Boat requires actual native action PlayerId to match own player");
        Check(!(bool)actorMatch.Invoke(null, new object[] { boatTicket, boat, "1", "(6,12)" }), "Cloak_Boat requires requested target coordinate association");
        Check(!(bool)actorMatch.Invoke(null, new object[] { boatTicket, boat, null, "(6,11)" }), "unreadable native player cannot bind Cloak_Boat proof");
        var stranger = Unit("31", "1", "(7,11)", 50, null); Set(stranger, "Type", "Cloak_Boat");
        Check(!(bool)actorMatch.Invoke(null, new object[] { boatTicket, stranger, "1", "(6,11)" }), "same kind and tile cannot replace named Cloak_Boat ID");
        Set(boat, "Type", "Transportship");
        Check(!(bool)actorMatch.Invoke(null, new object[] { boatTicket, boat, "1", "(6,11)" }), "ordinary ship cannot acquire Cloak-specific proof");
        Set(boat, "Type", "Cloak_Boat");
        var stackState = new CombatActionStackFixture(); stackState.ActionStack.Values.Add(new object());
        var reward = new InfiltrationRewardAction(); stackState.ActionStack.Values.Add(reward);
        var consumed = Frame("boat-fixture");
        var infiltratePass = Activator.CreateInstance(proofPassType, true); Set(infiltratePass, "CandidateKind", "InfiltrateAction"); Set(infiltratePass, "CandidateCoord", "(6,11)"); Set(infiltratePass, "State", stackState); Set(infiltratePass, "Before", boatStart); Set(infiltratePass, "BeforeStackCount", 1);
        ((IList)Get(infiltratePass, "ProofTickets")).Add(boatTicket); recordProof.Invoke(instance, new[] { infiltratePass, consumed });
        var rewardObjects = (IList)Get(boatTicket, "NativeInfiltrationRewards");
        Check((bool)Get(boatTicket, "NativeInfiltrateObserved") && rewardObjects.Count == 1 && object.ReferenceEquals(rewardObjects[0], reward),
            "consumed Cloak_Boat hook records actual appended native reward object");
        Check(stackState.ActionStack.ReadIndices.Count == 1 && stackState.ActionStack.ReadIndices[0] == 1,
            "infiltration proof reads only newly appended own action and never old stack entries");
        Check(stackState.ActionStack.EnumerationAttempts == 0, "native action stack Count never enumerates earlier actions");
        Check(host.GetMethod("CRStackCount", F).Invoke(null, new object[] { new CombatUnreadableStackFixture() }) == null,
            "unreadable native action stack Count remains unknown rather than zero");
        var rewardMatch = host.GetMethod("CRInfiltrationRewardMatches", F);
        Check((bool)rewardMatch.Invoke(null, new object[] { boatTicket, reward, "1", "(6,11)" }), "actual own reward remains bound after its Cloak_Boat actor has disappeared");
        Check(!(bool)rewardMatch.Invoke(null, new object[] { boatTicket, new InfiltrationRewardAction(), "1", "(6,11)" }), "unrelated native reward cannot inherit consumed boat actor attribution");
        Check(!(bool)rewardMatch.Invoke(null, new object[] { boatTicket, reward, "2", "(6,11)" }), "visible enemy target owner cannot become infiltration reward actor");
        Check(!(bool)rewardMatch.Invoke(null, new object[] { boatTicket, reward, "1", "(6,12)" }), "native reward requires its requested target coordinate");
        Check(!(bool)rewardMatch.Invoke(null, new object[] { boatTicket, reward, null, "(6,11)" }), "unreadable reward player does not infer an own actor");
        Check(!(bool)host.GetMethod("CRContainsNative", F).Invoke(null, new object[] { Get(boatTicket, "NativeInfiltrationRewards"), new InfiltrationRewardAction() }),
            "unrelated reward object cannot inherit boat infiltration proof");
        var trainObjects = new List<TrainAction>(); for (int i = 0; i < 5; i++) { var train = new TrainAction(); trainObjects.Add(train); stackState.ActionStack.Values.Add(train); }
        var rewardPass = Activator.CreateInstance(proofPassType, true); Set(rewardPass, "CandidateKind", "InfiltrationRewardAction"); Set(rewardPass, "Action", reward); Set(rewardPass, "State", stackState); Set(rewardPass, "Before", consumed); Set(rewardPass, "BeforeStackCount", 2);
        ((IList)Get(rewardPass, "ProofTickets")).Add(boatTicket); recordProof.Invoke(instance, new[] { rewardPass, consumed });
        Check(((IList)Get(boatTicket, "NativeInfiltrationTrains")).Count == 5 && ((HashSet<string>)Get(boatTicket, "NativeInfiltrationSpawnIds")).Count == 0,
            "actual reward appends five exact TrainAction objects without pre-filling generated units");
        Check(!(bool)infiltrateCheck.Invoke(null, new[] { boatTicket, consumed }), "queued TrainAction objects alone cannot confirm actual boat spawn");
        string[] realCoords = { "(5,13)", "(5,11)", "(5,12)", "(4,9)", "(8,12)" };
        var boatFinal = Frame("boat-fixture");
        for (int i = 0; i < 5; i++)
        {
            var prior = Frame("boat-fixture"); foreach (DictionaryEntry e in (IDictionary)Get(boatFinal, "Units")) Add(prior, e.Value);
            var dagger = Unit((41 + i).ToString(), "1", realCoords[i], 100, null); Set(dagger, "Type", "Dagger"); Add(boatFinal, dagger);
            var trainPass = Activator.CreateInstance(proofPassType, true); Set(trainPass, "CandidateKind", "TrainAction"); Set(trainPass, "Action", trainObjects[i]); Set(trainPass, "CandidateCoord", realCoords[i]); Set(trainPass, "Before", prior);
            Check((bool)host.GetMethod("CRContainsNative", F).Invoke(null, new object[] { Get(boatTicket, "NativeInfiltrationTrains"), trainObjects[i] }), "executing generated TrainAction has exact native reward association " + i);
            ((IList)Get(trainPass, "ProofTickets")).Add(boatTicket); recordProof.Invoke(instance, new[] { trainPass, boatFinal });
        }
        Check(((HashSet<string>)Get(boatTicket, "NativeInfiltrationSpawnIds")).Count == 5 && (bool)infiltrateCheck.Invoke(null, new[] { boatTicket, boatFinal }),
            "native boat infiltration chain confirms observed Dagger41-45 despite consumed attacker");
        Set(boatTicket, "Id", 901L); Set(boatTicket, "Stream", Get(instance, "_combatStream")); Set(boatTicket, "Accepted", true); Set(boatTicket, "Dispatched", true); Set(boatTicket, "FinalFrame", boatFinal); Set(boatTicket, "FinalVersion", 159L); Set(boatTicket, "FinalEvent", 155L);
        Set(boatTicket, "BeforeFingerprint", "player=1|turn=17|acting=1|units=30:1:Cloak_Boat:(7,11):50:False:False;|imps=|");
        Set(boatTicket, "FinalFingerprint", "player=1|turn=17|acting=1|units=41:1:Dagger:(5,13):100:False:False;|imps=|");
        ((IDictionary)Get(instance, "_combatTickets")).Add(901L, boatTicket);
        var boatSettled = json.Deserialize<Dictionary<string, object>>((string)host.GetMethod("CombatSettlementJson", F).Invoke(instance, new object[] { null, 901L }));
        Check((bool)boatSettled["actionCompleted"] && (string)boatSettled["effectConfirmation"] == "confirmed"
            && ((string)boatSettled["confirmationBasis"]).Contains("exact appended"), "final boat attack reply uses action-specific native proof instead of missing-attacker ordinary attack predicate");
        Set(boatTicket, "NativeInfiltrateObserved", false);
        var unprovenBoat = json.Deserialize<Dictionary<string, object>>((string)host.GetMethod("CombatSettlementJson", F).Invoke(instance, new object[] { null, 901L }));
        Check(!(bool)unprovenBoat["actionCompleted"] && (string)unprovenBoat["effectConfirmation"] == "unknown",
            "five real new units without matching native infiltration receipt never manufacture confirmation");
        // Real event89: native MindBender35 converts Warrior19; HP stays 50.
        var convertBefore = Frame("convert-fixture"); var mind = Unit("35", "1", "(9,7)", 100, null); Set(mind, "Type", "MindBender"); Add(convertBefore, mind);
        var enemyWarrior = Unit("19", "2", "(8,6)", 50, null); Add(convertBefore, enemyWarrior);
        var convertAfter = Frame("convert-fixture"); Add(convertAfter, mind); var convertedWarrior = Unit("19", "1", "(8,6)", 50, null); Add(convertAfter, convertedWarrior);
        var convertTicket = Activator.CreateInstance(ticketType, true); Set(convertTicket, "Before", convertBefore); Set(convertTicket, "Action", "attack"); Set(convertTicket, "UnitId", "35"); Set(convertTicket, "Coord", "(8,6)");
        var convertMatch = host.GetMethod("CRConvertActorMatches", F); var convertedCheck = host.GetMethod("CRConversionConfirmed", F);
        Check((bool)convertMatch.Invoke(null, new object[] { convertTicket, mind, enemyWarrior, "1", "(8,6)" }), "real own MindBender35 binds native ConvertAction to requested Warrior19");
        Check(!(bool)convertMatch.Invoke(null, new object[] { convertTicket, mind, enemyWarrior, "2", "(8,6)" }), "enemy native player cannot bind own conversion proof");
        Check(!(bool)convertMatch.Invoke(null, new object[] { convertTicket, mind, enemyWarrior, null, "(8,6)" }), "unknown native player cannot bind own conversion proof");
        Check(!(bool)convertMatch.Invoke(null, new object[] { convertTicket, mind, enemyWarrior, "1", "(8,7)" }), "conversion requires requested target coordinate");
        Check(!(bool)convertMatch.Invoke(null, new object[] { convertTicket, mind, Unit("20", "2", "(8,6)", 50, null), "1", "(8,6)" }), "same tile replacement cannot substitute conversion target ID");
        Check(!(bool)convertMatch.Invoke(null, new object[] { convertTicket, mind, Unit("19", null, "(8,6)", 50, null), "1", "(8,6)" }), "unknown target owner cannot imply enemy conversion");
        Check(!(bool)convertedCheck.Invoke(null, new[] { convertTicket, convertAfter }), "visible owner change without native ConvertAction receipt is not confirmation");
        var convertPass = Activator.CreateInstance(proofPassType, true); Set(convertPass, "CandidateKind", "ConvertAction"); Set(convertPass, "CandidateCoord", "(8,6)"); Set(convertPass, "Before", convertBefore);
        var convertCause = Activator.CreateInstance(cause, true); Set(convertCause, "ActionType", "ConvertAction"); Set(convertCause, "ActorId", "35"); Set(convertCause, "ActorOwner", "1"); Set(convertCause, "TargetId", "19"); Set(convertPass, "Cause", convertCause);
        recordProof.Invoke(instance, new[] { convertPass, convertAfter });
        Check(((HashSet<string>)Get(convertTicket, "NativeConvertTargets")).Count == 0, "unbound native conversion cannot attach to an unrelated ticket");
        ((IList)Get(convertPass, "ProofTickets")).Add(convertTicket); Set(convertCause, "ActorId", "36"); recordProof.Invoke(instance, new[] { convertPass, convertAfter });
        Check(((HashSet<string>)Get(convertTicket, "NativeConvertTargets")).Count == 0, "wrong native actor cannot record same-target conversion proof");
        Set(convertCause, "ActorId", "35"); Set(convertCause, "TargetId", "20"); recordProof.Invoke(instance, new[] { convertPass, convertAfter });
        Check(((HashSet<string>)Get(convertTicket, "NativeConvertTargets")).Count == 0, "wrong native target ID cannot record conversion proof");
        Set(convertCause, "TargetId", "19"); recordProof.Invoke(instance, new[] { convertPass, convertAfter });
        Check(((HashSet<string>)Get(convertTicket, "NativeConvertTargets")).Contains("19") && (bool)convertedCheck.Invoke(null, new[] { convertTicket, convertAfter }), "actual ConvertAction with same visible unit owner2 to owner1 confirms unchanged HP50");
        var replacementAfter = Frame("convert-fixture"); Add(replacementAfter, Unit("20", "1", "(8,6)", 50, null));
        Check(!(bool)convertedCheck.Invoke(null, new[] { convertTicket, replacementAfter }), "new own unit at target cannot replace converted native identity");
        Check(!(bool)convertedCheck.Invoke(null, new[] { convertTicket, Frame("another-episode") }), "conversion receipt cannot cross native game episode");
        Set(convertTicket, "Id", 902L); Set(convertTicket, "Stream", Get(instance, "_combatStream")); Set(convertTicket, "Accepted", true); Set(convertTicket, "Dispatched", true); Set(convertTicket, "FinalFrame", convertAfter);
        Set(convertTicket, "BeforeFingerprint", "player=1|turn=22|acting=1|units=35:1:MindBender:(9,7):100:False:False;19:2:Warrior:(8,6):50:False:False;|imps=|");
        Set(convertTicket, "FinalFingerprint", "player=1|turn=22|acting=1|units=35:1:MindBender:(9,7):100:True:True;19:1:Warrior:(8,6):50:True:True;|imps=|");
        ((IDictionary)Get(instance, "_combatTickets")).Add(902L, convertTicket);
        var convertedReply = json.Deserialize<Dictionary<string, object>>((string)host.GetMethod("CombatSettlementJson", F).Invoke(instance, new object[] { null, 902L }));
        Check((bool)convertedReply["actionCompleted"] && (string)convertedReply["effectConfirmation"] == "confirmed" && ((string)convertedReply["confirmationBasis"]).Contains("ConvertAction"), "final conversion reply overrides ordinary attack HP-only failure using native proof");
        Check(((IList)convertedReply["actualAffectedUnits"]).Count == 1, "conversion reports actual before and after target properties");
        ((HashSet<string>)Get(convertTicket, "NativeConvertTargets")).Clear();
        var unprovenConvertReply = json.Deserialize<Dictionary<string, object>>((string)host.GetMethod("CombatSettlementJson", F).Invoke(instance, new object[] { null, 902L }));
        Check(!(bool)unprovenConvertReply["actionCompleted"] && (string)unprovenConvertReply["effectConfirmation"] == "not_confirmed", "ordinary attack ownership-only change without ConvertAction receipt remains not_confirmed");
        // Live T27: Giant28 killed Bombership39, advanced into the Port, and became
        // Juggernaut51's passenger. The attacker is absent from the final tile list.
        var embarkBefore = Frame("attack-embark-fixture");
        var giant = Unit("28", "1", "(4,7)", 340, null); Set(giant, "Type", "Giant"); Set(giant, "Attacked", false); Add(embarkBefore, giant);
        var defenderBoat = Unit("39", "2", "(5,6)", 100, null); Set(defenderBoat, "Type", "Bombership"); Add(embarkBefore, defenderBoat);
        var embarkAfter = Frame("attack-embark-fixture");
        var carrier = Unit("51", "1", "(5,6)", 340, "28"); Set(carrier, "Type", "Juggernaut"); Set(carrier, "Attacked", true); Set(carrier, "Moved", true); Add(embarkAfter, carrier);
        var embarkTicket = Activator.CreateInstance(ticketType, true);
        Set(embarkTicket, "Before", embarkBefore); Set(embarkTicket, "Action", "attack"); Set(embarkTicket, "UnitId", "28"); Set(embarkTicket, "Coord", "(5,6)");
        Set(embarkTicket, "Accepted", true); Set(embarkTicket, "Dispatched", true);
        var embarkCheck = host.GetMethod("CRAttackEmbarkConfirmed", F);
        var killedIds = (HashSet<string>)Get(embarkTicket, "NativeKilledIds"); killedIds.Add("39");
        Check(!(bool)embarkCheck.Invoke(null, new[] { embarkTicket, embarkAfter }), "target kill and own carrier alone do not confirm an unobserved native attack");
        killedIds.Clear();
        var attackPass = Activator.CreateInstance(proofPassType, true); Set(attackPass, "CandidateKind", "AttackAction"); Set(attackPass, "CandidateCoord", "(5,6)"); Set(attackPass, "Before", embarkBefore);
        var attackCause = Activator.CreateInstance(cause, true); Set(attackCause, "ActionType", "AttackAction"); Set(attackCause, "ActorId", "27"); Set(attackCause, "ActorOwner", "1"); Set(attackCause, "TargetId", "39"); Set(attackPass, "Cause", attackCause);
        ((IList)Get(attackPass, "ProofTickets")).Add(embarkTicket);
        recordProof.Invoke(instance, new[] { attackPass, embarkAfter });
        var attackedIds = (HashSet<string>)Get(embarkTicket, "NativeAttackTargets");
        Check(attackedIds.Count == 0, "wrong native attack actor cannot attach same-target proof");
        Set(attackCause, "ActorId", "28"); Set(attackCause, "TargetId", "40"); recordProof.Invoke(instance, new[] { attackPass, embarkAfter });
        Check(attackedIds.Count == 0, "replacement defender ID cannot attach attack proof");
        Set(attackCause, "TargetId", "39"); recordProof.Invoke(instance, new[] { attackPass, embarkAfter });
        Check(attackedIds.Contains("39"), "exact native actor and requested visible target attach attack proof");
        Check(!(bool)embarkCheck.Invoke(null, new[] { embarkTicket, embarkAfter }), "defender disappearance without native kill stays unconfirmed");
        killedIds.Add("39");
        Check((bool)embarkCheck.Invoke(null, new[] { embarkTicket, embarkAfter }), "live attack then embark confirms by original passenger identity and exact native target kill");
        Set(carrier, "PassengerId", "27");
        Check(!(bool)embarkCheck.Invoke(null, new[] { embarkTicket, embarkAfter }), "same tile replacement carrier cannot substitute another passenger"); Set(carrier, "PassengerId", "28");
        Set(carrier, "Attacked", null);
        Check(!(bool)embarkCheck.Invoke(null, new[] { embarkTicket, embarkAfter }), "unknown attack consumption cannot prove embark attack completion"); Set(carrier, "Attacked", true);
        Set(embarkAfter, "Episode", "other-match");
        Check(!(bool)embarkCheck.Invoke(null, new[] { embarkTicket, embarkAfter }), "carrier proof cannot cross match episodes"); Set(embarkAfter, "Episode", "attack-embark-fixture");
        Set(carrier, "Owner", "2");
        Check(!(bool)embarkCheck.Invoke(null, new[] { embarkTicket, embarkAfter }), "enemy carrier cannot confirm own surviving attacker"); Set(carrier, "Owner", "1");
        Set(embarkTicket, "Id", 903L); Set(embarkTicket, "Stream", Get(instance, "_combatStream")); Set(embarkTicket, "FinalFrame", embarkAfter);
        Set(embarkTicket, "BeforeFingerprint", "player=1|units=28:1:Giant:(4,7):340:False:False;39:2:Bombership:(5,6):100:?:?;|imps=");
        Set(embarkTicket, "FinalFingerprint", "player=1|units=51:1:Juggernaut:(5,6):340:True:True;|imps=");
        ((IDictionary)Get(instance, "_combatTickets")).Add(903L, embarkTicket);
        var embarkReply = json.Deserialize<Dictionary<string, object>>((string)host.GetMethod("CombatSettlementJson", F).Invoke(instance, new object[] { null, 903L }));
        Check((bool)embarkReply["actionCompleted"] && (string)embarkReply["effectConfirmation"] == "confirmed" && ((string)embarkReply["confirmationBasis"]).Contains("carrier passenger"), "final attack reply confirms real kill and embark despite missing original actor in tile snapshot");
        killedIds.Clear();
        var uncertainEmbark = json.Deserialize<Dictionary<string, object>>((string)host.GetMethod("CombatSettlementJson", F).Invoke(instance, new object[] { null, 903L }));
        Check(!(bool)uncertainEmbark["actionCompleted"] && (string)uncertainEmbark["effectConfirmation"] == "unknown", "missing target kill proof preserves ordinary missing-attacker unknown result");
        CheckEventCoverage();
        CheckTicketHistory();
        Console.WriteLine("ALL PASS " + passed); return 0;
    }

    // Exercise production insertion/query/dispatch, not a replacement ticket store.
    // The complete public frame is managed input; no native game execution is claimed.
    private static void CheckTicketHistory()
    {
        instance = Activator.CreateInstance(host);
        Observe(Frame("ticket-capacity"));
        var begin = host.GetMethod("BeginCombatSettlement", F);
        var dispatch = host.GetMethod("CompleteCombatDispatch", F);
        var query = host.GetMethod("CombatSettlementJson", F);
        var tickets = (IDictionary)Get(instance, "_combatTickets");
        long first = 0, last = 0;
        bool monotonic = true, bounded = true;
        for (int index = 0; index < 257; index++)
        {
            long ticket = (long)begin.Invoke(instance, new object[] { null, "move", "4", "(2,2)", null, 10000L + index });
            monotonic &= ticket > last;
            if (index == 0) first = ticket;
            last = ticket;
            dispatch.Invoke(instance, new object[] { ticket, (bool?)false });
            bounded &= tickets.Count <= 128;
        }
        Check(first > 0 && monotonic && bounded && tickets.Count == 128,
            "production ticket insertion stays bounded across two turnovers without reusing IDs");
        var expired = json.Deserialize<Dictionary<string, object>>((string)query.Invoke(instance, new object[] { null, first }));
        Check(!(bool)expired["ok"] && (string)expired["errorCode"] == "SETTLEMENT_TICKET_UNKNOWN"
            && !(bool)expired["settlementComplete"] && !expired.ContainsKey("requestId"),
            "expired original ticket cannot acquire another request's result");
        var newest = json.Deserialize<Dictionary<string, object>>((string)query.Invoke(instance, new object[] { null, last }));
        Check((bool)newest["ok"] && Convert.ToInt64(newest["ticketId"]) == last
            && Convert.ToInt64(newest["requestId"]) == 10256L && (string)newest["settlementStatus"] == "rejected",
            "latest retained ticket preserves its original request and dispatch outcome");
        dispatch.Invoke(instance, new object[] { first, (bool?)true });
        var unchanged = json.Deserialize<Dictionary<string, object>>((string)query.Invoke(instance, new object[] { null, last }));
        Check((string)unchanged["settlementStatus"] == "rejected" && !tickets.Contains(first),
            "late dispatch of an evicted ticket neither recreates it nor changes the newest result");
        var oldStream = Get(instance, "_combatStream");
        Observe(Frame("ticket-next-game"));
        var previous = json.Deserialize<Dictionary<string, object>>((string)query.Invoke(instance, new object[] { null, last }));
        long next = (long)begin.Invoke(instance, new object[] { null, "move", "4", "(2,2)", null, 11000L });
        Check((string)previous["errorCode"] == "SETTLEMENT_TICKET_UNKNOWN"
            && !object.Equals(oldStream, Get(instance, "_combatStream")) && next > last,
            "new episode invalidates old ticket history without recycling its IDs");
    }

    private static Dictionary<string, object> Hooks()
    { return (Dictionary<string, object>)Events(0, 1)["nativeHooks"]; }
    private static void NativeObserve(object f, object c)
    { host.GetMethod("CRObserve", F).Invoke(instance, new[] { f, c }); }
    private static void CheckEventCoverage()
    {
        instance = Activator.CreateInstance(host); Observe(Frame("coverage-a"));
        Check((string)Hooks()["installationStatus"] == "not_attempted", "unattempted installation is explicit");
        var names = (string[])host.GetField("CombatHookTypes", F).GetValue(null);
        foreach (var name in names) host.GetMethod("CRRegisterHookInstallation", F).Invoke(instance, new object[] { name, "installed" });
        Check((string)Hooks()["installationStatus"] == "complete" && (int)Hooks()["installedTypeCount"] == names.Length, "installation counts audited hook types");
        Check((string)Events(0, 1)["nativeHookCoverage"] == "installed-not-live-validated", "installation alone never proves execution");
        var a = Frame("coverage-a"); Add(a, Unit("1", "1", "(1,1)", 100, null)); Add(a, Unit("2", "2", "(2,1)", 100, null)); Observe(a);
        Check(((IList)Hooks()["observedVisibleTypes"]).Count == 0, "snapshot differences do not count as native hook evidence");
        var attack = Activator.CreateInstance(cause, true); Set(attack, "ActionType", "AttackAction"); Set(attack, "VisibleNativeAction", true);
        NativeObserve(a, attack);
        Check(((IList)Hooks()["observedVisibleTypes"]).Count == 0, "hook without visible journal differences is not an observed event");
        var b = Frame("coverage-a"); Add(b, Unit("1", "1", "(1,1)", 70, null)); Add(b, Unit("2", "2", "(2,1)", 30, null));
        NativeObserve(b, attack);
        var observed = (IList)Hooks()["observedVisibleTypes"]; var row = (Dictionary<string, object>)observed[0];
        Check(observed.Count == 1 && (string)row["nativeAction"] == "AttackAction", "only the actual visible native type gains evidence");
        Check(Convert.ToInt64(row["visibleEventCount"]) == 2 && Convert.ToInt64(row["lastEventId"]) - Convert.ToInt64(row["firstEventId"]) == 1, "compound attack counts visible entries instead of inventing two executions");
        Check((string)Events(0, 1)["nativeHookCoverage"] == "installed-with-visible-evidence", "legacy coverage status reflects per-type evidence");
        Check(!(bool)Hooks()["completeActionCoverage"] && ((IList)Hooks()["unobservedTypes"]).Count == names.Length - 1, "one observed attack does not validate every hook or composite effect");
        Check((string)Hooks()["streamId"] == (string)Get(instance, "_combatStream"), "evidence carries its journal stream identity");
        NativeObserve(b, attack);
        Check(Convert.ToInt64(((Dictionary<string, object>)((IList)Hooks()["observedVisibleTypes"])[0])["visibleEventCount"]) == 2, "repeat sample or closed hook cannot duplicate evidence");
        var hidden = Activator.CreateInstance(cause, true); Set(hidden, "ActionType", "MoveAction");
        var c = Frame("coverage-a"); Set(c, "Turn", "5"); NativeObserve(c, hidden);
        Check(((IList)Hooks()["observedVisibleTypes"]).Count == 1, "hidden action kind cannot enter coverage telemetry");
        Set(hidden, "VisibleNativeAction", true); Set(hidden, "ActionType", "UninstalledAction");
        c = Frame("coverage-a"); Set(c, "Turn", "6"); NativeObserve(c, hidden);
        Check(((IList)Hooks()["observedVisibleTypes"]).Count == 1, "uninstalled type cannot masquerade as installed native evidence");
        // Failed hook close deliberately supplies a null cause to CRObserve.
        c = Frame("coverage-a"); Set(c, "Turn", "7"); NativeObserve(c, null);
        Check(((IList)Hooks()["observedVisibleTypes"]).Count == 1, "failed execution snapshot cannot validate a new hook type");
        Observe(null);
        Check(((IList)Hooks()["observedVisibleTypes"]).Count == 1, "temporary unreadable state does not erase already observed evidence");
        for (int i = 0; i < 2050; i++) { c = Frame("coverage-a"); Set(c, "Turn", (i + 8).ToString()); Observe(c); }
        Check((bool)Events(0, 1)["historyGap"] && ((IList)Hooks()["observedVisibleTypes"]).Count == 1, "bounded journal eviction retains explicit stream evidence");
        var future = Events((long)Get(instance, "_combatEventId") + 1, 1);
        Check(!(bool)future["ok"] && (string)future["errorCode"] == "EVENT_CURSOR_AHEAD" && future.ContainsKey("latestEventId"), "future cursor rejected instead of silently skipping subsequent events");
        Check((string)Events(-1, 1)["errorCode"] == "INVALID_EVENT_CURSOR", "negative native event cursor rejected");
        long lastId = (long)Get(instance, "_combatEventId");
        Check(((IList)Events(lastId, 1)["events"]).Count == 0 && !(bool)Events(lastId, 1)["hasMore"], "cursor at current tail remains an ordinary empty page");
        Observe(Frame("coverage-b"));
        Check(((IList)Hooks()["observedVisibleTypes"]).Count == 0 && (string)Hooks()["installationStatus"] == "complete", "new game clears stream evidence while retaining process installation");
        var move = Activator.CreateInstance(cause, true); Set(move, "VisibleNativeAction", true); Set(move, "ActionType", "MoveAction");
        c = Frame("coverage-b"); Set(c, "Turn", "5"); NativeObserve(c, move);
        Check(((IList)Hooks()["observedVisibleTypes"]).Count == 1, "new stream can independently accumulate visible evidence");
        c = Frame("coverage-b"); Set(c, "Me", "2"); Observe(c);
        Check(((IList)Hooks()["observedVisibleTypes"]).Count == 0, "seat change clears former seat native evidence");
        host.GetMethod("CRRegisterHookInstallation", F).Invoke(instance, new object[] { "MoveAction", "signature_unavailable" });
        Check((string)Hooks()["installationStatus"] == "partial" && (int)Hooks()["installedTypeCount"] == names.Length - 1, "missing Execute signature prevents false complete installation");
        host.GetMethod("CRRegisterHookInstallation", F).Invoke(instance, new object[] { "MoveAction", "signature_unavailable" });
        Check((int)Get(instance, "_combatHookFailed") == 1, "repeated installation status does not inflate failure counters");
    }
}

// Native-shaped standalone test fixtures only; never include these in the production DLL.
internal sealed class InfiltrationRewardAction { }
internal sealed class TrainAction
{
    public string PlayerId = "1", Type = "Dagger";
    public object trainActionAborted = false;
}
internal sealed class CombatActionStackFixture
{ public CombatActionStackSpy ActionStack = new CombatActionStackSpy(); }
internal sealed class CombatActionStackSpy : IEnumerable
{
    internal readonly List<object> Values = new List<object>();
    internal readonly List<int> ReadIndices = new List<int>();
    internal int EnumerationAttempts;
    public int Count { get { return Values.Count; } }
    public object this[int index] { get { ReadIndices.Add(index); return Values[index]; } }
    public IEnumerator GetEnumerator() { EnumerationAttempts++; throw new InvalidOperationException("old action stack enumeration forbidden"); }
}
internal sealed class CombatUnreadableStackFixture
{ public CombatUnreadableStack ActionStack = new CombatUnreadableStack(); }
internal sealed class CombatUnreadableStack
{ public int Count { get { throw new InvalidOperationException("native Count unavailable"); } } }
