using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using PolytopiaAgent;

internal static class StartupMetadataTests
{
    private static int passed;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
    private static Dictionary<string, object> Parse(string text)
    { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text); }

    public static int Main()
    {
        try
        {
            string cmd = "F:\\学习\\cmd.jsonl", output = "F:\\学习\\out.jsonl";
            string raw = BridgeMod.StartupReadyJson(cmd, output, 123, "2026-10-13T10:00:00", 1, true);
            var metadata = Parse(raw);
            Check((string)metadata["cmd"] == cmd && (string)metadata["out"] == output, "startup paths survive JSON escaping");
            Check((string)metadata["transport"] == "file" && (string)metadata["startupSchema"] == "bridge-startup-1", "versioned startup metadata retains transport contract");
            Check(metadata["controlledSeatLoaded"] is bool && (bool)metadata["controlledSeatLoaded"], "completed load is explicit boolean metadata");
            Check(metadata["configuredPlayerId"] is int && (int)metadata["configuredPlayerId"] == 1, "configured seat is numeric, independent of match state");
            Check(object.Equals(metadata["startupUiReady"], false) && (string)metadata["startupUiPhase"] == "initializing",
                "transport initialization cannot claim interactive menu readiness");
            var uiReady = Parse(BridgeMod.StartupReadyJson(cmd, output, 123, "start", 1, true, "start_menu"));
            Check(object.Equals(uiReady["startupUiReady"], true) && (string)uiReady["started"] == "start",
                "menu proof extends metadata without changing session identity");
            Check(object.Equals(Parse(BridgeMod.StartupReadyJson(cmd, output, 123, "start", 1, false, "start_menu"))["startupUiReady"], false),
                "menu phase cannot override incomplete seat loading");
            Check(BridgeMod.TransportReadyPidMatches(raw, 123), "existing ownership reader accepts extended ready document");
            Check(!BridgeMod.TransportReadyPidMatches(raw, 456), "new metadata cannot authorize another process");
            metadata = Parse(BridgeMod.StartupReadyJson(cmd, output, 123, "start", 2, true));
            Check((int)metadata["configuredPlayerId"] == 2, "hotseat selection is not silently changed to solo seat");
            metadata = Parse(BridgeMod.StartupReadyJson(cmd, output, 123, "start", null, true));
            Check(metadata["configuredPlayerId"] == null && (bool)metadata["controlledSeatLoaded"], "legacy automatic seat remains explicit null after load");
            metadata = Parse(BridgeMod.StartupReadyJson(cmd, output, 123, "start", 255, true));
            Check((int)metadata["configuredPlayerId"] == 255, "invalid configuration sentinel is preserved, never advertised as valid seat");
            metadata = Parse(BridgeMod.StartupReadyJson(cmd, output, 123, "start", null, false));
            Check(!(bool)metadata["controlledSeatLoaded"], "uncompleted load cannot appear acknowledged");
            Console.WriteLine("PASS total " + passed); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
