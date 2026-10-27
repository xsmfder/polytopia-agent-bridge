using System;
using System.Collections.Generic;
using PolytopiaAgent;

public sealed class TileData
{
    public enum EffectType { None, Flooded, Algae }
    public object Values;
    public int Reads;
    public object effects { get { Reads++; return Values; } }
}
public sealed class ForeignData { public enum EffectType { Algae } }
internal static class CymantiAlgaeTests
{
    static int count;
    static void Check(bool valid,string label){if(!valid)throw new Exception(label);count++;}
    static void Main()
    {
        var tile=new TileData{Values=new List<TileData.EffectType>{TileData.EffectType.Algae}};
        Check(BridgeMod.CymantiPublicAlgae(tile,false)==null && tile.Reads==0,"undisclosed tile is never read");
        Check(BridgeMod.CymantiPublicAlgae(tile,true)==true,"actual typed public algae is observed");
        tile.Values=new List<TileData.EffectType>{TileData.EffectType.Flooded};
        Check(BridgeMod.CymantiPublicAlgae(tile,true)==false,"other actual tile effects do not imply algae");
        tile.Values=new object[]{"Algae"};Check(BridgeMod.CymantiPublicAlgae(tile,true)==null,"text is not a native effect");
        tile.Values=new object[]{ForeignData.EffectType.Algae};Check(BridgeMod.CymantiPublicAlgae(tile,true)==null,"foreign enum is not tile authority");
        tile.Values=new object[]{(TileData.EffectType)999};Check(BridgeMod.CymantiPublicAlgae(tile,true)==null,"undefined enum is unknown");
        tile.Values=new List<TileData.EffectType>();Check(BridgeMod.CymantiPublicAlgae(tile,true)==false,"known empty effect set means absent");
        tile.Values=null;Check(BridgeMod.CymantiPublicAlgae(tile,true)==null,"unavailable effect set remains unknown");
        Console.WriteLine("CymantiAlgaeTests: "+count+" passed");
    }
}
