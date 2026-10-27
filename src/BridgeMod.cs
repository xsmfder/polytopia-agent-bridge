using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;





namespace PolytopiaAgent
{
    /// <summary>
    /// Local-only bridge between an external agent and a running Polytopia process.
    ///
    ///   transport  : two files under .offline-work/agent-bridge (fully local, auditable)
    ///                cmd.jsonl <- agent appends {"id":N,"cmd":"..."} lines
    ///                out.jsonl -> game appends {"reqId":N,...} lines
    ///   observation: pushed by the game's own update loop (no screenshots)
    ///   actions    : executed on the game's main thread by invoking the game's own logic
    ///
    /// No entitlement or licence logic is touched anywhere in this file.
    /// </summary>
    public partial class BridgeMod
    {
        internal static readonly string WorkDir =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".offline-work", "agent-bridge");
        internal static string LogPath { get { return Path.Combine(WorkDir, "bridge.log"); } }
        internal static string CmdPath { get { return Path.Combine(WorkDir, "cmd.jsonl"); } }
        internal static string OutPath { get { return Path.Combine(WorkDir, "out.jsonl"); } }

        private long _cmdOffset;
        private string _pending = "";
        private long _lastPollTick;

        private Type _gameManagerType;
        private Type _gameStateType;
        private PropertyInfo _gameStateProp;
        private PropertyInfo _currentTurnProp;
        private PropertyInfo _currentPlayerIndexProp;
        private PropertyInfo _mapProp;
        private PropertyInfo _playersProp;
        private PropertyInfo _currentStateProp;
        private PropertyInfo _currentUnitIdProp;

        private long _tick;
        private long _observationRevision;
        private long _lastSnapshotTicks;
        private long _lastProbeTick;
        private string _lastKey = "";
        private long _snapshotCount;
        private long _lastStateErrorTick;
        private bool GameActive;
        private PolytopiaPublicMarkers.PublicMarkerHooks _publicMarkerHooks;

        public void InitializeRuntime(string loader,string loaderVersion)
        {
            try
            {
                BeginRuntimeHost(loader,loaderVersion);
                Directory.CreateDirectory(WorkDir);
                Log("InitializeRuntime: " + loader + " " + loaderVersion
                    + " runtime=" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
                ResolveTypes();
                LoadControlledSeat();
                _fixedControlledSeat=PreserveProcessSeat(_fixedControlledSeat);
                _controlledSeatLoaded = true;
                InitializePublicMarkers();
                StartFileTransport();
                _performanceHost = this;
                StartReadPreparation();
            }
            catch (Exception ex)
            {
                Log("FATAL on init: " + ex);
                StopRuntime(true);
                throw;
            }
        }

        // ---------------------------------------------------------------- types

        private void ResolveTypes()
        {
            _gameManagerType = BridgeMod.FindType("Il2Cpp.GameManager") ?? FindType("GameManager")
                ?? FindType("PolytopiaAssembly.GameManager");
            if (_gameManagerType == null) { Log("resolve: GameManager NOT found"); return; }
            _gameStateProp = _gameManagerType.GetProperty("GameState",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            _gameStateType = _gameStateProp != null ? _gameStateProp.PropertyType
                : FindType("Il2Cpp.GameState") ?? FindType("GameState");
            Log("resolve: GameManager=" + _gameManagerType.FullName + " GameStateType=" + (_gameStateType != null ? _gameStateType.FullName : "null"));

            if (_gameStateType != null)
            {
                _currentTurnProp = Prop(_gameStateType, "CurrentTurn");
                _currentPlayerIndexProp = Prop(_gameStateType, "CurrentPlayerIndex");
                _mapProp = Prop(_gameStateType, "Map");
                _playersProp = Prop(_gameStateType, "PlayerStates");
                _currentStateProp = Prop(_gameStateType, "CurrentState");
                _currentUnitIdProp = Prop(_gameStateType, "CurrentUnitId");
            }
        }

        private static PropertyInfo Prop(Type t, string name)
        {
            return t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        }

        private void InitializePublicMarkers()
        {
            try
            {
                _publicMarkerHooks = PolytopiaPublicMarkers.PublicMarkerHooks.CreateNative(FindType, EpisodeId,
                    delegate(object state) { if (!PrivateSamplingAllowed(state)) return null; var controlled = ResolveControlledIdentity(state); return controlled.Verified ? (int?)controlled.Id : null; },
                    delegate { return _observationRevision; });
                bool installed = _publicMarkerHooks.Install(HarmonyInstance);
                Log("public markers: natural-render hooks " + (installed ? "installed; completion still required" : "unavailable"));
            }
            catch (Exception)
            {
                _publicMarkerHooks = null;
                Log("public markers: binding/hook installation unavailable");
            }
        }

        internal PolytopiaPublicMarkers.PublicMarkerHooks.Domains PublicMarkerWorld(object gs, string episode, int? controlled, int? turn, long revision)
        {
            return _publicMarkerHooks == null
                ? PolytopiaPublicMarkers.PublicMarkerHooks.UnavailableDomains(gs, episode, controlled, turn, revision)
                : _publicMarkerHooks.BuildWorld(gs, episode, controlled, turn, revision);
        }

        /// <summary>
        /// The player-facing name of a tech-list element: its own name member when it is a data
        /// object, otherwise the enum/type name. Returns null when no name can be established.
        /// </summary>
        /// <summary>
        /// The research item's technology name, taken from TechData's own `type` / `get_type`
        /// accessor (TechData.cs declares it; RVA 0x4191C0) and converted to the stable enum name.
        /// A CLR type name, a raw number or an undefined enum value is NOT a name: the caller keeps
        /// the item unresolved and must not call the set complete.
        /// </summary>
        internal static string TechEnumNameOf(object el, out string why)
        {
            why = null;
            if (el == null) { why = "no element"; return null; }
            object v;
            if (!TryReadNativeMember(el, "type", out v) || v == null)
            { why = "type/get_type unavailable"; return null; }
            // The value must be the REAL TechData.Type enum - not any other enum in the process.
            var techEnum = FindType("Il2CppPolytopia.Data.TechData+Type")
                ?? FindGameType("TechData+Type");
            return EnumRuleIdentityOfExpected(v, techEnum, out why);
        }


        private static string MemberNameOf(object el)
        {
            if (el == null) { return null; }
            foreach (var n in new[] { "name", "Name", "id", "Id", "type", "Type", "techType", "TechType", "tech", "Tech" })
            {
                var v = SafeScalar(MemberWithAccessor(el, n));
                if (!string.IsNullOrEmpty(v) && v != "null") { return v; }
            }
            var s = SafeScalar(el);
            if (!string.IsNullOrEmpty(s) && s != "null"
                && s.IndexOf("System.", StringComparison.Ordinal) != 0) { return s; }
            try { return el.ToString(); } catch { return null; }
        }
        /// <summary>JSON string literal for a plain identifier (no escaping surprises).</summary>
        private static string Quote(string s)
        {
            return "\"" + Esc(s) + "\"";
        }

        private static Type FindType(string name)
        { return CachedFindType(name); }

        internal static Type UncachedFindType(string name)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = a.GetType(name, false); } catch { }
                if (t != null) { return t; }
            }
            return null;
        }

        /// <summary>
        /// Find a nested type from its "Outer+Inner" name, ignoring the enclosing namespace.
        ///
        /// Many unrelated types share a leaf name (dozens of "Type" enums exist), so matching on
        /// the leaf alone picks the wrong one. This requires BOTH the declaring type name and the
        /// leaf to match, and prefers the Il2CppPolytopia game assembly.
        /// </summary>
        /// <summary>
        /// Resolve a type from its SIMPLE name across all loaded assemblies, preferring the
        /// Il2CppPolytopia game assembly. `FindType` performs an exact full-name lookup, so a bare
        /// "GameLogicData" never matched "Il2CppPolytopia.Data.GameLogicData" - that is exactly why
        /// the resource-category probe reported `unavailable` in the first live run.
        /// </summary>
        internal static Type FindTypeLoosely(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName)) { return null; }
            Type fallback = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string an = null;
                try { an = a.GetName().Name ?? ""; } catch { an = ""; }
                Type[] types;
                try { types = ResolverTypesOf(a); } catch { continue; }
                foreach (var t in types)
                {
                    if (t == null || t.Name != simpleName) { continue; }
                    if (IsGameInteropAssemblyName(an)) { return t; }
                    if (fallback == null) { fallback = t; }
                }
            }
            return fallback;
        }
        /// <summary>
        /// Resolve a type by simple name ONLY when the answer is unambiguous: candidates are matched
        /// on the full name, then narrowed to the Il2CppPolytopia* game assembly, and >1 remaining
        /// candidate yields null (= unknown) instead of an arbitrary pick. A "first match wins"
        /// lookup silently selects library types - e.g. the simple name "ResourceData" also exists
        /// as AsmResolver.PE.Win32Resources.ResourceData.
        /// </summary>
        internal static Type FindGameType(string simpleName)
        { return CachedFindGameType(simpleName); }

        internal static bool IsGameInteropAssemblyName(string name)
        {
            return name=="Il2CppPolytopiaAssembly" || name=="Il2CppGameLogicAssembly" || name=="Il2CppPolytopiaBackendBase"
                || name=="PolytopiaAssembly" || name=="GameLogicAssembly" || name=="PolytopiaBackendBase";
        }

        internal static Type UncachedFindGameType(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName)) { return null; }
            var game = new List<Type>();
            var other = new List<Type>();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string an;
                try { an = a.GetName().Name ?? ""; } catch { an = ""; }
                Type[] types;
                try { types = ResolverGameTypeCandidates(a, simpleName); } catch { continue; }
                foreach (var t in types)
                {
                    if (t == null || t.FullName == null) { continue; }
                    if (t.FullName != simpleName
                        && !t.FullName.EndsWith("." + simpleName, StringComparison.Ordinal)) { continue; }
                    if (IsGameInteropAssemblyName(an)) { game.Add(t); }
                    else { other.Add(t); }
                }
            }
            if (game.Count == 1) { return game[0]; }
            if (game.Count == 0 && other.Count == 1) { return other[0]; }
            return null;    // 0 candidates or ambiguous -> unknown, never a guess
        }


        /// <summary>
        /// Strict enum name constrained to ONE expected enum type: the value must be an instance of
        /// that exact enum (by type identity, falling back to full name), must be defined, and must
        /// have a name. Any other enum (or a number/CLR name) is refused.
        /// </summary>
        internal static string EnumNameOfExpected(object value, Type expected, out string why)
        {
            why = null;
            if (value == null) { why = "no value"; return null; }
            var vt = value.GetType();
            if (!vt.IsEnum) { why = "value is not an enum"; return null; }
            // Fail closed: without an expected enum type we cannot certify WHICH enum this value
            // belongs to, so it is unknown rather than "some enum".
            if (expected == null) { why = "expected enum type not resolved"; return null; }
            // EXACT type identity: the same FullName in a different assembly is not the same enum.
            if (vt != expected) { why = "enum is not the same type object"; return null; }
            if (!Enum.IsDefined(vt, value)) { why = "enum value undefined"; return null; }
            var nm = Enum.GetName(vt, value);
            if (string.IsNullOrEmpty(nm)) { why = "enum name unresolved"; return null; }
            return nm;
        }

        /// <summary>
        /// The train options for MY OWN, VISIBLE cities, taken from the game's own UI path
        /// (CommandUtils.GetTrainableUnits(gameState, playerState, tileData, includeUnavailable:false)).
        /// Only the controlled player's cities that are already in the standard legal observation are
        /// visited; nothing is enumerated from hidden tiles or other players' cities, and the response
        /// carries no native objects, pointers or diagnostics.
        /// </summary>
        /// <summary>
        /// The controlled player's OWN train options, taken from the game's UI path:
        /// `CommandUtils.GetTrainableUnits(gameState, playerState, tileData, includeUnavailable:false)`
        /// returns `List&lt;TrainCommand&gt;`. A TrainCommand carries Type/Coordinates but NO cost, so each
        /// price is read from THIS game's GameLogicData instance through GetUnitData(UnitData.Type).
        /// Only cities already validated by the standard observation are visited; nothing is scanned
        /// beyond them and no native object/pointer/signature ever reaches the planner projection.
        /// </summary>
        private string TrainOptions(object gs, object ps, List<object> myCityTiles)
        { return TrainOptionsForSites(gs, ps, myCityTiles, null); }

        private string TrainOptionsForSites(object gs, object ps, List<object> myCityTiles,
            List<OwnedWaterTrainSite> waterSites)
        {
            var o = new JsonObj();
            var codes = new JsonArr();
            var gl = GameLogicDataOf(gs);
            var cu = FindType("Il2CppPolytopia.CommandUtils") ?? FindGameType("CommandUtils");
            var unitEnum = FindType("Il2CppPolytopia.Data.UnitData+Type")
                ?? FindGameType("UnitData+Type");
            if (cu == null) { codes.Raw(Quote("commandutils-unavailable")); }
            if (gl == null) { codes.Raw(Quote("gamelogicdata-unavailable")); }
            if (ps == null) { codes.Raw(Quote("playerstate-unavailable")); }
            if (unitEnum == null) { codes.Raw(Quote("unittype-enum-unavailable")); }

            // Unique match only: wrong arity/parameter types or more than one candidate is UNKNOWN.
            MethodInfo train = null;
            int trainHits = 0;
            if (cu != null)
            {
                foreach (var cand in cu.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                   | BindingFlags.Static))
                {
                    if (!NativeMethodSignatureMatches(cand, "GetTrainableUnits",
                        new[] { "GameState", "PlayerState", "TileData", "Boolean" }, null)
                        || cand.ReturnType == typeof(void)) continue;
                    // The returned collection is checked by ReadNativeList, never a
                    // substring in the declared CLR type name (List/IList/etc.).
                    trainHits++;
                    train = cand;
                }
            }
            if (trainHits != 1) { codes.Raw(Quote(trainHits == 0 ? "gettrainableunits-missing"
                                                                   : "gettrainableunits-ambiguous")); }

            MethodInfo getUnitData = null;
            int gudHits = 0;
            if (gl != null && unitEnum != null)
            {
                foreach (var cand in gl.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                             | BindingFlags.Instance))
                {
                    if (!NativeMethodSignatureMatches(cand, "GetUnitData", new[] { "Type" }, "UnitData")) continue;
                    var q = cand.GetParameters();
                    if (q[0].ParameterType != unitEnum) { continue; }
                    gudHits++;
                    getUnitData = cand;
                }
            }
            if (gudHits != 1) { codes.Raw(Quote(gudHits == 0 ? "getunitdata-missing"
                                                             : "getunitdata-ambiguous")); }

            var cities = new JsonArr();
            var sites = new JsonArr();
            int citiesTried = 0, sitesTried = 0, sitesOk = 0, badItems = 0, options = 0, validOptions = 0;
            bool infrastructure = (cu != null && gl != null && ps != null && unitEnum != null
                                   && trainHits == 1 && gudHits == 1);
            foreach (var site in TrainingSites(myCityTiles, waterSites))
            {
                object tile = site.Tile;
                string cityCoords = site.Coordinates;
                sitesTried++;
                if (site.Kind == "city") citiesTried++;
                if (!infrastructure || string.IsNullOrEmpty(cityCoords) || cityCoords == "null")
                {
                    var bad = new JsonObj();
                    bad.NullOrStr("coords", cityCoords);
                    bad.Str("status", "unknown");
                    AddTrainingSiteRow(cities, sites, site, bad.Str("siteKind", site.Kind).Bool("explored", true).NullOrStr("owner", SafeScalar(MemberWithAccessor(ps, "Id"))).Close());
                    continue;
                }
                object list;
                try { list = train.Invoke(null, new object[] { gs, ps, tile, false }); }
                catch (Exception ex)
                {
                    codes.Raw(Quote("invoke-threw:" + ex.GetType().Name));
                    var bad = new JsonObj();
                    bad.Str("coords", cityCoords);
                    bad.Str("status", "failed");
                    AddTrainingSiteRow(cities, sites, site, bad.Str("siteKind", site.Kind).Bool("explored", true).NullOrStr("owner", SafeScalar(MemberWithAccessor(ps, "Id"))).Close());
                    continue;
                }
                var items = ReadNativeList(list);
                if (list == null || items == null)
                {
                    // A null/unenumerable result is a FAILURE, not an empty menu.
                    codes.Raw(Quote(list == null ? "null-collection" : "not-enumerable"));
                    var bad = new JsonObj();
                    bad.Str("coords", cityCoords);
                    bad.Str("status", "failed");
                    AddTrainingSiteRow(cities, sites, site, bad.Str("siteKind", site.Kind).Bool("explored", true).NullOrStr("owner", SafeScalar(MemberWithAccessor(ps, "Id"))).Close());
                    continue;
                }
                var arr = new JsonArr();
                bool cityOk = true;
                int cityOptions = 0;
                var seenTypes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var rawCommand in items)
                {
                    object cmd = rawCommand == null ? null : URConcreteCommand(rawCommand, "Train");
                    if (cmd == null)
                    {
                        codes.Raw(Quote("null-command"));
                        cityOk = false;
                        badItems++;
                        continue;
                    }
                    cityOptions++;
                    // The command must belong to the controlled player and to THIS city.
                    string cmdPlayer = SafeScalar(MemberWithAccessor(cmd, "PlayerId"));
                    string cmdCoords = SafeScalar(MemberWithAccessor(cmd, "Coordinates"));
                    // The controlled player's id must be readable, and the command must match it
                    // EXACTLY. There is no length-of-ToString or any other heuristic bypass.
                    string myId = SafeScalar(MemberWithAccessor(ps, "Id"));
                    bool identityOk = !string.IsNullOrEmpty(myId) && myId != "null"
                        && cmdPlayer == myId;
                    string why;
                    string uname;
                    bool typeOk = true;
                    if (!identityOk)
                    {
                        codes.Raw(Quote(string.IsNullOrEmpty(myId) || myId == "null"
                            ? "player-id-unreadable" : "command-player-mismatch"));
                        uname = null;
                        typeOk = false;
                    }
                    else
                    {
                        uname = EnumRuleIdentityOfExpected(MemberWithAccessor(cmd, "Type"), unitEnum, out why);
                    }
                    // itemOk is per ITEM: one bad entry must not poison the following good ones.
                    bool itemOk = identityOk && typeOk;
                    if (string.IsNullOrEmpty(cmdCoords) || cmdCoords == "null" || cmdCoords != cityCoords)
                    { codes.Raw(Quote("command-coords-mismatch")); itemOk = false; }
                    long? cost = null;
                    if (uname == null) { codes.Raw(Quote("unittype-unknown")); itemOk = false; }
                    else
                    {
                        try
                        {
                            object typeVal = Enum.Parse(unitEnum, uname);
                            var ud = getUnitData.Invoke(gl, new object[] { typeVal });
                            if (ud == null) { codes.Raw(Quote("unitdata-null")); itemOk = false; }
                            else
                            {
                                var cv = MemberWithAccessor(ud, "cost");
                                // never TrainCommand.cost (a TrainCommand has none)
                                if (cv == null || cv is bool
                                    || !(cv is int || cv is short || cv is long || cv is byte
                                         || cv is ushort || cv is uint))
                                { codes.Raw(Quote("cost-invalid")); itemOk = false; }
                                else
                                {
                                    var lc = Convert.ToInt64(cv, CultureInfo.InvariantCulture);
                                    if (lc < 0) { codes.Raw(Quote("cost-negative")); itemOk = false; }
                                    else { cost = lc; }
                                }
                            }
                        }
                        catch (Exception ex)
                        { codes.Raw(Quote("unitdata-threw:" + ex.GetType().Name)); itemOk = false; }
                    }
                    if (uname != null && itemOk && !seenTypes.Add(uname)) continue;
                    bool? nativeValid = itemOk ? URValid(cmd, gs) : null;
                    if (itemOk && !nativeValid.HasValue)
                    { codes.Raw(Quote("train-validity-unavailable")); itemOk = false; }
                    var eo = new JsonObj();
                    eo.NullOrStr("unitType", uname).NullableBool("nativeValid", nativeValid);
                    eo.Raw("cost", cost.HasValue ? cost.Value.ToString(CultureInfo.InvariantCulture) : "null");
                    // true only when this call really returned it AND its identity/city/cost all checked
                    eo.Bool("trainable", uname != null && cost.HasValue && itemOk && nativeValid == true);
                    if (uname != null && cost.HasValue && itemOk && nativeValid == true) validOptions++;
                    if (uname == null || !cost.HasValue || !itemOk) { badItems++; cityOk = false; }
                    arr.Raw(eo.Close());
                    options++;
                }
                var co = new JsonObj();
                co.Str("coords", cityCoords).Str("siteKind", site.Kind).Bool("explored", true)
                    .NullOrStr("owner", SafeScalar(MemberWithAccessor(ps, "Id")));
                co.Str("status", cityOk ? "ok" : "partial");
                co.Raw("options", arr.Close());
                AddTrainingSiteRow(cities, sites, site, co.Close());
                if (cityOk) { sitesOk++; }
            }
            string status;
            if (!infrastructure) { status = "unavailable"; }
            else if (sitesTried == 0) { status = "ok"; }        // legal scan completed, no own city
            else if (sitesOk == sitesTried && badItems == 0) { status = "ok"; }
            else if (sitesOk == 0 && validOptions == 0) { status = "unavailable"; }
            else { status = "partial"; }
            o.Str("authority", "CommandUtils.GetTrainableUnits(gs, ps, tile, false), actual TrainCommand.IsValid and GameLogicData.GetUnitData(Type).cost; existing own cities plus explored owned water/algae sites");
            o.Str("status", status);
            o.Raw("cities", cities.Close());
            o.Raw("sites", sites.Close());
            o.Raw("siteCount", sitesTried.ToString(CultureInfo.InvariantCulture));
            o.Raw("cityCount", citiesTried.ToString(CultureInfo.InvariantCulture));
            o.Raw("optionCount", options.ToString(CultureInfo.InvariantCulture));
            o.Raw("reasonCodes", codes.Close());   // stable tokens only: no native type/signature text
            return o.Close();
        }


        /// <summary>
        /// The movement destinations the game's own UI would highlight for MY units.
        ///
        /// Chain (verified against the live bytes): Unit.UpdateOverlays 0x1D5E080 ->
        /// UnitDataExtensions.GetMovement 0x1D5E4F3 -> GetMovementOptions 0x1D5E504 ->
        /// PathFinder.GetMoveOptions 0x536B30, consumed element-by-element by ShowOverlay.
        ///
        /// Only the controlled player's REAL UnitState objects, at their REAL positions, are passed,
        /// with the game's own movement budget; nothing here computes paths or costs, and no caller
        /// supplied position/range ever reaches the engine.
        /// </summary>
        private string LegalMoveTargets(object gs, object ps, byte me, List<object> myUnitTiles, HashSet<string> explored)
        {
            var o = new JsonObj();
            var codes = new JsonArr();
            var ext = FindGameType("UnitDataExtensions") ?? FindType("Il2CppPolytopia.UnitDataExtensions");
            if (ext == null || ps == null)
            {
                o.Str("status", "unavailable");
                o.Raw("reasonCodes", new JsonArr().Raw(Quote(ext == null ? "unitdataextensions-unavailable"
                    : "playerstate-unavailable")).Close());
                return o.Close();
            }
            // AutoPlay must be determinable and false: an auto-played player is not the human whose
            // highlights the UI would draw.
            bool? auto = ReadBoolPropertyOrGetter(ps, "AutoPlay");
            if (auto == null || auto.Value)
            {
                o.Str("status", "unavailable");
                codes.Raw(Quote("autoplay-not-false"));
                o.Raw("reasonCodes", codes.Close());
                o.Raw("units", new JsonArr().Close());
                return o.Close();
            }
            string seenMove = "";
            MethodInfo getMove = FindStaticUnique(ext, "GetMovement",
                new[] { "UnitState", "GameState" }, "Int32");
            MethodInfo getOptions = FindStaticUnique(ext, "GetMovementOptions",
                new[] { "UnitState", "GameState", "Int32" }, null);
            if (getMove == null || getOptions == null)
            {
                o.Str("status", "unavailable");
                codes.Raw(Quote(getMove == null ? "getmovement-not-unique" : "getmovementoptions-not-unique"));
                o.Raw("reasonCodes", codes.Close());
                return o.Close();
            }
            var units = new JsonArr();
            int asked = 0, ok = 0;
            foreach (var tile in myUnitTiles)
            {
                if (tile == null) { continue; }
                bool filtered;
                var unit = TileUnitVisible(tile, gs, me, out filtered);
                if (unit == null || !filtered) { continue; }
                if (SafeScalar(MemberWithAccessor(unit, "owner")) != me.ToString(CultureInfo.InvariantCulture))
                { codes.Raw(Quote("own-unit-identity-mismatch")); continue; }
                asked++;
                var uo = new JsonObj();
                uo.NullOrStr("executionId", SafeScalar(MemberWithAccessor(unit, "id")));
                string from = SafeScalar(MemberWithAccessor(tile, "coordinates"));
                uo.NullOrStr("from", from);
                // the UI gates: the unit must be able to act at all, and must not have moved
                bool? movedOk = ReadBoolMember(unit, "moved");
                bool? canMove = ReadBoolPropertyOrGetter(unit, "CanMove");
                if (movedOk == null || canMove == null)
                {
                    uo.Str("status", "unavailable");
                    codes.Raw(Quote("unit-action-state-unreadable"));
                    uo.Raw("targets", new JsonArr().Close());
                    units.Raw(uo.Close());
                    continue;
                }
                if (movedOk.Value || !canMove.Value)
                {
                    // a unit that has already moved legitimately has an EMPTY highlight set
                    uo.Str("status", "ok");
                    uo.Raw("targets", new JsonArr().Close());
                    units.Raw(uo.Close());
                    ok++;
                    continue;
                }
                int range;
                try { range = Convert.ToInt32(getMove.Invoke(null, new object[] { unit, gs }),
                                               CultureInfo.InvariantCulture); }
                catch (Exception ex)
                {
                    uo.Str("status", "unavailable");
                    codes.Raw(Quote("getmovement-threw:" + ex.GetType().Name));
                    uo.Raw("targets", new JsonArr().Close());
                    units.Raw(uo.Close());
                    continue;
                }
                object raw;
                try { raw = getOptions.Invoke(null, new object[] { unit, gs, range }); }
                catch (Exception ex)
                {
                    uo.Str("status", "unavailable");
                    codes.Raw(Quote("getmovementoptions-threw:" + ex.GetType().Name));
                    uo.Raw("targets", new JsonArr().Close());
                    units.Raw(uo.Close());
                    continue;
                }
                var list = ReadNativeList(raw);
                if (raw == null || list == null)
                {
                    uo.Str("status", "unavailable");
                    codes.Raw(Quote(raw == null ? "null-collection" : "not-enumerable"));
                    uo.Raw("targets", new JsonArr().Close());
                    units.Raw(uo.Close());
                    continue;
                }
                var targets = new JsonArr();
                bool clean = true, anyUnexplored = false;
                int n = 0;
                foreach (var t in list)
                {
                    if (t == null) { clean = false; codes.Raw(Quote("null-target")); continue; }
                    string c = SafeScalar(MemberWithAccessor(t, "coordinates")
                        ?? (t.GetType().Name.IndexOf("WorldCoordinates", StringComparison.Ordinal) >= 0 ? SafeScalar(t) : null));
                    if (string.IsNullOrEmpty(c) || c == "null") { clean = false; codes.Raw(Quote("target-coords-unreadable")); continue; }
                    n++;
                    if (!explored.Contains(c)) { anyUnexplored = true; continue; }
                    targets.Raw(Quote(c));
                }
                if (anyUnexplored)
                {
                    // A destination the player has never explored must never be published; the whole
                    // unit is withheld (and the count is NOT reported - that would leak).
                    uo.Str("status", "withheld");
                    codes.Raw(Quote("target-outside-explored"));
                    uo.Raw("targets", new JsonArr().Close());
                    units.Raw(uo.Close());
                    continue;
                }
                uo.Str("status", clean ? "ok" : "partial");
                uo.Raw("targets", targets.Close());
                units.Raw(uo.Close());
                if (clean) { ok++; }
            }
            o.Str("authority", "UnitDataExtensions.GetMovement(GameState) + GetMovementOptions(GameState, range) - the UI highlight chain");
            o.Str("status", asked == 0 ? "ok" : (ok == asked ? "ok" : (ok == 0 ? "unavailable" : "partial")));
            o.Raw("units", units.Close());
            o.Raw("unitCount", asked.ToString(CultureInfo.InvariantCulture));
            o.Raw("reasonCodes", codes.Close());
            return o.Close();
        }

        /// <summary>Static unique match helper (name + parameter simple names + optional return name).</summary>
        internal static MethodInfo FindStaticUnique(Type t, string name, string[] paramNames, string returnName)
        {
            return FindUniqueNativeMethod(t, name, paramNames, returnName, true);
        }

        /// <summary>
        /// Per-city training capacity for MY OWN visible cities, from the game's own validators:
        /// CommandValidation.CanCitySupportUnit(gameState, worldCoordinates) and
        /// MapDataExtensions.GetCityUnitCount(mapData, worldCoordinates). The capacity rule in this
        /// build is City level + 1; the global unit total is never used as a substitute.
        /// </summary>
        private string TrainLimits(object gs, List<object> myCityTiles, Dictionary<string, int> cityLevels)
        {
            var o = new JsonObj();
            var codes = new JsonArr();
            var cv = FindGameType("CommandValidation") ?? FindType("Il2CppPolytopia.CommandValidation");
            var mde = FindGameType("MapDataExtensions") ?? FindType("Il2CppPolytopia.MapDataExtensions");
            var map = MemberWithAccessor(gs, "Map");
            MethodInfo canSupport = FindStaticUnique(cv, "CanCitySupportUnit",
                new[] { "GameState", "WorldCoordinates" }, "Boolean");
            MethodInfo cityCount = FindStaticUnique(mde, "GetCityUnitCount",
                new[] { "MapData", "WorldCoordinates" }, "Int32");
            if (canSupport == null) { codes.Raw(Quote("cancitysupportunit-not-unique")); }
            if (cityCount == null) { codes.Raw(Quote("getcityunitcount-not-unique")); }
            if (map == null) { codes.Raw(Quote("map-unavailable")); }
            var cities = new JsonArr();
            int asked = 0, ok = 0;
            foreach (var tile in myCityTiles)
            {
                if (tile == null) { continue; }
                asked++;
                var co = new JsonObj();
                string coords = SafeScalar(MemberWithAccessor(tile, "coordinates"));
                co.NullOrStr("coords", coords);
                object world = MemberWithAccessor(tile, "coordinates");
                if (world == null || canSupport == null || cityCount == null || map == null
                    || string.IsNullOrEmpty(coords) || coords == "null")
                {
                    co.Str("status", "unavailable");
                    co.Raw("supportedUnitCount", "null");
                    co.Raw("capacity", "null");
                    co.Raw("canSupportAnotherUnit", "null");
                    cities.Raw(co.Close());
                    continue;
                }
                long? count = null, capacity = null;
                bool? canAnother = null;
                try
                {
                    var c = cityCount.Invoke(null, new object[] { map, world });
                    if (c != null && !(c is bool) && (c is int || c is short || c is long || c is byte
                        || c is ushort || c is uint))
                    {
                        var lc = Convert.ToInt64(c, CultureInfo.InvariantCulture);
                        if (lc >= 0) { count = lc; }
                    }
                }
                catch (Exception ex) { codes.Raw(Quote("citycount-threw:" + ex.GetType().Name)); }
                try
                {
                    var b = canSupport.Invoke(null, new object[] { gs, world });
                    if (b is bool) { canAnother = (bool)b; }
                }
                catch (Exception ex) { codes.Raw(Quote("cansupport-threw:" + ex.GetType().Name)); }
                if (cityLevels != null && coords != null && cityLevels.ContainsKey(coords))
                {
                    // the published rule in this build: a city supports level + 1 units
                    capacity = cityLevels[coords] + 1;
                }
                else { codes.Raw(Quote("city-level-unreadable")); }
                bool completeCity = count.HasValue && capacity.HasValue && canAnother.HasValue;
                co.Str("status", completeCity ? "ok" : ((count.HasValue || capacity.HasValue || canAnother.HasValue) ? "partial" : "unavailable"));
                co.Raw("supportedUnitCount", count.HasValue ? count.Value.ToString(CultureInfo.InvariantCulture) : "null");
                co.Raw("capacity", capacity.HasValue ? capacity.Value.ToString(CultureInfo.InvariantCulture) : "null");
                co.Raw("canSupportAnotherUnit", canAnother.HasValue ? (canAnother.Value ? "true" : "false") : "null");
                cities.Raw(co.Close());
                if (completeCity) { ok++; }
            }
            o.Str("authority", "CommandValidation.CanCitySupportUnit + MapDataExtensions.GetCityUnitCount; capacity = City level + 1 (this build)");
            o.Str("status", asked == 0 ? "ok" : (ok == asked ? "ok" : (ok == 0 ? "unavailable" : "partial")));
            o.Raw("cities", cities.Close());
            o.Raw("cityCount", asked.ToString(CultureInfo.InvariantCulture));
            o.Raw("reasonCodes", codes.Close());
            return o.Close();
        }


        /// <summary>
        /// The attack targets the game's own UI would highlight for MY units.
        ///
        /// Chain (to be confirmed against the current bytes by the read-only analyst):
        /// Unit.UpdateOverlays 0x1D5E080 -> the attack call site 0x1D5E2A0 ->
        /// UnitDataExtensions.GetAttackOptions(unit, gs, range, ignoreDiplomacyRelation:false) 0x554970,
        /// with the game's own GetRange(unit, gs) 0x555300. Only the controlled player's REAL,
        /// already-filtered UnitState objects are passed, and the range always comes from the game -
        /// a caller-supplied range would be a range oracle and is never accepted.
        ///
        /// The MOVEMENT `moved == false` gate is deliberately NOT applied here: a unit that has
        /// already moved may still attack, and inventing a universal action rule is exactly what the
        /// contract forbids.
        /// </summary>
        private string LegalAttackTargets(object gs, object ps, byte me, bool canAct,
            List<object> myUnitTiles, HashSet<string> visibleEnemyCoords)
        {
            var o = new JsonObj();
            var codes = new JsonArr();
            var ext = FindGameType("UnitDataExtensions") ?? FindType("Il2CppPolytopia.UnitDataExtensions");
            // the UI only draws attack highlights for a human (non-AutoPlay) player
            bool? autoPlay = ps == null ? null : ReadBoolMember(ps, "AutoPlay");
            if (autoPlay == null || autoPlay.Value) { codes.Raw(Quote("autoplay-not-false")); }
            if (ext == null || ps == null || !canAct || autoPlay == null || autoPlay.Value)
            {
                o.Str("status", "unavailable");
                codes.Raw(Quote(ext == null ? "unitdataextensions-unavailable"
                    : (ps == null ? "playerstate-unavailable" : "not-currently-actionable")));
                o.Raw("units", new JsonArr().Close());
                o.Raw("reasonCodes", codes.Close());
                return o.Close();
            }
            MethodInfo getRange = FindStaticUnique(ext, "GetRange",
                new[] { "UnitState", "GameState" }, "Int32");
            MethodInfo getOptions = FindStaticUnique(ext, "GetAttackOptions",
                new[] { "UnitState", "GameState", "Int32", "Boolean" }, null);
            if (getRange == null || getOptions == null)
            {
                o.Str("status", "unavailable");
                codes.Raw(Quote(getRange == null ? "getrange-not-unique" : "getattackoptions-not-unique"));
                o.Raw("units", new JsonArr().Close());
                o.Raw("reasonCodes", codes.Close());
                return o.Close();
            }
            var units = new JsonArr();
            int asked = 0, ok = 0;
            foreach (var tile in myUnitTiles)
            {
                if (tile == null) { continue; }
                bool filtered;
                var unit = TileUnitVisible(tile, gs, me, out filtered);
                if (unit == null || !filtered) { continue; }
                // re-check ownership on THIS snapshot object before anything is published
                if (SafeScalar(MemberWithAccessor(unit, "owner"))
                    != me.ToString(CultureInfo.InvariantCulture)) { continue; }
                asked++;
                var uo = new JsonObj();
                uo.NullOrStr("executionId", SafeScalar(MemberWithAccessor(unit, "id")));
                uo.NullOrStr("from", SafeScalar(MemberWithAccessor(tile, "coordinates")));
                // attack-relevant state only: HasAttacked / CanAttack (never the movement gate)
                // UnitState.attacked is a real public bool FIELD (there is no HasAttacked member),
                // so it is read with the same accessor the world path already uses. CanAttack() is a
                // no-arg bool METHOD (0x527180) = !attacked && !Frozen && (moved==0 || Dash).
                bool? attacked = ReadBoolMember(unit, "attacked");
                bool? canAttack = ReadBoolPropertyOrGetter(unit, "CanAttack");
                if (attacked == null || canAttack == null)
                {
                    uo.Str("status", "unavailable");
                    codes.Raw(Quote("attack-state-unreadable"));
                    uo.Raw("targets", new JsonArr().Close());
                    units.Raw(uo.Close());
                    continue;
                }
                if (attacked.Value || !canAttack.Value)
                {
                    // a unit that already attacked legitimately has an EMPTY highlight set
                    uo.Str("status", "ok");
                    uo.Raw("targets", new JsonArr().Close());
                    units.Raw(uo.Close());
                    ok++;
                    continue;
                }
                int range;
                try { range = Convert.ToInt32(getRange.Invoke(null, new object[] { unit, gs }),
                                               CultureInfo.InvariantCulture); }
                catch (Exception ex)
                {
                    uo.Str("status", "unavailable");
                    codes.Raw(Quote("getrange-threw"));
                    uo.Raw("targets", new JsonArr().Close());
                    units.Raw(uo.Close());
                    continue;
                }
                object raw;
                try
                {
                    // ignoreDiplomacyRelation is explicitly false, exactly like the UI entry
                    raw = getOptions.Invoke(null, new object[] { unit, gs, range, false });
                }
                catch (Exception ex)
                {
                    uo.Str("status", "unavailable");
                    codes.Raw(Quote("getattackoptions-threw"));
                    uo.Raw("targets", new JsonArr().Close());
                    units.Raw(uo.Close());
                    continue;
                }
                var list = ReadNativeList(raw);
                if (raw == null || list == null)
                {
                    uo.Str("status", "unavailable");
                    codes.Raw(Quote(raw == null ? "null-collection" : "not-enumerable"));
                    uo.Raw("targets", new JsonArr().Close());
                    units.Raw(uo.Close());
                    continue;
                }
                var targets = new JsonArr();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                bool clean = true;
                foreach (var t in list)
                {
                    if (t == null) { clean = false; codes.Raw(Quote("null-target")); continue; }
                    // GetAttackOptions returns List<WorldCoordinates>: the element IS the
                    // coordinate structure (no `coordinates` member), exactly like the movement
                    // reader's verified path.
                    string c = SafeScalar(MemberWithAccessor(t, "coordinates")) ?? SafeScalar(t);
                    if (string.IsNullOrEmpty(c) || c == "null" || c.Length < 3
                        || c[0] != '(' || c[c.Length - 1] != ')')
                    { clean = false; codes.Raw(Quote("target-coords-unreadable")); continue; }
                    if (!seen.Add(c)) { clean = false; codes.Raw(Quote("target-duplicate")); continue; }
                    // a target must be a CURRENTLY VISIBLE enemy position from the standard
                    // observation; historical sightings can never be attacked by assumption
                    if (!visibleEnemyCoords.Contains(c))
                    { clean = false; codes.Raw(Quote("target-not-currently-visible")); continue; }
                    targets.Raw(Quote(c));
                }
                if (!clean)
                {
                    uo.Str("status", "withheld");
                    codes.Raw(Quote("unit-withheld"));
                    uo.Raw("targets", new JsonArr().Close());
                    units.Raw(uo.Close());
                    continue;
                }
                uo.Str("status", "ok");
                uo.Raw("targets", targets.Close());
                units.Raw(uo.Close());
                ok++;
            }
            o.Str("authority", "UnitDataExtensions.GetAttackOptions(GetRange(unit, gs), ignoreDiplomacyRelation:false) - the UI highlight chain");
            o.Str("status", asked == 0 ? "ok" : (ok == asked ? "ok" : (ok == 0 ? "unavailable" : "partial")));
            o.Raw("units", units.Close());
            o.Raw("unitCount", asked.ToString(CultureInfo.InvariantCulture));
            o.Raw("reasonCodes", codes.Close());
            return o.Close();
        }

        /// <summary>
        /// The GameLogicData instance belonging to THIS game's state. These accessors are INSTANCE
        /// methods, so they must run on the real object: it is taken from the GameState's own getter
        /// (GameState.cs get_GameLogicData, RVA 0x4F4200) and then identified by the INSTANCE's own
        /// runtime type. Nothing is constructed, and no global object of a different data version is
        /// substituted.
        /// </summary>
        internal static object GameLogicDataOf(object gs)
        {
            if (gs == null) { return null; }
            var t = gs.GetType();
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var n in new[] { "get_GameLogicData", "GameLogicData", "get_logicData", "LogicData" })
            {
                try
                {
                    var p = t.GetProperty(n, F);
                    if (p != null)
                    {
                        var v = p.GetValue(gs, null);
                        if (v != null) { return v; }
                    }
                }
                catch { }
                try
                {
                    foreach (var m in t.GetMethods(F))
                    {
                        if (m.Name != n || m.GetParameters().Length != 0) { continue; }
                        var v = m.Invoke(gs, null);
                        if (v != null) { return v; }
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>The controlled player's PlayerState object, or null.</summary>
        private object PlayerStateOf(object gs, byte player)
        {
            var players = AsEnumerable(Val(_playersProp, gs));
            if (players == null) { return null; }
            var meStr = player.ToString(CultureInfo.InvariantCulture);
            foreach (var p in players)
            {
                if (p == null) { continue; }
                if (SafeScalar(MemberWithAccessor(p, "Id")) == meStr) { return p; }
            }
            return null;
        }

        /// <summary>
        /// UNIQUE method match on the INSTANCE's own type: exact name, exact parameter count, every
        /// parameter and (optionally) the return type matched by name, and MORE THAN ONE candidate
        /// yields null. A fuzzy "first hit" is exactly what produced the wrong results earlier.
        /// Callers get the signatures actually seen in `seen` for diagnosis.
        /// </summary>
        internal static MethodInfo FindInstanceMethodUnique(Type t, string name, string[] paramNames,
            string returnName, out string seen)
        {
            seen = "";
            if (t == null || paramNames == null) { return null; }
            MethodInfo hit = null;
            int hits = 0;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            {
                if (m.Name != name) { continue; }
                var ps = m.GetParameters();
                if (ps.Length != paramNames.Length) { seen += m.Name + "/" + ps.Length + " "; continue; }
                bool ok = NativeMethodSignatureMatches(m, name, paramNames, returnName);
                seen += m.Name + "/" + ps.Length + (ok ? "* " : " ");
                if (!ok) { continue; }
                hits++;
                hit = m;
            }
            return hits == 1 ? hit : null;     // absent OR ambiguous => unknown
        }

        /// <summary>
        /// Invoke a bool-returning method, treating null returns and exceptions as UNKNOWN. A failed
        /// probe must never be silently reported as false.
        /// </summary>
        internal static bool? InvokeBool(MethodInfo m, object target, object[] args)
        {
            if (m == null) { return null; }
            try
            {
                var r = m.Invoke(target, args);
                if (r is bool) { return (bool)r; }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// The player-visible researchable set, straight from the game: GameLogicData instance
        /// GetUnlockableTech + GetTechPrice. Costs are never computed locally.
        /// </summary>
        private string ResearchableTechs(object gs, object ps, HashSet<string> ownedNames)
        {
            var o = new JsonObj();
            if (ownedNames == null)
                return o.Str("status", "unavailable").Str("reason", "owned technologies unavailable; research candidates cannot be filtered").Close();
            var gl = GameLogicDataOf(gs);
            if (gl == null || ps == null)
            {
                o.Str("status", "unavailable");
                o.Str("reason", gl == null ? "no GameLogicData instance on this GameState"
                                           : "no controlled PlayerState instance");
                return o.Close();
            }
            string seen = "", seenPrice = "";
            var m = FindInstanceMethodUnique(gl.GetType(), "GetUnlockableTech",
                new[] { "PlayerState", "GameState" }, null, out seen);
            var price = FindInstanceMethodUnique(gl.GetType(), "GetTechPrice",
                new[] { "TechData", "PlayerState", "GameState" }, null, out seenPrice);
            if (m == null)
            {
                o.Str("status", "unavailable");
                o.Str("reason", "GetUnlockableTech not uniquely matched on " + gl.GetType().Name
                    + "; saw=[" + seen + "]");
                return o.Close();
            }
            object list;
            try { list = m.Invoke(gl, new object[] { ps, gs }); }
            catch (Exception ex)
            {
                o.Str("status", "unavailable");
                o.Str("reason", "GetUnlockableTech threw " + ex.GetType().Name);
                return o.Close();
            }
            var items = ReadNativeList(list);
            if (items == null)
            {
                o.Str("status", "unavailable");
                o.Str("reason", "GetUnlockableTech collection unavailable or unreadable");
                return o.Close();
            }
            var arr = new JsonArr();
            int n = 0, withCost = 0, noCost = 0, badItems = 0;
            foreach (var el in items)
            {
                string why = null;
                string nm = TechEnumNameOf(el, out why);
                if (nm == null || ownedNames.Contains(nm))
                {
                    // Unresolved (or already owned) items are published as null WITHOUT diagnostics:
                    // a CLR type name or an internal reason must never look like a real tech.
                    var bad = new JsonObj();
                    bad.Raw("tech", "null");
                    bad.Str("nameStatus", ownedNames.Contains(nm) ? "already_owned" : "unresolved");
                    arr.Raw(bad.Close());
                    n++;
                    badItems++;
                    continue;
                }
                n++;
                var eo = new JsonObj();
                eo.Str("tech", nm);
                if (price == null)
                {
                    eo.Raw("cost", "null");
                    eo.Str("costStatus", "GetTechPrice not uniquely matched; saw=[" + seenPrice + "]");
                    noCost++;
                }
                else
                {
                    try
                    {
                        var c = price.Invoke(gl, new object[] { el, ps, gs });
                        if (c == null)
                        {
                            eo.Raw("cost", "null");
                            eo.Str("costStatus", "null-return");
                            noCost++;
                        }
                        else if (c is bool || !(c is int || c is short || c is long || c is byte
                                 || c is ushort || c is uint))
                        {
                            eo.Raw("cost", "null");
                            eo.Str("costStatus", "not-an-integer");
                            noCost++;
                        }
                        else
                        {
                            long cost = Convert.ToInt64(c, CultureInfo.InvariantCulture);
                            if (cost < 0)
                            {
                                eo.Raw("cost", "null");
                                eo.Str("costStatus", "negative");
                                noCost++;
                            }
                            else
                            {
                                eo.Raw("cost", cost.ToString(CultureInfo.InvariantCulture));
                                eo.Str("costStatus", "ok");
                                withCost++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        eo.Raw("cost", "null");
                        eo.Str("costStatus", "invoke-failed:" + ex.GetType().Name);
                        noCost++;
                    }
                }
                // Affordability is deliberately NOT computed here (no local cost arithmetic and no
                // wallet side effects); `stars` is exposed so the caller can compare.
                eo.Raw("affordable", "null");
                arr.Raw(eo.Close());
            }
            o.Str("authority", "GameLogicData instance GetUnlockableTech + GetTechPrice");
            o.Str("status", (noCost == 0 && badItems == 0) ? "ok" : "partial");
            o.Raw("items", arr.Close());
            o.Raw("count", n.ToString(CultureInfo.InvariantCulture));
            o.Raw("costsRead", withCost.ToString(CultureInfo.InvariantCulture));
            if (noCost > 0)
            {
                o.Str("reason", noCost + "/" + n + " costs unreadable; the cost is NOT computed locally");
            }
            return o.Close();
        }

        internal static Type FindTypeBySuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) { return null; }
            var plus = name.LastIndexOf('+');
            var outer = plus > 0 ? name.Substring(0, plus) : null;
            var leaf = plus > 0 ? name.Substring(plus + 1) : name;
            // allow "Namespace.Outer+Leaf" too
            if (outer != null && outer.IndexOf('.') >= 0) { outer = outer.Substring(outer.LastIndexOf('.') + 1); }

            Type loose = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = ResolverTypesOf(a); } catch { continue; }
                var gameAssembly = (a.GetName().Name ?? "").IndexOf("GameLogic", StringComparison.OrdinalIgnoreCase) >= 0
                    || (a.GetName().Name ?? "").IndexOf("Il2CppPolytopia", StringComparison.OrdinalIgnoreCase) >= 0;
                foreach (var t in types)
                {
                    if (t == null || !t.IsEnum) { continue; }
                    if (!t.Name.Equals(leaf, StringComparison.Ordinal)) { continue; }
                    var full = t.FullName ?? t.Name;
                    if (full.Equals(name, StringComparison.Ordinal)) { return t; }
                    var decl = t.DeclaringType;
                    if (outer != null && decl != null && decl.Name.Equals(outer, StringComparison.Ordinal))
                    {
                        if (gameAssembly) { return t; }
                        if (loose == null) { loose = t; }
                    }
                }
            }
            return loose;
        }

        // ------------------------------------------------------------ transport

        private void StartFileTransport()
        {
            if (File.Exists(CmdPath) && new FileInfo(CmdPath).Length > 0)
            {
                _cmdOffset = new FileInfo(CmdPath).Length;  // ignore stale commands
            }
            else
            {
                File.WriteAllText(CmdPath, "");
                _cmdOffset = 0;
            }
            // Preserve textual reply history across starts. Clients begin at the current
            // byte offset; keeping old output does not make them replay or rescan it.
            Log("file transport ready: " + CmdPath + " (skip " + _cmdOffset + " stale bytes)");
            WriteReadyFile();
        }

        private bool WriteReadyFile(bool requireOwner = false)
        {
            try
            {
                int pid = TransportReadyPidValue();
                return PublishStartupReady(WorkDir,
                    HostReadyDocument(StartupReadyJson(CmdPath, OutPath, pid, _startupStarted,
                        _fixedControlledSeat, _controlledSeatLoaded, _startupUiPhase)), pid, requireOwner);
            }
            catch (Exception ex) { Log("ready-file failed: " + ex.Message); return false; }
        }

        /// <summary>Append one response line; `reqId` lets the agent correlate a reply to its request.</summary>
        private void Emit(string json, long reqId)
        {
            if (!TransportOwnsReadyFile())
            {
                if (reqId > 0) LogReplyDeliveryFailure(reqId, "ownership", null);
                return;
            }
            json = BridgeJsonCodec.NormalizeResponse(json);
            var line = (reqId > 0 ? "{\"reqId\":" + reqId + "," + json.Substring(1) : json);
            try
            {
                using (var fs = new FileStream(OutPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    sw.Write(line);
                    sw.Write('\n');
                }
            }
            catch (Exception ex) { LogReplyDeliveryFailure(reqId, "write", ex); }
        }

        // ----------------------------------------------------------- main loop

        private bool _publicRuntimeHooksAttempted;
        private bool _combatRuntimeHooksAttempted;

        public void TickRuntime()
        {
            if(!_runtimeRunning)return;
            long frameStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            _tick++;
            try
            {
                ObserveStartupUiReadiness();
                TickGameOperation();
                // Match-bound hooks must not force native class initializers before Unity scenes.
                // The original budgeted observation detects the first active match.
                bool sampleDue = _tick % 6 == 0 && PeriodicWorkDue(ref _nextCombatSampleTime, 100);
                var activeGame = GameActive && (sampleDue || !_combatRuntimeHooksAttempted || !_publicRuntimeHooksAttempted)
                    ? GameStateInstance() : null;
                if (activeGame != null && !_combatRuntimeHooksAttempted)
                {
                    _combatRuntimeHooksAttempted = true;
                    InitializeCombatRuntime();
                }
                if (activeGame != null && !_publicRuntimeHooksAttempted)
                {
                    _publicRuntimeHooksAttempted = true;
                    InstallPublicRuntimeHooks();
                    InstallPublicDisplayHooks();
                }
                if (sampleDue)
                {
                    ObserveHotseatResumeActivation();
                    CombatRuntimeTick(activeGame);
                }
                ProcessInbox();
                MaybeSnapshot(false, 0);
            }
            catch (Exception ex)
            {
                Log("OnUpdate error: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _updateFrameTimings.Record(frameStarted, System.Diagnostics.Stopwatch.GetTimestamp(), System.Diagnostics.Stopwatch.Frequency);
            }
        }

        private void ProcessInbox()
        {
            if (_tick % InboxPollEveryTicks != 0 || !PeriodicWorkDue(ref _nextInboxPollTime, 50)) return;
            // Budget and unchanged length avoid idle I/O; consume only under current ownership.
            bool mayDispatch;
            string text = TransportPollCmdTextForDispatch(_tick, ref _lastPollTick, ref _cmdOffset, ref _pending,
                CmdPath, WorkDir, OwnPid, TransportIoStats.Shared, out mayDispatch);
            if (!mayDispatch) return;
            if (text != null) _pending += text;
            // An unchanged file can still have buffered requests left from a previous batch.
            TransportDrainPending(ref _pending, WorkDir, OwnPid, TransportIoStats.Shared,
                ProcessInboxLine, System.Diagnostics.Stopwatch.GetTimestamp, System.Diagnostics.Stopwatch.Frequency);
        }

        private void ProcessInboxLine(string line)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            string command = null;
            long requestId = 0; bool routerEntered = false;
            string previousStream = _combatStream; long previousTicket = _combatTicketId;
            try
            {
                requestId = ParseLong(Json.GetString(line, "id"));
                BridgeJsonCodec.Object(line);
                if (!TransportRequestTargetsThisInstance(line)) return;
                TransportIoStats.Shared.AddLineYielded();
                command = Json.GetString(line, "cmd");
                BeginReadRequest(command);
                previousStream = _combatStream; previousTicket = _combatTicketId;
                routerEntered = true;
                HandleCommand(line, requestId);
            }
            catch (Exception ex)
            {
                Reply(UnhandledRequestFailure(ex, requestId, routerEntered, previousStream, previousTicket, _activeAuditRequested), requestId);
            }
            finally
            {
                try { EndReadRequest(); }
                finally
                {
                    _activeAuditRequested = false; _activeRequestId = 0;
                    RecordRequestTiming(command, System.Diagnostics.Stopwatch.GetTimestamp() - started);
                }
            }
        }

        private static long ParseLong(string s)
        {
            long v;
            return long.TryParse(s, out v) ? v : 0;
        }

        /// <summary>Write one response line. reqId lets the agent correlate a reply to its request.</summary>
        private void Reply(string json, long requestId)
        {
            Emit(json, requestId);
        }

        private long _activeRequestId;
        private bool _activeAuditRequested;

        private void HandleCommand(string raw, long requestId)
        {
            _activeRequestId = requestId;
            _activeAuditRequested = Json.GetString(raw, "audit") == "1";
            var cmd = Json.GetString(raw, "cmd");
            NoteReadDiagnosticTicket(cmd, raw);
            Log("cmd#" + requestId + ": " + cmd);

            if (_fixedControlledSeat.HasValue && !SeatIndependentCommand(cmd, raw) && !PrivateSamplingAllowed(GameStateInstance()))
            {
                Reply(PreDispatchRejection().Str("errorCode", "CONTROLLED_SEAT_PAUSED")
                    .Bool("canAct", false).Close(), requestId);
                return;
            }

            // Debug opt-in only. The caller writes the flag itself, so this is NOT isolation:
            // real restriction has to live in the adapter (see AdapterDecision).
            if (!IsDecisionCommand(cmd) && !IsSetupAdminCommand(cmd) && Json.GetString(raw, "audit") != "1")
            {
                Reply(PreDispatchRejection().Str("error", "audit-only command '" + cmd
                    + "'; add \"audit\":\"1\" to opt in (debug convenience, not a permission boundary)")
                    .Raw("decisionCommands", "[" + string.Join(",", Array.ConvertAll(DecisionCommands, x => "\"" + Esc(x) + "\"")) + "]")
                    .Close(), requestId);
                return;
            }

            string contextError = ValidateActionContext(cmd, raw);
            if (contextError != null)
            {
                Reply(PreDispatchRejection()
                    .Str("errorCode", contextError).Str("recovery", "observe and select a fresh action; no action dispatched").Close(), requestId);
                return;
            }

            switch (cmd)
            {
                case "schema":
                    Reply(SchemaInfoJson(), requestId); break;
                case "handoff_status": Reply(HandoffStatus(), requestId); break;
                case "handoff_start": Reply(HandoffStart(Json.GetString(raw, "playerId"), Json.GetString(raw, "expectedHandoffId")), requestId); break;
                case "hotseat_games": Reply(HotseatGames(Json.GetString(raw, "op")), requestId); break;
                case "hotseat_resume": Reply(HotseatResume(Json.GetString(raw, "gameId")), requestId); break;
                case "hotseat_resume_status": Reply(HotseatResumeStatus(), requestId); break;
                case "game_lifecycle": Reply(GameLifecycleAction(Json.GetString(raw, "operation"), Json.GetString(raw, "operationId"),
                    Json.GetString(raw, "gameId"), Json.GetString(raw, "expectedEpisodeId"), Json.GetString(raw, "playerId")), requestId); break;
                case "game_lifecycle_status": Reply(GameLifecycleStatus(Json.GetString(raw, "operationId"), Json.GetString(raw, "playerId")), requestId); break;
                case "events":
                    long cursor; int limit;
                    if (!long.TryParse(Json.GetString(raw, "afterEventId"), out cursor)) cursor = 0;
                    if (!int.TryParse(Json.GetString(raw, "limit"), out limit)) limit = 100;
                    Reply(CombatEventsJson(GameStateInstance(), cursor, limit), requestId); break;
                case "settlement":
                    long ticket;
                    if (!long.TryParse(Json.GetString(raw, "ticket"), out ticket)) ticket = 0;
                    Reply(CombatSettlementJson(GameStateInstance(), ticket), requestId); break;
                case "attack_preview": Reply(AttackPreviewPublic(Json.GetString(raw, "unit"), Json.GetString(raw, "at")), requestId); break;
                case "composite_preview":
                    Reply(PolytopiaBattlePreview.BattlePreviewSnapshot.CompositeSnapshot(
                        Json.GetString(raw, "unit"), Json.GetString(raw, "at"), Json.GetString(raw, "kind")), requestId); break;
                case "unit_properties": Reply(UnitRuntimeSnapshot(), requestId); break;
                case "unit_actions": Reply(UnitActionsSnapshot(GetArgs(raw)), requestId); break;
                case "unit_action": Reply(ExecuteUnitAction(GetArgs(raw)), requestId); break;
                case "tile_actions": Reply(TileActionsPublic(GetArgs(raw), false), requestId); break;
                case "tile_action": Reply(TileActionsPublic(GetArgs(raw), true), requestId); break;
                case "score_snapshot": Reply(ScoreSnapshotPublic(), requestId); break;
                case "result": Reply(ResultPublic(Json.GetString(raw, "episode")), requestId); break;
                case "tribe_info": Reply(TribeInfoPublicRequest(Json.GetString(raw, "opponent"), Json.GetString(raw, "mode"), Json.GetString(raw, "viewRequestId")), requestId); break;
                case "leaderboard": Reply(LeaderboardPublicRequest(Json.GetString(raw, "mode"), Json.GetString(raw, "viewRequestId")), requestId); break;
                case "notifications":
                    long afterNotice; int noticeLimit;
                    if (!long.TryParse(Json.GetString(raw, "after"), out afterNotice)) afterNotice = 0;
                    if (!int.TryParse(Json.GetString(raw, "limit"), out noticeLimit)) noticeLimit = 100;
                    Reply(NotificationsPublic(afterNotice, Json.GetString(raw, "stream"), noticeLimit), requestId); break;
                case "threat_map": Reply(VisibleThreatSnapshot(), requestId); break;
                case "game_info": Reply(GameInfoPublic(), requestId); break;
                case "tribe_relations": Reply(TribeRelationsPublic(), requestId); break;
                case "tribe_action": Reply(TribeDiplomacyPublic(Json.GetString(raw, "operation"),
                    Json.GetString(raw, "opponent"), Json.GetString(raw, "accept")), requestId); break;
                case "popup_info": Reply(PopupInfoPublic(), requestId); break;
                case "city_options": Reply(PolytopiaCityOps.CityOptionsSnapshot.Snapshot(), requestId); break;
                case "setup_status": Reply(SetupStatus(), requestId); break;
                case "setup_action": Reply(SetupAction(Json.GetString(raw,"actionId"),Json.GetString(raw,"setupSessionId"),Json.GetString(raw,"menuRevision")),requestId); break;
                case "setup_flow": Reply(NativeSetupStep(Json.GetString(raw, "step"), Json.GetString(raw, "tribe"),
                    Json.GetString(raw, "opponents"), Json.GetString(raw, "map_preset"), Json.GetString(raw, "difficulty")), requestId); break;
                case "ping":
                    Reply("{\"ok\":true,\"pong\":true,\"payload\":{\"tick\":" + _tick + ",\"snapshots\":" + _snapshotCount + "}}", requestId);
                    break;
                case "obs":
                    MaybeSnapshot(true, requestId);
                    break;
                case "api":
                    Reply(ApiReport(), requestId);
                    break;
                case "state":
                    Reply(StateReport(), requestId);
                    break;
                case "end_turn":
                    Reply(EndTurn(), requestId);
                    break;
                case "probe_actions":
                    Reply(ProbeActions(), requestId);
                    break;
                case "ai_commands":
                    Reply(AiCommands(), requestId);
                    break;
                case "ui":
                    Reply(UiClick(Json.GetString(raw, "name")), requestId);
                    break;
                case "probe_types":
                    Reply(ProbeTypes(), requestId);
                    break;
                case "find":
                    Reply(FindObjects(Json.GetString(raw, "name")), requestId);
                    break;
                case "call":
                    Reply(CallMethod(Json.GetString(raw, "type"), Json.GetString(raw, "method"),
                        Json.GetString(raw, "args")), requestId);
                    break;
                case "get":
                    Reply(GetStatic(Json.GetString(raw, "name")), requestId);
                    break;
                case "set":
                    Reply(SetStatic(Json.GetString(raw, "name"), Json.GetString(raw, "value")), requestId);
                    break;
                case "players":
                    Reply(PlayersDump(), requestId);
                    break;
                case "input":
                    Reply(InputState(Json.GetString(raw, "set")), requestId);
                    break;
                case "action":
                    Reply(ExecuteGameAction(Json.GetString(raw, "name"), DefaultableActionArgument(raw, "reward"),
                        DefaultableActionArgument(raw, "accept"), Json.GetString(raw, "from"),
                        Json.GetString(raw, "to"), Json.GetString(raw, "at"),
                        DefaultableActionArgument(raw, "kind"), Json.GetString(raw, "audit")), requestId);
                    break;
                case "gmfields":
                    Reply(GameManagerFields(), requestId);
                    break;
                case "client":
                    Reply(ClientProbe(), requestId);
                    break;
                case "triggers":
                    Reply(TriggerDump(), requestId);
                    break;
                case "world":
                    // Read-only snapshot of the things a turn is made of: the local player's
                    // units (id + coordinates) and cities (level/population/production), plus
                    // the resource tiles and animals the units can act on. No screenshots, and
                    // no fogged tile or fogged unit (see FogGate).
                    Reply(WorldDump(), requestId);
                    break;
                case "winner":
                    // Read-only terminal judgement. See WinnerReport for its honest limits.
                    Reply(WinnerReport(), requestId);
                    break;
                case "fog":
                    // Read-only audit of the fog tests `world` applies, so the "fog-honest"
                    // property can be checked instead of trusted.
                    Reply(FogReport(), requestId);
                    break;
                case "enum":
                    // Read live enum names, e.g. cmd=enum name=UpgradeCommand+Type.
                    Reply(EnumDump(Json.GetString(raw, "name")), requestId);
                    break;
                case "typeinfo":
                    // Read the members of a live type, e.g. cmd=typeinfo name=UIButtonBase_UI2.
                    Reply(TypeInfo(Json.GetString(raw, "name"), Json.GetString(raw, "filter")), requestId);
                    break;
                case "instances":
                    // Live instances of a type + readable members: cmd=instances name=GameSettings
                    // filter=Opponent,Difficulty cap=5.
                    Reply(InstanceDump(Json.GetString(raw, "name"), Json.GetString(raw, "filter"),
                        Json.GetString(raw, "cap")), requestId);
                    break;
                case "newgame":
                    // Start a single-player match through the game's own setup path:
                    //   cmd=newgame opponents=0 difficulty=Normal mapsize=.. mappreset=Continents
                    //             gametype=SinglePlayer dry=1
                    // (codex-dsh-063 added mappreset/gametype so the map can be chosen to have land,
                    //  coast and navigable water; the values are the live enums of this build.)
                    Reply(NewGame(Json.GetString(raw, "opponents"), Json.GetString(raw, "difficulty"),
                        Json.GetString(raw, "mapsize"), Json.GetString(raw, "mappreset"),
                        Json.GetString(raw, "gametype"), Json.GetString(raw, "dry")), requestId);
                    break;
                case "path":
                    // The game's own PathFinder: cmd=path from="(x,y)" to="(x,y)".
                    Reply(PathQuery(Json.GetString(raw, "from"), Json.GetString(raw, "to"),
                        Json.GetString(raw, "max")), requestId);
                    break;
                case "reach":
                    // Every tile the unit can reach this turn: PathFinder.GetMoveOptions.
                    Reply(ReachQuery(Json.GetString(raw, "from"), Json.GetString(raw, "max")), requestId);
                    break;
                case "unitdata":
                    Reply(CurrentUnitDataKeys(), requestId);
                    break;
                case "buttons":
                    Reply(ButtonScan(Json.GetString(raw, "cap"), Json.GetString(raw, "filter")), requestId);
                    break;
                case "uiat":
                    Reply(UiClickAt(Json.GetString(raw, "x"), Json.GetString(raw, "y"), Json.GetString(raw, "tol")), requestId);
                    break;
                case "popup":
                    Reply(PopupCommand(Json.GetString(raw, "op"), Json.GetString(raw, "popupId"),
                        Json.GetString(raw, "index"), Json.GetString(raw, "reward")), requestId);
                    break;
                case "popup_test":
                    // Spawn a REAL popup using the game's own PopupTester component so the
                    // choose/close path can be verified against genuine popups.
                    Reply(PopupTest(Json.GetString(raw, "name"), Json.GetString(raw, "action")), requestId);
                    break;
                case "popup_make":
                    // Spawn a real popup through PopupManager's own factory API, which works
                    // whenever PopupManager.instance exists (i.e. from the main menu too).
                    Reply(PopupMake(Json.GetString(raw, "kind")), requestId);
                    break;
                default:
                    Reply("{\"ok\":false,\"error\":\"unknown cmd: " + Esc(cmd) + "\"}", requestId);
                    break;
            }
        }

        // --------------------------------------------------------- observation

        private static string Trim(string s, int n)
        {
            if (s == null) { return ""; }
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        private object GameStateInstance()
        {
            if (_fixedControlledSeat.HasValue) return MainClientState();
            if (_gameStateProp == null) { return null; }
            try { return _gameStateProp.GetValue(null, null); }
            catch (Exception ex)
            {
                RecordNativeReadFailure(_gameManagerType, "GameState", ex);
                if (_tick - _lastStateErrorTick > 300)
                {
                    _lastStateErrorTick = _tick;
                    Log("GameState read failed: " + ex.GetType().Name + ": " + ex.Message);
                }
                return null;
            }
        }

        private object Val(PropertyInfo p, object o)
        {
            if (p == null || o == null) { return null; }
            try { return p.GetValue(o, null); }
            catch (Exception error) { RecordNativeReadFailure(p.DeclaringType, p.Name, error); return null; }
        }

        private void MaybeSnapshot(bool force, long requestId)
        {
            // GameManager.GameState throws while no match is loaded, so only poll it on a
            // budget (1/s idle, ~2/s in game) instead of every frame.
            if (!force && !PeriodicWorkDue(ref _nextSnapshotTime, GameActive ? 500 : 1000)) return;
            long since = _tick - _lastProbeTick;
            if (!TransportSnapshotPrecheck(_tick, _lastProbeTick, force, GameActive, WorkDir, OwnPid,
                TransportIoStats.Shared)) return;
            _lastProbeTick = _tick;

            var gs = GameStateInstance();
            if (gs == null)
            {
                if (force) { Reply("{\"ok\":false,\"error\":\"no active game\"}", requestId); }
                if (GameActive) { GameActive = false; Log("state: no active game"); }
                return;
            }
            if (!GameActive)
            {
                GameActive = true;
                Log("state: match detected");
                try
                {
                    File.WriteAllText(Path.Combine(WorkDir, "match-active.json"),
                        "{\"detected\":\"" + DateTime.Now.ToString("s") + "\"}\n");
                }
                catch { }
            }
            var turn = Val(_currentTurnProp, gs);
            var idx = Val(_currentPlayerIndexProp, gs);
            var key = (turn == null ? "?" : turn.ToString()) + "/" + (idx == null ? "?" : idx.ToString());
            bool changed = key != _lastKey;
            if (!force && !changed && since < 120) { return; }
            _lastSnapshotTicks = _tick;
            if (!PrivateSamplingAllowed(gs))
            {
                if (force) Reply("{\"ok\":false,\"errorCode\":\"CONTROLLED_SEAT_PAUSED\"}", requestId);
                return;
            }
            _lastKey = key;
            Reply(Observation(gs, turn, idx, force || changed), requestId);
            _snapshotCount++;
        }

        private string Observation(object gs, object turn, object idx, bool changed)
        {
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"type\":\"obs\",\"changed\":").Append(changed ? "true" : "false");
            sb.Append(",\"turn\":").Append(Num(turn));
            sb.Append(",\"playerIndex\":").Append(Num(idx));
            sb.Append(",\"phase\":\"").Append(Esc(Str(Val(_currentStateProp, gs)))).Append('"');
            // currentUnitId is a global game field whose visibility to a player is unproven, so it
            // is withheld by default rather than passed off as public state.
            sb.Append(",\"currentUnitId\":null,\"currentUnitIdWithheld\":true");
            sb.Append(",\"playerCount\":").Append(PlayerCount(gs));
            sb.Append(",\"players\":").Append(Players(gs));
            sb.Append(",\"map\":").Append(MapInfo(gs));
            sb.Append(",\"tick\":").Append(_tick);
            sb.Append('}');
            return sb.ToString();
        }

        private string Num(object v)
        {
            if (v == null) { return "null"; }
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture); }
            catch { return "null"; }
        }

        private string Str(object v) { return v == null ? "" : v.ToString(); }

        private string PlayerCount(object gs)
        {
            int n;
            if (TryCount(Val(_playersProp, gs), out n)) { return n.ToString(CultureInfo.InvariantCulture); }
            return "null";
        }

        /// <summary>Count any il2cpp list/array (they do not always surface as IEnumerable here).</summary>
        private static bool TryCount(object o, out int count)
        {
            count = 0;
            if (o == null) { return false; }
            var en = o as IEnumerable;
            if (en != null) { foreach (var _ in en) { count++; } return true; }
            var t = o.GetType();
            var c = t.GetProperty("Count") ?? t.GetProperty("Length");
            if (c != null)
            {
                try { count = Convert.ToInt32(c.GetValue(o, null)); return true; } catch { }
            }
            return false;
        }

        private static IEnumerable AsEnumerable(object o)
        {
            if (o == null) { return null; }
            var en = o as IEnumerable;
            if (en != null) { return en; }
            // il2cpp generic lists sometimes only expose a struct enumerator; fall back to indexer
            var t = o.GetType();
            var item = t.GetProperty("Item");
            var cnt = t.GetProperty("Count");
            if (item == null || cnt == null) { return null; }
            int n;
            try { n = Convert.ToInt32(cnt.GetValue(o, null)); } catch { return null; }
            var items = new List<object>();
            for (int i = 0; i < n; i++)
            {
                try { items.Add(item.GetValue(o, new object[] { i })); } catch { }
            }
            return items;
        }

        /// <summary>
        /// Players as the local human may know them. The scoreboard (tribe + score) is public, but
        /// another player's treasury, city count and units are not: those are emitted as null/[ ]
        /// with `witheldByFog`, so a reader can tell "hidden" apart from "zero".
        /// </summary>
        private string Players(object gs)
        {
            var list = AsEnumerable(Val(_playersProp, gs));
            if (list == null) { return "[]"; }
            // Identity must be verified before anything can be called "mine". When it cannot be
            // verified, nobody is me and every private field stays withheld.
            var identity = ResolveControlledIdentity(gs);
            byte me = identity.Id;
            bool verified = identity.Verified;
            var sb = new StringBuilder("[");
            int i = 0;
            foreach (var p in list)
            {
                if (p == null) { continue; }
                if (i > 0) { sb.Append(','); }
                var pt = p.GetType();
                var idv = Grab(pt, p, "Id", "_Id_k__BackingField", "PlayerId");
                byte id = 0;
                if (idv != null) { try { id = Convert.ToByte(idv); } catch { } }
                bool mine = verified && id == me;
                var auto = Grab(pt, p, "AutoPlay", "_AutoPlay_k__BackingField", "IsAI");
                bool autoOn = false;
                if (auto != null) { try { autoOn = Convert.ToBoolean(auto); } catch { } }
                sb.Append("{\"index\":").Append(i)
                  .Append(",\"id\":").Append(id.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"tribe\":\"").Append(Esc(Str(Grab(pt, p, "Tribe", "TribeType", "tribe")))).Append('"')
                  .Append(",\"autoPlay\":").Append(autoOn ? "true" : "false")
                  .Append(",\"isHuman\":").Append(autoOn ? "false" : "true")
                  .Append(",\"isMe\":").Append(mine ? "true" : "false")
                  .Append(",\"score\":").Append(Num(Grab(pt, p, "Score", "Points", "score")));
                if (mine)
                {
                    sb.Append(",\"stars\":").Append(Num(Grab(pt, p, "Stars", "Currency", "currency")))
                      .Append(",\"cities\":").Append(Count(Grab(pt, p, "CityStates", "Cities", "cityStates")))
                      .Append(",\"units\":").Append(UnitList(pt, p));
                }
                else
                {
                    // invisible in game: withheld and labelled, never reported as a real zero
                    sb.Append(",\"stars\":null,\"cities\":null,\"units\":[],\"witheldByFog\":true");
                }
                sb.Append('}');
                i++;
            }
            sb.Append(']');
            return sb.ToString();
        }

        private string Bool(object v)
        {
            if (v == null) { return "null"; }
            try { return Convert.ToBoolean(v) ? "true" : "false"; } catch { return "null"; }
        }

        private string Count(object v)
        {
            int n;
            if (TryCount(v, out n)) { return n.ToString(CultureInfo.InvariantCulture); }
            return "null";
        }

        /// <summary>Compact unit list: id/tile/hp/moves so an agent can plan without any UI.</summary>
        private string UnitList(Type pt, object p)
        {
            var units = AsEnumerable(Grab(pt, p, "UnitStates", "Units"));
            if (units == null) { return "[]"; }
            var sb = new StringBuilder("[");
            int n = 0;
            foreach (var u in units)
            {
                if (u == null) { continue; }
                if (n >= 40) { break; }
                var ut = u.GetType();
                if (n > 0) { sb.Append(','); }
                sb.Append("{\"id\":").Append(Num(Grab(ut, u, "Id", "UnitId")))
                  .Append(",\"type\":\"").Append(Esc(Str(Grab(ut, u, "Type", "UnitType")))).Append('"')
                  .Append(",\"x\":").Append(Num(Grab(ut, u, "X", "TileX")))
                  .Append(",\"y\":").Append(Num(Grab(ut, u, "Y", "TileY")))
                  .Append(",\"hp\":").Append(Num(Grab(ut, u, "Hp", "Health")))
                  .Append(",\"maxHp\":").Append(Num(Grab(ut, u, "MaxHp", "MaxHealth")))
                  .Append(",\"moved\":").Append(Bool(Grab(ut, u, "HasMoved", "Moved", "IsDone")))
                  .Append(",\"attacked\":").Append(Bool(Grab(ut, u, "HasAttacked", "Attacked")))
                  .Append('}');
                n++;
            }
            sb.Append(']');
            return sb.ToString();
        }

        private object Grab(Type t, object o, params string[] names)
        {
            foreach (var raw in names)
            {
                foreach (var n in new[] { raw, char.ToLowerInvariant(raw[0]) + raw.Substring(1) })
                {
                    var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                    if (p != null && p.GetIndexParameters().Length == 0) { try { return p.GetValue(o, null); } catch { } }
                    var f = t.GetField(n, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                    if (f != null) { try { return f.GetValue(o); } catch { } }
                }
            }
            return null;
        }

        private string MapInfo(object gs)
        {
            var map = Val(_mapProp, gs);
            if (map == null) { return "null"; }
            var mt = map.GetType();
            var w = Grab(mt, map, "Width", "SizeX");
            var h = Grab(mt, map, "Height", "SizeY");
            var tiles = Grab(mt, map, "Tiles", "TileData");
            return "{\"width\":" + Num(w) + ",\"height\":" + Num(h) + ",\"tiles\":" + Count(tiles) + "}";
        }

        // ------------------------------------------------------------- actions

        private string EndTurn()
        {
            return ExecuteGameAction("end_turn", "", "", "", "", "", "", "");
        }

        private object InstanceOf(Type t)
        {
            return StaticMember(t, "Instance");
        }

        /// <summary>
        /// List live uGUI buttons (name + screen position + active state), or invoke the one
        /// whose name contains `name`. Lets the agent hit the right target regardless of layout.
        /// </summary>
        /// <summary>
        /// Invoke a no-arg game method by type+name. This is the agent's action channel that does
        /// NOT depend on the UI: e.g. {"cmd":"call","type":"GameManager","method":"ResumeSingleplayerGame"}.
        /// </summary>
        private string CallMethod(string typeName, string methodName, string args)
        {
            if (methodName.Length == 0) { return "{\"ok\":false,\"error\":\"method required\"}"; }
            var t = FindType("Il2Cpp." + typeName) ?? FindType(typeName) ?? FindType("PolytopiaAssembly." + typeName);
            if (t == null) { return "{\"ok\":false,\"error\":\"type not found: " + Esc(typeName) + "\"}"; }
            if (t.Name == "GameManager" && methodName == "ResumeSingleplayerGame" && !SingleplayerResumeReady())
                return "{\"ok\":false,\"errorCode\":\"NATIVE_MENU_NOT_READY\",\"dispatched\":false}";

            var candidates = new List<MethodInfo>();
            bool wantsArg = !string.IsNullOrEmpty(args);
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                if (m.Name != methodName) { continue; }
                var fps = m.GetParameters();
                // codex-dsh-066: an optional single-string argument lets one entry point answer a
                // parameterised read (e.g. an adjacency query for one coordinate). Everything else
                // keeps the original zero-argument contract.
                if (wantsArg)
                {
                    if (fps.Length == 1 && fps[0].ParameterType == typeof(string)) { candidates.Add(m); }
                }
                else if (fps.Length == 0) { candidates.Add(m); }
            }
            if (candidates.Count == 0)
            {
                var names = new List<string>();
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                {
                    if (m.Name.IndexOf(methodName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        names.Add(m.Name + "(" + m.GetParameters().Length + ")"
                            + (m.IsStatic ? ":static" : ":instance"));
                    }
                }
                var sq = new StringBuilder("{\"ok\":false,\"error\":\"no ").Append(wantsArg ? "single-string" : "zero-arg")
                    .Append(" method ").Append(Esc(methodName))
                    .Append("\",\"similar\":[");
                sq.Append(string.Join(",", Array.ConvertAll(names.ToArray(), x => "\"" + Esc(x) + "\"")));
                sq.Append("]}");
                return sq.ToString();
            }

            foreach (var m in candidates)
            {
                object target = null;
                if (!m.IsStatic)
                {
                    target = InstanceOf(t);
                    if (target == null) { return "{\"ok\":false,\"error\":\"no instance of " + Esc(t.Name) + "\"}"; }
                }
                try
                {
                    var r = m.Invoke(target, wantsArg ? new object[] { args } : null);
                    string called = t.Name + "." + m.Name + "(" + (wantsArg ? "args" : "") + ")";
                    return "{\"ok\":true,\"invoked\":\"" + Esc(called) + "\",\"returns\":\""
                        + Esc(r == null ? "null" : r.ToString()) + "\"}";
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException ?? ex;
                    string called2 = t.Name + "." + m.Name + "(" + (wantsArg ? "args" : "") + ")";
                    return "{\"ok\":false,\"invoked\":\"" + Esc(called2) + "\",\"error\":\""
                        + Esc(inner.GetType().Name + ": " + inner.Message) + "\"}";
                }
            }
            return "{\"ok\":false,\"error\":\"no invocable candidate\"}";
        }

        /// <summary>Read a GameManager static field/property (debug switches, flags).</summary>
        private string GetStatic(string name)
        {
            if (_gameManagerType == null) { return "{\"ok\":false,\"error\":\"no GameManager\"}"; }
            var p = _gameManagerType.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (p != null)
            {
                try { var v = p.GetValue(null, null); return "{\"ok\":true,\"name\":\"" + Esc(name) + "\",\"value\":\"" + Esc(v == null ? "null" : v.ToString()) + "\"}"; }
                catch (Exception ex) { return "{\"ok\":false,\"error\":\"" + Esc(ex.Message) + "\"}"; }
            }
            var f = _gameManagerType.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f != null)
            {
                try { var v = f.GetValue(null); return "{\"ok\":true,\"name\":\"" + Esc(name) + "\",\"value\":\"" + Esc(v == null ? "null" : v.ToString()) + "\"}"; }
                catch (Exception ex) { return "{\"ok\":false,\"error\":\"" + Esc(ex.Message) + "\"}"; }
            }
            var names = new List<string>();
            foreach (var m in _gameManagerType.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (m.Name.IndexOf("debug", StringComparison.OrdinalIgnoreCase) >= 0) { names.Add(m.Name); }
            }
            return "{\"ok\":false,\"error\":\"member not found\",\"debugMembers\":["
                + string.Join(",", Array.ConvertAll(names.ToArray(), x => "\"" + Esc(x) + "\"")) + "]}";
        }

        /// <summary>Write a GameManager static bool/int field or property.</summary>
        private string SetStatic(string name, string value)
        {
            if (_gameManagerType == null) { return "{\"ok\":false,\"error\":\"no GameManager\"}"; }
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            PropertyInfo p = _gameManagerType.GetProperty(name, flags);
            FieldInfo f = p == null ? _gameManagerType.GetField(name, flags) : null;
            if (p == null && f == null) { return GetStatic(name); }
            Type vt = p != null ? p.PropertyType : f.FieldType;
            object converted;
            try
            {
                if (vt == typeof(bool)) { converted = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase); }
                else if (vt == typeof(int)) { converted = int.Parse(value, CultureInfo.InvariantCulture); }
                else if (vt == typeof(float)) { converted = float.Parse(value, CultureInfo.InvariantCulture); }
                else { converted = value; }
            }
            catch (Exception ex) { return "{\"ok\":false,\"error\":\"bad value: " + Esc(ex.Message) + "\"}"; }
            try
            {
                if (p != null) { p.SetValue(null, converted, null); } else { f.SetValue(null, converted); }
                return "{\"ok\":true,\"name\":\"" + Esc(name) + "\",\"set\":\"" + Esc(converted.ToString()) + "\"}";
            }
            catch (Exception ex) { return "{\"ok\":false,\"error\":\"" + Esc(ex.Message) + "\"}"; }
        }

        /// <summary>Dump every PlayerState and UnitState field so the obs schema can be exact.</summary>
        private string PlayersDump()
        {
            var gs = GameStateInstance();
            if (gs == null) { return "{\"ok\":false,\"error\":\"no active game\"}"; }
            var gsType = gs.GetType();
            var players = AsEnumerable(Val(gsType.GetProperty("PlayerStates"), gs));
            var sb = new StringBuilder("{\"ok\":true,\"omniscient\":true"
                + ",\"warning\":\"raw PlayerState dump: enemy stars/cities/startTile are NOT visible in game. "
                + "Use 'world' or 'obs' for play decisions; this command exists to verify game state.\""
                + ",\"players\":[");
            int i = 0;
            if (players != null)
            {
                foreach (var p in players)
                {
                    if (p == null) { continue; }
                    if (i > 0) { sb.Append(','); }
                    sb.Append("{\"index\":").Append(i).Append(",\"type\":\"").Append(Esc(p.GetType().Name)).Append('"');
                    sb.Append(",\"values\":{");
                    bool first = true;
                    foreach (var q in p.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (q.GetIndexParameters().Length > 0) { continue; }
                        object v;
                        try { v = q.GetValue(p, null); } catch { continue; }
                        if (v != null && v.GetType().Name.StartsWith("Il2Cpp", StringComparison.Ordinal)) { v = "<" + v.GetType().Name + ">"; }
                        if (!first) { sb.Append(','); }
                        first = false;
                        sb.Append('"').Append(Esc(q.Name)).Append("\":\"").Append(Esc(v == null ? "null" : v.ToString())).Append('"');
                    }
                    sb.Append("}}");
                    i++;
                }
            }
            sb.Append("],\"playerCount\":").Append(i).Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Inspect (and optionally set) PolytopiaInput's mode and its InputType blocker map.
        /// "Hover works but clicks do nothing" is exactly what a blocker or the wrong input mode
        /// produces, so the agent needs to see this state before acting.
        ///   {"cmd":"input"}                       -> read mode + blockers + mouse button state
        ///   {"cmd":"input","set":"MouseOrTouch"}  -> switch input mode
        /// </summary>
        private string InputState(string setMode)
        {
            var t = FindType("PolytopiaInput") ?? FindType("Il2Cpp.PolytopiaInput");
            if (t == null) { return "{\"ok\":false,\"error\":\"PolytopiaInput not found\"}"; }
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var sb = new StringBuilder("{\"ok\":true");

            var modeProp = t.GetProperty("_inputMode", flags) ?? t.GetProperty("inputMode", flags);
            var modeField = modeProp == null ? t.GetField("_inputMode", flags) : null;
            object modeVal = null;
            try
            {
                if (modeProp != null) { modeVal = modeProp.GetValue(null, null); }
                else if (modeField != null) { modeVal = modeField.GetValue(null); }
            }
            catch { }
            sb.Append(",\"mode\":\"").Append(Esc(modeVal == null ? "?" : modeVal.ToString())).Append('"');

            if (setMode.Length > 0)
            {
                try
                {
                    Type mt = modeProp != null ? modeProp.PropertyType : (modeField != null ? modeField.FieldType : null);
                    object conv = mt != null && mt.IsEnum ? Enum.Parse(mt, setMode, true) : (object)setMode;
                    if (modeProp != null && modeProp.CanWrite) { modeProp.SetValue(null, conv, null); }
                    else if (modeField != null) { modeField.SetValue(null, conv); }
                    var now = modeProp != null ? modeProp.GetValue(null, null) : modeField.GetValue(null);
                    sb.Append(",\"setMode\":\"").Append(Esc(now == null ? "?" : now.ToString())).Append('"');
                }
                catch (Exception ex) { sb.Append(",\"setModeError\":\"").Append(Esc(ex.Message)).Append('"'); }
            }

            var blockers = t.GetField("blockers", flags) != null
                ? t.GetField("blockers", flags).GetValue(null)
                : (t.GetProperty("blockers", flags) != null ? t.GetProperty("blockers", flags).GetValue(null, null) : null);
            sb.Append(",\"blockers\":");
            Log("input: blockers raw = " + (blockers == null ? "null" : blockers.GetType().FullName));
            try
            {
                if (blockers == null) { sb.Append("null"); }
                else
                {
                    var en = blockers as IEnumerable;
                    sb.Append('{');
                    bool first = true;
                    int guard = 0;
                    if (en != null)
                    {
                        foreach (var kv in en)
                        {
                            if (kv == null || ++guard > 64) { continue; }
                            var k = Member(kv, "Key") ?? Member(kv, "key");
                            var v = Member(kv, "Value") ?? Member(kv, "value");
                            if (!first) { sb.Append(','); }
                            first = false;
                            sb.Append('"').Append(Esc(k == null ? "?" : k.ToString())).Append("\":")
                              .Append(Esc(v == null ? "?" : v.ToString()));
                        }
                    }
                    sb.Append('}');
                    sb.Append(",\"blockersEnumerable\":").Append(en != null ? "true" : "false");
                }
            }
            catch (Exception ex)
            {
                Log("input: blockers failed " + ex.Message);
                sb.Append("\"<error:").Append(Esc(ex.Message)).Append(">\"");
            }

            // the game's own virtual cursor (used by the Omnicursor input modes)
            try
            {
                var oc = t.GetField("Omnicursor", flags) != null ? t.GetField("Omnicursor", flags).GetValue(null) : null;
                if (oc != null)
                {
                    var pos = Member(oc, "Position") ?? Member(oc, "position") ?? Member(oc, "ScreenPosition");
                    sb.Append(",\"omnicursor\":\"").Append(Esc(oc.GetType().Name)).Append('"');
                    if (pos != null)
                    {
                        sb.Append(",\"omnicursorPos\":\"").Append(Esc(pos.ToString())).Append('"');
                    }
                    sb.Append(",\"omnicursorMembers\":[");
                    var om = new List<string>();
                    foreach (var m in oc.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        om.Add(m.Name);
                        if (om.Count > 40) { break; }
                    }
                    sb.Append(string.Join(",", Array.ConvertAll(om.ToArray(), x => "\"" + Esc(x) + "\"")));
                    sb.Append(']');
                }
                else { sb.Append(",\"omnicursor\":null"); }
            }
            catch (Exception ex) { Log("input: omnicursor failed " + ex.Message); }

            // live legacy-Input state through the game's own wrapper
            foreach (var name in new[] { "GetMouseButton", "GetMouseButtonDown", "GetMouseButtonUp", "get_touchCount" })
            {
                var m = t.GetMethod(name, flags);
                if (m == null) { continue; }
                try
                {
                    object[] args = m.GetParameters().Length == 1 ? new object[] { 0 } : null;
                    var r = m.Invoke(null, args);
                    sb.Append(",\"").Append(name).Append("\":\"").Append(Esc(r == null ? "null" : r.ToString())).Append('"');
                }
                catch { }
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Execute a game command through the game's own ActionManager (no UI input at all).
        ///   {"cmd":"action","name":"end_turn"}
        ///   {"cmd":"action","name":"city_reward","reward":"workshop"|"explorer"}
        /// This is the deterministic action channel: construct the real Command object and hand it
        /// to ClientActionManager.ExecuteCommand, the same path the game itself uses.
        /// </summary>
        private string ExecuteGameAction(string name, string reward, string accept,
            string unitFrom, string unitTo, string unitAt, string buildKind, string auditArg, object preparedNativeCommand = null,
            string associatedUnitIdOverride = null)
        {
            var gs = GameStateInstance();
            if (gs == null) { return ActionPreparationError("NO_ACTIVE_GAME", "no active game"); }

            object gmInstance = _gameManagerType != null ? SafeInstance() : null;

            // GameManager.actionManager is NOT a GameManager field (the dump shows it on a
            // compiler-generated state machine); the real holder is ClientBase.ActionManager.
            // Use il2cpp's own reflection so the wrapper's managed view cannot hide it.
            object am = FindActionManager(gmInstance, true);
            if (am == null)
            {
                return PreDispatchRejection().Str("errorCode", "ACTION_MANAGER_UNAVAILABLE")
                    .Str("error", "main client action manager unavailable").Close();
            }
            Log("action: ActionManager = " + am.GetType().Name);

            Type cmdType = null;
            object[] ctorArgs = null;
            string label = name;
            // Actions whose own target/argument are not the `from`/`to`/`kind` fields: the reward
            // choice names a CityReward and belongs to the city in the pending CityLevelUp trigger.
            // Without these the confirmation can only say "needs the reward that was chosen".
            string echoCoordOverride = null;
            string echoArgOverride = null;

            if (preparedNativeCommand != null)
            {
                cmdType = preparedNativeCommand.GetType();
                label = cmdType.Name;
            }
            else if (name == "end_turn" || name == "endturn")
            {
                cmdType = FindType("EndTurnCommand") ?? FindType("Il2Cpp.EndTurnCommand");
                byte currentPlayer = CurrentPlayerId(gs);
                ctorArgs = new object[] { currentPlayer };
                label = "EndTurnCommand(player=" + currentPlayer + ")";
            }
            else if (name == "city_reward" || name == "reward")
            {
                cmdType = FindType("CityRewardCommand") ?? FindType("Il2Cpp.CityRewardCommand");
                var rewardType = FindType("CityReward") ?? FindType("Il2Cpp.CityReward");
                if (cmdType == null || rewardType == null) { return ActionPreparationError("COMMAND_TYPE_UNAVAILABLE", "native command type unavailable"); }
                var rewardName = reward.Length > 0 ? reward : "workshop";
                object rewardVal;
                if (!TryNamedNativeEnum(rewardType, rewardName, out rewardVal))
                    return ActionInvalidEnumResponse("reward", rewardName, rewardType);

                // the city waiting for a reward carries its coordinates in the trigger list
                object coords = BuildCoordinatesFromPendingTrigger(gs);
                if (coords == null) return PreDispatchRejection().Str("errorCode", "CITY_REWARD_COORDINATES_UNAVAILABLE")
                    .Str("error", "no readable pending own city reward coordinates").Close();
                byte player = CurrentPlayerId(gs);
                ctorArgs = new object[] { player, rewardVal, coords };
                label = "CityRewardCommand(player=" + player + ", " + rewardName + ", " + coords + ")";
                // Associate the selected reward with the same bounded own trigger coordinate
                // used by the command. Missing metadata is a preparation refusal above.
                echoArgOverride = rewardName;
                echoCoordOverride = SafeScalar(coords);
            }
            else if (name == "peace_response" || name == "peace" || name == "respond")
            {
                // Tribe encounter / peace request. Verified interface:
                //   PeaceRequestResponseCommand(byte playerId, byte opponentId, bool accepted)
                // with opponentId taken from the pending CommandTrigger of type PeaceRequest.
                cmdType = FindType("PeaceRequestResponseCommand") ?? FindType("Il2Cpp.PeaceRequestResponseCommand");
                if (cmdType == null) { return ActionPreparationError("COMMAND_TYPE_UNAVAILABLE", "native command type unavailable"); }
                var trigInfo = new TriggerInfo();
                if (!FindPendingTrigger(gs, "PeaceRequest", out trigInfo))
                {
                    return ActionPreparationError("PEACE_TRIGGER_UNAVAILABLE", "no pending own peace request");
                }
                byte player = CurrentPlayerId(gs);
                byte opponent = (byte)trigInfo.OpponentId;
                bool accepted;
                if (!TryActionBoolean(accept, true, out accepted))
                    return ActionPreparationError("INVALID_BOOLEAN", "accept must be true/false or 1/0");
                ctorArgs = new object[] { player, opponent, accepted };
                label = "PeaceRequestResponseCommand(player=" + player + ", opponent=" + opponent
                    + ", accepted=" + (accepted ? "true" : "false") + ")";
            }
            else if (name == "move")
            {
                // MoveCommand(byte playerId, UnitState unit, WorldCoordinates target)
                cmdType = FindType("MoveCommand") ?? FindType("Il2Cpp.MoveCommand");
                if (cmdType == null) { return ActionPreparationError("COMMAND_TYPE_UNAVAILABLE", "native command type unavailable"); }
                var unit = FindUnitAt(gs, unitFrom);
                if (unit == null) { return ActionPreparationError("UNIT_ACTOR_UNAVAILABLE", "own visible unit unavailable"); }
                int tx, ty;
                if (!TryParseCoords(unitTo, out tx, out ty))
                {
                    return ActionPreparationError("INVALID_COORDINATES", "coordinates invalid, unavailable or outside map");
                }
                object target = BuildCoordinates(tx, ty, gs);
                if (target == null) { return ActionPreparationError("INVALID_COORDINATES", "coordinates invalid, unavailable or outside map"); }
                ctorArgs = new object[] { CurrentPlayerId(gs), unit, target };
                label = "MoveCommand(player=" + CurrentPlayerId(gs) + ", unit=" + SafeScalar(MemberWithAccessor(unit, "id"))
                    + " @ " + Esc(unitFrom) + " -> " + Esc(unitTo) + ")";
            }
            else if (name == "explode" || name == "harvest")
            {
                // ExplodeCommand(byte playerId, WorldCoordinates coordinates): the game's own
                // action for harvesting an animal (and the star reward that follows).
                cmdType = FindType("ExplodeCommand") ?? FindType("Il2Cpp.ExplodeCommand");
                if (cmdType == null) { return ActionPreparationError("COMMAND_TYPE_UNAVAILABLE", "native command type unavailable"); }
                int hx, hy;
                if (!TryParseCoords(unitAt.Length > 0 ? unitAt : unitFrom, out hx, out hy))
                {
                    return ActionPreparationError("INVALID_COORDINATES", "coordinates invalid, unavailable or outside map");
                }
                object at = BuildCoordinates(hx, hy, gs);
                if (at == null) { return ActionPreparationError("INVALID_COORDINATES", "coordinates invalid, unavailable or outside map"); }
                ctorArgs = new object[] { CurrentPlayerId(gs), at };
                label = "ExplodeCommand(player=" + CurrentPlayerId(gs) + ", at=" + Esc(unitAt.Length > 0 ? unitAt : unitFrom) + ")";
            }
            else if (name == "build" || name == "harvest_improvement")
            {
                // BuildCommand(byte playerId, ImprovementData.Type type, WorldCoordinates coordinates)
                // This is how the game harvests a resource: HarvestFruit / Hunting / Mine ...
                cmdType = FindType("BuildCommand") ?? FindType("Il2Cpp.BuildCommand");
                if (cmdType == null) { return ActionPreparationError("COMMAND_TYPE_UNAVAILABLE", "native command type unavailable"); }
                var impType = FindType("ImprovementData+Type") ?? FindType("Il2Cpp.ImprovementData+Type")
                    ?? FindTypeBySuffix("ImprovementData+Type") ?? FindType("ImprovementType");
                if (impType == null) { return ActionPreparationError("NATIVE_ENUM_UNAVAILABLE", "native enum definition unavailable"); }
                var kind = buildKind.Length > 0 ? buildKind : "HarvestFruit";
                object kindVal;
                if (!TryNamedNativeEnum(impType, kind, out kindVal))
                    return ActionInvalidEnumResponse("improvement", kind, impType);
                var atArg = unitAt.Length > 0 ? unitAt : unitFrom;
                int bx, by;
                if (!TryParseCoords(atArg, out bx, out by))
                {
                    return ActionPreparationError("INVALID_COORDINATES", "coordinates invalid, unavailable or outside map");
                }
                object bcoords = BuildCoordinates(bx, by, gs);
                if (bcoords == null) { return ActionPreparationError("INVALID_COORDINATES", "coordinates invalid, unavailable or outside map"); }
                ctorArgs = new object[] { CurrentPlayerId(gs), kindVal, bcoords };
                label = "BuildCommand(player=" + CurrentPlayerId(gs) + ", " + kind + ", at=" + Esc(atArg) + ")";
            }
            else if (name == "upgrade" || name == "upgrade_unit" || name == "upgradeunit")
            {
                // All legacy spellings refer to the native unit UpgradeCommand.
                name = "upgrade_unit";
                cmdType = FindType("UpgradeCommand") ?? FindType("Il2Cpp.UpgradeCommand");
                if (cmdType == null) return PreDispatchRejection().Str("errorCode", "UPGRADE_COMMAND_UNAVAILABLE").Close();
                string upgradeError;
                if (!TryPrepareUnitUpgrade(gs, unitFrom, unitAt, buildKind, out ctorArgs, out upgradeError))
                    return PreDispatchRejection().Str("errorCode", upgradeError)
                        .Str("error", "unit upgrade requires kind=<UnitData.Type> and one own-unit tile; from/at must agree").Close();
                echoCoordOverride = SafeScalar(ctorArgs[2]);
                echoArgOverride = SafeScalar(ctorArgs[1]);
                label = "UpgradeCommand(player=" + CurrentPlayerId(gs) + ", " + Esc(echoArgOverride)
                    + ", at=" + Esc(echoCoordOverride) + ")";
            }
            else if (name == "ruins")
            {
                // ExamineRuinsCommand(byte playerId, WorldCoordinates coordinates)
                cmdType = FindType("ExamineRuinsCommand") ?? FindType("Il2Cpp.ExamineRuinsCommand");
                if (cmdType == null) { return ActionPreparationError("COMMAND_TYPE_UNAVAILABLE", "native command type unavailable"); }
                var atArg3 = unitAt.Length > 0 ? unitAt : unitFrom;
                int rx, ry;
                if (!TryParseCoords(atArg3, out rx, out ry))
                {
                    return ActionPreparationError("INVALID_COORDINATES", "coordinates invalid, unavailable or outside map");
                }
                object rcoords = BuildCoordinates(rx, ry, gs);
                if (rcoords == null) { return ActionPreparationError("INVALID_COORDINATES", "coordinates invalid, unavailable or outside map"); }
                ctorArgs = new object[] { CurrentPlayerId(gs), rcoords };
                label = "ExamineRuinsCommand(player=" + CurrentPlayerId(gs) + ", at=" + Esc(atArg3) + ")";
            }
            else if (name == "research")
            {
                // ResearchCommand(byte playerId, TechData.Type type)
                cmdType = FindType("ResearchCommand") ?? FindType("Il2Cpp.ResearchCommand");
                if (cmdType == null) { return ActionPreparationError("COMMAND_TYPE_UNAVAILABLE", "native command type unavailable"); }
                var techType = FindType("TechData+Type") ?? FindTypeBySuffix("TechData+Type");
                if (techType == null) { return ActionPreparationError("NATIVE_ENUM_UNAVAILABLE", "native enum definition unavailable"); }
                var techName = buildKind.Length > 0 ? buildKind : unitAt;
                object techVal;
                if (!TryNamedNativeEnum(techType, techName, out techVal))
                    return ActionInvalidEnumResponse("tech", techName, techType);
                ctorArgs = new object[] { CurrentPlayerId(gs), techVal };
                label = "ResearchCommand(player=" + CurrentPlayerId(gs) + ", " + techName + ")";
            }
            else if (name == "train")
            {
                // TrainCommand(byte playerId, UnitData.Type type, WorldCoordinates coordinates)
                cmdType = FindType("TrainCommand") ?? FindType("Il2Cpp.TrainCommand");
                if (cmdType == null) { return ActionPreparationError("COMMAND_TYPE_UNAVAILABLE", "native command type unavailable"); }
                var unitKind = FindType("UnitData+Type") ?? FindTypeBySuffix("UnitData+Type");
                if (unitKind == null) { return ActionPreparationError("NATIVE_ENUM_UNAVAILABLE", "native enum definition unavailable"); }
                var unitName = buildKind.Length > 0 ? buildKind : "Warrior";
                object unitVal;
                if (!TryNamedNativeEnum(unitKind, unitName, out unitVal))
                    return ActionInvalidEnumResponse("unit", unitName, unitKind);
                var atTrain = unitAt.Length > 0 ? unitAt : unitFrom;
                int trx, tryy;
                if (!TryParseCoords(atTrain, out trx, out tryy))
                {
                    return ActionPreparationError("INVALID_COORDINATES", "coordinates invalid, unavailable or outside map");
                }
                object tcoords = BuildCoordinates(trx, tryy, gs);
                if (tcoords == null) { return ActionPreparationError("INVALID_COORDINATES", "coordinates invalid, unavailable or outside map"); }
                ctorArgs = new object[] { CurrentPlayerId(gs), unitVal, tcoords };
                label = "TrainCommand(player=" + CurrentPlayerId(gs) + ", " + unitName + ", at=" + Esc(atTrain) + ")";
            }
            else if (name == "attack")
            {
                // AttackCommand(byte playerId, UnitState unit, WorldCoordinates target)
                cmdType = FindType("AttackCommand") ?? FindType("Il2Cpp.AttackCommand");
                if (cmdType == null) { return ActionPreparationError("COMMAND_TYPE_UNAVAILABLE", "native command type unavailable"); }
                var attacker = FindUnitAt(gs, unitFrom);
                if (attacker == null) { return ActionPreparationError("UNIT_ACTOR_UNAVAILABLE", "own visible unit unavailable"); }
                int ax, ay;
                if (!TryParseCoords(unitTo, out ax, out ay))
                {
                    return ActionPreparationError("INVALID_COORDINATES", "coordinates invalid, unavailable or outside map");
                }
                object acoords = BuildCoordinates(ax, ay, gs);
                if (acoords == null) { return ActionPreparationError("INVALID_COORDINATES", "coordinates invalid, unavailable or outside map"); }
                ctorArgs = new object[] { CurrentPlayerId(gs), attacker, acoords };
                label = "AttackCommand(player=" + CurrentPlayerId(gs) + ", unit=" + SafeScalar(MemberWithAccessor(attacker, "id"))
                    + " @ " + Esc(unitFrom) + " -> " + Esc(unitTo) + ")";
            }
            else if (name == "capture")
            {
                // CaptureCommand(byte playerId, uint unitId, WorldCoordinates coordinates)
                var atCap = unitAt.Length > 0 ? unitAt : unitFrom;
                string captureError;
                if (!TryPrepareCaptureCommand(gs, unitFrom, atCap, out preparedNativeCommand, out captureError))
                {
                    if (captureError == "CAPTURE_CONSTRUCTION_FAILED") return CommandConstructionUnknown(captureError);
                    return PreDispatchRejection().Str("errorCode", captureError)
                        .Str("error", "capture command could not be prepared from the current controlled unit").Close();
                }
                cmdType = preparedNativeCommand.GetType();
                label = "CaptureCommand(player=" + CurrentPlayerId(gs) + ", unitId="
                    + SafeScalar(MemberWithAccessor(preparedNativeCommand, "UnitId")) + ", at=" + Esc(atCap) + ")";
            }
            else if (name == "disembark" || name == "land")
            {
                // DisembarkCommand(byte playerId, WorldCoordinates coordinates): land the unit
                // carried by the naval unit at these coordinates.
                cmdType = FindType("DisembarkCommand") ?? FindType("Il2Cpp.DisembarkCommand");
                if (cmdType == null) { return ActionPreparationError("COMMAND_TYPE_UNAVAILABLE", "native command type unavailable"); }
                var atDis = unitAt.Length > 0 ? unitAt : unitFrom;
                int dx2, dy2;
                if (!TryParseCoords(atDis, out dx2, out dy2))
                {
                    return ActionPreparationError("INVALID_COORDINATES", "coordinates invalid, unavailable or outside map");
                }
                object dcoords = BuildCoordinates(dx2, dy2, gs);
                if (dcoords == null) { return ActionPreparationError("INVALID_COORDINATES", "coordinates invalid, unavailable or outside map"); }
                ctorArgs = new object[] { CurrentPlayerId(gs), dcoords };
                label = "DisembarkCommand(player=" + CurrentPlayerId(gs) + ", at=" + Esc(atDis) + ")";
            }
            else if (name == "disband")
            {
                // DisbandCommand(byte playerId, WorldCoordinates coordinates). Mono.Cecil on this build
                // (codex-dsh-067 supplement) shows the constructor has NO unitId: the unit is named by
                // its tile. The unit is only used to refuse an empty tile before dispatch.
                cmdType = FindType("DisbandCommand") ?? FindType("Il2Cpp.DisbandCommand");
                if (cmdType == null) { return ActionPreparationError("COMMAND_TYPE_UNAVAILABLE", "native command type unavailable"); }
                var atBand = unitAt.Length > 0 ? unitAt : unitFrom;
                var bandUnit = FindUnitAt(gs, atBand);
                if (bandUnit == null) { return ActionPreparationError("UNIT_ACTOR_UNAVAILABLE", "own visible unit unavailable"); }
                int bdx, bdy;
                if (!TryParseCoords(atBand, out bdx, out bdy))
                {
                    return ActionPreparationError("INVALID_COORDINATES", "coordinates invalid, unavailable or outside map");
                }
                object bdcoords = BuildCoordinates(bdx, bdy, gs);
                if (bdcoords == null) { return ActionPreparationError("INVALID_COORDINATES", "coordinates invalid, unavailable or outside map"); }
                ctorArgs = new object[] { CurrentPlayerId(gs), bdcoords };
                label = "DisbandCommand(player=" + CurrentPlayerId(gs) + ", unit="
                    + Esc(SafeScalar(MemberWithAccessor(bandUnit, "id"))) + " @ " + Esc(atBand) + ")";
            }
            else
            {
                return ActionPreparationError("UNKNOWN_ACTION", "unsupported raw action; use current native menu descriptors");
            }

            if (cmdType == null) { return ActionPreparationError("COMMAND_TYPE_UNAVAILABLE", "native command type unavailable"); }

            object command = preparedNativeCommand;
            var constructor = command == null ? UniqueInvocableConstructor(cmdType, ctorArgs) : null;
            if (command == null && constructor == null)
                return PreDispatchRejection().Str("errorCode", "COMMAND_CONSTRUCTOR_UNAVAILABLE")
                    .Str("error", "no unique compatible command constructor").Close();
            var exec = FindCommandExecutor(am.GetType(), cmdType);
            if (exec == null)
                return PreDispatchRejection().Str("errorCode", "COMMAND_EXECUTOR_UNAVAILABLE")
                    .Str("error", "no unique supported native command executor").Close();
            try
            {
                if (command == null) command = constructor.Invoke(ctorArgs);
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Log("command construction #" + _activeRequestId + " " + cmdType.Name + " failed: " + inner.GetType().Name);
                return CommandConstructionUnknown("COMMAND_CONSTRUCTION_FAILED");
            }

            // hand it to the game's ActionManager
            string called = exec.Name + "(" + exec.GetParameters().Length + ")";
            // One identity for the WHOLE command: resolved once, used for the before fingerprint,
            // for the after fingerprint (end_turn switches the acting player - the viewpoint must
            // not follow it), and for the follow-up pending count.
            var identity = ResolveControlledIdentity(gs);
            byte controlledBefore = CurrentPlayerId(gs);
            string before = ActionFingerprint(gs, identity);
            string associatedUnitId = associatedUnitIdOverride ?? SafeScalar(MemberWithAccessor(command, "UnitId"));
            string associatedCoord = echoCoordOverride ?? PublicCommandCoordinateForEcho(command, name)
                ?? (unitAt.Length > 0 ? unitAt : (unitTo.Length > 0 ? unitTo : unitFrom));
            if (associatedUnitId == null && IsUnitActorAction(name))
            {
                string origin = SafeScalar(MemberWithAccessor(command, "Origin") ?? MemberWithAccessor(command, "From"))
                    ?? (unitFrom.Length > 0 ? unitFrom : associatedCoord);
                string record = FpUnitAt(FpField(before, "units"), origin);
                if (record == null) record = FpUnitAt(FpField(before, "units"), associatedCoord);
                if (record != null && FpFieldAt(record, 1) == identity.Id.ToString(CultureInfo.InvariantCulture))
                    associatedUnitId = FpFieldAt(record, 0);
            }
            string settleState = CombatStateJson(gs);
            bool explicitResolution = name == "city_reward" || name == "reward" || name == "peace_response";
            if (Json.GetString(settleState, "settlementStatus") != "complete"
                && !(explicitResolution && CombatResolutionReady(gs) == true))
                return PreDispatchRejection().Str("errorCode", "ACTION_PIPELINE_BUSY").Raw("state", settleState).Close();
            long settlementTicket = BeginCombatSettlement(gs, name, associatedUnitId,
                associatedCoord,
                echoArgOverride ?? (buildKind.Length > 0 ? buildKind : null), _activeRequestId);
            CombatTicket tracedTicket;
            string executionEpisode = _combatTickets.TryGetValue(settlementTicket, out tracedTicket)
                && tracedTicket.Before != null ? tracedTicket.Before.Episode : "?";
            string executionTrace = "#" + _activeRequestId + " ticket=" + settlementTicket + " ep=" + executionEpisode
                + " player=" + controlledBefore + " stream=" + _combatStream;
            try
            {
                string validationError;
                Log("command dispatch " + executionTrace + " type=" + cmdType.Name);
                object ret = InvokeCommandExecutor(exec, am, command, out validationError);
                string retStr = ret == null ? "null" : ret.ToString();
                bool? accepted = NativeBool(ret);
                CompleteCombatDispatch(settlementTicket, accepted);
                Log("command result " + executionTrace + " " + label + " via " + called + " -> " + retStr
                    + (validationError.Length > 0 ? " err='" + validationError + "'" : ""));

                string after = auditArg == "1" ? ActionFingerprint(gs, identity) : null;
                int? pendAfter = identity.Verified
                    ? PendingTriggerCountFor(gs, identity.Id, new JsonArr()) : null;
                bool? popAfter = null;
                try { popAfter = PopupIsOpen(); } catch { }

                var echo = new ActionEcho();
                echo.ActionName = name;
                echo.ActionLabel = label;
                echo.Via = called;
                echo.Manager = am.GetType().Name;
                echo.Accepted = accepted;
                echo.AcceptedRaw = retStr;
                echo.ValidationError = validationError;
                echo.Before = before;
                echo.After = after;
                echo.ControlledId = identity.Verified
                    ? identity.Id.ToString(CultureInfo.InvariantCulture) : null;
                // Target association: the command's own unit/coordinate/argument, so the reply can
                // say whether THAT unit reached THAT tile rather than "something changed".
                echo.TargetUnitId = associatedUnitId;
                echo.TargetCoord = associatedCoord;
                echo.TargetArg = echoArgOverride ?? (buildKind.Length > 0 ? buildKind : null);
                echo.Audit = auditArg == "1";
                echo.PendingMineAfter = pendAfter;
                echo.PopupAfter = popAfter;
                return AttachCombatSettlement(BuildActionResponse(echo), gs, settlementTicket);
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                CompleteCombatDispatch(settlementTicket, null);
                Log("command execute " + executionTrace + " "
                    + cmdType.Name + " failed: " + inner.GetType().Name + ": " + inner.Message);
                // The raw exception text can carry internal detail: the planner gets a coarse
                // failure, and the precise message only when the audit flag was passed.
                return BuildCommandInvocationFailure(gs, settlementTicket, label, called, controlledBefore, inner, auditArg == "1");
            }
        }

        /// <summary>
        /// The player id a command must carry: PlayerState.Id of the player whose turn it is.
        /// This is NOT the player index (in this build the first player's Id is 1, index 0),
        /// which is why EndTurnCommand(playerIndex) is rejected with "Player mismatch".
        /// </summary>
        private byte CurrentPlayerId(object gs)
        {
            try
            {
                var players = AsEnumerable(Val(_playersProp, gs));
                if (players == null) { return 0; }
                int? idx = NativeInt32(Val(_currentPlayerIndexProp, gs));
                if (!idx.HasValue || idx.Value < 0) return 0; // Unknown; never assume index zero.
                int i = 0;
                foreach (var p in players)
                {
                    if (i == idx.Value)
                    {
                        if (p == null) return 0;
                        return NativePlayerId(Grab(p.GetType(), p, "Id", "_Id_k__BackingField", "PlayerId"), true) ?? (byte)0;
                    }
                    i++;
                }
            }
            catch (Exception ex) { Log("action: CurrentPlayerId failed " + ex.Message); }
            return 0;
        }

        /// <summary>Locate the coordinates of a city awaiting a reward choice (used by CityRewardCommand).</summary>
        /// <summary>
        /// Build a WorldCoordinates for the city that is awaiting a reward choice, from the
        /// CommandTrigger list (verified struct: playerId/opponentId/type/coordinates).
        /// Construction uses the confirmed static WorldCoordinates.FromIndex(int index, int width)
        /// rather than guessing a constructor signature.
        /// </summary>
        private object BuildCoordinatesFromPendingTrigger(object gs)
        {
            TriggerInfo found; int x, y;
            return FindPendingTrigger(gs, "CityLevelUp", out found) && TryParseCoords(found.Coordinates, out x, out y)
                ? BuildCoordinates(x, y, gs) : null;
        }

        internal struct TriggerInfo
        {
            internal int PlayerId;
            internal int OpponentId;
            internal string Type;
            internal string Coordinates;
        }

        /// <summary>
        /// Read GameState.pendingCommandTriggers and return the first entry of the given
        /// CommandTriggerType. CommandTrigger is a verified struct:
        ///   { byte playerId; byte opponentId; CommandTriggerType type; WorldCoordinates coordinates; }
        /// Read-only: touches nothing in the game.
        /// </summary>
        private bool FindPendingTrigger(object gs, string typeName, out TriggerInfo found)
        {
            found = new TriggerInfo();
            if (gs == null || _gameStateType == null || !PrivateSamplingAllowed(gs)) return false;
            byte player = CurrentPlayerId(gs);
            if (player == 0 || player == NaturePlayerId) return false;
            try
            {
                object raw = null;
                var f = _gameStateType.GetField("pendingCommandTriggers",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) { raw = f.GetValue(gs); }
                if (raw == null)
                {
                    var p = _gameStateType.GetProperty("pendingCommandTriggers",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null) { raw = p.GetValue(gs, null); }
                }
                if (raw == null) { Log("trigger: pendingCommandTriggers unavailable"); return false; }
                var list = ReadNativeList(raw);
                if (list == null) { Log("trigger: pendingCommandTriggers not enumerable"); return false; }

                foreach (var trig in list)
                {
                    if (trig == null) { continue; }
                    if (trig.GetType().Name.IndexOf("CommandTrigger", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                    var typeVal = SafeScalar(MemberWithAccessor(trig, "type"));
                    if (typeVal == null) { continue; }
                    if (!string.Equals(typeVal, typeName, StringComparison.Ordinal)) { continue; }
                    int? actor = NativeInt32(MemberWithAccessor(trig, "playerId"));
                    if (actor != player) continue; // Do not read another player's trigger target.
                    int? opponent = NativeInt32(MemberWithAccessor(trig, "opponentId"));
                    if (typeName == "PeaceRequest" && (!opponent.HasValue || opponent <= 0
                        || opponent >= NaturePlayerId || opponent == player)) continue;

                    found.Type = typeVal;
                    found.PlayerId = player;
                    found.OpponentId = opponent ?? -1;
                    found.Coordinates = typeName == "CityLevelUp"
                        ? SafeScalar(MemberWithAccessor(trig, "coordinates")) : null;
                    return true;
                }
                Log("trigger: no pending trigger of type " + typeName);
            }
            catch (Exception ex)
            {
                Log("trigger: FindPendingTrigger failed " + (ex.InnerException ?? ex).Message);
            }
            return false;
        }

        private int ReadMapWidth(object gs)
        {
            try
            {
                var map = Val(_mapProp, gs);
                if (map == null) { return 0; }
                var w = MemberWithAccessor(map, "width");
                int? width = NativeInt32(w);
                return width.HasValue && width.Value > 0 ? width.Value : 0;
            }
            catch { return 0; }
        }

        /// <summary>Parse the canonical "(x,y)" form produced by SafeScalar.</summary>
        internal static bool TryParseCoords(string s, out int x, out int y)
        {
            x = y = 0;
            if (string.IsNullOrEmpty(s)) { return false; }
            s = s.Trim();
            if (s.Length < 5 || s[0] != '(' || s[s.Length - 1] != ')') { return false; }
            var parts = s.Substring(1, s.Length - 2).Split(',');
            if (parts.Length != 2) { return false; }
            return int.TryParse(parts[0].Trim(), out x) && int.TryParse(parts[1].Trim(), out y);
        }

        /// <summary>WorldCoordinates.FromIndex(int index, int width) - the confirmed static factory.</summary>
        private static object WorldCoordinatesFromIndex(int index, int width)
        {
            if (index < 0 || width <= 0) return null;
            var t = FindType("WorldCoordinates") ?? FindType("Il2Cpp.WorldCoordinates");
            if (t == null) { return null; }
            var method = FindStaticUnique(t, "FromIndex", new[] { "Int32", "Int32" }, "WorldCoordinates");
            if (method == null || method.ReturnType != t) return null;
            try
            {
                object value = method.Invoke(null, new object[] { index, width });
                int? x = NativeInt32(MemberWithAccessor(value, "x")), y = NativeInt32(MemberWithAccessor(value, "y"));
                return x == index % width && y == index / width ? value : null;
            }
            catch (Exception ex) { Log("action: FromIndex failed " + (ex.InnerException ?? ex).GetType().Name); return null; }
        }

        private object FindPendingRewardCoordinates(object gs)
        {
            try
            {
                var pendingProp = _gameStateType.GetProperty("pendingCommandTriggers", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? _gameStateType.GetProperty("PendingCommandTriggers", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var triggers = pendingProp != null ? pendingProp.GetValue(gs, null) : null;
                Log("action: pendingCommandTriggers = " + (triggers == null ? "null" : triggers.GetType().Name));
                foreach (var holder in new object[] { triggers, gs })
                {
                    if (holder == null) { continue; }
                    foreach (var p in holder.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (p.GetIndexParameters().Length > 0) { continue; }
                        var pn = p.Name.ToLowerInvariant();
                        if (pn.Contains("coordinate")) { try { var v = p.GetValue(holder, null); if (v != null) { return v; } } catch { } }
                    }
                }
            }
            catch (Exception ex) { Log("action: coordinate search failed " + ex.Message); }
            return null;
        }

        /// <summary>
        /// Find the live ActionManager. GameManager exposes the per-frame client in
        /// `client` / `fakeSession` (both ClientBase, not Unity objects), and ClientBase.ActionManager
        /// is the manager the game itself drives 闂?commands must go through that one to take effect.
        /// </summary>
        internal static bool IsUnitActorAction(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "move": case "attack": case "capture": case "disband":
                case "disembark": case "upgrade": case "upgradeunit": case "upgrade_unit": case "promote": case "veteran":
                case "recover": case "heal_self": case "heal_others": case "boost": case "explode":
                    return true;
                default: return false;
            }
        }

        private object FindActionManager(object gmInstance, bool trace = false)
        {
            if (gmInstance == null) { return null; }
            if (_fixedControlledSeat.HasValue)
            {
                var mainClient = MemberWithAccessor(gmInstance, "client");
                // A missing main manager is unknown; fake/replay managers cannot stand in for it.
                return MemberWithAccessor(mainClient, "ActionManager");
            }

            // 1) the game's real client (ClientBase) -> its ActionManager property
            foreach (var fieldName in new[] { "client", "fakeSession", "localClient", "remoteClient", "replayClient" })
            {
                var client = InstanceField(gmInstance, fieldName);
                if (trace) Log("action: GameManager." + fieldName + " = " + (client == null ? "null" : client.GetType().Name));
                if (client == null) { continue; }
                var am = InstanceField(client, "ActionManager", "actionManager");
                if (am != null)
                {
                    if (trace) Log("action: live ActionManager via " + fieldName + "." + am.GetType().Name);
                    return am;
                }
            }

            // 2) GameManager may carry a whole-game action manager directly
            var direct = InstanceField(gmInstance, "actionManager", "ActionManager", "gameActionManager");
            if (direct != null) { if (trace) Log("action: live ActionManager via GameManager field"); return direct; }

            if (trace) Log("action: no live ActionManager found; caller may fall back");
            return null;
        }

        /// <summary>FindObjectsOfType via UnityEngine.Object, with the il2cpp Type conversion.</summary>
        private List<object> FindObjectsByTypeName(Type t)
        {
            var result = new List<object>();
            try
            {
                var objType = FindType("UnityEngine.Object");
                var il2cppType = Il2CppTypeOf(t);
                if (objType == null || il2cppType == null) { return result; }
                foreach (var holder in new[] { FindType("UnityEngine.Resources"), objType })
                {
                    if (holder == null) { continue; }
                    foreach (var m in holder.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        var ps = m.GetParameters();
                        if ((m.Name == "FindObjectsOfTypeAll" || m.Name == "FindObjectsOfType")
                            && ps.Length == 1 && ps[0].ParameterType.Name == "Type")
                        {
                            var raw = m.Invoke(null, new object[] { il2cppType });
                            var en = raw as IEnumerable;
                            if (en != null) { foreach (var o in en) { if (o != null) { result.Add(o); } } }
                            Log("action: " + m.Name + "(" + t.Name + ") -> " + result.Count + " objects");
                            return result;
                        }
                    }
                }
            }
            catch (Exception ex) { Log("action: FindObjectsByTypeName failed " + (ex.InnerException ?? ex).Message); }
            return result;
        }

        private static object StaticMember(Type t, params string[] names)
        {
            return NativeAliasedMember(null, t, true, names);
        }

        private static object InstanceField(object instance, params string[] names)
        {
            return NativeAliasedMember(instance, instance == null ? null : instance.GetType(), false, names);
        }

        /// <summary>Dump the live GameManager instance members so the action-manager holder is findable.</summary>
        private string GameManagerFields()
        {
            var gm = _gameManagerType != null ? SafeInstance() : null;
            if (gm == null) { return "{\"ok\":false,\"error\":\"GameManager.instance is null\"}"; }
            var sb = new StringBuilder("{\"ok\":true,\"type\":\"").Append(Esc(gm.GetType().Name)).Append("\",\"fields\":[");
            bool first = true;
            foreach (var f in gm.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                object v = null;
                try { v = f.GetValue(gm); } catch { }
                if (!first) { sb.Append(','); }
                first = false;
                sb.Append("{\"name\":\"").Append(Esc(f.Name)).Append('"')
                  .Append(",\"type\":\"").Append(Esc(f.FieldType.Name)).Append('"')
                  .Append(",\"null\":").Append(v == null ? "true" : "false");
                if (v != null && v.GetType().Name.IndexOf("Manager", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sb.Append(",\"valueType\":\"").Append(Esc(v.GetType().Name)).Append('"');
                }
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Locate the game's real ClientBase (which owns the per-frame ClientActionManager):
        /// search loaded assets/managers for anything exposing an ActionManager property.
        /// </summary>
        private string ClientProbe()
        {
            var sb = new StringBuilder("{\"ok\":true,\"candidates\":[");
            bool first = true;
            var clientBaseType = FindType("ClientBase") ?? FindType("Il2Cpp.ClientBase");
            sb.Append("{\"clientBaseType\":\"").Append(Esc(clientBaseType == null ? "null" : clientBaseType.FullName)).Append("\"}");
            first = false;

            // scan every loaded assembly type for an ActionManager member, then look for live instances
            var holders = new List<string>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    try
                    {
                        if (t.GetProperty("ActionManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null
                            || t.GetField("actionManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null)
                        {
                            holders.Add(t.FullName);
                        }
                    }
                    catch { }
                }
            }
            if (!first) { sb.Append(','); }
            sb.Append("\"typesWithActionManager\":[")
              .Append(string.Join(",", Array.ConvertAll(holders.ToArray(), x => "\"" + Esc(x) + "\"")))
              .Append(']');

            // try to pull a live instance out of the managers the game exposes
            foreach (var holderName in new[] { "Il2Cpp.ClientBase", "ClientBase", "Il2Cpp.GameManager", "GameManager" })
            {
                var ht = FindType(holderName);
                if (ht == null) { continue; }
                foreach (var o in FindObjectsByTypeName(ht))
                {
                    var am = InstanceField(o, "ActionManager", "actionManager");
                    if (am != null)
                    {
                        sb.Append(",\"foundOn\":\"").Append(Esc(ht.FullName)).Append("\",\"managerType\":\"")
                          .Append(Esc(am.GetType().Name)).Append('"');
                        sb.Append(']');
                        sb.Append('}');
                        return sb.ToString();
                    }
                }
            }
            sb.Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Dump GameState.pendingCommandTriggers (the queue that holds, among others, the
        /// CityReward trigger whose coordinates CityRewardCommand needs).
        /// </summary>
        private const int MaxTriggers = 8;

        /// <summary>
        /// Read GameState.pendingCommandTriggers (a List&lt;CommandTrigger&gt;) and report the
        /// whitelisted members of each element. Evidence-based field access only:
        /// CommandTrigger.cs in the Cpp2IL dump declares exactly four public fields
        /// (playerId, opponentId, type, coordinates) and no properties. The generated interop also
        /// exposes NativeFieldInfoPtr_&lt;name&gt; plus PascalCase getters, so both spellings are tried,
        /// and anything else is reported as unsupported rather than reflected generically.
        /// </summary>
        private string TriggerDump()
        {
            const string triggerTypeName = "CommandTrigger";
            var gs = GameStateInstance();
            if (gs == null) { return RenderTriggers(false, "no active game", null, null, new List<string>(), false, MaxTriggers); }

            string phase = "lookup";
            object fieldRaw = null;
            string rawType = null;
            try
            {
                var f = _gameStateType.GetField("pendingCommandTriggers",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null)
                {
                    Log("triggers: phase=" + phase + " field pendingCommandTriggers not found as FieldInfo; falling back to property");
                    var p = _gameStateType.GetProperty("pendingCommandTriggers",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p == null)
                    {
                        return RenderTriggers(false, "pendingCommandTriggers not exposed on GameState", null, null,
                            new List<string>(), false, MaxTriggers);
                    }
                    fieldRaw = p.GetValue(gs, null);
                }
                else
                {
                    fieldRaw = f.GetValue(gs);
                }
                rawType = fieldRaw == null ? "null" : fieldRaw.GetType().FullName;
                Log("triggers: phase=" + phase + " raw=" + rawType);
                if (fieldRaw == null)
                {
                    return RenderTriggers(true, null, "null", null, new List<string>(), false, MaxTriggers);
                }

                phase = "enumerate";
                var list = AsEnumerable(fieldRaw);
                if (list == null)
                {
                    return RenderTriggers(false, "pendingCommandTriggers is not enumerable (unsupported)", rawType, null,
                        new List<string>(), false, MaxTriggers);
                }

                phase = "read";
                var frames = new List<string>();
                int total = 0;
                bool truncated = false;
                foreach (var trig in list)
                {
                    if (trig == null) { continue; }
                    if (total >= MaxTriggers) { truncated = true; break; }
                    total++;

                    string tn = trig.GetType().Name;
                    bool supported = tn.IndexOf(triggerTypeName, StringComparison.OrdinalIgnoreCase) >= 0;
                    var vals = new Dictionary<string, string>();
                    bool anyRead = false;
                    if (supported)
                    {
                        foreach (var member in new[] { "playerId", "opponentId", "type", "coordinates" })
                        {
                            object v = MemberWithAccessor(trig, member);
                            if (v == null) { continue; }
                            vals[member] = SafeScalar(v);
                            anyRead = true;
                        }
                    }
                    frames.Add(RenderTrigger(supported, tn, vals, anyRead));
                }
                Log("triggers: phase=read elements=" + total + " truncated=" + truncated);
                return RenderTriggers(true, null, rawType, triggerTypeName, frames, truncated, MaxTriggers);
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Log("triggers: FAILED at phase=" + phase + " " + inner.GetType().Name + ": " + inner.Message);
                return RenderTriggers(false, "phase " + phase + ": " + inner.GetType().Name + ": " + inner.Message,
                    rawType, null, new List<string>(), false, MaxTriggers);
            }
        }

        /// <summary>
        /// Try explicit accessor spellings only (never a generic member sweep, never il2cpp
        /// metadata pointers). Confirmed shape of CommandTrigger (Cpp2IL dump): public fields
        /// playerId/opponentId/type/coordinates, zero properties; the generated interop also
        /// exposes PascalCase getters, so both are attempted.
        /// Note: NativeFieldInfoPtr_* members are il2cpp metadata pointers, NOT data, so they are
        /// deliberately not consulted.
        /// </summary>
        private static object MemberWithAccessor(object o, string clrName)
        {
            object value;
            return TryReadNativeMember(o, clrName, out value) ? value : null;
        }

        private static object UncachedMemberWithAccessor(object o, string clrName)
        {
            if (o == null || string.IsNullOrEmpty(clrName)) { return null; }
            var t = o.GetType();
            var pascal = char.ToUpperInvariant(clrName[0]) + clrName.Substring(1);
            foreach (var prop in new[] { "get_" + pascal, "get_" + clrName })
            {
                var m = t.GetMethod(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m != null && m.GetParameters().Length == 0)
                {
                    try { return m.Invoke(o, null); } catch { }
                }
            }
            foreach (var name in new[] { clrName, pascal })
            {
                var fld = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fld != null)
                {
                    try { return fld.GetValue(o); } catch { }
                }
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.GetIndexParameters().Length == 0)
                {
                    try { return p.GetValue(o, null); } catch { }
                }
            }
            return null;
        }

        /// <summary>
        /// Render only provably-safe scalars. Coordinates are accepted only when the value's type
        /// is exactly WorldCoordinates (verified struct: private int x/y with public X/Y getters)
        /// and BOTH components are readable - a partial read yields null, never a fake "(,)".
        /// </summary>
        private static string SafeScalar(object v)
        {
            if (v == null) { return null; }
            var t = v.GetType();
            if (t.IsPrimitive || t.IsEnum || t == typeof(string)) { return v.ToString(); }
            if (t.Name == "WorldCoordinates" || t.FullName == "WorldCoordinates" || t.FullName == "Il2Cpp.WorldCoordinates")
            {
                return RenderCoordinates(
                    SafeScalar(MemberWithAccessor(v, "x")),
                    SafeScalar(MemberWithAccessor(v, "y")));
            }
            return null;   // unsupported value type: omit rather than ToString() an interop object
        }

        /// <summary>
        /// Format a coordinate pair, or null when either component is unreadable. Exposed so the
        /// "no fake coordinates" rule is verifiable offline without fabricating a private struct.
        /// </summary>
        internal static string RenderCoordinates(string x, string y)
        {
            if (x == null || y == null) { return null; }
            if (x.Length == 0 || y.Length == 0) { return null; }
            return "(" + x + "," + y + ")";
        }

        /// <summary>
        /// Read-only button scan for diagnostics: first `cap` buttons with name/label/active,
        /// plus the real total (enumerated fully, but members are only read for the first `cap`).
        /// Never invokes anything.
        /// </summary>
        private string ButtonScan(string capArg, string filter)
        {
            int cap = 40;
            int ci;
            if (int.TryParse(capArg, out ci) && ci > 0 && ci <= 500) { cap = ci; }
            var t = FindType("UnityEngine.UI.Button");
            var objType = FindType("UnityEngine.Object");
            if (t == null || objType == null) { return "{\"ok\":false,\"error\":\"uGUI Button not found\"}"; }
            var il2cppType = Il2CppTypeOf(t);
            if (il2cppType == null) { return "{\"ok\":false,\"error\":\"Il2CppType conversion failed\"}"; }

            MethodInfo find = null;
            foreach (var holder in new[] { FindType("UnityEngine.Resources"), objType })
            {
                if (holder == null) { continue; }
                foreach (var m in holder.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    var ps = m.GetParameters();
                    if ((m.Name == "FindObjectsOfTypeAll" || m.Name == "FindObjectsOfType")
                        && ps.Length == 1 && ps[0].ParameterType.Name == "Type") { find = m; break; }
                }
                if (find != null) { break; }
            }
            if (find == null) { return "{\"ok\":false,\"error\":\"no FindObjectsOfTypeAll(Type)\"}"; }

            int total = 0;
            var o = new JsonObj();
            o.Bool("ok", true);
            o.Bool("readOnly", true);
            o.Raw("cap", cap.ToString(CultureInfo.InvariantCulture));
            if (filter.Length > 0) { o.Str("filter", filter); }
            var arr = new JsonArr();
            var activeCounts = new Dictionary<string, int>();
            try
            {
                var raw = find.Invoke(null, new object[] { il2cppType });
                var en = raw as IEnumerable;
                if (en != null)
                {
                    foreach (var b in en)
                    {
                        if (b == null) { continue; }
                        total++;
                        if (total > cap) { continue; }        // still counting, no member reads
                        var info = SnapshotButton(b);
                        string key = info.Active ?? "unknown";
                        int c;
                        activeCounts.TryGetValue(key, out c);
                        activeCounts[key] = c + 1;
                        string nm = info.Name + (info.Label.Length > 0 ? "/" + info.Label : "");
                        if (filter.Length > 0 && nm.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                        var bo = new JsonObj();
                        bo.Str("name", info.Name);
                        if (info.Label.Length > 0) { bo.Str("label", info.Label); }
                        bo.Str("active", info.Active);
                        if (!string.IsNullOrEmpty(info.Pos)) { bo.Str("pos", info.Pos); }
                        arr.Raw(bo.Close());
                    }
                }
            }
            catch (Exception ex)
            {
                return "{\"ok\":false,\"error\":\"scan failed: " + Esc((ex.InnerException ?? ex).Message) + "\"}";
            }
            o.Raw("total", total.ToString(CultureInfo.InvariantCulture));
            o.Bool("truncated", total > cap);
            var counts = new JsonObj();
            foreach (var kv in activeCounts) { counts.Raw(kv.Key, kv.Value.ToString(CultureInfo.InvariantCulture)); }
            o.Nested("activeCounts", counts);
            o.Raw("buttons", arr.Close());
            return o.Close();
        }

        /// <summary>
        /// Invoke the single button nearest to a screen coordinate. This does not depend on the
        /// object name (which the interop exposes poorly) nor on activeInHierarchy (which does not
        /// read through reliably). Safety: refuses when zero candidates are within tolerance or
        /// when more than one is, and reports every candidate considered.
        /// </summary>
        private string UiClickAt(string xs, string ys, string tols)
        {
            float tx, ty, tol = 60f;
            if (!float.TryParse(xs, NumberStyles.Float, CultureInfo.InvariantCulture, out tx)) { return "{\"ok\":false,\"error\":\"x required\"}"; }
            if (!float.TryParse(ys, NumberStyles.Float, CultureInfo.InvariantCulture, out ty)) { return "{\"ok\":false,\"error\":\"y required\"}"; }
            float tmp;
            if (float.TryParse(tols, NumberStyles.Float, CultureInfo.InvariantCulture, out tmp) && tmp > 0) { tol = tmp; }

            var t = FindType("UnityEngine.UI.Button");
            var objType = FindType("UnityEngine.Object");
            if (t == null || objType == null) { return "{\"ok\":false,\"error\":\"uGUI Button not found\"}"; }
            var il2cppType = Il2CppTypeOf(t);
            if (il2cppType == null) { return "{\"ok\":false,\"error\":\"Il2CppType conversion failed\"}"; }
            MethodInfo find = null;
            foreach (var holder in new[] { FindType("UnityEngine.Resources"), objType })
            {
                if (holder == null) { continue; }
                foreach (var m in holder.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    var ps = m.GetParameters();
                    if ((m.Name == "FindObjectsOfTypeAll" || m.Name == "FindObjectsOfType")
                        && ps.Length == 1 && ps[0].ParameterType.Name == "Type") { find = m; break; }
                }
                if (find != null) { break; }
            }
            if (find == null) { return "{\"ok\":false,\"error\":\"no FindObjectsOfTypeAll(Type)\"}"; }

            var candidates = new List<object>();
            var candNames = new List<string>();
            var candDist = new List<float>();
            try
            {
                var raw = find.Invoke(null, new object[] { il2cppType });
                var en = raw as IEnumerable;
                if (en != null)
                {
                    foreach (var b in en)
                    {
                        if (b == null) { continue; }
                        var tr = MemberWithAccessor(b, "transform");
                        var pos = tr == null ? null : MemberWithAccessor(tr, "position");
                        if (pos == null) { continue; }
                        // transform.position is WORLD space; project to screen pixels before comparing
                        object screen = WorldToScreen(pos);
                        float px, py;
                        if (!TryFloat(screen != null ? MemberWithAccessor(screen, "x") : MemberWithAccessor(pos, "x"), out px)) { continue; }
                        if (!TryFloat(screen != null ? MemberWithAccessor(screen, "y") : MemberWithAccessor(pos, "y"), out py)) { continue; }
                        float d = (float)Math.Sqrt((px - tx) * (px - tx) + (py - ty) * (py - ty));
                        if (d > tol) { continue; }
                        candidates.Add(b);
                        candNames.Add(ObjectName(b));
                        candDist.Add(d);
                    }
                }
            }
            catch (Exception ex) { return "{\"ok\":false,\"error\":\"scan failed: " + Esc((ex.InnerException ?? ex).Message) + "\"}"; }

            Log("uiat: (" + tx + "," + ty + ") tol=" + tol + " candidates=" + candidates.Count);
            var o = new JsonObj();
            o.Raw("target", "[" + tx.ToString(CultureInfo.InvariantCulture) + "," + ty.ToString(CultureInfo.InvariantCulture) + "]");
            o.Raw("tolerance", tol.ToString(CultureInfo.InvariantCulture));
            var arr = new JsonArr();
            for (int i = 0; i < candidates.Count; i++)
            {
                var co = new JsonObj();
                co.Str("name", candNames[i]);
                co.Raw("distance", candDist[i].ToString("0.##", CultureInfo.InvariantCulture));
                arr.Raw(co.Close());
            }
            o.Raw("candidates", arr.Close());

            if (candidates.Count == 0)
            {
                o.Bool("ok", false); o.Str("error", "no button within tolerance");
                return o.Close();
            }
            if (candidates.Count > 1)
            {
                o.Bool("ok", false); o.Str("error", "ambiguous: " + candidates.Count + " buttons within tolerance");
                return o.Close();
            }

            try
            {
                var press = t.GetMethod("Press");
                if (press != null) { press.Invoke(candidates[0], null); }
                else
                {
                    var onClick = Member(candidates[0], "onClick");
                    var invoke = onClick != null ? onClick.GetType().GetMethod("Invoke") : null;
                    if (invoke == null) { throw new MissingMethodException("no Press()/onClick.Invoke()"); }
                    invoke.Invoke(onClick, null);
                }
                o.Bool("ok", true);
                o.Str("invoked", candNames[0]);
                Log("uiat: invoked '" + candNames[0] + "'");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                o.Bool("ok", false);
                o.Str("invokeError", inner.GetType().Name + ": " + inner.Message);
            }
            return o.Close();
        }

        /// <summary>Project a world-space position to screen pixels via Camera.main.WorldToScreenPoint.</summary>
        private static object WorldToScreen(object worldPos)
        {
            try
            {
                var cam = FindType("UnityEngine.Camera");
                if (cam == null) { return null; }
                MethodInfo main = null;
                foreach (var m in cam.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "get_main" || m.Name == "get_current") { main = m; break; }
                }
                object camera = main != null ? main.Invoke(null, null) : null;
                if (camera == null) { return null; }
                foreach (var m in cam.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "WorldToScreenPoint") { continue; }
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType.Name == "Vector3")
                    {
                        return m.Invoke(camera, new[] { worldPos });
                    }
                }
            }
            catch (Exception ex) { Log("WorldToScreen failed: " + (ex.InnerException ?? ex).Message); }
            return null;
        }

        /// <summary>
        /// Popup control through PopupManager (all static, verified in PopupManager.cs):
        ///   {"cmd":"popup"}                     -> state: PopupShowing / IsPopupShowing(id) / current popup type
        ///   {"cmd":"popup","op":"hide"}         -> PopupManager.HideCurrentPopup()
        ///   {"cmd":"popup","op":"closeall"}     -> PopupManager.RemoveAllPopups()
        ///   {"cmd":"popup","op":"back"}         -> PopupManager.OnBackButtonClicked()
        ///   {"cmd":"popup","op":"unskippable"}  -> IsUnskippablePopupShowing()
        /// </summary>
        private string PopupCommand(string op, string id, string buttonIndex, string expectedReward = "")
        {
            var t = FindType("PopupManager") ?? FindType("Il2Cpp.PopupManager");
            if (t == null)
                return op.Length == 0 || op == "state" ? "{\"ok\":false,\"error\":\"PopupManager not found\"}"
                    : UiPreparationError("POPUP_MANAGER_UNAVAILABLE", "native popup manager unavailable");
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            if (op.Length == 0 || op == "state")
            {
                var o = new JsonObj();
                o.Bool("ok", true);
                object showing = InvokeStatic(t, "get_PopupShowing");
                o.NullOrStr("popupShowing", showing == null ? null : showing.ToString());
                object instance = StaticMember(t, "instance");
                o.NullOrStr("instance", instance == null ? null : instance.GetType().Name);
                object current = InvokeStatic(t, "GetCurrentPopup");
                o.NullOrStr("currentPopup", current == null ? null : current.GetType().Name);
                if (id.Length > 0)
                {
                    object isShowing = InvokeStatic(t, "IsPopupShowing", new object[] { id });
                    o.NullOrStr("isPopupShowing:" + id, isShowing == null ? null : isShowing.ToString());
                }
                object unskippable = InvokeStatic(t, "IsUnskippablePopupShowing");
                o.NullOrStr("unskippable", unskippable == null ? null : unskippable.ToString());
                if (current != null)
                {
                    var labels = new JsonArr();
                    var btns = MemberWithAccessor(current, "buttons");
                    var list = btns == null ? null : AsEnumerable(btns);
                    if (list != null)
                    {
                        int n = 0;
                        foreach (var b in list)
                        {
                            if (b == null || n >= 8) { break; }
                            n++;
                            var lb = new JsonObj();
                            lb.Str("name", ObjectName(b));
                            lb.Str("label", ReadLabel(b));
                            lb.Str("active", ReadActive(b));
                            labels.Raw(lb.Close());
                        }
                    }
                    o.Raw("currentButtons", labels.Close());
                }
                return o.Close();
            }

            switch (op)
            {
                case "hide": return InvokePopupAction(t, "HideCurrentPopup");
                case "closeall": return InvokePopupAction(t, "RemoveAllPopups");
                case "back": return InvokePopupAction(t, "OnBackButtonClicked");
                case "buttons": return PopupButtons();
                case "next": return StepCommandTrigger();
                case "shape": return PopupShape();
                case "native": return PopupNative();
                case "components": return PopupComponents();
                case "resolve": return PopupResolve();
                case "tree": return PopupTree();
                case "click": return PopupClick(buttonIndex);
                case "buttonlist": return PopupButtonList();
                case "choose": return PopupChoose(buttonIndex);
                case "dismiss_resolved_reward": return DismissResolvedReward(expectedReward);
                case "replay":
                {
                    // The game's own programmatic button trigger:
                    //   PopupBase.ReplayClickButton(int buttonIndex, Action onHideCallback, float delay)
                    // Returns the chosen PopupButtonData (id/text/callback/closesPopup) so the
                    // caller can verify which interface path was actually taken.
                    object replayPopup = t == null ? null : InvokeStatic(t, "GetCurrentPopup");
                    if (replayPopup == null) { return "{\"ok\":false,\"error\":\"no current popup\"}"; }
                    replayPopup = BestPopupComponent(replayPopup);
                    int idx;
                    if (buttonIndex.Length == 0 || !int.TryParse(buttonIndex, out idx))
                    {
                        return "{\"ok\":false,\"error\":\"popup op=replay requires \\\"index\\\" (int)\"}";
                    }
                    return ReplayClick(replayPopup, idx);
                }
                default: return "{\"ok\":false,\"error\":\"unknown op: " + Esc(op)
                    + "\",\"known\":[\"state\",\"hide\",\"closeall\",\"back\",\"buttons\",\"shape\",\"replay\",\"next\"]"
                    + ",\"note\":\"popup choices are made through the game interface (action peace_response / action city_reward, or popup op=replay); never by force-closing\"}";
            }
        }

        /// <summary>Whether PopupManager reports a popup right now (used to see which step closed it).</summary>
        private static bool PopupShowingNow(Type popupManagerType)
        {
            try
            {
                var v = InvokeStatic(popupManagerType, "get_PopupShowing");
                return v != null && v.ToString().IndexOf("true", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return true; }
        }

        /// <summary>
        /// Drive the game's own PopupTester component, which exists purely to spawn real
        /// popups for manual testing (its methods carry [Button] attributes in the editor).
        /// Used ONLY to obtain a genuine popup so the explicit-choice path can be verified
        /// against real game popups. Returns the popup state before and after, so the caller
        /// can see exactly what the game did.
        /// </summary>
        private string PopupTest(string name, string action)
        {
            var o = new JsonObj();
            if (name.Length == 0)
            {
                o.Bool("ok", false);
                o.Str("error", "popup_test requires \"name\"");
                o.Raw("known", "[\"basic\",\"reward\",\"unit\",\"icon\",\"tribe\",\"tech\",\"two\",\"close\"]");
                return o.Close();
            }

            var testerType = FindType("PopupTester") ?? FindType("Il2Cpp.PopupTester");
            if (testerType == null) { return "{\"ok\":false,\"error\":\"PopupTester not found\"}"; }
            var goType = FindType("UnityEngine.GameObject");
            if (goType == null) { return "{\"ok\":false,\"error\":\"UnityEngine.GameObject not found\"}"; }

            // Reach the live game object graph through the GameManager instance.
            var gm = SafeInstance();
            o.Str("gameManager", gm == null ? "null" : gm.GetType().Name);

            object tester = FindOrCreateTester(testerType, goType);
            if (tester == null) { return "{\"ok\":false,\"error\":\"could not obtain a PopupTester instance\"}"; }

            string method;
            switch (name.ToLowerInvariant())
            {
                case "basic": method = "OnTestBasicPopup"; break;
                case "reward": method = "OnTestRewardPopup"; break;
                case "unit": method = "OnTestUnitPopup"; break;
                case "icon": method = "OnTestIconPopup"; break;
                case "tribe": method = "OnTestSelectTribePopup"; break;
                case "tech": method = "OnTestTechPopup"; break;
                case "two": method = "OnTestTwoPopups"; break;
                default: return "{\"ok\":false,\"error\":\"unknown popup_test name: " + Esc(name) + "\"}";
            }

            o.Bool("ok", false);
            o.Str("tester", tester.GetType().Name);
            o.Str("invoked", method);
            o.Raw("before", PopupStateJson());
            try
            {
                var m = tester.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m == null) { o.Str("error", method + " not found"); return o.Close(); }
                m.Invoke(tester, null);
                o.Bool("ok", true);
                Log("popup_test: invoked PopupTester." + method);
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                o.Str("error", inner.GetType().Name + ": " + inner.Message);
                Log("popup_test failed: " + inner.Message);
            }
            o.Raw("after", PopupStateJson());
            o.Raw("shape", PopupShape());
            return o.Close();
        }

        private object FindOrCreateTester(Type testerType, Type goType)
        {
            object il2 = Il2CppTypeOf(testerType);
            // 1) any PopupTester already in the scene (FindObjectsOfType with the il2cpp Type)
            try
            {
                foreach (var m in goType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "FindObjectsOfType") { continue; }
                    var ps = m.GetParameters();
                    if (ps.Length != 1) { continue; }
                    try
                    {
                        var found = m.Invoke(null, new[] { il2 });
                        var list = found == null ? null : AsEnumerable(found);
                        if (list != null) { foreach (var t in list) { if (t != null) { return t; } } }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log("popup_test: scene scan failed " + (ex.InnerException ?? ex).Message); }

            // 2) attach a fresh one (the component is stateless; its methods just spawn popups)
            object go;
            try
            {
                var ctor = goType.GetConstructor(new[] { typeof(string) });
                go = ctor != null ? ctor.Invoke(new object[] { "BridgePopupTester" })
                                  : Activator.CreateInstance(goType);
                if (go == null) { return null; }
            }
            catch (Exception ex) { Log("popup_test: GameObject ctor failed " + (ex.InnerException ?? ex).Message); return null; }

            // AddComponent<T>() is generic: bind it with the tester type so il2cpp returns a
            // really-typed PopupTester (the non-generic overload hands back a bare Component).
            foreach (var cand in goType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (cand.Name != "AddComponent") { continue; }
                MethodInfo use = cand;
                try
                {
                    if (cand.IsGenericMethodDefinition)
                    {
                        var gps = cand.GetGenericArguments();
                        if (gps.Length != 1) { continue; }
                        use = cand.MakeGenericMethod(new[] { testerType });
                        return use.Invoke(go, null);
                    }
                }
                catch (Exception ex) { Log("popup_test: AddComponent<T> failed " + (ex.InnerException ?? ex).Message); }
            }
            return null;
        }

        /// <summary>
        /// Enumerate every MonoBehaviour actually attached to the current popup's GameObject.
        ///
        /// PopupManager.GetCurrentPopup() hands back a PopupBase-typed wrapper, which hides the
        /// concrete subclass and therefore its buttonData/Buttons/ReplayClickButton. The
        /// components on the object itself are the real ones (the same GetComponents approach
        /// that already reads real component types elsewhere in this mod), so this is how the
        /// true popup shape is obtained without native pointer arithmetic.
        /// </summary>
        private string PopupComponents()
        {
            var t = FindType("PopupManager") ?? FindType("Il2Cpp.PopupManager");
            if (t == null) { return "{\"ok\":false,\"error\":\"PopupManager not found\"}"; }
            object current = InvokeStatic(t, "GetCurrentPopup");
            if (current == null) { return "{\"ok\":false,\"error\":\"no current popup\"}"; }

            var o = new JsonObj();
            o.Bool("ok", true);
            o.Str("wrapperType", current.GetType().FullName);

            var go = MemberWithAccessor(current, "gameObject");
            o.NullOrStr("gameObject", go == null ? null : ObjectName(go));
            if (go == null) { o.Str("error", "popup has no gameObject"); return o.Close(); }

            var arr = new JsonArr();
            object comps = GetComponentList(go);
            var list = comps == null ? null : AsEnumerable(comps);
            int n = 0;
            if (list != null)
            {
                foreach (var c in list)
                {
                    if (c == null || n >= 24) { continue; }
                    n++;
                    var co = new JsonObj();
                    co.Str("type", c.GetType().FullName);
                    co.Bool("isPopupBase", MemberWithAccessor(c, "IsUnskippable") != null);
                    // button-bearing members, read from the component itself
                    var bd = MemberWithAccessor(c, "buttonData");
                    co.Bool("hasButtonData", bd != null);
                    var bdList = bd == null ? null : AsEnumerable(bd);
                    if (bdList != null)
                    {
                        var items = new JsonArr();
                        foreach (var one in bdList) { if (one == null) { continue; } items.Raw(ButtonDataJson(one).Close()); }
                        co.Raw("buttonData", items.Close());
                    }
                    var bts = MemberWithAccessor(c, "Buttons");
                    var btList = bts == null ? null : AsEnumerable(bts);
                    if (btList != null)
                    {
                        var items = new JsonArr();
                        foreach (var one in btList)
                        {
                            if (one == null) { continue; }
                            var io = new JsonObj();
                            io.Str("type", one.GetType().Name);
                            io.Str("label", ReadLabel(one));
                            io.Str("active", ReadActive(one));
                            items.Raw(io.Close());
                        }
                        co.Raw("buttons", items.Close());
                    }
                    arr.Raw(co.Close());
                }
            }
            o.Raw("componentCount", n.ToString(CultureInfo.InvariantCulture));
            o.Raw("components", arr.Close());
            return o.Close();
        }

        /// <summary>
        /// Every component on a GameObject. GetComponents is generic in the il2cpp binding, so
        /// this tries the concrete overloads, then closes the generic ones over MonoBehaviour
        /// and over the popup base type, before giving up.
        /// </summary>
        private object GetComponentList(object go)
        {
            var gt = go.GetType();
            var mono = FindType("UnityEngine.MonoBehaviour");
            var popupBase = FindType("PopupBase") ?? FindType("Il2Cpp.PopupBase");

            foreach (var m in gt.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name != "GetComponents" && m.Name != "GetComponentsInChildren") { continue; }
                try
                {
                    if (m.IsGenericMethodDefinition)
                    {
                        if (m.GetGenericArguments().Length != 1) { continue; }
                        foreach (var argType in new[] { mono, popupBase })
                        {
                            if (argType == null) { continue; }
                            object il2 = Il2CppTypeOf(argType);
                            if (il2 == null) { continue; }
                            try
                            {
                                var closed = m.MakeGenericMethod(new[] { argType });
                                var res = closed.Invoke(go, null);
                                if (res != null) { return res; }
                            }
                            catch { }
                        }
                        continue;
                    }
                    if (m.GetParameters().Length == 0)
                    {
                        var res = m.Invoke(go, null);
                        if (res != null) { return res; }
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Spawn a real popup through PopupManager's own factories. These are the API the game
        /// itself calls, so the popup produced is genuine - this is how the explicit-choice path
        /// gets verified without needing to reach a particular point in a match.
        /// Read/write: it does open a popup, so callers should follow with popup op=tree/choose.
        /// </summary>
        private string PopupMake(string kind)
        {
            var t = FindType("PopupManager") ?? FindType("Il2Cpp.PopupManager");
            if (t == null) { return "{\"ok\":false,\"error\":\"PopupManager not found\"}"; }
            var inst = StaticMember(t, "instance");
            if (inst == null) { return "{\"ok\":false,\"error\":\"PopupManager.instance is null\"}"; }
            if (kind.Length == 0) { kind = "basic"; }

            var o = new JsonObj();
            o.Bool("ok", false);
            o.Str("kind", kind);
            o.Raw("before", PopupStateJson());

            string factory;
            switch (kind.ToLowerInvariant())
            {
                case "basic": factory = "GetBasicPopup"; break;
                case "basic_ok": factory = "GetBasicPopupWithData"; break;
                case "icon": factory = "GetIconPopup"; break;
                case "reward": factory = "GetRewardPopup"; break;
                case "unit": factory = "GetUnitPopup"; break;
                case "tech": factory = "GetTechPopup"; break;
                case "blocking": factory = "GetBlockingPopup"; break;
                case "tribe": factory = "GetSelectTribePopup"; break;
                default: return "{\"ok\":false,\"error\":\"unknown kind: " + Esc(kind) + "\"}";
            }

            object popup = null;
            try
            {
                if (factory == "GetBlockingPopup")
                {
                    popup = InvokeStatic(t, factory, new object[] { "bridge-test", "Bridge test", "Spawned by the agent bridge for interface verification." });
                }
                else if (factory == "GetBasicPopupWithData")
                {
                    // Build it through the popup's own public setters: SetHeader/SetDescription
                    // then SetDefaultOkbutton, which is the game's own way to create the OK
                    // button (with its real PopupButtonData and closesPopup=true).
                    popup = InvokeStatic(t, factory == "GetBasicPopupWithData" ? "GetBasicPopup" : factory);
                    if (popup != null)
                    {
                        InvokeOn(popup, "SetHeader", new object[] { "Bridge test" });
                        InvokeOn(popup, "SetDescription", new object[] { "Spawned by the agent bridge to verify the explicit-choice path." });
                        object btn = InvokeOn(popup, "SetDefaultOkbutton", new object[] { null });
                        o.NullOrStr("okButton", btn == null ? null : btn.GetType().Name);
                        Log("popup_make: SetDefaultOkbutton -> " + (btn == null ? "null" : btn.GetType().Name));
                    }
                }
                else
                {
                    popup = InvokeStatic(t, factory);
                }
                o.NullOrStr("popup", popup == null ? null : popup.GetType().Name);
                o.Bool("ok", popup != null);
                if (popup == null) { o.Str("error", factory + "() returned null"); }
                else { try { InvokeOn(popup, "Show", null); } catch (Exception ex) { o.Str("showError", ex.Message); } }
                Log("popup_make: " + factory + " -> " + (popup == null ? "null" : popup.GetType().Name));
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                o.Str("error", inner.GetType().Name + ": " + inner.Message);
                Log("popup_make failed: " + inner.Message);
            }

            if (popup != null) { o.Raw("tree", PopupTree()); }
            o.Raw("after", PopupStateJson());
            return o.Close();
        }

        /// <summary>
        /// Build a BasicPopupData with one real OK button and hand it to the game's own
        /// GetBasicPopupWithData factory, so the resulting popup carries an actual
        /// PopupButtonData (with its own callback and closesPopup flag) to act on.
        /// </summary>
        private object MakeBasicPopupWithOk(Type popupManagerType, string factory)
        {
            var dataType = FindType("PopupManager+BasicPopupData") ?? FindType("BasicPopupData")
                ?? FindType("Il2Cpp.PopupManager+BasicPopupData") ?? FindType("Il2Cpp.BasicPopupData");
            if (dataType == null) { Log("popup_make: BasicPopupData type not found"); return null; }

            // PopupButtonData(string text, States state, Action callback, int id, bool closesPopup, ...)
            var btnType = FindType("PopupBase+PopupButtonData") ?? FindType("PopupButtonData")
                ?? FindType("Il2Cpp.PopupBase+PopupButtonData") ?? FindType("Il2Cpp.PopupButtonData");
            object btnData = null;
            if (btnType != null)
            {
                foreach (var ctor in btnType.GetConstructors())
                {
                    var ps = ctor.GetParameters();
                    try
                    {
                        if (ps.Length >= 5)
                        {
                            object[] a = new object[ps.Length];
                            for (int i = 0; i < ps.Length; i++)
                            {
                                a[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
                                if (a[i] == null && ps[i].ParameterType == typeof(int)) { a[i] = 0; }
                            }
                            a[0] = "OK";
                            if (ps.Length > 3) { a[3] = 0; }
                            if (ps.Length > 4) { a[4] = true; }
                            btnData = ctor.Invoke(a);
                        }
                        else if (ps.Length == 1)
                        {
                            btnData = ctor.Invoke(new object[] { "OK" });
                        }
                        if (btnData != null) { break; }
                    }
                    catch (Exception ex) { Log("popup_make: PopupButtonData ctor(" + ps.Length + ") failed " + (ex.InnerException ?? ex).Message); }
                }
            }

            var arrType = btnType == null ? null : btnType.MakeArrayType();
            object btnArr = arrType == null ? null : Array.CreateInstance(btnType, btnData == null ? 0 : 1);
            if (btnData != null && btnArr != null) { ((Array)btnArr).SetValue(btnData, 0); }

            foreach (var ctor in dataType.GetConstructors())
            {
                var ps = ctor.GetParameters();
                if (ps.Length != 3) { continue; }
                try
                {
                    return ctor.Invoke(new object[] { "Bridge test", "Spawned by the agent bridge to verify the explicit-choice path.", btnArr });
                }
                catch (Exception ex) { Log("popup_make: BasicPopupData ctor failed " + (ex.InnerException ?? ex).Message); }
            }
            return null;
        }

        /// <summary>
        /// The TileData at "x,y", from GameState.Map.Tiles (a flat array indexed y*width+x).
        /// This is what a real reward pick hands to RewardPopup/city reward plumbing.
        /// </summary>
        private object TileAtCoordinates(object gs, int x, int y)
        {
            try
            {
                object map; int index, width;
                if (!TryMapIndex(gs, x, y, out map, out index, out width)) return null;
                var tiles = MemberWithAccessor(map, "Tiles");
                if (tiles == null) { Log("tile: Map.Tiles unavailable"); return null; }
                object selected = null;
                // Prefer a real indexer if the array surfaces as a list.
                var asList = tiles as System.Collections.IList;
                if (asList != null)
                {
                    if (index < 0 || index >= asList.Count) { Log("tile: index " + index + " out of range (" + asList.Count + ")"); return null; }
                    selected = asList[index];
                }
                else
                {
                    var en = AsEnumerable(tiles);
                    if (en == null) return null;
                    int i = 0;
                    foreach (var tile in en)
                    {
                        if (i == index) { selected = tile; break; }
                        i++;
                    }
                }
                int actualX, actualY;
                if (selected != null && TryParseCoords(SafeScalar(MemberWithAccessor(selected, "coordinates")), out actualX, out actualY)
                    && actualX == x && actualY == y) return selected;
            }
            catch (Exception ex) { Log("tile: lookup failed " + (ex.InnerException ?? ex).Message); }
            return null;
        }

        /// <summary>
        /// The real reward pick: invoke the RewardPopup's own RewardChoosenCallback with the
        /// tile that is awaiting the reward and the chosen CityReward, then let the popup close
        /// itself. This is the same callback the popup's reward buttons are wired to
        /// (CommandTriggerUIUtils.PerformCityRewardAction), so the game executes the choice.
        /// </summary>
        // Legacy named popup choice uses the same own-trigger, enum, constructor,
        // executor and settlement path as formal city rewards.
        private string ChooseReward(object popup, string rewardName)
        {
            return ExecuteGameAction("city_reward", rewardName, "", "", "", "", "", _activeAuditRequested ? "1" : "");
        }

        // ------------------------------------------------------------- fog of war
        //
        // A snapshot must never be more informative than the screen it stands in for. The game
        // already answers both fog questions per player, so both answers are read from live game
        // objects and never inferred from coordinates:
        //
        //   explored : "has this player ever seen this tile". Terrain, villages, cities and
        //              resources stay remembered under the dim layer, so explored is enough
        //              to keep reporting them.
        //   visible  : "is this tile in sight right now". Only then are the transient things
        //              readable - other players' units above all.
        //
        // When the game exposes no test the gate hides everything and says so: a snapshot that
        // silently falls back to dumping the whole map is worse than a useless one.

        /// <summary>Per-tile fog tests built from live game state, with provenance for auditing.</summary>
        private sealed class FogGate
        {
            public bool HasExplored;
            public bool HasVisible;
            public Func<object, bool> Explored = t => false;
            public Func<object, bool> Visible = t => false;
            public string ExploredSource = "none";
            public string VisibleSource = "none";
            public string TileType = "?";
            public string Note;
            public int Errors;
        }

        private static object FirstElement(object collection)
        {
            var list = collection == null ? null : AsEnumerable(collection);
            if (list == null) { return null; }
            foreach (var item in list) { if (item != null) { return item; } }
            return null;
        }

        /// <summary>
        /// Locate a static method by name and arity across every loaded assembly. Used for the
        /// game's own "tiles visible to player N" helper, whose declaring type is not worth
        /// hard-coding into a build that can be patched.
        /// </summary>
        internal static MethodInfo FindStaticMethod(string name, int arity)
        { return CachedFindStaticMethod(name, arity); }

        internal static MethodInfo UncachedFindStaticMethod(string name, int arity)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    if (t == null) { continue; }
                    MethodInfo[] ms;
                    try { ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly); }
                    catch { continue; }
                    foreach (var m in ms)
                    {
                        if (m.Name != name) { continue; }
                        if (m.GetParameters().Length == arity) { return m; }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Build the fog gate for one player. Sources, best first:
        ///   explored : TileData.GetExplored(player)          - the game's per-player test
        ///   visible  : GetPlayerVisibleTiles(map, player, …) - the game's own in-sight list
        ///              TileData.visible                     - per-tile flag, when it is a bool
        /// Anything unavailable stays closed.
        /// </summary>
        private FogGate BuildFogGate(object map, object tilesRaw, byte viewPlayer)
        {
            var gate = new FogGate();
            var sample = FirstElement(tilesRaw);
            if (sample == null) { gate.Note = "no tiles"; return gate; }
            var tileType = sample.GetType();
            gate.TileType = tileType.FullName ?? tileType.Name;

            // (1) explored: the game's own per-player test on the tile
            foreach (var argType in new[] { typeof(byte), typeof(int) })
            {
                var m = tileType.GetMethod("GetExplored",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { argType }, null);
                if (m == null || m.ReturnType != typeof(bool)) { continue; }
                object arg = argType == typeof(int) ? (object)(int)viewPlayer : (object)viewPlayer;
                gate.HasExplored = true;
                gate.ExploredSource = "GetExplored(" + argType.Name + ")";
                gate.Explored = t =>
                {
                    try { return (bool)m.Invoke(t, new[] { arg }); }
                    catch (Exception ex) { gate.Errors++; gate.Note = "GetExplored: " + ex.GetType().Name; return false; }
                };
                break;
            }

            // (2) in sight right now: prefer the game's own list over any per-tile flag
            string vueError = "no map";
            var vue = map == null ? null : GameVisibleTiles(map, viewPlayer, out vueError);
            if (vue != null)
            {
                var set = new HashSet<string>();
                foreach (var c in vue) { set.Add(c); }
                gate.HasVisible = true;
                gate.VisibleSource = "GetPlayerVisibleTiles (== GetExplored; native disasm codex-dsh-005)";
                gate.Visible = t =>
                {
                    var c = SafeScalar(MemberWithAccessor(t, "coordinates"));
                    return c != null && set.Contains(c);
                };
            }
            else if (MemberWithAccessor(sample, "visible") is bool)
            {
                gate.HasVisible = true;
                gate.VisibleSource = "Tile.visible";
                gate.Visible = t =>
                {
                    var v = MemberWithAccessor(t, "visible");
                    if (v is bool) { return (bool)v; }
                    gate.Errors++;
                    return false;
                };
            }
            else if (gate.Note == null && vueError != null)
            {
                gate.Note = "no in-sight source (" + vueError + ")";
            }

            if (!gate.HasExplored && !gate.HasVisible) { gate.Note = "no fog API found; everything hidden"; }
            else if (!gate.HasExplored) { gate.Note = "no explored test; only in-sight tiles reported"; }
            else if (!gate.HasVisible) { gate.Note = "no in-sight test; transient state hidden everywhere"; }
            return gate;
        }

        /// <summary>
        /// The player whose eyes the snapshot represents: the local human, NOT whoever's turn it
        /// is. Reading the world from an AI player's perspective would hand the agent exactly the
        /// information the fog exists to withhold.
        ///
        /// Guard: the resolution must never land on the Nature sentinel. `TileData.GetExplored(255)`
        /// is true for every tile (PlayerState.NATURE_PLAYER_ID == 255), so one bad fallback would
        /// open the entire fog gate; when it happens we degrade to the acting player, or to 0 which
        /// hides everything, rather than to 255 which hides nothing.
        /// </summary>
        internal sealed class ControlledIdentity
        {
            public bool Verified;
            public byte Id;
            public string Source = "none";
            public string Problem;
        }

        /// <summary>
        /// The game's own notion of the local player is the only authority accepted here
        /// (`GameManager.LocalPlayer`, a static PlayerState). There is deliberately NO "best
        /// guess": picking the first player, the current player, or "some human" cannot be shown
        /// to be the controlled player, and a wrong pick either opens the fog (Nature's
        /// GetExplored is true everywhere) or hands the planner another player's private view.
        /// Unverified identity => no private observation at all.
        /// </summary>
        private static object _episodeGameState;
        private static object _episodeNativeIdentity;
        private static string _episodeId;

        /// <summary>
        /// Opaque local episode id. Generated once per GameState instance lifetime, so it is stable
        /// within one confirmed live game and different after a save reload (conservative reset).
        /// Contains no seed, no hash of a seed and no native pointer.
        /// </summary>
        private string EpisodeId(object gs)
        {
            if (gs == null) { return null; }
            if (_fixedControlledSeat.HasValue)
            {
                object mainClient = MainClient();
                if (mainClient == null || !CRSameNative(gs, MainClientState())) return null;
                string gameId = MainSessionGameId(mainClient);
                if (!string.IsNullOrEmpty(gameId) && gameId != "null")
                {
                    if (!CRSameNative(mainClient, _episodeMainClient) || gameId != _episodeMainGameId || string.IsNullOrEmpty(_episodeId))
                        _episodeId = "ep-" + Guid.NewGuid().ToString("N").Substring(0, 16);
                    _episodeMainClient = mainClient; _episodeMainGameId = gameId;
                    return _episodeId;
                }
            }
            object nativeIdentity = PolytopiaPublicMarkers.PublicMarkerHooks.StableNativeIdentity(gs);
            bool sameState = nativeIdentity != null && _episodeNativeIdentity != null
                ? object.Equals(nativeIdentity, _episodeNativeIdentity) : ReferenceEquals(gs, _episodeGameState);
            if (!sameState || string.IsNullOrEmpty(_episodeId))
            {
                _episodeGameState = gs;
                _episodeNativeIdentity = nativeIdentity;
                _episodeId = "ep-" + Guid.NewGuid().ToString("N").Substring(0, 16);
            }
            return _episodeId;
        }
        private ControlledIdentity ResolveControlledIdentity(object gs)
        {
            var r = new ControlledIdentity();
            try
            {
                if (_gameManagerType != null)
                {
                    object lp;
                    bool readable = TryReadNativeStaticMember(_gameManagerType, "LocalPlayer", out lp);
                    if (lp != null)
                    {
                        byte? id = NativePlayerId(MemberWithAccessor(lp, "Id"), false);
                        if (id.HasValue)
                        {
                            r.Verified = true;
                            r.Id = id.Value;
                            r.Source = "GameManager.LocalPlayer";
                            return r;
                        }
                        else { r.Problem = "GameManager.LocalPlayer has no valid native player Id"; }
                    }
                    else { r.Problem = readable ? "GameManager.LocalPlayer is null" : "GameManager.LocalPlayer unreadable"; }
                }
                else { r.Problem = "GameManager type unavailable"; }
            }
            catch (Exception ex)
            {
                r.Problem = "identity probe failed: " + ex.GetType().Name;
            }
            Log("identity: controlled player UNVERIFIED (" + r.Problem + ")");
            return r;
        }

        /// <summary>
        /// Ask the game for the tiles the player can see right now. Read-only. Returns null when
        /// the build exposes no such helper.
        /// </summary>
        private List<string> GameVisibleTiles(object map, byte player, out string error)
        {
            error = null;
            Type tileType = FindGameType("TileData");
            var m = FindVisibleTilesMethod(FindGameType("MapDataExtensions"), map, tileType);
            if (m == null) { error = "GetPlayerVisibleTiles(MapData,byte,List) not found"; return null; }
            var ps = m.GetParameters();
            object list;
            try { list = Activator.CreateInstance(ps[2].ParameterType); }
            catch (Exception ex)
            {
                RecordNativeReadFailure(m.DeclaringType, "visible_list_constructor", ex);
                error = "native visible tile collection creation failed: " + (ex.InnerException ?? ex).GetType().Name;
                return null;
            }
            try { m.Invoke(null, new[] { map, (object)player, list }); }
            catch (Exception ex)
            {
                RecordNativeReadFailure(m.DeclaringType, m.Name, ex);
                error = "native visible tile source failed: " + (ex.InnerException ?? ex).GetType().Name;
                return null;
            }
            var coords = new List<string>();
            var values = ReadNativeList(list);
            if (values == null) { error = "native visible tile collection unreadable"; return null; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var td in values)
            {
                object raw;
                string c;
                int x, y;
                if (td == null || !tileType.IsInstanceOfType(td)
                    || !TryReadNativeMember(td, "coordinates", out raw)
                    || (c = SafeScalar(raw)) == null || !TryParseCoords(c, out x, out y)
                    || !seen.Add(c))
                {
                    error = "native visible tile entry unreadable or duplicated";
                    return null;
                }
                coords.Add(c);
            }
            return coords;
        }

        /// <summary>
        /// Read-only audit of the fog tests `world` applies, so "the snapshot is fog-honest" is a
        /// checkable claim rather than a promise: which API answered, whether the two tests
        /// disagree, and how they compare with the game's own visible-tile list.
        /// </summary>
        private string FogReport()
        {
            var gs = GameStateInstance();
            if (gs == null) { return "{\"ok\":false,\"error\":\"no active game\"}"; }
            var o = new JsonObj();
            o.Bool("ok", true);
            var identity = ResolveControlledIdentity(gs);
            o.Bool("controlledIdentityVerified", identity.Verified);
            if (!identity.Verified)
            {
                o.Str("error", "controlled player identity unverified; audit view withheld");
                o.Str("identityProblem", identity.Problem);
                return o.Close();
            }
            byte me = identity.Id;
            o.Str("controlledIdentitySource", identity.Source);
            o.Raw("viewPlayerId", me.ToString(CultureInfo.InvariantCulture));
            o.Raw("currentPlayerId", CurrentPlayerId(gs).ToString(CultureInfo.InvariantCulture));

            var map = Val(_mapProp, gs);
            object tilesRaw = map == null ? null : (MemberWithAccessor(map, "Tiles"));
            var gate = BuildFogGate(map, tilesRaw, me);
            o.Str("tileType", gate.TileType);
            o.Str("exploredSource", gate.ExploredSource);
            o.Str("visibleSource", gate.VisibleSource);
            o.Bool("hasExplored", gate.HasExplored);
            o.Bool("hasVisible", gate.HasVisible);
            if (!string.IsNullOrEmpty(gate.Note)) { o.Str("note", gate.Note); }

            int total = 0, explored = 0, visible = 0, inSightNotExplored = 0, exploredNotInSight = 0;
            int unitsOnExplored = 0;
            var sampleArr = new JsonArr();
            var en = tilesRaw == null ? null : AsEnumerable(tilesRaw);
            if (en != null)
            {
                foreach (var tile in en)
                {
                    if (tile == null) { continue; }
                    total++;
                    bool ex = gate.Explored(tile);
                    bool vi = gate.Visible(tile);
                    if (ex) { explored++; }
                    if (vi) { visible++; }
                    if (vi && !ex) { inSightNotExplored++; }
                    if (ex && !vi) { exploredNotInSight++; }
                    var u = MemberWithAccessor(tile, "unit");
                    // Only units on EXPLORED tiles are counted. Counting the ones in the fog - even
                    // as a number, even as a boolean - would leak that they exist.
                    if (u != null && ex) { unitsOnExplored++; }
                    // Sampling is gated on EXPLORED, not on enumeration order: an unexplored tile
                    // must contribute nothing at all - no coordinate, no owner, no unit presence.
                    var sample = FogSampleJson(ex, vi,
                        SafeScalar(MemberWithAccessor(tile, "coordinates")),
                        SafeScalar(MemberWithAccessor(tile, "owner")), u != null);
                    if (sample != null && explored <= 10) { sampleArr.Raw(sample); }
                }
            }
            o.Raw("tilesTotal", total.ToString(CultureInfo.InvariantCulture));
            o.Raw("tilesExplored", explored.ToString(CultureInfo.InvariantCulture));
            o.Raw("tilesInSight", visible.ToString(CultureInfo.InvariantCulture));
            o.Raw("inSightButNotExplored", inSightNotExplored.ToString(CultureInfo.InvariantCulture));
            o.Raw("exploredButNotInSight", exploredNotInSight.ToString(CultureInfo.InvariantCulture));
            o.Raw("unitsOnExploredTiles", unitsOnExplored.ToString(CultureInfo.InvariantCulture));
            // A CONSTANT policy marker. It must not depend on what is hidden: the previous
            // "unitsInFogWithheld = hiddenCount > 0" leaked the existence of hidden units.
            o.Raw("hiddenEntityPolicy", HiddenEntityPolicyJson());
            o.Raw("samples", sampleArr.Close());

            string err;
            var vue = GameVisibleTiles(map, me, out err);
            o.NullOrStr("gameVisibleTilesError", err);
            if (vue != null)
            {
                o.Raw("gameVisibleTiles", vue.Count.ToString(CultureInfo.InvariantCulture));
                // CORRECTION (codex-dsh-005): native disassembly of
                // MapDataExtensions.GetPlayerVisibleTiles shows it is literally
                // "foreach tile: if tile.GetExplored(playerId) add" - the same predicate as
                // GetExplored, not an independent in-sight test. The earlier claim that agreement
                // here validated coordinate round-tripping was therefore meaningless: the two
                // numbers are one function evaluated twice.
                int same = 0;
                var en2 = tilesRaw == null ? null : AsEnumerable(tilesRaw);
                if (en2 != null)
                {
                    foreach (var tile in en2)
                    {
                        if (tile == null) { continue; }
                        if (gate.Visible(tile)) { same++; }
                    }
                }
                o.Raw("gateInSightViaCoordinates", same.ToString(CultureInfo.InvariantCulture));
                o.Raw("coordinateMismatch", (vue.Count - same).ToString(CultureInfo.InvariantCulture));
                o.Str("visibleTilesSemantics", "identical_to_GetExplored");
                o.Str("visibleTilesEvidence", "native disassembly, codex-dsh-005 (result-005-visibility-analysis.md)");
            }
            o.Bool("tileVisibleFlagIsBool", MemberWithAccessor(FirstElement(tilesRaw), "visible") is bool);
            o.Raw("testErrors", gate.Errors.ToString(CultureInfo.InvariantCulture));
            return o.Close();
        }

        // ---------------------------------------------- decision-channel contract
        //
        // The judgement calls below are PURE functions on purpose. "May I still act", "is the
        // game over" and "can this tile be sampled" are exactly the parts a reviewer must be able
        // to test without a running game, so the game reads happen at the call sites and the
        // rules live here. Unknown input never becomes an optimistic answer.

        /// <summary>
        /// Commands the mod will answer without an explicit debug opt-in.
        ///
        /// HONEST LIMIT: this is a debug convenience, NOT a permission boundary. The caller writes
        /// "audit":"1" itself, so nothing here isolates a real client. Isolation has to happen in
        /// the adapter that talks to the planner - see AdapterDecision, which is the filter a
        /// decision-facing adapter must apply, and which refuses the audit override outright.
        /// </summary>
        internal static readonly string[] DecisionCommands = new string[] {
            "ping", "world", "obs", "path", "reach", "action", "end_turn", "popup",
            "schema", "events", "settlement", "attack_preview", "composite_preview", "threat_map", "unitdata",
            "unit_properties", "unit_actions", "unit_action", "game_info", "tribe_relations", "tribe_action", "popup_info", "city_options",
            "tile_actions", "tile_action", "score_snapshot", "result", "tribe_info", "leaderboard", "notifications",
            "handoff_status", "handoff_start",
        };

        /// <summary>Nature's player id: its GetExplored() answers true for every tile.</summary>
        internal const byte NaturePlayerId = 255;

        /// <summary>
        /// Commands a decision-facing adapter may forward to the planner. Deliberately a strict
        /// subset of DecisionCommands, and it must refuse the audit override rather than honour it.
        /// </summary>
        internal static readonly string[] AdapterCommands = new string[] {
            "ping", "world", "obs", "action", "popup",
            "schema", "events", "settlement", "attack_preview", "composite_preview", "threat_map",
            "unit_properties", "unit_actions", "unit_action", "game_info", "tribe_relations", "tribe_action", "popup_info", "city_options", "unitdata",
            "tile_actions", "tile_action", "score_snapshot", "result", "tribe_info", "leaderboard", "notifications",
            "handoff_status", "handoff_start",
        };

        /// <summary>
        /// popup sub-operations that answer a popup through its own control. `hide`, `closeall`
        /// and `back` are absent on purpose (dismissing a popup is not a player-equivalent
        /// choice), and so are the generic reflection dumps `components`, `shape`, `native` and
        /// `tree`: living inside the popup command does not make a raw object dump safe.
        /// </summary>
        internal static readonly string[] AdapterPopupOps = new string[] {
            "state", "buttons", "buttonlist", "resolve", "choose", "click", "next", "dismiss_resolved_reward",
        };

        /// <summary>
        /// The adapter-level filter for one request. Pure, so the refusal rules can be tested.
        /// Returns "allow" or "deny:&lt;reason&gt;".
        ///
        /// A planner adapter MUST call this: the audit override is refused even when supplied, no
        /// generic reflection command is forwarded, forced popup dismissal is refused, and a
        /// reward/choice action must name what it chooses instead of letting the caller pick.
        /// </summary>
        internal static string AdapterDecision(string cmd, string op, string auditFlag,
            string actionName, string choice)
        {
            if (auditFlag == "1") { return "deny:audit_override_not_accepted_by_adapter"; }
            if (cmd == null || cmd.Length == 0) { return "deny:missing_command"; }
            bool known = false;
            foreach (var c in AdapterCommands) { if (c == cmd) { known = true; break; } }
            if (!known) { return "deny:not_in_adapter_allowlist"; }

            if (cmd == "popup")
            {
                if (op == null || op.Length == 0) { return "deny:popup_requires_explicit_op"; }
                bool ok = false;
                foreach (var p in AdapterPopupOps) { if (p == op) { ok = true; break; } }
                if (!ok) { return "deny:popup_subop_not_allowed:" + op; }
                if (op == "choose" && (choice == null || choice.Length == 0))
                {
                    return "deny:popup_choose_requires_explicit_choice";
                }
                if (op == "click" && (choice == null || choice.Length == 0))
                {
                    return "deny:popup_click_requires_explicit_index";
                }
            }
            if (cmd == "action")
            {
                if (actionName == null || actionName.Length == 0) { return "deny:action_requires_name"; }
                if ((actionName == "city_reward" || actionName == "reward")
                    && (choice == null || choice.Length == 0))
                {
                    return "deny:reward_action_requires_explicit_choice";
                }
            }
            return "allow";
        }

        internal static bool IsDecisionCommand(string cmd)
        {
            if (cmd == null) { return false; }
            foreach (var c in DecisionCommands) { if (c == cmd) { return true; } }
            return false;
        }

        /// <summary>
        /// Every key `world` may emit. The offline regression parses WorldDump() and fails if it
        /// emits a key outside this set - which is what keeps hidden-entity aggregates (a hidden
        /// unit count, a hidden city count) out of the planner's response for good.
        /// </summary>
        internal static readonly string[] DecisionWorldKeys = new string[] {
            "ok", "error", "turn", "playerIndex", "localPlayerId", "currentPlayerId", "myTurn",
            "controlledIdentityVerified", "controlledIdentitySource", "identityProblem",
            "observationRevision", "matchContext", "episodeId", "completeness",
            "canAct", "canActReason", "pendingTriggerCountMine",
            "popupShowing", "popupNeedsChoice", "unknownFields", "schema", "currentState", "matchEnded",
            "state", "visibilitySemantics", "myImprovementCount", "neutralVillages", "neutralVillageCount",
            "visibilityFilters", "resourceVisibilityAuthority", "hiddenEntityPolicy",
            "resourceCategoryPermissions", "researchableTechs", "tribeCatalog", "trainOptions", "trainLimits", "legalMoveTargets", "legalAttackTargets",
            "publicSiegeMarkers", "publicRuinRainbowMarkers",
            "width", "tilesScanned", "tilesExplored", "tilesInSight", "myCityTiles",
            "units", "improvements", "resources", "stars", "ownedTechs", "ownedTechsStatus", "availableTechs",
            "availableTechsDeprecated", "enemyCities", "enemyCityCount", "tiles", "fog", "enums",
        };

        /// <summary>
        /// Aggregate keys that describe things the player cannot see. Even without coordinates
        /// these leak existence/counts, so they must never appear in a decision response.
        /// </summary>
        internal static readonly string[] ForbiddenDecisionKeys = new string[] {
            "unitsHiddenByFog", "tilesHidden", "hiddenUnitCount", "hiddenCityCount",
            "tilesWithUnit", "hiddenUnits", "unknownVillages",
        };

        internal static bool IsDecisionWorldKey(string key)
        {
            if (key == null) { return false; }
            foreach (var k in DecisionWorldKeys) { if (k == key) { return true; } }
            return false;
        }

        /// <summary>Record a field that could not be read, once, so null always comes with a name.</summary>
        private static void Note(HashSet<string> seen, JsonArr sink, string field)
        {
            if (seen == null || sink == null || field == null) { return; }
            if (!seen.Add(field)) { return; }
            sink.Raw("\"" + Esc(field) + "\"");
        }

        /// <summary>
        /// The game's own phase enum (verified live: GameState+State = Unknown, Lobby, Started,
        /// FinalTurn, Ended). Started/FinalTurn accept commands; Lobby/Ended do not; anything not
        /// recognised - including Unknown - stays unknown rather than being guessed either way.
        /// </summary>
        internal static bool? IsPlayingPhase(string phase)
        {
            if (phase == null) { return null; }
            if (phase == "Started" || phase == "FinalTurn") { return true; }
            if (phase == "Lobby" || phase == "Ended") { return false; }
            return null;
        }

        /// <summary>
        /// May the controlled player still issue commands this turn? Returns "true"/"false", or
        /// null when the answer is not determinable. reason explains which rule decided.
        /// A false positive here would let a planner act on a turn it does not own, so every
        /// unreadable input resolves to null.
        /// </summary>
        internal static string CanActOf(bool? myTurn, string phase, int? pendingTriggerMine,
            bool? popupShowing, bool? popupNeedsChoice, bool? processing, out string reason)
        {
            reason = null;
            if (myTurn == null) { reason = "myTurn unknown"; return null; }
            if (!myTurn.Value) { reason = "not my turn"; return "false"; }
            if (pendingTriggerMine == null) { reason = "pending trigger count unknown"; return null; }
            if (popupShowing == null) { reason = "popup presence unknown"; return null; }
            if (popupNeedsChoice == null) { reason = "popup state unknown"; return null; }
            if (processing == null) { reason = "command pipeline state unknown"; return null; }
            var playing = IsPlayingPhase(phase);
            if (playing == null) { reason = "game phase unknown: " + (phase ?? "null"); return null; }
            if (!playing.Value) { reason = "phase not playing: " + phase; return "false"; }
            if (pendingTriggerMine.Value > 0) { reason = "waiting on a pending trigger"; return "false"; }
            // a modal blocks the command pipeline (verified: the intro modal rejected end_turn)
            if (popupShowing.Value) { reason = "a popup is open"; return "false"; }
            if (popupNeedsChoice.Value) { reason = "a popup is waiting for a choice"; return "false"; }
            if (processing.Value) { reason = "command pipeline is busy"; return "false"; }
            reason = "own turn, phase " + phase + ", no pending trigger or choice";
            return "true";
        }

        /// <summary>
        /// Map a raw validation message to a stable code so a planner can branch on identity
        /// instead of on English prose. The names mirror CommandBase.VALIDATION_ERROR_*.
        /// </summary>
        internal static string ErrorCodeOf(string message)
        {
            if (string.IsNullOrEmpty(message)) { return null; }
            string[][] pairs = new[] {
                new[] { "Pending command trigger exists", "COMMAND_TRIGGER_EXISTS" },
                new[] { "Tech not unlockable", "TECH_NOT_UNLOCKABLE" },
                new[] { "Not enough resources", "CANT_AFFORD" },
                new[] { "Not unlocked", "NOT_UNLOCKED" },
                new[] { "Missing unit data", "MISSING_UNIT_DATA" },
                new[] { "Missing improvement data", "MISSING_IMPROVEMENT_DATA" },
                new[] { "Missing player", "MISSING_PLAYER" },
                new[] { "Player mismatch", "PLAYER_MISMATCH" },
                new[] { "Invalid attack target", "INVALID_ATTACK_TARGET" },
                new[] { "Can't attack", "CANT_ATTACK" },
                new[] { "Invalid path", "INVALID_PATH" },
                new[] { "Can't move", "CANT_MOVE" },
                new[] { "Tile occupied", "TILE_OCCUPIED" },
                new[] { "Can't capture", "CANT_CAPTURE" },
                new[] { "Can't support more units", "CANT_SUPPORT_MORE_UNITS" },
                new[] { "Can't promote", "CANT_PROMOTE" },
                new[] { "Can't build", "CANT_BUILD" },
            };
            foreach (var p in pairs)
            {
                if (message.IndexOf(p[0], StringComparison.OrdinalIgnoreCase) >= 0) { return p[1]; }
            }
            return "UNMAPPED";
        }

        /// <summary>
        /// Global terminal state, judged separately from the controlled player's outcome.
        /// A false TryGetWinner is NOT evidence that the game is still running, and a game can be
        /// over with no winner computable, so the only definite answer is "a winner exists".
        /// </summary>
        internal static bool? GameTerminalOf(bool? winnerReadable, bool winnerFound)
        {
            if (winnerReadable == null || !winnerReadable.Value) { return null; }
            return winnerFound ? (bool?)true : null;
        }

        /// <summary>
        /// Outcome for the controlled player. Unknown identities must not be turned into a verdict:
        /// an unreadable winnerId would otherwise read as "somebody else won" = loss.
        /// A confirmed elimination (wipedAtCommandIndex, whose constructor writes -1 and whose
        /// writers set CommandStack.Count-1) IS a real per-player loss, but it says nothing about
        /// the global terminal state, which is why the two are reported separately.
        /// </summary>
        internal static string ControlledPlayerResult(bool? winnerReadable, bool winnerFound,
            string winnerId, string controlledId, bool? wiped)
        {
            if (winnerReadable == true && winnerFound)
            {
                if (winnerId == null || controlledId == null) { return "unknown"; }
                return winnerId == controlledId ? "win" : "loss";
            }
            if (wiped == true) { return "loss"; }
            return "unknown";
        }

        /// <summary>Everything the winner response needs, so the assembly can be tested offline.</summary>
        internal sealed class WinnerInput
        {
            public bool? SignatureOk;        // a by-ref PlayerState overload was found
            public string Signature;         // exact signature, for the audit trail
            public bool? WinnerReadable;     // TryGetWinner returned a value
            public bool WinnerFound;
            public string WinnerId;          // null when unreadable
            public string ControlledId;      // null when unreadable
            public bool? Wiped;              // null when unknown
            public string Phase;
            public string GameVersion;
            public string GameType;
            public string RulesSummary;
            public string InvokeError;
            public bool ControlledIdentityVerified;
            public string ControlledIdentitySource;
            // null = not investigated; false = known to have side effects (codex-dsh-005 finding)
            public bool? SideEffectFree;
            public string SideEffectNote;
        }

        /// <summary>
        /// Assemble the winner response. Every key is written exactly once: an earlier version set
        /// ok=false then ok=true on the same object and emitted a duplicate key.
        /// </summary>
        internal static string BuildWinnerResponse(WinnerInput w)
        {
            var o = new JsonObj();
            bool usable = w != null && w.SignatureOk == true;
            o.Bool("ok", usable);
            if (w == null) { o.Str("error", "no winner input"); return o.Close(); }
            if (!usable)
            {
                o.Str("error", "no by-ref PlayerState overload available");
                o.Raw("gameTerminal", "null");
                o.Str("controlledPlayerResult", "unknown");
                o.Str("signatureStatus", "no_byref_playerstate_overload");
                o.Raw("sideEffectFree", "null");
                o.Str("planningUse", "unsupported_do_not_use_for_decisions");
                return o.Close();
            }
            o.Str("signature", w.Signature);
            o.Str("signatureStatus", "byref_playerstate_verified");
            o.Bool("controlledIdentityVerified", w.ControlledIdentityVerified);
            o.NullOrStr("controlledIdentitySource", w.ControlledIdentitySource);
            string unknownReason = null;
            var reasons = new JsonArr();
            if (w.WinnerReadable == null) { reasons.Raw("\"tryGetWinner_result_unreadable\""); }
            if (w.WinnerReadable == true && w.WinnerFound && w.WinnerId == null)
            {
                reasons.Raw("\"winner_returned_but_identity_unreadable\"");
                unknownReason = "winner_identity_unreadable";
            }
            if (w.ControlledId == null) { reasons.Raw("\"controlled_player_id_unreadable\""); }
            if (w.Wiped == null) { reasons.Raw("\"wiped_state_unreadable\""); }
            reasons.Raw("\"tryGetWinner_false_is_not_evidence_of_not_over\"");
            reasons.Raw("\"tryGetWinner_never_reads_CurrentState\"");
            if (w.SideEffectFree == false) { reasons.Raw("\"tryGetWinner_has_side_effects\""); }
            if (unknownReason == null && (w.WinnerReadable == null || w.ControlledId == null)) { unknownReason = "read_unavailable"; }

            var terminal = GameTerminalOf(w.WinnerReadable, w.WinnerFound);
            if (terminal == null) { o.Raw("gameTerminal", "null"); }
            else { o.Bool("gameTerminal", terminal.Value); }
            o.Str("controlledPlayerResult",
                ControlledPlayerResult(w.WinnerReadable, w.WinnerFound, w.WinnerId, w.ControlledId, w.Wiped));
            o.NullOrStr("winnerId", w.WinnerId);
            o.NullOrStr("controlledPlayerId", w.ControlledId);
            o.NullOrStr("phase", string.IsNullOrEmpty(w.Phase) ? null : w.Phase);
            o.NullOrStr("gameVersion", w.GameVersion);
            o.NullOrStr("gameType", w.GameType);
            o.NullOrStr("rules", w.RulesSummary);
            o.NullOrStr("playerWiped", w.Wiped == null ? null : (w.Wiped.Value ? "true" : "false"));
            if (w.Wiped == true) { o.Bool("playerEliminated", true); }
            else if (w.Wiped == false) { o.Bool("playerEliminated", false); }
            else { o.Raw("playerEliminated", "null"); }
            o.NullOrStr("unknownReason", unknownReason);
            if (!string.IsNullOrEmpty(w.InvokeError)) { o.Str("invokeError", w.InvokeError); }
            if (w.SideEffectFree == null) { o.Raw("sideEffectFree", "null"); }
            else { o.Bool("sideEffectFree", w.SideEffectFree.Value); }
            o.NullOrStr("sideEffectNote", w.SideEffectNote);
            o.Str("planningUse", w.SideEffectFree == true
                ? "read_only_confirmed" : "audit_only_side_effects_confirmed");
            o.Raw("caveats", reasons.Close());
            return o.Close();
        }

        /// <summary>
        /// One `fog` audit policy marker. Constant on purpose: a marker derived from the number of
        /// hidden entities (as an earlier version did) leaks their existence even without
        /// coordinates, so this field must never depend on hidden truth.
        /// </summary>
        internal static string HiddenEntityPolicyJson()
        {
            var o = new JsonObj();
            o.Str("policy", "hidden_entities_never_reported");
            o.Bool("countsReported", false);
            o.Bool("existenceReported", false);
            return o.Close();
        }

        /// <summary>One `fog` audit sample. Returns null for a tile the player has not explored, so an
        /// unexplored tile can never contribute coordinates, owner or unit presence to any output.</summary>
        internal static string FogSampleJson(bool explored, bool visible, string coord, string owner,
            bool hasUnit)
        {
            if (!explored) { return null; }
            var so = new JsonObj();
            so.NullOrStr("c", coord);
            so.Bool("explored", true);
            so.Bool("visible", visible);
            so.NullOrStr("owner", owner);
            so.Bool("hasUnit", hasUnit);
            return so.Close();
        }

        /// <summary>
        /// The five per-unit fields a planner needs and the game already holds. Own units only:
        /// another player's remaining moves are not something the screen shows.
        /// </summary>
        internal static bool UnitActionFieldsVisible(string unitOwner, string viewPlayer)
        {
            return unitOwner != null && viewPlayer != null && unitOwner == viewPlayer;
        }

        /// <summary>
        /// The unit action fields as REAL JSON booleans or null. A string "False" is truthy in
        /// Python, so these must never be rendered as strings.
        /// </summary>
        internal static void UnitActionFields(JsonObj uo, bool? moved, bool? attacked,
            bool? canMove, bool? canAttack, string promotionLevel, string maxHealth)
        {
            uo.NullableBool("canMove", canMove);
            uo.NullableBool("canAttack", canAttack);
            uo.NullableBool("moved", moved);
            uo.NullableBool("attacked", attacked);
            uo.NullOrStr("promotionLevel", promotionLevel);
            uo.NullOrStr("maxHealth", maxHealth);
        }

        /// <summary>
        /// Own-scoped fingerprint, rendered as `k=v|k=v` pairs so both sides can compare named
        /// fields without a JSON parser. Contains only the controlled player's own state.
        /// </summary>
        internal static string Fingerprint(string playerId, string turn, string acting,
            string pendingMine, string stars, string techs, string popupShowing,
            string units, string imps, string res, string explored, string terrains, string roads = null)
        {
            return "player=" + (playerId ?? "?")
                + "|turn=" + (turn ?? "?")
                + "|acting=" + (acting ?? "?")
                + "|pendingMine=" + (pendingMine ?? "?")
                + "|stars=" + (stars ?? "?")
                + "|techs=" + (techs ?? "?")
                + "|popup=" + (popupShowing ?? "?")
                + "|units=" + (units ?? "")
                + "|imps=" + (imps ?? "")
                + "|res=" + (res ?? "")
                + "|explored=" + (explored ?? "?")
                + "|terrains=" + (terrains ?? "")
                + "|roads=" + (roads ?? "");
        }

        internal static string FpField(string fp, string key)
        {
            if (string.IsNullOrEmpty(fp) || string.IsNullOrEmpty(key)) { return null; }
            foreach (var part in fp.Split('|'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) { continue; }
                if (part.Substring(0, eq) == key) { return part.Substring(eq + 1); }
            }
            return null;
        }

        /// <summary>Unit record fields: 0 id, 1 owner, 2 type, 3 coords, 4 hp, 5 moved, 6 attacked.</summary>
        internal static string FpUnitRecord(string units, string unitId)
        {
            if (string.IsNullOrEmpty(units) || string.IsNullOrEmpty(unitId)) { return null; }
            foreach (var rec in units.Split(';'))
            {
                if (rec.Length == 0) { continue; }
                int c = rec.IndexOf(':');
                if (c > 0 && rec.Substring(0, c) == unitId) { return rec; }
            }
            return null;
        }

        internal static string FpUnitAt(string units, string coord)
        {
            if (string.IsNullOrEmpty(units) || string.IsNullOrEmpty(coord)) { return null; }
            foreach (var rec in units.Split(';'))
            {
                if (rec.Length == 0) { continue; }
                var f = rec.Split(':');
                if (f.Length >= 4 && f[3] == coord) { return rec; }
            }
            return null;
        }

        internal static string FpFieldAt(string record, int index)
        {
            if (string.IsNullOrEmpty(record)) { return null; }
            var f = record.Split(':');
            return index >= 0 && index < f.Length ? f[index] : null;
        }

        /// <summary>Improvement record fields: 0 coords, 1 owner, 2 type, 3 level, 4 population.</summary>
        internal static string FpImpRecord(string imps, string coord)
        {
            if (string.IsNullOrEmpty(imps) || string.IsNullOrEmpty(coord)) { return null; }
            foreach (var rec in imps.Split(';'))
            {
                if (rec.Length == 0) { continue; }
                var f = rec.Split(':');
                if (f.Length >= 2 && f[0] == coord) { return rec; }
            }
            return null;
        }

        /// <summary>Resource record fields: 0 coords, 1 type. Only the controlled player's own
        /// tiles are recorded, so the fingerprint stays own-scoped.</summary>
        internal static string FpResRecord(string res, string coord)
        {
            if (string.IsNullOrEmpty(res) || string.IsNullOrEmpty(coord)) { return null; }
            foreach (var rec in res.Split(';'))
            {
                if (rec.Length == 0) { continue; }
                int c = rec.IndexOf(':');
                if (c > 0 && rec.Substring(0, c) == coord) { return rec; }
            }
            return null;
        }

        /// <summary>Terrain record fields: 0 coords, 1 terrain. Only the controlled player's own
        /// revealed tiles are recorded, and an unreadable terrain is simply not recorded - so a
        /// missing record always means "not readable", never a terrain value.</summary>
        internal static string FpTerrainRecord(string terrains, string coord)
        {
            if (string.IsNullOrEmpty(terrains) || string.IsNullOrEmpty(coord)) { return null; }
            foreach (var rec in terrains.Split(';'))
            {
                if (rec.Length == 0) { continue; }
                var f = rec.Split(':');
                if (f.Length >= 2 && f[0] == coord && f[1].Length > 0) { return f[1]; }
            }
            return null;
        }

        /// <summary>Sum of the population fields of MY improvement records, or null when unreadable.
        /// Used to explain a city-growth effect (a harvest grants population) without inventing one.</summary>
        internal static string FpSumMinePopulation(string imps, string playerId)
        {
            if (string.IsNullOrEmpty(imps) || string.IsNullOrEmpty(playerId)) { return null; }
            long sum = 0;
            bool any = false;
            foreach (var rec in imps.Split(';'))
            {
                if (rec.Length == 0) { continue; }
                var f = rec.Split(':');
                if (f.Length < 5 || f[1] != playerId) { continue; }
                int pop;
                if (!int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out pop))
                { continue; }
                sum += pop;
                any = true;
            }
            return any ? sum.ToString(CultureInfo.InvariantCulture) : null;
        }

        internal static bool FpHasTech(string techs, string tech)
        {
            if (string.IsNullOrEmpty(techs) || string.IsNullOrEmpty(tech)) { return false; }
            foreach (var t in techs.Split(',')) { if (t == tech) { return true; } }
            return false;
        }

        /// <summary>
        /// Did THIS action produce ITS OWN effect on the SPECIFIC target it named? Returns
        /// "confirmed", "not_confirmed" or "unknown".
        ///
        /// Whole-army coordinates, a tech *count*, a unit *count*, a city *summary* or a popup
        /// closing can all change for reasons unrelated to the command, so none of them can
        /// confirm a particular action. Only a predicate tied to the named unit / target tile /
        /// tech / city counts, and when that association cannot be established the answer stays
        /// unconfirmed or unknown rather than being guessed.
        /// </summary>
        internal static string ConfirmAction(string playerId, string actionName, string unitId,
            string at, string arg, string before, string after, out string basis)
        {
            basis = null;
            if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(after))
            { basis = "fingerprint missing"; return "unknown"; }
            string bUnits = FpField(before, "units"), aUnits = FpField(after, "units");
            string bImps = FpField(before, "imps"), aImps = FpField(after, "imps");
            if (bUnits == null || aUnits == null || bImps == null || aImps == null)
            { basis = "fingerprint incomplete"; return "unknown"; }
            // The same frozen controlled player must own both snapshots, or the comparison is
            // between two different viewpoints and proves nothing.
            string bPlayer = FpField(before, "player"), aPlayer = FpField(after, "player");
            if (bPlayer == null || aPlayer == null || playerId == null
                || bPlayer != playerId || aPlayer != playerId)
            {
                basis = "controlled player not identical/known across before and after";
                return "unknown";
            }
            string name = actionName == null ? "" : actionName.ToLowerInvariant();

            switch (name)
            {
                case "move":
                case "disembark":
                {
                    if (unitId == null || at == null) { basis = "needs unit and destination"; return "unknown"; }
                    var b = FpUnitRecord(bUnits, unitId);
                    var a = FpUnitRecord(aUnits, unitId);
                    if (b == null || a == null) { basis = "the named unit is not known in both snapshots"; return "unknown"; }
                    string bPos = FpFieldAt(b, 3), aPos = FpFieldAt(a, 3);
                    if (bPos == null || aPos == null) { basis = "unit position unknown in one snapshot"; return "unknown"; }
                    if (bPos == at) { basis = "the unit already stood on the target before the command"; return "not_confirmed"; }
                    basis = "named unit moved to the requested destination: " + (aPos == at);
                    return aPos == at ? "confirmed" : "not_confirmed";
                }
                case "attack":
                {
                    if (unitId == null || at == null) { basis = "needs attacker and target"; return "unknown"; }
                    var b = FpUnitRecord(bUnits, unitId);
                    var a = FpUnitRecord(aUnits, unitId);
                    if (b == null || a == null) { basis = "attacker not known in both snapshots"; return "unknown"; }
                    string bFlag = FpFieldAt(b, 6), aFlag = FpFieldAt(a, 6);
                    if (bFlag == null || aFlag == null)
                    { basis = "attacked flag unknown in one snapshot"; return "unknown"; }
                    bool flipped = bFlag == "False" && aFlag == "True";   // unknown->True is NOT a flip
                    var bTarget = FpUnitAt(bUnits, at);
                    // A melee winner can occupy this tile with exactly the defender's old HP.
                    // Follow the original defender, rather than comparing a replacement occupant.
                    string targetId = FpFieldAt(bTarget, 0);
                    var aTarget = targetId == null ? null : FpUnitRecord(aUnits, targetId);
                    string bHp = FpFieldAt(bTarget, 4), aHp = FpFieldAt(aTarget, 4);
                    bool targetChanged = (bTarget != null && aTarget == null)
                        || (bHp != null && aHp != null && bHp != aHp);
                    bool targetKnownBefore = bTarget != null;
                    basis = "attackerFalseToTrue=" + flipped + "; targetObservable=" + targetKnownBefore
                        + "; targetChanged=" + targetChanged;
                    if (!targetKnownBefore) { return "unknown"; }   // cannot associate the target
                    return (flipped && targetChanged) ? "confirmed" : "not_confirmed";
                }
                case "capture":
                {
                    if (at == null) { basis = "needs target tile"; return "unknown"; }
                    var a = FpImpRecord(aImps, at);
                    var b = FpImpRecord(bImps, at);
                    if (a == null) { basis = "target improvement not observable afterwards"; return "unknown"; }
                    if (b == null) { basis = "target ownership unknown before the command"; return "unknown"; }
                    string aOwner = FpFieldAt(a, 1), bOwner = FpFieldAt(b, 1);
                    if (aOwner == null || bOwner == null) { basis = "ownership unreadable in one snapshot"; return "unknown"; }
                    basis = "ownership changed to mine: " + (bOwner != playerId && aOwner == playerId);
                    return (bOwner != playerId && aOwner == playerId) ? "confirmed" : "not_confirmed";
                }
                case "research":
                {
                    if (string.IsNullOrEmpty(arg)) { basis = "needs the tech name"; return "unknown"; }
                    string bTechs = FpField(before, "techs"), aTechs = FpField(after, "techs");
                    if (bTechs == null || aTechs == null || bTechs == "?" || aTechs == "?") { basis = "tech list unavailable"; return "unknown"; }
                    bool had = FpHasTech(bTechs, arg), has = FpHasTech(aTechs, arg);
                    basis = "named tech newly owned: " + (!had && has);
                    return (!had && has) ? "confirmed" : "not_confirmed";
                }
                case "train":
                {
                    if (string.IsNullOrEmpty(arg) || at == null) { basis = "needs unit kind and training tile"; return "unknown"; }
                    var now = FpUnitAt(aUnits, at);
                    var then = FpUnitAt(bUnits, at);
                    if (now == null) { basis = "no unit is observable on the training tile afterwards"; return "not_confirmed"; }
                    string nowType = FpFieldAt(now, 2), nowOwner = FpFieldAt(now, 1);
                    string thenType = FpFieldAt(then, 2);
                    if (nowType == null || nowOwner == null)
                    { basis = "unit type/owner unreadable afterwards"; return "unknown"; }
                    basis = "named kind now on the named training tile: " + (nowOwner == playerId && nowType == arg && thenType != arg);
                    return (nowOwner == playerId && nowType == arg && thenType != arg)
                        ? "confirmed" : "not_confirmed";
                }
                case "destroy":
                {
                    if (at == null) { basis = "needs named tile"; return "unknown"; }
                    var b = FpImpRecord(bImps, at);
                    var a = FpImpRecord(aImps, at);
                    string terrain = FpTerrainRecord(FpField(after, "terrains"), at);
                    if (b == null || FpFieldAt(b, 1) != playerId || FpFieldAt(b, 2) == null || terrain == null)
                    { basis = "named owned building or afterwards owned/explored tile unavailable"; return "unknown"; }
                    if (FpFieldAt(b, 2) == "City") { basis = "Destroy is not city capture or disband"; return "not_confirmed"; }
                    basis = "named owned building " + FpFieldAt(b, 2) + " removed; tile remains owned and observable";
                    return a == null ? "confirmed" : "not_confirmed";
                }
                case "break_ice":
                {
                    string b = FpTerrainRecord(FpField(before, "publicTerrains"), at) ?? FpTerrainRecord(FpField(before, "terrains"), at);
                    string a = FpTerrainRecord(FpField(after, "publicTerrains"), at) ?? FpTerrainRecord(FpField(after, "terrains"), at);
                    if (b == null || a == null) { basis = "target terrain unavailable in one snapshot"; return "unknown"; }
                    basis = "named target terrain " + b + "->" + a;
                    if (b != "Ice") { basis += "; non-ice effect removal is not proved by this terrain channel"; return "unknown"; }
                    return b == "Ice" && (a == "Water" || a == "Ocean") ? "confirmed" : "not_confirmed";
                }
                case "build":
                {
                    if (at == null || string.IsNullOrEmpty(arg)) { basis = "needs target tile and improvement kind"; return "unknown"; }
                    // Terrain operations do not leave an improvement behind. Confirm the named
                    // tile's visible transition; BurnForest must also create its visible Crop.
                    // Stars alone, another tile changing, or an unreadable terrain cannot prove it.
                    bool growForest = string.Equals(arg, "GrowForest", StringComparison.OrdinalIgnoreCase);
                    bool burnForest = string.Equals(arg, "BurnForest", StringComparison.OrdinalIgnoreCase);
                    if (growForest || burnForest || string.Equals(arg, "ClearForest", StringComparison.OrdinalIgnoreCase))
                    {
                        string bTerr = FpTerrainRecord(FpField(before, "terrains"), at);
                        string aTerr = FpTerrainRecord(FpField(after, "terrains"), at);
                        string terrEvidence = "namedTile=" + at
                            + "; targetTerrain=" + (bTerr ?? "unreadable") + "->"
                            + (aTerr ?? "unreadable")
                            + "; stars=" + (FpField(before, "stars") ?? "?")
                            + "->" + (FpField(after, "stars") ?? "?");
                        if (bTerr == null || aTerr == null)
                        {
                            basis = "the named target's terrain is not readable in both snapshots ("
                                + terrEvidence + ")";
                            return "unknown";
                        }
                        string expectedBefore = growForest ? "Field" : "Forest";
                        string expectedAfter = growForest ? "Forest" : "Field";
                        bool beforeMatches = string.Equals(bTerr, expectedBefore, StringComparison.OrdinalIgnoreCase);
                        bool afterMatches = string.Equals(aTerr, expectedAfter, StringComparison.OrdinalIgnoreCase);
                        basis = "target terrain " + bTerr + "->" + aTerr + " (" + terrEvidence + ")";
                        if (!beforeMatches || !afterMatches)
                        {
                            basis += "; expected named target " + expectedBefore + "->" + expectedAfter;
                            return "not_confirmed";
                        }
                        if (burnForest)
                        {
                            string visibleResources = FpField(after, "res");
                            string targetResource = FpFieldAt(FpResRecord(visibleResources, at), 1);
                            basis += "; targetResourceAfter=" + (targetResource ?? "unobserved");
                            // An absent resource is not proof of its native absence: the snapshot
                            // contains only technology-visible resources, never hidden resource data.
                            if (visibleResources == null || targetResource == null) { return "unknown"; }
                            if (!string.Equals(targetResource, "Crop", StringComparison.OrdinalIgnoreCase)) { return "not_confirmed"; }
                        }
                        return "confirmed";
                    }
                    var b = FpImpRecord(bImps, at);
                    var a = FpImpRecord(aImps, at);
                    if (string.Equals(arg, "Road", StringComparison.OrdinalIgnoreCase))
                    {
                        string roadBefore = FpTerrainRecord(FpField(before, "roads"), at) ?? FpFieldAt(b, 6);
                        string roadAfter = FpTerrainRecord(FpField(after, "roads"), at) ?? FpFieldAt(a, 6);
                        if ((roadBefore != "true" && roadBefore != "false") || (roadAfter != "true" && roadAfter != "false"))
                        { basis = "native road flag unreadable for named tile"; return "unknown"; }
                        basis = "named tile native HasRoad " + roadBefore + "->" + roadAfter;
                        return roadBefore == "false" && roadAfter == "true" ? "confirmed" : "not_confirmed";
                    }
                    string aType = a == null ? null : FpFieldAt(a, 2);
                    string aOwner = a == null ? null : FpFieldAt(a, 1);
                    string bResRec = FpResRecord(FpField(before, "res"), at);
                    string aResRec = FpResRecord(FpField(after, "res"), at);
                    string bResType = FpFieldAt(bResRec, 1);
                    string aResType = FpFieldAt(aResRec, 1);
                    // Every signal the player can see, reported together so the verdict is
                    // explainable: the resource on the target tile, my city population and stars.
                    string evidence = "targetResource=" + (bResType ?? "none") + "->" + (aResType ?? "none")
                        + "; targetImprovement=" + (b == null ? "none" : (FpFieldAt(b, 2) ?? "?"))
                        + "->" + (aType ?? "none")
                        + "; cityPopulationMine=" + (FpSumMinePopulation(bImps, playerId) ?? "?")
                        + "->" + (FpSumMinePopulation(aImps, playerId) ?? "?")
                        + "; stars=" + (FpField(before, "stars") ?? "?")
                        + "->" + (FpField(after, "stars") ?? "?");
                    // (1) The requested improvement now stands on the target tile.
                    if (a != null && aType != null)
                    {
                        bool kindMatches = string.Equals(aType, arg, StringComparison.OrdinalIgnoreCase);
                        basis = "requested kind present on the target: " + kindMatches + " (" + evidence + ")";
                        return (kindMatches && aOwner == playerId) ? "confirmed" : "not_confirmed";
                    }
                    // (2) A HARVEST: the resource that stood on the target tile is consumed and NO
                    // improvement is created (verified in this build: ImprovementData.Type.HarvestFruit
                    // has no `creates`, so the old "an improvement must appear" predicate could only
                    // ever return unknown). The consumed resource is itself the visible effect; a star
                    // change alone never confirms anything.
                    if (!string.IsNullOrEmpty(bResType) && string.IsNullOrEmpty(aResType))
                    {
                        basis = "harvest consumed the resource on the target (" + evidence + ")";
                        return "confirmed";
                    }
                    // (3) Neither a resource to consume nor a new improvement: no observable effect.
                    if (string.IsNullOrEmpty(bResType) && string.IsNullOrEmpty(aResType))
                    {
                        basis = "no resource and no improvement observable on the target (" + evidence + ")";
                        return "unknown";
                    }
                    basis = "the resource on the target changed into another resource, not consumed ("
                        + evidence + ")";
                    return "not_confirmed";
                }
                case "upgrade":
                case "upgradeunit":
                case "upgrade_unit":
                {
                    if (at == null || string.IsNullOrEmpty(arg))
                    { basis = "needs the unit tile and the target unit type"; return "unknown"; }
                    var b = FpUnitAt(bUnits, at);
                    var a = FpUnitAt(aUnits, at);
                    if (b == null) { basis = "no unit was observable on the tile before the command"; return "unknown"; }
                    string bType = FpFieldAt(b, 2), aType = FpFieldAt(a, 2);
                    string bOwner = FpFieldAt(b, 1), aOwner = a == null ? null : FpFieldAt(a, 1);
                    if (string.IsNullOrEmpty(playerId) || string.IsNullOrEmpty(bType) || string.IsNullOrEmpty(bOwner)
                        || (a != null && string.IsNullOrEmpty(aOwner)))
                    { basis = "unit type or ownership unavailable for upgrade transition"; return "unknown"; }
                    if (aType == null) { basis = "no unit observable on the tile afterwards"; return "not_confirmed"; }
                    // A successful upgrade is a real TRANSITION of MY unit on that tile: it existed
                    // before with a different type, and afterwards it is the requested type. Just
                    // seeing the requested type afterwards is not enough - pointing the command at a
                    // unit that is already a Scoutship must not read as a successful upgrade.
                    bool mineBefore = bOwner == playerId;
                    bool mineAfter = aOwner == playerId;
                    bool wasDifferent = !string.Equals(bType, arg, StringComparison.OrdinalIgnoreCase);
                    bool isTarget = string.Equals(aType, arg, StringComparison.OrdinalIgnoreCase);
                    bool transition = mineBefore && wasDifferent && mineAfter && isTarget;
                    basis = "before=" + (bType ?? "?") + "(mine=" + mineBefore + ") after=" + aType
                        + "(mine=" + mineAfter + "); requested=" + arg + "; transition=" + transition;
                    return transition ? "confirmed" : "not_confirmed";
                }
                case "disband":
                {
                    if (at == null) { basis = "needs the unit tile"; return "unknown"; }
                    var b = FpUnitAt(bUnits, at);
                    var a = FpUnitAt(aUnits, at);
                    if (b == null) { basis = "no unit was observable on the tile before the command"; return "unknown"; }
                    bool gone = a == null;
                    basis = "unit " + (FpFieldAt(b, 2) ?? "?") + " on " + at + " was mine before; gone afterwards=" + gone;
                    return gone ? "confirmed" : "not_confirmed";
                }
                case "ruins":
                {
                    if (at == null) { basis = "needs target tile"; return "unknown"; }
                    var aRuin = FpImpRecord(aImps, at);
                    var bRuin = FpImpRecord(bImps, at);
                    // The target-associated, player-visible effect of examining a ruin is that the
                    // Ruin on THAT tile is gone afterwards. Whether a reward was granted is a
                    // different question and is read from the reward popup / public effects - it is
                    // deliberately NOT claimed here.
                    bool ruinBefore = bRuin != null
                        && string.Equals(FpFieldAt(bRuin, 2), "Ruin", StringComparison.OrdinalIgnoreCase);
                    bool ruinAfter = aRuin != null
                        && string.Equals(FpFieldAt(aRuin, 2), "Ruin", StringComparison.OrdinalIgnoreCase);
                    basis = "tile " + at + " ruinBefore=" + ruinBefore + " ruinAfter=" + ruinAfter;
                    if (ruinBefore && !ruinAfter)
                    { basis = basis + "; the ruin on the target tile was consumed"; return "confirmed"; }
                    if (!ruinBefore)
                    { basis = basis + "; no ruin was observable on the target before the command"; return "unknown"; }
                    return "not_confirmed";
                }
                case "end_turn":
                {
                    string bTurn = FpField(before, "turn"), aTurn = FpField(after, "turn");
                    string bActing = FpField(before, "acting"), aActing = FpField(after, "acting");
                    if (bTurn == null || aTurn == null || bActing == null || aActing == null)
                    { basis = "turn/acting unknown in a snapshot"; return "unknown"; }
                    bool turnMoved = bTurn != aTurn;
                    bool actorMoved = bActing != aActing;
                    basis = "knownBeforeAndAfter turnMoved=" + turnMoved + " actorMoved=" + actorMoved;
                    return (turnMoved || actorMoved) ? "confirmed" : "not_confirmed";
                }
                case "city_reward":
                case "reward":
                {
                    if (at == null) { basis = "needs the city the reward belongs to"; return "unknown"; }
                    if (string.IsNullOrEmpty(arg)) { basis = "needs the reward that was chosen"; return "unknown"; }
                    var b = FpImpRecord(bImps, at);
                    var a = FpImpRecord(aImps, at);
                    if (b == null || a == null) { basis = "city not known in both snapshots"; return "unknown"; }
                    string bSig = FpFieldAt(b, 3) + "/" + FpFieldAt(b, 4);   // level/population
                    string aSig = FpFieldAt(a, 3) + "/" + FpFieldAt(a, 4);
                    bool cityChanged = bSig != aSig;
                    // Some rewards materialise as an improvement on the city tile (Park / City Wall /
                    // Monument style) - that IS player-visible evidence tied to this city.
                    bool cityImprovementChanged = !string.Equals(FpFieldAt(b, 2), FpFieldAt(a, 2),
                        StringComparison.OrdinalIgnoreCase);
                    // CityReward.Explorer has no other visible effect than revealing map, so the
                    // revealed-tile count is its signature.
                    int bExp = 0, aExp = 0;
                    bool exploredKnown =
                        int.TryParse(FpField(before, "explored"), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out bExp)
                        && int.TryParse(FpField(after, "explored"), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out aExp);
                    bool exploredGrew = exploredKnown && aExp > bExp;
                    bool popupClosed = FpField(before, "popup") == "true"
                        && FpField(after, "popup") == "false";
                    basis = "chosen=" + arg + "; cityLevel/Population " + bSig + "->" + aSig
                        + "; improvementTypeChanged=" + cityImprovementChanged
                        + "; exploredTiles=" + (exploredKnown
                            ? bExp.ToString(CultureInfo.InvariantCulture) + "->"
                              + aExp.ToString(CultureInfo.InvariantCulture) : "unknown")
                        + "; popupClosed=" + popupClosed;
                    string chosenBefore = FpFieldAt(b, 5), chosenAfter = FpFieldAt(a, 5);
                    if (chosenBefore == null || chosenAfter == null || chosenBefore == "?" || chosenAfter == "?")
                    { basis += "; native chosen reward list unavailable"; return "unknown"; }
                    int rewardCountBefore = Array.FindAll(chosenBefore.Split(','), x => string.Equals(x, arg, StringComparison.OrdinalIgnoreCase)).Length;
                    int rewardCountAfter = Array.FindAll(chosenAfter.Split(','), x => string.Equals(x, arg, StringComparison.OrdinalIgnoreCase)).Length;
                    if (rewardCountAfter > rewardCountBefore)
                    { basis += "; requested reward newly present in native city reward list"; return "confirmed"; }
                    // The selecting command was accepted and the reward popup was consumed, but THIS
                    // reward's own effect (e.g. a production modifier) is not in the observation. The
                    // reply stays accepted_pending_confirmation on purpose: "the choice went through"
                    // is NOT the same claim as "the reward's effect was observed".
                    basis = basis + "; the choice went through but this reward's effect is not visible "
                        + "in the observation (no improvement on the city, no level/population change, "
                        + "no new map revealed)";
                    return "not_confirmed";
                }
                default:
                    basis = "no target-associating predicate exists for '" + name + "'";
                    return "unknown";
            }
        }

        /// <summary>Everything the action response needs, so its assembly can be tested offline.</summary>
        internal sealed class ActionEcho
        {
            public string ActionName;
            public string ActionLabel;
            public string Via;
            public string Manager;
            public bool? Accepted;              // null when the call did not report a value
            public string AcceptedRaw;
            public string ValidationError;
            public string Before;
            public string After;
            public string ControlledId;          // captured BEFORE the command was sent
            public string TargetUnitId;          // the unit the command named (for association)
            public string TargetCoord;           // the tile/coordinate the command named
            public string TargetArg;             // tech name or unit kind the command named
            public int? PendingMineAfter;        // null when unreadable
            public bool? PopupAfter;             // null when unreadable
            public bool Audit;                   // true = include raw diagnostics (audit path only)
        }

        /// <summary>
        /// Coarse error identity for actions whose fine-grained error could reveal whether a
        /// hidden resource exists (for example "no resource here" vs "not unlocked"). The planner
        /// gets one refusal code for that family; the precise text stays in validationError for
        /// the audit path.
        /// </summary>
        internal static string CoarseErrorCode(string actionName, string code)
        {
            if (code == null) { return null; }
            string name = actionName == null ? "" : actionName.ToLowerInvariant();
            if (name == "build" || name == "ruins")
            {
                if (code == "NOT_UNLOCKED" || code == "CANT_BUILD" || code == "CANT_AFFORD"
                    || code == "MISSING_IMPROVEMENT_DATA")
                {
                    return "BUILD_REFUSED";
                }
            }
            return code;
        }

        /// <summary>
        /// Assemble admission metadata. Only AttachCombatSettlement can supply the original
        /// ticket's final public result; immediate snapshot comparison is diagnostic only.
        /// </summary>
        internal static string BuildActionResponse(ActionEcho e)
        {
            var o = new JsonObj();
            o.Bool("ok", true);
            o.Str("action", e.ActionLabel);
            o.Str("via", e.Via);
            o.Str("manager", e.Manager);
            if (e.Accepted == null) { o.Raw("accepted", "null"); }
            else { o.Bool("accepted", e.Accepted.Value); }
            o.NullOrStr("acceptedRaw", e.AcceptedRaw);
            // No raw validation text here: only the coarse, player-equivalent code. The precise
            // string can distinguish "no resource" from "resource present but locked", so it is
            // audit-only (see the diagnostics block below).
            string code = CoarseErrorCode(e.ActionName, ErrorCodeOf(e.ValidationError));
            if (code != null) { o.Str("errorCode", code); }
            o.Str("controlledPlayerId", e.ControlledId);
            o.NullOrStr("targetUnitId", e.TargetUnitId);
            o.NullOrStr("targetCoord", e.TargetCoord);
            o.NullOrStr("targetArg", e.TargetArg);

            string confirmation = "unknown", basis = "query the original settlement ticket";
            if (e.Audit)
            {
                string snapshotBasis;
                string snapshotConfirmation = ConfirmAction(e.ControlledId, e.ActionName, e.TargetUnitId,
                    e.TargetCoord, e.TargetArg, e.Before, e.After, out snapshotBasis);
                o.Raw("snapshotComparison", new JsonObj().Str("confirmation", snapshotConfirmation)
                    .NullOrStr("basis", snapshotBasis).Str("authority", "immediate visible fingerprint comparison; not command completion").Close());
            }
            string verdict;
            // "no value reported" is not the same as "rejected": an unreported result stays unknown.
            bool rejected = e.Accepted == false || !string.IsNullOrEmpty(e.ValidationError);
            if (rejected) { verdict = "rejected"; }
            else if (e.Accepted == null) { verdict = "unknown"; }
            else if (e.PendingMineAfter == null || e.PopupAfter == null) { verdict = "unknown"; }
            else if (e.PendingMineAfter.Value > 0 || e.PopupAfter.Value) { verdict = "needs_resolution"; }
            else { verdict = "accepted_pending_confirmation"; }

            // The fingerprint is a plain string (k=v|k=v), NOT JSON: it must be quoted like any
            // other value, or the frame stops being parseable at all.
            //
            // PLANNER vs AUDIT: the raw fingerprints carry every visible unit's internal id,
            // moved/attacked flags and coordinates. Those are deliberately withheld from `world`,
            // so sending the whole string here would re-disclose them through the action channel.
            // The planner therefore receives a SAFE PROJECTION only; the raw strings, the raw
            // validation text and any exception detail stay on the audit path.
            if (e.Audit)
            {
                o.Str("before", e.Before);
                o.Str("after", e.After);
                o.Str("validationError", e.ValidationError);
                o.Bool("diagnostics", true);
            }
            else
            {
                o.Bool("diagnosticsWithheld", true);
            }
            o.Str("verdict", verdict);
            o.Str("confirmation", confirmation);
            o.NullOrStr("confirmationBasis", basis);
            o.Bool("actionCompleted", verdict == "confirmed");
            o.Raw("pendingTriggerCountMine", e.PendingMineAfter == null
                ? "null" : e.PendingMineAfter.Value.ToString(CultureInfo.InvariantCulture));
            if (e.PopupAfter == null) { o.Raw("popupShowing", "null"); }
            else { o.Bool("popupShowing", e.PopupAfter.Value); }
            bool need = verdict != "confirmed";
            o.Bool("needsRedecision", need);
            o.Str("reobserve", need ? "required" : "optional");
            return o.Close();
        }

        // ------------------------------------------- live probes feeding those rules

        private IEnumerable PendingTriggerList(object gs)
        { return ReadPendingCommandTriggers(gs); }

        internal static List<object> ReadPendingCommandTriggers(object gs)
        {
            try
            {
                var t = gs.GetType();
                var p = t.GetProperty("pendingCommandTriggers",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                bool found = p != null;
                object raw = p != null ? p.GetValue(gs, null) : null;
                if (!found)
                {
                    var f = t.GetField("pendingCommandTriggers",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? t.GetField("_pendingCommandTriggers_k__BackingField",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null) { found = true; raw = f.GetValue(gs); }
                }
                // A member that EXISTS and reads back null is an empty collection, not an unreadable
                // one: this is exactly what a fresh match looks like on turn 0 (codex-dsh-063 read
                // raw=null while CurrentState was Started and it was my turn). Only a member that
                // could not be located keeps the answer unknown.
                if (!found) { return null; }
                if (raw == null) { return new List<object>(); }
                return ReadNativeList(raw);
            }
            catch (Exception ex) { Log("world: pendingCommandTriggers failed " + ex.Message); return null; }
        }

        /// <summary>
        /// Anyone's pending triggers. Kept for the audit path only: `world` reports solely the
        /// controlled player's own count, because other players' pending rewards are private.
        /// </summary>
        private int? TotalPendingTriggers(object gs, JsonArr unknown)
        {
            var list = PendingTriggerList(gs);
            if (list == null) { return null; }
            int n = 0;
            foreach (var t in list) { if (t != null) { n++; } }
            return n;
        }


        /// <summary>
        /// Triggers that belong to one player. A trigger whose owner cannot be read returns null
        /// instead of being attributed to anyone - guessing here would mis-state whether the
        /// controlled player is blocked.
        /// </summary>
        private int? PendingTriggerCountFor(object gs, byte player, JsonArr unknown)
        {
            var list = PendingTriggerList(gs);
            if (list == null) { return null; }
            var want = player.ToString(CultureInfo.InvariantCulture);
            int n = 0;
            foreach (var t in list)
            {
                if (t == null) { continue; }
                var pid = SafeScalar(MemberWithAccessor(t, "playerId"));
                if (pid == null) { return null; }
                if (pid == want) { n++; }
            }
            return n;
        }

        /// <summary>Whether a popup is on screen, or null when PopupManager cannot answer.</summary>
        private bool? PopupIsOpen()
        {
            var t = FindType("PopupManager") ?? FindType("Il2Cpp.PopupManager");
            if (t == null) { return null; }
            var raw = InvokeStatic(t, "get_PopupShowing") ?? InvokeStatic(t, "get_IsPopupShowing");
            if (raw == null) { return null; }
            return NativeBool(raw);
        }

        /// <summary>
        /// Whether the open popup demands a choice from the player (a reward list), as opposed to
        /// a plain acknowledgement. null when the popup contents cannot be inspected.
        /// </summary>
        /// <summary>
        /// Read-only: the component on the CURRENTLY OPEN popup that carries `cityRewards` - i.e. the
        /// actual reward choices on offer for a city that just levelled up - or null when no such
        /// popup is open / its list cannot be inspected. Never invokes anything on the popup.
        /// Exposed so the read-only CityOptions view can describe an upgrade choice without
        /// re-implementing the popup lookup.
        /// </summary>
        internal static object CurrentCityRewardPopupComponent()
        {
            var t = FindType("PopupManager") ?? FindType("Il2Cpp.PopupManager");
            if (t == null) { return null; }
            object current = InvokeStatic(t, "GetCurrentPopup");
            if (current == null) { return null; }
            var go = MemberWithAccessor(current, "gameObject");
            if (go == null) { return null; }
            object active = MemberWithAccessor(go, "activeInHierarchy");
            if (!(active is bool) || !(bool)active) return null;
            // Resolve the known component without reading its choices. Its city must
            // be matched by the caller before any reward list is inspected.
            if (current.GetType().Name == "RewardPopup") return current;
            var rewardType = FindGameType("RewardPopup");
            return rewardType == null ? null : GetComponentOfType(go, rewardType);
        }

        /// <summary>
        /// Is the command pipeline mid-animation/busy? Any read that cannot be made returns null;
        /// a busy pipeline must never look idle.
        /// </summary>
        private bool? ActionPipelineBusy(JsonArr unknown)
        {
            // Reuse the SAME proven chain the command channel uses (GameManager -> ClientBase ->
            // ActionManager), so the state read here describes the client that actually dispatches.
            // ClientActionManager is not a Unity object and has no static Instance, so a static
            // instance lookup could never work.
            object am = FindActionManager(_gameManagerType != null ? SafeInstance() : null);
            if (am == null) { unknown.Raw("\"canAct:actionmanager-unresolved\""); return null; }
            // Every gate member is read through its PROPERTY first and its explicit getter second.
            // canAct may only report "can act" when all of them were actually read AND are false:
            // an unreadable gate stays unknown instead of being treated as idle.
            bool? processing = ReadBoolPropertyOrGetter(am, "IsProcessing");
            bool? pausedOrPausing = ReadBoolPropertyOrGetter(am, "IsPausedOrPausing");
            bool? paused = ReadBoolPropertyOrGetter(am, "IsPaused");
            bool? simulating = ReadBoolPropertyOrGetter(am, "IsSimulating");
            string missing = "";
            if (processing == null) { missing += "IsProcessing "; }
            if (pausedOrPausing == null) { missing += "IsPausedOrPausing "; }
            if (paused == null) { missing += "IsPaused "; }
            if (simulating == null) { missing += "IsSimulating "; }
            if (missing.Length > 0)
            {
                unknown.Raw("\"canAct:unreadable[" + missing.Trim() + "]\"");
                return null;
            }
            if (processing.Value || pausedOrPausing.Value || paused.Value || simulating.Value)
            { return true; }
            return false;
        }

        // Compatibility entry points share one native boolean accessor plan.
        private static bool? ReadBoolMember(object o, string name)
        { return ReadNativeBooleanMember(o, name); }

        internal static bool? ReadBoolPropertyOrGetter(object o, string name)
        { return ReadNativeBooleanMember(o, name); }

        internal static bool? ReadBoolInvoke(object o, string name)
        { return ReadNativeBooleanMember(o, name); }

        /// <summary>
        /// The unit this player may actually see on a tile, FAIL-CLOSED.
        ///
        /// `TileData.GetUnit(gameState, playerId, includeHidden=false)` applies the game's own
        /// filters (explored, plus `UnitEffect.Invisible`). When that call cannot be made - the
        /// overload is missing, its parameter types do not match, or the invoke throws - the unit
        /// is WITHHELD (null) rather than falling back to the raw `unit` property: unknownFields
        /// cannot un-disclose data that was already sent, so a failed filter must fail closed.
        /// </summary>
        internal static object TileUnitVisible(object tile, object gs, byte player, out bool filtered)
        {
            filtered = false;
            if (tile == null) { return null; }
            UnitFilter.LookupAttempted++;
            var m = FindTileVisibilityMethod(tile.GetType(), true);
            if (m != null)
            {
                // Counted per attempted tile: the status describes the whole sweep, not one tile.
                UnitFilter.SignatureAvailable = true;
                UnitFilter.Attempted++;
                try
                {
                    var r = m.Invoke(tile, new object[] { gs, player, false });
                    filtered = true;
                    UnitFilter.Succeeded++;
                    return r;
                }
                catch { filtered = false; UnitFilter.Failed++; return null; }
            }
            return null;
        }

        /// <summary>
        /// The resource this player may actually see on a tile, FAIL-CLOSED.
        /// `TileData.GetResource(gameState, playerId)` applies the game's own visibility rule;
        /// when it cannot be called the resource is withheld rather than disclosed raw.
        /// </summary>
        internal static object TileResourceVisible(object tile, object gs, byte player, out bool filtered)
        {
            filtered = false;
            if (tile == null) { return null; }
            ResourceFilter.LookupAttempted++;
            var m = FindTileVisibilityMethod(tile.GetType(), false);
            if (m != null)
            {
                ResourceFilter.SignatureAvailable = true;
                ResourceFilter.Attempted++;
                try
                {
                    var r = m.Invoke(tile, new object[] { gs, player });
                    filtered = true;
                    ResourceFilter.Succeeded++;
                    return r;
                }
                catch { filtered = false; ResourceFilter.Failed++; return null; }
            }
            return null;
        }

        /// <summary>
        /// Per-call statistics for one filtered accessor. "Complete" requires EVERY attempted call
        /// to have succeeded - a single successful tile must not make the whole observation look
        /// trustworthy (the previous OR-aggregated boolean did exactly that).
        /// </summary>
        internal sealed class FilterStat
        {
            public bool SignatureAvailable;
            public int LookupAttempted;   // tiles examined, whether or not an overload was found
            public int Attempted;         // tiles where a usable overload was actually invoked
            public int Succeeded;
            public int Failed;
        }

        internal static FilterStat UnitFilter = new FilterStat();
        internal static FilterStat ResourceFilter = new FilterStat();

        internal static void ResetFilterStats()
        {
            UnitFilter = new FilterStat();
            ResourceFilter = new FilterStat();
        }

        internal static string FilterStatJson(FilterStat s)
        {
            var o = new JsonObj();
            o.Bool("signatureAvailable", s.SignatureAvailable);
            o.Raw("lookupAttempted", s.LookupAttempted.ToString(CultureInfo.InvariantCulture));
            o.Raw("attempted", s.Attempted.ToString(CultureInfo.InvariantCulture));
            o.Raw("succeeded", s.Succeeded.ToString(CultureInfo.InvariantCulture));
            o.Raw("failed", s.Failed.ToString(CultureInfo.InvariantCulture));
            string status = s.LookupAttempted == 0 ? "not_attempted"
                : s.Attempted == 0 ? "signature_unavailable"
                : s.Failed > 0 ? "failed"
                : "complete";
            o.Str("status", status);
            return o.Close();
        }

        internal static string FilterSnapshotStatus()
        {
            // Lookup is counted separately from invocation, so "nothing examined yet" and "tiles
            // examined but no usable overload exists" stay distinguishable.
            if (UnitFilter.LookupAttempted == 0 || ResourceFilter.LookupAttempted == 0) { return "not_attempted"; }
            if (!UnitFilter.SignatureAvailable || !ResourceFilter.SignatureAvailable) { return "signature_unavailable"; }
            if (UnitFilter.Failed > 0 || ResourceFilter.Failed > 0) { return "failed"; }
            if (UnitFilter.Succeeded == 0 || ResourceFilter.Succeeded == 0) { return "no_successful_reads"; }
            return "complete";
        }

        /// <summary>
        /// Filter status for the whole observation. Counts are per EXPLORED TILE, so they are the
        /// same whether or not any unit/resource exists - the status can never leak what is hidden.
        /// </summary>
        internal static string VisibilityFilterStatusJson()
        {
            var o = new JsonObj();
            o.Raw("unit", FilterStatJson(UnitFilter));
            o.Raw("resource", FilterStatJson(ResourceFilter));
            o.Str("snapshotStatus", FilterSnapshotStatus());
            return o.Close();
        }

        /// <summary>
        /// Distribution-independent permission summary: which resource categories this player may
        /// see at all. The game's own `GameLogicData.IsResourceVisibleToPlayer(type, player, gs)`
        /// reads only the player's tech/tribe and the data tables - never the map - so publishing
        /// the summary cannot reveal where anything is, which is exactly why it is allowed while
        /// per-tile markers (hiddenByTech / requiredTech) are not.
        /// </summary>
        private string ResourceCategoryPermissions(object gs, byte player)
        {
            var o = new JsonObj();
            var gl = GameLogicDataOf(gs);
            var resType = FindType("Il2CppPolytopia.Data.ResourceData+Type")
                ?? FindGameType("ResourceData+Type");
            var ps = PlayerStateOf(gs, player);
            string seen = "";
            var m = gl == null ? null : FindInstanceMethodUnique(gl.GetType(),
                "IsResourceVisibleToPlayer", new[] { "Type", "PlayerState", "GameState" }, "Boolean",
                out seen);
            if (gl == null || resType == null || ps == null || m == null)
            {
                o.Str("status", "unavailable");
                o.Str("reason", (gl == null ? "no GameLogicData instance; " : "")
                    + (resType == null ? "ResourceData+Type not resolved; " : "")
                    + (ps == null ? "no controlled PlayerState; " : "")
                    + (m == null ? "IsResourceVisibleToPlayer not uniquely matched; saw=[" + seen + "]" : ""));
                return o.Close();
            }
            o.Str("basis", "GameLogicData instance of THIS GameState ("
                + gl.GetType().FullName + "); method invoked on that instance");
            var vis = new JsonArr();
            var hid = new JsonArr();
            var unknownCat = new JsonArr();
            int total = 0, definite = 0;
            foreach (var name in Enum.GetNames(resType))
            {
                if (name == "None") { continue; }
                object val;
                try { val = Enum.Parse(resType, name); } catch { continue; }
                total++;
                var v = InvokeBool(m, gl, new object[] { val, ps, gs });
                if (v == null)
                {
                    // Unknown is NOT hidden and NOT false: it must not be published either way.
                    unknownCat.Raw(Quote(name));
                    continue;
                }
                definite++;
                (v.Value ? vis : hid).Raw(Quote(name));
            }
            // "ok" requires EVERY category to have produced a definite bool.
            bool complete = total > 0 && definite == total;
            o.Str("status", complete ? "ok" : (definite == 0 ? "unavailable" : "partial"));
            o.Raw("visibleCategories", vis.Close());
            o.Raw("hiddenCategories", hid.Close());
            o.Raw("unknownCategories", unknownCat.Close());
            o.Raw("categoryCount", total.ToString(CultureInfo.InvariantCulture));
            o.Raw("definiteCount", definite.ToString(CultureInfo.InvariantCulture));
            if (!complete)
            {
                o.Str("reason", "only " + definite.ToString(CultureInfo.InvariantCulture) + "/"
                    + total.ToString(CultureInfo.InvariantCulture)
                    + " categories yielded a definite bool; unknown is not treated as hidden");
            }
            return o.Close();
        }

        /// <summary>
        /// Read-only terminal judgement via GameState.TryGetWinner(out PlayerState).
        /// The honest limits are part of the contract:
        ///   * the overload must really be `bool TryGetWinner(out PlayerState)` - arity plus a bool
        ///     return is not enough, so the by-ref element type is checked explicitly;
        ///   * false does NOT prove the game is ongoing and does not prove it is not over;
        ///   * unknown identities never become a verdict, and unknown wipe state is never reported
        ///     as "confirmed alive";
        ///   * side effects of the native body are unverified, so this command stays audit-only
        ///     (it is deliberately absent from DecisionCommands/AdapterCommands).
        /// </summary>
        private string WinnerReport()
        {
            var w = new WinnerInput();
            var gs = GameStateInstance();
            if (gs == null) { return "{\"ok\":false,\"error\":\"no active game\"}"; }

            var identity = ResolveControlledIdentity(gs);
            string meStr = identity.Verified ? identity.Id.ToString(CultureInfo.InvariantCulture) : null;
            string phase = Str(Val(_currentStateProp, gs));
            w.Phase = phase.Length == 0 ? null : phase;
            w.ControlledId = meStr;
            w.ControlledIdentityVerified = identity.Verified;
            w.ControlledIdentitySource = identity.Source;

            MethodInfo m = null;
            foreach (var name in new[] { "TryGetWinner", "TryGetWinnerDefault" })
            {
                foreach (var cand in gs.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (cand.Name != name) { continue; }
                    var ps = cand.GetParameters();
                    // Must be exactly one by-ref parameter whose element type is PlayerState.
                    if (ps.Length != 1 || !ps[0].ParameterType.IsByRef) { continue; }
                    if (cand.ReturnType != typeof(bool)) { continue; }
                    var elem = ps[0].ParameterType.GetElementType();
                    if (elem == null || elem.Name != "PlayerState") { continue; }
                    m = cand;
                    break;
                }
                if (m != null) { break; }
            }
            if (m == null)
            {
                w.SignatureOk = false;
                w.Signature = "none";
                return BuildWinnerResponse(w);
            }
            w.SignatureOk = true;
            var psig = m.GetParameters()[0];
            w.Signature = (m.IsStatic ? "static " : "") + m.ReturnType.Name + " " + m.Name + "("
                + (psig.IsOut ? "out " : "ref ") + psig.ParameterType.GetElementType().Name + ")";

            object winner = null;
            try
            {
                var args = new object[] { null };
                var ret = m.Invoke(gs, args);
                winner = args[0];
                if (ret != null) { w.WinnerReadable = Convert.ToBoolean(ret); }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                w.InvokeError = inner.GetType().Name + ": " + inner.Message;
            }
            if (w.WinnerReadable == true) { w.WinnerFound = true; }
            w.WinnerId = winner == null ? null : SafeScalar(MemberWithAccessor(winner, "Id"));

            try
            {
                var players = AsEnumerable(Val(_playersProp, gs));
                if (players != null)
                {
                    foreach (var ps in players)
                    {
                        if (ps == null) { continue; }
                        if (SafeScalar(MemberWithAccessor(ps, "Id")) != meStr) { continue; }
                        var v = MemberWithAccessor(ps, "wipedAtCommandIndex");
                        if (v != null) { w.Wiped = Convert.ToInt32(v) >= 0; }
                        break;
                    }
                }
            }
            catch { }
            // Evidence (codex-dsh-005, native disassembly): TryGetWinner is NOT read-only. The
            // Default/V90 path reaches RecalculatePlayerStateValues, which writes
            // PlayerState.capitalCountCache (0x4F2BFB) for every player, and it can throw on a
            // null Settings or an empty PlayerStates list. It therefore stays audit-only.
            w.SideEffectFree = false;
            w.SideEffectNote = "writes PlayerState.capitalCountCache for every player "
                + "(GetPlayersSortedByRank -> RecalculatePlayerStateValues); can throw on null "
                + "Settings or empty PlayerStates";
            w.GameVersion = Num(MemberWithAccessor(gs, "Version"));
            var settings = MemberWithAccessor(gs, "Settings");
            if (settings != null)
            {
                w.GameType = SafeScalar(MemberWithAccessor(settings, "GameType"));
                var rules = MemberWithAccessor(settings, "rules");
                if (rules != null)
                {
                    var parts = new List<string>();
                    foreach (var f in new[] { "TurnLimit", "ScoreLimit", "WinByCapital", "WinByExtermination" })
                    {
                        var v = SafeScalar(MemberWithAccessor(rules, f));
                        parts.Add(f + "=" + (v ?? "?"));
                    }
                    w.RulesSummary = string.Join(",", parts.ToArray());
                }
            }
            return BuildWinnerResponse(w);
        }

        /// <summary>
        /// Own-scoped fingerprint, v2. It carries the per-entity detail the confirmation predicates
        /// need (each unit's own coordinates/type/hp/flags, each improvement's owner/type/level/pop)
        /// so an action can be tied to the specific unit, tile, tech or city it named - a whole-army
        /// summary could not do that.
        ///
        /// The controlled identity is PASSED IN, never re-resolved: end_turn switches the acting
        /// player, and re-resolving here would silently switch the viewpoint mid-command.
        /// </summary>
        private string ActionFingerprint(object gs, ControlledIdentity identity)
        {
            string meStr = identity.Verified
                ? identity.Id.ToString(CultureInfo.InvariantCulture) : null;
            string turn = Num(Val(_currentTurnProp, gs));
            string acting = CurrentPlayerId(gs).ToString(CultureInfo.InvariantCulture);
            string stars = null;
            string techs = null;
            var players = AsEnumerable(Val(_playersProp, gs));
            if (players != null && meStr != null)
            {
                foreach (var p in players)
                {
                    if (p == null) { continue; }
                    if (SafeScalar(MemberWithAccessor(p, "Id")) != meStr) { continue; }
                    stars = SafeScalar(MemberWithAccessor(p, "Currency"));
                    techs = OwnedTechFingerprint(ReadPlayerOwnedTechNames(p));
                    break;
                }
            }

            var units = new StringBuilder();
            var imps = new StringBuilder();
            var res = new StringBuilder();
            var roads = new StringBuilder();
            // Record the terrain of MY revealed tiles (`owner == me`, revealed only). This is the
            // same public read `world` uses, kept own-scoped like `res`, and it is what lets a
            // terrain builds (ClearForest/GrowForest/BurnForest) be confirmed on their named tile.
            // A tile whose terrain cannot be read is simply not recorded: absence therefore means
            // "not readable", never a terrain value.
            var terrains = new StringBuilder();
            var publicTerrains = new StringBuilder();
            var map = Val(_mapProp, gs);
            object tilesRaw = map == null ? null
                : (MemberWithAccessor(map, "Tiles"));
            var gate = BuildFogGate(map, tilesRaw, identity.Verified ? identity.Id : (byte)0);
            var tiles = tilesRaw == null ? null : AsEnumerable(tilesRaw);
            if (tiles != null && meStr != null)
            {
                foreach (var tile in tiles)
                {
                    if (tile == null) { continue; }
                    if (!gate.Explored(tile)) { continue; }
                    string coord = SafeScalar(MemberWithAccessor(tile, "coordinates"));
                    if (coord == null) { continue; }
                    if (gate.HasVisible && gate.Visible(tile))
                    {
                        string disclosedTerrain = SafeScalar(MemberWithAccessor(tile, "terrain"));
                        if (!string.IsNullOrEmpty(disclosedTerrain) && disclosedTerrain != "null")
                            publicTerrains.Append(coord).Append(':').Append(disclosedTerrain).Append(';');
                    }
                    if (SafeScalar(MemberWithAccessor(tile, "owner")) == meStr)
                    {
                        bool? nativeRoad = ReadBoolPropertyOrGetter(tile, "HasRoad");
                        if (nativeRoad.HasValue) roads.Append(coord).Append(':').Append(nativeRoad.Value ? "true" : "false").Append(';');
                    }
                    bool filtered;
                    var unit = TileUnitVisible(tile, gs, identity.Id, out filtered);
                    if (unit != null && filtered)
                    {
                        units.Append(SafeScalar(MemberWithAccessor(unit, "id"))).Append(':')
                             .Append(SafeScalar(MemberWithAccessor(unit, "owner"))).Append(':')
                             .Append(SafeScalar(MemberWithAccessor(unit, "type"))).Append(':')
                             .Append(coord).Append(':')
                             .Append(SafeScalar(MemberWithAccessor(unit, "health"))).Append(':')
                             .Append(SafeScalar(MemberWithAccessor(unit, "moved"))).Append(':')
                             .Append(SafeScalar(MemberWithAccessor(unit, "attacked"))).Append(';');
                    }
                    var imp = MemberWithAccessor(tile, "improvement");
                    if (imp != null)
                    {
                        imps.Append(coord).Append(':')
                            .Append(SafeScalar(MemberWithAccessor(tile, "owner"))).Append(':')
                            .Append(SafeScalar(MemberWithAccessor(imp, "type"))).Append(':')
                            .Append(SafeScalar(MemberWithAccessor(imp, "level"))).Append(':')
                            .Append(SafeScalar(MemberWithAccessor(imp, "population"))).Append(':');
                        var chosen = SafeScalar(MemberWithAccessor(tile, "owner")) == meStr
                            ? AsEnumerable(MemberWithAccessor(imp, "rewards")) : null;
                        if (chosen == null) imps.Append('?');
                        else
                        {
                            bool firstReward = true;
                            foreach (var reward in chosen)
                            {
                                if (!firstReward) imps.Append(',');
                                firstReward = false;
                                imps.Append(SafeScalar(reward));
                            }
                        }
                        bool? roadFlag = SafeScalar(MemberWithAccessor(tile, "owner")) == meStr
                            ? ReadBoolPropertyOrGetter(tile, "HasRoad") : null;
                        imps.Append(':').Append(roadFlag.HasValue ? (roadFlag.Value ? "true" : "false") : "?").Append(';');
                    }
                    // Resources matter for the build/harvest confirmation (a harvest consumes the
                    // resource on the target tile). Own tiles only: the fingerprint stays own-scoped.
                    if (SafeScalar(MemberWithAccessor(tile, "owner")) == meStr)
                    {
                        bool rfSig = ResourceFilter.SignatureAvailable;
                        int rfLa = ResourceFilter.LookupAttempted, rfAt = ResourceFilter.Attempted;
                        int rfSc = ResourceFilter.Succeeded, rfFa = ResourceFilter.Failed;
                        bool resFiltered;
                        var resource = TileResourceVisible(tile, gs, identity.Id, out resFiltered);
                        ResourceFilter.SignatureAvailable = rfSig;
                        ResourceFilter.LookupAttempted = rfLa; ResourceFilter.Attempted = rfAt;
                        ResourceFilter.Succeeded = rfSc; ResourceFilter.Failed = rfFa;
                        if (resource != null && resFiltered)
                        {
                            res.Append(coord).Append(':')
                               .Append(SafeScalar(MemberWithAccessor(resource, "type"))).Append(';');
                        }
                        // Same public terrain read `world` reports. Unreadable -> not recorded.
                        string terrain = SafeScalar(MemberWithAccessor(tile, "terrain"));
                        if (!string.IsNullOrEmpty(terrain) && terrain != "null")
                        {
                            terrains.Append(coord).Append(':').Append(terrain).Append(';');
                        }
                    }
                }
            }
            int? pend = meStr == null ? null : PendingTriggerCountFor(gs, identity.Id, new JsonArr());
            bool? pop = null;
            try { pop = PopupIsOpen(); } catch { }
            // How much of the map this player has revealed. Needed because one city reward
            // (CityReward.Explorer) has no other visible effect: without it that choice could never
            // be told apart from "nothing happened".
            string explored = null;
            if (tiles != null)
            {
                int n = 0;
                foreach (var t in tiles)
                {
                    if (t == null) { continue; }
                    try { if (gate.Explored(t)) { n++; } } catch { }
                }
                explored = n.ToString(CultureInfo.InvariantCulture);
            }
            return Fingerprint(meStr, turn, acting,
                pend == null ? null : pend.Value.ToString(CultureInfo.InvariantCulture),
                stars, techs, pop == null ? null : (pop.Value ? "true" : "false"),
                units.ToString(), imps.ToString(), res.ToString(), explored, terrains.ToString(), roads.ToString())
                + "|publicTerrains=" + publicTerrains.ToString();
        }

        /// <summary>Read-only snapshot of what a turn is made of: the local player's units (id + tile),
        /// cities (level/population/production), resource tiles, and the live enum names for
        /// unit/resource/improvement types. All from live game objects - no screenshots.
        ///
        /// Fog contract (see FogGate): a tile that was never explored is omitted entirely, and
        /// on an explored-but-dim tile the durable features are kept while transient ones -
        /// other players' units, live population - are withheld.
        /// </summary>
        private string WorldDump()
        {
            var gs = GameStateInstance();
            if (gs == null) { return "{\"ok\":false,\"error\":\"no active game\"}"; }
            var o = new JsonObj();
            o.Bool("ok", true);
            object observedTurnValue = Val(_currentTurnProp, gs);
            o.Raw("turn", Num(observedTurnValue));
            o.Raw("playerIndex", Num(Val(_currentPlayerIndexProp, gs)));
            // The fog gate is keyed to the CONTROLLED player, whose identity must be verified
            // rather than guessed. Without it there is no honest private view to give, so the
            // response carries nothing private instead of falling back to "some human".
            var identity = ResolveControlledIdentity(gs);
            if (!identity.Verified)
            {
                o.Bool("controlledIdentityVerified", false);
                o.Str("error", "controlled player identity unverified; private observation withheld");
                o.Str("identityProblem", identity.Problem);
                return o.Close();
            }
            byte me = identity.Id;
            byte acting = CurrentPlayerId(gs);
            o.Bool("controlledIdentityVerified", true);
            o.Str("controlledIdentitySource", identity.Source);
            // A monotonic revision for THIS observation stream. A consumer must apply observations
            // in revision order and treat repeats as no-ops.
            long observedRevision = ++_observationRevision;
            o.Raw("observationRevision", observedRevision.ToString(CultureInfo.InvariantCulture));
            // Episode identity: an opaque, locally generated random id. It is deliberately
            // unrelated to the map seed (a seed is not proven player knowledge and could be used to
            // derive the map), and an unsalted seed hash would not be acceptable either. Stability
            // comes from the GameState instance lifetime: a save reload gives a new instance, hence
            // a new episode, which conservatively resets any memory built on the old one. The
            // native instance identity is only compared internally (managed reference fallback
            // conservatively resets when native identity is unavailable) and is never emitted;
            // no seed and no native pointer reaches the response or any log.
            string episode = EpisodeId(gs);
            if (episode == null) { o.Raw("matchContext", "null"); }
            else
            {
                o.Str("episodeId", episode);
                o.Str("matchContext", episode);      // compatibility alias, same opaque value
            }
            // Recompute the filter statistics for THIS call.
            ResetFilterStats();
            o.Raw("localPlayerId", me.ToString(CultureInfo.InvariantCulture));
            o.Raw("currentPlayerId", acting.ToString(CultureInfo.InvariantCulture));
            bool myTurn = me == acting;
            o.Bool("myTurn", myTurn);

            // --- "may I still act" is judged from the game's own signals ----------------
            // Reads that fail leave the input null, and CanActOf turns any null into a null
            // answer: an unreadable rule must never read as permission to act.
            var unknown = new JsonArr();
            var unknownSet = new HashSet<string>();
            // Only the controlled player's OWN pending triggers are reported: a global trigger list
            // can contain other players' pending rewards, and even their count is private.
            int? pendingMine = PendingTriggerCountFor(gs, me, unknown);
            if (pendingMine == null) { o.Raw("pendingTriggerCountMine", "null"); }
            else { o.Raw("pendingTriggerCountMine", pendingMine.Value.ToString(CultureInfo.InvariantCulture)); }

            bool? popupOpen = null;
            bool? popupNeedsChoice = null;
            try
            {
                popupOpen = PopupIsOpen();
                if (popupOpen == null) { o.Raw("popupShowing", "null"); unknown.Raw("\"popupShowing\""); }
                else { o.Bool("popupShowing", popupOpen.Value); }
                popupNeedsChoice = PopupNeedsChoice();
                if (popupNeedsChoice == null) { o.Raw("popupNeedsChoice", "null"); unknown.Raw("\"popupNeedsChoice\""); }
                else { o.Bool("popupNeedsChoice", popupNeedsChoice.Value); }
            }
            catch (Exception ex)
            {
                o.Raw("popupShowing", "null");
                o.Raw("popupNeedsChoice", "null");
                unknown.Raw("\"popupShowing\"");
                unknown.Raw("\"popupNeedsChoice\"");
                Log("world: popup probe failed " + ex.Message);
            }

            string phase = Str(Val(_currentStateProp, gs));
            // The game's OWN state machine, the same value `CanActOf` judges actionability from and
            // the end screen is driven by: Unknown/Lobby/Started/FinalTurn/Ended. Reported so a
            // terminal state can be read from the game instead of inferred from "no cities" or from
            // TryGetWinner (whose side effects are not confirmed).
            if (string.IsNullOrEmpty(phase))
            {
                o.Raw("currentState", "null");
                o.Raw("matchEnded", "null");
            }
            else
            {
                o.Str("currentState", phase);
                if (phase == "Ended") { o.Bool("matchEnded", true); }
                else if (phase == "Unknown") { o.Raw("matchEnded", "null"); }
                else { o.Bool("matchEnded", false); }
            }
            string combatState = CombatStateJson(gs);
            o.Raw("state", combatState);
            bool? processing = ActionPipelineBusy(unknown);
            if (Json.GetString(combatState, "settlementStatus") != "complete") processing = true;
            string canActReason;
            string canAct = CanActOf(myTurn, phase, pendingMine, popupOpen, popupNeedsChoice,
                processing, out canActReason);
            if (canAct == null) { o.Raw("canAct", "null"); unknown.Raw("\"canAct\""); }
            else { o.Bool("canAct", canAct == "true"); }
            o.Str("canActReason", canActReason);

            var map = Val(_mapProp, gs);
            if (map == null) { o.Str("error", "no map"); return o.Close(); }
            int width = ReadMapWidth(gs);
            o.Raw("width", width > 0 ? width.ToString(CultureInfo.InvariantCulture) : "null");
            if (width <= 0) Note(unknownSet, unknown, "map.width");

            object tilesRaw = MemberWithAccessor(map, "Tiles");
            var tiles = tilesRaw == null ? null : AsEnumerable(tilesRaw);

            var gate = BuildFogGate(map, tilesRaw, me);
            // Both gates resolve to the same predicate in this build: GetPlayerVisibleTiles is
            // GetExplored (native disassembly, codex-dsh-005), and the renderer's only tile gate is
            // !GetExplored. `inSight` is therefore kept only as a compatibility name for "revealed".
            var transientGate = gate.HasVisible ? gate.Visible : gate.Explored;
            string transientSource = gate.HasVisible ? gate.VisibleSource : gate.ExploredSource;

            var units = new JsonArr();
            var cities = new JsonArr();
            var resourceTiles = new JsonArr();
            int scanned = 0, mine = 0, ownedCityCount = 0, exploredCount = 0, inSightCount = 0;
            var meStr = me.ToString(CultureInfo.InvariantCulture);
            var myCityTilesList = new List<object>();
            var myWaterTrainSites = new List<OwnedWaterTrainSite>();
            var myUnitTilesList = new List<object>();
            var observedOwnedUnitData = new List<object>();
            var ownUnitCoordinates = new HashSet<string>(StringComparer.Ordinal);
            var visibleEnemyTilesList = new List<object>();
            var cityLevels = new Dictionary<string, int>(StringComparer.Ordinal);
            var exploredCoords = new HashSet<string>(StringComparer.Ordinal);
            if (tiles != null)
            {
                foreach (var tile in tiles)
                {
                    if (tile == null) { continue; }
                    scanned++;
                    bool explored = gate.Explored(tile);
                    bool inSight = transientGate(tile);
                    if (!explored) { continue; }   // never seen: report nothing at all
                    exploredCount++;
                    if (inSight) { inSightCount++; }

                    var coord = SafeScalar(MemberWithAccessor(tile, "coordinates"));
                    var owner = SafeScalar(MemberWithAccessor(tile, "owner"));
                    bool ownTile = owner == meStr;
                    var waterTrainSite = OwnWaterTrainSite(tile, me, explored);
                    if (waterTrainSite != null) myWaterTrainSites.Add(waterTrainSite);
                    if (!string.IsNullOrEmpty(coord) && coord != "null") { exploredCoords.Add(coord); }

                    // units: mine are always on my map; anyone else's only while in sight
                    bool unitFiltered;
                    var unit = TileUnitVisible(tile, gs, me, out unitFiltered);
                    
                    if (unit != null)
                    {
                        var ownerOfUnit = SafeScalar(MemberWithAccessor(unit, "owner")) ?? owner;
                        if (unitFiltered && SafeScalar(MemberWithAccessor(unit, "owner")) == meStr)
                        {
                            myUnitTilesList.Add(tile);
                            observedOwnedUnitData.Add(URPanelData(unit,gs));
                            if (!string.IsNullOrEmpty(coord) && coord != "null") ownUnitCoordinates.Add(coord);
                        }
                        // a filtered, currently VISIBLE enemy is the only thing an attack may target
                        if (unitFiltered && ownerOfUnit != null && ownerOfUnit != "null" && ownerOfUnit != meStr) { visibleEnemyTilesList.Add(tile); }
                        if (ownerOfUnit == meStr || inSight)
                        {
                            var uo = new JsonObj();
                            uo.NullOrStr("id", SafeScalar(MemberWithAccessor(unit, "id")));
                            uo.NullOrStr("type", SafeScalar(MemberWithAccessor(unit, "type")));
                            uo.NullOrStr("owner", ownerOfUnit);
                            uo.NullOrStr("coords", coord);
                            uo.NullOrStr("health", SafeScalar(MemberWithAccessor(unit, "health")));
                            // Own XP remains available for settlement proofs. Opponent XP is
                            // disclosed only when the verified native panel displays its progress;
                            // veteran/Static units do not reveal a lifetime kill counter.
                            string xpValue = ownerOfUnit == meStr ? SafeScalar(MemberWithAccessor(unit, "xp"))
                                : SafeScalar(URPublicKills(unit, gs));
                            if (xpValue == null && ownerOfUnit == meStr) { Note(unknownSet, unknown, "unit.xp"); }
                            uo.NullOrStr("xp", xpValue);
                            // Action permission only exists on my own units: the screen never
                            // shows an opponent's remaining moves, so those fields are null.
                            if (UnitActionFieldsVisible(ownerOfUnit, meStr))
                            {
                                // Real JSON booleans or null: a string "False" is truthy in Python.
                                // Each unreadable field is also named in unknownFields.
                                string maxHp = SafeScalar(URUnitRule("GetMaxHealth", unit, gs));
                                if (maxHp == null) { Note(unknownSet, unknown, "unit.maxHealth"); }
                                string promo = SafeScalar(MemberWithAccessor(unit, "promotionLevel"));
                                if (promo == null) { Note(unknownSet, unknown, "unit.promotionLevel"); }
                                bool? movedFlag = ReadBoolMember(unit, "moved");
                                if (movedFlag == null) { Note(unknownSet, unknown, "unit.moved"); }
                                bool? attackedFlag = ReadBoolMember(unit, "attacked");
                                if (attackedFlag == null) { Note(unknownSet, unknown, "unit.attacked"); }
                                bool? canMove = ReadBoolInvoke(unit, "CanMove");
                                if (canMove == null) { Note(unknownSet, unknown, "unit.canMove"); }
                                bool? canAttack = ReadBoolInvoke(unit, "CanAttack");
                                if (canAttack == null) { Note(unknownSet, unknown, "unit.canAttack"); }
                                UnitActionFields(uo, movedFlag, attackedFlag, canMove, canAttack, promo, maxHp);
                                uo.Bool("inSight", true);
                            }
                            else
                            {
                                // Another player's remaining moves are not on screen: null, not false.
                                UnitActionFields(uo, null, null, null, null, null, null);
                                uo.Bool("inSight", true);
                            }
                            uo.Bool("currentlyVisible", unitFiltered);
                            uo.Raw("lastObservedRevision", observedRevision.ToString(CultureInfo.InvariantCulture));
                            uo.Raw("lastObservedTurn", Num(observedTurnValue));
                            AppendNativeUnitFields(uo, unit, gs, ownerOfUnit == meStr);
                            units.Raw(uo.Close());
                        }
                        // else: a hidden unit is not counted anywhere - even its number is
                        // information the player does not have.
                    }

                    // cities: a tile whose improvement is a city for this player. The location and
                    // owner are remembered under the dim layer; live numbers are not.
                    var imp = MemberWithAccessor(tile, "improvement");
                    if (imp != null)
                    {
                        var impType = SafeScalar(MemberWithAccessor(imp, "type"));
                        var co = new JsonObj();
                        co.NullOrStr("coords", coord);
                        co.NullOrStr("owner", owner);
                        co.Bool("isMine", ownTile);
                        co.NullOrStr("improvementType", impType);
                        if(ownTile)
                        {
                            co.Raw("ownedBuildingState",CymantiOwnedBuildingState(tile)??"null");
                        }
                        co.Bool("inSight", inSight);
                        if (ownTile || inSight)
                        {
                            co.NullOrStr("level", SafeScalar(MemberWithAccessor(imp, "level")));
                            co.NullOrStr("population", SafeScalar(MemberWithAccessor(imp, "population")));
                        }
                        cities.Raw(co.Close());
                        if (ownTile)
                        {
                            mine++;
                            var lvl = SafeScalar(MemberWithAccessor(imp, "level"));
                            int lv;
                            if (!string.IsNullOrEmpty(coord) && lvl != null && lvl != "null"
                                && int.TryParse(lvl, NumberStyles.Integer, CultureInfo.InvariantCulture, out lv))
                            { cityLevels[coord] = lv; }
                            // Same validated object the standard observation just used: explored,
                            // improvement City, owner == me. Reused for train options - never a scan.
                            if (string.Equals(impType, "City", StringComparison.OrdinalIgnoreCase))
                            { myCityTilesList.Add(tile); ownedCityCount++; }
                        }
                    }

                    bool resFiltered;
                    var res = TileResourceVisible(tile, gs, me, out resFiltered);
                    
                    if (res != null)
                    {
                        var ro = new JsonObj();
                        ro.NullOrStr("coords", coord);
                        ro.NullOrStr("resourceType", SafeScalar(MemberWithAccessor(res, "type")));
                        ro.NullOrStr("owner", owner);
                        ro.Bool("inSight", inSight);
                        resourceTiles.Raw(ro.Close());
                    }
                }
            }
            o.Raw("tilesScanned", scanned.ToString(CultureInfo.InvariantCulture));
            o.Raw("tilesExplored", exploredCount.ToString(CultureInfo.InvariantCulture));
            o.Raw("tilesInSight", inSightCount.ToString(CultureInfo.InvariantCulture));
            // NOTE: no hidden-tile or hidden-unit count is emitted. Those aggregates leak the
            // existence of things the player cannot see; they belong to the audit command only.
            o.Raw("myCityTiles", ownedCityCount.ToString(CultureInfo.InvariantCulture));
            o.Raw("myImprovementCount", mine.ToString(CultureInfo.InvariantCulture));
            o.Str("schema", "decision-world-3");
            o.Raw("visibilitySemantics", VisibilitySemanticsJson());
            o.Raw("units", units.Close());
            o.Raw("improvements", cities.Close());
            o.Raw("resources", resourceTiles.Close());
            var fog = new JsonObj();
            fog.Str("exploredSource", gate.ExploredSource);
            fog.Str("visibleSource", gate.VisibleSource);
            fog.Str("transientSource", transientSource);
            fog.Raw("testErrors", gate.Errors.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(gate.Note)) { fog.Str("note", gate.Note); }
            o.Raw("fog", fog.Close());

            // Own public economy and technology. Read one controlled player and one
            // technology collection; reuse the exact known set for research filtering.
            var controlledPlayer = PlayerStateOf(gs, me);
            object currency;
            o.NullOrStr("stars", TryReadNativeMember(controlledPlayer, "Currency", out currency) ? SafeScalar(currency) : null);
            var ownedNames = AppendOwnedTechFields(o, controlledPlayer);
            if (ownedNames == null) Note(unknownSet, unknown, "ownedTechs");
            o.Raw("unknownFields", unknown.Close());
            // Constant filter-availability status. Emitted whether or not any unit/resource exists,
            // so it can never itself reveal that something is hidden; and it is what tells a reader
            // that an empty list means "withheld / nothing disclosable", never "the map is empty".
            o.Raw("visibilityFilters", VisibilityFilterStatusJson());
            o.Str("completeness", FilterSnapshotStatus());
            o.Str("resourceVisibilityAuthority", "TileData.GetResource(gameState, playerId)");
            o.Str("hiddenEntityPolicy", "hidden_entities_never_reported");
            // Global, map-independent permission summary (safe); per-tile markers are not.
            o.Raw("resourceCategoryPermissions", ResourceCategoryPermissions(gs, me));
            // Player-visible researchable set with the game's own prices (never computed here).
            o.Raw("researchableTechs", ResearchableTechs(gs, controlledPlayer, ownedNames));
            o.Raw("tribeCatalog", ProjectTribeCatalog(GameLogicDataOf(gs), controlledPlayer, gs, observedOwnedUnitData));
            // My own visible cities only: the city entries already produced by the standard
            // observation (explored + improvement City + owner == mine) are reused, never a scan.
            o.Raw("trainOptions", TrainOptionsForSites(gs, PlayerStateOf(gs, me), myCityTilesList, myWaterTrainSites));
            o.Raw("trainLimits", TrainLimits(gs, myCityTilesList, cityLevels));
            if (canAct == "true")
            { o.Raw("legalMoveTargets", LegalMoveTargets(gs, PlayerStateOf(gs, me), me, myUnitTilesList, exploredCoords)); }
            else
            { o.Raw("legalMoveTargets", "{\"status\":\"unavailable\",\"units\":[],\"reasonCodes\":[\"not-currently-actionable\"]}"); }
            // attack candidates come from the same UI chain and may only target currently VISIBLE enemies
            var visibleEnemyCoords = new HashSet<string>(StringComparer.Ordinal);
            foreach (var et in visibleEnemyTilesList)
            {
                var ec = et == null ? null : SafeScalar(MemberWithAccessor(et, "coordinates"));
                if (!string.IsNullOrEmpty(ec) && ec != "null") { visibleEnemyCoords.Add(ec); }
            }
            if (canAct == "true")
            { o.Raw("legalAttackTargets", LegalAttackTargets(gs, PlayerStateOf(gs, me), me, true, myUnitTilesList, visibleEnemyCoords)); }
            else
            { o.Raw("legalAttackTargets", "{\"status\":\"unavailable\",\"units\":[],\"reasonCodes\":[\"not-currently-actionable\"]}"); }
            // Public scene FX are their own domains. They never extend the explored tile roster.
            var publicMarkers = PublicMarkerWorld(gs, episode, me,
                PolytopiaPublicMarkers.PublicMarkerReader.StrictInt(observedTurnValue), observedRevision);
            o.Raw("publicSiegeMarkers", publicMarkers.Siege);
            o.Raw("publicRuinRainbowMarkers", publicMarkers.Rainbow);
            // enemy cities: everything owned by someone else - but only where I have actually been
            var enemy = new JsonArr();
            var neutral = new JsonArr();
            int enemyCount = 0, neutralCount = 0;
            if (tiles != null)
            {
                foreach (var tile in tiles)
                {
                    if (tile == null) { continue; }
                    if (!gate.Explored(tile)) { continue; }
                    var imp2 = MemberWithAccessor(tile, "improvement");
                    if (imp2 == null) { continue; }
                    var impT = SafeScalar(MemberWithAccessor(imp2, "type"));
                    if (!string.Equals(impT, "City", StringComparison.OrdinalIgnoreCase)) { continue; }
                    var own = SafeScalar(MemberWithAccessor(tile, "owner"));
                    if (own == null || own == meStr) { continue; }
                    var eo = new JsonObj();
                    eo.NullOrStr("coords", SafeScalar(MemberWithAccessor(tile, "coordinates")));
                    eo.NullOrStr("owner", own);
                    eo.Bool("inSight", transientGate(tile));
                    if (own == "0") { neutral.Raw(eo.Close()); neutralCount++; }
                    else if (own != "255") { enemy.Raw(eo.Close()); enemyCount++; }
                }
            }
            o.Raw("enemyCities", enemy.Close());
            o.Raw("enemyCityCount", enemyCount.ToString(CultureInfo.InvariantCulture));
            o.Raw("neutralVillages", neutral.Close());
            o.Raw("neutralVillageCount", neutralCount.ToString(CultureInfo.InvariantCulture));

            // compact per-tile terrain, so a planner can reason about water / visibility
            // without asking the game one tile at a time. Unexplored tiles are absent, not blank.
            var tileArr = new JsonArr();
            var visibleStyles = CreateVisibleStyleContext(gs);
            if (tiles != null)
            {
                foreach (var tile in tiles)
                {
                    if (tile == null) { continue; }
                    if (!gate.Explored(tile)) { continue; }
                    var to = new JsonObj();
                    to.NullOrStr("c", SafeScalar(MemberWithAccessor(tile, "coordinates")));
                    to.NullableBool("water", ReadBoolMember(tile, "IsWater"));
                    to.NullableBool("land", ReadBoolMember(tile, "IsLand"));
                    to.Bool("inSight", transientGate(tile));
                    to.Bool("explored", true);
                    to.Raw("currentlyVisible", "null");
                    to.Raw("lastObservedRevision", observedRevision.ToString(CultureInfo.InvariantCulture));
                    to.Raw("lastObservedTurn", Num(observedTurnValue));
                    string publicTerrain=SafeScalar(MemberWithAccessor(tile,"terrain"));
                    to.NullOrStr("terrain", publicTerrain);
                    to.NullableBool("ice",publicTerrain==null?(bool?)null:publicTerrain=="Ice");
                    bool ownTileEffects = SafeScalar(MemberWithAccessor(tile, "owner")) == me.ToString(CultureInfo.InvariantCulture);
                    to.Raw("terrainEffects", ownTileEffects ? URScalars(MemberWithAccessor(tile, "effects")) : "null");
                    to.Str("terrainEffectsScope", ownTileEffects ? "owned-explored-tile" : "withheld");
                    bool ownUnitFloodTile = ownUnitCoordinates.Contains(SafeScalar(MemberWithAccessor(tile, "coordinates")) ?? "");
                    bool publicFloodVisible = ownTileEffects || ownUnitFloodTile || (gate.HasVisible && gate.Visible(tile));
                    var publicFlood = publicFloodVisible ? PublicFloodState(tile) : null;
                    to.NullableBool("flooded", publicFlood == null ? (bool?)null : publicFlood.Flooded);
                    to.Str("floodedScope", ownTileEffects ? "owned-explored-tile" : ownUnitFloodTile ? "own-unit-tile" : publicFloodVisible ? "visible-explored-tile" : "withheld");
                    to.NullableBool("algae",CymantiPublicAlgae(tile,publicFloodVisible));
                    to.Str("algaeScope",ownTileEffects ? "owned-explored-tile" : ownUnitFloodTile ? "own-unit-tile" : publicFloodVisible ? "visible-explored-tile" : "withheld");
                    to.Raw("visualStyle", visibleStyles.Read(tile, true));
                    to.NullOrStr("owner", SafeScalar(MemberWithAccessor(tile, "owner")));
                    tileArr.Raw(to.Close());
                }
            }
            o.Raw("tiles", tileArr.Close());

            // Exact native data enums, including special tribes and tile effects.
            o.Raw("enums", NativeEnumCatalog());
            return o.Close();
        }

        /// <summary>WorldCoordinates for an x,y pair, via the confirmed FromIndex factory.</summary>
        private object BuildCoordinates(int x, int y, object gs)
        {
            object map; int index, width;
            return TryMapIndex(gs, x, y, out map, out index, out width)
                ? WorldCoordinatesFromIndex(index, width) : null;
        }

        /// <summary>
        /// The UnitState standing at "x,y", belonging to the player whose turn it is.
        /// Used to feed MoveCommand, which needs the live unit object.
        /// </summary>
        private object FindUnitAt(object gs, string coords)
        {
            int x, y;
            if (!TryParseCoords(coords, out x, out y)) { return null; }
            try
            {
                byte player = CurrentPlayerId(gs);
                if (player == 0 || player == NaturePlayerId || !PrivateSamplingAllowed(gs)) return null;
                object tile = TileAtCoordinates(gs, x, y);
                if (tile == null) { return null; }
                bool filtered;
                object unit = TileUnitVisible(tile, gs, player, out filtered);
                return filtered && unit != null && NativeInt32(MemberWithAccessor(unit, "owner")) == player ? unit : null;
            }
            catch (Exception ex) { Log("action: FindUnitAt failed " + (ex.InnerException ?? ex).Message); }
            return null;
        }

        /// <summary>
        /// List the members of a live enum by name, walking the loaded assemblies. Used to name
        /// an argument (e.g. UpgradeCommand's Type) from the running game instead of the dump.
        /// Read-only.
        /// </summary>
        private string EnumDump(string name)
        {
            var o = new JsonObj();
            o.Bool("ok", false);
            o.Str("wanted", name);
            var hits = new JsonArr();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] ts;
                try { ts = asm.GetTypes(); } catch { continue; }
                foreach (var t in ts)
                {
                    if (t == null || !t.IsEnum) { continue; }
                    var full = t.FullName ?? t.Name;
                    if (full.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                    var vals = new JsonArr();
                    try { foreach (var v in Enum.GetNames(t)) { vals.Raw("\"" + Esc(v) + "\""); } } catch { }
                    var ho = new JsonObj();
                    ho.Str("type", full);
                    ho.Raw("values", vals.Close());
                    hits.Raw(ho.Close());
                }
            }
            o.Raw("enums", hits.Close());
            o.Bool("ok", true);
            return o.Close();
        }

        /// <summary>
        /// Members of a live type (methods with arity, properties), optionally filtered by
        /// substring. Used to find the real click entry point on a UI control.
        /// Read-only.
        /// </summary>
        private string TypeInfo(string name, string filter)
        {
            var o = new JsonObj();
            o.Bool("ok", false);
            o.Str("wanted", name);
            if (name.Length == 0) { o.Str("error", "typeinfo requires \"name\""); return o.Close(); }

            Type t = FindType(name) ?? FindType("Il2Cpp." + name) ?? FindTypeBySuffix(name);
            if (t == null)
            {
                // last resort: leaf-name match on classes
                var leaf = name.IndexOf('+') >= 0 ? name.Substring(name.LastIndexOf('+') + 1) : name;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] ts;
                    try { ts = asm.GetTypes(); } catch { continue; }
                    foreach (var cand in ts)
                    {
                        if (cand != null && cand.Name.Equals(leaf, StringComparison.Ordinal)) { t = cand; break; }
                    }
                    if (t != null) { break; }
                }
            }
            if (t == null) { o.Str("error", "type not found"); return o.Close(); }

            o.Str("type", t.FullName ?? t.Name);
            o.Str("baseType", t.BaseType == null ? null : t.BaseType.FullName);
            var methods = new JsonArr();
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (filter.Length > 0 && m.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                var ps = m.GetParameters();
                methods.Raw("\"" + Esc((m.IsStatic ? "static " : "") + m.Name + "(" + ps.Length + ")"
                    + (ps.Length > 0 ? ":" + ps[0].ParameterType.Name : "")) + "\"");
            }
            o.Raw("methods", methods.Close());
            var props = new JsonArr();
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (filter.Length > 0 && p.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                props.Raw("\"" + Esc(p.Name + ":" + p.PropertyType.Name) + "\"");
            }
            o.Raw("properties", props.Close());
            var flds = new JsonArr();
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (filter.Length > 0 && f.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                flds.Raw("\"" + Esc((f.IsStatic ? "static " : "") + f.Name + ":" + f.FieldType.Name) + "\"");
            }
            o.Raw("fields", flds.Close());
            o.Bool("ok", true);
            return o.Close();
        }

        // ------------------------------------------------ new game with real opponents

        private static MethodInfo FindObjectsMethod()
        {
            var objType = FindType("UnityEngine.Object");
            foreach (var holder in new[] { FindType("UnityEngine.Resources"), objType })
            {
                if (holder == null) { continue; }
                foreach (var m in holder.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "FindObjectsOfTypeAll" && m.Name != "FindObjectsOfType") { continue; }
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType.Name == "Type") { return m; }
                }
            }
            return null;
        }

        /// <summary>Live Unity objects of a type. Read-only.</summary>
        private static List<object> LiveInstances(Type t)
        {
            var found = new List<object>();
            var find = FindObjectsMethod();
            if (find == null || t == null) { return found; }
            object il2 = Il2CppTypeOf(t);
            if (il2 == null) { return found; }
            try
            {
                var raw = find.Invoke(null, new object[] { il2 });
                var en = raw as IEnumerable;
                if (en != null) { foreach (var x in en) { if (x != null) { found.Add(x); } } }
            }
            catch (Exception ex) { Log("LiveInstances " + t.Name + " failed: " + (ex.InnerException ?? ex).Message); }
            return found;
        }

        /// <summary>
        /// Set an ENUM-valued member from its name, using the member's own enum type. Returns a short
        /// explanation, so a typo or a read-only member is reported instead of silently ignored.
        /// </summary>
        private static string SetEnumMember(object target, string name, string value)
        {
            if (target == null) { return "no target"; }
            var t = target.GetType();
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var p in t.GetProperties(F))
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { continue; }
                if (!p.CanWrite) { return "property " + p.Name + " is read-only"; }
                if (!p.PropertyType.IsEnum) { return "property " + p.Name + " is not an enum"; }
                object v;
                try { v = Enum.Parse(p.PropertyType, value, true); if (!KnownRuleEnumValue(p.PropertyType,v)) return "undefined enum value"; }
                catch (Exception ex) { return "bad value '" + value + "': " + ex.Message; }
                try { p.SetValue(target, v, null); return "property " + p.Name + " -> " + v; }
                catch (Exception ex) { return "set failed: " + ex.Message; }
            }
            foreach (var m in t.GetMethods(F))
            {
                if (!string.Equals(m.Name, "set_" + name, StringComparison.OrdinalIgnoreCase)) { continue; }
                var fps = m.GetParameters();
                if (fps.Length != 1 || !fps[0].ParameterType.IsEnum) { continue; }
                try
                {
                    object enumValue=Enum.Parse(fps[0].ParameterType,value,true);
                    if (!KnownRuleEnumValue(fps[0].ParameterType,enumValue)) return "undefined enum value";
                    m.Invoke(target, new object[] { enumValue });
                    return "setter set_" + name + " -> " + value;
                }
                catch (Exception ex) { return "setter failed: " + ex.Message; }
            }
            foreach(var field in t.GetFields(F))
            {
                if (!string.Equals(field.Name,name,StringComparison.OrdinalIgnoreCase) || !field.FieldType.IsEnum || field.IsInitOnly) continue;
                try
                {
                    object enumValue=Enum.Parse(field.FieldType,value,true);
                    if (!KnownRuleEnumValue(field.FieldType,enumValue)) return "undefined enum value";
                    field.SetValue(target,enumValue); return "field " + field.Name + " -> " + enumValue;
                }
                catch(Exception ex) {return "field set failed: " + ex.Message;}
            }
            return "no writable enum member '" + name + "'";
        }

        /// <summary>Call a parameterless instance method and return its value as text (null if unreadable).</summary>
        private static string InvokeIntMember(object o, string name)
        {
            if (o == null) { return null; }
            var m = o.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (m == null) { return null; }
            try { var r = m.Invoke(o, null); return r == null ? null : r.ToString(); }
            catch (Exception) { return null; }
        }

        /// <summary>A value as a JSON literal when it is a plain scalar, otherwise null.</summary>
        private static string ScalarOrNull(object v)
        {
            var s = SafeScalar(v);
            if (s == null) { return "null"; }
            if (v is bool) { return s == "True" || s == "true" ? "true" : "false"; }
            if (v is int || v is long || v is short || v is byte) { return s; }
            return "\"" + Esc(s) + "\"";
        }

        /// <summary>Write a member by name (property first, then field). Returns the mechanism used.</summary>
        private static string SetMember(object target, string name, object value)
        {
            if (target == null) { return null; }
            var t = target.GetType();
            var pascal = char.ToUpperInvariant(name[0]) + name.Substring(1);
            foreach (var n in pascal == name ? new[] { name } : new[] { pascal, name })
            {
                var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.CanWrite)
                {
                    try { p.SetValue(target, value, null); return "property " + n; } catch { }
                }
                var f = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null)
                {
                    try { f.SetValue(target, value); return "field " + n; } catch { }
                }
            }
            return null;
        }

        /// <summary>
        /// Concrete types that declare an instance method with this name and no parameters. Used to
        /// locate the object that owns the Start Game path instead of hard-coding a UI class name.
        /// </summary>
        private static List<Type> TypesWithMethod(string method)
        {
            var found = new List<Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] ts;
                try { ts = asm.GetTypes(); } catch { continue; }
                foreach (var t in ts)
                {
                    if (t == null || t.IsAbstract) { continue; }
                    MethodInfo[] ms;
                    try { ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly); }
                    catch { continue; }
                    foreach (var m in ms)
                    {
                        if (m.Name == method && m.GetParameters().Length == 0) { found.Add(t); break; }
                    }
                }
            }
            return found;
        }

        /// <summary>
        /// Start a single-player match through the game's own setup path: take the live setup
        /// object, point its GameSettings at N opponents and a difficulty, then invoke the same
        /// CreateSinglePlayerGame() the Start Game button invokes. No clicks, and the opponents are
        /// the game's own bots - so they take real turns instead of standing in as "Nature".
        /// dry=1 changes nothing and reports what it found.
        /// </summary>
        private string NewGame(string opponentsArg, string difficultyArg, string mapSizeArg,
            string mapPresetArg, string gameTypeArg, string dryArg)
        {
            var o = new JsonObj();
            o.Bool("ok", false);
            bool dry = dryArg == "1" || dryArg.Equals("true", StringComparison.OrdinalIgnoreCase);

            var owners = TypesWithMethod("CreateSinglePlayerGame");
            var names = new JsonArr();
            foreach (var t in owners) { names.Raw("\"" + Esc(t.FullName ?? t.Name) + "\""); }
            o.Raw("setupTypes", names.Close());
            if (owners.Count == 0) { o.Str("error", "no type declares CreateSinglePlayerGame()"); return o.Close(); }

            object view = null;
            Type viewType = null;
            foreach (var t in owners)
            {
                // preferred: the game's own singleton. FindObjectsOfTypeAll hands back base-typed
                // wrappers (UnityEngine.Object), and invoking through those throws TargetException.
                var inst = InstanceOf(t);
                if (inst == null)
                {
                    var live = LiveInstances(t);
                    if (live.Count > 0) { inst = live[0]; }
                }
                if (inst != null) { view = inst; viewType = t; break; }
            }
            if (view == null) { o.Str("error", "no live instance of the setup type"); return o.Close(); }
            o.Str("setupType", viewType.FullName ?? viewType.Name);
            o.Str("instanceType", view.GetType().FullName ?? view.GetType().Name);
            o.Raw("setupInstances", LiveInstances(viewType).Count.ToString(CultureInfo.InvariantCulture));

            // the settings object the setup path itself would use
            object settings = null;
            string from = null;
            foreach (var n in new[] { "GameSettings", "gameSettings", "Settings", "settings", "m_GameSettings" })
            {
                var v = MemberWithAccessor(view, n);
                if (v != null && v.GetType().Name.IndexOf("GameSettings", StringComparison.OrdinalIgnoreCase) >= 0)
                { settings = v; from = viewType.Name + "." + n; break; }
            }
            if (settings == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] ts;
                    try { ts = asm.GetTypes(); } catch { continue; }
                    foreach (var t in ts)
                    {
                        if (t == null) { continue; }
                        var p = t.GetProperty("PreliminaryGameSettings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (p == null) { continue; }
                        try { settings = p.GetValue(null, null); } catch { settings = null; }
                        if (settings != null) { from = t.Name + ".PreliminaryGameSettings"; break; }
                    }
                    if (settings != null) { break; }
                }
            }
            if (settings == null) { o.Str("error", "GameSettings object not found on the setup path"); return o.Close(); }
            o.Str("settingsFrom", from);
            o.Str("settingsType", settings.GetType().Name);

            o.NullOrStr("beforeOpponentCount", SafeScalar(MemberWithAccessor(settings, "OpponentCount")));
            o.NullOrStr("beforeDifficulty", SafeScalar(MemberWithAccessor(settings, "Difficulty")));
            o.NullOrStr("beforeMapSize", SafeScalar(MemberWithAccessor(settings, "MapSize")));
            o.NullOrStr("beforeGameType", SafeScalar(MemberWithAccessor(settings, "GameType")));
            o.NullOrStr("beforeMapPreset", SafeScalar(MemberWithAccessor(settings, "mapPreset")));
            o.NullOrStr("beforePlayerCount", InvokeIntMember(settings, "GetPlayerCount"));
            if (dry) { o.Str("dryRun", "no change made"); o.Bool("ok", true); return o.Close(); }

            var changes = new JsonObj();
            int want;
            if (int.TryParse(opponentsArg, out want))
            {
                var how = SetMember(settings, "OpponentCount", want);
                changes.Str("OpponentCount", how ?? "failed");
            }
            if (difficultyArg.Length > 0)
            {
                var prop = settings.GetType().GetProperty("Difficulty", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object dv = null;
                try
                {
                    dv = prop != null && prop.PropertyType.IsEnum
                        ? Enum.Parse(prop.PropertyType, difficultyArg, true)
                        : (object)int.Parse(difficultyArg, CultureInfo.InvariantCulture);
                }
                catch (Exception ex)
                {
                    changes.Str("Difficulty", "parse failed: " + ex.Message);
                }
                if (dv != null) { changes.Str("Difficulty", (SetMember(settings, "Difficulty", dv) ?? "failed") + " -> " + dv); }
            }
            int ms;
            if (int.TryParse(mapSizeArg, out ms))
            {
                var how = SetMember(settings, "MapSize", ms);
                changes.Str("MapSize", how ?? "failed");
            }
            // mapPreset / GameType: the same enum-valued members the setup screen writes. Setting is
            // by the property's OWN enum type, so a wrong name fails loudly instead of silently
            // keeping the default.
            if (mapPresetArg.Length > 0)
            {
                changes.Str("mapPreset", SetEnumMember(settings, "mapPreset", mapPresetArg));
            }
            if (gameTypeArg.Length > 0)
            {
                changes.Str("GameType", SetEnumMember(settings, "GameType", gameTypeArg));
            }
            o.Raw("changes", changes.Close());
            o.NullOrStr("afterOpponentCount", SafeScalar(MemberWithAccessor(settings, "OpponentCount")));
            o.NullOrStr("afterDifficulty", SafeScalar(MemberWithAccessor(settings, "Difficulty")));
            o.NullOrStr("afterMapSize", SafeScalar(MemberWithAccessor(settings, "MapSize")));
            o.NullOrStr("afterGameType", SafeScalar(MemberWithAccessor(settings, "GameType")));
            o.NullOrStr("afterMapPreset", SafeScalar(MemberWithAccessor(settings, "mapPreset")));
            o.NullOrStr("afterPlayerCount", InvokeIntMember(settings, "GetPlayerCount"));
            var roster = MemberWithAccessor(settings, "players") as IEnumerable;
            if (roster != null)
            {
                var rl = new List<string>();
                foreach (var p in roster)
                {
                    if (p == null) { continue; }
                    rl.Add("{\"playerData\":\"" + Esc(p.GetType().Name) + "\""
                        + ",\"isHuman\":" + ScalarOrNull(MemberWithAccessor(p, "IsHuman"))
                        + ",\"isBot\":" + ScalarOrNull(MemberWithAccessor(p, "IsBot"))
                        + ",\"autoPlay\":" + ScalarOrNull(MemberWithAccessor(p, "AutoPlay"))
                        + ",\"tribe\":" + ScalarOrNull(MemberWithAccessor(p, "Tribe"))
                        + "}");
                }
                o.Raw("settingsRoster", new JsonArr().Raw(string.Join(",", rl.ToArray())).Close());
            }

            // Resolve the method from the INSTANCE's own type, not from the declaring type the
            // metadata scan found: two interop proxies can describe the same native class, and
            // invoking one proxy's MethodInfo on the other's object throws TargetException.
            MethodInfo start = null;
            foreach (var m in view.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name == "CreateSinglePlayerGame" && m.GetParameters().Length == 0) { start = m; break; }
            }
            if (start == null) { o.Str("error", "CreateSinglePlayerGame() not on the live instance type"); return o.Close(); }
            try
            {
                var ret = start.Invoke(view, null);
                o.Str("invoked", "CreateSinglePlayerGame()");
                o.NullOrStr("returns", ret == null ? null : ret.ToString());
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                o.Str("invokeError", inner.GetType().Name + ": " + inner.Message);
                return o.Close();
            }
            o.Bool("ok", true);
            return o.Close();
        }

        private static bool NameMatches(string name, List<string> patterns)
        {
            if (patterns.Count == 0) { return true; }
            foreach (var p in patterns)
            {
                if (name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            }
            return false;
        }

        /// <summary>
        /// Live instances of a type with their readable members. Exists so "what is the game
        /// actually holding right now" is answered by reading the object, not by guessing which
        /// static or singleton owns it: `filter` is a comma-separated list of member substrings
        /// (empty = every member whose value is a plain scalar). Read-only, capped.
        /// </summary>
        private string InstanceDump(string typeName, string filterArg, string capArg)
        {
            var o = new JsonObj();
            o.Bool("ok", false);
            if (typeName.Length == 0) { o.Str("error", "instances requires name"); return o.Close(); }
            var t = FindType(typeName) ?? FindType("Il2Cpp." + typeName) ?? FindTypeBySuffix(typeName);
            if (t == null) { o.Str("error", "type not found"); return o.Close(); }
            o.Str("type", t.FullName ?? t.Name);

            var patterns = new List<string>();
            foreach (var part in filterArg.Split(','))
            {
                var p = part.Trim();
                if (p.Length > 0) { patterns.Add(p); }
            }
            int cap = 10;
            int ci;
            if (int.TryParse(capArg, out ci) && ci > 0 && ci <= 100) { cap = ci; }
            int memberCap = 80;

            object il2cppType = Il2CppTypeOf(t);
            if (il2cppType == null) { o.Str("error", "Il2CppType conversion failed"); return o.Close(); }
            MethodInfo find = null;
            var objType = FindType("UnityEngine.Object");
            foreach (var holder in new[] { FindType("UnityEngine.Resources"), objType })
            {
                if (holder == null) { continue; }
                foreach (var m in holder.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "FindObjectsOfTypeAll" && m.Name != "FindObjectsOfType") { continue; }
                    var fps = m.GetParameters();
                    if (fps.Length == 1 && fps[0].ParameterType.Name == "Type") { find = m; break; }
                }
                if (find != null) { break; }
            }
            if (find == null) { o.Str("error", "no FindObjectsOfTypeAll(Type)"); return o.Close(); }

            var arr = new JsonArr();
            int total = 0, reported = 0, members = 0;
            try
            {
                var raw = find.Invoke(null, new object[] { il2cppType });
                var en = raw as IEnumerable;
                if (en != null)
                {
                    foreach (var inst in en)
                    {
                        if (inst == null) { continue; }
                        total++;
                        if (reported >= cap) { continue; }
                        reported++;
                        var it = inst.GetType();
                        var io = new JsonObj();
                        io.Str("instanceType", it.Name);
                        var nm = SafeScalar(MemberWithAccessor(inst, "name"));
                        if (!string.IsNullOrEmpty(nm)) { io.Str("name", nm); }
                        var vals = new JsonObj();
                        foreach (var f in it.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            if (members >= memberCap) { break; }
                            if (!NameMatches(f.Name, patterns)) { continue; }
                            string sv = null;
                            try { sv = SafeScalar(f.GetValue(inst)); } catch { }
                            if (sv == null) { continue; }
                            vals.Str(f.Name, sv);
                            members++;
                        }
                        foreach (var p in it.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            if (members >= memberCap) { break; }
                            if (p.GetIndexParameters().Length > 0) { continue; }
                            if (!NameMatches(p.Name, patterns)) { continue; }
                            string sv = null;
                            try { sv = SafeScalar(p.GetValue(inst, null)); } catch { }
                            if (sv == null) { continue; }
                            vals.Str(p.Name, sv);
                            members++;
                        }
                        io.Raw("values", vals.Close());
                        arr.Raw(io.Close());
                    }
                }
            }
            catch (Exception ex)
            {
                o.Str("error", (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message);
                return o.Close();
            }
            o.Raw("instancesFound", total.ToString(CultureInfo.InvariantCulture));
            o.Raw("instancesReported", reported.ToString(CultureInfo.InvariantCulture));
            o.Raw("membersRead", members.ToString(CultureInfo.InvariantCulture));
            o.Raw("instances", arr.Close());
            o.Bool("ok", true);
            return o.Close();
        }

        /// <summary>
        /// Ask the game's own PathFinder for a route. Using the game's pathfinder means the
        /// route respects terrain, movement cost and unit type exactly as the UI would.
        /// Read-only.
        /// </summary>
        private string PathQuery(string from, string to, string maxArg)
        {
            var o = new JsonObj();
            o.Bool("ok", false);
            var gs = GameStateInstance();
            if (gs == null) { o.Str("error", "no active game"); return o.Close(); }
            var pf = FindType("PathFinder") ?? FindType("Il2Cpp.PathFinder");
            if (pf == null) { o.Str("error", "PathFinder not found"); return o.Close(); }

            int fx, fy, tx, ty;
            if (!TryParseCoords(from, out fx, out fy)) { o.Str("error", "bad from '" + Esc(from) + "'"); return o.Close(); }
            if (!TryParseCoords(to, out tx, out ty)) { o.Str("error", "bad to '" + Esc(to) + "'"); return o.Close(); }
            object start = BuildCoordinates(fx, fy, gs);
            object dest = BuildCoordinates(tx, ty, gs);
            if (start == null || dest == null) { o.Str("error", "could not build coordinates"); return o.Close(); }
            int max;
            if (!int.TryParse(maxArg, out max)) { max = 99; }

            object unit = FindUnitAt(gs, from);
            o.NullOrStr("unit", unit == null ? null : SafeScalar(MemberWithAccessor(unit, "id")));

            foreach (var m in pf.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (m.Name != "GetPath") { continue; }
                var ps = m.GetParameters();
                if (ps.Length != 5) { continue; }
                try
                {
                    var path = m.Invoke(null, new object[] { gs, start, dest, max, unit });
                    var list = path == null ? null : AsEnumerable(path);
                    var arr = new JsonArr();
                    int n = 0;
                    if (list != null)
                    {
                        foreach (var c in list) { if (c == null) { continue; } arr.Raw("\"" + Esc(SafeScalar(c)) + "\""); n++; }
                    }
                    o.Raw("path", arr.Close());
                    o.Raw("steps", n.ToString(CultureInfo.InvariantCulture));
                    o.Bool("ok", true);
                    return o.Close();
                }
                catch (Exception ex)
                {
                    o.Str("error", (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message);
                }
            }
            if (!o.ToString().Contains("error")) { o.Str("error", "no usable GetPath overload"); }
            return o.Close();
        }

        /// <summary>Every tile reachable this turn, via PathFinder.GetMoveOptions. Read-only.</summary>
        private string ReachQuery(string from, string maxArg)
        {
            var o = new JsonObj();
            o.Bool("ok", false);
            var gs = GameStateInstance();
            if (gs == null) { o.Str("error", "no active game"); return o.Close(); }
            var pf = FindType("PathFinder") ?? FindType("Il2Cpp.PathFinder");
            if (pf == null) { o.Str("error", "PathFinder not found"); return o.Close(); }
            int fx, fy;
            if (!TryParseCoords(from, out fx, out fy)) { o.Str("error", "bad from '" + Esc(from) + "'"); return o.Close(); }
            object start = BuildCoordinates(fx, fy, gs);
            if (start == null) { o.Str("error", "could not build coordinates"); return o.Close(); }
            int max;
            if (!int.TryParse(maxArg, out max)) { max = 99; }
            object unit = FindUnitAt(gs, from);
            foreach (var m in pf.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (m.Name != "GetMoveOptions") { continue; }
                if (m.GetParameters().Length != 4) { continue; }
                try
                {
                    var res = m.Invoke(null, new object[] { gs, start, max, unit });
                    var list = res == null ? null : AsEnumerable(res);
                    var arr = new JsonArr();
                    if (list != null) { foreach (var c in list) { if (c == null) { continue; } arr.Raw("\"" + Esc(SafeScalar(c)) + "\""); } }
                    o.Raw("tiles", arr.Close());
                    o.Bool("ok", true);
                    return o.Close();
                }
                catch (Exception ex)
                {
                    o.Str("error", (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message);
                }
            }
            o.Str("error", "no usable GetMoveOptions overload");
            return o.Close();
        }

        /// <summary>
        /// The unit types this build actually carries data for: keys of
        /// GameLogicData.AllUnitData. "Missing unit data" on a train attempt means the type is
        /// absent here, which is why naval units cannot be produced or embarked into.
        /// Read-only.
        /// </summary>
        private string UnitDataKeys()
        {
            var o = new JsonObj();
            o.Bool("ok", false);
            var dm = FindType("PolytopiaDataManager") ?? FindType("Il2Cpp.PolytopiaDataManager");
            var gl = FindType("GameLogicData") ?? FindType("Il2CppPolytopia.Data.GameLogicData")
                ?? FindTypeBySuffix("GameLogicData");
            if (dm == null || gl == null) { o.Str("error", "data manager/types not found"); return o.Close(); }

            object current = InvokeStatic(dm, "get_currentVersion");
            if (current == null) { o.Str("error", "GameLogicData.currentVersion is null"); return o.Close(); }
            object all = MemberWithAccessor(current, "AllUnitData");
            if (all == null) { o.Str("error", "AllUnitData unavailable"); return o.Close(); }

            var keys = new JsonArr();
            int n = 0;
            // il2cpp dictionaries surface as IEnumerable of KeyValuePair
            var en = AsEnumerable(all);
            if (en != null)
            {
                foreach (var kv in en)
                {
                    if (kv == null) { continue; }
                    object k = MemberWithAccessor(kv, "Key");
                    if (k == null) { continue; }
                    keys.Raw("\"" + Esc(SafeScalar(k)) + "\"");
                    n++;
                }
            }
            o.Raw("count", n.ToString(CultureInfo.InvariantCulture));
            o.Raw("types", keys.Close());
            o.Bool("ok", true);
            return o.Close();
        }

        private static object InvokeOn(object target, string method, object[] args)
        {
            if (target == null) return null;
            try
            {
                var chosen = UniqueInvocableMethod(target.GetType(), method, args, false);
                return chosen == null ? null : chosen.Invoke(target, args);
            }
            catch (Exception error) { RecordNativeReadFailure(target.GetType(), method, error); return null; }
        }

        /// <summary>
        /// Every clickable control inside the current popup, with the text it displays.
        ///
        /// Reward/confirm popups do not always populate PopupButtonData, so the buttons have to
        /// be found where they physically are: components on the popup's child transforms. Each
        /// entry reports the component and the label found on that node or under it, which is
        /// what lets a caller choose by name ("Explorer") instead of guessing an index.
        /// Read-only.
        /// </summary>
        private string PopupButtonList()
        {
            var clickables = new List<object>();
            var labels = new List<string>();
            var nodes = new List<string>();
            var visitedNodes = new List<string>();
            CollectClickables(out clickables, out labels, out nodes, visitedNodes, 200);

            var o = new JsonObj();
            o.Bool("ok", true);
            var arr = new JsonArr();
            for (int i = 0; i < clickables.Count; i++)
            {
                var e = new JsonObj();
                e.Raw("index", i.ToString(CultureInfo.InvariantCulture));
                e.Str("component", clickables[i].GetType().Name);
                e.NullOrStr("label", i < labels.Count ? labels[i] : null);
                e.NullOrStr("node", i < nodes.Count ? nodes[i] : null);
                var hasOnClick = MemberWithAccessor(clickables[i], "onClick") != null;
                e.Bool("hasOnClick", hasOnClick);
                arr.Raw(e.Close());
            }
            o.Raw("clickables", arr.Close());
            o.Raw("count", clickables.Count.ToString(CultureInfo.InvariantCulture));
            var vis = new JsonArr();
            foreach (var n in visitedNodes) { vis.Raw("\"" + Esc(n) + "\""); }
            o.Raw("visited", vis.Close());
            return o.Close();
        }

        /// <summary>
        /// Act on one of the popup's real controls: fire its own click handler
        /// (onClick.Invoke(), or Press() on the game's button base class). This is the same entry
        /// point a player's click reaches, so the game runs its real handler and closes the popup
        /// itself - no fabricated event data, no force-close.
        /// </summary>
        private string PopupClick(string indexArg)
        {
            int wanted;
            if (indexArg.Length == 0 || !int.TryParse(indexArg, out wanted)) { wanted = 0; }

            var clickables = new List<object>();
            var labels = new List<string>();
            var nodes = new List<string>();
            CollectClickables(out clickables, out labels, out nodes, new List<string>(), 200);
            if (clickables.Count == 0)
            {
                return "{\"ok\":false,\"error\":\"no clickable control found in the current popup\"}";
            }

            // Select by index, or by label when the argument is a name (e.g. "Explorer").
            object target = null;
            int chosenIndex = -1;
            if (indexArg.Length > 0 && !int.TryParse(indexArg, out wanted))
            {
                for (int i = 0; i < labels.Count; i++)
                {
                    if (labels[i] != null && labels[i].IndexOf(indexArg, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        target = clickables[i];
                        chosenIndex = i;
                        break;
                    }
                }
            }
            if (target == null)
            {
                if (wanted < 0 || wanted >= clickables.Count) { wanted = 0; }
                target = clickables[wanted];
                chosenIndex = wanted;
            }

            var o = new JsonObj();
            o.Bool("ok", false);
            o.Str("component", target.GetType().Name);
            o.Raw("index", chosenIndex.ToString(CultureInfo.InvariantCulture));
            o.NullOrStr("label", chosenIndex >= 0 && chosenIndex < labels.Count ? labels[chosenIndex] : null);
            o.NullOrStr("node", chosenIndex >= 0 && chosenIndex < nodes.Count ? nodes[chosenIndex] : null);
            var t = FindType("PopupManager") ?? FindType("Il2Cpp.PopupManager");
            o.NullOrStr("popupBefore", t == null ? null : (PopupShowingNow(t) ? "True" : "False"));

            bool fired = false;
            // 1) the game's own click SIGNAL - this is literally what a player's click fires
            //    (the call stack in Player.log ends in Signal:Dispatch()).
            fired = FireSignal(target, o);
            // 2) a Press()/onClick entry point, if this control exposes one
            if (!fired)
            {
                foreach (var m in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name != "Press" || m.GetParameters().Length != 0) { continue; }
                    try { m.Invoke(target, null); fired = true; o.Str("via", "Press()"); break; }
                    catch (Exception ex) { o.Str("pressError", (ex.InnerException ?? ex).Message); }
                }
            }
            if (!fired)
            {
                var onClick = MemberWithAccessor(target, "onClick");
                if (onClick != null)
                {
                    try
                    {
                        var inv = onClick.GetType().GetMethod("Invoke");
                        if (inv != null)
                        {
                            var ps = inv.GetParameters();
                            inv.Invoke(onClick, new object[ps.Length]);
                            fired = true;
                            o.Str("via", "onClick.Invoke()");
                        }
                    }
                    catch (Exception ex) { o.Str("onClickError", (ex.InnerException ?? ex).Message); }
                }
            }
            // The control may have come back as a bare GameObject; resolve its real button
            // components and fire the first one that exposes a handler.
            if (!fired)
            {
                foreach (var cb in ConcreteButtonsOn(target))
                {
                    foreach (var m in cb.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (m.Name != "Press" || m.GetParameters().Length != 0) { continue; }
                        try
                        {
                            m.Invoke(cb, null);
                            fired = true;
                            o.Str("via", cb.GetType().Name + ".Press()");
                            break;
                        }
                        catch (Exception ex) { o.Str("pressError", (ex.InnerException ?? ex).Message); }
                    }
                    if (fired) { break; }
                }
            }
            if (!fired) { o.Str("error", "control has neither Press() nor onClick"); return o.Close(); }

            Log("popup: click " + target.GetType().Name + " idx=" + chosenIndex + " label=" + (chosenIndex >= 0 && chosenIndex < labels.Count ? labels[chosenIndex] : ""));
            o.Bool("ok", true);
            o.NullOrStr("popupAfter", t == null ? null : (PopupShowingNow(t) ? "True" : "False"));
            return o.Close();
        }

        /// <summary>
        /// Walk the current popup's controls and collect everything that can be clicked, together
        /// with the nearest label text on that node or beneath it.
        /// </summary>
        private void CollectClickables(out List<object> clickables, out List<string> labels,
            out List<string> nodes, List<string> visited, int budget)
        {
            clickables = new List<object>();
            labels = new List<string>();
            nodes = new List<string>();
            var t = FindType("PopupManager") ?? FindType("Il2Cpp.PopupManager");
            if (t == null) { return; }
            object popup = InvokeStatic(t, "GetCurrentPopup");
            if (popup == null) { return; }
            var go = MemberWithAccessor(popup, "gameObject");
            if (go == null) { return; }
            object tr = MemberWithAccessor(go, "transform");
            if (tr == null) { return; }
            var state = new int[] { 0 };
            WalkForClickables(tr, state, clickables, labels, nodes, visited, budget);
        }

        private void WalkForClickables(object tr, int[] state, List<object> clickables,
            List<string> labels, List<string> nodes, List<string> visited, int budget)
        {
            if (tr == null || state[0] >= budget) { return; }
            state[0]++;
            object go = MemberWithAccessor(tr, "gameObject");
            if (go == null) { return; }
            string nodeName = ObjectName(go);
            visited.Add(nodeName);
            string ownLabel = ReadLabel(go);

            var comps = GetComponentList(go);
            var ce = comps == null ? null : AsEnumerable(comps);
            if (ce != null)
            {
                foreach (var c in ce)
                {
                    if (c == null) { continue; }
                    var cn = c.GetType().Name;
                    bool named = cn.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool real = MemberWithAccessor(c, "onClick") != null;
                    // The il2cpp binding often hands back a bare MonoBehaviour wrapper for these
                    // controls, so also scan for the concrete button components actually attached.
                    if (named || real)
                    {
                        string label = ReadControlText(c);
                        if (string.IsNullOrEmpty(label)) { label = ownLabel; }
                        if (string.IsNullOrEmpty(label)) { label = FindLabelUnder(tr, 3); }
                        clickables.Add(c);
                        labels.Add(label);
                        nodes.Add(nodeName);
                    }
                }
            }
            // A node named like a button is a button even when its component came back untyped.
            if (nodeName.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var concrete = ConcreteButtonsOn(go);
                if (concrete.Count == 0 && clickables.Count == 0)
                {
                    clickables.Add(go);
                    labels.Add(string.IsNullOrEmpty(ownLabel) ? FindLabelUnder(tr, 3) : ownLabel);
                    nodes.Add(nodeName);
                }
                foreach (var cb in concrete)
                {
                    string label = ReadControlText(cb);
                    if (string.IsNullOrEmpty(label)) { label = ownLabel; }
                    if (string.IsNullOrEmpty(label)) { label = FindLabelUnder(tr, 3); }
                    clickables.Add(cb);
                    labels.Add(label);
                    nodes.Add(nodeName);
                }
            }

            var childCount = MemberWithAccessor(tr, "childCount");
            int count = 0;
            try { count = childCount == null ? 0 : Convert.ToInt32(childCount, CultureInfo.InvariantCulture); } catch { }
            for (int i = 0; i < count; i++)
            {
                if (state[0] >= budget) { break; }
                object child = CallInstance(tr, "GetChild", i);
                if (child == null) { continue; }
                WalkForClickables(child, state, clickables, labels, nodes, visited, budget);
            }
        }

        /// <summary>
        /// The concrete button components attached to a node, found by asking GetComponent for
        /// each button-like type in the loaded assemblies. GetComponent returns non-null only for
        /// components really present, so this recovers SquareButton_UI2 / UIRoundButton etc. even
        /// when the generic component list degrades them to plain MonoBehaviour.
        /// </summary>
        private List<object> ConcreteButtonsOn(object go)
        {
            var found = new List<object>();
            // The base classes are the reliable handle: every clickable control derives from
            // UIButtonBase / UIButtonBase_UI2, and GetComponent resolves those directly even
            // when the concrete subclass comes back as a bare MonoBehaviour.
            foreach (var baseName in new[] { "UIButtonBase", "UIButtonBase_UI2", "UIRoundButton", "UISquareButton_UI2", "UITextButton" })
            {
                var bt = FindType(baseName) ?? FindType("Il2Cpp." + baseName);
                if (bt == null) { continue; }
                var comp = GetComponentOfType(go, bt);
                if (comp != null && !found.Contains(comp)) { found.Add(comp); }
            }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] ts;
                try { ts = asm.GetTypes(); } catch { continue; }
                foreach (var t in ts)
                {
                    if (t == null || !t.IsClass || t.IsAbstract) { continue; }
                    if (t.FullName != null && t.FullName.IndexOf('+') >= 0) { continue; }
                    var n = t.Name;
                    if (n.IndexOf("Button", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                    var comp = GetComponentOfType(go, t);
                    if (comp == null) { continue; }
                    if (!found.Contains(comp)) { found.Add(comp); }
                }
            }
            return found;
        }

        /// <summary>
        /// Fire a UI control's own click signal.
        ///
        /// UIButtonBase_UI2 exposes onClickedSignal (a Signal); a real click ends in
        /// Signal.Dispatch(), so dispatching it runs the identical handler chain the game runs
        /// when a player presses the button. Nothing is fabricated and nothing is forced.
        /// </summary>
        private bool FireSignal(object target, JsonObj o)
        {
            foreach (var propName in new[] { "onClickedSignal", "OnClickedSignal", "onUpSignal" })
            {
                object sig = null;
                try { sig = MemberWithAccessor(target, propName); } catch { }
                if (sig == null) { continue; }
                foreach (var m in sig.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name != "Dispatch" || m.GetParameters().Length != 0) { continue; }
                    try
                    {
                        m.Invoke(sig, null);
                        o.Str("via", target.GetType().Name + "." + propName + ".Dispatch()");
                        Log("popup: dispatched " + target.GetType().Name + "." + propName);
                        return true;
                    }
                    catch (Exception ex) { o.Str("signalError", (ex.InnerException ?? ex).Message); }
                }
            }
            return false;
        }

        /// <summary>
        /// A UI control's own text: UIButtonBase_UI2 exposes Text, which is the label a caller
        /// sees on the button (e.g. "Explorer").
        /// </summary>
        private string ReadControlText(object component)
        {
            try
            {
                var t = MemberWithAccessor(component, "Text");
                if (t != null)
                {
                    var s = SafeScalar(t);
                    if (!string.IsNullOrEmpty(s)) { return s; }
                }
                // UIRoundButton keeps its caption in a TMP field rather than a Text property
                var tip = MemberWithAccessor(component, "textInContainer");
                if (tip != null)
                {
                    var s2 = ReadLabel(tip);
                    if (!string.IsNullOrEmpty(s2)) { return s2; }
                }
                var tf = MemberWithAccessor(component, "textfield");
                if (tf != null)
                {
                    var s3 = ReadLabel(tf);
                    if (!string.IsNullOrEmpty(s3)) { return s3; }
                }
            }
            catch { }
            return null;
        }

        /// <summary>First non-empty label found within a few levels beneath a transform.</summary>
        private string FindLabelUnder(object tr, int depth)
        {
            if (tr == null || depth < 0) { return null; }
            var childCount = MemberWithAccessor(tr, "childCount");
            int count = 0;
            try { count = childCount == null ? 0 : Convert.ToInt32(childCount, CultureInfo.InvariantCulture); } catch { }
            for (int i = 0; i < count; i++)
            {
                object child = CallInstance(tr, "GetChild", i);
                if (child == null) { continue; }
                object go = MemberWithAccessor(child, "gameObject");
                if (go != null)
                {
                    var l = ReadLabel(go);
                    if (!string.IsNullOrEmpty(l)) { return l; }
                }
                var deeper = FindLabelUnder(child, depth - 1);
                if (!string.IsNullOrEmpty(deeper)) { return deeper; }
            }
            return null;
        }
        /// <summary>
        /// Walk the current popup's transform tree and report every component, flagging the
        /// clickable ones. When PopupManager hands back a plain PopupBase (no buttonData and no
        /// Buttons), the real controls live as child components - this finds them so a choice
        /// can be made through the control that actually exists.
        /// Read-only.
        /// </summary>
        private string PopupTree()
        {
            var t = FindType("PopupManager") ?? FindType("Il2Cpp.PopupManager");
            if (t == null) { return "{\"ok\":false,\"error\":\"PopupManager not found\"}"; }
            object current = InvokeStatic(t, "GetCurrentPopup");
            if (current == null) { return "{\"ok\":false,\"error\":\"no current popup\"}"; }

            var o = new JsonObj();
            o.Bool("ok", true);
            o.Str("popupType", current.GetType().FullName);
            var go = MemberWithAccessor(current, "gameObject");
            o.NullOrStr("root", go == null ? null : ObjectName(go));
            if (go == null) { return o.Close(); }

            var nodes = new JsonArr();
            object tr = MemberWithAccessor(go, "transform");
            var budgetState = new int[] { 0 };
            WalkTransform(tr, 0, nodes, budgetState, 120);
            o.Raw("nodes", nodes.Close());

            // The popup's own resolution callback, if the game stored one.
            var hc = MemberWithAccessor(current, "HideCallback");
            var h = new JsonObj();
            h.Bool("present", hc != null);
            if (hc != null)
            {
                h.Str("type", hc.GetType().FullName);
                object tgt = null, mth = null;
                try { tgt = MemberWithAccessor(hc, "Target"); } catch { }
                try { mth = MemberWithAccessor(hc, "Method"); } catch { }
                h.NullOrStr("target", tgt == null ? null : tgt.ToString());
                h.NullOrStr("method", mth == null ? null : mth.ToString());
            }
            o.Raw("hideCallback", h.Close());
            return o.Close();
        }

        private void WalkTransform(object tr, int depth, JsonArr outArr, int[] budgetState, int budget)
        {
            if (tr == null || depth > 6 || outArr == null || budgetState[0] >= budget) { return; }
            budgetState[0]++;
            object go = MemberWithAccessor(tr, "gameObject");
            if (go == null) { return; }
            var node = new JsonObj();
            node.Raw("depth", depth.ToString(CultureInfo.InvariantCulture));
            node.Str("name", ObjectName(go));
            node.Str("active", ReadActive(go));
            var label = ReadLabel(go);
            if (label.Length > 0) { node.Str("label", label); }

            var comps = new JsonArr();
            var compList = GetComponentList(go);
            var ce = compList == null ? null : AsEnumerable(compList);
            if (ce != null)
            {
                foreach (var c in ce)
                {
                    if (c == null) { continue; }
                    var cn = c.GetType().Name;
                    var tagged = cn;
                    if (cn.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0) { tagged = cn + "*CLICKABLE"; }
                    comps.Raw("\"" + Esc(tagged) + "\"");
                }
            }
            node.Raw("components", comps.Close());
            outArr.Raw(node.Close());

            int n = 0;
            var childCount = MemberWithAccessor(tr, "childCount");
            int count = 0;
            try { count = childCount == null ? 0 : Convert.ToInt32(childCount, CultureInfo.InvariantCulture); } catch { }
            for (int i = 0; i < count; i++)
            {
                if (budgetState[0] >= budget) { break; }
                object child = CallInstance(tr, "GetChild", i);
                if (child == null) { continue; }
                WalkTransform(child, depth + 1, outArr, budgetState, budget);
                n++;
            }
        }

        /// <summary>
        /// Resolve the CONCRETE component types attached to the current popup's GameObject.
        ///
        /// This is the missing piece: PopupManager.GetCurrentPopup() yields an object typed as
        /// PopupBase, so every subclass member reads as absent. GetComponent(type) returns
        /// non-null only for a component the object really has, so sweeping every popup-like
        /// type through GetComponent reveals the true type (e.g. RewardPopup) and thereby the
        /// real entry points (RewardChoosenCallback, SetRewards, OnHide, ReplayClickButton).
        /// Read-only.
        /// </summary>
        private string PopupResolve()
        {
            var t = FindType("PopupManager") ?? FindType("Il2Cpp.PopupManager");
            if (t == null) { return "{\"ok\":false,\"error\":\"PopupManager not found\"}"; }
            object current = InvokeStatic(t, "GetCurrentPopup");
            if (current == null) { return "{\"ok\":false,\"error\":\"no current popup\"}"; }
            var go = MemberWithAccessor(current, "gameObject");
            if (go == null) { return "{\"ok\":false,\"error\":\"popup has no gameObject\"}"; }

            var o = new JsonObj();
            o.Bool("ok", true);
            o.Str("root", ObjectName(go));

            var found = new JsonArr();
            var attached = new List<object>();
            int considered = 0;
            foreach (var cand in AllPopupLikeTypes())
            {
                considered++;
                object comp = GetComponentOfType(go, cand);
                if (comp == null) { continue; }
                attached.Add(comp);
                var co = new JsonObj();
                co.Str("type", cand.FullName);
                var btns = new List<string>();
                AddMemberNames(cand, btns);
                var m = new JsonArr();
                foreach (var n in btns) { m.Raw("\"" + Esc(n) + "\""); }
                co.Raw("entryPoints", m.Close());
                found.Raw(co.Close());
            }
            o.Raw("considered", considered.ToString(CultureInfo.InvariantCulture));
            o.Raw("attached", found.Close());
            o.Raw("attachedCount", attached.Count.ToString(CultureInfo.InvariantCulture));

            // Report the real popup's own reward plumbing if this is a reward popup.
            foreach (var comp in attached)
            {
                var cn = comp.GetType().Name;
                if (cn.IndexOf("RewardPopup", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                var rp = new JsonObj();
                var cb = MemberWithAccessor(comp, "RewardChoosenCallback");
                rp.Bool("hasRewardChoosenCallback", cb != null);
                if (cb != null) { rp.Str("callbackType", cb.GetType().FullName); }
                var rewards = MemberWithAccessor(comp, "cityRewards");
                var list = rewards == null ? null : AsEnumerable(rewards);
                if (list != null)
                {
                    var ra = new JsonArr();
                    foreach (var r in list) { if (r == null) { continue; } ra.Raw("\"" + Esc(SafeScalar(r)) + "\""); }
                    rp.Raw("cityRewards", ra.Close());
                }
                o.Raw("rewardPopup", rp.Close());
                break;
            }
            return o.Close();
        }

        /// <summary>Every loaded type whose name looks like a popup or a popup button.</summary>
        private static IEnumerable<Type> AllPopupLikeTypes()
        {
            var seen = new List<Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] ts;
                try { ts = asm.GetTypes(); } catch { continue; }
                foreach (var cand in ts)
                {
                    if (cand == null || !cand.IsClass || cand.IsAbstract) { continue; }
                    // Compiler-generated nested types (async state machines, display classes) are
                    // not real components; the il2cpp binding can still hand one back for
                    // GetComponent<T>, so exclude them or the result is full of false positives.
                    if (cand.FullName != null && cand.FullName.IndexOf('+') >= 0) { continue; }
                    if (cand.Name.IndexOf("__c", StringComparison.Ordinal) >= 0) { continue; }
                    if (cand.Name.StartsWith("<", StringComparison.Ordinal)) { continue; }
                    if (typeof(Delegate).IsAssignableFrom(cand)) { continue; }
                    if (cand.Name.IndexOf("StateMachine", StringComparison.OrdinalIgnoreCase) >= 0) { continue; }
                    var n = cand.Name;
                    if (n.IndexOf("Popup", StringComparison.OrdinalIgnoreCase) < 0 &&
                        n.IndexOf("RoundButton", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                    if (seen.Contains(cand)) { continue; }
                    seen.Add(cand);
                    yield return cand;
                }
            }
        }

        /// <summary>GetComponent(type) - non-null only when the GameObject really has that component.</summary>
        private static object GetComponentOfType(object go, Type wanted)
        {
            foreach (var m in go.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name != "GetComponent") { continue; }
                try
                {
                    if (m.IsGenericMethodDefinition)
                    {
                        if (m.GetGenericArguments().Length != 1) { continue; }
                        var closed = m.MakeGenericMethod(new[] { wanted });
                        var res = closed.Invoke(go, null);
                        if (res != null) { return res; }
                        continue;
                    }
                    var ps = m.GetParameters();
                    if (ps.Length != 1) { continue; }
                    if (ps[0].ParameterType == typeof(string)) { continue; }
                    var il2 = Il2CppTypeOf(wanted);
                    if (il2 == null) { continue; }
                    var r = m.Invoke(go, new[] { il2 });
                    if (r != null) { return r; }
                }
                catch { }
            }
            return null;
        }

        /// <summary>Names of the members that could act as an entry point on a popup component.</summary>
        private static void AddMemberNames(Type cand, List<string> into)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var m in cand.GetMethods(F))
            {
                if (m.IsSpecialName) { continue; }
                if (m.DeclaringType == typeof(object)) { continue; }
                var n = m.Name;
                if (n == "OnHide" || n == "Hide" || n == "ReplayClickButton" || n == "SetDefaultOkbutton" ||
                    n == "SetMainButton" || n == "SetRewards" || n == "GetButtonById" || n == "OnRewardButtonClicked" ||
                    n == "AddPopupToStack" || n == "OnShowComplete")
                {
                    into.Add(n + "(" + m.GetParameters().Length + ")");
                }
            }
            foreach (var p in cand.GetProperties(F))
            {
                var n = p.Name;
                if (n == "buttonData" || n == "Buttons" || n == "RewardChoosenCallback" || n == "TopButton" ||
                    n == "TopButtonData" || n == "IsUnskippable")
                {
                    into.Add("prop " + n);
                }
            }
        }

        /// <summary>
        /// The most specific popup component on the current popup's GameObject.
        ///
        /// GetCurrentPopup() is typed PopupBase, whose subclass members are unreachable. The
        /// concrete component (BasicPopup / BasicPopupLegacy / RewardPopup / ...) is really
        /// attached, so prefer it and fall back to the wrapper only if nothing else resolves.
        /// </summary>
        private object BestPopupComponent(object fallback)
        {
            try
            {
                var go = MemberWithAccessor(fallback, "gameObject");
                if (go == null) { return fallback; }
                object match = null;
                object genericPopup = null;
                foreach (var cand in AllPopupLikeTypes())
                {
                    var n = cand.Name;
                    if (n.IndexOf("Popup", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                    if (n == "PopupBase") { continue; }
                    if (n.IndexOf("PopupManager", StringComparison.OrdinalIgnoreCase) >= 0) { continue; }
                    if (n.StartsWith("Popup_", StringComparison.OrdinalIgnoreCase)) { continue; }
                    var comp = GetComponentOfType(go, cand);
                    if (comp == null) { continue; }
                    // RewardPopup carries its own reward interface (RewardChoosenCallback) and
                    // overrides OnHide/Hide; prefer it over the generic BasicPopup that is also
                    // attached, because acting on BasicPopup does not close the visible popup.
                    if (n.IndexOf("RewardPopup", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return comp;
                    }
                    if (n.IndexOf("BasicPopupLegacy", StringComparison.OrdinalIgnoreCase) >= 0) { genericPopup = comp; }
                    if (match == null) { match = comp; }
                    if (genericPopup == null &&
                        (MemberWithAccessor(comp, "buttonData") != null || MemberWithAccessor(comp, "Buttons") != null))
                    {
                        genericPopup = comp;
                    }
                }
                return genericPopup ?? match ?? fallback;
            }
            catch (Exception ex) { Log("popup: BestPopupComponent failed " + (ex.InnerException ?? ex).Message); }
            return fallback;
        }

        /// <summary>Invoke an instance method by name, preferring a single-argument overload.</summary>
        private static object CallInstance(object target, string method, object arg)
        {
            if (target == null) { return null; }
            foreach (var m in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name != method) { continue; }
                var ps = m.GetParameters();
                if (arg == null && ps.Length == 0) { try { return m.Invoke(target, null); } catch { } continue; }
                if (ps.Length != 1) { continue; }
                try { return m.Invoke(target, new[] { arg }); }
                catch { }
            }
            return null;
        }

        private string PopupStateJson()
        {
            var t = FindType("PopupManager") ?? FindType("Il2Cpp.PopupManager");
            var o = new JsonObj();
            if (t == null) { o.Bool("ok", false); o.Str("error", "no PopupManager"); return o.Close(); }
            object current = InvokeStatic(t, "GetCurrentPopup");
            o.NullableBool("popupShowing", CRBool(InvokeStatic(t, "get_PopupShowing")));
            o.NullableBool("unskippable", CRBool(InvokeStatic(t, "IsUnskippablePopupShowing")));
            o.NullOrStr("currentPopup", current == null ? null : current.GetType().Name);
            return o.Close();
        }

        private static string StrOrNull(object v) { return v == null ? null : v.ToString(); }

        /// <summary>
        /// Ask the IL2CPP runtime itself what the current popup really is.
        ///
        /// PopupManager.GetCurrentPopup() comes back typed as PopupBase, so every managed
        /// property that the concrete subclass hides (buttonData, Buttons, ReplayClickButton)
        /// reads as absent even though the component is really a BasicPopup/RewardPopup.
        /// The native class name from the object's Il2CppClass is the ground truth.
        /// Read-only.
        /// </summary>
        private string PopupNative()
        {
            var t = FindType("PopupManager") ?? FindType("Il2Cpp.PopupManager");
            if (t == null) { return "{\"ok\":false,\"error\":\"PopupManager not found\"}"; }
            object current = InvokeStatic(t, "GetCurrentPopup");
            if (current == null) { return "{\"ok\":false,\"error\":\"no current popup\"}"; }

            var o = new JsonObj();
            o.Bool("ok", true);
            o.Str("managedType", current.GetType().FullName);
            o.NullOrStr("pointer", PointerOf(current));
            var diag = new JsonObj();
            o.Raw("nativeClass", NativeClassName(current, diag));
            o.Raw("probe", diag.Close());

            // Every managed type in the domain whose name looks like a popup, grouped so the
            // caller can see which concrete popup classes actually exist in this build.
            var names = new JsonArr();
            int listed = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] ts;
                try { ts = asm.GetTypes(); } catch { continue; }
                foreach (var cand in ts)
                {
                    if (cand == null || !cand.IsClass) { continue; }
                    if (cand.Name.IndexOf("Popup", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                    if (listed >= 60) { break; }
                    names.Raw("\"" + Esc(cand.FullName) + "\"");
                    listed++;
                }
                if (listed >= 60) { break; }
            }
            o.Raw("popupTypes", names.Close());
            return o.Close();
        }

        private static string PointerOf(object o)
        {
            try
            {
                var p = o.GetType().GetProperty("Pointer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p == null) { return null; }
                var v = p.GetValue(o, null);
                return v == null ? null : "0x" + Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString("X");
            }
            catch { return null; }
        }

        /// <summary>Il2CppClass name straight from the runtime: il2cpp_object_get_class(obj)->name.</summary>
        private static string NativeClassName(object o, JsonObj diag)
        {
            try
            {
                IntPtr ptr = IntPtr.Zero;
                foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (p.Name != "Pointer") { continue; }
                    var v = p.GetValue(o, null);
                    diag.NullOrStr("pointerProp", v == null ? null : v.ToString());
                    if (v != null && v is IntPtr) { ptr = (IntPtr)v; }
                    break;
                }
                if (ptr == IntPtr.Zero) { ptr = Il2CppRawPointer(o, diag); }
                diag.NullOrStr("ptr", ptr == IntPtr.Zero ? null : "0x" + ptr.ToInt64().ToString("X"));
                if (ptr == IntPtr.Zero) { return null; }

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var il2 = asm.GetType("Il2CppInterop.Runtime.IL2CPP", false);
                    if (il2 == null) { continue; }
                    diag.Str("il2cppApiAssembly", asm.GetName().Name);

                    var getClass = FindFn(il2, "il2cpp_object_get_class", diag, "getClass");
                    if (getClass == null) { continue; }
                    object klass = InvokeFn(getClass, ptr);
                    diag.NullOrStr("klassRaw", klass == null ? null : klass.ToString());
                    IntPtr kptr = ToPtr(klass);
                    diag.NullOrStr("kptr", kptr == IntPtr.Zero ? null : "0x" + kptr.ToInt64().ToString("X"));
                    if (kptr == IntPtr.Zero) { return null; }

                    var getName = FindFn(il2, "il2cpp_class_get_name", diag, "getName");
                    if (getName == null) { return null; }
                    object namePtr = InvokeFn(getName, kptr);
                    IntPtr sp = ToPtr(namePtr);
                    if (sp == IntPtr.Zero) { return null; }
                    return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(sp);
                }
            }
            catch (Exception ex) { Log("popup native class failed: " + (ex.InnerException ?? ex).Message); }
            return null;
        }

        /// <summary>The pointer behind an Il2CppObjectBase, whichever field the interop layer uses.</summary>
        private static IntPtr Il2CppRawPointer(object o, JsonObj diag)
        {
            try
            {
                var t = o.GetType();
                var a = new JsonArr();
                int n = 0;
                for (var cur = t; cur != null && n < 24; cur = cur.BaseType)
                {
                    foreach (var p in cur.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (n >= 24) { break; }
                        if (p.Name.IndexOf("pointer", StringComparison.OrdinalIgnoreCase) < 0 &&
                            p.Name.IndexOf("class", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                        a.Raw("\"" + Esc(cur.Name + "." + p.Name + ":" + p.PropertyType.Name + "=" +
                            SafeScalar(GetPropSafe(p, o))) + "\"");
                        n++;
                    }
                }
                diag.Raw("pointerLike", a.Close());
            }
            catch { }
            return IntPtr.Zero;
        }

        private static object GetPropSafe(PropertyInfo p, object o)
        {
            try { return p.GetValue(o, null); } catch { return null; }
        }

        private static FieldInfo FindFn(Type il2, string name, JsonObj diag, string label)
        {
            var f = il2.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null)
            {
                var a = new JsonArr();
                const int MAX = 12;
                int n = 0;
                foreach (var cand in il2.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (cand.Name.IndexOf(label, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                    if (n >= MAX) { break; }
                    a.Raw("\"" + Esc(cand.Name) + ":" + cand.FieldType.Name + "\"");
                    n++;
                }
                diag.Raw(label + "_candidates", a.Close());
                return null;
            }
            return f;
        }

        private static object InvokeFn(FieldInfo fnField, params object[] args)
        {
            var d = fnField.GetValue(null) as Delegate;
            return d == null ? null : d.DynamicInvoke(args);
        }

        private static IntPtr ToPtr(object v)
        {
            if (v == null) { return IntPtr.Zero; }
            if (v is IntPtr) { return (IntPtr)v; }
            try { return new IntPtr(Convert.ToInt64(v, CultureInfo.InvariantCulture)); } catch { return IntPtr.Zero; }
        }

        /// <summary>
        /// Verbatim shape of the current popup. This is the diagnostic that decides which
        /// interface a popup actually offers instead of guessing from its name:
        ///   - its class chain (BasicPopup? BasicPopupLegacy? RewardPopup? plain PopupBase?)
        ///   - whether buttonData / Buttons / ReplayClickButton are overridden (DeclaringType)
        ///   - the raw protected m_buttonData field as a fallback
        /// Read-only: invokes only getters.
        /// </summary>
        private string PopupShape()
        {
            var t = FindType("PopupManager") ?? FindType("Il2Cpp.PopupManager");
            if (t == null) { return "{\"ok\":false,\"error\":\"PopupManager not found\"}"; }
            object current = InvokeStatic(t, "GetCurrentPopup");
            if (current == null) { return "{\"ok\":false,\"error\":\"no current popup\"}"; }
            var ct = current.GetType();
            var o = new JsonObj();
            o.Bool("ok", true);
            o.Str("runtimeType", ct.FullName);

            // class chain
            var chain = new JsonArr();
            for (var b = ct; b != null && b != typeof(object); b = b.BaseType) { chain.Raw("\"" + Esc(b.Name) + "\""); }
            o.Raw("chain", chain.Close());

            // who actually declares the members we might use
            var decl = new JsonObj();
            foreach (var name in new[] { "buttonData", "Buttons", "TopButton", "TopButtonData",
                                         "ReplayClickButton", "OnHide", "Hide", "Show",
                                         "RewardChoosenCallback", "SetRewards", "SetData" })
            {
                var m = ct.GetMethod("get_" + name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? ct.GetMethod("set_" + name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m == null)
                {
                    foreach (var cand in ct.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (cand.Name == name) { m = cand; break; }
                    }
                }
                decl.NullOrStr(name, m == null ? null : (m.DeclaringType == null ? m.Name : m.DeclaringType.Name + "." + m.Name));
            }
            o.Raw("declaredBy", decl.Close());

            // raw protected array, bypassing the virtual getter
            o.Raw("m_buttonData", RawButtonArray(current, ct).Close());
            o.Raw("buttonDataProp", ButtonDataArray(current).Close());
            return o.Close();
        }

        private JsonArr RawButtonArray(object popup, Type ct)
        {
            var arr = new JsonArr();
            var f = ct.GetField("m_buttonData", BindingFlags.NonPublic | BindingFlags.Instance)
                 ?? ct.GetField("buttonData", BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) { return arr; }
            object raw;
            try { raw = f.GetValue(popup); } catch { return arr; }
            var list = raw == null ? null : AsEnumerable(raw);
            if (list == null) { return arr; }
            foreach (var bd in list)
            {
                if (bd == null) { continue; }
                arr.Raw(ButtonDataJson(bd).Close());
            }
            return arr;
        }

        private JsonArr ButtonDataArray(object popup)
        {
            var arr = new JsonArr();
            object data = null;
            try { data = MemberWithAccessor(popup, "buttonData"); } catch { }
            var list = data == null ? null : AsEnumerable(data);
            if (list == null) { return arr; }
            foreach (var bd in list)
            {
                if (bd == null) { continue; }
                arr.Raw(ButtonDataJson(bd).Close());
            }
            return arr;
        }

        private JsonObj ButtonDataJson(object bd)
        {
            var bo = new JsonObj();
            bo.NullOrStr("text", SafeScalar(MemberWithAccessor(bd, "text")));
            bo.NullOrStr("id", SafeScalar(MemberWithAccessor(bd, "id")));
            bo.NullOrStr("closesPopup", SafeScalar(MemberWithAccessor(bd, "closesPopup")));
            bo.NullOrStr("state", SafeScalar(MemberWithAccessor(bd, "state")));
            bo.Bool("hasCallback", MemberWithAccessor(bd, "callback") != null);
            return bo;
        }

        /// <summary>
        /// Select a popup button through the game's OWN programmatic trigger
        /// PopupBase.ReplayClickButton(int buttonIndex, Action onHideCallback, float delay).
        /// The game runs its real handler; we do not fabricate event data and we do not
        /// force the popup closed.
        /// </summary>
        private string ReplayClick(object popup, int index)
        {
            var ct = popup.GetType();
            var o = new JsonObj();
            o.Bool("ok", false);
            o.Str("popupType", ct.Name);
            o.Raw("index", index.ToString(CultureInfo.InvariantCulture));

            MethodInfo m = null;
            foreach (var cand in ct.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (cand.Name != "ReplayClickButton") { continue; }
                var ps = cand.GetParameters();
                if (ps.Length >= 1 && ps[0].ParameterType == typeof(int)) { m = cand; break; }
            }
            if (m == null) { o.Str("error", "no ReplayClickButton(int,...) on " + ct.Name); return o.Close(); }

            var mps = m.GetParameters();
            object[] args = new object[mps.Length];
            args[0] = index;
            for (int i = 1; i < mps.Length; i++) { args[i] = mps[i].HasDefaultValue ? mps[i].DefaultValue : null; }
            o.Str("via", (m.DeclaringType == null ? "" : m.DeclaringType.Name + ".") + m.Name + "(" + mps.Length + ")");
            try
            {
                m.Invoke(popup, args);
                o.Bool("ok", true);
                Log("popup: ReplayClickButton(" + index + ") via " + (m.DeclaringType == null ? "" : m.DeclaringType.Name));
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                o.Str("error", inner.GetType().Name + ": " + inner.Message);
                Log("popup: ReplayClickButton failed " + inner.Message);
            }
            return o.Close();
        }

        /// <summary>List the current popup's button data without invoking anything.</summary>
        /// <summary>
        /// Ask the game to advance its own popup queue. This is the game's own entry point
        /// (CommandTriggerUIUtils.TryShowNextCommandTrigger is what ShowCommandTrigger's button
        /// callbacks end with), so the popup is dismissed by the game itself after a real choice
        /// has been executed - never by force-closing or by fabricating event data.
        /// </summary>
        private string _pendingPopupAdvanceKey;

        internal static bool PendingPopupAdvanceAllowed(bool privateVisible, bool verified,
            bool ownTurn, bool? showing, bool currentReadable, bool currentPresent, int? pending)
        {
            return privateVisible && verified && ownTurn && showing == false
                && currentReadable && !currentPresent && pending.HasValue && pending.Value > 0;
        }

        private string StepCommandTrigger()
        {
            var gs = GameStateInstance();
            bool visible = gs != null && PrivateSamplingAllowed(gs);
            var identity = ResolveControlledIdentity(gs);
            object current;
            bool readable = TryReadCurrentPopup(out current);
            int? pending = visible && identity.Verified ? PendingTriggerCountFor(gs, identity.Id, new JsonArr()) : null;
            if (!PendingPopupAdvanceAllowed(visible, identity.Verified,
                gs != null && CurrentPlayerId(gs) == identity.Id, PopupIsOpen(), readable, current != null, pending))
                return UiPreparationError("PENDING_POPUP_NOT_READY", "requires an owned visible turn, no popup and known pending triggers");
            var frame = _combatFrame;
            if (frame == null || !frame.Complete || string.IsNullOrEmpty(_combatStream))
                return UiPreparationError("ACTION_CONTEXT_UNAVAILABLE", "visible state version unavailable");
            string key = frame.Episode + ":" + identity.Id + ":" + _combatStream + ":" + _combatVersion + ":" + pending;
            if (_pendingPopupAdvanceKey == key)
                return UiPreparationError("PENDING_POPUP_ALREADY_ADVANCED", "queue callback already attempted for this state; reobserve without replay");
            var t = FindGameType("CommandTriggerUIUtils");
            var method = UniqueInvocableMethod(t, "TryShowNextCommandTrigger", null, true);
            if (method == null || (method.ReturnType != typeof(void) && method.ReturnType != typeof(bool)))
                return UiPreparationError("PENDING_POPUP_CALLBACK_UNAVAILABLE", "native zero-argument queue callback unavailable");
            _pendingPopupAdvanceKey = key;
            try
            {
                LogUiCallback("dispatch", "CommandTriggerUIUtils.TryShowNextCommandTrigger");
                method.Invoke(null, null);
                LogUiCallback("returned", "CommandTriggerUIUtils.TryShowNextCommandTrigger");
                return new JsonObj().Bool("ok", true).Bool("callbackAttempted", true)
                    .Bool("queueAdvanceRequested", true).Str("outcome", "invoked")
                    .Str("invoked", "CommandTriggerUIUtils.TryShowNextCommandTrigger()")
                    .Str("reobserve", "required").Close();
            }
            catch (Exception error)
            { return UiCallbackFailure(new JsonObj(), "PENDING_POPUP_CALLBACK_FAILED", "queue", error); }
        }

        private string InvokePopupAction(Type t, string method)
        {
            var m = t.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (m == null) { return "{\"ok\":false,\"error\":\"PopupManager." + Esc(method) + " not found\"}"; }
            try
            {
                var r = m.Invoke(null, null);
                Log("popup: " + method + "() -> " + (r == null ? "null" : r.ToString()));
                return "{\"ok\":true,\"invoked\":\"PopupManager." + Esc(method) + "()\",\"returns\":\""
                    + Esc(r == null ? "null" : r.ToString()) + "\"}";
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Log("popup: " + method + " failed " + inner.Message);
                return "{\"ok\":false,\"invoked\":\"PopupManager." + Esc(method) + "()\",\"error\":\""
                    + Esc(inner.GetType().Name + ": " + inner.Message) + "\"}";
            }
        }

        /// <summary>
        /// Invoke only a single compatible static overload. Missing/ambiguous shapes remain unknown;
        /// an invocation failure cannot trigger a second method with potentially repeated effects.
        /// </summary>
        private static object InvokeStatic(Type t, string method, object[] args = null)
        {
            try
            {
                var chosen = UniqueInvocableMethod(t, method, args, true);
                return chosen == null ? null : chosen.Invoke(null, args);
            }
            catch (Exception ex) { RecordNativeReadFailure(t, method, ex); return null; }
        }

        private static bool TryFloat(object v, out float f)
        {
            f = 0f;
            if (v == null) { return false; }
            try { f = Convert.ToSingle(v, CultureInfo.InvariantCulture); return true; }
            catch { return false; }
        }

        private string ProbeTypes()
        {
            var sb = new StringBuilder("{\"ok\":true,\"types\":[");
            string[] names = { "Il2CppSystem.Type", "Il2CppInterop.Runtime.Il2CppType", "UnityEngine.Object", "UnityEngine.Resources" };
            bool first = true;
            foreach (var n in names)
            {
                var t = FindType(n);
                if (!first) { sb.Append(','); }
                first = false;
                sb.Append("{\"type\":\"").Append(Esc(n)).Append("\",\"found\":").Append(t != null ? "true" : "false");
                if (t != null)
                {
                    var names2 = new List<string>();
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        names2.Add(m.Name + "(" + string.Join(",", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name)) + ")");
                    }
                    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Static))
                    {
                        names2.Add("prop:" + p.Name + ":" + p.PropertyType.Name);
                    }
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                    {
                        names2.Add("field:" + f.Name + ":" + f.FieldType.Name);
                    }
                    sb.Append(",\"members\":[");
                    sb.Append(string.Join(",", Array.ConvertAll(names2.ToArray(), x => "\"" + Esc(x) + "\"")));
                    sb.Append(']');
                }
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Resolve live scene objects by name via GameObject.Find (avoids il2cpp Type marshalling):
        ///   {"cmd":"find","name":"NewGame"}     -> any GameObject whose name contains `name`
        ///   {"cmd":"find","name":"Button"}      -> up to 40 objects of that exact name
        /// Reports position/size/label so an agent can act on real UI instead of guessed pixels.
        /// </summary>
        private string FindObjects(string name)
        {
            if (name.Length == 0) { return "{\"ok\":false,\"error\":\"name required\"}"; }
            var goType = FindType("UnityEngine.GameObject");
            if (goType == null) { return "{\"ok\":false,\"error\":\"GameObject not found\"}"; }
            MethodInfo find = null;
            foreach (var m in goType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var ps = m.GetParameters();
                if (m.Name == "Find" && ps.Length == 1 && ps[0].ParameterType.Name == "String") { find = m; break; }
            }
            if (find == null) { return "{\"ok\":false,\"error\":\"GameObject.Find(string) missing\"}"; }

            var sb = new StringBuilder("{\"ok\":true,\"name\":\"").Append(Esc(name)).Append("\",\"objects\":[");
            int n = 0;
            try
            {
                var go = find.Invoke(null, new object[] { name });
                if (go != null)
                {
                    AppendObject(sb, ref n, go, name);
                }
            }
            catch (Exception ex)
            {
                return "{\"ok\":false,\"error\":\"Find threw: " + Esc((ex.InnerException ?? ex).Message) + "\"}";
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private void AppendObject(StringBuilder sb, ref int n, object go, string name)
        {
            if (n > 0) { sb.Append(','); }
            n++;
            sb.Append("{\"name\":\"").Append(Esc((Member(go, "name") ?? "?").ToString())).Append('"');
            sb.Append(",\"active\":").Append(Convert.ToBoolean(Member(go, "activeInHierarchy") ?? false) ? "true" : "false");
            var rt = Member(go, "transform");
            if (rt != null)
            {
                var pos = Member(rt, "position");
                if (pos != null)
                {
                    sb.Append(",\"worldPos\":[").Append(Component(pos, "x")).Append(',').Append(Component(pos, "y")).Append(']');
                    sb.Append(",\"screenPos\":[").Append(ScreenCoord(pos, "x", true)).Append(',').Append(ScreenCoord(pos, "y", false)).Append(']');
                }
            }
            sb.Append('}');
        }

        private string Component(object v, string axis)
        {
            var p = v.GetType().GetProperty(axis) ?? v.GetType().GetProperty(axis.ToUpperInvariant());
            if (p == null) { return "null"; }
            try { return Convert.ToDouble(p.GetValue(v, null)).ToString("0.##", CultureInfo.InvariantCulture); }
            catch { return "null"; }
        }

        private string ScreenCoord(object worldPos, string axis, bool isX)
        {
            try
            {
                var cam = FindType("UnityEngine.Camera");
                if (cam == null) { return "null"; }
                MethodInfo main = null;
                foreach (var m in cam.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "get_main" || m.Name == "get_current") { main = m; break; }
                }
                object camera = main != null ? main.Invoke(null, null) : null;
                if (camera == null) { return "null"; }
                MethodInfo w2s = null;
                foreach (var m in cam.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "WorldToScreenPoint") { continue; }
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType.Name == "Vector3") { w2s = m; break; }
                }
                if (w2s == null) { return "null"; }
                var screen = w2s.Invoke(camera, new[] { worldPos });
                return Component(screen, axis);
            }
            catch { return "null"; }
        }

        private string UiClick(string name)
        {
            Type t = FindType("UnityEngine.UI.Button");
            Type objType = FindType("UnityEngine.Object");
            if (t == null || objType == null)
            {
                return "{\"ok\":false,\"error\":\"uGUI types not found (Button=" + (t != null) + " Object=" + (objType != null) + ")\"}";
            }
            // il2cpp interop needs an Il2CppSystem.Type, not a System.Type.
            object il2cppType = Il2CppTypeOf(t);
            if (il2cppType == null) { return "{\"ok\":false,\"error\":\"could not build Il2CppSystem.Type\"}"; }

            // NOTE: Type.GetMethod(name, new[]{typeof(Type)}) fails against il2cpp interop
            // assemblies, so locate the overload by name/arity/parameter-name instead.
            MethodInfo find = null;
            foreach (var holder in new[] { FindType("UnityEngine.Resources"), objType })
            {
                if (holder == null) { continue; }
                foreach (var m in holder.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "FindObjectsOfTypeAll" && m.Name != "FindObjectsOfType") { continue; }
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType.Name == "Type") { find = m; break; }
                }
                if (find != null) { break; }
            }
            if (find == null) { return "{\"ok\":false,\"error\":\"no FindObjectsOfTypeAll(Type)\"}"; }

            object raw;
            try { raw = find.Invoke(null, new object[] { il2cppType }); }
            catch (Exception ex) { return "{\"ok\":false,\"error\":\"FindObjectsOfType threw: " + Esc((ex.InnerException ?? ex).Message) + "\"}"; }

            // bounded snapshot: stop at MaxUiButtons, then peek ONE extra element to distinguish
            // "exactly at the cap" from "genuinely more objects exist" - without reading the extra
            // element's members.
            var snapshot = new List<ButtonInfo>();
            bool truncated = false;
            var en = raw as IEnumerable;
            if (en != null)
            {
                foreach (var o in en)
                {
                    if (snapshot.Count >= MaxUiButtons) { truncated = true; break; }
                    if (o == null) { continue; }
                    snapshot.Add(SnapshotButton(o));
                }
            }
            Log("ui: snapshot buttons=" + snapshot.Count + " truncated=" + truncated + " query='" + name + "'");

            string plan = BridgeMod.PlanUiInvocation(snapshot, name, truncated, MaxUiButtons);
            if (BridgeMod.PlanIsReadOnly(plan)) { return plan; }
            if (BridgeMod.PlanIsRefusal(plan)) { return plan; }

            // resolve the single planned target and actually invoke it
            var target = BridgeMod.PlanTarget(plan);
            object component = null;
            if (target >= 0 && target < snapshot.Count) { component = snapshot[target].Component; }
            if (component == null) { return "{\"ok\":false,\"error\":\"planned target vanished\"}"; }

            string invoked = null, invokeError = null;
            try
            {
                var press = t.GetMethod("Press");
                if (press != null) { press.Invoke(component, null); }
                else
                {
                    var onClick = Member(component, "onClick");
                    var invoke = onClick != null ? onClick.GetType().GetMethod("Invoke") : null;
                    if (invoke == null) { throw new MissingMethodException("no Press()/onClick.Invoke()"); }
                    invoke.Invoke(onClick, null);
                }
                invoked = snapshot[target].Name;
                Log("ui: invoked '" + invoked + "'");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                invokeError = inner.GetType().Name + ": " + inner.Message;
                Log("ui: invoke failed for '" + snapshot[target].Name + "': " + invokeError);
            }
            return BridgeMod.RenderUiResult(snapshot, name, truncated, MaxUiButtons, invoked, invokeError);
        }

        private const int MaxUiButtons = 64;

        /// <summary>A button is described by a fixed whitelist; no unknown members are touched.</summary>
        internal struct ButtonInfo
        {
            public object Component;
            public string Name;
            public string Label;
            public string Active;   // "true" / "false" / "unknown"
            public string Pos;      // "x,y,z" or null
            public string MatchKey { get { return (Label ?? "") + "\u0000" + (Name ?? ""); } }
        }

        private ButtonInfo SnapshotButton(object o)
        {
            var b = new ButtonInfo();
            b.Component = o;
            b.Name = ObjectName(o);
            b.Label = ReadLabel(o);
            b.Active = ReadActive(o);
            var pos = WorldPosition(o);
            b.Pos = pos == null ? null : pos.ToString();
            return b;
        }

        /// <summary>
        /// Activity for click planning. Only gameObject.activeInHierarchy proves a button is live;
        /// component.enabled is deliberately NOT used as a substitute (an enabled component on an
        /// inactive GameObject is not clickable). The il2cpp interop exposes gameObject and
        /// activeInHierarchy as get_* METHODS (no CLR property - verified in
        /// UnityEngine.CoreModule interop: get_gameObject / get_activeInHierarchy), so the explicit
        /// accessor path is required. Unreadable => "unknown".
        /// </summary>
        private string ReadActive(object component)
        {
            var go = MemberWithAccessor(component, "gameObject");
            var v = go == null ? null : MemberWithAccessor(go, "activeInHierarchy");
            if (v is bool) { return ((bool)v) ? "true" : "false"; }
            return "unknown";
        }

        // ------------------------------------------------- ui plan / render (pure, offline-testable)

        /// <summary>Read-only snapshot: lists buttons, never invokes anything.</summary>
        internal static string RenderButtonsReadOnly(List<ButtonInfo> snapshot, string query, bool truncated, int cap)
        {
            var o = new JsonObj();
            o.Bool("ok", true);
            o.Str("mode", "read");
            o.Str("query", query ?? "");
            o.Raw("count", snapshot.Count.ToString(CultureInfo.InvariantCulture));
            o.Bool("truncated", truncated);
            o.Raw("cap", cap.ToString(CultureInfo.InvariantCulture));
            var arr = new JsonArr();
            foreach (var b in snapshot) { arr.Raw(RenderButton(b)); }
            o.Raw("buttons", arr.Close());
            return o.Close();
        }

        internal static string RenderButton(ButtonInfo b)
        {
            var o = new JsonObj();
            o.Str("name", b.Name ?? "?");
            if (!string.IsNullOrEmpty(b.Label)) { o.Str("label", b.Label); }
            o.Str("active", b.Active ?? "unknown");
            if (!string.IsNullOrEmpty(b.Pos)) { o.Str("pos", b.Pos); }
            return o.Close();
        }

        /// <summary>
        /// Decide what to do for a query. Returns a complete read-only/refusal frame, or a marker
        /// frame carrying the single target index to invoke.
        ///
        /// Clicking requires an unambiguous match inside a COMPLETE snapshot and an explicitly
        /// active ("true") button. "unknown" activity is never clickable, and an incomplete
        /// snapshot (truncated) refuses named clicks with snapshot_incomplete - widening the cap
        /// would only hide the completeness problem.
        /// </summary>
        internal static string PlanUiInvocation(List<ButtonInfo> all, string name, bool truncated, int cap)
        {
            // listed: for diagnostics, everything not explicitly inactive
            var listed = new List<int>();
            // clickable: only buttons proven active
            var clickable = new List<int>();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Active == "false") { continue; }
                listed.Add(i);
                if (all[i].Active == "true") { clickable.Add(i); }
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                var only = new List<ButtonInfo>();
                foreach (var i in listed) { only.Add(all[i]); }
                Log("ui: read-only listing of " + only.Count + " candidate button(s)");
                return RenderButtonsReadOnly(only, name, truncated, cap);
            }

            if (truncated)
            {
                var o = new JsonObj();
                o.Bool("ok", false);
                o.Str("error", "snapshot_incomplete");
                o.Str("query", name);
                o.Str("note", "named invocation requires a complete button snapshot; increase nothing, re-query when fewer objects exist");
                o.Bool("truncated", true);
                o.Raw("cap", cap.ToString(CultureInfo.InvariantCulture));
                o.Raw("snapshotSize", all.Count.ToString(CultureInfo.InvariantCulture));
                return o.Close();
            }

            // exact label match wins; otherwise unique substring match is allowed
            var exact = new List<int>();
            foreach (var i in clickable)
            {
                if (string.Equals(all[i].Label, name, StringComparison.OrdinalIgnoreCase)) { exact.Add(i); }
            }
            var pool = exact;
            string how = "label-exact";
            if (pool.Count == 0)
            {
                how = "substring";
                foreach (var i in clickable)
                {
                    if (all[i].MatchKey.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) { pool.Add(i); }
                }
            }

            if (pool.Count == 0)
            {
                var o = new JsonObj();
                o.Bool("ok", false);
                o.Str("error", "no button matches query");
                o.Str("query", name);
                o.Bool("truncated", false);
                o.Raw("count", clickable.Count.ToString(CultureInfo.InvariantCulture));
                return o.Close();
            }

            if (pool.Count > 1)
            {
                var o = new JsonObj();
                o.Bool("ok", false);
                o.Str("error", "ambiguous button query: " + pool.Count + " matches");
                o.Str("query", name);
                o.Str("matched_by", how);
                var arr = new JsonArr();
                foreach (var i in pool) { arr.Raw(RenderButton(all[i])); }
                o.Raw("candidates", arr.Close());
                return o.Close();
            }

            var marker = new JsonObj();
            marker.Bool("ok", true);
            marker.Str("__plan", "invoke");
            marker.Raw("target", pool[0].ToString(CultureInfo.InvariantCulture));
            return marker.Close();
        }

        internal static bool PlanIsReadOnly(string frame) { return frame != null && frame.IndexOf("\"mode\":\"read\"", StringComparison.Ordinal) >= 0; }
        internal static bool PlanIsRefusal(string frame) { return frame != null && !PlanIsReadOnly(frame) && frame.IndexOf("\"__plan\"", StringComparison.Ordinal) < 0; }

        internal static int PlanTarget(string frame)
        {
            if (frame == null) { return -1; }
            const string key = "\"target\":";
            int i = frame.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) { return -1; }
            i += key.Length;
            int end = i;
            while (end < frame.Length && (char.IsDigit(frame[end]) || frame[end] == '-')) { end++; }
            int v;
            return int.TryParse(frame.Substring(i, end - i), out v) ? v : -1;
        }

        internal static string RenderUiResult(List<ButtonInfo> all, string name, bool truncated, int cap, string invoked, string invokeError)
        {
            var o = new JsonObj();
            o.Bool("ok", invokeError == null);
            o.Str("mode", "invoke");
            o.Str("query", name ?? "");
            o.Raw("count", all.Count.ToString(CultureInfo.InvariantCulture));
            o.Bool("truncated", truncated);
            if (invoked != null) { o.Str("invoked", invoked); }        // only set when the call returned
            if (invokeError != null) { o.Str("invokeError", invokeError); }
            return o.Close();
        }

        // ---------------------------------------------- triggers render (pure, offline-testable)

        /// <summary>
        /// Render one CommandTrigger from a whitelist of its real members (playerId, opponentId,
        /// type, coordinates). Unknown types/members are reported as unsupported instead of being
        /// reflected generically.
        /// </summary>
        internal static string RenderTrigger(bool supportedType, string typeName, Dictionary<string, string> whitelisted, bool anyRead)
        {
            var o = new JsonObj();
            if (!supportedType)
            {
                o.Bool("ok", false);
                o.Str("unsupported_type", typeName ?? "unknown");
                return o.Close();
            }
            o.Bool("ok", true);
            if (!anyRead)
            {
                o.Bool("unsupported", true);
                o.Str("note", "no whitelisted member readable");
                o.Str("type", typeName ?? "");
                return o.Close();
            }
            var fields = new JsonObj();
            foreach (var kv in whitelisted)
            {
                if (kv.Value == null) { continue; }
                fields.Str(kv.Key, kv.Value);
            }
            o.Str("type", typeName ?? "");
            o.Nested("fields", fields);
            return o.Close();
        }

        internal static string RenderTriggers(bool ok, string error, string raw, string typeName, List<string> triggerFrames, bool truncated, int cap)
        {
            var o = new JsonObj();
            o.Bool("ok", ok);
            if (error != null) { o.Str("error", error); }
            o.NullOrStr("raw", raw);
            if (!string.IsNullOrEmpty(typeName)) { o.Str("elementType", typeName); }
            o.Bool("truncated", truncated);
            o.Raw("cap", cap.ToString(CultureInfo.InvariantCulture));
            var arr = new JsonArr();
            foreach (var f in triggerFrames) { arr.Raw(f); }
            o.Raw("triggers", arr.Close());
            return o.Close();
        }

        /// <summary>System.Type -> Il2CppSystem.Type via Il2CppInterop.Runtime.Il2CppType.From.</summary>
        private static object Il2CppTypeOf(Type t)
        {
            try
            {
                var conv = FindType("Il2CppInterop.Runtime.Il2CppType");
                if (conv == null) { return null; }
                foreach (var m in conv.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "From") { continue; }
                    var ps = m.GetParameters();
                    if (ps.Length >= 1 && ps[0].ParameterType.Name == "Type")
                    {
                        var args = new object[ps.Length];
                        args[0] = t;
                        for (int i = 1; i < ps.Length; i++)
                        {
                            args[i] = ps[i].ParameterType == typeof(bool) ? (object)false : null;
                        }
                        return m.Invoke(null, args);
                    }
                }
            }
            catch (Exception ex) { Log("Il2CppTypeOf failed: " + ex.Message); }
            return null;
        }

        private static object Member(object o, params string[] path)        {
            object cur = o;
            foreach (var p in path)
            {
                if (cur == null) { return null; }
                var t = cur.GetType();
                var prop = t.GetProperty(p);
                if (prop != null) { cur = prop.GetValue(cur, null); continue; }
                var fld = t.GetField(p);
                if (fld != null) { cur = fld.GetValue(cur); continue; }
                return null;
            }
            return cur;
        }

        /// <summary>
        /// Robust component name. The il2cpp interop exposes gameObject/transform as get_* methods,
        /// so a plain property read fails; try that first, then walk up via transform.parent.
        /// </summary>
        private static string ObjectName(object component)
        {
            var direct = Member(component, "name");
            if (direct != null) { return direct.ToString(); }
            var viaAccessor = MemberWithAccessor(component, "name");
            if (viaAccessor != null) { return viaAccessor.ToString(); }
            var go = MemberWithAccessor(component, "gameObject");
            var goName = go == null ? null : MemberWithAccessor(go, "name");
            if (goName != null) { return goName.ToString(); }
            var tr = MemberWithAccessor(component, "transform");
            var parent = tr == null ? null : MemberWithAccessor(tr, "parent");
            var parentName = parent == null ? null : MemberWithAccessor(parent, "name");
            if (parentName != null) { return "<parent>" + parentName; }
            return "?";
        }

        /// <summary>Screen-ish position of a UI element (transform.position), for layout-free targeting.</summary>
        private static object WorldPosition(object component)
        {
            try
            {
                var tr = Member(component, "transform");
                if (tr == null) { return null; }
                var pos = Member(tr, "position");
                return pos;
            }
            catch { return null; }
        }

        private string ReadLabel(object button)
        {
            try
            {
                var getInChildren = button.GetType().GetMethod("GetComponentInChildren", new[] { typeof(Type) });
                var textType = FindType("UnityEngine.UI.Text");
                if (getInChildren != null && textType != null)
                {
                    var txt = getInChildren.Invoke(button, new object[] { textType });
                    if (txt != null) { return Member(txt, "text") as string ?? ""; }
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Ask the game itself to create and run commands for the local player. This is the same
        /// entry point the built-in AI uses, so an agent can hand a turn to the game's own logic.
        /// </summary>
        private string AiCommands()
        {
            if (_gameManagerType == null) { return "{\"ok\":false,\"error\":\"no GameManager\"}"; }
            var names = new[] { "TryCreateAndExecuteAICommands", "TryCreateAndExecuteAICommandsCoroutine" };
            foreach (var want in names)
            {
                foreach (var m in _gameManagerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name != want) { continue; }
                    if (m.GetParameters().Length != 0) { continue; }
                    object target = _gameManagerType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic) != null
                        ? SafeInstance()
                        : null;
                    if (target == null) { return "{\"ok\":false,\"error\":\"GameManager.instance is null\"}"; }
                    try
                    {
                        var r = m.Invoke(target, null);
                        return "{\"ok\":true,\"invoked\":\"" + Esc(m.Name + "()") + "\",\"returns\":\""
                            + Esc(r == null ? "null" : r.ToString()) + "\"}";
                    }
                    catch (Exception ex)
                    {
                        var inner = ex.InnerException ?? ex;
                        return "{\"ok\":false,\"invoked\":\"" + Esc(m.Name + "()") + "\",\"error\":\""
                            + Esc(inner.GetType().Name + ": " + inner.Message) + "\"}";
                    }
                }
            }
            return "{\"ok\":false,\"error\":\"no AI command entry point\"}";
        }

        private object SafeInstance()
        {
            return StaticMember(_gameManagerType, "instance");
        }

        /// <summary>Lists turn/action entry points so the action vocabulary can be bound precisely.</summary>
        private string ProbeActions()
        {
            var sb = new StringBuilder("{\"ok\":true,\"type\":\"api_probe\",\"types\":[");
            string[] typeNames = { "Il2Cpp.GameManager", "GameManager", "Il2Cpp.ActionManager", "ActionManager" };
            bool firstType = true;
            foreach (var tn in typeNames)
            {
                var t = FindType(tn);
                if (t == null) { continue; }
                if (!firstType) { sb.Append(','); }
                firstType = false;
                sb.Append("{\"type\":\"").Append(Esc(t.FullName)).Append("\",\"methods\":[");
                bool first = true;
                var names = new List<string>();
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                {
                    var n = m.Name;
                    if (n.StartsWith("get_") || n.StartsWith("set_") || n.StartsWith("add_") || n.StartsWith("remove_")) { continue; }
                    var sig = n + "(" + m.GetParameters().Length + ")";
                    if (names.Contains(sig)) { continue; }
                    names.Add(sig);
                }
                names.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var n in names)
                {
                    if (!first) { sb.Append(','); }
                    first = false;
                    sb.Append('"').Append(Esc(n)).Append('"');
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private string ApiReport()
        {
            var sb = new StringBuilder("{\"ok\":true,\"type\":\"api\",\"gameManager\":\"")
                .Append(Esc(_gameManagerType != null ? _gameManagerType.FullName : "null"))
                .Append("\",\"gameState\":\"").Append(Esc(_gameStateType != null ? _gameStateType.FullName : "null"))
                .Append("\",\"gameManagerStatics\":[");
            if (_gameManagerType != null)
            {
                var names = new List<string>();
                foreach (var p in _gameManagerType.GetProperties(BindingFlags.Public | BindingFlags.Static))
                { names.Add(p.Name + ":" + p.PropertyType.Name); }
                sb.Append(string.Join(",", Array.ConvertAll(names.ToArray(), x => "\"" + Esc(x) + "\"")));
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>One-shot structural dump of live PlayerState / MapData so the obs schema can be finalised.</summary>
        private string StateReport()
        {
            var gs = GameStateInstance();
            if (gs == null) { return "{\"ok\":false,\"error\":\"no active game\"}"; }
            var sb = new StringBuilder("{\"ok\":true,\"type\":\"state\"");
            sb.Append(",\"gameStateMembers\":[");
            var members = new List<string>();
            foreach (var p in _gameStateType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            { members.Add(p.Name + ":" + p.PropertyType.Name); }
            sb.Append(string.Join(",", Array.ConvertAll(members.ToArray(), x => "\"" + Esc(x) + "\"")));
            sb.Append(']');

            var list = AsEnumerable(Val(_playersProp, gs));
            if (list != null)
            {
                sb.Append(",\"playerStateMembers\":[");
                var pm = new List<string>();
                foreach (var p in list)
                {
                    if (p == null) { continue; }
                    foreach (var q in p.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    { pm.Add(q.Name + ":" + q.PropertyType.Name); }
                    break;
                }
                sb.Append(string.Join(",", Array.ConvertAll(pm.ToArray(), x => "\"" + Esc(x) + "\"")));
                sb.Append(']');
            }
            else
            {
                var rawList = Val(_playersProp, gs);
                sb.Append(",\"playerStatesRaw\":\"").Append(Esc(rawList == null ? "null" : rawList.GetType().FullName)).Append('"');
                sb.Append(",\"playerStatesCount\":").Append(Num(Grab(rawList != null ? rawList.GetType() : typeof(object), rawList, "Count", "Length")));
                sb.Append(",\"playerStatesIEnumerable\":").Append(rawList is IEnumerable ? "true" : "false");
                sb.Append(",\"playerStatesInterfaces\":[");
                if (rawList != null)
                {
                    var ifs = new List<string>();
                    foreach (var i in rawList.GetType().GetInterfaces()) { ifs.Add(i.FullName); }
                    sb.Append(string.Join(",", Array.ConvertAll(ifs.ToArray(), x => "\"" + Esc(x) + "\"")));
                }
                sb.Append(']');
            }

            var map = Val(_mapProp, gs);
            if (map != null)
            {
                sb.Append(",\"mapDataMembers\":[");
                var mm = new List<string>();
                foreach (var q in map.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                { mm.Add(q.Name + ":" + q.PropertyType.Name); }
                sb.Append(string.Join(",", Array.ConvertAll(mm.ToArray(), x => "\"" + Esc(x) + "\"")));
                sb.Append(']');
            }
            sb.Append('}');
            return sb.ToString();
        }

        internal static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch (Exception error) { RecordLogWriteFailure(error); }
        }

        internal static string Esc(string s)
        {
            if (s == null) { return ""; }
            return BridgeJsonCodec.Escape(s);
        }
    }

    /// <summary>
    /// Minimal JSON object/array writer. Exists because hand-rolled comma bookkeeping produced
    /// malformed frames twice (UiClick appending properties after the closing brace; TriggerDump
    /// emitting "{,\"fields\":..." when the properties section was empty). Every value is written
    /// through a delimiter helper, and empty objects/arrays stay syntactically valid.
    /// Pure text logic: no game state, no reflection - so it is unit-testable offline.
    /// </summary>
    internal sealed class JsonObj
    {
        private readonly List<string> _keys = new List<string>();
        private readonly List<string> _values = new List<string>();
        private readonly Dictionary<string, int> _indices = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly StringBuilder _destination;
        public JsonObj() { }
        public JsonObj(StringBuilder sb) { _destination = sb; }
        public bool IsEmpty { get { return _keys.Count == 0; } }
        private JsonObj Put(string key, string raw)
        {
            int index;
            if (key != null && _indices.TryGetValue(key, out index)) _values[index] = raw ?? "null";
            else
            {
                if (key != null) _indices.Add(key, _keys.Count);
                _keys.Add(key); _values.Add(raw ?? "null");
            }
            return this;
        }
        public JsonObj Raw(string key, string rawJson) { return Put(key, rawJson); }
        public JsonObj Str(string key, string value) { return Put(key, "\"" + BridgeMod.Esc(value ?? "") + "\""); }
        public JsonObj Bool(string key, bool value) { return Put(key, value ? "true" : "false"); }
        public JsonObj NullableBool(string key, bool? value)
        { return Put(key, value.HasValue ? (value.Value ? "true" : "false") : "null"); }
        public JsonObj NullOrStr(string key, string value)
        { return Put(key, value == null ? "null" : "\"" + BridgeMod.Esc(value) + "\""); }
        public JsonObj Nested(string key, JsonObj child) { return Put(key, child == null ? "null" : child.Close()); }
        public string Close()
        {
            var output = new StringBuilder("{");
            for (int i = 0; i < _keys.Count; i++)
            {
                if (i > 0) output.Append(',');
                if (_keys[i] != null) output.Append('"').Append(BridgeMod.Esc(_keys[i])).Append("\":");
                output.Append(_values[i]);
            }
            output.Append('}');
            if (_destination != null) { _destination.Clear(); _destination.Append(output); }
            return output.ToString();
        }
    }

    /// <summary>JSON array of raw elements with correct comma placement.</summary>
    internal sealed class JsonArr
    {
        private readonly StringBuilder _sb;
        private bool _first = true;

        public JsonArr() { _sb = new StringBuilder("["); }
        public bool IsEmpty { get { return _first; } }

        public JsonArr Raw(string rawJson)
        {
            if (_first) { _first = false; } else { _sb.Append(','); }
            _sb.Append(rawJson);
            return this;
        }

        public string Close() { _sb.Append(']'); return _sb.ToString(); }
    }

    /// <summary>Minimal JSON field reader: good enough for flat request envelopes.</summary>
    internal static class Json
    {
        public static string GetString(string json, string key)
        {
            try
            {
                object value;
                var obj = BridgeJsonCodec.Object(json);
                return obj.TryGetValue(key, out value) ? BridgeJsonCodec.Scalar(value) : "";
            }
            catch (Exception) { return ""; }
        }
    }
}
