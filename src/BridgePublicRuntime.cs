// Player-public information channels. No DSH, screenshots, AI state, or winner computation.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private static BridgeMod _publicRuntimeOwner;
        private static object _publicResultState;
        private static string _publicResultEpisode;
        private static byte? _publicResultWinner;
        private static bool? _publicResultWon;
        private static byte? _publicResultControlled;
        private static bool _publicResultHookInstalled;
        private static bool _publicRuntimeInstalling;
        [ThreadStatic] private static bool _publicCallbackActive;
        private static bool _publicInstallCallbackNoted;
        private static bool _publicReentryNoted;
        private static bool _publicAliveEntryNoted;
        private static bool _publicResultEntryNoted;
        private sealed class PublicAliveObservation
        { internal string Episode; internal int? Commands; internal bool Alive; internal string Turn; }
        private static readonly Dictionary<byte, PublicAliveObservation> _publicAlive = new Dictionary<byte, PublicAliveObservation>();
        internal sealed class PublicDiplomacyObservation
        {
            internal object NativeAction;
            internal object NativeCommand;
            internal long Ticket;
            internal string Episode, Kind;
            internal byte Me, Other;
            internal int? StarsBefore, StarsAfter, Cost;
            internal bool? QualifierBefore, QualifierAfter, Accepted;
            internal bool? TriggerConsumed;
            internal bool ResponseAuthorized;
            internal bool Complete;
            internal bool? IncomingRequestBefore;
            internal string OwnStateBefore, OwnStateAfter;
        }
        private readonly List<PublicDiplomacyObservation> _publicDiplomacyActive = new List<PublicDiplomacyObservation>();
        private readonly Dictionary<long, PublicDiplomacyObservation> _publicDiplomacyProof = new Dictionary<long, PublicDiplomacyObservation>();
        private readonly HashSet<string> _publicPeaceReceipts = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<long, string> _publicResponseExpectations = new Dictionary<long, string>();
        private string PublicReceiptKey(object gs, byte me, byte other)
        { return EpisodeId(gs) + ":" + me.ToString(CultureInfo.InvariantCulture) + ":" + other.ToString(CultureInfo.InvariantCulture); }

        // Called only by the audited native Execute hooks. First test the actor before reading
        // any target field; other players' actions have no public diplomacy proof or journal.
        private void PublicNativeActionPrefix(object gs, object nativeAction, string kind)
        {
            string operation = PublicDiplomacyActionOperation(kind);
            if (operation == null) { return; }
            var identity = ResolveControlledIdentity(gs);
            if (!identity.Verified || PublicInt(MemberWithAccessor(nativeAction, "PlayerId")) != identity.Id) { return; }
            var other = PublicInt(MemberWithAccessor(nativeAction,
                kind == "PeaceRequestResponseAction" || kind == "ReceiveDiplomacyMessageAction" ? "SenderId" : "OpponentId"));
            if (!other.HasValue || other <= 0 || other >= 255 || other == identity.Id) { return; }
            var ps = PlayerStateOf(gs, identity.Id);
            var known = PublicKnownPlayers(ps, identity.Id);
            if (known == null || !known.Contains((byte)other.Value)) { return; }
            if (operation == "receive_message")
            {
                if (SafeScalar(MemberWithAccessor(nativeAction, "MessageType")) != "PeaceRequest") { return; }
                _publicDiplomacyActive.Add(new PublicDiplomacyObservation { NativeAction = nativeAction,
                    Episode = EpisodeId(gs), Kind = kind, Me = identity.Id, Other = (byte)other.Value,
                    IncomingRequestBefore = PublicIncomingPeaceRequest(ps, (byte)other.Value) });
                if (_publicDiplomacyActive.Count > 128) { _publicDiplomacyActive.RemoveAt(0); }
                return;
            }
            CombatTicket ticket = null;
            foreach (var candidate in _combatTickets.Values)
            {
                if (candidate.Stream != _combatStream || candidate.FinalFrame != null || candidate.Accepted == false
                    || candidate.Before == null || candidate.Before.Episode != EpisodeId(gs)
                    || candidate.Before.Me != identity.Id.ToString(CultureInfo.InvariantCulture)
                    || candidate.Action != operation || candidate.Arg != other.Value.ToString(CultureInfo.InvariantCulture)) { continue; }
                if (ticket == null || candidate.Id > ticket.Id) { ticket = candidate; }
            }
            if (ticket == null) { return; }
            var proof = new PublicDiplomacyObservation { NativeAction = nativeAction, Ticket = ticket.Id,
                Episode = EpisodeId(gs), Kind = kind, Me = identity.Id, Other = (byte)other.Value,
                StarsBefore = PublicInt(MemberWithAccessor(ps, "Currency")),
                OwnStateBefore = PublicExistingRelationState(ps, (byte)other.Value) };
            if (operation == "peace_response")
            {
                var accepted = MemberWithAccessor(nativeAction, "Accepted"); proof.Accepted = accepted is bool ? (bool?)accepted : null;
                string expected;
                proof.ResponseAuthorized = proof.Accepted.HasValue && _publicResponseExpectations.TryGetValue(ticket.RequestId, out expected)
                    && expected == PublicReceiptKey(gs, proof.Me, proof.Other) + ":" + proof.Accepted.Value.ToString();
            }
            if (operation == "establish_embassy" || operation == "destroy_embassy")
            {
                string error;
                proof.NativeCommand = PublicDiplomacyCommand(gs, proof.Me, proof.Other, operation, true);
                proof.QualifierBefore = PublicCommandValid(proof.NativeCommand, gs, out error);
            }
            _publicDiplomacyActive.Add(proof);
            if (_publicDiplomacyActive.Count > 128) { _publicDiplomacyActive.RemoveAt(0); }
        }

        private static string PublicExistingRelationState(object player, byte other)
        { return SafeScalar(MemberWithAccessor(PublicDictionaryValue(MemberWithAccessor(player, "relations"), other), "State")); }

        private void PublicNativeActionComplete(object gs, object nativeAction, string kind)
        {
            if (PublicDiplomacyActionOperation(kind) == null) { return; }
            for (int i = _publicDiplomacyActive.Count - 1; i >= 0; i--)
            {
                var proof = _publicDiplomacyActive[i];
                if (proof.Kind != kind || !CRSameNative(proof.NativeAction, nativeAction)) { continue; }
                _publicDiplomacyActive.RemoveAt(i);
                if (proof.Episode != EpisodeId(gs)) { return; }
                var identity = ResolveControlledIdentity(gs);
                if (!identity.Verified || identity.Id != proof.Me) { return; }
                var ps = PlayerStateOf(gs, proof.Me);
                if (kind == "ReceiveDiplomacyMessageAction")
                {
                    string triggerReason;
                    if (proof.IncomingRequestBefore == true
                        && PublicActivePeaceTrigger(PendingTriggerList(gs), proof.Me, proof.Other, out triggerReason) == true)
                    { _publicPeaceReceipts.Add(PublicReceiptKey(gs, proof.Me, proof.Other)); }
                    return;
                }
                if (kind == "PeaceRequestResponseAction")
                {
                    _publicPeaceReceipts.Remove(PublicReceiptKey(gs, proof.Me, proof.Other));
                    proof.TriggerConsumed = PublicPeaceTriggerConsumed(PendingTriggerList(gs), proof.Me, proof.Other);
                }
                proof.StarsAfter = PublicInt(MemberWithAccessor(ps, "Currency"));
                proof.OwnStateAfter = PublicExistingRelationState(ps, proof.Other);
                if (kind == "EstablishEmbassyAction" || kind == "DestroyEmbassyAction")
                {
                    if (kind == "EstablishEmbassyAction") proof.Cost = PublicInt(MemberWithAccessor(nativeAction, "embassyCost"));
                    string error;
                    proof.QualifierAfter = PublicCommandValid(proof.NativeCommand, gs, out error);
                }
                proof.Complete = true;
                _publicDiplomacyProof[proof.Ticket] = proof;
                if (_publicDiplomacyProof.Count > 128)
                { var keys = new List<long>(_publicDiplomacyProof.Keys); keys.Sort(); _publicDiplomacyProof.Remove(keys[0]); }
                return;
            }
        }

        // null leaves non-diplomacy settlement untouched. Native action completion is necessary
        // here; neither a command acknowledgement nor an unrelated currency change is proof.
        private string PublicSettlementConfirmation(long ticket, object gs, out string basis)
        {
            basis = null;
            CombatTicket tracked;
            if (!_combatTickets.TryGetValue(ticket, out tracked) || !PublicDiplomacyAdapterSupported(tracked.Action)) { return null; }
            if (tracked.Stream != _combatStream || tracked.Before == null || tracked.Before.Episode != EpisodeId(gs))
            { basis = "diplomacy ticket belongs to another stream or episode"; return "unknown"; }
            PublicDiplomacyObservation proof;
            if (!_publicDiplomacyProof.TryGetValue(ticket, out proof) || !proof.Complete || proof.Episode != EpisodeId(gs)
                || proof.Ticket != ticket || proof.Me.ToString(CultureInfo.InvariantCulture) != tracked.Before.Me
                || proof.Other.ToString(CultureInfo.InvariantCulture) != tracked.Arg)
            { basis = "no successful actor/target-correlated native diplomacy action observed for this ticket"; return "unknown"; }
            return PublicDiplomacyProofConfirmation(proof, tracked.Action, out basis);
        }

        internal static string PublicDiplomacyProofConfirmation(PublicDiplomacyObservation proof, string operation, out string basis)
        {
            if (proof == null || !proof.Complete)
            { basis = "no successfully completed native diplomacy action"; return "unknown"; }
            if (!PublicDiplomacyAdapterSupported(operation) || PublicDiplomacyActionOperation(proof.Kind) != operation)
            { basis = "native diplomacy action kind does not match requested operation"; return "unknown"; }
            basis = "native " + proof.Kind + " Execute completed; actor=" + proof.Me + "; target=" + proof.Other;
            if (operation == "peace_request")
            { basis += "; PeaceTreatyAction sends request only; does not prove opponent acceptance or Peace state"; return "confirmed"; }
            if (operation == "break_peace")
            {
                if (proof.OwnStateBefore == null || proof.OwnStateAfter == null)
                { basis += "; own relationship before/after unavailable"; return "unknown"; }
                basis += "; own state " + proof.OwnStateBefore + "->" + proof.OwnStateAfter
                    + "; CoolDown is the native post-break state since version 80; not immediate War";
                return proof.OwnStateBefore == "Peace" && (proof.OwnStateAfter == "CoolDown" || proof.OwnStateAfter == "War")
                    ? "confirmed" : "not_confirmed";
            }
            if (operation == "destroy_embassy")
            {
                if (!proof.QualifierBefore.HasValue || !proof.QualifierAfter.HasValue)
                { basis += "; same native menu command qualification unavailable"; return "unknown"; }
                basis += "; same native menu command qualification " + proof.QualifierBefore + "->" + proof.QualifierAfter
                    + "; no opponent relation or economic state read";
                return proof.QualifierBefore == true && proof.QualifierAfter == false ? "confirmed" : "not_confirmed";
            }
            if (operation == "establish_embassy")
            {
                if (!proof.StarsBefore.HasValue || !proof.StarsAfter.HasValue || !proof.Cost.HasValue
                    || !proof.QualifierBefore.HasValue || !proof.QualifierAfter.HasValue)
                { basis += "; currency/cost/native qualification unreadable"; return "unknown"; }
                bool confirmed = proof.Cost.Value > 0 && proof.StarsBefore.Value - proof.StarsAfter.Value == proof.Cost.Value
                    && proof.QualifierBefore == true && proof.QualifierAfter == false;
                basis += "; own currency " + proof.StarsBefore + "->" + proof.StarsAfter + "; native action cost=" + proof.Cost
                    + "; native menu command qualification " + proof.QualifierBefore + "->" + proof.QualifierAfter
                    + "; no opponent relation read";
                return confirmed ? "confirmed" : "not_confirmed";
            }
            if (!proof.ResponseAuthorized || !proof.Accepted.HasValue || proof.OwnStateAfter == null || !proof.TriggerConsumed.HasValue)
            { basis += "; explicit correlated response authorization, accepted flag, own relationship or consumed trigger unavailable"; return "unknown"; }
            // Response action executes after its command already popped the matching trigger.
            // Its actual Accepted field and only our existing relation determine the outcome.
            bool peace = proof.OwnStateAfter == "Peace";
            basis += "; accepted=" + proof.Accepted + "; own state " + (proof.OwnStateBefore ?? "unknown") + "->" + proof.OwnStateAfter
                + "; correlated own pending request consumed=" + proof.TriggerConsumed;
            return proof.TriggerConsumed == true && (proof.Accepted.Value ? peace : !peace) ? "confirmed" : "not_confirmed";
        }

        private void InstallPublicRuntimeHooks()
        {
            _publicRuntimeOwner = this;
            _publicResultHookInstalled = false;
            _publicRuntimeInstalling = true;
            Log("public-hooks install.begin");
            try
            {
                Log("public-hooks resolve GameManager.before");
                var t = FindGameType("GameManager");
                Log("public-hooks resolve GameManager.after found=" + (t != null));
                Log("public-hooks resolve ScoreDetails.before");
                var scoreType = FindGameType("ScoreDetails");
                Log("public-hooks resolve ScoreDetails.after found=" + (scoreType != null));
                Log("public-hooks lookup MatchEnded.before");
                var method = t == null || scoreType == null ? null : t.GetMethod("MatchEnded", BindingFlags.Static | BindingFlags.Public,
                    null, new[] { typeof(bool), scoreType, typeof(byte) }, null);
                Log("public-hooks lookup MatchEnded.after found=" + (method != null));
                if (HarmonyInstance == null) { return; }
                if (method != null)
                {
                    var hook = typeof(BridgeMod).GetMethod("PublicMatchEndedPrefix", BindingFlags.Static | BindingFlags.NonPublic);
                    Log("public-hooks patch MatchEnded.before");
                    _publicResultHookInstalled = HarmonyInstance.Patch(method, new HarmonyMethod(hook), null, null, null, null) != null;
                    Log("public-hooks patch MatchEnded.after installed=" + _publicResultHookInstalled);
                }
                Log("public-hooks lookup IsAlive.before");
                var alive = FindStaticUnique(FindGameType("PlayerExtensions"), "IsAlive", new[] { "PlayerState", "GameState" }, "Boolean");
                Log("public-hooks lookup IsAlive.after found=" + (alive != null));
                if (alive != null)
                {
                    var hook = typeof(BridgeMod).GetMethod("PublicAlivePostfix", BindingFlags.Static | BindingFlags.NonPublic);
                    Log("public-hooks patch IsAlive.before");
                    bool installed = HarmonyInstance.Patch(alive, null, new HarmonyMethod(hook), null, null, null) != null;
                    Log("public-hooks patch IsAlive.after installed=" + installed);
                }
            }
            catch (Exception ex) { Log("public-result hook unavailable: " + (ex.InnerException ?? ex).Message); }
            finally { _publicRuntimeInstalling = false; Log("public-hooks install.end"); }
        }

        private static bool EnterPublicCallback(string name)
        {
            if (_publicRuntimeInstalling)
            {
                if (!_publicInstallCallbackNoted) { _publicInstallCallbackNoted = true; Log("public-hooks callback ignored while installing: " + name); }
                return false;
            }
            if (_publicCallbackActive)
            {
                if (!_publicReentryNoted) { _publicReentryNoted = true; Log("public-hooks callback reentry suppressed: " + name); }
                return false;
            }
            _publicCallbackActive = true;
            return true;
        }

        // Observe the game's natural IsAlive evaluations, do not ask it to recalculate anything.
        private static void PublicAlivePostfix(object __0, object __1, bool __result)
        {
            if (!EnterPublicCallback("IsAlive")) { return; }
            try
            {
                if (!_publicAliveEntryNoted) { _publicAliveEntryNoted = true; Log("public-hooks IsAlive callback.first-entry"); }
                var owner = _publicRuntimeOwner;
                if (owner == null || __1 == null) { return; }
                if (!owner.PrivateSamplingAllowed(__1)) return;
                var live = owner.GameStateInstance();
                var sourceNative = PolytopiaPublicMarkers.PublicMarkerHooks.StableNativeIdentity(__1);
                var liveNative = PolytopiaPublicMarkers.PublicMarkerHooks.StableNativeIdentity(live);
                if (!(sourceNative != null && liveNative != null ? object.Equals(sourceNative, liveNative) : ReferenceEquals(__1, live))) { return; }
                var identity = owner.ResolveControlledIdentity(__1);
                if (!identity.Verified) { return; }
                var known = PublicKnownPlayers(owner.PlayerStateOf(__1, identity.Id), identity.Id);
                var id = PublicInt(MemberWithAccessor(__0, "Id"));
                if (!id.HasValue || id.Value <= 0 || id.Value >= 255 || known == null || !known.Contains((byte)id.Value)) { return; }
                _publicAlive[(byte)id.Value] = new PublicAliveObservation { Episode = owner.EpisodeId(__1),
                    Commands = PublicInt(MemberWithAccessor(MemberWithAccessor(__1, "CommandStack"), "Count")),
                    Turn = SafeScalar(MemberWithAccessor(__1, "CurrentTurn")), Alive = __result };
            }
            catch { }
            finally { _publicCallbackActive = false; }
        }

        // This observes an outcome the normal game already announced. It never invokes MatchEnded.
        private static void PublicMatchEndedPrefix(bool __0, object __1, byte __2)
        {
            if (!EnterPublicCallback("MatchEnded")) { return; }
            try
            {
                if (!_publicResultEntryNoted) { _publicResultEntryNoted = true; Log("public-hooks MatchEnded callback.first-entry"); }
                var owner = _publicRuntimeOwner;
                var gs = owner == null ? null : owner.GameStateInstance();
                if (gs == null) { return; }
                if (!owner.PrivateSamplingAllowed(gs)) return;
                var identity = owner.ResolveControlledIdentity(gs);
                if (!identity.Verified) { return; }
                _publicResultState = gs;
                _publicResultEpisode = owner.EpisodeId(gs);
                _publicResultWinner = __2 == 0 || __2 == 255 ? (byte?)null : __2;
                _publicResultWon = __0;
                _publicResultControlled = identity.Id;
                owner.CaptureNativeFinalScore(gs, identity.Id, __1, __0, _publicResultWinner);
            }
            catch { }
            finally { _publicCallbackActive = false; }
        }

        private static string PublicUnavailable(string schema, string reason)
        { return new JsonObj().Bool("ok", false).Str("schema", schema).Str("status", "unavailable").Str("reason", reason).Close(); }

        private static int? PublicInt(object value)
        {
            return NativeInt32(value);
        }

        private static string PublicNumber(object value)
        {
            return ResultsNumber(value);
        }

        // IL2CPP Dictionary is not CLR IEnumerable. Its Item takes a byte KEY, not a
        // list index: the generic AsEnumerable index fallback cannot read relation pairs.
        // Confirm existence before invoking the read-only native getter; never GetRelation.
        private static object PublicDictionaryValue(object dictionary, byte key)
        {
            if (dictionary == null) { return null; }
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                var type = dictionary.GetType();
                var contains = type.GetMethod("ContainsKey", flags, null, new[] { typeof(byte) }, null);
                if (contains != null)
                {
                    if (contains.ReturnType != typeof(bool) || !(bool)contains.Invoke(dictionary, new object[] { key })) { return null; }
                    var getter = type.GetMethod("get_Item", flags, null, new[] { typeof(byte) }, null);
                    if (getter == null || getter.ReturnType == typeof(void)) { return null; }
                    return getter.Invoke(dictionary, new object[] { key });
                }
            }
            catch { return null; } // A failed existence/read check remains unknown; no fallback mutation.
            // Compatibility for existing enumerable Key/Value fixtures without dictionary methods.
            var list = ReadNativeList(dictionary);
            if (list == null) { return null; }
            foreach (var pair in list)
            { if (pair != null && PublicInt(MemberWithAccessor(pair, "Key")) == key) { return MemberWithAccessor(pair, "Value"); } }
            return null;
        }

        private static HashSet<byte> PublicKnownPlayers(object player, byte me)
        {
            var raw = MemberWithAccessor(player, "knownPlayers");
            var list = ReadNativeList(raw);
            if (list == null) { return null; }
            var known = new HashSet<byte>(); known.Add(me);
            foreach (var value in list)
            {
                var id = PublicInt(value);
                if (!id.HasValue || id.Value < 0 || id.Value > 255) return null;
                // Existing native contact lists may contain Nature/unknown sentinels.
                if (id.Value == 0 || id.Value == 255) continue;
                known.Add((byte)id.Value);
            }
            return known;
        }

        private string GameInfoPublic()
        {
            var gs = GameStateInstance();
            if (gs == null) { return PublicUnavailable("game-info-1", "no active game"); }
            if (!PrivateSamplingAllowed(gs)) return PausedSeatInfo(gs);
            var identity = ResolveControlledIdentity(gs);
            if (!identity.Verified) { return PublicUnavailable("game-info-1", "controlled identity unavailable"); }
            var ps = PlayerStateOf(gs, identity.Id);
            var known = PublicKnownPlayers(ps, identity.Id);
            var settings = MemberWithAccessor(gs, "Settings");
            // CommandStack.Count alone is insufficient while a command is still resolving: several
            // deaths/captures can occur within that one entry. Historical observations remain
            // available, but become a current status only after the shared settlement guard clears.
            bool settled = Json.GetString(CombatStateJson(gs), "settlementStatus") == "complete";
            var o = new JsonObj().Bool("ok", true).Str("schema", "game-info-1").Str("episodeId", EpisodeId(gs));
            o.Raw("controlledPlayerId", identity.Id.ToString(CultureInfo.InvariantCulture));
            o.NullOrStr("phase", SafeScalar(MemberWithAccessor(gs, "CurrentState")));
            o.Raw("turn", PublicNumber(MemberWithAccessor(gs, "CurrentTurn")));
            var set = new JsonObj();
            foreach (var name in new[] { "GameType", "BaseGameMode", "RulesGameMode", "Difficulty", "mapPreset" })
            { set.NullOrStr(name, SafeScalar(MemberWithAccessor(settings, name))); }
            foreach (var name in new[] { "OpponentCount", "MapSize", "TimeLimit" })
            { set.Raw(name, PublicNumber(MemberWithAccessor(settings, name))); }
            o.Raw("settings", settings == null ? "null" : set.Close());
            var rules = MemberWithAccessor(settings, "rules");
            var ruleJson = new JsonObj();
            foreach (var name in new[] { "TurnLimit", "ScoreLimit" })
            { ruleJson.Raw(name, PublicNumber(MemberWithAccessor(rules, name))); }
            foreach (var name in new[] { "WinByCapital", "WinByExtermination" })
            { ruleJson.NullableBool(name, ReadBoolMember(rules, name)); }
            ruleJson.NullOrStr("PlayerDeathCondition", SafeScalar(MemberWithAccessor(rules, "PlayerDeathCondition")));
            o.Raw("victoryConditions", rules == null ? "null" : ruleJson.Close());
            var rows = new JsonArr();
            var players = ReadNativeList(MemberWithAccessor(gs, "PlayerStates"));
            if (players != null && known != null)
            {
                foreach (var player in players)
                {
                    var id = PublicInt(MemberWithAccessor(player, "Id"));
                    if (!id.HasValue || id.Value <= 0 || id.Value >= 255 || !known.Contains((byte)id.Value)) { continue; }
                    var wiped = PublicInt(MemberWithAccessor(player, "wipedAtCommandIndex"));
                    var resigned = PublicInt(MemberWithAccessor(player, "resignedTurn"));
                    bool? eliminated = wiped.HasValue && wiped.Value >= 0 || resigned.HasValue && resigned.Value >= 0
                        ? (bool?)true : null;
                    PublicAliveObservation observation;
                    bool hasObservation = _publicAlive.TryGetValue((byte)id.Value, out observation)
                        && observation.Episode == EpisodeId(gs);
                    int? commandCount = PublicInt(MemberWithAccessor(MemberWithAccessor(gs, "CommandStack"), "Count"));
                    bool fresh = settled && hasObservation && observation.Commands.HasValue && commandCount == observation.Commands
                        && observation.Turn == SafeScalar(MemberWithAccessor(gs, "CurrentTurn"));
                    bool? alive = eliminated == true ? (bool?)false : (fresh ? (bool?)observation.Alive : null);
                    if (fresh && !observation.Alive) { eliminated = true; }
                    else if (fresh && observation.Alive && eliminated != true) { eliminated = false; }
                    var row = new JsonObj().Raw("playerId", id.Value.ToString(CultureInfo.InvariantCulture));
                    row.NullOrStr("tribe", SafeScalar(MemberWithAccessor(player, "tribe")));
                    row.NullableBool("eliminated", eliminated).NullableBool("alive", alive);
                    row.Str("statusAuthority", fresh ? "naturally observed native IsAlive result at same command count and turn"
                        : (eliminated == true ? "native wipe/resign marker" : "unavailable; roster presence is not proof of survival"));
                    row.NullableBool("lastObservedAlive", hasObservation ? (bool?)observation.Alive : null);
                    row.NullOrStr("lastObservedTurn", hasObservation ? observation.Turn : null);
                    row.Raw("wipedAtCommandIndex", PublicNumber(MemberWithAccessor(player, "wipedAtCommandIndex")));
                    row.Raw("resignedTurn", PublicNumber(MemberWithAccessor(player, "resignedTurn")));
                    rows.Raw(row.Close());
                }
            }
            o.Raw("players", known == null || players == null ? "null" : rows.Close());
            bool same = _publicResultState != null && _publicResultEpisode == EpisodeId(gs)
                && _publicResultControlled == identity.Id;
            var result = new JsonObj().Bool("observerInstalled", _publicResultHookInstalled);
            result.Str("status", same ? "observed" : "unavailable");
            result.Raw("winnerId", same && _publicResultWinner.HasValue ? _publicResultWinner.Value.ToString(CultureInfo.InvariantCulture) : "null");
            result.NullableBool("controlledPlayerWon", same ? _publicResultWon : null);
            result.Str("authority", "normal GameManager.MatchEnded callback; no terminal computation");
            if (!same) { result.Str("reason", "no normal MatchEnded callback observed for this episode; Ended alone does not identify winner"); }
            o.Raw("result", result.Close());
            return o.Close();
        }

        private static string PublicRelationCore(object me, byte other)
        {
            var relation = PublicDictionaryValue(MemberWithAccessor(me, "relations"), other);
            var o = new JsonObj();
            o.NullOrStr("state", SafeScalar(MemberWithAccessor(relation, "State")));
            o.Raw("embassyLevel", PublicNumber(MemberWithAccessor(relation, "EmbassyLevel")));
            o.Raw("embassyBuildTurn", PublicNumber(MemberWithAccessor(relation, "EmbassyBuildTurn")));
            o.Str("embassyDirection", "opponent embassy in controlled player's capital; not controlled player's outgoing embassy");
            o.Str("authority", "existing controlled PlayerState.relations entry");
            var messages = new JsonArr();
            var raw = MemberWithAccessor(me, "messages");
            var list = ReadNativeList(raw);
            if (list != null)
            {
                foreach (var message in list)
                {
                    if (PublicInt(MemberWithAccessor(message, "Sender")) != other) { continue; }
                    messages.Raw(new JsonObj().NullOrStr("type", SafeScalar(MemberWithAccessor(message, "Type")))
                        .Raw("senderId", other.ToString(CultureInfo.InvariantCulture)).Close());
                }
            }
            o.Raw("incomingMessages", list == null ? "null" : messages.Close());
            return o.Close();
        }

        // Peace request coordinates concern our own city. Embassy validation instead requires
        // an opponent city/unit: select only a unit returned by the player's native visibility
        // filter, never raw opponent city ownership or a capital from PlayerState.
        private static bool PublicDiplomacyTargetMatches(object visibleUnit, byte other)
        { return visibleUnit != null && PublicInt(MemberWithAccessor(visibleUnit, "owner")) == other; }

        private object PublicEmbassyCoordinates(object gs, byte me, byte other)
        {
            var visible = URUnits(gs, me);
            if (visible == null) { return null; }
            foreach (var entry in visible)
            {
                if (!PublicDiplomacyTargetMatches(entry.Unit, other)) { continue; }
                var coordinates = MemberWithAccessor(entry.Tile, "coordinates");
                if (coordinates != null) { return coordinates; }
            }
            return null;
        }

        // Native PeaceRequestResponseCommand.IsValid permits an empty trigger list. Its Execute
        // can nevertheless establish peace, so player-equivalent response eligibility needs
        // our real incoming request plus our currently active (last) correlated trigger.
        // ReceiveDiplomacyMessageAction normally removes the message as it creates the trigger:
        // a same-episode natural receipt observation preserves that actual request evidence.
        private static bool? PublicIncomingPeaceRequest(object player, byte other)
        {
            var raw = MemberWithAccessor(player, "messages");
            var messages = ReadNativeList(raw);
            if (messages == null) { return null; }
            bool request = false, messagesUnknown = false;
            foreach (var message in messages)
            {
                var sender = PublicInt(MemberWithAccessor(message, "Sender"));
                var type = SafeScalar(MemberWithAccessor(message, "Type"));
                if (!sender.HasValue || sender.Value <= 0 || sender.Value >= 255 || type == null) { messagesUnknown = true; continue; }
                if (sender == other && type == "PeaceRequest") { request = true; }
            }
            return request ? (bool?)true : messagesUnknown ? null : (bool?)false;
        }

        private static bool? PublicPeaceResponseAvailable(object player, IEnumerable triggers,
            byte me, byte other, bool observedReceipt, out string reason)
        {
            reason = null;
            var request = PublicIncomingPeaceRequest(player, other);
            if (request != true && !observedReceipt)
            { reason = request == null ? "owned incoming request unavailable and no natural receipt observed"
                : "no incoming PeaceRequest or natural receipt from target tribe"; return request; }
            return PublicActivePeaceTrigger(triggers, me, other, out reason);
        }

        private static bool? PublicActivePeaceTrigger(IEnumerable triggers, byte me, byte other, out string reason)
        {
            reason = null;
            if (triggers == null) { reason = "owned pending command triggers unavailable"; return null; }
            object active = null;
            foreach (var trigger in triggers)
            {
                var playerId = PublicInt(MemberWithAccessor(trigger, "playerId"));
                if (!playerId.HasValue) { reason = "pending trigger playerId unavailable"; return null; }
                if (playerId == me) { active = trigger; }
            }
            if (active == null) { reason = "no pending PeaceRequest trigger for controlled player"; return false; }
            var activeType = SafeScalar(MemberWithAccessor(active, "type"));
            var opponent = PublicInt(MemberWithAccessor(active, "opponentId"));
            if (activeType == null || !opponent.HasValue) { reason = "active trigger fields unavailable"; return null; }
            if (activeType != "PeaceRequest" || opponent != other)
            { reason = "active pending trigger does not match target PeaceRequest"; return false; }
            return true;
        }

        private static bool? PublicPeaceTriggerConsumed(IEnumerable triggers, byte me, byte other)
        {
            if (triggers == null) { return null; }
            bool unknown = false;
            foreach (var trigger in triggers)
            {
                var player = PublicInt(MemberWithAccessor(trigger, "playerId"));
                if (!player.HasValue) { unknown = true; continue; }
                if (player != me) { continue; }
                var type = SafeScalar(MemberWithAccessor(trigger, "type"));
                var opponent = PublicInt(MemberWithAccessor(trigger, "opponentId"));
                if (type == null || !opponent.HasValue) { unknown = true; continue; }
                if (type == "PeaceRequest" && opponent == other) { return false; }
            }
            return unknown ? (bool?)null : true;
        }

        private object PublicDiplomacyCoordinates(object gs, byte me)
        {
            var map = MemberWithAccessor(gs, "Map");
            var raw = MemberWithAccessor(map, "Tiles");
            var tiles = raw == null ? null : AsEnumerable(raw);
            if (tiles != null)
            {
                foreach (var tile in tiles)
                {
                    if (PublicInt(MemberWithAccessor(tile, "owner")) != me) { continue; }
                    var imp = MemberWithAccessor(tile, "improvement");
                    if (SafeScalar(MemberWithAccessor(imp, "type")) != "City") { continue; }
                    return MemberWithAccessor(tile, "coordinates");
                }
            }
            return null;
        }

        private object PublicDiplomacyCommand(object gs, byte me, byte other, string operation, bool accept)
        {
            if (operation != "peace_response")
                return PublicDiplomacySelect(PublicDiplomacyMenu(gs, PlayerStateOf(gs, me), other), operation, me, other);
            var type = FindGameType("PeaceRequestResponseCommand");
            if (type == null) { return null; }
            try
            {
                foreach (var ctor in type.GetConstructors())
                {
                    var pars = ctor.GetParameters();
                    if (pars.Length == 3 && pars[0].ParameterType == typeof(byte) && pars[1].ParameterType == typeof(byte)
                        && pars[2].ParameterType == typeof(bool))
                    { return ctor.Invoke(new object[] { me, other, accept }); }
                }
            }
            catch { }
            return null;
        }

        private static bool? PublicCommandValid(object command, object gs, out string error)
        { return NativeCommandValidity(command, gs, out error); }

        private string TribeRelationsPublic()
        {
            var gs = GameStateInstance();
            if (gs == null) { return PublicUnavailable("tribe-relations-1", "no active game"); }
            var identity = ResolveControlledIdentity(gs);
            if (!identity.Verified) { return PublicUnavailable("tribe-relations-1", "controlled identity unavailable"); }
            var ps = PlayerStateOf(gs, identity.Id);
            var known = PublicKnownPlayers(ps, identity.Id);
            if (known == null) { return PublicUnavailable("tribe-relations-1", "known players unavailable"); }
            var popupType = FindGameType("PopupManager");
            var currentPopup = popupType == null ? null : InvokeStatic(popupType, "GetCurrentPopup");
            var playerPopupType = FindGameType("PlayerInfoPopup");
            var popup = currentPopup == null || playerPopupType == null ? null
                : GetComponentOfType(MemberWithAccessor(currentPopup, "gameObject"), playerPopupType);
            var shownPlayer = MemberWithAccessor(popup, "player");
            int? shownId = popup != null && ReadActive(popup) == "true" ? PublicInt(MemberWithAccessor(shownPlayer, "Id")) : null;
            var rows = new JsonArr();
            var logic = GameLogicDataOf(gs);
            var diplomacy = MemberWithAccessor(logic, "diplomacyData");
            object embassyCost = null;
            try
            {
                var costMethod = diplomacy == null ? null : diplomacy.GetType().GetMethod("GetEmbassyCostForPlayer", new[] { typeof(byte), gs.GetType() });
                if (costMethod != null) { embassyCost = costMethod.Invoke(diplomacy, new object[] { identity.Id, gs }); }
            }
            catch { }
            foreach (var id in known)
            {
                if (id == identity.Id) { continue; }
                var row = new JsonObj().Raw("playerId", id.ToString(CultureInfo.InvariantCulture));
                row.Raw("relationship", PublicRelationCore(ps, id));
                row.Str("attitudeDirection", "opponent toward controlled player, as shown in PlayerInfoPopup");
                bool freshInfo = shownId == id && _tribeInfoOwnedPopup != null && CRSameNative(popup, _tribeInfoOwnedPopup)
                    && _tribeInfoStamp == TribeInfoStamp(gs);
                row.NullOrStr("attitudeLabel", freshInfo ? VisibleControlText(MemberWithAccessor(popup, "relationText")) : null);
                row.NullOrStr("attitudeReasonsText", freshInfo ? VisibleControlText(MemberWithAccessor(popup, "opinionText")) : null);
                row.Raw("visibleDetails", freshInfo ? TribeInfoVisibleDetails(popup) : "null");
                var slider = MemberWithAccessor(popup, "relationSlider");
                row.Raw("attitudeSliderValue", freshInfo && slider != null && ReadActive(slider) == "true" ? PublicNumber(MemberWithAccessor(slider, "value")) : "null");
                row.Str("attitudeAuthority", shownId == id ? "active native PlayerInfoPopup text/slider" : "unavailable; tribe information popup not currently displaying this player");
                row.Raw("embassyCost", PublicNumber(embassyCost));
                var incomeWidget = shownId == id ? MemberWithAccessor(popup, "embassyIncome") : null;
                bool incomeVisible = incomeWidget != null && ReadActive(incomeWidget) == "true";
                row.Raw("embassyIncome", incomeVisible ? PublicNumber(MemberWithAccessor(incomeWidget, "Amount")) : "null")
                    .NullOrStr("embassyIncomeText", incomeVisible ? PublicText(MemberWithAccessor(incomeWidget, "label"), 0) : null)
                    .Str("embassyIncomeAuthority", incomeVisible ? "native visible ResourceWidget.Amount"
                        : "unavailable unless displayed by the native UI; no opponent economic state read");
                var menu = PublicDiplomacyMenu(gs, ps, id);
                row.Str("menuStatus", menu == null ? "unavailable" : "ok").Str("menuAuthority", DiplomacyMenuAuthority);
                row.Raw("operations", PublicDiplomacyOperations(gs, ps, identity.Id, id, menu)); rows.Raw(row.Close());
            }
            return new JsonObj().Bool("ok", true).Str("schema", "tribe-relations-1").Str("episodeId", EpisodeId(gs))
                .Raw("controlledPlayerId", identity.Id.ToString(CultureInfo.InvariantCulture)).Raw("tribes", rows.Close()).Close();
        }

        private string TribeDiplomacyPublic(string operation, string opponentArg, string acceptArg)
        {
            var gs = GameStateInstance();
            if (gs == null) { return ActionPreparationError("NO_ACTIVE_GAME", "no active game", "tribe-action-1"); }
            var identity = ResolveControlledIdentity(gs);
            byte other;
            if (!identity.Verified || !byte.TryParse(opponentArg, out other) || other == 0 || other == 255 || other == identity.Id)
            { return ActionPreparationError("INVALID_DIPLOMACY_TARGET", "invalid controlled identity or opponent", "tribe-action-1"); }
            var ps = PlayerStateOf(gs, identity.Id);
            var known = PublicKnownPlayers(ps, identity.Id);
            if (known == null || !known.Contains(other)) { return ActionPreparationError("TRIBE_NOT_ENCOUNTERED", "target tribe has not been encountered", "tribe-action-1"); }
            if (CurrentPlayerId(gs) != identity.Id) { return ActionPreparationError("NOT_YOUR_TURN", "not controlled player's turn", "tribe-action-1"); }
            bool accept = false;
            if (operation == "peace_response" && (string.IsNullOrEmpty(acceptArg) || !TryActionBoolean(acceptArg, false, out accept)))
            { return ActionPreparationError("INVALID_BOOLEAN", "peace_response requires explicit boolean accept", "tribe-action-1"); }
            if (operation == "peace_response")
            {
                string qualificationError;
                var qualification = PublicPeaceResponseAvailable(ps, PendingTriggerList(gs), identity.Id, other,
                    _publicPeaceReceipts.Contains(PublicReceiptKey(gs, identity.Id, other)), out qualificationError);
                if (qualification != true)
                { return PreDispatchRejection().Str("schema", "tribe-action-1")
                    .Str("status", qualification == false ? "rejected" : "unavailable")
                    .NullOrStr("reason", qualificationError).Close(); }
            }
            var command = PublicDiplomacyCommand(gs, identity.Id, other, operation, accept);
            if (command == null)
            { return ActionPreparationError("ACTION_NOT_IN_CURRENT_MENU", "action absent from current native menu, ambiguous, or execution adapter unavailable", "tribe-action-1"); }
            string error; var valid = PublicCommandValid(command, gs, out error);
            if (valid != true)
            { return PreDispatchRejection().Str("schema", "tribe-action-1").Str("status", valid == false ? "rejected" : "unavailable").NullOrStr("reason", error).Close(); }
            string before = PublicRelationCore(ps, other);
            var starsBefore = MemberWithAccessor(ps, "Currency");
            if (operation == "peace_response")
            {
                CapturePeaceResponseDisplay(gs, identity.Id, other);
                _publicResponseExpectations[_activeRequestId] = PublicReceiptKey(gs, identity.Id, other) + ":" + accept.ToString();
                if (_publicResponseExpectations.Count > 128)
                { var keys = new List<long>(_publicResponseExpectations.Keys); keys.Sort(); _publicResponseExpectations.Remove(keys[0]); }
            }
            var execution = ExecuteGameAction(operation, "", acceptArg ?? "", "", "", "", opponentArg, "", command);
            var after = PublicRelationCore(ps, other);
            bool executionOk = false;
            bool? accepted = null;
            string executionStatus = "unknown";
            try
            {
                var envelope = BridgeJsonCodec.Object(execution);
                object value;
                if (envelope.TryGetValue("ok", out value) && value is bool) { executionOk = (bool)value; }
                if (envelope.TryGetValue("accepted", out value) && value is bool) { accepted = (bool)value; }
                if (envelope.TryGetValue("status", out value) || envelope.TryGetValue("verdict", out value))
                { executionStatus = value as string ?? "unknown"; }
            }
            catch { }
            return new JsonObj().Bool("ok", executionOk).Str("schema", "tribe-action-1").Str("operation", operation)
                .NullableBool("accepted", accepted).Str("status", executionStatus)
                .Raw("opponentId", other.ToString(CultureInfo.InvariantCulture)).Raw("nativeExecution", execution)
                .Raw("before", before).Raw("after", after).Bool("visibleRelationshipChanged", before != after)
                .Raw("starsBefore", PublicNumber(starsBefore)).Raw("starsAfter", PublicNumber(MemberWithAccessor(ps, "Currency")))
                .Str("confirmationAuthority", "native execution has its own accepted/settlement status; request dispatch alone does not establish peace").Close();
        }

        private static string PublicText(object control, int depth)
        {
            if (control == null || depth > 2) { return null; }
            var text = control as string;
            if (text != null) { return text; }
            foreach (var field in new[] { "Text", "text" })
            { text = MemberWithAccessor(control, field) as string; if (!string.IsNullOrEmpty(text)) { return text; } }
            foreach (var field in new[] { "textField", "textfield", "textInContainer" })
            { text = PublicText(MemberWithAccessor(control, field), depth + 1); if (!string.IsNullOrEmpty(text)) { return text; } }
            return null;
        }

        private string PopupInfoPublic()
        {
            var type = FindGameType("PopupManager");
            if (type == null) { return PublicUnavailable("popup-info-1", "PopupManager unavailable"); }
            var showing = InvokeStatic(type, "get_PopupShowing");
            object popup;
            bool currentReadable = TryReadCurrentPopup(out popup);
            var o = new JsonObj().Bool("ok", true).Str("schema", "popup-info-1");
            o.NullableBool("showing", showing is bool ? (bool?)showing : null);
            if (!(showing is bool) || !(bool)showing || !currentReadable || popup == null)
            { return o.Str("status", showing is bool && !(bool)showing ? "none" : "unavailable").Close(); }
            o.Str("status", "observed").Str("popupType", popup.GetType().Name);
            o.NullOrStr("title", PublicText(MemberWithAccessor(popup, "headerField"), 0)
                ?? PublicText(MemberWithAccessor(popup, "header"), 0)
                ?? PublicText(MemberWithAccessor(popup, "Title"), 0));
            o.NullOrStr("body", PublicText(MemberWithAccessor(popup, "descriptionField"), 0)
                ?? PublicText(MemberWithAccessor(popup, "description"), 0)
                ?? PublicText(MemberWithAccessor(popup, "Description"), 0));
            var texts = new JsonArr(); int budget = 256;
            var go = MemberWithAccessor(popup, "gameObject");
            PublicPopupTexts(MemberWithAccessor(go, "transform"), "", 0, texts, ref budget);
            o.Raw("visibleTexts", texts.Close()).Bool("truncated", budget <= 0);
            o.Raw("buttons", PopupButtonsFor(popup));
            if (popup.GetType().Name == "RewardPopup") o.Raw("rewardDisplay", RewardDisplayPublic(popup));
            o.Str("textAuthority", "current native UI text controls, already localized; no popup callback invoked");
            o.Str("actualRewardsAuthority", "use city_options.pendingCityReward/reward effects and settled action state; notification text alone is not an effect confirmation");
            return o.Close();
        }

        private void PublicPopupTexts(object transform, string parentPath, int depth, JsonArr rows, ref int budget)
        {
            if (transform == null || depth > 10 || budget-- <= 0) { return; }
            var go = MemberWithAccessor(transform, "gameObject");
            if (!object.Equals(MemberWithAccessor(go, "activeInHierarchy"), true)) { return; }
            var path = parentPath + "/" + ObjectName(go);
            var raw = GetComponentList(go);
            var components = raw == null ? null : AsEnumerable(raw);
            if (components != null)
            {
                foreach (var component in components)
                {
                    if (component == null || ReadBoolMember(component, "enabled") == false) { continue; }
                    var text = PublicText(component, 0);
                    if (string.IsNullOrEmpty(text)) { continue; }
                    rows.Raw(new JsonObj().Str("path", path).Str("componentType", component.GetType().Name).Str("text", text).Close());
                }
            }
            int? count = PublicInt(MemberWithAccessor(transform, "childCount"));
            if (!count.HasValue) { return; }
            var getChild = transform.GetType().GetMethod("GetChild", new[] { typeof(int) });
            if (getChild == null) { return; }
            for (int i = 0; i < count.Value && budget > 0; i++)
            { try { PublicPopupTexts(getChild.Invoke(transform, new object[] { i }), path, depth + 1, rows, ref budget); } catch { } }
        }
    }
}
