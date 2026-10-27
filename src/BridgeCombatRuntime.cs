// Player-visible event journal and native-pipeline settlement tracking.
// The journal never stores raw enemy states; only TileUnitVisible projections survive a sample.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal sealed class CombatUnitView
        {
            internal string Id, Owner, Type, Coord, PassengerId, Effects, PublicInfo;
            internal int? Health, Promotion, Xp;
            internal bool? Moved, Attacked, CanMove, CanAttack;
            internal bool? Frozen, AutoFreeze, FreezeArea, FreezeAttack;
            internal bool? Infiltrate, DoubleAttack, Swarm, Explode, Eat, PoisonAttack;
            internal bool? Poisoned, Swift;
            internal string PoisonedAuthority, SwiftAuthority, Links;
            internal string FrozenAuthority;
            internal string Signature() { return Id + ":" + Owner + ":" + Type + ":" + Coord + ":" + Health + ":" + Promotion + ":" + Xp + ":" + Moved + ":" + Attacked + ":" + CanMove + ":" + CanAttack + ":" + PassengerId + ":" + Effects + ":" + PublicInfo + ":" + Frozen + ":" + Poisoned + ":" + Swift + ":" + Links; }
            internal string Json()
            {
                return "{\"unitId\":" + CRQ(Id) + ",\"owner\":" + CRQ(Owner) + ",\"type\":" + CRQ(Type)
                    + ",\"coords\":" + CRQ(Coord) + ",\"health\":" + CRN(Health) + ",\"promotionLevel\":" + CRN(Promotion)
                    + ",\"xp\":" + CRN(Xp) + ",\"moved\":" + CRB(Moved) + ",\"attacked\":" + CRB(Attacked)
                    + ",\"canMove\":" + CRB(CanMove) + ",\"canAttack\":" + CRB(CanAttack)
                    + ",\"passengerId\":" + CRQ(PassengerId) + ",\"effects\":" + (Effects ?? "null")
                    + ",\"frozen\":" + CRB(Frozen) + ",\"frozenAuthority\":" + CRQ(FrozenAuthority)
                     + ",\"poisoned\":" + CRB(Poisoned) + ",\"poisonedAuthority\":" + CRQ(PoisonedAuthority)
                    + ",\"swift\":" + CRB(Swift) + ",\"swiftAuthority\":" + CRQ(SwiftAuthority)
                    + ",\"relations\":" + (Links ?? "null")
                    + ",\"publicInfo\":" + (PublicInfo ?? "null") + "}";
            }
        }

        internal sealed class CombatFrame
        {
            internal string Episode, Me, Turn, Acting, Phase, Stars;
            internal bool Complete;
            internal readonly Dictionary<string, CombatUnitView> Units = new Dictionary<string, CombatUnitView>(StringComparer.Ordinal);
            // Own improvements only: an explored enemy city does not grant its later hidden owner.
            internal readonly Dictionary<string, string> OwnTileStates = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> OwnBuildingStates = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> OwnResources = new Dictionary<string, string>(StringComparer.Ordinal);
            // Same revealed public terrain roster as world.tiles; no map-wide ice counts.
            internal readonly Dictionary<string, string> PublicTerrains = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, PublicFloodView> PublicFloodTiles = new Dictionary<string, PublicFloodView>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> OwnImprovements = new Dictionary<string, string>(StringComparer.Ordinal);
            // The own unit's public warning, never the hidden unit causing it.
            internal readonly Dictionary<string, string> OwnInvisibleThreatHints = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, bool> RuinsUnderOwnUnits = new Dictionary<string, bool>(StringComparer.Ordinal);
            internal readonly Dictionary<string, bool> CitiesUnderOwnUnits = new Dictionary<string, bool>(StringComparer.Ordinal);
            internal string Signature()
            {
                var p = new List<string>();
                foreach (var u in Units.Values) p.Add("U=" + u.Signature());
                foreach (var x in OwnImprovements) p.Add("I=" + x.Key + ":" + x.Value);
                foreach (var x in OwnTileStates) p.Add("T=" + x.Key + ":" + x.Value);
                foreach (var x in OwnBuildingStates) p.Add("B=" + x.Key + ":" + x.Value);
                foreach (var x in OwnResources) p.Add("V=" + x.Key + ":" + x.Value);
                foreach (var x in PublicTerrains) p.Add("P=" + x.Key + ":" + x.Value);
                foreach (var x in PublicFloodTiles) p.Add("F=" + x.Key + ":" + x.Value.Json());
                foreach (var x in OwnInvisibleThreatHints)
                {
                    CombatUnitView own;
                    if (Units.TryGetValue(x.Key, out own) && own.Owner == Me) p.Add("H=" + x.Key + ":" + x.Value);
                }
                foreach (var x in RuinsUnderOwnUnits) p.Add("R=" + x.Key + ":" + x.Value);
                foreach (var x in CitiesUnderOwnUnits) p.Add("C=" + x.Key + ":" + x.Value);
                p.Sort(StringComparer.Ordinal);
                return Episode + "|" + Me + "|" + Turn + "|" + Acting + "|" + Phase + "|" + Stars + "|" + string.Join(";", p.ToArray());
            }
        }

        internal sealed class CombatCause
        {
            internal string ActionType, ActorId, ActorOwner, TargetCoord, TargetId, AppliedTrainType;
            internal bool VisibleNativeAction, NativeAttract;
        }

        internal sealed class CombatTicket
        {
            internal readonly CymantiAlgaeBuildProof CymantiAlgaeBuild=new CymantiAlgaeBuildProof();
            internal long Id, RequestId, BeforeVersion, StartEvent;
            internal string Stream, Action, UnitId, Coord, Arg, BeforeFingerprint;
            internal CombatFrame Before;
            internal bool? Accepted;
            internal bool Dispatched;
            internal CombatFrame FinalFrame;
            internal long FinalVersion, FinalEvent;
            internal string FinalFingerprint;
            internal bool? FinalPipelineIdle;
            internal string FinalCompletionAuthority;
            internal object EndTurnClient;
            internal string EndTurnGameId, PublicHandoffId, PublicHandoffTurn, PublicHandoffTarget;
            internal readonly HashSet<string> NativeSpawnIds = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> NativeKilledIds = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> NativeAttackTargets = new HashSet<string>(StringComparer.Ordinal);
            internal CombatUnitView NativePrimaryBefore, NativePrimaryAfter;
            internal CombatUnitView NativeRetaliationBefore, NativeRetaliationAfter;
            internal readonly HashSet<string> NativeAttackKilledIds = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<object> NativeAreaActions = new List<object>();
            internal readonly List<AreaDamageObservation> NativeAreaDamage = new List<AreaDamageObservation>();
            internal readonly HashSet<string> NativeAreaKilledIds = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<string> NativeConversions = new List<string>();
            internal readonly HashSet<string> NativeConvertTargets = new HashSet<string>(StringComparer.Ordinal);
            internal bool NativeInfiltrateObserved;
            internal int? NativeVersion;
            internal readonly List<object> NativeInfiltrationRewards = new List<object>();
            internal readonly List<object> NativeInfiltrationTrains = new List<object>();
            internal readonly HashSet<string> NativeInfiltrationSpawnIds = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<object> NativeBoostActions = new List<object>();
            internal readonly HashSet<string> NativeBoostTargets = new HashSet<string>(StringComparer.Ordinal);
            internal bool NativeRecoverObserved;
            internal string NativeFloodBefore, NativeFloodAfter;
            internal string NativeClearFloodBefore, NativeClearFloodAfter;
            internal object NativeClearFloodAction;
            internal string NativeCanalBefore, NativeCanalAfter;
            internal bool NativeCanalBuildObserved;
            internal readonly List<object> NativeCanalFloodActions = new List<object>();
            internal bool NativeEnchantBuildObserved;
            internal readonly List<object> NativeEnchantTrains = new List<object>();
            internal CombatUnitView NativeEnchantedUnit;
            internal readonly PolarisProof Polaris = new PolarisProof();
            internal readonly CymantiProof Cymanti = new CymantiProof();
            internal readonly CymantiDecomposeProof CymantiDecompose = new CymantiDecomposeProof();
            internal readonly List<object> NativeHealActions = new List<object>();
            internal readonly HashSet<string> NativeHealTargets = new HashSet<string>(StringComparer.Ordinal);
            internal RuinsObservation NativeRuins;
            internal CityRewardObservation NativeCityReward;
        }

        private sealed class CombatHookPass
        {
            internal object State;
            internal CombatCause Cause;
            internal string CandidateKind, CandidateCoord;
            internal bool Closed;
            internal object Action;
            internal CombatFrame Before;
            internal int? BeforeStackCount;
            internal HashSet<string> RuinsTechsBefore;
            internal bool? RuinsExploredBefore;
            internal readonly List<CombatTicket> ProofTickets = new List<CombatTicket>();
        }

        private static BridgeMod _combatHookHost;
        [ThreadStatic] private static int _combatSamplingDepth;
        private CombatFrame _combatFrame;
        private string _combatStream = Guid.NewGuid().ToString("N"), _combatSignature;
        private long _combatVersion, _combatEventId, _combatTicketId;
        private long _combatLastChangeTimestamp;
        private long _combatLastSampleTick = -1;
        private int _combatStableFrames, _combatHookDepth;
        private int _combatHookInstalled, _combatHookFailed;
        private bool _combatCaptureAvailable;
        private bool _combatInstallationAttempted;
        private readonly List<KeyValuePair<long, string>> _combatEvents = new List<KeyValuePair<long, string>>();
        private readonly Dictionary<long, CombatTicket> _combatTickets = new Dictionary<long, CombatTicket>();
        private readonly List<CombatCause> _combatCauseStack = new List<CombatCause>();
        private const int CombatJournalCapacity = 2048;

        internal void InitializeCombatRuntime()
        {
            if (_combatInstallationAttempted) return;
            var state = GameStateInstance();
            if (state == null || !ResolveControlledIdentity(state).Verified) return;
            _combatInstallationAttempted = true;
            _combatHookHost = this;
            var actionBase = FindGameType("ActionBase");
            if (actionBase == null || HarmonyInstance == null)
            {
                foreach (var name in CombatHookTypes) CRRegisterHookInstallation(name, "unavailable");
                return;
            }
            InstallHotseatBoundaryHooks();
            InstallAreaDamageHook();
            // Small audited effect set. Never sweep every loaded assembly at startup: IL2CPP
            // shared stubs and recursive observation calls make blanket native hooks unsafe.
            foreach (var name in CombatHookTypes)
            {
                var type = FindGameType(name);
                if (!CRHookTargetAllowed(type,actionBase,FindGameType("CommandBase"))) { CRRegisterHookInstallation(name, "missing_type"); Log("combat hook: missing " + name); continue; }
                var method = CRFindExecuteHook(type);
                if (method != null)
                {
                    Log("combat hook: patch begin " + name + ".Execute(GameState)");
                    try
                    {
                        var patched = HarmonyInstance.Patch(method, CRHook("CombatActionPrefix"), CRHook("CombatActionPostfix"), null, CRHook("CombatActionFinalizer"), null);
                        CRRegisterHookInstallation(name, patched != null ? "installed" : "unavailable");
                        Log("combat hook: patch end " + name + " " + (patched != null ? "installed" : "unavailable"));
                    }
                    catch (Exception ex) { CRRegisterHookInstallation(name, "patch_failed"); Log("combat hook: patch failed " + name + " " + ex.GetType().Name); }
                }
                else { CRRegisterHookInstallation(name, "signature_unavailable"); Log("combat hook: unique Execute(GameState) signature unavailable " + name); }
            }
            Log("combat events: native action hooks installed=" + _combatHookInstalled + " failed=" + _combatHookFailed);
        }

        private void InstallHotseatBoundaryHooks()
        {
            PatchHotseatBoundary("ClientBase", "SetNewLocalPlayerTurnForPassAndPlay", false, new[] { typeof(byte) }, "HotseatClientBoundaryPrefix", null);
            PatchHotseatBoundary("ClientBase", "SeekToCommand", false, null, "HotseatClientBoundaryPrefix", null);
            PatchHotseatBoundary("ClientBase", "SkipRecap", false, Type.EmptyTypes, "HotseatClientBoundaryPrefix", null);
            PatchHotseatBoundary("ClientBase", "OnStateUpdated", false, null, null, "HotseatStateUpdatedPostfix");
            PatchHotseatBoundary("ClientActionManager", "Resume", false, Type.EmptyTypes, null, "HotseatResumePostfix");
            // Resume writes isRecapping itself, so StartRecap alone misses this path.
            PatchHotseatBoundary("GameEvents", "RecapStarted", true, Type.EmptyTypes, "HotseatRecapStartedPrefix", null);
        }

        private void PatchHotseatBoundary(string typeName, string methodName, bool isStatic, Type[] exactArgs, string prefix, string postfix)
        {
            try
            {
                Type type = FindGameType(typeName); if (type == null) return;
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != methodName || method.IsStatic != isStatic || method.ReturnType != typeof(void)) continue;
                    var args = method.GetParameters();
                    bool match = exactArgs == null ?
                        (methodName == "OnStateUpdated" ? args.Length == 1 && args[0].ParameterType.Name == "StateUpdateReason" :
                            args.Length == 2 && args[0].ParameterType == typeof(ushort) && args[1].ParameterType.Name.StartsWith("Action`1", StringComparison.Ordinal)) : args.Length == exactArgs.Length;
                    if (match && exactArgs != null)
                        for (int i = 0; i < args.Length; i++) if (args[i].ParameterType != exactArgs[i]) match = false;
                    if (!match) continue;
                    var patched = HarmonyInstance.Patch(method, prefix == null ? null : CRHook(prefix), postfix == null ? null : CRHook(postfix), null, null, null);
                    Log("hotseat boundary hook " + typeName + "." + methodName + (patched == null ? " unavailable" : " installed"));
                    return;
                }
                Log("hotseat boundary hook " + typeName + "." + methodName + " signature unavailable");
            }
            catch (Exception error) { Log("hotseat boundary hook " + typeName + "." + methodName + " " + error.GetType().Name); }
        }

        private static void HotseatClientBoundaryPrefix(object __instance)
        { var host = _combatHookHost; if (host != null) try { host.BeginHotseatBoundary(__instance); } catch { } }
        private static void HotseatStateUpdatedPostfix(object __instance)
        { var host = _combatHookHost; if (host != null) try { host.CompleteHotseatStateUpdate(__instance); } catch { } }
        private static void HotseatResumePostfix(object __instance)
        {
            var host = _combatHookHost;
            if (host != null) try
            {
                if (CRSameNative(__instance, MemberWithAccessor(host.MainClient(), "ActionManager"))) host.TryFinishHotseatWithoutRecap();
            }
            catch { }
        }
        private static void HotseatRecapStartedPrefix()
        { var host = _combatHookHost; if (host != null) try { host.StartHotseatVisibleRecap(); } catch { } }

        private static MethodInfo CRFindExecuteHook(Type type)
        {
            if (type == null) return null;
            MethodInfo selected = null;
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!NativeMethodSignatureMatches(method, "Execute", new[] {"GameState"}, "Void")
                    || method.ReturnType != typeof(void)) continue;
                if (selected != null) return null;
                selected = method;
            }
            return selected;
        }

        private static HarmonyMethod CRHook(string name)
        { return new HarmonyMethod(typeof(BridgeMod).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)); }

        private static bool CRSameNative(object a, object b)
        {
            if (object.ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            object pa = MemberWithAccessor(a, "Pointer"), pb = MemberWithAccessor(b, "Pointer");
            return pa is IntPtr && pb is IntPtr && (IntPtr)pa != IntPtr.Zero && (IntPtr)pa == (IntPtr)pb;
        }

        private static void CombatActionPrefix(object __instance, object __0, MethodBase __originalMethod, out CombatHookPass __state)
        { using (MeasureCommandCallback()) CombatActionPrefixCore(__instance, __0, __originalMethod, out __state); }

        private static void CombatActionPrefixCore(object __instance, object __0, MethodBase __originalMethod, out CombatHookPass __state)
        {
            __state = null;
            var h = _combatHookHost;
            if (h == null || _combatSamplingDepth > 0) return;
            if (__originalMethod != null && __originalMethod.DeclaringType.Name == "IncreaseCurrencyAction"
                && !h.CRHasPendingRuinsCurrency(__instance)) return;
            if (__originalMethod != null && __originalMethod.DeclaringType.Name == "ResearchAction"
                && !h.CRHasPendingRuinsResearch(__instance)) return;
            if (__originalMethod != null && __originalMethod.DeclaringType.Name == "IncreasePopulationAction"
                && !h.CRHasPendingRuinsPopulation(__instance)) return;
            if (__originalMethod != null && (__originalMethod.DeclaringType.Name == "ScoutMoveAction"
                || __originalMethod.DeclaringType.Name == "ExploreAction")
                && !h.CRHasPendingRuinsExplorer(__instance, __originalMethod.DeclaringType.Name)) return;
            _combatSamplingDepth++;
            try
            {
                if (!CRSameNative(__0, h.GameStateInstance()) || !h.PrivateSamplingAllowed(__0)) return;
                h.InvalidateNativeSamples();
                h.CRObserve(h.CRCapture(__0), null);
                if (h._combatFrame == null || !h._combatFrame.Complete) return;
                var c = new CombatCause();
                Type actionType = __originalMethod == null ? __instance.GetType() : __originalMethod.DeclaringType;
                string kind = actionType.Name;
                object action = CRTryNativeCast(__instance, actionType);
                if (action == null) return;
                try { h.PublicNativeActionPrefix(__0, action, kind); } catch { }
                bool privateScout = kind == "ScoutMoveAction";
                string origin = privateScout ? null : SafeScalar(MemberWithAccessor(action, "Origin"));
                string unitId = privateScout ? null : SafeScalar(MemberWithAccessor(action, "UnitId"));
                CombatUnitView actor = null;
                if (unitId != null) h._combatFrame.Units.TryGetValue(unitId, out actor);
                if (actor == null && origin != null) actor = CRUnitAt(h._combatFrame, origin);
                string coord = privateScout ? null : SafeScalar(MemberWithAccessor(action, "Target") ?? MemberWithAccessor(action, "Coordinates"));
                CombatUnitView target = CRUnitAt(h._combatFrame, coord);
                if (actor == null && origin == null && kind != "KillUnitAction" && kind != "TrainAction" && kind != "InfiltrationRewardAction") actor = target;
                if (actor != null) { c.ActorId = actor.Id; c.ActorOwner = actor.Owner; }
                // Reading a hidden action's target must not cause a coordinate or action-kind leak.
                if (target != null || (coord != null && h._combatFrame.OwnImprovements.ContainsKey(coord)))
                { c.TargetCoord = coord; c.TargetId = target == null ? null : target.Id; }
                c.VisibleNativeAction = actor != null || target != null || c.TargetCoord != null;
                c.ActionType = c.VisibleNativeAction ? kind : null;
                // Nested KillUnitAction retains a known visible attacker's identity and ordering.
                // A hidden attack has no parent actor and therefore stays unknown.
                if (kind == "KillUnitAction" && target != null && c.ActorId == null && h._combatCauseStack.Count > 0)
                {
                    var parent = h._combatCauseStack[h._combatCauseStack.Count - 1];
                    if (parent.ActionType == "AttackAction" && parent.ActorId != null)
                    { c.ActorId = parent.ActorId; c.ActorOwner = parent.ActorOwner; }
                }
                __state = new CombatHookPass(); __state.State = __0; __state.Cause = c;
                __state.CandidateKind = kind; __state.CandidateCoord = coord;
                __state.Action = action; __state.Before = h._combatFrame;
                var rewardTicket = kind == "CityRewardAction" ? h.CRUniqueCityRewardTicket(h._combatFrame, action) : null;
                foreach (var t in h._combatTickets.Values)
                {
                    if (t.Stream != h._combatStream || t.Accepted == false || t.FinalFrame != null) continue;
                    bool ownActor = actor != null && actor.Owner == t.Before.Me && actor.Id == t.UnitId
                        && SafeScalar(MemberWithAccessor(action, "PlayerId")) == t.Before.Me;
                    bool bound = kind == "InfiltrateAction" && CRInfiltrationActorMatches(t, actor,
                        actor != null && actor.Owner == t.Before.Me ? SafeScalar(MemberWithAccessor(action, "PlayerId")) : null, coord);
                    bool rewardBound = kind == "CityRewardAction" ? t == rewardTicket
                        : CRCityRewardChildMatches(t, h._combatFrame, action);
                    if (kind == "InfiltrationRewardAction")
                    {
                        bound = CRInfiltrationRewardMatches(t, action,
                            CRContainsNative(t.NativeInfiltrationRewards, action) ? SafeScalar(MemberWithAccessor(action, "PlayerId")) : null, coord);
                        if (bound)
                        {
                            // The consumed own Cloak is the known causal actor. A defender on
                            // Coordinates is the target, never the actor of its enemy's reward.
                            c.ActorId = t.UnitId; c.ActorOwner = t.Before.Me;
                            c.VisibleNativeAction = true; c.ActionType = kind; c.TargetCoord = t.Coord;
                        }
                    }
                    if (kind == "TrainAction") bound = CRContainsNative(t.NativeInfiltrationTrains, action)
                        || CRRuinsEffectChildMatches(t, action, kind) || CRContainsNative(t.NativeEnchantTrains, action);
                    if (kind == "ConvertAction") bound = CRConvertActorMatches(t, actor, target,
                        ownActor ? SafeScalar(MemberWithAccessor(action, "PlayerId")) : null, coord);
                    if(kind=="AttackAction")
                    {
                        var explosionActor=CRKnownExplosionActor(t,action,actor);
                        if(!object.ReferenceEquals(actor,explosionActor))
                        {actor=explosionActor;c.ActorId=actor.Id;c.ActorOwner=actor.Owner;c.VisibleNativeAction=true;c.ActionType=kind;}
                    }
                    if (kind == "AttackAction") bound = CRVisibleAttackActorMatches(t, actor, target,
                        ownActor ? SafeScalar(MemberWithAccessor(action, "PlayerId")) : null, coord)
                        || CRRetaliationMatches(t, actor, target, coord)
                        || CRAreaAttackMatches(t, action, actor, target,
                            actor != null && actor.Owner == t.Before.Me && CRContainsNative(t.NativeAreaActions, action)
                                ? SafeScalar(MemberWithAccessor(action, "PlayerId")) : null, coord);
                    if (kind == "KillUnitAction") bound = CRTrackedAttackKillMatches(t, target) || CRAreaKillMatches(t, target);
                    if (kind == "ExamineRuinsAction") bound = CRRuinsActorMatches(t, actor,
                        ownActor ? SafeScalar(MemberWithAccessor(action, "PlayerId")) : null, coord);
                    if (kind == "ScoutMoveAction" || kind == "ExploreAction")
                    {
                        bound = CRRuinsExplorerChildMatches(t, action, kind);
                        if (bound)
                        {
                            c.ActorId = t.UnitId; c.ActorOwner = t.Before.Me;
                            c.VisibleNativeAction = true; c.ActionType = kind; c.TargetCoord = t.Coord;
                        }
                    }
                    if (kind == "IncreaseCurrencyAction" || kind == "ResearchAction" || kind == "IncreasePopulationAction")
                    {
                        bound = kind == "ResearchAction" ? CRRuinsResearchMatches(t, action)
                            : kind == "IncreaseCurrencyAction" ? CRRuinsCurrencyMatches(t, action) : CRRuinsEffectChildMatches(t, action, kind);
                        if (bound)
                        {
                            c.ActorId = t.UnitId; c.ActorOwner = t.Before.Me;
                            c.VisibleNativeAction = true; c.ActionType = kind; c.TargetCoord = t.Coord;
                        }
                    }
                    if (kind == "FloodTileAction") bound = CRFloodActionMatches(t, h._combatFrame, action)
                        || CRCanalFloodActionMatches(t, h._combatFrame, action);
                    if (kind == "BuildAction") bound = CRCanalActionMatches(t, h._combatFrame, action)
                        || CREnchantBuildMatches(t, h._combatFrame, action);
                    if (kind == "ClearTileEffectAction")
                    {
                        bound=CRBindClearFloodAction(t,h._combatFrame,action);
                        if (bound)
                        {
                            c.ActorId=t.UnitId; c.ActorOwner=t.Before.Me;
                            c.VisibleNativeAction=true; c.ActionType=kind; c.TargetCoord=t.Coord;
                        }
                    }
                    if (kind == "BoostOthersAction") bound = t.Action == "boost" && ownActor;
                    if (kind == "BoostAction") bound = t.Action == "boost" && (ownActor || CRContainsNative(t.NativeBoostActions, action));
                    if (kind == "HealOthersAction") bound = t.Action == "heal_others" && ownActor;
                    if (kind == "RecoverAction") bound = (t.Action == "recover" || t.Action == "heal_self") && ownActor;
                    if (kind == "HealAction") bound = ((t.Action == "recover" || t.Action == "heal_self") && ownActor)
                        || CRContainsNative(t.NativeHealActions, action);
                    if (kind=="MoveAction" || kind=="FreezeAreaAction" || kind=="FreezeTileAction"
                        || kind=="FreezeUnitAction" || kind=="BreakIceAreaAction" || kind=="BreakIceAction")
                    {
                        bound=h.PolarisBind(t,h._combatFrame,action,kind);
                        if(bound && kind!="MoveAction")
                        {
                            c.ActorId=t.UnitId;c.ActorOwner=t.Before.Me;c.ActionType=kind;c.VisibleNativeAction=true;
                            c.TargetCoord=kind=="FreezeUnitAction"?SafeScalar(MemberWithAccessor(action,"Target"))
                                :SafeScalar(MemberWithAccessor(action,"Coordinates"));
                        }
                    }
                    if(h.CymantiBind(t,h._combatFrame,action,kind))
                    {
                        bound=true;
                        if(kind!="AttackAction" || t.Action=="explode")
                        {
                            c.ActorId=t.UnitId;c.ActorOwner=t.Before.Me;c.VisibleNativeAction=true;c.ActionType=kind;
                            string knownTarget=kind=="PoisonUnitAction"?SafeScalar(MemberWithAccessor(action,"Target")):coord;
                            if(knownTarget!=null && h._combatFrame.PublicTerrains.ContainsKey(knownTarget))c.TargetCoord=knownTarget;
                        }
                    }
                    if(CymantiDecomposeBind(t,h._combatFrame,action,kind))
                    {bound=true;c.ActorId=null;c.ActorOwner=t.Before.Me;c.ActionType=kind;c.VisibleNativeAction=true;c.TargetCoord=t.Coord;}
                    if(CymantiAlgaeBuildBind(t,h._combatFrame,action,kind))
                    {bound=true;c.ActorId=null;c.ActorOwner=t.Before.Me;c.ActionType=kind;c.VisibleNativeAction=true;c.TargetCoord=t.Coord;}
                    if (bound || rewardBound) __state.ProofTickets.Add(t);
                }
                bool rewardProof = false;
                foreach (var ticket in __state.ProofTickets)
                    if (ticket.Action == "city_reward" || ticket.Action == "reward") rewardProof = true;
                if (__state.ProofTickets.Count > 0 && (rewardProof || kind == "MoveAction" || kind == "FreezeAreaAction" || kind == "BreakIceAreaAction" || kind == "BuildAction" || kind == "InfiltrateAction" || kind == "InfiltrationRewardAction" || kind == "BoostOthersAction" || kind == "HealOthersAction" || kind == "ExamineRuinsAction" || kind == "ScoutMoveAction"))
                    __state.BeforeStackCount = CRStackCount(__0);
                if(__state.ProofTickets.Count>0 && CymantiIsRoot(kind))
                { __state.BeforeStackCount=CRStackCount(__0);h._cymantiActivePasses.Add(__state); }
                if(__state.ProofTickets.Count>0 && (kind=="MoveAction" || kind=="FreezeAreaAction" || kind=="BreakIceAreaAction"))
                    h._polarisActivePasses.Add(__state);
                if (kind == "ResearchAction" && __state.ProofTickets.Count > 0)
                    __state.RuinsTechsBefore = h.CRReadRuinsOwnTechs(__0, h._combatFrame);
                if (kind == "ExploreAction" && __state.ProofTickets.Count > 0)
                    __state.RuinsExploredBefore = h.CRReadRuinsExplorerTile(__0, h._combatFrame, action);
                h._combatCauseStack.Add(c);
                h._combatHookDepth++;
            }
            catch { }
            finally { _combatSamplingDepth--; }
        }

        private static void CombatActionPostfix(CombatHookPass __state)
        { using (MeasureCommandCallback()) CombatCloseHook(__state, false); }

        private static Exception CombatActionFinalizer(Exception __exception, CombatHookPass __state)
        { using (MeasureCommandCallback()) CombatCloseHook(__state, __exception != null); return __exception; }

        private static void CombatCloseHook(CombatHookPass pass, bool failed)
        {
            if (pass == null || pass.Closed) return;
            pass.Closed = true;
            var h = _combatHookHost;
            if (h == null) return;
            h._polarisActivePasses.Remove(pass);
            h._cymantiActivePasses.Remove(pass);
            h._combatHookDepth = Math.Max(0, h._combatHookDepth - 1);
            if (h._combatCauseStack.Count > 0) h._combatCauseStack.RemoveAt(h._combatCauseStack.Count - 1);
            // A hook can enter in live play and close after a seek or seat transition.
            // Keep cleanup balanced, but do not finish diplomacy/native proofs from a
            // suppressed historical state. Frozen tickets and the trusted frame survive.
            if (!h.PrivateSamplingAllowed(pass.State))
            {
                for (int i = h._publicDiplomacyActive.Count - 1; i >= 0; i--)
                    if (CRSameNative(h._publicDiplomacyActive[i].NativeAction, pass.Action))
                        h._publicDiplomacyActive.RemoveAt(i);
                h._combatCaptureAvailable = false;
                h._combatStableFrames = 0;
                return;
            }
            if (_combatSamplingDepth > 0) { h._combatStableFrames = 0; return; }
            _combatSamplingDepth++;
            try
            {
                h.InvalidateNativeSamples();
                var next = h.CRCapture(pass.State);
                // A native TrainAction can start on an empty enemy-owned tile (Cloak reward).
                // Only after its unit is actually visible may its previously hidden coordinate
                // enter the journal. The private candidate never leaves this hook pass.
                if (!failed && next != null && next.Complete && pass.CandidateKind == "TrainAction")
                {
                    var u = CRUnitAt(next, pass.CandidateCoord);
                    string nativeType;
                    if (u != null && u.Owner == next.Me
                        && SafeScalar(MemberWithAccessor(pass.Action, "PlayerId")) == next.Me
                        && CRTryAppliedTrainType(pass.Action, out nativeType) && nativeType == u.Type)
                    {
                        pass.Cause.ActionType = "TrainAction"; pass.Cause.VisibleNativeAction = true;
                        pass.Cause.TargetCoord = u.Coord; pass.Cause.ActorOwner = u.Owner;
                        pass.Cause.AppliedTrainType = nativeType;
                    }
                }
                if (!failed && pass.CandidateKind == "CreateResourceAction"
                    && CRAttractObserved(pass.Before, next, pass.Action))
                {
                    pass.Cause.ActionType = "CreateResourceAction";
                    pass.Cause.TargetCoord = SafeScalar(MemberWithAccessor(pass.Action, "Coordinates"));
                    pass.Cause.VisibleNativeAction = true;
                    pass.Cause.NativeAttract = true;
                    // Attract identifies the native reason, not an initiating unit or exact sanctuary.
                    pass.Cause.ActorId = pass.Cause.ActorOwner = pass.Cause.TargetId = null;
                }
                if(!failed && next!=null && next.Complete)h.CymantiReconcileEatBoundary(pass,next);
                h.CRObserve(next, failed ? null : pass.Cause);
                if (!failed && next != null && next.Complete) h.CRRecordNativeProof(pass, next);
                if (!failed) { try { h.PublicNativeActionComplete(pass.State, pass.Action, pass.CandidateKind); } catch { } }
                // Even an effect that changes no projected value resets the settlement guard.
                h._combatLastChangeTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                h._combatStableFrames = 0;
            }
            catch { h._combatStableFrames = 0; }
            finally { _combatSamplingDepth--; }
        }

        private static bool CRContainsNative(List<object> actions, object action)
        { foreach (var item in actions) if (CRSameNative(item, action)) return true; return false; }

        private static int? CRStackCount(object gs)
        {
            // The shared TryCount enumerates IEnumerable first. These are native List<ActionBase>
            // receipts: read Count directly so proving an own append never enumerates old actions.
            var stack = MemberWithAccessor(gs, "ActionStack");
            var count = CRInt(MemberWithAccessor(stack, "Count"));
            return count.HasValue && count.Value >= 0 ? count : null;
        }

        // Inspect only actions appended by this already-bound OWN native action. Do not walk
        // the earlier stack or publish pending objects/coordinates. Bind exact native objects,
        // then publish unit coordinates only after the matching TrainAction really executes.
        private static List<object> CRAppendedActions(object gs, int? previousCount, string kind)
        {
            List<object> result;
            CRTryAppendedActions(gs, previousCount, kind, out result);
            return result; // Retain known positives even when the suffix is partial.
        }

        // Completeness matters when claiming ALL reward children applied. A failed
        // index read must not turn an incomplete suffix into a shorter complete list.
        private static bool CRTryAppendedActions(object gs, int? previousCount, string kind, out List<object> result)
        {
            result = new List<object>();
            var stack = MemberWithAccessor(gs, "ActionStack"); var count = CRStackCount(gs);
            if (stack == null || !previousCount.HasValue || previousCount.Value < 0 || !count.HasValue
                || count.Value < previousCount.Value) return false;
            var wanted = FindGameType(kind); if (wanted == null) return false;
            var getter = FindUniqueNativeMethod(stack.GetType(), "get_Item", new[] {"Int32"}, null, false);
            if (getter == null || getter.ReturnType == typeof(void) || getter.ReturnType.IsValueType) return false;
            bool complete = true;
            for (int i = previousCount.Value; i < count.Value; i++)
                try
                {
                    var item = getter.Invoke(stack, new object[] { i });
                    if (item == null) { complete = false; continue; }
                    if (item is string || item.GetType().IsValueType) { complete = false; continue; }
                    object typed; string castError;
                    if (!TryNativeCast(item, wanted, out typed, out castError)) { complete = false; continue; }
                    if (typed != null)
                    {
                        if (CRContainsNative(result, typed)) complete = false;
                        else result.Add(typed);
                    }
                }
                catch { complete = false; }
            return complete && CRStackCount(gs) == count;
        }

        private void CRRecordNativeProof(CombatHookPass pass, CombatFrame after)
        {
            if (pass.ProofTickets.Count == 0) return;
            var ruinsTechsAfter = pass.CandidateKind == "ResearchAction" ? CRReadRuinsOwnTechs(pass.State, after) : null;
            bool? ruinsExploredAfter = pass.CandidateKind == "ExploreAction" ? CRReadRuinsExplorerTile(pass.State, after, pass.Action) : null;
            List<object> appended = null;
            if (pass.CandidateKind == "BuildAction") appended = CRAppendedActions(pass.State, pass.BeforeStackCount, "FloodTileAction");
            if (pass.CandidateKind == "InfiltrateAction") appended = CRAppendedActions(pass.State, pass.BeforeStackCount, "InfiltrationRewardAction");
            if (pass.CandidateKind == "InfiltrationRewardAction") appended = CRAppendedActions(pass.State, pass.BeforeStackCount, "TrainAction");
            if (pass.CandidateKind == "BoostOthersAction") appended = CRAppendedActions(pass.State, pass.BeforeStackCount, "BoostAction");
            if (pass.CandidateKind == "HealOthersAction") appended = CRAppendedActions(pass.State, pass.BeforeStackCount, "HealAction");
            foreach (var t in pass.ProofTickets)
            {
                PolarisRecord(pass,after,t);
                CymantiRecord(pass,after,t);
                if(pass.CandidateKind=="DecomposeAction")CymantiDecomposeRecord(t,pass.Before,after,pass.Action);
                if(pass.CandidateKind=="BuildAction")CymantiAlgaeBuildRecord(t,pass.Before,pass.Action);
                CRRecordCityReward(pass, after, t);
                if (t.Stream == _combatStream && pass.CandidateKind == "ExamineRuinsAction"
                    && CRRecordRuins(t, pass.Action, pass.Before, after, pass.Cause))
                {
                    List<object> children;
                    if (t.NativeRuins.Reward == "Resources")
                    {
                        t.NativeRuins.CurrencyChildrenComplete = CRTryAppendedActions(pass.State, pass.BeforeStackCount, "IncreaseCurrencyAction", out children);
                        t.NativeRuins.CurrencyChildren.AddRange(children);
                    }
                    else if (t.NativeRuins.Reward == "FreeTech")
                    {
                        t.NativeRuins.TechChildrenComplete = CRTryAppendedActions(pass.State, pass.BeforeStackCount, "ResearchAction", out children);
                        t.NativeRuins.TechChildren.AddRange(children);
                    }
                    else if (t.NativeRuins.Reward == "PopulationGrowth")
                    {
                        t.NativeRuins.PopulationChildrenComplete = CRTryAppendedActions(pass.State, pass.BeforeStackCount, "IncreasePopulationAction", out children);
                        t.NativeRuins.PopulationChildren.AddRange(children);
                    }
                    else if (IsRuinsUnitReward(t.NativeRuins.Reward))
                    {
                        t.NativeRuins.UnitChildrenComplete = CRTryAppendedActions(pass.State, pass.BeforeStackCount, "TrainAction", out children);
                        t.NativeRuins.UnitChildren.AddRange(children);
                    }
                    else if (t.NativeRuins.Reward == "Explorer") CRBindRuinsExplorer(t, pass.State, pass.BeforeStackCount);
                }
                if (t.Stream == _combatStream && pass.CandidateKind == "IncreaseCurrencyAction")
                    CRRecordRuinsCurrency(t, pass.Action, pass.Before, after);
                if (t.Stream == _combatStream && pass.CandidateKind == "ResearchAction")
                    CRRecordRuinsResearch(t, pass.Action, pass.Before, after, pass.RuinsTechsBefore, ruinsTechsAfter);
                if (t.Stream == _combatStream && pass.CandidateKind == "IncreasePopulationAction")
                    CRRecordRuinsPopulation(t, pass.Action, pass.Before, after);
                if (t.Stream == _combatStream && pass.CandidateKind == "TrainAction")
                {
                    CRRecordRuinsUnit(t, pass.Action, pass.Before, after);
                    CRRecordEnchantedUnit(t, pass.Action, pass.Before, after);
                }
                if (t.Stream == _combatStream && pass.CandidateKind == "ScoutMoveAction")
                    CRRecordRuinsScout(t, pass.Action, pass.Before, after, pass.State, pass.BeforeStackCount);
                if (t.Stream == _combatStream && pass.CandidateKind == "ExploreAction")
                    CRRecordRuinsExplore(t, pass.Action, pass.Before, after, pass.RuinsExploredBefore, ruinsExploredAfter);
                if (pass.CandidateKind == "InfiltrateAction")
                { t.NativeInfiltrateObserved = true; t.NativeInfiltrationRewards.AddRange(appended); }
                if (pass.CandidateKind == "InfiltrationRewardAction") t.NativeInfiltrationTrains.AddRange(appended);
                if (pass.CandidateKind == "BoostOthersAction") t.NativeBoostActions.AddRange(appended);
                if (pass.CandidateKind == "HealOthersAction") t.NativeHealActions.AddRange(appended);
                if (pass.CandidateKind == "RecoverAction") t.NativeRecoverObserved = true;
                if (t.Stream == _combatStream && pass.CandidateKind == "FloodTileAction")
                {
                    CRRecordFlood(t, pass.Action, pass.Before, after);
                    CRRecordCanalFlood(t, pass.Action, pass.Before, after);
                }
                if (t.Stream == _combatStream && pass.CandidateKind == "BuildAction")
                {
                    CRRecordCanalChildren(t, pass.Action, pass.Before, after, appended);
                    CRRecordCanal(t, pass.Action, pass.Before, after);
                    if (CREnchantBuildMatches(t, pass.Before, pass.Action))
                    {
                        t.NativeEnchantBuildObserved = true;
                        t.NativeEnchantTrains.AddRange(CRAppendedActions(pass.State, pass.BeforeStackCount, "TrainAction"));
                    }
                }
                if (t.Stream == _combatStream && pass.CandidateKind == "ClearTileEffectAction")
                    CRRecordClearFlood(t,pass.Action,pass.Before,after);
                if (t.Stream == _combatStream && pass.CandidateKind == "AttackAction" && CRContainsNative(t.NativeAreaActions, pass.Action))
                    CRRecordAreaDamage(t, pass.Action, pass.Before, after, pass.Cause, pass.CandidateCoord,
                        SafeScalar(MemberWithAccessor(pass.Action, "Animation")));
                if (pass.CandidateKind == "AttackAction" && pass.Before != null && after != null
                    && pass.Before.Episode == t.Before.Episode && after.Episode == t.Before.Episode
                    && pass.Before.Me == t.Before.Me && after.Me == t.Before.Me && pass.Cause != null
                    && pass.Cause.ActionType == "AttackAction" && pass.Cause.ActorId == t.UnitId
                    && pass.Cause.ActorOwner == t.Before.Me && pass.CandidateCoord == t.Coord)
                {
                    var targetBefore = CRUnitAt(pass.Before, t.Coord);
                    var requestedBefore = CRUnitAt(t.Before, t.Coord);
                    if (targetBefore != null && requestedBefore != null && targetBefore.Id == requestedBefore.Id
                        && pass.Cause.TargetId == targetBefore.Id && !string.IsNullOrEmpty(targetBefore.Owner)
                        && targetBefore.Owner != t.Before.Me && targetBefore.Owner == requestedBefore.Owner)
                    {
                        t.NativeAttackTargets.Add(targetBefore.Id);
                        CombatUnitView damaged;
                        if (after.Units.TryGetValue(targetBefore.Id, out damaged) && damaged.Owner == targetBefore.Owner
                            && damaged.Coord == targetBefore.Coord && targetBefore.Health.HasValue && damaged.Health.HasValue
                            && damaged.Health < targetBefore.Health)
                        { t.NativePrimaryBefore = targetBefore; t.NativePrimaryAfter = damaged; }
                    }
                }
                if (pass.CandidateKind == "AttackAction" && pass.Cause != null && pass.Before != null && after != null
                    && pass.Before.Episode == t.Before.Episode && after.Episode == t.Before.Episode
                    && pass.Before.Me == t.Before.Me && after.Me == t.Before.Me)
                {
                    CombatUnitView retaliator, victim, damaged;
                    pass.Before.Units.TryGetValue(pass.Cause.ActorId ?? "", out retaliator);
                    pass.Before.Units.TryGetValue(pass.Cause.TargetId ?? "", out victim);
                    if (CRRetaliationMatches(t, retaliator, victim, pass.CandidateCoord)
                        && pass.Cause.ActionType == "AttackAction" && pass.Cause.ActorOwner == retaliator.Owner
                        && after.Units.TryGetValue(victim.Id, out damaged) && damaged.Owner == victim.Owner
                        && damaged.Coord == victim.Coord && victim.Health.HasValue && damaged.Health.HasValue
                        && damaged.Health < victim.Health)
                    { t.NativeRetaliationBefore = victim; t.NativeRetaliationAfter = damaged; }
                }
                if (pass.CandidateKind == "KillUnitAction" && pass.Cause != null && pass.Before != null && after != null
                    && pass.Before.Episode == t.Before.Episode && after.Episode == t.Before.Episode
                    && pass.Before.Me == t.Before.Me && after.Me == t.Before.Me
                    && pass.Cause.ActionType == "KillUnitAction" && !after.Units.ContainsKey(pass.Cause.TargetId ?? ""))
                {
                    CombatUnitView victim;
                    pass.Before.Units.TryGetValue(pass.Cause.TargetId ?? "", out victim);
                    if (CRTrackedAttackKillMatches(t, victim)) t.NativeAttackKilledIds.Add(victim.Id);
                    if (t.Stream == _combatStream && CRAreaKillMatches(t, victim)) t.NativeAreaKilledIds.Add(victim.Id);
                }
                if (pass.CandidateKind == "ConvertAction" && pass.Before != null && after != null
                    && pass.Before.Episode == t.Before.Episode && after.Episode == t.Before.Episode
                    && pass.Before.Me == t.Before.Me && after.Me == t.Before.Me && pass.Cause != null
                    && pass.Cause.ActionType == "ConvertAction" && pass.Cause.ActorId == t.UnitId && pass.Cause.ActorOwner == t.Before.Me)
                {
                    var targetBefore = CRUnitAt(pass.Before, pass.CandidateCoord);
                    var requestedBefore = CRUnitAt(t.Before, t.Coord); CombatUnitView converted;
                    if (targetBefore != null && requestedBefore != null && targetBefore.Id == requestedBefore.Id
                        && pass.Cause.TargetId == targetBefore.Id && pass.CandidateCoord == t.Coord
                        && !string.IsNullOrEmpty(targetBefore.Owner) && targetBefore.Owner != t.Before.Me
                        && targetBefore.Owner == requestedBefore.Owner && after.Units.TryGetValue(targetBefore.Id, out converted)
                        && converted.Owner == t.Before.Me && converted.Coord == t.Coord)
                        t.NativeConvertTargets.Add(converted.Id);
                }
                var u = CRUnitAt(after, pass.CandidateCoord);
                CombatUnitView old;
                if (u == null || u.Owner != t.Before.Me) continue;
                string appliedType;
                if (pass.CandidateKind == "TrainAction" && t.NativeInfiltrateObserved && !pass.Before.Units.ContainsKey(u.Id)
                    && CRContainsNative(t.NativeInfiltrationTrains, pass.Action)
                    && SafeScalar(MemberWithAccessor(pass.Action, "PlayerId")) == t.Before.Me
                    && CRTryAppliedTrainType(pass.Action, out appliedType) && appliedType == u.Type)
                    t.NativeInfiltrationSpawnIds.Add(u.Id);
                if (pass.CandidateKind == "BoostAction" && pass.Before.Units.TryGetValue(u.Id, out old)
                    && old.Owner == t.Before.Me && CRBoostEffectAdded(old.Effects, u.Effects, t.NativeVersion))
                    t.NativeBoostTargets.Add(u.Id);
                if (pass.CandidateKind == "HealAction" && pass.Before.Units.TryGetValue(u.Id, out old)
                    && old.Owner == t.Before.Me && CRHealingChanged(old, u)) t.NativeHealTargets.Add(u.Id);
            }
        }

        internal static bool CRBoostEffectAdded(string before, string after, int? version)
        {
            if (before == null || after == null || !version.HasValue) return false;
            string effect = version.Value >= 115 ? "\"Swift\"" : "\"Boosted\"";
            return before.IndexOf(effect, StringComparison.Ordinal) < 0 && after.IndexOf(effect, StringComparison.Ordinal) >= 0;
        }

        internal static bool CRHealingChanged(CombatUnitView before, CombatUnitView after)
        {
            if (before == null || after == null || before.Id != after.Id || before.Owner != after.Owner) return false;
            bool hp = before.Health.HasValue && after.Health.HasValue && after.Health > before.Health;
            bool poison = before.Effects != null && after.Effects != null
                && before.Effects.IndexOf("\"Poisoned\"", StringComparison.Ordinal) >= 0
                && after.Effects.IndexOf("\"Poisoned\"", StringComparison.Ordinal) < 0;
            return hp || poison;
        }

        internal static bool CRInfiltrationConfirmed(CombatTicket ticket, CombatFrame current)
        {
            CombatUnitView cloak;
            if (ticket == null || current == null || ticket.Action != "attack" || ticket.Before == null
                || current.Episode != ticket.Before.Episode || current.Me != ticket.Before.Me
                || !ticket.Before.Units.TryGetValue(ticket.UnitId ?? "", out cloak) || cloak.Owner != ticket.Before.Me
                || cloak.Infiltrate != true || !ticket.NativeInfiltrateObserved || ticket.NativeInfiltrationRewards.Count == 0
                || ticket.NativeInfiltrationTrains.Count == 0) return false;
            foreach (var id in ticket.NativeInfiltrationSpawnIds)
            {
                CombatUnitView spawned;
                if (!ticket.Before.Units.ContainsKey(id) && current.Units.TryGetValue(id, out spawned)
                    && spawned.Owner == ticket.Before.Me && spawned.Coord != null && spawned.Type != null && spawned.Health.HasValue) return true;
            }
            return false;
        }

        internal static bool CRInfiltrationActorMatches(CombatTicket ticket, CombatUnitView actor, string nativePlayerId, string targetCoord)
        {
            CombatUnitView before;
            return ticket != null && ticket.Action == "attack" && ticket.Accepted != false && ticket.FinalFrame == null
                && ticket.Before != null && actor != null && actor.Id == ticket.UnitId && actor.Owner == ticket.Before.Me
                && nativePlayerId == ticket.Before.Me && actor.Infiltrate == true
                && ticket.Before.Units.TryGetValue(ticket.UnitId ?? "", out before) && before.Owner == ticket.Before.Me
                && before.Infiltrate == true && !string.IsNullOrEmpty(targetCoord) && targetCoord == ticket.Coord;
        }

        internal static bool CRInfiltrationRewardMatches(CombatTicket ticket, object reward, string nativePlayerId, string targetCoord)
        {
            return ticket != null && ticket.Before != null && ticket.NativeInfiltrateObserved
                && nativePlayerId == ticket.Before.Me && !string.IsNullOrEmpty(targetCoord) && targetCoord == ticket.Coord
                && CRContainsNative(ticket.NativeInfiltrationRewards, reward);
        }

        internal static bool CRConvertActorMatches(CombatTicket ticket, CombatUnitView actor, CombatUnitView target, string nativePlayerId, string targetCoord)
        { return CRVisibleAttackActorMatches(ticket, actor, target, nativePlayerId, targetCoord); }

        internal static bool CRVisibleAttackActorMatches(CombatTicket ticket, CombatUnitView actor, CombatUnitView target, string nativePlayerId, string targetCoord)
        {
            CombatUnitView ownBefore;
            var targetBefore = ticket == null || ticket.Before == null ? null : CRUnitAt(ticket.Before, ticket.Coord);
            return ticket != null && ticket.Before != null && ticket.Action == "attack" && ticket.Accepted != false && ticket.FinalFrame == null
                && actor != null && actor.Id == ticket.UnitId && actor.Owner == ticket.Before.Me && nativePlayerId == ticket.Before.Me
                && ticket.Before.Units.TryGetValue(ticket.UnitId ?? "", out ownBefore) && ownBefore.Owner == ticket.Before.Me
                && target != null && targetBefore != null && target.Id == targetBefore.Id
                && !string.IsNullOrEmpty(target.Owner) && target.Owner != ticket.Before.Me && target.Owner == targetBefore.Owner
                && !string.IsNullOrEmpty(targetCoord) && targetCoord == ticket.Coord && target.Coord == targetCoord;
        }

        internal static bool CRConversionConfirmed(CombatTicket ticket, CombatFrame current)
        {
            if (ticket == null || current == null || ticket.Before == null || ticket.Action != "attack"
                || current.Episode != ticket.Before.Episode || current.Me != ticket.Before.Me) return false;
            var targetBefore = CRUnitAt(ticket.Before, ticket.Coord); CombatUnitView converted;
            return targetBefore != null && !string.IsNullOrEmpty(targetBefore.Owner) && targetBefore.Owner != ticket.Before.Me
                && ticket.NativeConvertTargets.Contains(targetBefore.Id) && current.Units.TryGetValue(targetBefore.Id, out converted)
                && converted.Owner == ticket.Before.Me && converted.Coord == ticket.Coord;
        }

        internal static bool CRAttackEmbarkConfirmed(CombatTicket ticket, CombatFrame current)
        {
            if (ticket == null || current == null || ticket.Before == null || ticket.Action != "attack"
                || !ticket.Dispatched || ticket.Accepted != true || !current.Complete
                || current.Episode != ticket.Before.Episode || current.Me != ticket.Before.Me) return false;
            CombatUnitView actor;
            var target = CRUnitAt(ticket.Before, ticket.Coord);
            if (!ticket.Before.Units.TryGetValue(ticket.UnitId ?? "", out actor)
                || actor.Owner != current.Me || actor.Attacked != false || current.Units.ContainsKey(actor.Id)
                || target == null || string.IsNullOrEmpty(target.Owner) || target.Owner == current.Me
                || !ticket.NativeAttackTargets.Contains(target.Id) || !ticket.NativeKilledIds.Contains(target.Id)
                || current.Units.ContainsKey(target.Id)) return false;
            // A new carrier must explicitly carry the original own attacker. A matching tile,
            // a disappeared defender, or consumed carrier flags alone cannot prove this attack.
            foreach (var carrier in current.Units.Values)
                if (CRCarrierIdentityTransition(ticket.Before, current, actor, carrier)
                    && carrier.PassengerId == actor.Id && carrier.Coord == ticket.Coord
                    && carrier.Attacked == true && carrier.Moved == true)
                    return true;
            return false;
        }

        internal static bool CRRetaliationMatches(CombatTicket ticket, CombatUnitView actor, CombatUnitView target, string coord)
        {
            var defender = ticket == null || ticket.Before == null ? null : CRUnitAt(ticket.Before, ticket.Coord);
            CombatUnitView attacker;
            return ticket != null && ticket.Action == "attack" && ticket.Accepted != false && ticket.FinalFrame == null
                && defender != null && ticket.NativeAttackTargets.Contains(defender.Id)
                && actor != null && actor.Id == defender.Id && actor.Owner == defender.Owner
                && target != null && target.Id == ticket.UnitId && target.Owner == ticket.Before.Me
                && ticket.Before.Units.TryGetValue(ticket.UnitId ?? "", out attacker)
                && attacker.Owner == ticket.Before.Me && target.Coord == attacker.Coord && coord == attacker.Coord;
        }

        internal static bool CRTrackedAttackKillMatches(CombatTicket ticket, CombatUnitView victim)
        {
            if (ticket == null || ticket.Action != "attack" || ticket.Accepted == false || ticket.FinalFrame != null
                || ticket.Before == null || victim == null || victim.Health != 0) return false;
            var damage = victim.Id == ticket.UnitId ? ticket.NativeRetaliationAfter : ticket.NativePrimaryAfter;
            return damage != null && damage.Id == victim.Id && damage.Owner == victim.Owner
                && damage.Coord == victim.Coord && damage.Health == 0;
        }

        internal static string CRAttackActorOutcome(CombatTicket ticket, CombatFrame current, out CombatUnitView survivor)
        {
            survivor = null;
            if (ticket == null || current == null || ticket.Before == null || ticket.Action != "attack"
                || !ticket.Dispatched || ticket.Accepted != true || !ticket.Before.Complete || !current.Complete
                || current.Episode != ticket.Before.Episode || current.Me != ticket.Before.Me) return "unknown";
            CombatUnitView actor;
            var defender = CRUnitAt(ticket.Before, ticket.Coord);
            if (!ticket.Before.Units.TryGetValue(ticket.UnitId ?? "", out actor) || actor.Owner != current.Me
                || actor.Attacked != false || defender == null || !ticket.NativeAttackTargets.Contains(defender.Id)) return "unknown";
            if (current.Units.TryGetValue(actor.Id, out survivor))
            {
                // Persist may reset attacked to false after a kill. Keep the raw flags
                // truthful and require this ticket's exact primary damage and kill,
                // rather than inferring success from an XP/position change or a type.
                bool retainedAttack = survivor.Attacked == false && survivor.Health > 0
                    && CRPrimaryAttackDamageObserved(ticket, defender) && ticket.NativePrimaryAfter.Health == 0
                    && ticket.NativeAttackKilledIds.Contains(defender.Id) && !current.Units.ContainsKey(defender.Id);
                bool doubleAttack = CRDoubleAttackConsumed(actor, survivor)
                    && CRPrimaryAttackDamageObserved(ticket, defender);
                return survivor.Owner == current.Me && (survivor.Attacked == true || retainedAttack || doubleAttack) ? "survived" : "unknown";
            }
            if (ticket.NativeAttackKilledIds.Contains(actor.Id)) return "died";
            // Both directions use the actual own passenger ID, never same-type/position guesses.
            if (actor.PassengerId != null && !ticket.Before.Units.ContainsKey(actor.PassengerId)
                && current.Units.TryGetValue(actor.PassengerId, out survivor)
                && CRCarrierIdentityTransition(ticket.Before, current, actor, survivor) && survivor.Coord == ticket.Coord
                && survivor.Moved == true && survivor.Attacked == true
                && ticket.NativeAttackKilledIds.Contains(defender.Id) && !current.Units.ContainsKey(defender.Id)) return "disembarked";
            if (CRAttackEmbarkConfirmed(ticket, current))
                foreach (var carrier in current.Units.Values)
                    if (carrier.Owner == current.Me && carrier.PassengerId == actor.Id && carrier.Coord == ticket.Coord)
                    { survivor = carrier; return "embarked"; }
            survivor = null;
            return "unknown";
        }

        private static bool CRPrimaryAttackDamageObserved(CombatTicket ticket, CombatUnitView defender)
        {
            var before = ticket.NativePrimaryBefore; var after = ticket.NativePrimaryAfter;
            return defender != null && before != null && after != null
                && before.Id == defender.Id && after.Id == defender.Id
                && before.Owner == defender.Owner && after.Owner == defender.Owner
                && before.Coord == ticket.Coord && after.Coord == ticket.Coord
                && before.Health.HasValue && after.Health.HasValue && after.Health < before.Health;
        }

        internal static string CRAttackEffectConfirmation(CombatTicket ticket, CombatFrame current, out string basis)
        {
            basis = null;
            CombatUnitView survivor;
            string outcome = CRAttackActorOutcome(ticket, current, out survivor);
            if (outcome == "unknown") return null;
            if (outcome == "embarked")
            { basis = "named native AttackAction and target KillUnitAction observed; surviving own attacker became carrier passenger at requested destination with consumed attack"; return "confirmed"; }
            var defender = CRUnitAt(ticket.Before, ticket.Coord);
            if (!CRPrimaryAttackDamageObserved(ticket, defender)) return null;
            basis = "exact own native AttackAction damaged requested visible target; actorOutcome=" + outcome
                + (survivor != null && CRDoubleAttackConsumed(ticket.Before.Units[ticket.UnitId],survivor)
                    ? "; own DoubleAttack consumed DoubleReady with matched primary damage; second attack remains available"
                    : survivor != null && survivor.Attacked == false ? "; attack flag remained false after matched primary damage to zero and target KillUnitAction" : "")
                + (outcome == "died" ? "; matched retaliation damage to zero and KillUnitAction observed" : "")
                + (outcome == "disembarked" ? "; target KillUnitAction and original carrier passenger ID observed at requested destination" : "");
            return "confirmed";
        }

        internal static void CRCaptureInvisibleHint(CombatFrame frame, CombatUnitView view, object unit, object gs)
        {
            if (frame == null || view == null || string.IsNullOrEmpty(frame.Me)
                || string.IsNullOrEmpty(view.Id) || view.Owner != frame.Me) return;
            frame.OwnInvisibleThreatHints[view.Id] = AdjacentInvisibleHint(unit, gs, true);
        }

        internal static List<string> CRInvisibleHintDiff(CombatFrame before, CombatFrame after)
        {
            var result = new List<string>();
            if (before == null || after == null || !before.Complete || !after.Complete
                || string.IsNullOrEmpty(before.Episode) || before.Episode != after.Episode
                || string.IsNullOrEmpty(before.Me) || before.Me != after.Me) return result;
            var ids = new List<string>(after.OwnInvisibleThreatHints.Keys); ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                CombatUnitView own;
                if (!after.Units.TryGetValue(id, out own) || own.Owner != after.Me) continue;
                string previous;
                before.OwnInvisibleThreatHints.TryGetValue(id, out previous);
                string current = after.OwnInvisibleThreatHints[id];
                if (current == previous) continue;
                result.Add("\"kind\":\"own_invisible_threat_changed\",\"unitId\":" + CRQ(id)
                    + ",\"coords\":" + CRQ(own.Coord) + ",\"before\":" + (previous ?? "null")
                    + ",\"after\":" + (current ?? "null"));
            }
            return result;
        }

        private CombatFrame CRCapture(object gs)
        {
            if (!PrivateSamplingAllowed(gs)) return null;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            _perfCaptureCount++;
            try { return CRCaptureCore(gs); }
            finally { _perfCaptureTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started; }
        }

        private CombatFrame CRCaptureCore(object gs)
        {
            var me = ResolveControlledIdentity(gs);
            if (!me.Verified) return null;
            var f = new CombatFrame();
            f.Episode = EpisodeId(gs); f.Me = me.Id.ToString(CultureInfo.InvariantCulture);
            f.Turn = SafeScalar(Val(_currentTurnProp, gs));
            f.Acting = CurrentPlayerId(gs).ToString(CultureInfo.InvariantCulture);
            f.Phase = SafeScalar(Val(_currentStateProp, gs));
            var map = Val(_mapProp, gs);
            var raw = MemberWithAccessor(map, "Tiles");
            var tiles = AsEnumerable(raw);
            if (tiles == null) return null;
            var gate = BuildFogGate(map, raw, me.Id);
            if (!gate.HasExplored) return null;
            var uf = UnitFilter;
            bool sig = uf.SignatureAvailable;
            int la = uf.LookupAttempted, at = uf.Attempted, sc = uf.Succeeded, fail = uf.Failed;
            var rf = ResourceFilter;
            bool rsig = rf.SignatureAvailable;
            int rla = rf.LookupAttempted, rat = rf.Attempted, rsc = rf.Succeeded, rfail = rf.Failed;
            bool complete = true;
            try
            {
                foreach (var tile in tiles)
                {
                    if (tile == null || !gate.Explored(tile)) continue;
                    var coord = SafeScalar(MemberWithAccessor(tile, "coordinates"));
                    if (coord == null) { complete = false; continue; }
                    string disclosedTerrain=SafeScalar(MemberWithAccessor(tile,"terrain"));
                    if(disclosedTerrain!=null)f.PublicTerrains[coord]=disclosedTerrain;
                    bool filtered;
                    var u = TileUnitVisible(tile, gs, me.Id, out filtered);
                    if (!filtered) { complete = false; continue; }
                    if (u != null)
                    {
                        var v = new CombatUnitView();
                        v.Id = SafeScalar(MemberWithAccessor(u, "id")); v.Owner = SafeScalar(MemberWithAccessor(u, "owner"));
                        v.Type = SafeScalar(MemberWithAccessor(u, "type")); v.Coord = coord;
                        v.Health = CRInt(MemberWithAccessor(u, "health"));
                        if (v.Id == null || v.Owner == null || v.Health == null) { complete = false; continue; }
                        v.PublicInfo = URPublicInfo(u, gs, URPanelData(u, gs), URHPCurrentBuildVerified(), null);
                        bool owned=v.Owner==f.Me;
                        string publicVisual=owned?null:URVisibleEffects(u,true,URHPCurrentBuildVerified());
                        v.Frozen=owned?PolarisOwnFrozen(u):PolarisVisibleFrozen(publicVisual);
                        v.FrozenAuthority=owned?"owned-native-effects":"visible-renderer-Frozen-particle";
                        v.Poisoned=owned?CymantiOwnEffect(u,"Poisoned"):CymantiVisibleEffect(publicVisual,"Poisoned");
                        v.Swift=owned?CymantiOwnEffect(u,"Swift"):CymantiVisibleEffect(publicVisual,"Swift");
                        v.PoisonedAuthority=owned?"owned-native-effects":"visible-renderer-Poisoned-particle";
                        v.SwiftAuthority=owned?"owned-native-effects":"visible-renderer-Swift-particle";
                        if(owned)v.Links=CymantiOwnRelations(u);
                        if(owned)
                        {
                            var unitData=URPanelData(u,gs);
                            v.AutoFreeze=PolarisHasAbility(unitData,"AutoFreeze");
                            v.FreezeArea=PolarisHasAbility(unitData,"FreezeArea");
                            v.FreezeAttack=PolarisHasAbility(unitData,"Freeze");
                            v.Infiltrate=PolarisHasAbility(unitData,"Infiltrate");
                            v.DoubleAttack=PolarisHasAbility(unitData,"DoubleAttack");
                            v.Swarm=PolarisHasAbility(unitData,"Swarm");v.Explode=PolarisHasAbility(unitData,"Explode");
                            v.Eat=PolarisHasAbility(unitData,"Eat");v.PoisonAttack=PolarisHasAbility(unitData,"Poison");
                        }
                        CRCaptureInvisibleHint(f, v, u, gs);
                        // Turn flags, effect internals, XP and passenger relationship are own-only.
                        if (v.Owner == f.Me)
                        {
                            v.Moved = CRBool(MemberWithAccessor(u, "moved")); v.Attacked = CRBool(MemberWithAccessor(u, "attacked"));
                            string ownTileImprovement;
                            if (TryReadOwnTileImprovement(tile, out ownTileImprovement))
                            {
                                f.RuinsUnderOwnUnits[coord] = ownTileImprovement == "Ruin";
                                f.CitiesUnderOwnUnits[coord] = ownTileImprovement == "City";
                            }
                            v.Promotion = CRInt(MemberWithAccessor(u, "promotionLevel")); v.Xp = CRInt(MemberWithAccessor(u, "xp"));
                            v.CanMove = CRNoArgBool(u, "CanMove"); v.CanAttack = CRNoArgBool(u, "CanAttack");
                            var passenger = MemberWithAccessor(u, "passengerUnit");
                            if (passenger != null && SafeScalar(MemberWithAccessor(passenger, "owner")) == f.Me)
                                v.PassengerId = SafeScalar(MemberWithAccessor(passenger, "id"));
                            var effects = AsEnumerable(MemberWithAccessor(u, "effects"));
                            if (effects != null)
                            {
                                var e = new List<string>();
                                foreach (var effect in effects) { var s = SafeScalar(effect); if (s != null) e.Add(CRQ(s)); }
                                e.Sort(StringComparer.Ordinal); v.Effects = "[" + string.Join(",", e.ToArray()) + "]";
                            }
                        }
                        f.Units[v.Id] = v;
                    }
                    bool ownFloodTile = SafeScalar(MemberWithAccessor(tile, "owner")) == f.Me;
                    bool ownUnitFloodTile = u != null && SafeScalar(MemberWithAccessor(u, "owner")) == f.Me;
                    if (ownFloodTile || ownUnitFloodTile || (gate.HasVisible && gate.Visible(tile)))
                    {
                        var publicFlood = PublicFloodState(tile);
                        if (publicFlood != null) f.PublicFloodTiles[coord] = publicFlood;
                    }
                    if (!ownFloodTile) continue;
                    bool resourceFiltered;
                    object visibleResource = TileResourceVisible(tile, gs, me.Id, out resourceFiltered);
                    if (resourceFiltered)
                    {
                        string resourceType = visibleResource == null ? "none"
                            : SafeScalar(MemberWithAccessor(visibleResource, "type"));
                        if (resourceType != null) f.OwnResources[coord] = resourceType;
                    }
                    string aquaticState = AquaticTileState(tile);
                    if (aquaticState != null) f.OwnTileStates[coord] = aquaticState;
                    string buildingState=CymantiOwnedBuildingState(tile);
                    if(buildingState!=null)f.OwnBuildingStates[coord]=buildingState;
                    var imp = MemberWithAccessor(tile, "improvement");
                    // Roads can be built on owned empty tiles, so their signature is recorded
                    // independently of the presence of a main improvement.
                    {
                        var rewards = imp == null ? null : AsEnumerable(MemberWithAccessor(imp, "rewards")); var rewardList = new List<string>();
                        if (rewards != null) foreach (var reward in rewards) { var s = SafeScalar(reward); if (s != null) rewardList.Add(s); }
                        rewardList.Sort(StringComparer.Ordinal);
                        object hasRoad = ReadBoolPropertyOrGetter(tile, "HasRoad");
                        f.OwnImprovements[coord] = (imp == null ? "none" : SafeScalar(MemberWithAccessor(imp, "type")) + ":" + SafeScalar(MemberWithAccessor(imp, "level")) + ":" + SafeScalar(MemberWithAccessor(imp, "population")))
                            + ":rewards=" + (rewards == null ? "?" : string.Join(",", rewardList.ToArray())) + ":hasRoad=" + SafeScalar(hasRoad)
                            + ":hasRoute=" + SafeScalar(MemberWithAccessor(tile, "hasRoute")) + ":isConnected="
                            + SafeScalar(imp != null && SafeScalar(MemberWithAccessor(imp, "type")) == "City" ? MemberWithAccessor(tile, "IsConnected") : null);
                    }
                }
                var playerList = AsEnumerable(Val(_playersProp, gs));
                if (playerList != null) foreach (var p in playerList)
                    if (p != null && SafeScalar(MemberWithAccessor(p, "Id")) == f.Me) { f.Stars = SafeScalar(MemberWithAccessor(p, "Currency")); break; }
                f.Complete = complete && uf.SignatureAvailable && gate.Errors == 0;
            }
            finally
            {
                uf.SignatureAvailable = sig; uf.LookupAttempted = la; uf.Attempted = at; uf.Succeeded = sc; uf.Failed = fail;
                rf.SignatureAvailable = rsig; rf.LookupAttempted = rla; rf.Attempted = rat; rf.Succeeded = rsc; rf.Failed = rfail;
            }
            return f;
        }

        internal void CombatRuntimeTick(object gs)
        {
            if (_combatSamplingDepth > 0) return;
            CRFreezePublicEndTurnHandoff(gs);
            // Pausing another human's seat preserves this seat's journal and tickets.
            if (!PrivateSamplingAllowed(gs)) return;
            if (_readRequestScope && _requestSampleGeneration == _nativeSampleGeneration
                && CRSameNative(gs, _requestSampleState)) return;
            _combatSamplingDepth++;
            try
            {
            var next = CRCapture(gs);
            if (_readRequestScope)
            { _requestSampleState = gs; _requestSampleGeneration = _nativeSampleGeneration; }
            CRObserve(next, null);
            // Only real frame advancement counts. Repeated queries in the same frame are not stability.
            if (_combatLastSampleTick != _tick)
            { _combatLastSampleTick = _tick; _combatStableFrames++; }
            if (next != null && next.Complete && CRSettlementStatus(gs) == "complete")
            {
                string fp = null;
                foreach (var t in _combatTickets.Values)
                    if (t.Stream == _combatStream && t.Dispatched && t.Accepted == true && t.FinalFrame == null)
                    {
                        if (fp == null) fp = ActionFingerprint(gs, ResolveControlledIdentity(gs));
                        t.FinalFrame = _combatFrame; t.FinalVersion = _combatVersion; t.FinalEvent = _combatEventId; t.FinalFingerprint = fp;
                        var finalPipeline = CRReadPipeline(gs);
                        t.FinalPipelineIdle = CRGameplayPipelineIdle(finalPipeline);
                        t.FinalCompletionAuthority = CRCompletionAuthority(finalPipeline, "complete");
                    }
            }
            }
            finally { _combatSamplingDepth--; }
        }

        private void CRObserve(CombatFrame next, CombatCause cause)
        {
            _combatCaptureAvailable = next != null && next.Complete;
            if (!_combatCaptureAvailable) { _combatStableFrames = 0; return; }
            EnsurePlayerRuntimeScope(next.Episode, next.Me);
            if (_combatFrame == null || _combatFrame.Episode != next.Episode || _combatFrame.Me != next.Me)
            {
                _combatStream = Guid.NewGuid().ToString("N"); _combatVersion = 1; _combatEventId = 0;
                _combatEvents.Clear(); _combatTickets.Clear(); _combatVisibleHookEvidence.Clear();
                _combatFrame = next; _combatSignature = next.Signature();
                _combatLastChangeTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(); _combatStableFrames = 0;
                return;
            }
            string signature = next.Signature();
            if (signature == _combatSignature) return;
            var previous = _combatFrame;
            _combatFrame = next; _combatSignature = signature; _combatVersion++;
            _combatLastChangeTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(); _combatStableFrames = 0;
            foreach (var json in CRDiff(previous, next, cause)) CRAppendEvent(next, cause, json);
            // Even a hidden MoveAction may toggle an own warning. Its private target
            // or action identity must NEVER be attached to this public observation.
            foreach (var json in CRInvisibleHintDiff(previous, next)) CRAppendEvent(next, null, json);
            if (cause != null)
            {
                foreach (var t in _combatTickets.Values)
                {
                    if (t.Stream != _combatStream || t.Accepted == false || t.FinalFrame != null) continue;
                    if (cause.ActionType == "TrainAction")
                        foreach (var u in next.Units.Values)
                            if (u.Owner == next.Me && CRTrainCauseMatchesUnit(cause, u) && !previous.Units.ContainsKey(u.Id)) t.NativeSpawnIds.Add(u.Id);
                    if (cause.ActionType == "KillUnitAction" && cause.TargetId != null && !next.Units.ContainsKey(cause.TargetId)) t.NativeKilledIds.Add(cause.TargetId);
                }
            }
        }

        private void CRAppendEvent(CombatFrame next, CombatCause cause, string json)
        {
            long eid = ++_combatEventId;
            string eventJson = "{\"eventId\":" + eid + ",\"streamId\":" + CRQ(_combatStream) + ",\"stateVersion\":" + _combatVersion
                + ",\"turn\":" + CRQ(next.Turn) + ",\"actingPlayerId\":" + CRQ(cause == null ? null : cause.ActorOwner)
                + ",\"actorId\":" + CRQ(cause == null ? null : cause.ActorId) + ",\"nativeAction\":" + CRQ(cause == null ? null : cause.ActionType)
                + ",\"nativeTargetId\":" + CRQ(cause == null ? null : cause.TargetId) + ",\"nativeTargetCoord\":" + CRQ(cause == null ? null : cause.TargetCoord)
                + ",\"source\":" + CRQ(cause == null ? "visible-snapshot-difference" : "native-action-visible-difference") + "," + json + "}";
            _combatEvents.Add(new KeyValuePair<long, string>(eid, eventJson));
            CRRecordVisibleHookEvidence(cause, eid);
            if (_combatEvents.Count > CombatJournalCapacity) _combatEvents.RemoveAt(0);
        }

        // One structural identity rule for journal, movement/attack settlement, city
        // effects and generated-unit filtering. Direction, destination and native cause
        // remain the callers' action-specific proof, not a second identity rule.
        internal static bool CRCarrierIdentityTransition(CombatFrame before, CombatFrame after,
            CombatUnitView old, CombatUnitView next)
        {
            return before != null && after != null && old != null && next != null
                && before.Complete && after.Complete && !string.IsNullOrEmpty(before.Episode)
                && before.Episode == after.Episode && !string.IsNullOrEmpty(before.Me) && before.Me == after.Me
                && !string.IsNullOrEmpty(old.Id) && !string.IsNullOrEmpty(next.Id) && old.Id != next.Id
                && old.Owner == before.Me && next.Owner == before.Me
                && before.Units.ContainsKey(old.Id) && after.Units.ContainsKey(next.Id)
                && !after.Units.ContainsKey(old.Id) && !before.Units.ContainsKey(next.Id)
                && (next.PassengerId == old.Id || old.PassengerId == next.Id);
        }

        internal static List<string> CRDiff(CombatFrame before, CombatFrame after, CombatCause cause)
        {
            var result = new List<string>();
            var transformedOld = new HashSet<string>(StringComparer.Ordinal);
            var transformedNew = new HashSet<string>(StringComparer.Ordinal);
            foreach (var old in before.Units.Values)
            {
                foreach (var now in after.Units.Values)
                {
                    // An explicit native passenger ID is evidence; matching position/type alone is not.
                    if (CRCarrierIdentityTransition(before, after, old, now))
                    {
                        transformedOld.Add(old.Id); transformedNew.Add(now.Id);
                        result.Add("\"kind\":\"carrier_identity_transition\",\"death\":false,\"before\":" + old.Json() + ",\"after\":" + now.Json());
                    }
                }
            }
            var ids = new List<string>(before.Units.Keys); ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var old = before.Units[id]; CombatUnitView now;
                if (!after.Units.TryGetValue(id, out now))
                {
                    if (transformedOld.Contains(id)) continue;
                    bool nativeKill = cause != null && cause.ActionType == "KillUnitAction" && cause.TargetId == id;
                    result.Add("\"kind\":" + CRQ(nativeKill ? "native_unit_removed" : "lost_visibility_or_removed")
                        + ",\"death\":" + (nativeKill ? "true" : "null") + ",\"cause\":" + CRQ(nativeKill ? "KillUnitAction" : "unknown")
                        + ",\"before\":" + old.Json() + ",\"after\":null");
                }
                else if (old.Signature() != now.Signature())
                    result.Add("\"kind\":\"unit_changed\",\"before\":" + old.Json() + ",\"after\":" + now.Json());
            }
            ids = new List<string>(after.Units.Keys); ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids)
                if (!before.Units.ContainsKey(id) && !transformedNew.Contains(id))
                    result.Add("\"kind\":\"new_visible_unit\",\"generatedByAction\":"
                        + (CRTrainCauseMatchesUnit(cause, after.Units[id]) ? "true" : "null")
                        + ",\"before\":null,\"after\":" + after.Units[id].Json());
            foreach (var tile in after.PublicFloodTiles)
            {
                PublicFloodView oldFlood;
                if (before.PublicFloodTiles.TryGetValue(tile.Key, out oldFlood) && oldFlood.Json() != tile.Value.Json())
                    result.Add("\"kind\":\"public_flood_state_changed\",\"coords\":" + CRQ(tile.Key)
                        + ",\"before\":" + oldFlood.Json() + ",\"after\":" + tile.Value.Json());
            }
            foreach(var tile in after.PublicTerrains)
            {
                string previous;
                if(before.PublicTerrains.TryGetValue(tile.Key,out previous) && previous!=tile.Value
                    && (previous=="Ice" || tile.Value=="Ice"))
                    result.Add("\"kind\":\"ice_terrain_changed\",\"coords\":"+CRQ(tile.Key)
                        +",\"beforeTerrain\":"+CRQ(previous)+",\"afterTerrain\":"+CRQ(tile.Value));
            }
            foreach (var tile in after.OwnTileStates)
            {
                string oldTile;
                if (before.OwnTileStates.TryGetValue(tile.Key, out oldTile) && oldTile != tile.Value)
                    result.Add("\"kind\":\"own_tile_state_changed\",\"coords\":" + CRQ(tile.Key) + ",\"before\":" + oldTile + ",\"after\":" + tile.Value);
            }
            foreach (var resource in after.OwnResources)
            {
                string oldResource;
                if (before.OwnResources.TryGetValue(resource.Key, out oldResource) && oldResource != resource.Value)
                {
                    bool attract = cause != null && cause.NativeAttract && cause.TargetCoord == resource.Key
                        && oldResource == "none" && resource.Value == "Game";
                    result.Add("\"kind\":" + CRQ(attract ? "animal_attracted" : "own_resource_changed")
                        + ",\"coords\":" + CRQ(resource.Key) + ",\"before\":" + CRQ(oldResource)
                        + ",\"after\":" + CRQ(resource.Value) + ",\"nativeReason\":" + CRQ(attract ? "Attract" : null)
                        + ",\"exactSanctuaryCoord\":null");
                }
            }
            foreach(var building in after.OwnBuildingStates)
            {
                string previous;before.OwnBuildingStates.TryGetValue(building.Key,out previous);
                if(previous!=building.Value)result.Add("\"kind\":\"own_building_state_changed\",\"coords\":"+CRQ(building.Key)
                    +",\"before\":"+(previous??"null")+",\"after\":"+building.Value);
            }
            foreach(var building in before.OwnBuildingStates)
                if(!after.OwnBuildingStates.ContainsKey(building.Key) && after.OwnImprovements.ContainsKey(building.Key))
                    result.Add("\"kind\":\"own_building_state_changed\",\"coords\":"+CRQ(building.Key)
                        +",\"before\":"+building.Value+",\"after\":null");
            foreach (var imp in after.OwnImprovements)
            {
                string old; before.OwnImprovements.TryGetValue(imp.Key, out old);
                if (old != imp.Value) result.Add("\"kind\":\"own_improvement_changed\",\"coords\":" + CRQ(imp.Key) + ",\"before\":" + CRQ(old) + ",\"after\":" + CRQ(imp.Value));
            }
            foreach (var imp in before.OwnImprovements)
                if (!after.OwnImprovements.ContainsKey(imp.Key)) result.Add("\"kind\":\"own_improvement_no_longer_observed\",\"coords\":" + CRQ(imp.Key) + ",\"before\":" + CRQ(imp.Value) + ",\"afterOwner\":null,\"cause\":\"unknown\"");
            if (before.Turn != after.Turn || before.Acting != after.Acting || before.Phase != after.Phase)
                result.Add("\"kind\":\"turn_state_changed\",\"beforeTurn\":" + CRQ(before.Turn) + ",\"afterTurn\":" + CRQ(after.Turn) + ",\"phase\":" + CRQ(after.Phase));
            return result;
        }

        internal long BeginCombatSettlement(object gs, string action, string unitId, string targetCoord, string targetArg, long requestId)
        {
            CombatRuntimeTick(gs);
            if (_combatFrame == null || !_combatFrame.Complete) return 0;
            var t = new CombatTicket(); t.Id = ++_combatTicketId; t.RequestId = requestId;
            t.Stream = _combatStream; t.BeforeVersion = _combatVersion; t.StartEvent = _combatEventId;
            t.Action = action; t.UnitId = unitId; t.Coord = targetCoord; t.Arg = targetArg; t.Before = _combatFrame;
            if (IsRuinsAction(action) && string.IsNullOrEmpty(t.UnitId))
            {
                var own = CRUnitAt(t.Before, targetCoord);
                if (own != null && own.Owner == t.Before.Me) t.UnitId = own.Id;
            }
            t.BeforeFingerprint = ActionFingerprint(gs, ResolveControlledIdentity(gs));
            t.NativeVersion = CRInt(MemberWithAccessor(gs, "Version"));
            if ((action == "end_turn" || action == "endturn") && _fixedControlledSeat.HasValue
                && t.Before.Me == _fixedControlledSeat.Value.ToString(CultureInfo.InvariantCulture)
                && t.Before.Acting == t.Before.Me && IsMainHotseatClient(MainClient())
                && SafeScalar(MemberWithAccessor(gs, "CurrentPlayer")) == t.Before.Me
                && HandoffPresentationInactive())
            { t.EndTurnClient = MainClient(); t.EndTurnGameId = MainSessionGameId(t.EndTurnClient); }
            _combatTickets[t.Id] = t;
            // Keep bounded ticket history, but never overwrite an ID with another action.
            if (_combatTickets.Count > 128)
            { var ids = new List<long>(_combatTickets.Keys); ids.Sort(); _combatTickets.Remove(ids[0]); }
            _combatStableFrames = 0; _combatLastChangeTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            return t.Id;
        }

        // Ending a hotseat turn completes at the native handoff curtain. Waiting for
        // another private frame would require opening the next human's map. Keep the
        // old visible frame frozen and attach only the publicly displayed handoff.
        private void CRFreezePublicEndTurnHandoff(object gs)
        {
            CombatTicket candidate = null;
            foreach (var t in _combatTickets.Values)
                if (t.Stream == _combatStream && t.FinalFrame == null && t.Dispatched && t.Accepted == true
                    && (t.Action == "end_turn" || t.Action == "endturn")
                    && t.EndTurnClient != null && t.EndTurnGameId != null)
                { if (candidate != null) return; candidate = t; }
            object displayed = null;
            try { if (_gameStateProp != null) displayed = _gameStateProp.GetValue(null, null); } catch { }
            if (candidate == null || candidate.Before == null || !candidate.Before.Complete
                || !_fixedControlledSeat.HasValue
                || candidate.Before.Me != _fixedControlledSeat.Value.ToString(CultureInfo.InvariantCulture)
                || candidate.Before.Acting != candidate.Before.Me
                || !CRSameNative(candidate.EndTurnClient, MainClient())
                || candidate.EndTurnGameId != MainSessionGameId(MainClient())
                || !CRSameNative(gs, MainClientState()) || !CRSameNative(gs, displayed)
                || candidate.Before.Episode != EpisodeId(gs)
                || SafeScalar(Val(_currentStateProp, gs)) != "Started"
                || ReadBoolPropertyOrGetter(MainClient(), "IsReplay") != false
                || HandoffStaticBool(FindGameType("HotSeatOverlay"), "IsActive") != true) return;
            object manager = MemberWithAccessor(MainClient(), "ActionManager");
            if (!CRSameNative(MemberWithAccessor(manager, "GameState"), gs)
                || ReadBoolPropertyOrGetter(manager, "IsSimulating") != false) return;
            // This branch of ReadHandoff returns from the curtain metadata path;
            // it does not consult triggers, units, players or the concealed map.
            var handoff = ReadHandoff(); int beforeTurn, afterTurn;
            if (handoff.Phase != "human_turn" || handoff.OverlayActive != true || handoff.OverlayPresented != true
                || !handoff.Target.HasValue || handoff.Target == _fixedControlledSeat
                || SafeScalar(MemberWithAccessor(gs, "CurrentPlayer")) != handoff.Target.Value.ToString(CultureInfo.InvariantCulture)
                || !Int32.TryParse(candidate.Before.Turn, NumberStyles.None, CultureInfo.InvariantCulture, out beforeTurn)
                || !Int32.TryParse(handoff.Turn, NumberStyles.None, CultureInfo.InvariantCulture, out afterTurn)
                || afterTurn < beforeTurn || (long)afterTurn > (long)beforeTurn + 1) return;
            candidate.PublicHandoffId = handoff.Id;
            candidate.PublicHandoffTurn = handoff.Turn;
            candidate.PublicHandoffTarget = handoff.Target.Value.ToString(CultureInfo.InvariantCulture);
            candidate.FinalFrame = candidate.Before;
            candidate.FinalVersion = candidate.BeforeVersion; candidate.FinalEvent = candidate.StartEvent;
            candidate.FinalFingerprint = candidate.BeforeFingerprint;
            candidate.FinalPipelineIdle = null;
            candidate.FinalCompletionAuthority = "same-game accepted native EndTurn followed by active presented hotseat curtain and matching public next player/turn; next human pipeline not sampled";
        }

        internal void CompleteCombatDispatch(long ticket, bool? accepted)
        {
            CombatTicket t;
            if (_combatTickets.TryGetValue(ticket, out t)) { t.Accepted = accepted; t.Dispatched = true; }
            _combatStableFrames = 0; _combatLastChangeTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        internal sealed class CombatPipelineView
        {
            internal bool? PropertyProcessing, RawProcessing, Recapping, Simulating, Paused, Pausing, Holding, Reacting;
            internal bool? CurrentReaction, Queued, Popup, EndMatchReaction, Replay, ManagerMatchesState;
            internal int? ReactionCount, PendingMine;
            internal bool TerminalObserved, NativeEnded, LocalTurn;
            internal int HookDepth, StableFrames;
            internal double QuietSeconds;
            internal string Json()
            {
                return "{\"propertyIsProcessing\":" + CRB(PropertyProcessing) + ",\"rawProcessing\":" + CRB(RawProcessing)
                    + ",\"isRecapping\":" + CRB(Recapping) + ",\"isSimulating\":" + CRB(Simulating)
                    + ",\"isPaused\":" + CRB(Paused) + ",\"isPausing\":" + CRB(Pausing)
                    + ",\"isHolding\":" + CRB(Holding) + ",\"isReacting\":" + CRB(Reacting)
                    + ",\"reactionQueueCount\":" + CRN(ReactionCount) + ",\"hasCurrentReaction\":" + CRB(CurrentReaction)
                    + ",\"hasQueuedCommands\":" + CRB(Queued) + ",\"pendingMine\":" + CRN(PendingMine)
                    + ",\"popupOpen\":" + CRB(Popup) + ",\"nativeEnded\":" + CRB(NativeEnded)
                    + ",\"terminalResultObservedSameEpisode\":" + CRB(TerminalObserved)
                    + ",\"currentReactionIsNativeEndMatch\":" + CRB(EndMatchReaction) + ",\"clientIsReplay\":" + CRB(Replay)
                    + ",\"actionManagerMatchesCurrentState\":" + CRB(ManagerMatchesState)
                    + ",\"hookDepth\":" + HookDepth + ",\"stableSamples\":" + StableFrames
                    + ",\"quietMilliseconds\":" + Math.Max(0, QuietSeconds * 1000).ToString("0.###", CultureInfo.InvariantCulture) + "}";
            }
        }

        // A readable null currentReaction is different from an absent/unreadable member.
        // Walk base wrappers as raw ActionManager.processing is inherited by ClientActionManager.
        private static bool CRTryPipelineMember(object o, string name, out object value)
        {
            value = null;
            if (o == null) return false;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                try
                {
                    var p = t.GetProperty(name, flags);
                    if (p != null && p.GetIndexParameters().Length == 0) { value = p.GetValue(o, null); return true; }
                    var f = t.GetField(name, flags);
                    if (f != null) { value = f.GetValue(o); return true; }
                    var m = t.GetMethod("get_" + name, flags, null, Type.EmptyTypes, null);
                    if (m != null && !m.ContainsGenericParameters && m.ReturnType != typeof(void)
                        && !m.ReturnType.IsByRef && !m.ReturnType.IsPointer)
                    { value = m.Invoke(o, null); return true; }
                }
                catch (Exception error) { RecordNativeReadFailure(t, name, error); return false; }
            }
            return false;
        }

        private static bool? CRPipelineBool(object o, string name)
        { object v; return CRTryPipelineMember(o, name, out v) ? CRBool(v) : null; }

        private CombatPipelineView CRReadPipeline(object gs)
        {
            if (_readRequestScope && _requestPipelineGeneration == _nativeSampleGeneration
                && _requestPipeline != null && CRSameNative(gs, _requestPipelineState)) return _requestPipeline;
            _perfPipelineReads++;
            var result = CRReadPipelineCore(gs);
            if (_readRequestScope)
            {
                _requestPipeline = result; _requestPipelineState = gs;
                _requestPipelineGeneration = _nativeSampleGeneration;
            }
            return result;
        }

        private CombatPipelineView CRReadPipelineCore(object gs)
        {
            var p = new CombatPipelineView();
            var identity = ResolveControlledIdentity(gs);
            var am = FindActionManager(_gameManagerType == null ? null : SafeInstance());
            p.PropertyProcessing = ReadBoolPropertyOrGetter(am, "IsProcessing");
            p.RawProcessing = CRPipelineBool(am, "processing");
            p.Recapping = CRPipelineBool(am, "isRecapping"); p.Simulating = CRPipelineBool(am, "isSimulating");
            p.Paused = CRPipelineBool(am, "isPaused"); p.Pausing = CRPipelineBool(am, "isPausing");
            p.Holding = CRPipelineBool(am, "isHolding"); p.Reacting = CRPipelineBool(am, "isReacting");
            object value; int count;
            if (CRTryPipelineMember(am, "reactionQueue", out value) && value != null && TryCount(value, out count)) p.ReactionCount = count;
            if (CRTryPipelineMember(am, "currentReaction", out value))
            {
                p.CurrentReaction = value != null;
                var terminalType = FindGameType("EndMatchReaction");
                p.EndMatchReaction = NativeCastIs(value, terminalType);
            }
            // EndMatchReaction intentionally omits onComplete only on its non-replay path.
            p.Replay = ReadBoolPropertyOrGetter(StaticMember(_gameManagerType, "Client"), "IsReplay");
            if (CRTryPipelineMember(am, "gameState", out value)) p.ManagerMatchesState = CRSameNative(value, gs);
            p.Queued = CRNoArgBool(gs, "HasQueuedCommands");
            if (identity.Verified) p.PendingMine = PendingTriggerCountFor(gs, identity.Id, new JsonArr());
            try { p.Popup = PopupIsOpen(); } catch { }
            // Use only the existing natural MatchEnded observer; never call TryGetWinner here.
            p.TerminalObserved = identity.Verified && _publicResultHookInstalled && _publicResultState != null
                && _publicResultWon.HasValue && CRSameNative(_publicResultState, gs)
                && _publicResultEpisode == EpisodeId(gs) && _publicResultControlled == identity.Id;
            p.NativeEnded = SafeScalar(Val(_currentStateProp, gs)) == "Ended";
            p.LocalTurn = _combatFrame != null && _combatFrame.Acting == _combatFrame.Me;
            p.HookDepth = _combatHookDepth; p.StableFrames = _combatStableFrames;
            p.QuietSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - _combatLastChangeTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
            return p;
        }

        internal static string CRPipelineSettlementPolicy(CombatPipelineView p)
        {
            if (p == null || p.PropertyProcessing == null || p.RawProcessing == null || p.Recapping == null
                || p.Simulating == null || p.Paused == null || p.Pausing == null || p.Holding == null
                || p.Reacting == null || p.ReactionCount == null || p.CurrentReaction == null || p.Queued == null
                || p.PendingMine == null || p.Popup == null) return "unknown";
            // A stable modal on the controlled turn can itself be retaining a
            // reaction/queue/processing flag until its native button is chosen.
            // Report the blocker without claiming idle or completion. Execution
            // eligibility remains checked separately by the popup/action routes.
            if (p.Popup.Value && p.LocalTurn && !p.Recapping.Value && !p.Simulating.Value
                && !p.Pausing.Value && p.HookDepth == 0 && p.StableFrames >= 2 && p.QuietSeconds >= 0.4)
                return "needs_resolution";
            if (p.Recapping.Value || p.Simulating.Value
                || p.Pausing.Value || p.Reacting.Value || p.ReactionCount.Value != 0
                || p.Queued.Value || p.HookDepth > 0) return "pending";
            bool terminal = p.NativeEnded && p.TerminalObserved;
            bool terminalPresentation = false;
            if (terminal && p.CurrentReaction.Value)
            {
                if (p.EndMatchReaction == null) return "unknown";
                if (p.EndMatchReaction.Value && (p.Replay == null || p.ManagerMatchesState == null)) return "unknown";
                terminalPresentation = p.EndMatchReaction == true && p.Replay == false && p.ManagerMatchesState == true;
            }
            // Native EndMatchReaction calls MatchEnded and returns WITHOUT onComplete in
            // non-replay gameplay. Keep raw flags truthful; only this terminal presentation
            // may retain processing/currentReaction after the result has been observed.
            if (!terminalPresentation && (p.PropertyProcessing.Value || p.RawProcessing.Value || p.CurrentReaction.Value)) return "pending";
            if (!terminal && (p.Paused.Value || p.Holding.Value)) return "pending";
            if (p.PendingMine.Value > 0 || p.Popup.Value) return "needs_resolution";
            if ((!terminal && !p.LocalTurn) || p.StableFrames < 2 || p.QuietSeconds < 0.4) return "pending";
            return "complete";
        }

        internal static bool? CRGameplayPipelineIdle(CombatPipelineView p)
        {
            if (p == null) return null;
            foreach (var busy in new[] { p.PropertyProcessing, p.RawProcessing, p.Recapping, p.Simulating,
                p.Paused, p.Pausing, p.Holding, p.Reacting, p.CurrentReaction, p.Queued, p.Popup })
                if (busy == true) return false;
            if (p.ReactionCount > 0 || p.PendingMine > 0 || p.HookDepth > 0) return false;
            foreach (var busy in new[] { p.PropertyProcessing, p.RawProcessing, p.Recapping, p.Simulating,
                p.Paused, p.Pausing, p.Holding, p.Reacting, p.CurrentReaction, p.Queued, p.Popup })
                if (busy == null) return null;
            return p.ReactionCount.HasValue && p.PendingMine.HasValue ? (bool?)true : null;
        }

        internal static string CRCompletionAuthority(CombatPipelineView p, string status)
        {
            if (status != "complete") return null;
            if (p.NativeEnded && p.TerminalObserved)
                return p.CurrentReaction == true && p.EndMatchReaction == true && p.Replay == false && p.ManagerMatchesState == true
                    ? "same-episode natural MatchEnded and native Ended; non-replay native EndMatchReaction intentionally retains currentReaction/processing without onComplete; other queues, reactions, triggers and popup cleared, visible state stable"
                    : "same-episode natural MatchEnded and native Ended; actual raw processing and queues idle, triggers and popup cleared, visible state stable; terminal lifecycle pause/hold may remain";
            return "actual native raw pipeline and queues idle, triggers and popup cleared, local turn and stable visible state";
        }

        private string CRSettlementStatus(object gs)
        {
            var id = ResolveControlledIdentity(gs);
            if (!_combatCaptureAvailable || !id.Verified || _combatFrame == null || !_combatFrame.Complete || _combatFrame.Me != id.Id.ToString(CultureInfo.InvariantCulture)) return "unknown";
            return CRPipelineSettlementPolicy(CRReadPipeline(gs));
        }

        // A pending choice must be allowed to resolve its own pause. This is NOT settlementComplete.
        // In particular, queued commands blocked behind that trigger cannot require the trigger
        // already to be gone. Unrelated reactions remain a hard blocker.
        internal bool? CombatResolutionReady(object gs)
        {
            CombatRuntimeTick(gs);
            var identity = ResolveControlledIdentity(gs);
            if (!_combatCaptureAvailable || !identity.Verified) return null;
            var am = FindActionManager(_gameManagerType == null ? null : SafeInstance());
            if (am == null) return null;
            var queue = MemberWithAccessor(am, "reactionQueue"); int count;
            int? reactionCount = queue != null && TryCount(queue, out count) ? (int?)count : null;
            bool? waiting = null;
            try { var m = am.GetType().GetMethod("IsWaitingForCommandTrigger", new[] { typeof(byte) }); if (m != null) waiting = (bool)m.Invoke(am, new object[] { identity.Id }); } catch { }
            var reaction = MemberWithAccessor(am, "currentReaction");
            bool? resolutionReaction = NativeCastIs(reaction, FindGameType("CityLevelUpReaction"));
            double quiet = (System.Diagnostics.Stopwatch.GetTimestamp() - _combatLastChangeTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
            return CRResolutionPolicy(ReadBoolPropertyOrGetter(am, "IsProcessing"), ReadBoolPropertyOrGetter(am, "IsSimulating"),
                CRBool(MemberWithAccessor(am, "isReacting")), CRBool(MemberWithAccessor(am, "isPausing")), reactionCount,
                reaction != null, resolutionReaction, CRNoArgBool(gs, "HasQueuedCommands"), waiting,
                PendingTriggerCountFor(gs, identity.Id, new JsonArr()), _combatStableFrames, quiet);
        }

        internal static bool? CRResolutionPolicy(bool? processing, bool? simulating, bool? reacting, bool? pausing,
            int? reactionCount, bool hasCurrentReaction, bool? isChoiceReaction, bool? queued, bool? waiting,
            int? pendingMine, int stableFrames, double quietSeconds)
        {
            if (processing == null || simulating == null || reacting == null || pausing == null || reactionCount == null
                || queued == null || waiting == null || pendingMine == null) return null;
            if (simulating.Value || pausing.Value || stableFrames < 2 || quietSeconds < 0.4) return false;
            bool mineChoice = pendingMine.Value > 0 && waiting.Value;
            if (hasCurrentReaction || reacting.Value || reactionCount.Value > 0)
                return !mineChoice || reactionCount.Value != 0 ? (bool?)false : isChoiceReaction;
            if (processing.Value || queued.Value) return mineChoice;
            return true;
        }

        internal string CombatStateJson(object gs)
        {
            CombatRuntimeTick(gs);
            string status = CRSettlementStatus(gs);
            var pipeline = CRReadPipeline(gs);
            return "{\"schema\":\"visible-combat-state-1\",\"streamId\":" + CRQ(_combatStream) + ",\"stateVersion\":" + _combatVersion
                + ",\"lastEventId\":" + _combatEventId + ",\"settlementStatus\":" + CRQ(status)
                + ",\"settlementComplete\":" + (status == "complete" ? "true" : "false")
                + ",\"pipelineIdle\":" + CRB(CRGameplayPipelineIdle(pipeline))
                + ",\"completionAuthority\":" + CRQ(CRCompletionAuthority(pipeline, status))
                + (_activeAuditRequested ? ",\"pipeline\":" + pipeline.Json() : "") + "}";
        }

        internal string CombatEventsJson(object gs, long afterEventId, int limit)
        {
            CombatRuntimeTick(gs);
            if (afterEventId < 0 || afterEventId > _combatEventId)
                return "{\"ok\":false,\"errorCode\":" + CRQ(afterEventId < 0 ? "INVALID_EVENT_CURSOR" : "EVENT_CURSOR_AHEAD")
                    + ",\"streamId\":" + CRQ(_combatStream) + ",\"latestEventId\":" + _combatEventId + "}";
            limit = Math.Max(1, Math.Min(256, limit));
            var result = new List<string>(); long cursor = afterEventId;
            long oldest = _combatEvents.Count == 0 ? _combatEventId + 1 : _combatEvents[0].Key;
            foreach (var item in _combatEvents)
                if (item.Key > afterEventId && result.Count < limit) { result.Add(item.Value); cursor = item.Key; }
            return "{\"ok\":true,\"schema\":\"visible-events-1\",\"streamId\":" + CRQ(_combatStream) + ",\"stateVersion\":" + _combatVersion
                + ",\"afterEventId\":" + afterEventId + ",\"nextEventId\":" + cursor + ",\"oldestEventId\":" + oldest
                + ",\"historyGap\":" + (afterEventId < oldest - 1 ? "true" : "false") + ",\"hasMore\":" + (cursor < _combatEventId ? "true" : "false")
                + ",\"nativeHookCoverage\":" + CRQ(CRNativeHookCoverageStatus())
                + ",\"nativeHooks\":" + CRNativeHookEvidenceJson()
                + ",\"events\":[" + string.Join(",", result.ToArray()) + "]}";
        }

        internal string CombatSettlementJson(object gs, long ticket)
        {
            CombatTicket pendingDisplayTicket;
            if (_combatTickets.TryGetValue(ticket, out pendingDisplayTicket))
                DismissCompletedPeaceResponseDisplay(gs, pendingDisplayTicket);
            CombatRuntimeTick(gs);
            CombatTicket t;
            if (!_combatTickets.TryGetValue(ticket, out t) || t.Stream != _combatStream)
                return "{\"ok\":false,\"errorCode\":\"SETTLEMENT_TICKET_UNKNOWN\",\"settlementComplete\":false}";
            bool canSample = t.FinalFrame == null && PrivateSamplingAllowed(gs);
            string status = !t.Dispatched || t.Accepted == null ? "unknown" : t.Accepted == false ? "rejected" : t.FinalFrame != null ? "complete" : canSample ? CRSettlementStatus(gs) : "unknown";
            var settlementPipeline = canSample ? CRReadPipeline(gs) : null;
            string basis = null, confirmation = "unknown";
            var current = t.FinalFrame ?? _combatFrame;
            var actualAffected = new List<string>();
            string attackOutcome = "null";
            if (status == "complete" && t.PublicHandoffId != null)
            { confirmation = "confirmed"; basis = t.FinalCompletionAuthority; }
            else if (status == "complete" && current != null)
            {
                confirmation = ConfirmAction(t.Before.Me, t.Action, t.UnitId, t.Coord, t.Arg,
                    t.BeforeFingerprint, t.FinalFingerprint ?? ActionFingerprint(gs, ResolveControlledIdentity(gs)), out basis);
                CombatUnitView old, now;
                t.Before.Units.TryGetValue(t.UnitId ?? "", out old); current.Units.TryGetValue(t.UnitId ?? "", out now);
                string name = (t.Action ?? "").ToLowerInvariant();
                if (name == "build" && t.Arg == "EnchantAnimal")
                {
                    confirmation = "unknown";
                    basis = "animal resource consumption alone does not confirm enchantment; exact native TrainAction and newly created own unit required";
                    if (CREnchantmentSettled(t, current))
                    {
                        confirmation = "confirmed";
                        basis = "matched own EnchantAnimal BuildAction and its exact executed TrainAction; visible Game consumed and new own unit of actual native train type observed";
                        actualAffected.Add("{\"role\":\"enchanted_animal\",\"before\":null,\"after\":" + t.NativeEnchantedUnit.Json() + "}");
                    }
                }
                actualAffected.AddRange(CRAreaDamageJson(t));
                string settledFlood;
                PublicFloodView settledCanal;
                PublicFloodView settledClear;
                if (name == "clear_tile_effect" && t.Arg == "Flooded" && t.NativeClearFloodAfter != null
                    && current.PublicFloodTiles.TryGetValue(t.Coord ?? "",out settledClear)
                    && !settledClear.Flooded && settledClear.Json() == t.NativeClearFloodAfter)
                {
                    confirmation="confirmed"; basis="matched own ClearTileEffectAction for requested Flooded effect and own-unit terrain flooded true->false";
                    actualAffected.Add("{\"role\":\"tile_terrain_effect\",\"coords\":"+CRQ(t.Coord)
                        +",\"before\":"+t.NativeClearFloodBefore+",\"after\":"+t.NativeClearFloodAfter+"}");
                }
                if (name == "build" && t.Arg == "Canal" && t.NativeCanalAfter != null
                    && current.PublicFloodTiles.TryGetValue(t.Coord ?? "", out settledCanal)
                    && settledCanal.Flooded && settledCanal.Json() == t.NativeCanalAfter)
                {
                    confirmation = "confirmed"; basis = "matched own Canal native execution and correlated FloodTileAction; requested visible terrain flooded false->true";
                    actualAffected.Add("{\"role\":\"tile_terrain_effect\",\"coords\":" + CRQ(t.Coord)
                        + ",\"before\":" + t.NativeCanalBefore + ",\"after\":" + t.NativeCanalAfter + "}");
                }
                if (name == "flood" && t.NativeFloodAfter != null
                    && current.OwnTileStates.TryGetValue(t.Coord ?? "", out settledFlood) && settledFlood == t.NativeFloodAfter)
                {
                    confirmation = "confirmed"; basis = "matched own FloodTileAction executed and requested owned tile terrain/effects changed";
                    actualAffected.Add("{\"role\":\"tile_terrain_effect\",\"coords\":" + CRQ(t.Coord)
                        + ",\"before\":" + t.NativeFloodBefore + ",\"after\":" + t.NativeFloodAfter + "}");
                }
                if ((name == "move" || name == "disembark") && old != null)
                {
                    foreach (var u in current.Units.Values)
                        if (u.Coord == t.Coord && CRCarrierIdentityTransition(t.Before, current, old, u))
                        { confirmation = "confirmed"; basis = "requested destination and native passenger identity transition observed"; }
                }
                if (CRAttackEmbarkConfirmed(t, current))
                { confirmation = "confirmed"; basis = "named native AttackAction and target KillUnitAction observed; surviving own attacker became carrier passenger at requested destination with consumed attack"; }
                if (name == "attack" && t.NativeAttackTargets.Count > 0)
                {
                    string attackBasis;
                    var attackConfirmation = CRAttackEffectConfirmation(t, current, out attackBasis);
                    if (attackConfirmation != null) { confirmation = attackConfirmation; basis = attackBasis; }
                    CombatUnitView survivingActor;
                    string actorOutcome = CRAttackActorOutcome(t, current, out survivingActor);
                    var target = CRUnitAt(t.Before, t.Coord);
                    bool targetKilled = target != null && t.NativeAttackKilledIds.Contains(target.Id);
                    attackOutcome = "{\"actorStatus\":" + CRQ(actorOutcome)
                        + ",\"actorBefore\":" + (old == null ? "null" : old.Json())
                        + ",\"actorAfter\":" + (survivingActor == null ? "null" : survivingActor.Json())
                        + ",\"primaryTargetId\":" + CRQ(target == null ? null : target.Id)
                        + ",\"primaryTargetKilled\":" + (targetKilled ? "true" : target != null && current.Units.ContainsKey(target.Id) ? "false" : "null")
                        + ",\"authority\":\"matched native primary attack, visible damage, retaliation and kill receipts; own passenger identity only\"}";
                    if (t.NativePrimaryBefore != null && t.NativePrimaryAfter != null)
                        actualAffected.Add("{\"role\":\"primary_target\",\"source\":\"matched_native_AttackAction\",\"death\":"
                            + (targetKilled ? "true" : t.NativePrimaryAfter.Health > 0 ? "false" : "null")
                            + ",\"before\":" + t.NativePrimaryBefore.Json() + ",\"after\":" + t.NativePrimaryAfter.Json() + "}");
                    if (t.NativeRetaliationBefore != null && t.NativeRetaliationAfter != null)
                        actualAffected.Add("{\"role\":\"attacker_retaliation\",\"source\":\"matched_native_AttackAction\",\"death\":"
                            + (t.NativeAttackKilledIds.Contains(t.UnitId) ? "true" : t.NativeRetaliationAfter.Health > 0 ? "false" : "null")
                            + ",\"before\":" + t.NativeRetaliationBefore.Json() + ",\"after\":" + t.NativeRetaliationAfter.Json() + "}");
                }
                if (old != null && now != null && old.Owner == current.Me && now.Owner == current.Me)
                {
                    if ((name == "promote" || name == "veteran") && old.Promotion.HasValue && now.Promotion > old.Promotion)
                    { confirmation = "confirmed"; basis = "named unit native promotionLevel increased"; }
                    if ((name == "recover" || name == "heal_self") && t.NativeRecoverObserved
                        && t.NativeHealTargets.Contains(old.Id) && CRHealingChanged(old, now))
                    { confirmation = "confirmed"; basis = "named own unit native recovery and HealAction observed; health increased or native Poison removed";
                        actualAffected.Add("{\"before\":" + old.Json() + ",\"after\":" + now.Json() + "}"); }
                }
                if (CRInfiltrationConfirmed(t, current))
                { confirmation = "confirmed"; basis = "named own Infiltrate-capable unit's native InfiltrateAction executed; exact appended InfiltrationRewardAction and TrainAction objects executed and their new own visible unit IDs/coordinates observed; no hidden city ownership inference"; }
                if (IsRuinsAction(t.Action))
                {
                    confirmation = CRRuinsConfirmed(t,current) ? "confirmed" : "unknown";
                    basis = confirmation == "confirmed" ? "matched own ExamineRuinsAction executed; requested ruin observed before and visibly consumed afterwards; reward selection separately reported"
                        : "target-bound executed ruins action and visible consumption proof unavailable";
                }
                if (CRConversionConfirmed(t, current))
                {
                    confirmation = "confirmed"; basis = "named own native ConvertAction executed against requested visible unit ID/coordinate; same unit's observed owner changed from enemy to controlled player";
                    var convertedBefore = CRUnitAt(t.Before, t.Coord);
                    actualAffected.Add("{\"before\":" + convertedBefore.Json() + ",\"after\":" + current.Units[convertedBefore.Id].Json() + "}");
                }
                string diplomaticBasis;
                string polarisBasis;
                var polarisConfirmation=PolarisConfirmation(t,current,actualAffected,out polarisBasis);
                if(polarisConfirmation!=null){confirmation=polarisConfirmation;basis=polarisBasis;}
                var diplomaticConfirmation = PublicSettlementConfirmation(t.Id, gs, out diplomaticBasis);
                if (diplomaticConfirmation != null) { confirmation = diplomaticConfirmation; basis = diplomaticBasis; }
                if (name == "heal_others" || name == "boost" || name == "swarm")
                {
                    bool readable = false;
                    foreach (var u in current.Units.Values)
                    {
                        CombatUnitView previous;
                        if (u.Owner != current.Me || !t.Before.Units.TryGetValue(u.Id, out previous) || previous.Owner != current.Me) continue;
                        bool effect = false;
                        if (name == "heal_others" && (previous.Health.HasValue && u.Health.HasValue || previous.Effects != null && u.Effects != null))
                        { readable = true; effect = t.NativeHealTargets.Contains(u.Id) && CRHealingChanged(previous, u); }
                        if ((name == "boost" || name == "swarm" && t.Cymanti.SwarmExecuted) && previous.Effects != null && u.Effects != null && t.NativeVersion.HasValue)
                        { readable = true; effect = t.NativeBoostTargets.Contains(u.Id) && CRBoostEffectAdded(previous.Effects, u.Effects, t.NativeVersion); }
                        if (effect) actualAffected.Add("{\"before\":" + previous.Json() + ",\"after\":" + u.Json() + "}");
                    }
                    confirmation = actualAffected.Count > 0 ? "confirmed" : readable ? "not_confirmed" : "unknown";
                    basis = name == "heal_others" ? "all own visible health increases or Poison removal linked to this native HealOthersAction's exact HealAction children"
                        : "own native effect additions linked to executed Boost/BoostOthers or SwarmCommand exact BoostAction children; current version requires Swift";
                }
            }
            if(status=="complete" && current!=null)
            {
                actualAffected.AddRange(CymantiAffected(t,current));
                string cymantiBasis;var cymantiConfirmation=CymantiConfirmation(t,current,out cymantiBasis);
                if(cymantiConfirmation!=null){confirmation=cymantiConfirmation;basis=cymantiBasis;}
                string algaeBasis;var algaeConfirmation=CymantiAlgaeBuildConfirmation(t,current,out algaeBasis);
                if(algaeConfirmation!=null){confirmation=algaeConfirmation;basis=algaeBasis;}
                string decomposeBasis;var decomposeConfirmation=CymantiDecomposeConfirmation(t,current,out decomposeBasis);
                if(decomposeConfirmation!=null)
                {
                    confirmation=decomposeConfirmation;basis=decomposeBasis;
                    if(confirmation=="confirmed")actualAffected.Add(new JsonObj().Str("role","owned_building_effect")
                        .Str("coords",t.Coord).Raw("before",t.CymantiDecompose.Before).Raw("after",t.CymantiDecompose.After).Close());
                }
            }
            var changes = current == null ? new List<string>() : CRDiff(t.Before, current, null);
            if (CRCityRewardJson(t, current) != "null") actualAffected.AddRange(t.NativeCityReward.Effects);
            var changed = new List<string>(); foreach (var c in changes) changed.Add("{" + c + "}");
            var spawned = new List<string>();
            if (current != null) foreach (var id in t.NativeSpawnIds)
            {
                CombatUnitView u;
                if (!current.Units.TryGetValue(id, out u) || t.Before.Units.ContainsKey(id)) continue;
                bool conversion = false;
                foreach (var old in t.Before.Units.Values)
                    if (CRCarrierIdentityTransition(t.Before, current, old, u)) conversion = true;
                if (!conversion) spawned.Add(u.Json());
            }
            // Cloak's actual generated positions come from native TrainAction observations, never layout guesses.
            return "{\"ok\":true,\"schema\":\"action-settlement-1\",\"ticketId\":" + ticket + ",\"requestId\":" + t.RequestId
                + ",\"episodeId\":" + CRQ(t.Before == null ? null : t.Before.Episode)
                + ",\"controlledPlayerId\":" + CRQ(t.Before == null ? null : t.Before.Me)
                + ",\"streamId\":" + CRQ(t.Stream) + ",\"action\":" + CRQ(t.Action) + ",\"actorId\":" + CRQ(t.UnitId) + ",\"targetCoord\":" + CRQ(t.Coord)
                + ",\"beforeStateVersion\":" + t.BeforeVersion + ",\"stateVersion\":" + (t.FinalFrame == null ? _combatVersion : t.FinalVersion) + ",\"startEventId\":" + t.StartEvent
                + ",\"lastEventId\":" + (t.FinalFrame == null ? _combatEventId : t.FinalEvent) + ",\"settlementStatus\":" + CRQ(status) + ",\"settlementComplete\":" + (status == "complete" ? "true" : "false")
                + ",\"pipelineIdle\":" + CRB(t.FinalFrame == null ? CRGameplayPipelineIdle(settlementPipeline) : t.FinalPipelineIdle)
                + ",\"completionAuthority\":" + CRQ(t.FinalFrame == null ? CRCompletionAuthority(settlementPipeline, status) : t.FinalCompletionAuthority)
                + ",\"effectConfirmation\":" + CRQ(confirmation) + ",\"confirmationBasis\":" + CRQ(basis)
                + ",\"actionCompleted\":" + (status == "complete" && confirmation == "confirmed" ? "true" : "false")
                + ",\"publicHandoff\":" + (t.PublicHandoffId == null ? "null" : "{\"handoffId\":" + CRQ(t.PublicHandoffId)
                    + ",\"turn\":" + CRQ(t.PublicHandoffTurn) + ",\"targetPlayerId\":" + CRQ(t.PublicHandoffTarget) + ",\"overlayActive\":true}")
                + ",\"visibleChanges\":[" + string.Join(",", changed.ToArray()) + "],\"nativeGeneratedUnits\":[" + string.Join(",", spawned.ToArray())
                + "],\"actualAffectedUnits\":[" + string.Join(",", actualAffected.ToArray()) + "],\"attackOutcome\":" + attackOutcome
                + ",\"actualAffectedUnitsCoverage\":\"partial; matched native primary, retaliation, exact AreaDamage and city reward children; event-time visible units only; unassociated effects remain unknown\""
                + ",\"nativeAreaHookInstalled\":" + (_combatAreaHookInstalled ? "true" : "false")
                + ",\"ruinsOutcome\":" + (status == "complete" ? CRRuinsJson(t,current) : "null")
                + ",\"cityRewardOutcome\":" + (status == "complete" ? CRCityRewardJson(t,current) : "null")
                + ",\"canalOutcome\":" + (status == "complete" ? CRCanalOutcome(t) : "null")
                + ",\"clearTileEffectOutcome\":" + (status == "complete" ? CRClearTileEffectOutcome(t) : "null")
                 + ",\"cymantiOutcome\":" + (status == "complete" ? CymantiOutcome(t,current) : "null")
                + ",\"cymantiDecomposeOutcome\":" + (status == "complete" ? CymantiDecomposeOutcome(t,current) : "null")
                + ",\"cymantiAlgaeBuildOutcome\":" + (status == "complete" ? CymantiAlgaeBuildOutcome(t,current) : "null")
                + ",\"polarisOutcome\":" + (status == "complete" ? PolarisOutcome(t,current) : "null")
                + ",\"generationAssociation\":\"native TrainAction during tracked interval; inspect actor/target and events; absence is not proof of no spawn\"}";
        }

        internal string AttachCombatSettlement(string response, object gs, long ticket)
        {
            string settlement = CombatSettlementJson(gs, ticket);
            return MergeActionSettlement(response, settlement, ticket);
        }

        // Shared post-execution qualification. Native type names remain dynamic; neither
        // reward source may call an aborted/unknown train a generated unit of another type.
        internal static bool CRTryAppliedTrainType(object action, out string unitType)
        {
            unitType = null;
            if (action == null || NativeBool(MemberWithAccessor(action, "trainActionAborted")) != false) return false;
            unitType = SafeScalar(MemberWithAccessor(action, "Type"));
            return !string.IsNullOrEmpty(unitType);
        }

        private static bool CRTrainCauseMatchesUnit(CombatCause cause, CombatUnitView unit)
        {
            return cause != null && unit != null && cause.VisibleNativeAction && cause.ActionType == "TrainAction"
                && !string.IsNullOrEmpty(cause.AppliedTrainType) && cause.AppliedTrainType == unit.Type
                && cause.ActorOwner == unit.Owner && cause.TargetCoord == unit.Coord;
        }

        private static CombatUnitView CRUnitAt(CombatFrame f, string coord)
        { if (f == null || coord == null) return null; foreach (var u in f.Units.Values) if (u.Coord == coord) return u; return null; }
        private static int? CRInt(object value)
        {
            return NativeInt32(value);
        }
        private static bool? CRBool(object value) { return NativeBool(value); }
        private static bool? CRNoArgBool(object value, string method)
        {
            if (value == null) return null;
            try { var m = value.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null); if (m != null && m.ReturnType == typeof(bool)) return (bool)m.Invoke(value, null); } catch { }
            return ReadBoolPropertyOrGetter(value, method);
        }
        private static string CRN(int? n) { return n.HasValue ? n.Value.ToString(CultureInfo.InvariantCulture) : "null"; }
        private static string CRB(bool? b) { return b.HasValue ? b.Value ? "true" : "false" : "null"; }
        private static string CRQ(string s)
        {
            if (s == null) return "null";
            var b = new StringBuilder("\""); foreach (char c in s)
            { if (c == '\\' || c == '"') b.Append('\\').Append(c); else if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4")); else b.Append(c); }
            return b.Append('"').ToString();
        }
    }
}
