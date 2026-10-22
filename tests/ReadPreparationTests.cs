using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using PolytopiaAgent;

public static class GameManager
{
    public static int Reads;
    public static object GameState { get { Interlocked.Increment(ref Reads); throw new Exception("query must not run"); } }
    public static object LocalPlayer { get { Interlocked.Increment(ref Reads); throw new Exception("player must not be read"); } }
}
internal static class ReadPreparationTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static int passed;
    private static void Check(bool value, string name)
    { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    public static int Main()
    {
        try
        {
            var before = BridgeJsonCodec.Object(BridgeMod.ReadPreparationJson());
            Check((string)before["status"] == "not_started", "preparation remains dormant until explicit bridge initialization");
            Check(BridgeMod.IsReadPreparationTarget(typeof(PolytopiaCityOps.CityOptionsSnapshot).GetMethod("Snapshot"))
                && BridgeMod.IsReadPreparationTarget(typeof(BridgeMod).GetMethod("WorldDump", F)), "managed query roots are selected by exact declaring type");
            Check(!BridgeMod.IsReadPreparationTarget(typeof(GameManager).GetProperty("GameState").GetGetMethod())
                && !BridgeMod.IsReadPreparationTarget(typeof(string).GetMethod("ToUpper", Type.EmptyTypes))
                && !BridgeMod.IsReadPreparationTarget(typeof(BridgeMod).GetMethod("OnUpdate", F))
                && !BridgeMod.IsReadPreparationTarget(null), "game methods, framework methods and ordinary update callbacks are never preparation targets");
            Type helper=typeof(PolytopiaCityOps.CityOptionsSnapshot).GetNestedType("SnapshotReads",F);
            Check(helper!=null && BridgeMod.IsReadPreparationTarget(helper.GetMethod("Resource",F))
                && !BridgeMod.IsReadPreparationTarget(typeof(BridgeMod).GetMethod("BeginCityReadTiming",F)),
                "owned nested snapshot helper is prepared without widening to unrelated bridge methods");
            var watch = Stopwatch.StartNew();
            BridgeMod.StartReadPreparation();
            string status;
            Dictionary<string, object> state;
            do
            {
                state = BridgeJsonCodec.Object(BridgeMod.ReadPreparationJson()); status = (string)state["status"];
                if (status == "complete" || status == "partial" || status == "unavailable") break;
                Thread.Sleep(1);
            } while (watch.ElapsedMilliseconds < 10000);
            Check(status == "complete" && int.Parse(BridgeJsonCodec.Scalar(state["preparedMethods"])) > 20
                && int.Parse(BridgeJsonCodec.Scalar(state["failures"])) == 0, "actual background method preparation completes without invoking queries");
            Check(GameManager.Reads == 0, "background preparation never reads game or player getters");
            string completed = BridgeMod.ReadPreparationJson();
            for (int i = 0; i < 50; i++) BridgeMod.StartReadPreparation();
            Check(BridgeMod.ReadPreparationJson() == completed, "repeated initialization cannot queue more preparation or reset evidence");
            var ready = BridgeJsonCodec.Object(BridgeMod.StartupReadyJson("cmd", "out", 123, "same-start", 2, true, "start_menu"));
            Check((bool)ready["startupUiReady"] && (string)ready["started"] == "same-start"
                && ((Dictionary<string, object>)ready["readPreparation"])["status"].Equals("complete"), "startup metadata reports preparation separately without changing UI readiness or transport identity");
            Check(GameManager.Reads == 0, "diagnostic metadata does not trigger a read while reporting compilation");
            Console.WriteLine("PASS total " + passed); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
