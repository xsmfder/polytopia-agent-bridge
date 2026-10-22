using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public class GameState { public List<object> pendingCommandTriggers = new List<object>(); }
public class PlayerState { public List<object> messages = new List<object>(); }
public class CommandBase
{
    public byte PlayerId = 2, OpponentId = 3; public string Type;
    public bool Valid = true, ValidationThrows;
    public object Coordinates { get { throw new Exception("hidden capital coordinates read"); } }
    public object Target { get { throw new Exception("hidden target read"); } }
    public string GetCommandType() { return Type; }
    public bool IsValid(GameState gs, out string error)
    { error = null; if (ValidationThrows) throw new Exception(); return Valid; }
}
public class PeaceRequestResponseCommand : CommandBase
{
    public bool Accepted;
    public PeaceRequestResponseCommand(byte me, byte other, bool accept)
    { PlayerId = me; OpponentId = other; Accepted = accept; Type = "PeaceRequestResponse"; }
}
public static class CommandUtils
{
    public static object Result; public static bool Include; public static byte Other; public static int Calls;
    public static object GetDiplomacyCommandsForOpponent(GameState gs, PlayerState player, byte opponentId, bool includeUnavailable)
    { Calls++; Include = includeUnavailable; Other = opponentId; return Result; }
    public static object GetDiplomacyCommandsForOpponent(GameState gs, PlayerState player, byte opponentId)
    { throw new Exception("wrong overload"); }
}
public class NativeList
{
    public List<object> Values = new List<object>(); public bool Fail;
    public int Count { get { return Values.Count; } }
    public object this[int index] { get { if (Fail && index == 1) throw new Exception(); return Values[index]; } }
}
internal static class DiplomacyMenuTests
{
    private static int passed;
    private static readonly BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static object Call(BridgeMod h, string name, params object[] args)
    { return typeof(BridgeMod).GetMethod(name, F).Invoke(h, args); }
    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static List<object> Menu(BridgeMod h, GameState gs, PlayerState ps)
    { return (List<object>)Call(h, "PublicDiplomacyMenu", gs, ps, (byte)3); }
    private static Dictionary<string,object>[] Rows(BridgeMod h, GameState gs, PlayerState ps, List<object> menu)
    { return Json.Deserialize<Dictionary<string,object>[]>((string)Call(h, "PublicDiplomacyOperations", gs, ps, (byte)2, (byte)3, menu)); }
    private sealed class Message { public byte Sender = 3; public string Type = "PeaceRequest"; }
    private sealed class Trigger { public byte playerId = 2, opponentId = 3; public string type = "PeaceRequest"; }
    public static int Main()
    {
        try { return Run(); } catch (Exception e) { Console.WriteLine(e); return 1; }
    }
    private static int Run()
    {
        var h = new BridgeMod(); var gs = new GameState(); var ps = new PlayerState();
        var peace = new CommandBase { Type = "PeaceTreaty" };
        CommandUtils.Result = new List<object> { peace };
        var menu = Menu(h, gs, ps);
        Check(menu.Count == 1 && !CommandUtils.Include && CommandUtils.Other == 3, "exact native API overload excludes unavailable candidates");
        Check(Object.ReferenceEquals(Call(h, "PublicDiplomacySelect", menu, "peace_request", (byte)2, (byte)3), peace), "native candidate object reused without construction or coordinates");
        var rows = Rows(h, gs, ps, menu);
        Check(rows.Length == 1 && (string)rows[0]["actionId"] == "peace_request", "no invented fixed embassy or response candidates");
        Check((bool)rows[0]["canExecute"] && (bool)rows[0]["nativeValid"], "supported native candidate eligible");
        Check(!rows[0].ContainsKey("coordinates") && !rows[0].ContainsKey("parameters"), "command hidden anchors never projected");
        Check(Call(h, "PublicCommandCoordinateForEcho", peace, "establish_embassy") == null,
            "diplomacy acknowledgement and settlement omit native private anchor");
        Check(Call(h, "PublicCommandCoordinateForEcho", new { Target = "(2,4)" }, "move").Equals("(2,4)"),
            "normal unit action echo retains visible target");
        CommandUtils.Result = new List<object> { new CommandBase { Type = "FutureDiplomacy" }, new CommandBase { Type = "BreakPeace" }, new CommandBase { Type = "DestroyEmbassy" } };
        rows = Rows(h, gs, ps, Menu(h, gs, ps));
        Check(rows.Length == 3 && (string)rows[0]["actionId"] == "native:FutureDiplomacy", "unknown native candidates retained dynamically");
        Check((string)rows[1]["actionId"] == "break_peace" && (string)rows[2]["actionId"] == "destroy_embassy", "native menu additions do not require candidate table changes");
        Check(!(bool)rows[0]["adapterSupported"] && !(bool)rows[0]["canExecute"], "unknown settlement adapter never executable");
        Check((bool)rows[1]["adapterSupported"] && (bool)rows[1]["canExecute"] && (bool)rows[2]["canExecute"],
            "native break peace and destroy embassy candidates have execution adapters");
        menu = Menu(h, gs, ps);
        Check(Call(h, "PublicDiplomacySelect", menu, "break_peace", (byte)2, (byte)3) != null
            && Call(h, "PublicDiplomacySelect", menu, "destroy_embassy", (byte)2, (byte)3) != null,
            "new adapters select current actor/target matched native candidate objects");
        Check(Call(h, "PublicCommandCoordinateForEcho", peace, "break_peace") == null
            && Call(h, "PublicCommandCoordinateForEcho", peace, "destroy_embassy") == null,
            "new diplomacy adapters omit native coordinate anchors");
        menu = new List<object> { peace };
        peace.PlayerId = 1;
        Check(Call(h, "PublicDiplomacySelect", menu, "peace_request", (byte)2, (byte)3) == null, "wrong actor candidate rejected");
        peace.PlayerId = 2; peace.OpponentId = 4;
        Check(Call(h, "PublicDiplomacySelect", menu, "peace_request", (byte)2, (byte)3) == null, "wrong target candidate rejected");
        peace.OpponentId = 3; menu.Add(peace);
        Check(Call(h, "PublicDiplomacySelect", menu, "peace_request", (byte)2, (byte)3) == null, "duplicate matching candidates rejected");
        menu.RemoveAt(1); peace.ValidationThrows = true;
        rows = Rows(h, gs, ps, menu);
        Check(rows[0]["nativeValid"] == null && rows[0]["canExecute"] == null, "validation failure remains unknown");
        peace.ValidationThrows = false;
        CommandUtils.Result = null; Check(Menu(h, gs, ps) == null, "unreadable menu distinct from empty");
        CommandUtils.Result = new List<object>(); Check(Menu(h, gs, ps).Count == 0, "native empty menu preserved");
        var list = new NativeList(); list.Values.Add(peace); list.Values.Add(peace);
        CommandUtils.Result = list; Check(Menu(h, gs, ps).Count == 2, "IL2CPP Count/indexer wrapper supported");
        list.Fail = true; Check(Menu(h, gs, ps) == null, "partial wrapper read cannot masquerade as complete menu");
        gs.pendingCommandTriggers.Add(new Trigger());
        Check(Rows(h, gs, ps, new List<object>()).Length == 0, "bare trigger cannot fabricate incoming request");
        ps.messages.Add(new Message()); rows = Rows(h, gs, ps, new List<object>());
        Check(rows.Length == 2 && !(bool)rows[0]["accept"] && (bool)rows[1]["accept"], "authorized incoming request returns both explicit response variants");
        Check((bool)rows[0]["canExecute"] && (bool)rows[1]["canExecute"], "response variants validated against native command");
        gs.pendingCommandTriggers.Clear();
        Check(Rows(h, gs, ps, new List<object>()).Length == 0, "consumed trigger removes response candidates");
        Console.WriteLine("PASS total " + passed); return 0;
    }
}
