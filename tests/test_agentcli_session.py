"""CLI session recovery against isolated SDK instances; no game connection."""
import contextlib
import copy
import io
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from agentcli import main
from sdk import PolytopiaSDK, SDKError


class Transport:
    def __init__(self):
        self.calls = []
        self.action_error = None

    def request(self, command, timeout=None, **args):
        self.calls.append((command, args))
        if command == "handoff_status":
            return {"ok": True, "ready": True, "phase": "map_ready"}
        if command == "popup_info":
            return {"ok": True, "showing": False}
        if command == "world":
            return {"ok": True, "controlledIdentityVerified": True, "episodeId": "ep-cli",
                    "localPlayerId": 2, "currentPlayerId": 2, "myTurn": True, "canAct": True,
                    "stars": "10", "state": {"streamId": "stream-cli", "stateVersion": 1},
                    "researchableTechs": {"status": "ok", "items": [{"tech": "TestTech", "cost": 3}]},
                    "trainOptions": {"cities": []}, "legalMoveTargets": {"units": []},
                    "legalAttackTargets": {"units": []}}
        if command in {"action", "unit_action"}:
            if self.action_error:
                raise self.action_error
            return {"ok": True, "accepted": True, "settlementTicket": 17}
        if command == "settlement":
            return {"ok": True, "ticketId": int(args["ticket"]), "episodeId": "ep-cli",
                    "controlledPlayerId": "2", "streamId": "stream-cli", "beforeStateVersion": 1,
                    "settlementStatus": "complete", "settlementComplete": True,
                    "effectConfirmation": "confirmed", "actionCompleted": True}
        if command == "city_options":
            return {"ok": True, "controlledPlayerId": 2, "tileOptions": [], "pendingCityReward": {"choices": []}}
        if command == "unit_actions":
            return {"ok": True, "units": []}
        raise AssertionError("unexpected native request: " + command)


class CLIFakeSessionTests(unittest.TestCase):
    def test_diagnostic_commands_and_audit_overrides_never_construct_the_sdk(self):
        def forbidden_factory(**args):
            self.fail('diagnostic input reached the SDK or transport')
        for args in (['players'], ['call'], ['get'], ['set'], ['popup', 'components'],
                     ['observe', '--audit', '1'], ['observe', '--cmd', 'players']):
            with self.subTest(args=args), contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as caught:
                    main(['--player', '2', *args], sdk_factory=forbidden_factory)
                self.assertEqual(caught.exception.code, 2)

    def invoke(self, argv, factory):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = main(argv, sdk_factory=factory)
        return code, json.loads(out.getvalue())

    def test_legacy_factory_receives_no_session_argument_or_context_calls(self):
        seen = []
        class LegacySDK:
            def observe(self, *, details):
                seen.append(("observe", details))
                return {"ok": True, "status": "observed"}
        def factory(*, player_id, episode_id, request_timeout, log_path):
            seen.append(("factory", player_id, episode_id, request_timeout, log_path))
            return LegacySDK()
        code, row = self.invoke(["--player", "2", "observe"], factory)
        self.assertEqual(code, 0)
        self.assertEqual(row["status"], "observed")
        self.assertEqual(seen, [("factory", 2, None, 20, None), ("observe", False)])

    def test_session_context_covers_resume_and_releases_after_call(self):
        seen = []
        class SessionSDK:
            def __enter__(self):
                seen.append("enter")
                return self
            def __exit__(self, *exc):
                seen.append(("exit", exc[0]))
            def resume_pending(self, *, timeout):
                seen.append(("resume-original", timeout))
                return {"ok": True, "status": "confirmed", "ticket": 17, "retryAllowed": False}
        def factory(**kwargs):
            seen.append(kwargs)
            return SessionSDK()
        code, row = self.invoke(["--player", "2", "--session", "state.json", "resume", "--timeout", "0"], factory)
        self.assertEqual(code, 0)
        self.assertEqual(row["ticket"], 17)
        self.assertEqual(seen[0]["session_path"], "state.json")
        self.assertEqual(seen[1:], ["enter", ("resume-original", 0), ("exit", None)])

    def test_resume_requires_session_before_constructing_sdk(self):
        def forbidden(**kwargs):
            raise AssertionError("SDK must not be constructed")
        code, row = self.invoke(["--player", "2", "resume"], forbidden)
        self.assertEqual(code, 3)
        self.assertEqual(row["errorCode"], "SESSION_REQUIRED")

    def test_context_released_on_sdk_error_and_malformed_input(self):
        exits = []
        class SessionSDK:
            unresolved = {"ticket": 17}
            def __enter__(self): return self
            def __exit__(self, *exc):
                exits.append(exc[0])
                self.unresolved = None
            def observe(self, **kwargs): raise SDKError("TEST_FAILURE", "failed read")
        factory = lambda **kwargs: SessionSDK()
        code, row = self.invoke(["--player", "2", "--session", "state.json", "observe"], factory)
        self.assertEqual((code, row["errorCode"]), (3, "TEST_FAILURE"))
        self.assertEqual(row["session"]["pendingReceipt"]["ticket"], 17)
        with tempfile.TemporaryDirectory() as tmp:
            bad = Path(tmp) / "bad.json"
            for value in ("{", "[]", '{"actions":{}}', '{"actions":[42]}'):
                bad.write_text(value, encoding="utf-8")
                code, row = self.invoke(["--player", "2", "--session", "state.json", "execute", str(bad), "x"], factory)
                self.assertEqual((code, row["errorCode"]), (3, "INVALID_INPUT"))
        self.assertEqual(exits, [SDKError, json.JSONDecodeError, ValueError, ValueError, ValueError])

    def test_transport_timeout_is_unknown_and_releases_session_context(self):
        exits = []
        class SessionSDK:
            def __enter__(self): return self
            def __exit__(self, *exc): exits.append(exc[0])
            def observe(self, **kwargs): raise TimeoutError("read timed out")
        code, row = self.invoke(["--player", "2", "--session", "state.json", "observe"],
                                lambda **kwargs: SessionSDK())
        self.assertEqual((code, row["status"], row["errorCode"]), (5, "unknown", "TRANSPORT_UNAVAILABLE"))
        self.assertIs(row["retryAllowed"], False)
        self.assertEqual(exits, [TimeoutError])


