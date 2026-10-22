using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.IO;
using System.Security.Cryptography;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private static object URMember(object o, string name)
        {
            object value;
            return TryReadNativeMember(o, name, out value) ? value : null;
        }

        // All queries use the live state's version, never the process-wide currentVersion.
        private static object URLogic(object gs)
        {
            return gs == null ? null : URMember(gs, "GameLogicData");
        }

        private static string URArg(Dictionary<string, string> args, string key)
        {
            string value;
            return args != null && args.TryGetValue(key, out value) ? value : "";
        }

        private string URError(string code)
        {
            bool rejected = code == "NOT_YOUR_TURN" || code == "UNIT_AND_ACTION_ID_REQUIRED"
                || code == "ACTION_NOT_IN_CURRENT_MENU" || code == "UNIT_ACTION_TARGET_ADAPTER_UNAVAILABLE"
                || code == "AMBIGUOUS_ACTION_ID";
            return new JsonObj().Bool("ok", false).Str("errorCode", code)
                .Str("verdict", rejected ? "rejected" : "unknown").Close();
        }

        private object URPlayer(object gs, byte me)
        {
            var players = ReadNativeList(Val(_playersProp, gs));
            if (players != null) foreach (var p in players)
                if (p != null && SafeScalar(URMember(p, "Id")) == me.ToString(CultureInfo.InvariantCulture)) return p;
            return null;
        }

        private sealed class URPublicUnit
        {
            public object Unit;
            public object Tile;
            public bool Own;
        }

        private List<URPublicUnit> URUnits(object gs, byte me)
        {
            return URUnitsCore(gs, me, null);
        }

        // A threat query needs both facts from the same map pass. Other unit
        // queries do not pay for an unused coordinate set or cache native tiles.
        private List<URPublicUnit> URUnitsCore(object gs, byte me, HashSet<string> exploredCoords)
        {
            var result = new List<URPublicUnit>();
            var map = Val(_mapProp, gs);
            var raw = URMember(map, "Tiles") ?? URMember(map, "tiles");
            var tiles = ReadNativeList(raw);
            if (map == null || tiles == null) return null;
            var fog = BuildFogGate(map, tiles, me);
            if (!fog.HasExplored) return null;
            foreach (var tile in tiles)
            {
                if (tile == null || !fog.Explored(tile)) continue;
                if (exploredCoords != null)
                {
                    string coord = SafeScalar(URMember(tile, "coordinates"));
                    if (coord != null) exploredCoords.Add(coord);
                }
                bool filtered;
                var unit = TileUnitVisible(tile, gs, me, out filtered);
                if (!filtered) return null;
                if (unit == null) continue;
                result.Add(new URPublicUnit { Unit = unit, Tile = tile,
                    Own = SafeScalar(URMember(unit, "owner")) == me.ToString(CultureInfo.InvariantCulture) });
            }
            return fog.Errors == 0 ? result : null;
        }

        private static object URNative(string typeName, string method, string[] types, object[] args)
        {
            var t = FindGameType(typeName) ?? FindTypeBySuffix(typeName);
            var m = t == null ? null : FindStaticUnique(t, method, types, null);
            if (m == null) return null;
            try { return m.Invoke(null, args); }
            catch (Exception error) { RecordNativeReadFailure(m.DeclaringType, m.Name, error); return null; }
        }

        private static object URUnitRule(string name, object unit, object gs)
        {
            return URNative("UnitDataExtensions", name, new[] { "UnitState", "GameState" }, new[] { unit, gs });
        }

        private static void URNumber(JsonObj o, string key, object value)
        {
            // These native unit fields/rules are integral. A boolean, numeric-looking
            // string or fractional value is a type mismatch, not a readable 0/1 or
            // permission to round the value. Preserve genuine zero and raw sentinels.
            if (value == null || value.GetType().IsEnum) { o.Raw(key, "null"); return; }
            var code = Type.GetTypeCode(value.GetType());
            o.Raw(key, code >= TypeCode.SByte && code <= TypeCode.UInt64 ? ResultsNumber(value) : "null");
        }

        private static string URScalars(object value)
        {
            if (value == null) return "null";
            var list = ReadNativeList(value);
            if (list == null) return "null";
            var a = new JsonArr();
            foreach (var x in list)
            {
                string scalar = SafeScalar(x);
                if (scalar == null) return "null";
                a.Raw(Quote(scalar));
            }
            return a.Close();
        }

        // This rule is proved by the health label and current/max-health popup in this
        // exact native build. It applies only to HP, never attack/defence or bonuses.
        private const string URHPNativeBuildSha256 = "490647AE5213158A3F35DD3DE3B50B808553A60E1F6BAB9B3A049F4108C3D655";
        // Original Steam 2.17.3.16375: audited UI routines, HP accessors,
        // float constants and complete ceilf helper match the proven display code.
        private const string URHPOriginalSteamBuildSha256 = "BFC4F4DE358A2FA129AB6621D2D2CB58AA56F8E4176FBC9EE13A316039D16926";
        private static readonly object URHPBuildLock = new object();
        private static bool? URHPBuildVerified;

        private static bool URHPNativeBuildVerified(string path)
        {
            try
            {
                using (var input = File.OpenRead(path))
                using (var sha = SHA256.Create())
                {
                    string digest = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
                    return digest == URHPNativeBuildSha256 || digest == URHPOriginalSteamBuildSha256;
                }
            }
            catch { return false; }
        }

        private static bool URHPCurrentBuildVerified()
        {
            lock (URHPBuildLock)
            {
                if (!URHPBuildVerified.HasValue)
                    URHPBuildVerified = URHPNativeBuildVerified(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameAssembly.dll"));
                return URHPBuildVerified.Value;
            }
        }

        private static object URHPDisplayValue(object raw, bool verified)
        {
            if (!verified || raw == null || raw.GetType().IsEnum) return null;
            var code = Type.GetTypeCode(raw.GetType());
            // Both sources are native integers (ushort health and int GetMaxHealth).
            if (code < TypeCode.SByte || code > TypeCode.UInt64) return null;
            try
            {
                long value = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                if (value < 0 || value > Int32.MaxValue) return null;
                float scaled = (float)value * 0.1f;
                return (int)Math.Ceiling((double)scaled);
            }
            catch { return null; }
        }

        private static string URHPDisplayFields(object healthRaw, object maxHealthRaw, bool verified)
        {
            var o = new JsonObj();
            URAppendHPDisplayFields(o, healthRaw, maxHealthRaw, verified);
            return o.Close();
        }

        private static void URAppendHPDisplayFields(JsonObj o, object healthRaw, object maxHealthRaw, bool verified)
        {
            o.Raw("displayScale", verified ? "0.1" : "null").Bool("scaleUnknown", !verified)
                .Str("displayScaleAppliesTo", "HP only: healthRaw/maxHealthRaw")
                .Str("displayRounding", verified ? "ceil(rawHP * float32(0.1))" : "unknown")
                .Str("displayAuthority", verified
                    ? "verified GameAssembly SHA256; UnitStatusDisplay.SetState + UnitPopup.set_UnitData native HP label paths"
                    : "native HP display rule unavailable for this build");
            URNumber(o, "healthDisplay", URHPDisplayValue(healthRaw, verified));
            URNumber(o, "maxHealthDisplay", URHPDisplayValue(maxHealthRaw, verified));
        }

        // Called by world on the same already visibility-filtered unit. Its fields are nested,
        // so the legacy schema receives no duplicate maxHealth keys.
        private void AppendNativeUnitFields(JsonObj o, object unit, object gs, bool own)
        {
            o.Raw("properties", URProperties(unit, gs, own));
        }

        private string URProperties(object unit, object gs, bool own)
        {
            var o = new JsonObj().Str("schema", "unit-properties-1");
            var data = URPanelData(unit, gs);
            object healthRaw = URMember(unit, "health"), maxHealthRaw = URUnitRule("GetMaxHealth", unit, gs);
            URNumber(o, "healthRaw", healthRaw);
            URNumber(o, "maxHealthRaw", maxHealthRaw);
            URAppendHPDisplayFields(o, healthRaw, maxHealthRaw, URHPCurrentBuildVerified());
            var basis = new JsonObj();
            foreach (var key in new[] { "health", "attack", "defence", "movement", "range", "cost" })
                URNumber(basis, key + "Raw", URMember(data, key));
            o.Raw("base", basis.Close());
            var effective = new JsonObj();
            var panelRules = new Dictionary<string, object>();
            panelRules["GetMaxHealth"] = maxHealthRaw;
            foreach (var rule in new[] { "GetAttack", "GetDefence", "GetDefenceBonus", "GetMovement", "GetRange" })
            {
                var value = URUnitRule(rule, unit, gs); panelRules[rule] = value;
                URNumber(effective, rule.Substring(3) + "Raw", value);
            }
            o.Raw("effective", effective.Close());
            o.Raw("abilities", URScalars(URMember(data, "unitAbilities")));
            o.Raw("effects", own ? URScalars(URMember(unit, "effects")) : "null");
            URNumber(o, "xp", own ? URMember(unit, "xp") : (object)URPublicKills(unit, gs));
            URNumber(o, "promotionLevel", own ? URMember(unit, "promotionLevel") : null);
            o.NullOrStr("promotesTo", SafeScalar(URMember(URMember(data, "promotesTo"), "type")));
            o.NullOrStr("upgradesFrom", SafeScalar(URMember(URMember(data, "upgradesFrom"), "type")));
            URNumber(o, "growthRate", URMember(data, "growthRate"));
            o.Str("authority", "GameState.GameLogicData; UnitState.UnitData; UnitDataExtensions.GetMaxHealth/GetAttack/GetDefence/GetDefenceBonus/GetMovement/GetRange");
            o.Raw("publicInfo", URPublicInfoCore(unit, gs, data, URHPCurrentBuildVerified(), panelRules, true));
            if (own)
            {
                o.Raw("adjacentInvisibleThreat", AdjacentInvisibleHint(unit, gs, true));
                URNumber(o, "fullCost", InvokeOn(unit, "GetFullCost", new[] { gs }));
                var ownerRaw = URMember(unit, "owner");
                byte owner;
                object player = ownerRaw != null && byte.TryParse(SafeScalar(ownerRaw), out owner) ? URPlayer(gs, owner) : null;
                URNumber(o, "promotionLimit", data == null || player == null ? null : InvokeOn(data, "getPromotionLimit", new[] { player, gs }));
                o.NullableBool("canMove", ReadBoolInvoke(unit, "CanMove"));
                o.NullableBool("canAttack", ReadBoolInvoke(unit, "CanAttack"));
                o.NullableBool("moved", ReadBoolMember(unit, "moved"));
                o.NullableBool("attacked", ReadBoolMember(unit, "attacked"));
                var passenger = URMember(unit, "passengerUnit");
                var carrier = new JsonObj().Str("authority", "UnitState.passengerUnit/leader/follower (owned units only)");
                URNumber(carrier, "leaderRaw", URMember(unit, "leader"));
                URNumber(carrier, "followerRaw", URMember(unit, "follower"));
                carrier.Raw("passenger", passenger == null ? "null" : URPassenger(passenger));
                carrier.Str("conversionSemantics", "passenger identity is native; leader/follower are raw linkage fields, not assumed carrier IDs");
                o.Raw("relations", carrier.Close());
            }
            else
            {
                o.Raw("adjacentInvisibleThreat", AdjacentInvisibleHint(unit, gs, false));
                o.Raw("fullCost", "null").Raw("relations", "null");
                o.Str("relationsStatus", "internal identity/linkage withheld; public passenger type is in publicInfo");
            }
            return o.Close();
        }

        private static string URPassenger(object passenger)
        {
            var o = new JsonObj();
            foreach (var key in new[] { "id", "owner", "type", "health", "promotionLevel", "xp", "coordinates" })
                o.NullOrStr(key, SafeScalar(URMember(passenger, key)));
            return o.Close();
        }

        private string UnitRuntimeSnapshot()
        {
            var gs = GameStateInstance();
            if (gs == null) return URError("NO_ACTIVE_GAME");
            var id = ResolveControlledIdentity(gs);
            if (!id.Verified) return URError("UNVERIFIED_PLAYER");
            var known = URUnits(gs, id.Id);
            if (known == null) return URError("VISIBLE_UNITS_UNAVAILABLE");
            var a = new JsonArr();
            foreach (var x in known)
            {
                var o = new JsonObj();
                o.NullOrStr("unitId", SafeScalar(URMember(x.Unit, "id")));
                o.NullOrStr("type", SafeScalar(URMember(x.Unit, "type")));
                o.NullOrStr("owner", SafeScalar(URMember(x.Unit, "owner")));
                o.NullOrStr("coords", SafeScalar(URMember(x.Tile, "coordinates")));
                o.Bool("isMine", x.Own).Raw("properties", URProperties(x.Unit, gs, x.Own));
                a.Raw(o.Close());
            }
            return new JsonObj().Bool("ok", true).Str("schema", "unit-properties-1")
                .Str("episodeId", EpisodeId(gs)).Raw("units", a.Close()).Close();
        }

        private string CurrentUnitDataKeys()
        {
            var gs = GameStateInstance();
            if (gs == null) return URError("NO_ACTIVE_GAME");
            var logic = URLogic(gs);
            if (logic == null) return URError("CURRENT_GAME_LOGIC_UNAVAILABLE");
            return URUnitDataCatalog(URMember(logic, "AllUnitData"));
        }

        private static List<object> URTypedKeys(object keys)
        {
            if (keys == null) return null;
            // IL2CPP nongeneric enumeration boxes structs/enums into Object wrappers. Read
            // the KeyCollection's typed enumerator Current instead of a boxed dictionary pair.
            try
            {
                MethodInfo factory = null;
                foreach (var m in keys.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "GetEnumerator" || m.IsGenericMethod || m.GetParameters().Length != 0) continue;
                    if (factory != null) { factory = null; break; }
                    factory = m;
                }
                if (factory != null)
                {
                    var iterator = factory.Invoke(keys, null);
                    if (iterator == null) return null;
                    var move = iterator.GetType().GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (move == null || move.ReturnType != typeof(bool)) return null;
                    var result = new List<object>();
                    try
                    {
                        int guard = 0;
                        while ((bool)move.Invoke(iterator, null))
                        {
                            if (++guard > 4096) return null;
                            var key = URMember(iterator, "Current");
                            if (key == null) return null;
                            result.Add(key);
                        }
                    }
                    finally
                    {
                        var dispose = iterator as IDisposable;
                        if (dispose != null) dispose.Dispose();
                    }
                    return result;
                }
                var enumerable = ReadNativeList(keys);
                if (enumerable == null) return null;
                var fallback = new List<object>();
                foreach (var key in enumerable) { if (key == null || fallback.Count >= 4096) return null; fallback.Add(key); }
                return fallback;
            }
            catch { return null; }
        }

        private static string URUnitDataCatalog(object dictionary)
        {
            var output = new JsonObj().Str("authority", "active GameState.GameLogicData.AllUnitData.Keys; native dictionary Count cross-check");
            object rawCount = URMember(dictionary, "Count");
            int nativeCount = NativeInt32(rawCount) ?? -1;
            URNumber(output, "nativeCount", rawCount);
            var keys = URTypedKeys(URMember(dictionary, "Keys"));
            var a = new JsonArr();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            bool clean = keys != null && nativeCount >= 0;
            if (keys != null) foreach (var key in keys)
            {
                string name = SafeScalar(key);
                if (string.IsNullOrEmpty(name) || name == "null" || !seen.Add(name)) { clean = false; continue; }
                a.Raw(Quote(name)); count++;
            }
            if (!clean || count != nativeCount)
                return output.Bool("ok", false).Str("status", "unknown").Str("errorCode", "UNIT_DATA_KEYS_UNREADABLE")
                    .Raw("count", "null").Raw("types", "null").Close();
            return output.Bool("ok", true).Str("status", "ok")
                .Raw("count", count.ToString(CultureInfo.InvariantCulture)).Raw("types", a.Close()).Close();
        }

        internal static string UnitRuntimeActionId(string commandType, string upgradeType)
        {
            return commandType == "Upgrade" ? "Upgrade:" + (upgradeType ?? "unknown") : commandType;
        }

        internal static string UnitRuntimeActionName(string type)
        {
            switch (type)
            {
                case "Promote": return "promote";
                case "Recover": return "recover";
                case "HealOthers": return "heal_others";
                case "Boost": return "boost";
                case "Upgrade": return "upgrade_unit";
                case "Disband": return "disband";
                case "Disembark": return "disembark";
                case "Explode": return "explode";
                default:
                    if (string.IsNullOrEmpty(type)) return null;
                    var name = new System.Text.StringBuilder();
                    for (int i = 0; i < type.Length; i++)
                    {
                        if (i > 0 && char.IsUpper(type[i])) name.Append('_');
                        name.Append(char.ToLowerInvariant(type[i]));
                    }
                    return name.ToString();
            }
        }

        private static object URConcreteCommand(object command, string type)
        {
            var target = FindGameType(type + "Command");
            if (target == null) return command; // Unknown future concrete menu types retain their own fields.
            object typed; string error;
            return TryNativeCast(command, target, out typed, out error) ? typed : null;
        }

        private List<object> URMenu(object gs, object player, object tile)
        {
            var raw = URNative("CommandUtils", "GetUnitActions",
                new[] { "GameState", "PlayerState", "TileData", "Boolean" }, new[] { gs, player, tile, (object)false });
            return ReadNativeCommandList(raw);
        }

        private static string URCommandType(object command)
        {
            return SafeScalar(InvokeOn(command, "GetCommandType", null));
        }

        private static string URUpgradeType(object command, string type)
        {
            if (type != "Upgrade") return null;
            return SafeScalar(URMember(URConcreteCommand(command, type), "Type"));
        }

        private static bool? URValid(object command, object gs)
        {
            string error;
            return NativeCommandValidity(command, gs, out error);
        }

        private static bool? URNativeMemberPublic(Type declaringType, MemberInfo member)
        {
            bool interop = false;
            for (var parent = declaringType; parent != null; parent = parent.BaseType)
                if (parent.FullName == "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase") interop = true;
            if (!interop) return true;
            // The interop generator makes native private fields into CLR public properties.
            // Inspect metadata flags before invoking a getter; a CLR public getter is insufficient.
            try
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                var pointerField = declaringType.GetField("NativeFieldInfoPtr_" + member.Name, flags);
                bool nativeField = pointerField != null;
                if (pointerField == null)
                    foreach (var field in declaringType.GetFields(flags))
                        if (field.Name.StartsWith("NativeMethodInfoPtr_get_" + member.Name + "_", StringComparison.Ordinal))
                        { if (pointerField != null) return null; pointerField = field; }
                if (pointerField == null) return null;
                var pointer = pointerField.GetValue(null);
                if (!(pointer is IntPtr) || (IntPtr)pointer == IntPtr.Zero) return null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var api = assembly.GetType("Il2CppInterop.Runtime.IL2CPP", false);
                    if (api == null) continue;
                    string name = nativeField ? "il2cpp_field_get_flags" : "il2cpp_method_get_flags";
                    object[] arguments = nativeField ? new[] { pointer } : new[] { pointer, (object)(uint)0 };
                    object result = null;
                    var method = api.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (method != null) result = method.Invoke(null, arguments);
                    else
                    {
                        var function = api.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        var invoke = function == null ? null : function.GetValue(null) as Delegate;
                        if (invoke != null) result = invoke.DynamicInvoke(arguments);
                    }
                    if (result != null) return (Convert.ToUInt32(result, CultureInfo.InvariantCulture) & 7u) == 6u;
                }
            }
            catch { }
            return null;
        }

        internal static string URCommandParameters(object command, out bool readable)
        {
            readable = command != null;
            var row = new JsonObj();
            if (command == null) return row.Close();
            var members = new SortedDictionary<string, object>(StringComparer.Ordinal);
            var type = command.GetType();
            if (type.Name == "CommandBase") readable = false;
            while (type != null && type.Name != "CommandBase" && type != typeof(object))
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                foreach (var field in type.GetFields(flags))
                {
                    bool? visible = URNativeMemberPublic(type, field);
                    if (visible != true) { if (!visible.HasValue) readable = false; continue; }
                    try { members[field.Name] = field.GetValue(command); } catch { readable = false; }
                }
                foreach (var property in type.GetProperties(flags))
                {
                    if (property.GetIndexParameters().Length != 0 || property.GetGetMethod() == null) continue;
                    bool? visible = URNativeMemberPublic(type, property);
                    if (visible != true) { if (!visible.HasValue) readable = false; continue; }
                    try { members[property.Name] = property.GetValue(command, null); } catch { readable = false; }
                }
                type = type.BaseType;
            }
            foreach (var item in members)
            {
                var value = item.Value;
                if (value == null) { row.Raw(item.Key, "null"); readable = false; continue; }
                var t = value.GetType();
                if (t.IsEnum || t == typeof(string) || t.Name == "WorldCoordinates") row.Str(item.Key, SafeScalar(value));
                else if (value is bool) row.Bool(item.Key, (bool)value);
                else if (ResultsNumber(value) != "null") row.Raw(item.Key, ResultsNumber(value));
                else { row.Raw(item.Key, "null"); readable = false; }
            }
            return row.Close();
        }

        private static string URMenuActionId(string type, string kind, string parameters, List<object> menu)
        {
            string legacy = UnitRuntimeActionId(type, kind);
            int count = 0;
            foreach (var item in menu)
                if (URCommandType(item) == type && URUpgradeType(item, type) == kind) count++;
            if (count <= 1) return legacy;
            // Stable across a fresh native menu and distinguishes multiple adjacent ice tiles.
            using (var hash = SHA256.Create())
                return legacy + ":" + BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(parameters)))
                    .Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        // This describes proof coverage only. It must never filter the native menu or
        // disable the shared prepared-command adapter for a newly added native action.
        private static bool UREffectAdapterSupported(string type)
        {
            switch (type)
            {
                case "Move": case "Attack": case "Capture": case "Disband":
                case "Disembark": case "Upgrade": case "Promote": case "Recover":
                case "HealOthers": case "Boost": case "ExamineRuins": case "Build":
                case "Train": case "Research": case "Destroy": case "BreakIce":
                case "CityReward": case "EndTurn": return true;
                default: return false;
            }
        }

        private static JsonObj URMenuActionDescriptor(string type, string kind, string parameters,
            bool readable, bool? valid, bool myTurn, List<object> menu)
        {
            bool prepared = readable && (type != "Upgrade" || !string.IsNullOrEmpty(kind));
            bool effect = UREffectAdapterSupported(type);
            string unavailable = !myTurn ? "NOT_YOUR_TURN"
                : !readable ? "NATIVE_COMMAND_PARAMETERS_UNAVAILABLE"
                : !prepared ? "UPGRADE_TYPE_UNAVAILABLE"
                : !valid.HasValue ? "NATIVE_VALIDATION_UNAVAILABLE"
                : !valid.Value ? "NATIVE_COMMAND_INVALID" : null;
            return new JsonObj().Str("actionId", URMenuActionId(type, kind, parameters, menu))
                .Str("commandType", type).Bool("executable", prepared && valid == true && myTurn)
                .NullableBool("nativeValid", valid).Raw("parameters", parameters)
                .Str("actionName", UnitRuntimeActionName(type)).NullOrStr("kind", kind)
                .Bool("parametersReadable", readable)
                .Str("executionAdapter", prepared ? "native-menu-command" : "unavailable")
                .NullOrStr("unavailableReason", unavailable)
                .Bool("effectAdapterSupported", effect)
                .Str("settlementCoverage", effect ? "action-specific-partial" : "unknown")
                .Str("settlementNote", "coverage is not execution success; query the original settlement ticket");
        }

        private string URTargets(string type, URPublicUnit u, object gs, byte me)
        {
            string method = type == "HealOthers" ? "GetHealOptions" : type == "Boost" ? "GetBoostOptions" : null;
            if (method == null) return "null";
            var raw = type == "HealOthers"
                ? URNative("TileDataExtensions", method, new[] { "TileData", "Byte", "GameState", "Boolean" }, new[] { u.Tile, (object)me, gs, (object)false })
                : URNative("TileDataExtensions", method, new[] { "TileData", "Byte", "GameState" }, new[] { u.Tile, (object)me, gs });
            var e = ReadNativeList(raw);
            if (e == null) return "null";
            var a = new JsonArr();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tile in e)
            {
                bool filtered;
                var unit = TileUnitVisible(tile, gs, me, out filtered);
                // Healing / Boost can only publish a currently disclosed target.
                if (!filtered) return "null";
                if (unit == null) continue;
                string uid = SafeScalar(URMember(unit, "id"));
                if (uid == null || !seen.Add(uid)) continue;
                a.Raw(new JsonObj().Str("unitId", uid)
                    .NullOrStr("coords", SafeScalar(URMember(tile, "coordinates")))
                    .NullOrStr("owner", SafeScalar(URMember(unit, "owner"))).Close());
            }
            return a.Close();
        }

        private string UnitActionsSnapshot(Dictionary<string, string> args)
        {
            var gs = GameStateInstance();
            if (gs == null) return URError("NO_ACTIVE_GAME");
            var id = ResolveControlledIdentity(gs);
            if (!id.Verified) return URError("UNVERIFIED_PLAYER");
            var player = URPlayer(gs, id.Id);
            if (player == null) return URError("PLAYER_UNAVAILABLE");
            string ask = URArg(args, "unit");
            bool found = false, complete = true;
            bool myTurn = CurrentPlayerId(gs) == id.Id;
            var units = new JsonArr();
            var known = URUnits(gs, id.Id);
            if (known == null) return URError("VISIBLE_UNITS_UNAVAILABLE");
            foreach (var u in known)
            {
                if (!u.Own) continue;
                string uid = SafeScalar(URMember(u.Unit, "id"));
                if (ask.Length > 0 && ask != uid) continue;
                found = true;
                var item = new JsonObj().NullOrStr("unitId", uid)
                    .NullOrStr("coords", SafeScalar(URMember(u.Tile, "coordinates")));
                var commands = URMenu(gs, player, u.Tile);
                if (commands == null)
                {
                    complete = false;
                    units.Raw(item.Str("status", "unknown").Raw("actions", "null").Close());
                    continue;
                }
                var actions = new JsonArr();
                bool unitComplete = true;
                foreach (var command in commands)
                {
                    string type = URCommandType(command);
                    if (string.IsNullOrEmpty(type)) { complete = false; unitComplete = false; continue; }
                    string kind = URUpgradeType(command, type);
                    var typedCommand = URConcreteCommand(command, type);
                    bool readable;
                    string parameters = URCommandParameters(typedCommand, out readable);
                    bool? valid = URValid(typedCommand, gs);
                    var a = URMenuActionDescriptor(type, kind, parameters, readable, valid, myTurn, commands);
                    a.Raw("cost", "null"); // Free is not inferred from unreadable cost.
                    if (type == "Upgrade" && kind != null)
                    {
                        var typed = URConcreteCommand(command, type);
                        var rawKind = URMember(typed, "Type");
                        var logic = URLogic(gs);
                        var data = logic == null || rawKind == null ? null : InvokeOn(logic, "GetUnitData", new[] { rawKind });
                        URNumber(a, "nativeUpgradeCost", URMember(data, "cost"));
                        a.Str("costAuthority", "current GameLogicData.GetUnitData(UpgradeCommand.Type).cost; command validation remains authoritative");
                    }
                    a.Str("targetMode", type == "HealOthers" || type == "Boost" ? "native-area" : "native-bound-parameters");
                    a.Raw("targets", URTargets(type, u, gs, id.Id));
                    if (type == "Recover")
                    {
                        URNumber(a, "healAmountRaw", URNative("ActionUtils", "GetHealAmount", new[] { "GameState", "TileData" }, new[] { gs, u.Tile }));
                        a.Str("healAmountAuthority", "ActionUtils.GetHealAmount(GameState, TileData); final HP remains authoritative");
                    }
                    a.Raw("actionConsumption", "null").Str("consumptionStatus", "observe native moved/attacked/effects after settlement");
                    actions.Raw(a.Close());
                }
                item.Str("status", unitComplete ? "ok" : "partial").Raw("actions", actions.Close());
                units.Raw(item.Close());
            }
            if (ask.Length > 0 && !found) return URError("OWN_UNIT_NOT_VISIBLE");
            return new JsonObj().Bool("ok", true).Str("schema", "unit-actions-1")
                .Str("status", complete ? "ok" : "partial").Bool("myTurn", myTurn)
                .Str("authority", "CommandUtils.GetUnitActions(GameState, PlayerState, TileData, includeUnavailable:false)")
                .Raw("units", units.Close()).Close();
        }

        private string ExecuteUnitAction(Dictionary<string, string> args)
        {
            var gs = GameStateInstance();
            if (gs == null) return ActionPreparationError("NO_ACTIVE_GAME", "native unit action could not be prepared");
            var id = ResolveControlledIdentity(gs);
            if (!id.Verified) return ActionPreparationError("UNVERIFIED_PLAYER", "native unit action could not be prepared");
            if (CurrentPlayerId(gs) != id.Id) return ActionPreparationError("NOT_YOUR_TURN", "native unit action could not be prepared");
            string ask = URArg(args, "unit"), wanted = URArg(args, "action_id");
            if (ask.Length == 0 || wanted.Length == 0) return ActionPreparationError("UNIT_AND_ACTION_ID_REQUIRED", "native unit action could not be prepared");
            var player = URPlayer(gs, id.Id);
            if (player == null) return ActionPreparationError("PLAYER_UNAVAILABLE", "native unit action could not be prepared");
            var known = URUnits(gs, id.Id);
            if (known == null) return ActionPreparationError("VISIBLE_UNITS_UNAVAILABLE", "native unit action could not be prepared");
            foreach (var u in known)
            {
                if (!u.Own || SafeScalar(URMember(u.Unit, "id")) != ask) continue;
                var menu = URMenu(gs, player, u.Tile);
                if (menu == null) return ActionPreparationError("NATIVE_MENU_UNAVAILABLE", "native unit action could not be prepared");
                object selected = null;
                string selectedType = null, selectedKind = null;
                foreach (var command in menu)
                {
                    string type = URCommandType(command);
                    if (type == null) continue;
                    string kind = URUpgradeType(command, type);
                    var typed = URConcreteCommand(command, type);
                    bool readable;
                    string parameters = URCommandParameters(typed, out readable);
                    if (URMenuActionId(type, kind, parameters, menu) != wanted) continue;
                    if (!readable) return ActionPreparationError("NATIVE_COMMAND_PARAMETERS_UNAVAILABLE", "native unit action could not be prepared");
                    if (selected != null) return ActionPreparationError("AMBIGUOUS_ACTION_ID", "native unit action could not be prepared");
                    selected = typed; selectedType = type; selectedKind = kind;
                }
                if (selected == null) return ActionPreparationError("ACTION_NOT_IN_CURRENT_MENU", "native unit action could not be prepared");
                string name = UnitRuntimeActionName(selectedType);
                if (name == null || (selectedType == "Upgrade" && string.IsNullOrEmpty(selectedKind)))
                    return ActionPreparationError("UNIT_ACTION_TARGET_ADAPTER_UNAVAILABLE", "native unit action could not be prepared");
                if (selectedType == "Build") selectedKind = SafeScalar(URMember(selected, "Type"));
                // Same command object is revalidated/executed through the shared pipeline.
                // Never call CommandBase.Execute directly (would bypass cost/settlement guards).
                return ExecuteGameAction(name, "", "", "", "",
                    SafeScalar(URMember(u.Tile, "coordinates")) ?? "", selectedKind ?? "", "", selected, ask);
            }
            return ActionPreparationError("OWN_UNIT_NOT_VISIBLE", "native unit action could not be prepared");
        }

        private string VisibleThreatSnapshot()
        {
            var gs = GameStateInstance();
            if (gs == null) return URError("NO_ACTIVE_GAME");
            var id = ResolveControlledIdentity(gs);
            if (!id.Verified) return URError("UNVERIFIED_PLAYER");
            var explored = new HashSet<string>(StringComparer.Ordinal);
            var known = URUnitsCore(gs, id.Id, explored);
            if (known == null) return URError("VISIBLE_UNITS_UNAVAILABLE");
            var a = new JsonArr();
            foreach (var u in known)
            {
                if (u.Own) continue;
                var item = new JsonObj().NullOrStr("unitId", SafeScalar(URMember(u.Unit, "id")))
                    .NullOrStr("type", SafeScalar(URMember(u.Unit, "type")))
                    .NullOrStr("owner", SafeScalar(URMember(u.Unit, "owner")))
                    .NullOrStr("coords", SafeScalar(URMember(u.Tile, "coordinates")));
                var data = URMember(u.Unit, "UnitData");
                URNumber(item, "movementRaw", URUnitRule("GetMovement", u.Unit, gs));
                var range = URUnitRule("GetRange", u.Unit, gs);
                URNumber(item, "rangeRaw", range);
                item.Raw("abilities", URScalars(URMember(data, "unitAbilities")));
                item.Raw("stationaryRangeEnvelope", URAttackEnvelope(u, range, explored, known));
                // Querying enemy native reach on live gs depends on their private exploration,
                // hidden blockers, ZOC and roads. Filtering its output cannot undo that leak.
                // Do not clear moved/attacked or clone an unfiltered live state to simulate.
                item.Str("coverage", "public-native-properties-and-stationary-range-geometry").Str("nextTurnThreatStatus", "unknown")
                    .Raw("reachableTiles", "null").Raw("attackTargetsAfterMove", "null")
                    .Raw("persistSequence", "null")
                    .Str("reason", "safe player-knowledge native pathfinding state not available; next-turn reset, roads, ZOC, Dash/Persist/Stiff not simulated");
                a.Raw(item.Close());
            }
            return new JsonObj().Bool("ok", true).Str("schema", "visible-threat-1").Str("status", "partial")
                .Bool("completeThreatMap", false).Str("episodeId", EpisodeId(gs))
                .Str("authority", "TileData.GetUnit(includeHidden:false); UnitDataExtensions.GetMovement/GetRange; public UnitData.unitAbilities")
                .Raw("enemies", a.Close()).Str("hiddenEnemyCoverage", "unknown")
                .Str("planningRule", "null reach is unknown, never evidence a landing is safe or an exhausted enemy cannot act next turn").Close();
        }

        private static string URAttackEnvelope(URPublicUnit enemy, object rangeRaw,
            HashSet<string> disclosedCoords, List<URPublicUnit> units)
        {
            var output = new JsonObj().Bool("geometryOnly", true).Bool("nativeLegalAttackTargets", false)
                .Str("authority", "WorldCoordinates.GetArea(center, native GetRange, allowDiagonal:true, includeCenter:false); filtered to player-explored coordinates")
                .Str("limitations", "stationary geometric range only; diplomacy, line-of-action, Dash/Persist/Stiff, roads, ZOC, remaining actions and movement are not validated; empty candidates are not evidence of safety");
            int? nativeRadius = NativeInt32(rangeRaw);
            if (!nativeRadius.HasValue || disclosedCoords == null || nativeRadius.Value < 0 || nativeRadius.Value > 32)
                return output.Str("status", "unknown").Raw("tiles", "null").Raw("ownUnitCandidates", "null").Close();
            int radius = nativeRadius.Value;
            var center = URMember(enemy.Tile, "coordinates");
            var area = URNative("WorldCoordinates", "GetArea",
                new[] { "WorldCoordinates", "Int32", "Boolean", "Boolean" },
                new[] { center, (object)radius, (object)true, (object)false });
            var e = ReadNativeList(area);
            if (e == null) return output.Str("status", "unknown").Raw("tiles", "null").Raw("ownUnitCandidates", "null").Close();
            var coords = new HashSet<string>(StringComparer.Ordinal);
            var tiles = new JsonArr();
            foreach (var c in e)
            {
                string key = SafeScalar(c);
                if (key == null) return output.Str("status", "unknown").Raw("tiles", "null").Raw("ownUnitCandidates", "null").Close();
                if (!disclosedCoords.Contains(key) || !coords.Add(key)) continue;
                tiles.Raw(Quote(key));
            }
            var candidates = new JsonArr();
            foreach (var unit in units)
            {
                if (!unit.Own) continue;
                string key = SafeScalar(URMember(unit.Tile, "coordinates"));
                if (key == null || !coords.Contains(key)) continue;
                candidates.Raw(new JsonObj().NullOrStr("unitId", SafeScalar(URMember(unit.Unit, "id")))
                    .Str("coords", key).Close());
            }
            return output.Str("status", "geometry-known").Raw("tiles", tiles.Close()).Raw("ownUnitCandidates", candidates.Close()).Close();
        }
    }
}

