// Installation is process-scoped; visible journal evidence is scoped to its stream.
using System;
using System.Collections.Generic;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        // Audited hook targets, not a catalog of available player actions.
        private static readonly string[] CombatHookTypes = { "AttackAction", "MoveAction", "KillUnitAction", "TrainAction", "EmbarkAction", "DisembarkAction", "InfiltrateAction", "InfiltrationRewardAction", "CaptureCityAction", "RecoverAction", "HealAction", "HealOthersAction", "BoostAction", "BoostOthersAction", "PromoteAction", "ConvertAction", "EstablishEmbassyAction", "DestroyEmbassyAction", "BreakPeaceAction", "PeaceTreatyAction", "PeaceRequestResponseAction", "ReceiveDiplomacyMessageAction", "ExamineRuinsAction", "IncreaseCurrencyAction", "ResearchAction", "CityRewardAction", "IncreasePopulationAction", "ScoutMoveAction", "ExploreAction" };
        private sealed class VisibleHookEvidence
        {
            internal long FirstEventId, LastEventId, EventCount;
        }
        private readonly Dictionary<string, string> _combatHookInstallation = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, VisibleHookEvidence> _combatVisibleHookEvidence = new Dictionary<string, VisibleHookEvidence>(StringComparer.Ordinal);

        private void CRRegisterHookInstallation(string kind, string status)
        {
            _combatHookInstallation[kind] = status;
            _combatHookInstalled = 0; _combatHookFailed = 0;
            foreach (var name in CombatHookTypes)
            {
                string current;
                if (!_combatHookInstallation.TryGetValue(name, out current)) continue;
                if (current == "installed") _combatHookInstalled++; else _combatHookFailed++;
            }
        }

        private void CRRecordVisibleHookEvidence(CombatCause cause, long eventId)
        {
            string installed;
            if (cause == null || !cause.VisibleNativeAction || cause.ActionType == null
                || !_combatHookInstallation.TryGetValue(cause.ActionType, out installed) || installed != "installed") return;
            VisibleHookEvidence evidence;
            if (!_combatVisibleHookEvidence.TryGetValue(cause.ActionType, out evidence))
            {
                evidence = new VisibleHookEvidence(); evidence.FirstEventId = eventId;
                _combatVisibleHookEvidence.Add(cause.ActionType, evidence);
            }
            evidence.LastEventId = eventId; evidence.EventCount++;
        }

        private string CRNativeHookCoverageStatus()
        {
            if (_combatHookInstalled != CombatHookTypes.Length || _combatHookFailed != 0) return "partial-or-unavailable";
            return _combatVisibleHookEvidence.Count == 0 ? "installed-not-live-validated" : "installed-with-visible-evidence";
        }

        private string CRNativeHookEvidenceJson()
        {
            var installation = new List<string>(); var observed = new List<string>(); var unobserved = new List<string>();
            foreach (var kind in CombatHookTypes)
            {
                string status;
                if (!_combatHookInstallation.TryGetValue(kind, out status)) status = "not_attempted";
                installation.Add("{\"nativeAction\":" + CRQ(kind) + ",\"status\":" + CRQ(status) + "}");
                VisibleHookEvidence evidence;
                if (_combatVisibleHookEvidence.TryGetValue(kind, out evidence))
                    observed.Add("{\"nativeAction\":" + CRQ(kind) + ",\"firstEventId\":" + evidence.FirstEventId
                        + ",\"lastEventId\":" + evidence.LastEventId + ",\"visibleEventCount\":" + evidence.EventCount + "}");
                else unobserved.Add(CRQ(kind));
            }
            string state = _combatHookInstalled == CombatHookTypes.Length && _combatHookFailed == 0 ? "complete"
                : _combatHookInstalled > 0 ? "partial" : _combatInstallationAttempted ? "unavailable" : "not_attempted";
            return "{\"schema\":\"visible-native-hook-evidence-1\",\"scope\":\"combat-action-execute\",\"streamId\":" + CRQ(_combatStream)
                + ",\"installationStatus\":" + CRQ(state) + ",\"expectedTypeCount\":" + CombatHookTypes.Length
                + ",\"installedTypeCount\":" + _combatHookInstalled + ",\"installation\":[" + string.Join(",", installation.ToArray())
                + "],\"observedVisibleTypes\":[" + string.Join(",", observed.ToArray()) + "],\"unobservedTypes\":[" + string.Join(",", unobserved.ToArray())
                + "],\"completeActionCoverage\":false,\"authority\":\"Installation is not execution evidence. Observations count visible journal entries from successfully completed hooks in this stream, not action executions. Hidden actions and snapshot-only differences do not contribute. Evidence survives journal eviction; ids may precede oldestEventId. Unobserved types and composite effects remain unvalidated.\"}";
        }
    }
}
