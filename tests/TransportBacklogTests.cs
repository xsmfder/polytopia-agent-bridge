using System;
using System.IO;
using System.Text;
using PolytopiaAgent;

internal static class TransportBacklogTests
{
    private static int passed;
    private static void Check(bool value, string name)
    { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }

    public static int Main(string[] args)
    {
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "inbox-backlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string cmd = Path.Combine(dir, "cmd.jsonl");
            File.WriteAllText(Path.Combine(dir, "bridge-ready.json"), "{\"pid\":123}");
            int cap = BridgeMod.CommandReadMaxBytes;
            string source = new string('a', cap - 1) + "汉😀\n" + new string('z', cap + 3);
            File.WriteAllText(cmd, source, new UTF8Encoding(false));
            long tick = 0, last = 0, offset = 0;
            string pending = "";
            var stats = new BridgeMod.TransportIoStats();
            var actual = new StringBuilder();
            int polls = 0;
            while (offset < new FileInfo(cmd).Length && polls++ < 10)
            {
                tick += 3;
                long before = offset;
                string part = BridgeMod.TransportPollCmdText(tick, ref last, ref offset, ref pending, cmd, dir, 123, stats);
                Check(offset - before <= cap, "each backlog read stays within byte budget");
                if (polls == 1) Check(offset == cap - 1 && part.Length == cap - 1, "split CJK bytes are deferred rather than replaced");
                actual.Append(part);
            }
            Check(actual.ToString() == source, "large backlog retains exact UTF8 text and order across polls");
            Check(offset == new FileInfo(cmd).Length, "offset accounts for consumed bytes only");
            Check(polls >= 3, "large backlog is drained over multiple polls");
            Check(stats.CmdBytes == offset + 1, "byte diagnostics include the deferred byte reread");

            // A concurrent append may stop halfway through a character even below the budget.
            byte[] utf8 = Encoding.UTF8.GetBytes("😀\n");
            File.WriteAllBytes(cmd, new byte[] { utf8[0], utf8[1] });
            offset = 0; pending = ""; tick += 3;
            string partial = BridgeMod.TransportPollCmdText(tick, ref last, ref offset, ref pending, cmd, dir, 123, stats);
            Check(partial == "" && offset == 0, "incomplete four-byte character is not consumed");
            using (var stream = new FileStream(cmd, FileMode.Append)) stream.Write(utf8, 2, utf8.Length - 2);
            tick += 3;
            partial = BridgeMod.TransportPollCmdText(tick, ref last, ref offset, ref pending, cmd, dir, 123, stats);
            Check(partial == "😀\n" && offset == utf8.Length, "later append completes the same command character");

            File.AppendAllText(cmd, "next\n");
            long retained = offset; pending = "held"; tick += 3;
            string denied = BridgeMod.TransportPollCmdText(tick, ref last, ref offset, ref pending, cmd, dir, 456, stats);
            Check(denied == null && offset == retained && pending == "held", "non-owner leaves backlog and pending state untouched");
            File.WriteAllText(cmd, "ok\n"); tick += 3;
            string reset = BridgeMod.TransportPollCmdText(tick, ref last, ref offset, ref pending, cmd, dir, 123, stats);
            Check(reset == "ok\n" && offset == 3 && pending == "", "truncation resets only current owner state");
            Console.WriteLine("PASS total " + passed); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally { Directory.Delete(dir, true); }
    }
}
