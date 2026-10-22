using System;
using System.IO;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using PolytopiaAgent;

internal static class RequestContextTests
{
    private static int passed, failed;
    private static void Check(bool value, string name)
    { if (value) passed++; else { failed++; Console.WriteLine("FAIL " + name); } }
    private static string Compare(string ep, string seat, string stream, string version, bool available)
    { return BridgeMod.CompareActionContext(ep, seat, stream, version, "episode-1", "2", "stream-1", 17, available); }
    private static string CompareRequest(string raw)
    {
        var request = BridgeJsonCodec.Object(raw);
        return Compare(BridgeMod.RequestActionContextField(request, "expectedEpisodeId"),
            BridgeMod.RequestActionContextField(request, "expectedPlayerId"),
            BridgeMod.RequestActionContextField(request, "expectedStreamId"),
            BridgeMod.RequestActionContextField(request, "expectedStateVersion"), true);
    }
    public static int Main(string[] args)
    {
        Check(Compare(null, null, null, null, false) == null, "legacy clients compatible without partial context");
        Check(CompareRequest("{\"cmd\":\"leaderboard\",\"mode\":\"read\"}") == null,
            "actual passive leaderboard envelope has no optional context");
        Check(CompareRequest("{\"expectedEpisodeId\":\"episode-1\"}") == "INVALID_ACTION_CONTEXT",
            "present partial context still rejected");
        Check(CompareRequest("{\"expectedEpisodeId\":null,\"expectedPlayerId\":null,\"expectedStreamId\":null,\"expectedStateVersion\":null}") == "INVALID_ACTION_CONTEXT",
            "present null context is not omitted context");
        Check(CompareRequest("{\"expectedEpisodeId\":\"\",\"expectedPlayerId\":\"\",\"expectedStreamId\":\"\",\"expectedStateVersion\":\"\"}") == "INVALID_ACTION_CONTEXT",
            "present empty context is not omitted context");
        Check(CompareRequest("{\"expectedEpisodeId\":\"episode-1\",\"expectedPlayerId\":\"2\",\"expectedStreamId\":\"stream-1\",\"expectedStateVersion\":\"17\"}") == null,
            "actual complete context envelope eligible");
        Check(Compare("episode-1", "2", "stream-1", "17", true) == null, "current context eligible");
        Check(Compare("episode-2", "2", "stream-1", "17", true) == "STALE_ACTION_CONTEXT", "new episode rejected");
        Check(Compare("episode-1", "1", "stream-1", "17", true) == "STALE_ACTION_CONTEXT", "wrong controlled seat rejected");
        Check(Compare("episode-1", "2", "stream-2", "17", true) == "STALE_ACTION_CONTEXT", "new stream rejects old action");
        Check(Compare("episode-1", "2", "stream-1", "16", true) == "STALE_ACTION_CONTEXT", "old revision rejected");
        Check(Compare("episode-1", "2", "stream-1", "18", true) == "STALE_ACTION_CONTEXT", "future revision rejected");
        Check(Compare("episode-1", "2", "stream-1", "17", false) == "ACTION_CONTEXT_UNAVAILABLE", "paused or unavailable refuses");
        Check(Compare("episode-1", null, "stream-1", "17", true) == "INVALID_ACTION_CONTEXT", "partial context refuses");
        Check(Compare("", "2", "stream-1", "17", true) == "INVALID_ACTION_CONTEXT", "empty episode refuses");
        foreach (var bad in new[] { "0", "-1", "x", "17.0", "9223372036854775808" })
            Check(Compare("episode-1", "2", "stream-1", bad, true) == "INVALID_ACTION_CONTEXT", "invalid revision " + bad);
        foreach (var bad in new[] { "0", "255", "-1", "true" })
            Check(Compare("episode-1", bad, "stream-1", "17", true) == "INVALID_ACTION_CONTEXT", "invalid seat " + bad);
        foreach (var command in new[] { "action", "unit_action", "tile_action", "tribe_action", "popup", "end_turn", "attack_preview",
            "game_info", "unit_properties", "score_snapshot", "threat_map", "composite_preview", "tribe_info", "leaderboard" })
            Check(BridgeMod.ContextGuardedCommand(command), "guarded operation " + command);
        foreach (var command in new[] { "world", "events", "settlement", "result", "handoff_status", "handoff_start", null })
            Check(!BridgeMod.ContextGuardedCommand(command), "read or separately guarded handoff " + command);
        // Wiring regression: the comparison must run before any command dispatch.
        string path = args.Length > 0 ? args[0]
            : Path.Combine(Directory.GetCurrentDirectory(), ".offline-work/agent-bridge/src/BridgeMod.cs");
        string source = File.ReadAllText(path);
        int handler = source.IndexOf("private void HandleCommand(", StringComparison.Ordinal);
        int guard = source.IndexOf("ValidateActionContext(cmd, raw)", handler, StringComparison.Ordinal);
        int dispatch = source.IndexOf("switch (cmd)", handler, StringComparison.Ordinal);
        Check(guard > handler && guard < dispatch, "real handler guards before native dispatch");
        var refused = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
            BridgeMod.PreDispatchRejection().Str("errorCode", "CONTROLLED_SEAT_PAUSED").Bool("canAct", false).Close());
        Check((bool)refused["ok"] == false && (bool)refused["accepted"] == false
            && (bool)refused["dispatched"] == false && (string)refused["outcome"] == "not_dispatched",
            "pre-dispatch refusal supplies explicit nonexecution evidence");
        Check((string)refused["errorCode"] == "CONTROLLED_SEAT_PAUSED" && (bool)refused["canAct"] == false,
            "refusal retains original diagnostics");
        string gates = source.Substring(handler, dispatch - handler);
        Check(gates.Split(new[] { "Reply(PreDispatchRejection()" }, StringSplitOptions.None).Length - 1 == 3,
            "all three actual front gates use nonexecution response");
        int handlerEnd = source.IndexOf("private object GameStateInstance(", dispatch, StringComparison.Ordinal);
        int routerProof = source.IndexOf("PreDispatchRejection()", dispatch, StringComparison.Ordinal);
        Check(handlerEnd > dispatch && (routerProof < 0 || routerProof >= handlerEnd),
            "router does not fabricate nonexecution proof after routing a command");
        int action = source.IndexOf("private string ExecuteGameAction(", handlerEnd, StringComparison.Ordinal);
        int invoked = source.IndexOf("object ret = InvokeCommandExecutor(exec, am, command, out validationError);", action, StringComparison.Ordinal);
        int actionEnd = source.IndexOf("private byte CurrentPlayerId(", invoked, StringComparison.Ordinal);
        Check(invoked > action && actionEnd > invoked
            && !source.Substring(invoked, actionEnd - invoked).Contains("PreDispatchRejection()"),
            "actual native invocation and its exception paths cannot claim nonexecution");
        Console.WriteLine("TOTAL " + passed);
        return failed == 0 ? 0 : 1;
    }
}
