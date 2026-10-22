// Actual visible render cues, not UnitState.effects. No animation or UI callback is invoked.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private sealed class UnitVisualView
        {
            internal string ParticleStatus = "unavailable", OverlayStatus = "unavailable";
            internal List<string> Particles;
            internal string Color;
            internal object Strength;
            internal string Json()
            {
                var particles = new JsonObj().Str("status", ParticleStatus);
                if (Particles == null) particles.Raw("types", "null");
                else
                {
                    var names = new JsonArr();
                    foreach (string name in Particles) names.Raw(Quote(name));
                    particles.Raw("types", names.Close());
                }
                var overlay = new JsonObj().Str("status", OverlayStatus).Raw("color", Color ?? "null")
                    .Raw("strength", ResultsNumber(Strength));
                string status = ParticleStatus == "withheld" ? "withheld"
                    : ParticleStatus == "ok" && OverlayStatus == "ok" ? "ok"
                    : ParticleStatus == "ok" || ParticleStatus == "partial" || OverlayStatus == "ok" ? "partial" : "unavailable";
                return new JsonObj().Str("schema", "unit-visual-effects-1").Str("status", status)
                    .Raw("particles", particles.Close()).Raw("overlay", overlay.Close()).Close();
            }
        }

        // Caller has already passed TileUnitVisible. Recheck renderer association and
        // actual visibility before touching its effect dictionary or material inputs.
        private static string URVisibleEffects(object unit, bool disclosed, bool verified)
        {
            var view = new UnitVisualView();
            if (!disclosed)
            { view.ParticleStatus = view.OverlayStatus = "withheld"; return view.Json(); }
            if (!verified || unit == null) return view.Json();
            try
            {
                uint? id = NativeUInt32(URMember(unit, "id"));
                var type = FindGameType("MapRenderer");
                object renderer;
                if (!TryReadNativeStaticMember(type, "Current", out renderer)) return view.Json();
                if (!id.HasValue || renderer == null) return view.Json();
                var get = type.GetMethod("GetUnitInstance", BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(uint) }, null);
                if (get == null || get.ReturnType.Name != "Unit") return view.Json();
                object rendered = get.Invoke(renderer, new object[] { id.Value });
                if (rendered == null || !CRSameNative(URMember(rendered, "unitState"), unit)) return view.Json();
                var visible = HintMethod(rendered.GetType(), "GetVisible", new string[0], "Boolean");
                if (visible == null || !object.Equals(visible.Invoke(rendered, null), true)) return view.Json();
                URReadVisualParticles(rendered, view);
                URReadVisualOverlay(rendered, view);
            }
            catch (Exception error)
            { RecordNativeReadFailure(unit.GetType(), "visible_effects", error); /* Never an empty effects list. */ }
            return view.Json();
        }

        private static void URReadVisualParticles(object rendered, UnitVisualView view)
        {
            try
            {
                object dictionary;
                if (!URPanelMember(rendered, "unitEffectParticles", out dictionary) || dictionary == null) return;
                int? count = NativeInt32(URMember(dictionary, "Count"));
                var keys = URTypedKeys(URMember(dictionary, "Keys"));
                if (!count.HasValue || count.Value < 0 || keys == null || keys.Count != count.Value) return;
                var names = new SortedSet<string>(StringComparer.Ordinal);
                var seen = new HashSet<object>(); bool partial = false;
                foreach (object key in keys)
                {
                    if (key == null || !key.GetType().IsEnum || key.GetType().Name != "UnitEffect"
                        || !Enum.IsDefined(key.GetType(), key) || !seen.Add(key)) { partial = true; continue; }
                    try
                    {
                        var item = dictionary.GetType().GetMethod("get_Item", BindingFlags.Public | BindingFlags.Instance,
                            null, new[] { key.GetType() }, null);
                        object wrapper = item == null ? null : item.Invoke(dictionary, new[] { key });
                        object controller;
                        if (wrapper == null || !URPanelMember(wrapper, "pooledParticleController", out controller))
                        { partial = true; continue; }
                        if (controller == null) continue;
                        int? wrapperId = NativeInt32(URMember(wrapper, "poolId"));
                        int? controllerId = NativeInt32(URMember(controller, "poolId"));
                        if (!wrapperId.HasValue || !controllerId.HasValue) { partial = true; continue; }
                        if (wrapperId.Value == 0 || wrapperId != controllerId) continue;
                        bool? used = NativeBool(URMember(controller, "IsUsed"));
                        bool? active = NativeBool(URMember(URMember(controller, "gameObject"), "activeInHierarchy"));
                        if (!used.HasValue || !active.HasValue) { partial = true; continue; }
                        if (!used.Value || !active.Value) continue;
                        bool? playing = NativeBool(URMember(controller, "isPlaying"));
                        bool? stopping = NativeBool(URMember(controller, "isStopping"));
                        if (!playing.HasValue || !stopping.HasValue) { partial = true; continue; }
                        if (playing.Value) names.Add(SafeScalar(key));
                        else if (stopping.Value) partial = true;
                    }
                    catch { partial = true; }
                }
                view.Particles = new List<string>(names); view.ParticleStatus = partial ? "partial" : "ok";
            }
            catch { }
        }

        private static bool URVisualNumber(object value)
        {
            return (value is float || value is double) && ResultsNumber(value) != "null"
                && Math.Abs(Convert.ToDouble(value)) <= Single.MaxValue;
        }

        private static void URReadVisualOverlay(object rendered, UnitVisualView view)
        {
            try
            {
                // Unit.ColorizeUnit uses skin visual parts' SpriteRenderer property
                // blocks. WorldObject.SpriteRenderer can legitimately be null for units.
                var parts = ReadNativeList(URMember(URMember(rendered, "skinVisuals"), "visualParts"));
                if (parts == null) return;
                string selected = null; object selectedStrength = null;
                foreach (object part in parts)
                {
                    bool? tinted = NativeBool(URMember(part, "tintable"));
                    if (!tinted.HasValue) return;
                    // Tintable parts display team-color overrides, not the effect overlay.
                    if (tinted.Value) continue;
                    bool? active = NativeBool(URMember(URMember(part, "visualPart"), "activeInHierarchy"));
                    if (!active.HasValue) return;
                    if (!active.Value) continue;
                    object sprite = URMember(URMember(part, "renderer"), "spriteRenderer");
                    if (sprite == null) continue; // ColorizeUnit skips this exact absent slot.
                    bool? enabled = NativeBool(URMember(sprite, "enabled"));
                    bool? hierarchy = NativeBool(URMember(URMember(sprite, "gameObject"), "activeInHierarchy"));
                    if (!enabled.HasValue || !hierarchy.HasValue) return;
                    if (!enabled.Value || !hierarchy.Value) continue;
                    string color; object strength;
                    if (!URReadOverlayBlock(sprite, out color, out strength)) return;
                    if (selected != null && (selected != color || ResultsNumber(selectedStrength) != ResultsNumber(strength))) return;
                    selected = color; selectedStrength = strength;
                }
                if (selected != null)
                { view.Color = selected; view.Strength = selectedStrength; view.OverlayStatus = "ok"; }
            }
            catch { }
        }

        private static bool URReadOverlayBlock(object sprite, out string rgba, out object strength)
        {
            rgba = null; strength = null; object block = null;
            try
            {
                MethodInfo copy = null;
                foreach (var method in sprite.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (method.Name != "GetPropertyBlock" || method.ContainsGenericParameters || method.ReturnType != typeof(void)) continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length != 1 || parameters[0].ParameterType.Name != "MaterialPropertyBlock") continue;
                    if (copy != null) return false;
                    copy = method;
                }
                if (copy == null) return false;
                Type type = copy.GetParameters()[0].ParameterType;
                var hasColor = HintMethod(type, "HasColor", new[] { "String" }, "Boolean");
                var hasFloat = HintMethod(type, "HasFloat", new[] { "String" }, "Boolean");
                var getColor = HintMethod(type, "GetColor", new[] { "String" }, null);
                var getFloat = HintMethod(type, "GetFloat", new[] { "String" }, null);
                if (hasColor == null || hasFloat == null || getColor == null || getFloat == null) return false;
                block = Activator.CreateInstance(type); // Owned scratch copy; never set the renderer's block/material.
                copy.Invoke(sprite, new[] { block });
                if (!object.Equals(hasColor.Invoke(block, new object[] { "_OverlayColor" }), true)
                    || !object.Equals(hasFloat.Invoke(block, new object[] { "_OverlayStrength" }), true)) return false;
                object color = getColor.Invoke(block, new object[] { "_OverlayColor" });
                object amount = getFloat.Invoke(block, new object[] { "_OverlayStrength" });
                if (color == null || !URVisualNumber(amount)) return false;
                var values = new JsonArr();
                foreach (string component in new[] { "r", "g", "b", "a" })
                {
                    object value = URMember(color, component);
                    if (!URVisualNumber(value)) return false;
                    values.Raw(ResultsNumber(value));
                }
                rgba = values.Close(); strength = amount; return true;
            }
            catch { return false; }
            finally
            {
                if (block != null)
                {
                    try
                    {
                        var dispose = block.GetType().GetMethod("Dispose", BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.Instance, null, Type.EmptyTypes, null);
                        if (dispose != null && dispose.ReturnType == typeof(void)) dispose.Invoke(block, null);
                    }
                    catch { }
                }
            }
        }
    }
}
