// Exercise production preview reads. Failed native collections cannot be a
// complete shortened target list, and malformed numeric fields are not rounded.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using PolytopiaBattlePreview;

public enum PreviewNumber { One = 1 }
public sealed class PreviewValue { public object health; }
public sealed class PreviewBoolOverloads
{
    public int Calls;
    public bool CanAttack() { Calls++; return false; }
    public bool CanAttack(int ignored) { throw new Exception("wrong overload"); }
}
public sealed class PreviewIndexedList
{
    public object Count { get; set; }
    public int FailedIndex = -1;
    public object this[int index] { get { if (index == FailedIndex) throw new Exception("item unreadable"); return index; } }
}
public sealed class PreviewThrowingEnumerable : IEnumerable
{
    public IEnumerator GetEnumerator() { yield return 1; throw new Exception("enumeration failed"); }
}
public sealed class PreviewThrowingList : ArrayList
{
    public override object this[int index]
    {
        get { if (index == 1) throw new Exception("IList item unreadable"); return base[index]; }
        set { base[index] = value; }
    }
}
public sealed class PreviewOverloadedIndexer
{
    public int Count { get { return 1; } }
    public object this[int index] { get { return "native-int-index"; } }
    public object this[string index] { get { throw new Exception("unrelated indexer"); } }
}

internal static class PreviewAdapterTests
{
    private static int passed, failed;
    private static readonly BindingFlags F = BindingFlags.NonPublic | BindingFlags.Static;
    private static object Read(string method, params object[] args)
    { return typeof(BattlePreviewSnapshot).GetMethod(method, F).Invoke(null, args); }
    private static void Check(bool condition, string name)
    { if (condition) passed++; else { failed++; Console.WriteLine("FAIL " + name); } }
    public static int Main()
    {
        foreach (object value in new object[] { true, "1", 1.4m, 1.4d, 1.0f, PreviewNumber.One, (long)int.MaxValue + 1 })
            Check(Read("MI", new PreviewValue { health = value }, "health") == null,
                "preview rejects non-native integer: " + value.GetType().Name);
        foreach (object value in new object[] { (sbyte)-1, (ushort)100, 100, 100L, (uint)100, (ulong)100 })
            Check(Convert.ToInt32(Read("MI", new PreviewValue { health = value }, "health")) == Convert.ToInt32(value),
                "preview retains exact native integer: " + value.GetType().Name);
        var boolean = new PreviewBoolOverloads();
        Check(object.Equals(Read("MB", boolean, "CanAttack"), false) && boolean.Calls == 1,
            "parameter overload does not hide current no-arg native false");
        Check(Read("MB", new PreviewValue { health = 1 }, "health") == null,
            "numeric member cannot become permission");
        foreach (object count in new object[] { true, false, "2", 2.2m, 2.2d, PreviewNumber.One, -1 })
            Check(Read("AsList", new PreviewIndexedList { Count = count }) == null,
                "unreadable or negative native count cannot be a complete list: " + count);
        var items = (List<object>)Read("AsList", new PreviewIndexedList { Count = 3 });
        Check(items.Count == 3 && Convert.ToInt32(items[2]) == 2, "valid native indexed list preserved");
        Check(Read("AsList", new PreviewIndexedList { Count = 3, FailedIndex = 1 }) == null,
            "failed index cannot silently remove a possible target");
        Check(((List<object>)Read("AsList", new PreviewIndexedList { Count = 0 })).Count == 0,
            "proven empty indexed list remains empty");
        Check(Read("AsList", new PreviewThrowingEnumerable()) == null,
            "mid-enumeration failure cannot publish a shortened list");
        var failedList = new PreviewThrowingList(); failedList.Add(0); failedList.Add(1);
        Check(Read("AsList", failedList) == null, "IList index failure becomes unavailable rather than an escaping exception");
        Check((string)((List<object>)Read("AsList", new PreviewOverloadedIndexer()))[0] == "native-int-index",
            "unrelated indexer overload does not hide native integer indexing");
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
