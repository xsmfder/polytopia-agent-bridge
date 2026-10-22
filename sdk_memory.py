"""Player notes from coherent SDK observations; optional validated persistence."""
import copy

from observation import normalize_world, ObservationRejected, public_unit_info
from sightings import EnemyMemory, Sighting, STATUS_VISIBLE, STATUS_UNOBSERVED, STATUS_DESTROYED


def _int(value, minimum=0, nullable=False):
    return nullable and value is None or type(value) is int and value >= minimum


class ObservedEnemyMemory:
    def __init__(self, max_records=512):
        self.max_records = max_records
        self._scope = None
        self._memory = None
        self._sample = 0
        self._gap = False
        self._metadata = {}
        self._discarded = 0
        self._last_revision = None

    def pause(self):
        # A rejected read/curtain is not an empty enemy observation.
        self._gap = True

    def export_state(self):
        """Serialize player notes only; no native object or hidden identity survives."""
        if self._scope is None:
            return None
        records = []
        for sighting in self._memory.memory.values():
            record = sighting.to_planner_dict()
            record['lastSeenSampleId'] = record.pop('lastSeenObservationRevision')
            record.update(self._metadata[sighting.sighting_id])
            records.append(record)
        return copy.deepcopy({'schema': 'sdk-enemy-memory-state-1', 'scope': list(self._scope),
                              'sampleId': self._sample, 'lastRevision': self._last_revision,
                              'nextSightingId': self._memory._next_id,
                              'discardedRecordCount': self._discarded, 'records': records})

    def restore_state(self, value):
        """Restore validated notes atomically, always breaking observation continuity."""
        if value is None:
            return
        required = {'schema', 'scope', 'sampleId', 'lastRevision', 'nextSightingId',
                    'discardedRecordCount', 'records'}
        if not isinstance(value, dict) or set(value) != required or value['schema'] != 'sdk-enemy-memory-state-1':
            raise ValueError('invalid enemy-memory state schema')
        scope = value['scope']
        if (not isinstance(scope, list) or len(scope) != 3 or not isinstance(scope[0], str)
                or not scope[0] or not _int(scope[1], 1) or scope[1] >= 255
                or not isinstance(scope[2], str) or not scope[2]):
            raise ValueError('invalid enemy-memory scope')
        sample, revision, next_id = value['sampleId'], value['lastRevision'], value['nextSightingId']
        if (not _int(sample, 1) or not _int(revision) or not _int(next_id, 1)
                or not _int(value['discardedRecordCount']) or not isinstance(value['records'], list)):
            raise ValueError('invalid enemy-memory counters')
        memory = EnemyMemory(scope[0], scope[1])
        metadata = {}
        fields = {'sightingId', 'owner', 'unitType', 'hp', 'lastSeenPosition', 'lastSeenTurn',
                  'lastSeenSampleId', 'firstSeenTurn', 'status', 'observationLost', 'association',
                  'candidateOf', 'associationEvidence', 'identityCertainty',
                  'lastSeenObservationRevision', 'lastSeenStateVersion'}
        for record in value['records']:
            if not isinstance(record, dict) or set(record) not in (fields, fields | {'publicInfo'}):
                raise ValueError('invalid enemy-memory record fields')
            panel = public_unit_info(record.get('publicInfo'), persisted=True)
            sid, pos = record['sightingId'], record['lastSeenPosition']
            if (not _int(sid, 1) or sid in memory.memory or sid >= next_id
                    or not _int(record['owner']) or record['owner'] == scope[1]
                    or not isinstance(record['unitType'], str) or not record['unitType']
                    or not _int(record['hp'], nullable=True)
                    or not isinstance(pos, list) or len(pos) != 2 or not all(_int(v) for v in pos)
                    or not _int(record['lastSeenTurn'], nullable=True)
                    or not _int(record['firstSeenTurn'], nullable=True)
                    or not _int(record['lastSeenSampleId'], 1) or record['lastSeenSampleId'] > sample
                    or not _int(record['lastSeenObservationRevision']) or record['lastSeenObservationRevision'] > revision
                    or not _int(record['lastSeenStateVersion'])
                    or record['status'] not in (STATUS_VISIBLE, STATUS_UNOBSERVED, STATUS_DESTROYED)
                    or type(record['observationLost']) is not bool
                    or record['association'] not in ('new', 'candidate', 'assumed_continuation')
                    or not _int(record['candidateOf'], 1, nullable=True)
                    or record['candidateOf'] is not None and record['candidateOf'] >= sid
                    or record['identityCertainty'] != ('unlinked' if record['association'] == 'new' else 'assumed')
                    or record['associationEvidence'] not in (None, 'assumed_contiguous_samples',
                                                            'legally_associated_destruction_event')):
                raise ValueError('invalid enemy-memory record values')
            sighting = Sighting(sid, record['owner'], record['unitType'], record['hp'], tuple(pos),
                                record['lastSeenTurn'], record['lastSeenSampleId'],
                                record['association'], record['candidateOf'], public_info=panel)
            sighting.first_seen_turn = record['firstSeenTurn']
            # A persisted visible record is historical after a process boundary.
            sighting.freeze()
            if record['status'] == STATUS_DESTROYED:
                sighting.status, sighting.observation_lost = STATUS_DESTROYED, False
                sighting.last_continuity_evidence = record['associationEvidence']
            memory.memory[sid] = sighting
            metadata[sid] = {key: record[key] for key in ('lastSeenObservationRevision', 'lastSeenStateVersion')}
        memory._next_id = next_id
        memory.last_applied_revision = sample
        # Commit only after every record has passed validation. No live sightings
        # are restored, and the next accepted observation cannot assume continuity.
        self._scope, self._memory, self._metadata = tuple(scope), memory, metadata
        self._sample, self._last_revision = sample, revision
        self._discarded, self._gap = value['discardedRecordCount'], True

    def update(self, world, binding):
        try:
            observation = normalize_world(world)
        except (ObservationRejected, ValueError, TypeError) as exc:
            self.pause()
            return {'schema': 'observed-enemy-memory-1', 'status': 'unavailable',
                    'records': None, 'reason': str(exc)}
        scope = (binding['episodeId'], binding['playerId'], binding['streamId'])
        revision = observation['observationRevision']
        if self._scope == scope and self._last_revision is not None and revision < self._last_revision:
            self.pause()
            return {'schema': 'observed-enemy-memory-1', 'status': 'unavailable',
                    'records': None, 'reason': 'observation revision moved backwards'}
        if self._scope != scope:
            self._scope = scope
            self._memory = EnemyMemory(observation['matchId'], observation['controlledPlayerId'])
            self._sample, self._metadata, self._discarded = 0, {}, 0
            self._gap = False
        # Native observation revisions also count action/menu/bookend reads. Use
        # a separate accepted-sample ordinal for the existing sighting matcher.
        self._sample += 2 if self._gap else 1
        self._gap = False
        observation['observationRevision'] = self._sample
        self._memory.observe(observation)
        self._last_revision = revision
        for sighting in self._memory.current_visible:
            self._metadata[sighting.sighting_id] = {'lastSeenObservationRevision': revision,
                                                   'lastSeenStateVersion': binding['stateVersion']}
        # Bound old notes without dropping currently visible sightings.
        for sighting_id in sorted(self._memory.memory):
            if len(self._memory.memory) <= self.max_records:
                break
            if self._memory.memory[sighting_id].status != STATUS_VISIBLE:
                del self._memory.memory[sighting_id]
                self._metadata.pop(sighting_id, None)
                self._discarded += 1
        records = []
        for record in self._memory.enemy_memory():
            record['lastSeenSampleId'] = record.pop('lastSeenObservationRevision')
            record.update(self._metadata[record['sightingId']])
            records.append(record)
        return {'schema': 'observed-enemy-memory-1', 'status': 'observed',
                'binding': copy.deepcopy(binding), 'sampleId': self._sample, 'records': records,
                'historyTruncated': self._discarded > 0, 'discardedRecordCount': self._discarded,
                'authority': 'accepted coherent SDK observations only; session-local player notes; HP is native raw HP',
                'identitySemantics': 'sightingId is a local note, not a native unit id; continuity is assumed, never confirmed',
                'absenceSemantics': 'unobserved means not seen in this accepted sample, not dead or still at the remembered position'}
