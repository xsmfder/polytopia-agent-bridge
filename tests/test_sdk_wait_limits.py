"""Invalid deadlines must fail before a callback, ticket restore or polling."""
import contextlib
import copy
import io
import json
import unittest

from test_sdk import Fake
from sdk import PolytopiaSDK, SDKError
from agentcli import main

BAD_TIMEOUTS = [float('nan'), float('inf'), -float('inf'), -1, True, False, '1', None, 10 ** 1000]
BAD_INTERVALS = BAD_TIMEOUTS + [0]
BINDING = {'episodeId': 'ep-one', 'playerId': 2, 'streamId': 'stream-one', 'stateVersion': 1}


class WaitLimitTests(unittest.TestCase):
    def setUp(self):
        self.f = Fake()
        self.s = PolytopiaSDK(self.f, player_id=2)

    def test_execute_rejects_bad_timeout_before_discovery_or_write(self):
        proposal = next(a for a in self.s.list_actions()['actions'] if a['args'].get('name') == 'research')
        self.f.calls.clear()
        for wait in [False, True]:
            for value in BAD_TIMEOUTS:
                with self.subTest(wait=wait, value=repr(value)):
                    with self.assertRaises(SDKError) as error:
                        self.s.execute(proposal, wait=wait, timeout=value)
                    self.assertEqual(error.exception.code, 'INVALID_ARGUMENT')
                    self.assertIsNone(self.s.unresolved)
        self.assertEqual(self.f.calls, [])

    def test_wait_rejects_bad_limits_without_restoring_or_clearing_ticket(self):
        for field, values in [('timeout', BAD_TIMEOUTS), ('interval', BAD_INTERVALS)]:
            for value in values:
                with self.subTest(field=field, value=repr(value)):
                    with self.assertRaises(SDKError) as error:
                        self.s.wait_result(8, binding=BINDING, **{field: value})
                    self.assertEqual(error.exception.code, 'INVALID_ARGUMENT')
                    self.assertIsNone(self.s.unresolved)
        original = {'ticket': 8, 'binding': copy.deepcopy(BINDING)}
        self.s.unresolved = copy.deepcopy(original)
        self.s._resolution_required = True
        with self.assertRaises(SDKError):
            self.s.wait_result(8, timeout=float('nan'))
        self.assertEqual(self.s.unresolved, original)
        self.assertTrue(self.s._resolution_required)
        self.assertEqual(self.f.calls, [])

    def test_handoff_bad_limits_fail_before_any_poll_or_start(self):
        self.f.handlers['handoff_status'] = {'ok': True, 'phase': 'human_turn'}
        for field, values in [('timeout', BAD_TIMEOUTS), ('interval', BAD_INTERVALS)]:
            for value in values:
                with self.subTest(field=field, value=repr(value)):
                    with self.assertRaises(SDKError):
                        self.s.watch_handoff(return_on_human=True, **{field: value})
        self.assertEqual(self.f.calls, [])

    def test_request_timeout_must_be_finite_positive_before_transport(self):
        for value in BAD_TIMEOUTS + [0]:
            with self.subTest(value=repr(value)):
                with self.assertRaises(SDKError):
                    PolytopiaSDK(self.f, player_id=2, request_timeout=value)
        self.assertEqual(self.f.calls, [])

    def test_zero_timeout_queries_original_ticket_once_and_keeps_pending(self):
        self.f.handlers['settlement']['settlementStatus'] = 'pending'
        result = self.s.wait_result(8, binding=BINDING, timeout=0)
        self.assertEqual(result['status'], 'timeout')
        self.assertFalse(result['retryAllowed'])
        self.assertEqual(self.s.unresolved, {'ticket': 8, 'binding': BINDING})
        self.assertEqual([c for c, _ in self.f.calls], ['settlement'])

    def test_valid_finite_deadline_sleeps_within_remaining_budget(self):
        now = [0.0]
        sleeps = []
        def sleep(seconds):
            sleeps.append(seconds)
            now[0] += seconds
        self.s.clock, self.s.sleep = lambda: now[0], sleep
        self.f.handlers['settlement']['settlementStatus'] = 'pending'
        result = self.s.wait_result(8, binding=BINDING, timeout=0.3, interval=0.2)
        self.assertEqual(result['status'], 'timeout')
        self.assertEqual(len(sleeps), 2)
        self.assertAlmostEqual(sum(sleeps), 0.3)
        self.assertEqual([c for c, _ in self.f.calls], ['settlement'] * 3)

    def test_cli_rejects_nan_handoff_and_infinite_transport_timeout(self):
        for argv in [['--player', '2', 'watch_handoff', '--timeout', 'nan'],
                     ['--player', '2', '--request-timeout', 'inf', 'observe']]:
            with self.subTest(argv=argv), contextlib.redirect_stdout(io.StringIO()) as output:
                code = main(argv, sdk_factory=lambda **kw: PolytopiaSDK(self.f, **kw))
            self.assertEqual(code, 3)
            self.assertEqual(json.loads(output.getvalue())['errorCode'], 'INVALID_ARGUMENT')
        self.assertEqual(self.f.calls, [])


if __name__ == '__main__':
    unittest.main()
