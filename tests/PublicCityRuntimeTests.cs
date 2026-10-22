// Tests invoke the production filtering/read seams using game-shaped, read-only fixtures.
using System;
using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.Reflection;
using PolytopiaAgent;
using PolytopiaCityOps;

internal static class PublicCityRuntimeTests
{
    internal sealed class Kind { public string type; public Kind(string t) { type = t; } }
    internal sealed class Requirement { public Kind terrain; public Kind resource; }
    internal sealed class Data { public List<object> terrainRequirements = new List<object>(); }
    internal sealed class MissingResource { public Kind terrain = new Kind("Mountain"); }
    internal sealed class Player { public List<byte> knownPlayers; public Dictionary<byte, object> relations; public List<Message> messages; }
    internal sealed class Relation { public string State = "Peace"; public int EmbassyLevel = 1, EmbassyBuildTurn = 4; }
    // Matches the verified native Dictionary<byte,T> CLR wrapper shape: no CLR IEnumerable.
    internal sealed class InteropRelations
    {
        public readonly Dictionary<byte, object> Values = new Dictionary<byte, object>();
        public readonly List<string> Calls = new List<string>();
        public bool ContainsFails, GetterFails;
        public int Count { get { throw new Exception("list-index fallback forbidden"); } }
        public bool ContainsKey(byte key) { Calls.Add("contains:" + key); if (ContainsFails) throw new Exception("native existence unreadable"); return Values.ContainsKey(key); }
        public object this[byte key] { get { Calls.Add("get:" + key); if (GetterFails) throw new Exception("native getter unreadable"); return Values[key]; } }
        public object GetRelation(byte key) { throw new Exception("creating relation API forbidden"); }
    }
    internal sealed class InteropPlayer { public object relations; public List<Message> messages = new List<Message>(); }
    internal sealed class Message { public string Type; public byte Sender; }
    internal sealed class Trigger { public byte playerId, opponentId; public string type; }
    internal sealed class Unit { public byte owner; }
    internal sealed class TextField { public string Text; }
    internal sealed class NestedText { public TextField textField = new TextField { Text = "已淘汰\n部落\t通知" }; }
    private static int passed;
    private static object Invoke(string name, params object[] args)
    { return typeof(BridgeMod).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args); }
    private static void Check(bool condition, string label)
    { if (!condition) { throw new Exception("FAIL " + label); } passed++; Console.WriteLine("PASS " + label); }
    private static bool? Response(Player player, IEnumerable triggers, byte other)
    { return (bool?)Invoke("PublicPeaceResponseAvailable", player, triggers, (byte)1, other, false, null); }
    public static int Main()
    {
        Check(CityOptionsSnapshot.IsOwnedGarrison(new Unit { owner = 1 }, (byte)1), "own city garrison permits own effective attributes");
        Check(!CityOptionsSnapshot.IsOwnedGarrison(new Unit { owner = 2 }, (byte)1), "enemy occupying own city cannot inherit own terrain and technology bonuses");
        Check(!CityOptionsSnapshot.IsOwnedGarrison(new object(), (byte)1), "unreadable garrison owner remains excluded");
        var farm = new Data();
        farm.terrainRequirements.Add(new Requirement { terrain = new Kind("Field"), resource = new Kind("Crop") });
        Check(CityOptionsSnapshot.ResourceRequirementsVisible(farm, "Field", null) == false, "hidden crop cannot reveal Farm coordinates through CanBuild");
        Check(CityOptionsSnapshot.ResourceRequirementsVisible(farm, "Field", "Crop") == true, "newly disclosed crop permits native eligibility query");
        Check(CityOptionsSnapshot.ResourceRequirementsVisible(farm, "Field", "Metal") == false, "wrong resource does not pass predicate");
        var mixed = new Data(); mixed.terrainRequirements.Add(new Requirement { terrain = new Kind("Mountain"), resource = new Kind("Metal") });
        mixed.terrainRequirements.Add(new Requirement { terrain = new Kind("Field"), resource = null });
        Check(CityOptionsSnapshot.ResourceRequirementsVisible(mixed, "Field", null) == true, "native resource-free OR clause survives hidden resource gate");
        Check(CityOptionsSnapshot.ResourceRequirementsVisible(mixed, "Mountain", null) == false, "terrain-specific hidden mineral stays hidden");
        var opaque = new Data(); opaque.terrainRequirements.Add(new MissingResource());
        Check(CityOptionsSnapshot.ResourceRequirementsVisible(opaque, "Mountain", null) == null, "absent resource member is unknown rather than resource-free");
        Check(CityOptionsSnapshot.ResourceRequirementsVisible(new object(), "Field", null) == null, "unknown requirement catalog does not become unrestricted");
        Check(CityOptionsSnapshot.ResourceRequirementsVisible(new Data(), "Field", null) == true, "native empty requirement catalog is unrestricted");

        var player = new Player { knownPlayers = new List<byte> { 2, 0, 255 }, relations = new Dictionary<byte, object>(), messages = new List<Message>() };
        var known = (HashSet<byte>)Invoke("PublicKnownPlayers", player, (byte)1);
        Check(known.SetEquals(new byte[] { 1, 2 }), "known tribe projection excludes unknown tribe/Nature sentinels");
        Check(Invoke("PublicKnownPlayers", new Player(), (byte)1) == null, "unreadable known players stays unknown");
        player.relations[2] = new Relation();
        Check(Invoke("PublicDictionaryValue", player.relations, (byte)3) == null && player.relations.Count == 1, "relation lookup does not construct missing diplomacy records");
        var nativeRelations = new InteropRelations(); var nativeRelation = new Relation(); nativeRelations.Values[2] = nativeRelation;
        Check(object.ReferenceEquals(Invoke("PublicDictionaryValue", nativeRelations, (byte)2), nativeRelation), "non-CLR-enumerable native byte-key dictionary returns the actual existing relation");
        Check(string.Join(",", nativeRelations.Calls.ToArray()) == "contains:2,get:2", "native relation lookup confirms key exists before invoking getter");
        nativeRelations.Calls.Clear();
        Check(Invoke("PublicDictionaryValue", nativeRelations, (byte)3) == null && nativeRelations.Values.Count == 1
            && string.Join(",", nativeRelations.Calls.ToArray()) == "contains:3", "missing native key never invokes getter or creates a relation");
        nativeRelations.Calls.Clear(); nativeRelations.ContainsFails = true;
        Check(Invoke("PublicDictionaryValue", nativeRelations, (byte)2) == null && nativeRelations.Calls.Count == 1, "unreadable native existence remains unknown without unsafe getter or enumeration");
        nativeRelations.ContainsFails = false; nativeRelations.GetterFails = true;
        Check(Invoke("PublicDictionaryValue", nativeRelations, (byte)2) == null, "failed existing native relation getter remains unknown");
        nativeRelations.GetterFails = false;
        var interopPlayer = new InteropPlayer { relations = nativeRelations };
        var nativeCore = (string)Invoke("PublicRelationCore", interopPlayer, (byte)2);
        Check(nativeCore.Contains("\"state\":\"Peace\"") && nativeCore.Contains("\"embassyLevel\":1") && nativeCore.Contains("\"embassyBuildTurn\":4"), "production relationship projection reads State and embassy fields from native dictionary path");
        Check((string)Invoke("PublicExistingRelationState", interopPlayer, (byte)2) == "Peace", "diplomacy action confirmation uses same read-only native relation path");
        Check(Invoke("PublicDictionaryValue", new List<KeyValuePair<byte, object>> { new KeyValuePair<byte, object>(2, nativeRelation) }, (byte)2) == nativeRelation, "legacy enumerable KeyValue pairs remain compatible");
        player.messages.Add(new Message { Type = "PeaceRequest", Sender = 2 });
        player.messages.Add(new Message { Type = "EstablishEmbassy", Sender = 3 });
        string relation = (string)Invoke("PublicRelationCore", player, (byte)2);
        Check(relation.Contains("\"state\":\"Peace\"") && relation.Contains("\"embassyLevel\":1"), "native relationship and embassy level are preserved");
        Check(relation.Contains("opponent embassy in controlled player's capital"), "embassy level direction is explicit rather than confused with own outgoing embassy");
        Check(relation.Contains("PeaceRequest") && !relation.Contains("EstablishEmbassy"), "incoming requests are target correlated");
        var triggers = new List<object> { new Trigger { playerId = 1, opponentId = 2, type = "PeaceRequest" } };
        Check(Response(player, triggers, 2) == true, "response requires actual own request and active correlated trigger");
        Check(Response(player, triggers, 3) == false, "request from another tribe cannot authorize response");
        Check(Response(player, new List<object>(), 2) == false, "native empty-trigger permissiveness cannot create unsolicited peace");
        Check(Response(player, null, 2) == null, "unreadable pending triggers remain unknown");
        Check(Response(new Player(), triggers, 2) == null, "unreadable incoming messages remain unknown");
        Check(Response(player, new List<object> { new Trigger { playerId = 2, opponentId = 2, type = "PeaceRequest" } }, 2) == false,
            "another player's request trigger does not authorize own response");
        triggers.Add(new Trigger { playerId = 1, opponentId = 2, type = "CityLevelUp" });
        Check(Response(player, triggers, 2) == false, "last own trigger must be PeaceRequest rather than older matching trigger");
        triggers.Add(new Trigger { playerId = 1, opponentId = 3, type = "PeaceRequest" });
        Check(Response(player, triggers, 2) == false, "active request for different sender cannot be skipped");
        Check(Response(player, new List<object> { new object() }, 2) == null, "missing trigger fields do not become zero or empty");
        Check((bool)Invoke("PublicDiplomacyTargetMatches", new Unit { owner = 2 }, (byte)2), "native-visible target unit provides embassy target");
        Check(!(bool)Invoke("PublicDiplomacyTargetMatches", new Unit { owner = 1 }, (byte)2), "own city or own unit cannot provide embassy target");
        Check(!(bool)Invoke("PublicDiplomacyTargetMatches", null, (byte)2), "withheld target unit cannot authorize embassy target");
        var received = new Player { messages = new List<Message>() };
        var receivedTriggers = new List<object> { new Trigger { playerId = 1, opponentId = 2, type = "PeaceRequest" } };
        Check((bool?)Invoke("PublicPeaceResponseAvailable", received, receivedTriggers, (byte)1, (byte)2, true, null) == true,
            "natural receipt authorizes native trigger after Receive action removed message");
        Check(Response(received, receivedTriggers, 2) == false, "unobserved receipt is not fabricated from an orphan trigger");
        Check((bool?)Invoke("PublicPeaceResponseAvailable", received, new List<object>(), (byte)1, (byte)2, true, null) == false,
            "cached receipt cannot replay response after native trigger consumption");
        Check((bool?)Invoke("PublicPeaceTriggerConsumed", new List<object>(), (byte)1, (byte)2) == true,
            "empty known own trigger list proves request consumed");
        Check((bool?)Invoke("PublicPeaceTriggerConsumed", receivedTriggers, (byte)1, (byte)2) == false,
            "remaining same request prevents completed response claim");
        string proofBasis;
        var proof = new BridgeMod.PublicDiplomacyObservation { Kind = "EstablishEmbassyAction", Me = 1, Other = 2,
            Complete = true, StarsBefore = 12, StarsAfter = 8, Cost = 4, QualifierBefore = true, QualifierAfter = false };
        Check(BridgeMod.PublicDiplomacyProofConfirmation(proof, "establish_embassy", out proofBasis) == "confirmed",
            "successful own native embassy plus exact spend and qualification transition confirms effect");
        proof.StarsAfter = 7;
        Check(BridgeMod.PublicDiplomacyProofConfirmation(proof, "establish_embassy", out proofBasis) == "not_confirmed",
            "unrelated currency change cannot satisfy exact embassy cost");
        proof.StarsAfter = 8; proof.QualifierAfter = null;
        Check(BridgeMod.PublicDiplomacyProofConfirmation(proof, "establish_embassy", out proofBasis) == "unknown",
            "unreadable target qualification is not false");
        proof.Complete = false;
        Check(BridgeMod.PublicDiplomacyProofConfirmation(proof, "establish_embassy", out proofBasis) == "unknown",
            "exception or incomplete native action cannot confirm diplomacy");
        var responseProof = new BridgeMod.PublicDiplomacyObservation { Kind = "PeaceRequestResponseAction", Me = 1, Other = 2,
            Complete = true, Accepted = true, ResponseAuthorized = true, OwnStateAfter = "Peace", TriggerConsumed = true };
        Check(BridgeMod.PublicDiplomacyProofConfirmation(responseProof, "peace_response", out proofBasis) == "confirmed",
            "correlated requested acceptance plus own Peace and consumed request confirms effect");
        responseProof.TriggerConsumed = false;
        Check(BridgeMod.PublicDiplomacyProofConfirmation(responseProof, "peace_response", out proofBasis) == "not_confirmed",
            "unchanged request trigger prevents response completion");
        responseProof.TriggerConsumed = true; responseProof.ResponseAuthorized = false;
        Check(BridgeMod.PublicDiplomacyProofConfirmation(responseProof, "peace_response", out proofBasis) == "unknown",
            "own native response without explicit request-bound authorization stays unknown");
        responseProof.ResponseAuthorized = true; responseProof.Accepted = false; responseProof.OwnStateAfter = "Neutral";
        Check(BridgeMod.PublicDiplomacyProofConfirmation(responseProof, "peace_response", out proofBasis) == "confirmed",
            "explicit rejection consumes request without creating Peace");
        Check(BridgeMod.PublicDiplomacyProofConfirmation(new BridgeMod.PublicDiplomacyObservation {
            Complete = true, Kind = "PeaceTreatyAction", Me = 1, Other = 2 }, "peace_request", out proofBasis) == "confirmed"
            && proofBasis.Contains("does not prove opponent acceptance"), "peace request confirmation describes dispatch rather than concluded treaty");
        Check((string)Invoke("PublicText", new NestedText(), 0) == "已淘汰\n部落\t通知", "nested native UI localized text is read exactly");
        Check(Invoke("PublicText", new object(), 0) == null, "missing popup text is not inferred");
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        Check((string)Invoke("PublicNumber", 1.5m) == "1.5", "public numeric JSON remains invariant under locale");
        Console.WriteLine("TOTAL " + passed + " passed");
        return 0;
    }
}
