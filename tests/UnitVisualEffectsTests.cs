// Native-shaped render/pool fixtures; no game process or internal effects list.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public enum UnitEffect { Frozen, Poisoned, Boosted, FutureGlow = 99 }
public class VisualObject { public object activeInHierarchy = true; }
public class VisualColor { public object r = 0.2f, g = 0.5f, b = 1f, a = 1f; }
public class VisualSprite
{
    public object enabled = true;
    public bool Present = true;
    public object overlayColor = new VisualColor(), overlayStrength = 0.75f;
    public VisualObject gameObject = new VisualObject();
    public void GetPropertyBlock(MaterialPropertyBlock block)
    { block.Color = overlayColor; block.Strength = overlayStrength; block.Present = Present; }
}
public class MaterialPropertyBlock
{
    public static int Created, Disposed;
    public object Color, Strength; public bool Present;
    public MaterialPropertyBlock() { Created++; }
    public bool HasColor(string name) { return Present && name == "_OverlayColor"; }
    public bool HasFloat(string name) { return Present && name == "_OverlayStrength"; }
    public object GetColor(string name) { if (!Present) throw new Exception("missing color is not zero"); return Color; }
    public object GetFloat(string name) { if (!Present) throw new Exception("missing strength is not zero"); return Strength; }
    public void Dispose() { Disposed++; }
}
public class RendererUnion { public object spriteRenderer = new VisualSprite(); }
public class VisualPart
{
    public object tintable = false;
    public VisualObject visualPart = new VisualObject();
    public RendererUnion renderer = new RendererUnion();
}
public class SkinVisuals { public object visualParts = new List<VisualPart> { new VisualPart() }; }
public class VisualParticle
{
    public object poolId = 7, IsUsed = true, isPlaying = true, isStopping = false;
    public VisualObject gameObject = new VisualObject();
}
public class VisualWrapper
{
    public object poolId = 7, pooledParticleController = new VisualParticle();
}
public class VisualState
{
    public object id = (uint)51;
    public int EffectsReads;
    public object effects { get { EffectsReads++; throw new Exception("raw effects must not be read"); } }
}
public class Unit
{
    public object unitState;
    public bool Visible = true;
    public object SpriteRenderer { get { throw new Exception("unused WorldObject renderer must not be read"); } }
    public SkinVisuals skinVisuals = new SkinVisuals();
    public int ParticleReads;
    public object Dictionary = new Dictionary<UnitEffect, VisualWrapper>();
    public object unitEffectParticles { get { ParticleReads++; return Dictionary; } }
    public bool GetVisible() { return Visible; }
}
public class MapRenderer
{
    public static MapRenderer Current { get; set; }
    public Unit Value;
    public int Calls; public uint LastId;
    public Unit GetUnitInstance(uint id) { Calls++; LastId = id; return Value; }
}
public class BrokenVisualDictionary
{
    public object Count = 1;
    public object Keys = new[] { UnitEffect.Poisoned };
    public object this[UnitEffect key] { get { throw new Exception("unreadable wrapper"); } }
}