class CLIRealSessionTests(unittest.TestCase):
    def setUp(self):
        # Windows app-container TEMP has different rename permissions. Exercise
        # atomic state replacement in the explicitly writable project workspace.
        output = ROOT / "repairs" / "20261012" / "session-recovery"
        output.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=output)
        self.addCleanup(self.temp.cleanup)
        self.session = Path(self.temp.name) / "session.json"
        self.discovery = Path(self.temp.name) / "discovery.json"
        self.transport = Transport()
        self.instances = []

    def factory(self, **kwargs):
        sdk = PolytopiaSDK(self.transport, **kwargs)
        self.instances.append(sdk)
        return sdk

    def invoke(self, command, *, player=2, episode=None):
        argv = ["--player", str(player), "--session", str(self.session)]
        if episode:
            argv.extend(["--episode", episode])
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = main(argv + command, sdk_factory=self.factory)
        return code, json.loads(out.getvalue())

    def proposal(self):
        code, discovery = self.invoke(["list_actions", "--scope", "core"])
        self.assertEqual(code, 0)
        action = next(a for a in discovery["actions"] if a["args"].get("name") == "research")
        self.discovery.write_text(json.dumps(discovery), encoding="utf-8")
        return action

    def execute(self, action):
        return self.invoke(["execute", str(self.discovery), action["actionId"]])

    def writes(self):
        return [c for c, _ in self.transport.calls if c in {"action", "unit_action"}]

    def test_accepted_observe_keeps_pending_resume_original_then_unlock(self):
        action = self.proposal()
        code, accepted = self.execute(action)
        self.assertEqual((code, accepted["status"], accepted["ticket"]), (6, "accepted", 17))
        code, observed = self.invoke(["observe"])
        self.assertEqual((code, observed["status"]), (0, "observed"))
        self.assertEqual(observed["session"]["pendingReceipt"]["ticket"], 17)
        self.assertIn("original ticket", observed["session"]["nextStep"])
        self.assertEqual(json.loads(self.session.read_text(encoding="utf-8"))["unresolved"]["ticket"], 17)
        code, rejected = self.execute(action)
        self.assertEqual((code, rejected["errorCode"]), (3, "UNRESOLVED_ACTION"))
        before = len(self.transport.calls)
        code, resumed = self.invoke(["resume", "--timeout", "0"])
        self.assertEqual((code, resumed["status"], resumed["ticket"]), (0, "confirmed", 17))
        self.assertEqual(self.transport.calls[before:], [("settlement", {"ticket": "17"})])
        self.assertIsNone(json.loads(self.session.read_text(encoding="utf-8"))["unresolved"])
        self.assertEqual(self.writes(), ["action"])
        # The process lock is released after every CLI call; a later writer can
        # now make a fresh decision without replaying the old uncertain request.
        code, accepted_again = self.execute(action)
        self.assertEqual((code, accepted_again["status"]), (6, "accepted"))
        self.assertEqual(self.writes(), ["action", "action"])
        self.assertEqual(len({id(s) for s in self.instances}), len(self.instances))

    def test_uncertain_without_ticket_cannot_auto_resume_or_resubmit(self):
        action = self.proposal()
        self.transport.action_error = TimeoutError("reply lost after submission")
        code, uncertain = self.execute(action)
        self.assertEqual((code, uncertain["status"]), (5, "unknown"))
        before = copy.deepcopy(self.transport.calls)
        code, resumed = self.invoke(["resume", "--timeout", "0"])
        self.assertEqual((code, resumed["status"], resumed["errorCode"]), (5, "unknown", "RECOVERY_REQUIRED"))
        self.assertIs(resumed["retryAllowed"], False)
        self.assertIn("no ticket", resumed["session"]["nextStep"])
        self.assertEqual(self.transport.calls, before)
        code, rejected = self.execute(action)
        self.assertEqual((code, rejected["errorCode"]), (3, "UNRESOLVED_ACTION"))
        self.assertEqual(self.writes(), ["action"])
        self.assertIsNotNone(json.loads(self.session.read_text(encoding="utf-8"))["unresolved"])

    def test_session_seat_and_episode_mismatch_leave_file_and_transport_unchanged(self):
        self.proposal()
        original = self.session.read_bytes()
        self.transport.calls.clear()
        for kwargs in ({"player": 1}, {"episode": "other-episode"}):
            code, row = self.invoke(["observe"], **kwargs)
            self.assertEqual((code, row["errorCode"]), (3, "SESSION_SCOPE_MISMATCH"))
            self.assertEqual(self.session.read_bytes(), original)
            self.assertEqual(self.transport.calls, [])
        self.assertEqual(self.invoke(["observe"])[0], 0)

    def test_corrupt_session_is_not_overwritten_and_lock_is_released(self):
        for bad in ("{", "[]", '{"schema":"wrong"}'):
            self.session.write_text(bad, encoding="utf-8")
            before = self.session.read_bytes()
            code, row = self.invoke(["observe"])
            self.assertEqual((code, row["errorCode"]), (3, "SESSION_INVALID"))
            self.assertEqual(self.session.read_bytes(), before)
            self.assertEqual(self.transport.calls, [])
        self.session.unlink()
        self.assertEqual(self.invoke(["observe"])[0], 0)

    def test_cross_process_lock_rejects_cli_before_any_native_request(self):
        script = """import sys
sys.path.insert(0, sys.argv[1])
from sdk import PolytopiaSDK
with PolytopiaSDK(object(), player_id=2, session_path=sys.argv[2]):
    print('locked', flush=True)
    sys.stdin.readline()
"""
        holder = subprocess.Popen([sys.executable, "-c", script, str(ROOT), str(self.session)],
                                  stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            ready = holder.stdout.readline().strip()
            if ready != "locked":
                self.fail("session lock holder failed: " + holder.stderr.read())
            code, row = self.invoke(["observe"])
            self.assertEqual((code, row["errorCode"]), (3, "SESSION_LOCKED"))
            self.assertEqual(self.transport.calls, [])
        finally:
            holder.communicate("\n", timeout=10)
        self.assertEqual(holder.returncode, 0)
        self.assertEqual(self.invoke(["observe"])[0], 0)


if __name__ == "__main__":
    unittest.main()