internal static class UnitVisualEffectsTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static int passed;
    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static object Call(string method, params object[] args)
    { return typeof(BridgeMod).GetMethod(method, F).Invoke(null, args); }
    private static Dictionary<string, object> Read(VisualState state, bool disclosed = true, bool verified = true)
    { return Json.Deserialize<Dictionary<string, object>>((string)Call("URVisibleEffects", state, disclosed, verified)); }
    private static Dictionary<string, object> Row(Dictionary<string, object> value, string key)
    { return (Dictionary<string, object>)value[key]; }
    private static Unit Reset(VisualState state)
    {
        var unit = new Unit { unitState = state };
        MapRenderer.Current = new MapRenderer { Value = unit };
        return unit;
    }
    public static int Main()
    { try { Run(); Console.WriteLine("ALL PASS " + passed); return 0; } catch (Exception e) { Console.WriteLine(e); return 1; } }
    private static void Run()
    {
        var state = new VisualState(); var unit = Reset(state);
        var wrappers = new Dictionary<UnitEffect, VisualWrapper> {
            { UnitEffect.Poisoned, new VisualWrapper() }, { UnitEffect.FutureGlow, new VisualWrapper() } };
        unit.Dictionary = wrappers;
        var result = Read(state); var names = (System.Collections.IList)Row(result, "particles")["types"];
        Check((string)result["status"] == "ok" && names.Count == 2 && (string)names[0] == "FutureGlow",
            "actual active future effect names survive without a fixed effect table");
        Check(((System.Collections.IList)Row(result, "overlay")["color"]).Count == 4
            && Convert.ToDouble(Row(result, "overlay")["strength"]) == 0.75,
            "native renderer color and strength are exposed independently from mechanical states");
        Check(state.EffectsReads == 0 && !Json.Serialize(result).Contains("poolId"),
            "no internal effects read or pool identity disclosure");
        unit.ParticleReads = 0; int calls = MapRenderer.Current.Calls;
        Check((string)Read(state, false)["status"] == "withheld" && MapRenderer.Current.Calls == calls,
            "hidden unit exits before renderer lookup");
        Check((string)Read(state, true, false)["status"] == "unavailable" && MapRenderer.Current.Calls == calls,
            "unsupported build exits before renderer lookup");
        unit.Visible = false;
        Check((string)Read(state)["status"] == "unavailable" && unit.ParticleReads == 0,
            "hidden render unit cannot disclose its effect dictionary");
        unit.Visible = true; unit.unitState = new object();
        Check((string)Read(state)["status"] == "unavailable" && unit.ParticleReads == 0,
            "pooled render unit associated with another state cannot disclose cues");
        unit.unitState = state;
        state.id = uint.MaxValue; Read(state);
        Check(MapRenderer.Current.LastId == uint.MaxValue, "native unit lookup preserves the full uint identifier range");
        foreach (object bad in new object[] { true, -1, 1.5, "51" })
        {
            state.id = bad; calls = MapRenderer.Current.Calls; Read(state);
            Check(MapRenderer.Current.Calls == calls, "bad native identifier cannot reach renderer lookup");
        }
        state.id = (uint)51;
        var particle = (VisualParticle)wrappers[UnitEffect.Poisoned].pooledParticleController;
        particle.poolId = 8;
        result = Read(state); names = (System.Collections.IList)Row(result, "particles")["types"];
        Check(names.Count == 1 && (string)names[0] == "FutureGlow", "reused particle from another allocation is excluded");
        particle.poolId = 7; particle.IsUsed = false;
        Check(((System.Collections.IList)Row(Read(state), "particles")["types"]).Count == 1, "returned inactive particle does not prove a present effect");
        particle.IsUsed = true; particle.gameObject.activeInHierarchy = false;
        Check(((System.Collections.IList)Row(Read(state), "particles")["types"]).Count == 1, "inactive hierarchy does not count as visible particles");
        particle.gameObject.activeInHierarchy = true; particle.isPlaying = false; particle.isStopping = true;
        Check((string)Row(Read(state), "particles")["status"] == "partial", "uncertain fading cue is not a complete absence claim");
        particle.isPlaying = true; particle.isStopping = false; particle.poolId = true;
        Check((string)Row(Read(state), "particles")["status"] == "partial", "malformed pool identity retains only the known public subset");
        unit.Dictionary = new BrokenVisualDictionary();
        Check((string)Row(Read(state), "particles")["status"] == "partial", "throwing native dictionary slot cannot become complete zero effects");
        unit.Dictionary = null;
        Check(Row(Read(state), "particles")["types"] == null, "missing particle dictionary is unknown rather than empty");
        unit.Dictionary = new Dictionary<UnitEffect, VisualWrapper>();
        Check(((System.Collections.IList)Row(Read(state), "particles")["types"]).Count == 0, "readable empty native dictionary is known empty");
        var parts = (List<VisualPart>)unit.skinVisuals.visualParts;
        var sprite = (VisualSprite)parts[0].renderer.spriteRenderer;
        sprite.Present = false;
        result = Read(state);
        Check((string)result["status"] == "partial" && Row(result, "overlay")["color"] == null,
            "missing shader properties withhold color without hiding known particles");
        sprite.Present = true;
        foreach (object bad in new object[] { float.NaN, double.PositiveInfinity, double.MaxValue, true, "0.5" })
        {
            sprite.overlayStrength = bad;
            Check((string)Row(Read(state), "overlay")["status"] == "unavailable", "bad native strength is not coerced to a valid cue");
        }
        sprite.overlayStrength = 0f;
        Check(Convert.ToDouble(Row(Read(state), "overlay")["strength"]) == 0,
            "real zero overlay strength is preserved");
        ((VisualColor)sprite.overlayColor).r = float.NaN;
        Check(Row(Read(state), "overlay")["color"] == null, "nonfinite color cannot enter planner state");
        sprite.overlayColor = new VisualColor(); sprite.enabled = false;
        Check(Row(Read(state), "overlay")["color"] == null, "disabled visual part is not exposed");
        sprite.enabled = true;
        var tinted = new VisualPart { tintable = true };
        ((VisualSprite)tinted.renderer.spriteRenderer).overlayStrength = 0.9f;
        parts.Add(tinted);
        Check((string)Row(Read(state), "overlay")["status"] == "ok", "team-tinted layers do not masquerade as effect color");
        tinted.tintable = false;
        Check(Row(Read(state), "overlay")["color"] == null, "inconsistent active effect layers are not collapsed into an invented cue");
        parts.Remove(tinted);
        object host = new BridgeMod();
        var plain = typeof(BridgeMod).GetMethod("URPublicInfo", F);
        var explicitRead = typeof(BridgeMod).GetMethod("URPublicInfoCore", F);
        object[] args = { state, null, null, true, null };
        string before = (string)plain.Invoke(host, args);
        string visibleBefore = (string)explicitRead.Invoke(host, new object[] { state, null, null, true, null, true });
        sprite.overlayStrength = 0.25f;
        string after = (string)plain.Invoke(host, args);
        string visibleAfter = (string)explicitRead.Invoke(host, new object[] { state, null, null, true, null, true });
        Check(before == after && !before.Contains("visualEffects") && visibleBefore != visibleAfter,
            "explicit observation updates visual cues while combat sampling stays mechanically stable");
        Check(state.EffectsReads == 0, "all public render paths leave raw effects untouched");
        Check(MaterialPropertyBlock.Created > 0 && MaterialPropertyBlock.Created == MaterialPropertyBlock.Disposed,
            "owned property-block scratch copies are released on successful and failed reads");
    }
}
