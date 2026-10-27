"""Player-facing Polytopia SDK. One decision writer; no desktop automation.

Native rows are retained, including nulls and incomplete discovery. Action descriptors
are proposals tied to a visible stream/version, not an idempotency guarantee.
"""
from __future__ import annotations

import hashlib
import copy
import json
import math
import time
import uuid
from functools import wraps
from pathlib import Path
from typing import Any

from sdk_values import integer, coordinates, native_identifier, native_rule_identifier
from sdk_candidates import current_rows

VERSION = "polytopia-sdk-1"


class SDKError(RuntimeError):
    def __init__(self, code: str, message: str, **details: Any):
        super().__init__(message)
        self.code, self.details = code, details

    def as_dict(self):
        return {"schema": VERSION, "ok": False, "status": "rejected",
                "errorCode": self.code, "error": str(self), **self.details}


def _hash(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False,
                                    separators=(",", ":")).encode()).hexdigest()


def _integer(value):
    try:
        return integer(value)
    except (TypeError, ValueError):
        return None


def _action_not_dispatched(payload):
    """Only consistent native refusal evidence permits dropping an action intent."""
    if (not isinstance(payload, dict) or payload.get("accepted") is True
            or payload.get("dispatched") is True
            or payload.get("outcome") not in (None, "not_dispatched")):
        return False
    return (payload.get("accepted") is False or payload.get("dispatched") is False
            or payload.get("outcome") == "not_dispatched")


def _unit_argument(value):
    parsed = _integer(value)
    if parsed is None or not 0 <= parsed <= 4294967295:
        raise SDKError("INVALID_ARGUMENT", "unit must be a native uint unit id")
    return str(parsed)


def _coordinate_argument(value):
    try:
        xy = coordinates(value)
    except ValueError:
        raise SDKError("INVALID_ARGUMENT", "at must be an Int32 coordinate such as (2,3)")
    return "(" + ",".join(str(c) for c in xy) + ")"


def _finite_seconds(value, *, positive=False):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return False
    try:
        return math.isfinite(value) and (value > 0 if positive else value >= 0)
    except OverflowError:
        return False


def _logged(method):
    @wraps(method)
    def wrapper(self, *args, **kwargs):
        self._ensure_open()
        try:
            result = method(self, *args, **kwargs)
        except Exception as failure:
            if self.log_path is not None:
                entry = {"time": time.time(), "method": method.__name__, "args": args,
                         "kwargs": kwargs, "exception": {"type": type(failure).__name__, "message": str(failure)},
                         "unresolved": self.unresolved, "setupUnresolved": self._setup_unresolved}
                if isinstance(failure, SDKError):
                    entry["exception"]["errorCode"] = failure.code
                    entry["exception"]["details"] = failure.details
                try:
                    with self.log_path.open("a", encoding="utf-8") as fh:
                        fh.write(json.dumps(entry, ensure_ascii=False) + "\n")
                except (OSError, TypeError, ValueError):
                    pass  # Preserve the original exception and its receipt.
            try:
                self._save_state()
            except SDKError as checkpoint_error:
                details = checkpoint_error.as_dict()
                if isinstance(failure, SDKError):
                    failure.details["stateSaveError"] = details
                else:
                    failure.stateSaveError = details
            raise
        try:
            self._save_state()
        except SDKError as checkpoint_error:
            # Preserve a known native receipt if its final checkpoint fails. The
            # durable write-ahead intent and in-memory failure gate remain held.
            result["stateSaveError"] = checkpoint_error.as_dict()
            result["retryAllowed"] = False
        if self.log_path is not None:
            entry = {"time": time.time(), "method": method.__name__, "args": args,
                     "kwargs": kwargs, "response": result}
            try:
                with self.log_path.open("a", encoding="utf-8") as fh:
                    fh.write(json.dumps(entry, ensure_ascii=False) + "\n")
            except (OSError, TypeError, ValueError) as exc:
                # Logging failure must not obscure an already submitted action.
                result["logError"] = str(exc)
        return result
    return wrapper


from sdk_lifecycle import GameLifecycleMixin


class PolytopiaSDK(GameLifecycleMixin):
    def __init__(self, transport=None, *, player_id: int, episode_id=None,
                 request_timeout=20.0, clock=time.monotonic, sleep=time.sleep, log_path=None,
                 session_path=None):
        if _integer(player_id) is None or not 0 < int(player_id) < 255:
            raise SDKError("INVALID_ARGUMENT", "player_id must identify a real player")
        if not _finite_seconds(request_timeout, positive=True):
            raise SDKError("INVALID_ARGUMENT", "request_timeout must be finite and positive")
        if transport is None:
            from bridge_transport import Bridge  # lazy: no desktop modules
            transport = Bridge(timeout=request_timeout)
        self.transport = transport
        self.player_id = int(player_id)
        self.episode_id = episode_id
        self.request_timeout = request_timeout
        self.clock, self.sleep = clock, sleep
        self.stream_id = None
        self.unresolved = None
        self._setup_unresolved = None
        self._resolution_required = False
        from sdk_memory import ObservedEnemyMemory
        self._enemy_memory = ObservedEnemyMemory()
        self._session_store = None
        self._session_closed = False
        self._pending_writes = []
        self._saved_state_json = None
        self._state_save_failed = False
        self.log_path = Path(log_path) if log_path is not None else None
        if (session_path is not None and self.log_path is not None
                and Path(session_path).resolve() == self.log_path.resolve()):
            raise SDKError("INVALID_ARGUMENT", "session_path and log_path must be different files")
        if self.log_path is not None:
            self.log_path.parent.mkdir(parents=True, exist_ok=True)
        if session_path is not None:
            from sdk_state import SessionStore, SessionStateError
            try:
                self._session_store = SessionStore(session_path)
                state = self._session_store.load()
                if state is not None:
                    self._restore_session(state, episode_id)
                self._save_state()
            except (SessionStateError, SDKError, ValueError, TypeError) as exc:
                if self._session_store is not None:
                    self._session_store.close()
                if isinstance(exc, SDKError):
                    raise
                raise SDKError(getattr(exc, "code", "SESSION_INVALID"), str(exc)) from exc

    def _restore_session(self, state, requested_episode):
        required = {"playerId", "episodeId", "streamId", "unresolved", "resolutionRequired",
                    "setupUnresolved", "pendingWrites", "enemyMemory"}
        if (not required.issubset(state) or isinstance(state["playerId"], bool)
                or not isinstance(state["playerId"], int)
                or not isinstance(state["resolutionRequired"], bool)
                or not isinstance(state["pendingWrites"], list)
                or any(state[key] is not None and not isinstance(state[key], dict)
                       for key in ("unresolved", "setupUnresolved", "enemyMemory"))
                or any(state[key] is not None and (not isinstance(state[key], str) or not state[key])
                       for key in ("episodeId", "streamId"))):
            raise SDKError("SESSION_INVALID", "invalid persisted SDK state")
        if (state["playerId"] != self.player_id
                or requested_episode is not None and state["episodeId"] != requested_episode):
            raise SDKError("SESSION_SCOPE_MISMATCH", "session belongs to another seat or episode")
        for pending in state["pendingWrites"]:
            if (not isinstance(pending, dict) or pending.get("kind") not in ("action", "setup", "ui")
                    or not isinstance(pending.get("id"), str) or not pending["id"]
                    or pending.get("state") not in ("uncertain", "receipt")
                    or not isinstance(pending.get("command"), str)
                    or not isinstance(pending.get("args"), dict)
                    or pending.get("ticket") is not None and (_integer(pending["ticket"]) is None or int(pending["ticket"]) <= 0)):
                raise SDKError("SESSION_INVALID", "invalid persisted write intent")
            if pending["kind"] == "action":
                self._validate_session_binding(pending.get("binding"), state)
        if state["unresolved"] is not None:
            self._validate_session_binding(state["unresolved"].get("binding"), state)
        setup = state["setupUnresolved"]
        if setup is not None and (not isinstance(setup.get("setupBinding"), dict)
                or not isinstance(setup["setupBinding"].get("setupSessionId"), str)
                or not setup["setupBinding"]["setupSessionId"]
                or _integer(setup["setupBinding"].get("menuRevision")) is None
                or int(setup["setupBinding"]["menuRevision"]) <= 0
                or not isinstance(setup.get("actionId"), str)):
            raise SDKError("SESSION_INVALID", "invalid persisted setup receipt")
        memory = state["enemyMemory"]
        if memory is not None:
            if memory.get("scope") != [state["episodeId"], self.player_id, state["streamId"]]:
                raise SDKError("SESSION_SCOPE_MISMATCH", "enemy notes belong to another session scope")
            restore = getattr(self._enemy_memory, "restore_state", None)
            if restore is None:
                raise SDKError("SESSION_INVALID", "enemy memory restoration is unavailable")
            restore(memory)
        self.episode_id, self.stream_id = state["episodeId"], state["streamId"]
        self.unresolved = copy.deepcopy(state["unresolved"])
        self._resolution_required = state["resolutionRequired"]
        self._setup_unresolved = copy.deepcopy(setup)
        self._pending_writes = copy.deepcopy(state["pendingWrites"])

    def _validate_session_binding(self, binding, state):
        if (not isinstance(binding, dict) or binding.get("playerId") != self.player_id
                or binding.get("episodeId") != state["episodeId"]
                or binding.get("streamId") != state["streamId"]
                or not binding.get("episodeId") or not binding.get("streamId")
                or _integer(binding.get("stateVersion")) is None or int(binding["stateVersion"]) < 0):
            raise SDKError("SESSION_INVALID", "persisted action binding does not match session")

    def _ensure_open(self):
        if self._session_closed:
            raise SDKError("SESSION_CLOSED", "SDK session is closed")

    def _save_state(self):
        if self._session_store is None:
            return
        self._ensure_open()
        from sdk_state import SCHEMA, SessionStateError
        export = getattr(self._enemy_memory, "export_state", None)
        state = {"schema": SCHEMA, "playerId": self.player_id, "episodeId": self.episode_id,
                 "streamId": self.stream_id, "unresolved": self.unresolved,
                 "resolutionRequired": self._resolution_required, "setupUnresolved": self._setup_unresolved,
                 "pendingWrites": self._pending_writes, "enemyMemory": export() if export else None}
        try:
            serialized = json.dumps(state, ensure_ascii=False, sort_keys=True, allow_nan=False)
            if serialized == self._saved_state_json:
                self._state_save_failed = False
                return
            self._session_store.save(state)
            self._saved_state_json = serialized
            self._state_save_failed = False
        except SessionStateError as exc:
            self._state_save_failed = True
            raise SDKError(exc.code, str(exc), retryAllowed=False,
                           pendingReceipt=copy.deepcopy(self.unresolved),
                           setupReceipt=copy.deepcopy(self._setup_unresolved),
                           pendingWrites=copy.deepcopy(self._pending_writes)) from exc

    def close(self):
        if self._session_closed:
            return
        try:
            self._save_state()
        finally:
            if self._session_store is not None:
                self._session_store.close()
            self._session_closed = True

    def __enter__(self):
        self._ensure_open()
        return self

    def __exit__(self, exc_type, exc, traceback):
        try:
            self.close()
        except SDKError as checkpoint_error:
            if exc is None:
                raise
            if isinstance(exc, SDKError):
                exc.details["stateSaveError"] = checkpoint_error.as_dict()
            else:
                exc.stateSaveError = checkpoint_error.as_dict()

    def _resolves_pending_choice(self, command, args):
        # Explicit responses to current native offers, not new turns or unrelated
        # diplomacy. Discovery/revalidation still proves the actual offer before
        # dispatch. Both entry points share the same durable child-write gate.
        return self._resolution_required and (
            command == "action" and args.get("name") == "city_reward"
            or command == "tribe_action" and args.get("operation") == "peace_response"
            and args.get("accept") in ("false", "true"))

    def _begin_write(self, command, args, original_binding=None):
        gameplay = command in {"action", "unit_action", "tile_action", "tribe_action"}
        if self._state_save_failed:
            raise SDKError("SESSION_IO_ERROR", "recover the failed state checkpoint before another write",
                           retryAllowed=False, pendingWrites=copy.deepcopy(self._pending_writes))
        resolving = self._resolves_pending_choice(command, args)
        popup_resolution = command == "popup" and args.get("op") in {"choose", "dismiss_resolved_reward", "next"}
        uncertain = [row for row in self._pending_writes if row["state"] == "uncertain"]
        child_pending = any(row.get("parentTicket") is not None for row in self._pending_writes)
        if uncertain:
            raise SDKError("RECOVERY_REQUIRED", "an earlier write has no conclusive receipt; do not replay it",
                           pendingWrites=copy.deepcopy(uncertain), retryAllowed=False)
        setup_popup = False
        if popup_resolution and self._setup_unresolved is not None:
            pending = self._setup_unresolved
            binding = pending["setupBinding"]
            # A newly created game may show its ordinary intro before game_ready.
            # The public popup path already verifies the controlled world and UI.
            # Keep the creation receipt; only its one received continue request
            # may coexist with this explicit popup response, never an unknown write.
            setup_popup = (pending.get("creationSubmitted") is True and pending.get("actionId") == "continue"
                and len(self._pending_writes) == 1 and self._pending_writes[0].get("kind") == "setup"
                and self._pending_writes[0].get("command") == "setup_action"
                and self._pending_writes[0].get("state") == "receipt"
                and self._pending_writes[0]["args"].get("actionId") == "continue"
                and self._pending_writes[0]["args"].get("setupSessionId") == binding["setupSessionId"]
                and _integer(self._pending_writes[0]["args"].get("menuRevision")) == binding["menuRevision"])
        if self._setup_unresolved is not None and (command != "setup_action" or self._pending_writes):
            if not setup_popup:
                raise SDKError("SETUP_PENDING", "query the original setup action before another write")
        if self.unresolved is not None and _integer(self.unresolved.get("ticket")) is None:
            raise SDKError("RECOVERY_REQUIRED", "original action has no ticket; no write may bypass recovery",
                           receipt=copy.deepcopy(self.unresolved), retryAllowed=False)
        if self.unresolved is not None or self._pending_writes:
            if not popup_resolution and not (resolving and not child_pending):
                raise SDKError("UNRESOLVED_ACTION", "query original receipts before another write",
                               receipt=copy.deepcopy(self.unresolved), retryAllowed=False)
        kind = "action" if gameplay else "setup" if command == "setup_action" else "ui"
        intent = {"id": uuid.uuid4().hex, "kind": kind, "command": command,
                  "args": copy.deepcopy(args), "state": "uncertain", "ticket": None}
        if gameplay:
            intent["binding"] = copy.deepcopy(original_binding) if original_binding is not None else {
                "episodeId": args.get("expectedEpisodeId"), "playerId": self.player_id,
                "streamId": args.get("expectedStreamId"), "stateVersion": int(args["expectedStateVersion"])}
            if resolving:
                intent["parentTicket"] = (self.unresolved or {}).get("ticket")
        elif kind == "setup":
            # Commit the receipt only after all write gates accept this request,
            # together with its intent and before checkpointing or transport I/O.
            self._setup_unresolved = {
                "setupBinding": {"setupSessionId": args["setupSessionId"], "menuRevision": int(args["menuRevision"])},
                "actionId": args["actionId"], "creationSubmitted": args["actionId"] == "continue"}
        self._pending_writes.append(intent)
        self._save_state()  # With session_path, durable intent precedes the transport write.
        return intent

    @staticmethod
    def _popup_callback_completed(response):
        return (any(response.get(key) is True for key in
                    ("invokedCallback", "invokedOnHide", "invokedHide", "invokedOnRewardButtonClicked", "queueAdvanceRequested"))
                or response.get("dismissRequested") is True and response.get("rewardResubmitted") is False)

    def _ui_write_status(self, response, completed=False):
        # Share the public status and intent-release decision. A false dispatch flag
        # cannot overrule evidence that a callback may already have run.
        if ((response.get("episodeId") is not None and response["episodeId"] != self.episode_id)
                or (response.get("controlledPlayerId") is not None
                    and _integer(response["controlledPlayerId"]) != self.player_id)
                or (response.get("streamId") is not None and response["streamId"] != self.stream_id)):
            return "unknown"
        attempted = any(response.get(key) is True for key in (
            "callbackAttempted", "callbackCompleted", "callbackSubmitted", "invokedCallback",
            "invokedOnHide", "invokedHide", "invokedOnRewardButtonClicked", "dismissRequested",
            "viewEffectConfirmed", "accepted", "dispatched"))
        refused = (any(response.get(key) is False for key in
                       ("callbackAttempted", "callbackSubmitted", "dispatched"))
                   or response.get("outcome") == "not_dispatched")
        if refused and not attempted and response.get("outcome") in (None, "not_dispatched"):
            return "rejected"
        uncertain = (response.get("status") in ("unknown", "pending", "not_confirmed")
            or response.get("outcome") == "unknown" or response.get("error")
            or any(response.get(key) for key in
                   ("callbackError", "onHideError", "hideError", "onRewardButtonClickedError", "invocationError")))
        return "submitted" if response.get("ok") is True and completed and not refused and not uncertain else "unknown"

    @staticmethod
    def _setup_write_status(response, args):
        # Native pre-callback errors omit binding fields. If a field is present,
        # it must still refer to this request before either success or refusal.
        for key in ("setupSessionId", "menuRevision", "actionId"):
            if key in response:
                actual, expected = response[key], args[key]
                if key == "menuRevision":
                    actual, expected = _integer(actual), _integer(expected)
                if actual != expected:
                    return "unknown"
        if response.get("callbackSubmitted") is False:
            attempted = any(response.get(key) is True for key in
                            ("callbackAttempted", "callbackCompleted", "dispatched", "accepted", "settingVerified"))
            return ("rejected" if not attempted and response.get("ok") is False
                    and response.get("status") == "rejected"
                    and response.get("outcome") in (None, "not_dispatched") else "unknown")
        if (response.get("callbackSubmitted") is not True
                or not all(key in response for key in ("setupSessionId", "menuRevision", "actionId"))
                or any(response.get(key) is False for key in ("callbackAttempted", "dispatched", "accepted"))
                or response.get("outcome") not in (None, "invoked")
                or response.get("error") or response.get("callbackError")):
            return "unknown"
        status = response.get("status")
        if status == "not_confirmed":
            return status
        if response.get("ok") is True and (status == "submitted"
                or status == "configured" and response.get("settingVerified") is True):
            return status
        return "unknown"

    def _finish_write(self, intent, response):
        if intent is None:
            return
        if intent["command"] == "game_lifecycle":
            self._finish_lifecycle_write(intent, response)
            self._save_state()
            return
        payload = response.get("nativeExecution", response)
        if not isinstance(payload, dict):
            self._save_state()
            return
        terminal = False
        if intent["kind"] == "action":
            ticket = _integer(payload.get("settlementTicket"))
            if ticket is not None and ticket > 0:
                intent.update(ticket=ticket, state="receipt")
            elif _action_not_dispatched(payload):
                # The native pipeline gate can refuse before allocating a ticket.
                # Its explicit outcome is conclusive; a generic error/rejection
                # without dispatch evidence must retain the uncertain intent.
                terminal = True
        elif intent["kind"] == "setup":
            status = self._setup_write_status(response, intent["args"])
            terminal = status in {"configured", "rejected"}
            if status in {"submitted", "not_confirmed"}:
                intent["state"] = "receipt"
        else:
            positive = False
            if intent["command"] in {"tribe_info", "leaderboard"} and any(key in response for key in
                    ("viewRequestId", "viewOperation", "callbackCompleted")):
                positive = (self._view_matches(intent, response)
                    and (response.get("callbackCompleted") is True and response.get("outcome") == "invoked"
                         and response.get("status") in {"opening", "closing", "closed"}
                         or self._view_effect_confirmed(intent, response)))
            elif intent["command"] in {"leaderboard", "tribe_info"}:
                positive = response.get("status") == ("opening" if intent["args"].get("mode") == "open" else "closed")
            elif intent["command"] == "popup":
                positive = self._popup_callback_completed(response)
            elif intent["command"] == "handoff_start":
                positive = response.get("accepted") is True and response.get("callbackSubmitted") is True
            terminal = self._ui_write_status(response, positive) in {"submitted", "rejected"}
        if terminal:
            self._pending_writes.remove(intent)
        try:
            self._save_state()
        except SDKError as failure:
            failure.details["native"] = copy.deepcopy(response)
            raise

    def _request(self, command, *, _intent_binding=None, **args):
        self._ensure_open()
        mutating = (command in {"action", "unit_action", "tile_action", "tribe_action", "setup_action", "handoff_start", "game_lifecycle"}
                    or command == "popup" and args.get("op") in {"choose", "dismiss_resolved_reward", "next"}
                    or command in {"tribe_info", "leaderboard"} and args.get("mode", "read") != "read")
        view_write = command in {"tribe_info", "leaderboard"} and args.get("mode", "read") != "read"
        if view_write and "viewRequestId" in args:
            raise SDKError("INVALID_ARGUMENT", "viewRequestId is assigned by the SDK write intent")
        intent = self._begin_write(command, args, _intent_binding) if mutating else None
        wire_args = dict(args)
        if view_write:
            wire_args["viewRequestId"] = intent["id"] if intent is not None else uuid.uuid4().hex
        response = self.transport.request(command, timeout=self.request_timeout, **wire_args)
        if not isinstance(response, dict):
            raise SDKError("INVALID_RESPONSE", "bridge response must be an object")
        self._finish_write(intent, response)
        return response

    @_logged
    def load_game(self, *, mode="singleplayer", game_id=None, wait=False, timeout=30):
        """Load a native local save into a fresh session; does not choose a hotseat turn."""
        if mode not in {"singleplayer", "hotseat"}:
            raise SDKError("INVALID_ARGUMENT", "mode must be singleplayer or hotseat")
        return self._game_operation("load_" + mode, game_id=game_id, wait=wait, timeout=timeout)

    @_logged
    def saved_games(self, *, wait=False, timeout=30):
        """Explicitly open the native local hotseat list; returns its displayed ids/labels."""
        return self._game_operation("open_hotseat", wait=wait, timeout=timeout)

    @_logged
    def save_game(self, *, wait=False, timeout=30):
        """Save the current controlled local match with native validation enabled."""
        return self._game_operation("save", wait=wait, timeout=timeout)

    @_logged
    def return_to_menu(self, *, wait=False, timeout=30):
        """Save first, then return to the native menu; does not terminate the game."""
        return self._game_operation("return_to_menu", wait=wait, timeout=timeout)

    @_logged
    def quit_game(self, *, wait=False, timeout=30):
        """Save an active controlled match, return to menu, then request normal Quit."""
        return self._game_operation("quit", wait=wait, timeout=timeout)

    @_logged
    def wait_lifecycle(self, receipt, *, timeout=30):
        """Query an original load/save/exit operation; never submit it again."""
        return self._wait_game_operation(receipt, timeout=timeout)

    def _checked(self, command, **args):
        r = self._request(command, **args)
        if r.get("ok") is False or r.get("error"):
            raise SDKError(r.get("errorCode", "READ_UNAVAILABLE"),
                           r.get("error", r.get("reason", "native read unavailable")), native=r)
        return r

    @_logged
    def setup_status(self):
        """Read normal single-player menus; never sample an existing match's world."""
        native = self._request("setup_status")
        phase = native.get("phase")
        pending = copy.deepcopy(self._setup_unresolved)
        if pending and native.get("setupSessionId") != pending["setupBinding"]["setupSessionId"]:
            return self._envelope("unknown", native, errorCode="SETUP_SESSION_CHANGED",
                                  setupReceipt=pending, retryAllowed=False, **pending)
        status = ("confirmed" if phase == "game_ready" and native.get("ready") is True and native.get("ok") is True
                  else "needs_resolution" if phase == "needs_resolution"
                  else "submitted" if phase == "loading"
                  else "observed" if native.get("ok") is True else "unavailable")
        if status == "confirmed":
            if (_integer(native.get("configuredPlayerId")) != self.player_id or self.player_id != 1
                    or native.get("creationSubmitted") is not True
                    or not isinstance(native.get("episodeId"), str) or not native["episodeId"]):
                raise SDKError("SETUP_IDENTITY_UNVERIFIED", "created game lacks the intended identity", native=native)
            if self.episode_id is not None and self.episode_id != native["episodeId"]:
                raise SDKError("EPISODE_CHANGED", "use a fresh SDK for a new match", native=native)
            self.episode_id = native["episodeId"]
            self._setup_unresolved = None
            self._pending_writes = [row for row in self._pending_writes if row["kind"] != "setup"]
        elif (pending and not pending.get("creationSubmitted")
              and phase not in {"loading", "unavailable"}):
            revision = _integer(native.get("menuRevision"))
            if revision is not None and revision > pending["setupBinding"]["menuRevision"]:
                # Memory-only and durable sessions use the same native completion
                # evidence and release the receipt and write intent together.
                binding = pending["setupBinding"]
                last = native.get("lastAction")
                matches = [row for row in self._pending_writes
                           if row.get("kind") == "setup" and row.get("command") == "setup_action"
                           and row["args"].get("actionId") == pending["actionId"]
                           and row["args"].get("setupSessionId") == binding["setupSessionId"]
                           and _integer(row["args"].get("menuRevision")) == binding["menuRevision"]]
                expected = dict(binding, actionId=pending["actionId"])
                if ((len(matches) == 1 or not self._pending_writes)
                        and status == "observed" and native.get("optionsComplete") is True
                        and native.get("creationSubmitted") is False
                        and _integer(native.get("configuredPlayerId")) == self.player_id == 1
                        and isinstance(last, dict) and self._setup_write_status(last, expected) == "submitted"):
                    self._setup_unresolved = None
                    if matches:
                        self._pending_writes.remove(matches[0])
        response = self._envelope(status, native, retryAllowed=False, setupReceipt=pending, **(pending or {}))
        response["ok"] = native.get("ok") is True and status != "unavailable"
        return response

    @_logged
    def setup_action(self, action_id, *, expected_setup, wait=False, timeout=30.0):
        """Invoke one discovered menu action. Match creation is explicit administration."""
        if not _finite_seconds(timeout):
            raise SDKError("INVALID_ARGUMENT", "finite nonnegative setup timeout required")
        if self.player_id != 1:
            raise SDKError("SINGLEPLAYER_SEAT_ONE_REQUIRED", "configure seat 1 and use a seat-1 SDK before creating a solo game")
        if self.episode_id is not None or self.unresolved is not None or self._resolution_required:
            raise SDKError("ACTIVE_SDK_PRESERVED", "use a fresh SDK after normally saving and exiting the active match")
        if self._setup_unresolved is not None:
            raise SDKError("SETUP_PENDING", "read setup_status or wait_setup for the original action; do not resend",
                           setupReceipt=copy.deepcopy(self._setup_unresolved))
        native = expected_setup.get("native", expected_setup) if isinstance(expected_setup, dict) else None
        if (not isinstance(native, dict) or native.get("ok") is not True or native.get("schema") != "singleplayer-setup-1"
                or not isinstance(native.get("setupSessionId"), str) or not native["setupSessionId"]
                or _integer(native.get("menuRevision")) is None or int(native["menuRevision"]) <= 0
                or _integer(native.get("configuredPlayerId")) != 1 or not isinstance(action_id, str)):
            raise SDKError("INVALID_SETUP_CONTEXT", "provide a current setup_status with configured seat 1")
        if not isinstance(native.get("actions"), list):
            raise SDKError("INVALID_SETUP_CONTEXT", "native actions must be a list")
        rows = [a for a in native["actions"] if isinstance(a, dict) and a.get("actionId") == action_id]
        if len(rows) != 1 or rows[0].get("executable") is not True or rows[0].get("enabled") is not True:
            raise SDKError("SETUP_ACTION_UNAVAILABLE", "action must be a unique executable native menu candidate")
        binding = {"setupSessionId": native["setupSessionId"], "menuRevision": int(native["menuRevision"])}
        receipt = {"setupBinding": binding, "actionId": action_id, "creationSubmitted": action_id == "continue"}
        try:
            reply = self._request("setup_action", actionId=action_id, setupSessionId=binding["setupSessionId"],
                                  menuRevision=str(binding["menuRevision"]))
        except (TimeoutError, RuntimeError) as exc:
            if isinstance(exc, SDKError) and exc.code != "INVALID_RESPONSE":
                raise
            return self._envelope("unknown", None, **receipt, error=str(exc), retryAllowed=False)
        status = self._setup_write_status(reply, dict(binding, actionId=action_id))
        if status in {"configured", "rejected"}:
            self._setup_unresolved = None
        result = self._envelope(status, reply, **receipt, retryAllowed=False)
        if wait and self._setup_unresolved is not None and status == "submitted":
            return self.wait_setup(result, timeout=timeout)
        return result

    @_logged
    def wait_setup(self, receipt, *, timeout=30.0, interval=0.25):
        """Poll only; a timeout or lost response never replays menu callbacks."""
        binding = receipt.get("setupBinding") if isinstance(receipt, dict) else None
        if (not _finite_seconds(timeout) or not _finite_seconds(interval, positive=True)
                or not isinstance(binding, dict) or not isinstance(binding.get("setupSessionId"), str)
                or not binding["setupSessionId"] or _integer(binding.get("menuRevision")) is None
                or int(binding["menuRevision"]) <= 0 or not isinstance(receipt.get("actionId"), str)):
            raise SDKError("INVALID_SETUP_RECEIPT", "original setup binding, action and valid polling limits required")
        pending = {"setupBinding": {"setupSessionId": binding["setupSessionId"], "menuRevision": int(binding["menuRevision"])},
                   "actionId": receipt["actionId"], "creationSubmitted": receipt["actionId"] == "continue"}
        if self._setup_unresolved is not None and self._setup_unresolved != pending:
            raise SDKError("SETUP_RECEIPT_MISMATCH", "another setup action is unresolved")
        self._setup_unresolved = pending
        deadline = self.clock() + timeout
        while True:
            try:
                state = self.setup_status()
            except (TimeoutError, RuntimeError) as exc:
                if isinstance(exc, SDKError):
                    raise
                return self._envelope("unknown", None, **pending, error=str(exc), retryAllowed=False)
            if self._setup_unresolved is None or state["status"] in {"needs_resolution", "unavailable", "unknown"}:
                return state
            if self.clock() >= deadline:
                return self._envelope("timeout", state["native"], **pending, retryAllowed=False)
            self.sleep(min(interval, max(0, deadline - self.clock())))

    def _bind(self, world):
        if world.get("controlledIdentityVerified") is not True:
            raise SDKError("IDENTITY_UNVERIFIED", "controlled player identity is not verified")
        if _integer(world.get("localPlayerId")) != self.player_id:
            raise SDKError("SEAT_CHANGED", "observation belongs to another player")
        episode = world.get("episodeId")
        if not episode:
            raise SDKError("EPISODE_UNKNOWN", "episode is unavailable")
        if self.episode_id is not None and self.episode_id != episode:
            raise SDKError("EPISODE_CHANGED", "create a new SDK for the intended match")
        self.episode_id = episode
        stream = (world.get("state") or {}).get("streamId")
        if self.stream_id is not None and self.stream_id != stream:
            raise SDKError("STREAM_CHANGED", "event stream changed; rebind explicitly")
        self.stream_id = stream

    def _world(self):
        world = self._checked("world")
        self._bind(world)
        return world

    def _binding(self, world):
        return {"episodeId": self.episode_id, "playerId": self.player_id,
                "streamId": self.stream_id,
                "stateVersion": (world.get("state") or {}).get("stateVersion")}

    def _handoff_decision_visible(self, status):
        # Native needs_decision is reached only after the curtain is inactive,
        # the controlled identity is verified and private sampling is allowed.
        return (status.get("ok") is True and status.get("phase") == "needs_decision"
                and status.get("needsDecision") is True
                and status.get("overlayActive") is False
                and _integer(status.get("configuredPlayerId")) == self.player_id
                and _integer(status.get("targetPlayerId")) == self.player_id)

    @_logged
    def observe(self, *, details=False):
        try:
            return self._observe_once(details=details)
        except SDKError as exc:
            self._enemy_memory.pause()
            if exc.code != "STATE_CHANGED":
                raise
            # Discard the mixed read and preserve the original pending write.
            # Only an explicit new read is retryable; no game action is re-sent.
            return {"schema": VERSION, "ok": False, "status": "state_changed",
                    "errorCode": exc.code, "error": str(exc), "world": None,
                    "retryAllowed": False, "retryObservationAllowed": True,
                    "pendingReceipt": copy.deepcopy(self.unresolved),
                    "recovery": "query the original ticket if pending; request a fresh observation before planning"}
        except (TimeoutError, RuntimeError):
            self._enemy_memory.pause()
            raise

    def _observe_once(self, *, details=False):
        handoff = self._request("handoff_status")
        decision_visible = self._handoff_decision_visible(handoff)
        if handoff.get("ok") is True and handoff.get("phase") != "not_hotseat" and handoff.get("ready") is not True and not decision_visible:
            # Public handoff metadata only: do not sample a darkened seat's world.
            self._enemy_memory.pause()
            return {"schema": VERSION, "ok": True, "status": "handoff",
                    "handoff": handoff, "world": None}
        world = self._world()
        result = {"schema": VERSION, "ok": True, "status": "needs_resolution" if decision_visible else "observed",
                  "binding": self._binding(world), "world": world,
                  "popup": self._checked("popup_info")}
        if decision_visible:
            result["handoff"] = handoff
        if details:
            result["cities"] = self._checked("city_options")
            result["relations"] = self._checked("tribe_relations")
            # Passive cached/native observations only. Opening the statistics UI
            # remains an explicit leaderboard("open") operation.
            result["leaderboard"] = self._public_display_read("leaderboard", mode="read")
            result["notifications"] = self._public_display_read("notifications", after="0", limit="100")
        # Popup sampling is a separate native request even in the basic view.
        # Do not combine an old map with a newly advanced popup or turn.
        self._same_version(world, self._world())
        result["enemyMemory"] = self._enemy_memory.update(world, result["binding"])
        return result

    def _public_display_read(self, command, *, _allow_legacy_anonymous=False, **args):
        native = self._request(command, **args)
        episode, seat = native.get("episodeId"), _integer(native.get("controlledPlayerId"))
        if episode is not None and episode != self.episode_id or seat is not None and seat != self.player_id:
            raise SDKError("PUBLIC_DISPLAY_SCOPE_CHANGED", "public display belongs to another game or seat", native=native)
        available = native.get("ok") is True and native.get("status") not in {None, "unavailable"}
        legacy_anonymous = _allow_legacy_anonymous and not any(key in native for key in
            ("viewRequestId", "viewOperation", "callbackCompleted", "viewEffectConfirmed"))
        if available and (episode is None or seat is None) and not legacy_anonymous:
            raise SDKError("PUBLIC_DISPLAY_SCOPE_UNKNOWN", "public display lacks episode/seat identity", native=native)
        if command == "leaderboard":
            # Rejected passive reads (including owned turn recap) have no view
            # attempt. Older bridges use "" as well as null for absent metadata.
            # Any real attempt, stream or effect still requires its matching scope.
            navigation_scope = (available or any(native.get(key) not in (None, "") for key in
                ("viewRequestId", "viewOperation", "streamId")) or native.get("viewEffectConfirmed") is True)
            if "viewRequestId" in native and navigation_scope and native.get("streamId") != self.stream_id:
                raise SDKError("PUBLIC_DISPLAY_SCOPE_CHANGED", "leaderboard navigation belongs to another event stream", native=native)
            self._reconcile_view(native, command)
        return native

    def _same_version(self, before, after):
        a, b = self._binding(before), self._binding(after)
        if a != b or before.get("currentPlayerId") != after.get("currentPlayerId"):
            raise SDKError("STATE_CHANGED", "visible state changed during discovery; observe again")

    def _descriptor(self, binding, scope, command, args, native, eligible):
        proposal = {"binding": binding, "scope": scope, "command": command, "args": args}
        return {"actionId": _hash(proposal)[:24], **proposal,
                "eligible": eligible, "native": native}

    def _scoped_query(self, command, schema, *, args=None, ready=False, view_action=False, native_episode=True):
        """Main-thread context guard plus bookend observations; retain original native payload."""
        observation = self.observe()
        if observation.get("world") is None:
            return observation
        before = observation["world"]
        if before.get("myTurn") is not True:
            raise SDKError("READ_NOT_READY", "query requires the controlled player's visible turn")
        if ready and (observation.get("status") != "observed" or before.get("canAct") is not True):
            raise SDKError("PREVIEW_NOT_READY", "resolve pending game actions or choices first")
        binding = observation["binding"]
        version = _integer(binding.get("stateVersion"))
        if not isinstance(binding.get("streamId"), str) or not binding["streamId"] or version is None or version <= 0:
            raise SDKError("VERSION_UNKNOWN", "query requires a visible stream and positive state version")
        try:
            native = self._request(command, **(args or {}), expectedEpisodeId=binding["episodeId"],
                expectedPlayerId=str(self.player_id), expectedStreamId=binding["streamId"], expectedStateVersion=str(version))
            if native.get("errorCode") in {"STALE_ACTION_CONTEXT", "PREVIEW_STATE_CHANGED"}:
                raise SDKError("STATE_CHANGED", "state changed before query; observe again")
            after = self._world()
            if view_action:
                # Explicit information-view open/close may change popup state/version.
                # Game, seat, stream and turn must still match; reads require exact version.
                final = self._binding(after)
                final_version = _integer(final.get("stateVersion"))
                if (any(final[k] != binding[k] for k in ("episodeId", "playerId", "streamId"))
                        or final_version is None or final_version < version
                        or before.get("currentPlayerId") != after.get("currentPlayerId")):
                    raise SDKError("STATE_CHANGED", "view scope changed during operation")
            else:
                self._same_version(before, after)
        except SDKError as exc:
            if exc.code != "STATE_CHANGED":
                raise
            result = self._envelope("state_changed", None, errorCode=exc.code, retryAllowed=False,
                retryObservationAllowed=True, pendingReceipt=copy.deepcopy(self.unresolved),
                viewOperationAttempted=view_action)
            result["ok"] = False
            return result
        except (TimeoutError, RuntimeError) as exc:
            return self._envelope("unknown", None, error=str(exc), binding=binding, retryAllowed=False,
                pendingReceipt=copy.deepcopy(self.unresolved), viewOperationAttempted=view_action)
        status = native.get("status")
        unavailable = native.get("ok") is False or status in {"unavailable", "unavailable_before_terminal"}
        if (native.get("episodeId") is not None and native["episodeId"] != binding["episodeId"]
                or native.get("controlledPlayerId") is not None and _integer(native["controlledPlayerId"]) != self.player_id
                or native.get("streamId") is not None and native["streamId"] != binding["streamId"]
                or native.get("stateVersion") is not None and _integer(native["stateVersion"]) != version):
            raise SDKError("READ_SCOPE_CHANGED", "native result belongs to another scope or version")
        if not unavailable:
            if native.get("schema") != schema:
                raise SDKError("INVALID_RESPONSE", "unexpected native query schema")
            if native_episode and native.get("episodeId") != binding["episodeId"]:
                raise SDKError("READ_SCOPE_UNKNOWN", "native result lacks its required episode")
            if status not in {None, "ok", "partial", "provisional", "opening", "closing", "closed"}:
                raise SDKError("INVALID_RESPONSE", "unexpected native query status")
            if native.get("ok") is not True and not (command == "composite_preview" and status == "partial"):
                raise SDKError("INVALID_RESPONSE", "native result has no successful read evidence")
        result = self._envelope(status if status in {"partial", "provisional", "opening", "closing", "closed", "unavailable_before_terminal"}
            or command == "tribe_info" and status == "unknown"
            else "unavailable" if unavailable else "observed", native, binding=binding, retryAllowed=False,
            bindingAuthority="native main-thread expected context and SDK world observations before/after; not a native payload stamp")
        result["ok"] = not unavailable
        if view_action:
            result["afterBinding"] = self._binding(after)
        return result

    @_logged
    def game_info(self):
        """Current native game settings and already disclosed players; no save creation/resume."""
        return self._scoped_query("game_info", "game-info-1")

    @_logged
    def unit_properties(self):
        """Properties of own and currently visible units; native unknowns remain unknown."""
        return self._scoped_query("unit_properties", "unit-properties-1")

    @_logged
    def score_snapshot(self):
        """Native current score projection; Domination rating remains unavailable before terminal."""
        return self._scoped_query("score_snapshot", "score-snapshot-1")

    @_logged
    def threat_map(self):
        """Visible enemy properties and stationary range geometry, never hidden enemy pathfinding."""
        return self._scoped_query("threat_map", "visible-threat-1")

    @_logged
    def composite_preview(self, *, unit, at, kind="move"):
        """Native legal own-unit move path and partial destination effects; no action submitted."""
        if kind != "move":
            raise SDKError("INVALID_ARGUMENT", "native uint unit and kind=move required; use attack_preview for attacks")
        parsed, coord = _unit_argument(unit), _coordinate_argument(at)
        return self._scoped_query("composite_preview", "move-composite-preview-1", ready=True, native_episode=False,
            args={"unit": parsed, "at": coord, "kind": kind})

    @_logged
    def tribe_info(self, opponent, *, mode="read"):
        """Explicit native information view lifecycle; only read is passive, default never opens UI."""
        other = _integer(opponent)
        if other is None or not 0 < other < 255 or other == self.player_id or mode not in {"open", "read", "close"}:
            raise SDKError("INVALID_ARGUMENT", "known other player and mode open/read/close required")
        result = self._scoped_query("tribe_info", "tribe-info-1", args={"opponent": str(other), "mode": mode},
            ready=mode == "open", view_action=mode != "read", native_episode=mode != "close")
        native = result.get("native")
        if native is not None and result.get("ok") is True:
            if ((mode != "close" or "viewRequestId" in native)
                    and _integer(native.get("playerId")) != other):
                raise SDKError("READ_TARGET_CHANGED", "information view belongs to another opponent")
            if mode == "read":
                self._reconcile_view(native, "tribe_info", other)
        return result

    def _view_matches(self, intent, native):
        args = intent["args"]
        command = intent["command"]
        schema = {"tribe_info": "tribe-info-1", "leaderboard": "leaderboard-1"}.get(command)
        return (schema is not None and native.get("schema") == schema
            and native.get("viewRequestId") == intent["id"]
            and native.get("viewOperation") == args.get("mode")
            and native.get("episodeId") == args.get("expectedEpisodeId") == self.episode_id
            and _integer(native.get("controlledPlayerId")) == self.player_id
            and (command != "tribe_info" or _integer(native.get("playerId")) == _integer(args.get("opponent")))
            and (command != "leaderboard" or native.get("streamId") == self.stream_id)
            and args.get("expectedStreamId") == self.stream_id)

    def _reconcile_view(self, native, command, opponent=None):
        # Only passive, positively rendered evidence can resolve a lost UI reply.
        # World snapshots and another view's callbacks are never substitutes.
        if native.get("ok") is not True or native.get("viewEffectConfirmed") is not True:
            return
        for intent in list(self._pending_writes):
            if (intent.get("kind") != "ui" or intent.get("command") != command
                    or intent.get("state") != "uncertain"
                    or command == "tribe_info" and _integer(intent["args"].get("opponent")) != opponent
                    or not self._view_matches(intent, native)):
                continue
            if self._view_effect_confirmed(intent, native):
                self._pending_writes.remove(intent)

    @staticmethod
    def _view_effect_confirmed(intent, native):
        mode, status = intent["args"].get("mode"), native.get("status")
        return (native.get("viewEffectConfirmed") is True
            and (mode == "open" and (status == "ok" or intent["command"] == "tribe_info" and status is None)
                 or mode == "close" and status == "closed"))

    @_logged
    def attack_preview(self, *, unit=None, at=None):
        """Current legal-target prediction, in native raw HP; never submits an action."""
        args = {}
        if unit is not None:
            args["unit"] = _unit_argument(unit)
        if at is not None:
            args["at"] = _coordinate_argument(at)
        observation = self.observe()
        if observation.get("world") is None or observation.get("status") != "observed":
            return observation
        world = observation["world"]
        if world.get("myTurn") is not True or world.get("canAct") is not True:
            raise SDKError("PREVIEW_NOT_READY", "attack preview requires a settled controlled turn")
        binding = observation["binding"]
        if not binding["streamId"] or _integer(binding["stateVersion"]) is None:
            raise SDKError("VERSION_UNKNOWN", "attack preview requires a visible state version")
        native = self._request("attack_preview", **args,
            expectedEpisodeId=binding["episodeId"], expectedPlayerId=str(self.player_id),
            expectedStreamId=binding["streamId"], expectedStateVersion=str(binding["stateVersion"]))
        if native.get("errorCode") and native.get("status") != "unavailable":
            raise SDKError(native["errorCode"], native.get("error", "native attack preview unavailable"), native=native)
        status = native.get("status", "unavailable")
        if status not in {"ok", "partial", "unavailable"}:
            raise SDKError("INVALID_RESPONSE", "unknown attack preview status", native=native)
        if status != "unavailable" and (
            native.get("episodeId") != binding["episodeId"]
            or _integer(native.get("controlledPlayerId")) != self.player_id
            or native.get("streamId") != binding["streamId"]
            or _integer(native.get("stateVersion")) != binding["stateVersion"]):
            raise SDKError("PREVIEW_SCOPE_CHANGED", "preview does not match the observed binding", native=native)
        result = self._envelope("observed" if status == "ok" else status, native, binding=binding)
        result["ok"] = native.get("ok") is True and status != "unavailable"
        return result

    @_logged
    def list_actions(self, scope="core", *, unit=None, at=None):
        return self._discover_actions(scope, unit=unit, at=at)

    @staticmethod
    def _action_domain(command, args):
        # Route existing SDK adapters, not native action names. Future unit/tile
        # actions still come from the same dynamic menus without a skill whitelist.
        if command == "action":
            name = args.get("name")
            if name in {"research", "train", "move", "attack", "end_turn"}:
                return "world"
            if name in {"build", "city_reward"}:
                return "city"
        return {"unit_action": "unit", "tile_action": "tile", "tribe_action": "tribe"}.get(command)

    def _discover_actions(self, scope, *, unit=None, at=None, selected=None):
        if not isinstance(scope, str) or scope not in {"core", "all", "unit", "tile", "city", "tribe"}:
            raise SDKError("INVALID_ARGUMENT", "scope must be core/all/unit/tile/city/tribe")
        domains = {"core": {"world", "city", "unit"},
                   "all": {"world", "city", "unit", "tile", "tribe"}}.get(scope, {scope})
        if selected is not None:
            domain = self._action_domain(selected["command"], selected["args"])
            if domain not in domains:
                raise SDKError("ACTION_NOT_AVAILABLE", "action adapter does not belong to the discovered scope")
            domains = {domain}
            # A descriptor from scope=all/core keeps its original scope and ID,
            # while native revalidation only reads the relevant unit or tile.
            unit = selected["args"].get("unit") if domain == "unit" else None
            at = selected["args"].get("at") if domain == "tile" else None
        # Validate caller selectors before the first world/menu query, including
        # selectors unused by the selected scope. No invalid input reaches native.
        if unit is not None:
            unit = _unit_argument(unit)
        if at is not None:
            at = _coordinate_argument(at)
        if scope == "tile" and at is None:
            raise SDKError("INVALID_ARGUMENT", "tile scope requires at")
        world = self._world()
        if world.get("myTurn") is not True:
            raise SDKError("NOT_CONTROLLED_TURN", "private menus require the controlled player's turn")
        binding = self._binding(world)
        if binding["stateVersion"] is None or not binding["streamId"]:
            raise SDKError("VERSION_UNKNOWN", "action discovery requires a visible state version")
        if selected is not None and binding != selected["binding"]:
            raise SDKError("STALE_ACTION", "action belongs to another episode/seat/stream/version")
        actions, sources = [], {}

        def add(command, args, row, eligible, source_scope=scope):
            if selected is not None and (command != selected["command"] or args != selected["args"]):
                return
            actions.append(self._descriptor(binding, source_scope, command, args, row, eligible))

        def affordable(row):
            stock, cost = _integer(world.get("stars")), _integer(row.get("cost"))
            if world.get("canAct") is not True:
                return world.get("canAct")
            return stock >= cost if stock is not None and cost is not None else None

        def native_eligible(row):
            bits = (row.get("executable"), row.get("nativeValid"), world.get("canAct"))
            return False if False in bits else True if all(b is True for b in bits) else None

        if "world" in domains:
            sources["research"] = world.get("researchableTechs")
            for row in current_rows(world.get("researchableTechs"), "items"):
                if native_rule_identifier(row.get("tech")) is not None:
                    add("action", {"name": "research", "kind": row["tech"]}, row, affordable(row))
            sources["train"] = world.get("trainOptions")
            seen_train = set()
            for source in ("cities", "sites"):
                for site in current_rows(world.get("trainOptions"), source):
                    if source == "sites" and (site.get("explored") is not True
                            or _integer(site.get("owner")) != self.player_id
                            or site.get("siteKind") not in {"city", "owned-water", "owned-algae"}):
                        continue
                    for row in current_rows(site, "options"):
                        if source == "sites" and row.get("nativeValid") is not True:
                            continue
                        if site.get("coords") and native_rule_identifier(row.get("unitType")) is not None:
                            key = (site["coords"], row["unitType"])
                            if key in seen_train:
                                continue
                            seen_train.add(key)
                            eligible = affordable(row) if row.get("trainable") is True else row.get("trainable")
                            if "nativeValid" in row and row["nativeValid"] is not True:
                                eligible = False if row["nativeValid"] is False else None
                            add("action", {"name": "train", "at": site["coords"], "kind": row["unitType"]},
                                row, eligible)
            for key, name in (("legalMoveTargets", "move"), ("legalAttackTargets", "attack")):
                sources[key] = world.get(key)
                for actor in current_rows(world.get(key), "units"):
                    if 'status' in actor and actor['status'] != 'ok':
                        continue
                    for dest in actor.get("targets", []):
                        add("action", {"name": name, "from": actor["from"], "to": dest}, actor,
                            world.get("canAct"))
            # End turn is a fixed lifecycle operation, not a hardcoded unit menu.
            add("action", {"name": "end_turn"}, {"authority": "world.canAct"}, world.get("canAct"))

        if "city" in domains:
            cities = self._checked("city_options")
            if _integer(cities.get("controlledPlayerId")) != self.player_id:
                raise SDKError("SEAT_CHANGED", "city menu belongs to another player")
            sources["cities"] = cities
            for row in cities.get("tileOptions", []):
                if row.get("coords") and row.get("improvement"):
                    eligible = row.get("buildableNow") if world.get("canAct") is True else world.get("canAct")
                    add("action", {"name": "build", "at": row["coords"], "kind": row["improvement"]}, row, eligible)
            pending = cities.get("pendingCityReward") or {}
            for row in current_rows(pending, "choices", statuses=("offered", "offered-partial")):
                if row.get("reward") and pending.get("city"):
                    add("action", {"name": "city_reward", "at": pending["city"], "reward": row["reward"]},
                        row, True)

        if "unit" in domains:
            menu = self._checked("unit_actions", **({"unit": str(unit)} if unit is not None else {}))
            sources["unit"] = menu
            for actor in menu.get("units", []):
                for row in actor.get("actions") or []:
                    if row.get("actionId") and actor.get("unitId") is not None:
                        add("unit_action", {"unit": str(actor["unitId"]), "action_id": row["actionId"]}, row,
                            native_eligible(row))

        if "tile" in domains:
            coords = [at] if at is not None else [r.get("coords") for r in sources.get("cities", {}).get("ownedEconomicTiles", [])]
            for coord in sorted(set(c for c in coords if c)):
                menu = self._checked("tile_actions", at=coord)
                sources.setdefault("tiles", []).append(menu)
                for row in menu.get("actions") or []:
                    if row.get("actionId"):
                        add("tile_action", {"at": coord, "action_id": row["actionId"]}, row,
                            native_eligible(row))

        if "tribe" in domains:
            menu = self._checked("tribe_relations")
            sources["tribes"] = menu
            for tribe in menu.get("tribes", []):
                for row in tribe.get("operations", []):
                    if not row.get("actionId"):
                        continue
                    args = {"operation": row["actionId"], "opponent": str(tribe["playerId"])}
                    eligible = False if row.get("adapterSupported") is False else row.get("canExecute")
                    if row["actionId"] == "peace_response":
                        # New native discovery supplies each actual response variant.
                        # Keep compatibility with the older unparameterized response row.
                        accepts = (str(row["accept"]).lower(),) if type(row.get("accept")) is bool else ("false", "true")
                        for accept in accepts:
                            add("tribe_action", {**args, "accept": accept}, row, eligible)
                    else:
                        add("tribe_action", args, row, eligible if world.get("canAct") is True else world.get("canAct"))
        self._same_version(world, self._world())
        return {"schema": VERSION, "ok": True, "status": "discovered", "binding": binding,
                "actions": actions, "sources": sources,
                "discovery": {"scope": scope, "unit": unit, "at": at}}

    @_logged
    def _revalidate_action(self, action):
        # Retain fresh source evidence in the SDK log without a second global scan.
        return self._discover_actions(action["scope"], selected=action)

    @_logged
    def execute(self, action, *, wait=False, timeout=30.0):
        if not _finite_seconds(timeout):
            raise SDKError("INVALID_ARGUMENT", "finite nonnegative timeout required before submitting an action")
        resolving = (isinstance(action, dict) and isinstance(action.get("args"), dict)
                     and self._resolves_pending_choice(action.get("command"), action["args"]))
        if self.unresolved is not None and not resolving:
            raise SDKError("UNRESOLVED_ACTION", "query the existing receipt; do not resend an uncertain action", receipt=self.unresolved)
        if not isinstance(action, dict) or not all(k in action for k in ("actionId", "binding", "command", "args", "scope")) or not isinstance(action.get("args"), dict):
            raise SDKError("INVALID_ACTION", "execute requires a discovered action descriptor")
        scope, args = action["scope"], action["args"]
        fresh = self._revalidate_action(action)
        if fresh["binding"] != action["binding"]:
            raise SDKError("STALE_ACTION", "action belongs to another episode/seat/stream/version")
        candidate = next((a for a in fresh["actions"] if a["actionId"] == action["actionId"] and
                          a["command"] == action["command"] and a["args"] == args), None)
        if candidate is None or candidate["eligible"] is not True:
            raise SDKError("ACTION_NOT_AVAILABLE", "action is absent or not positively executable")
        binding = action["binding"]
        guards = {"expectedEpisodeId": binding["episodeId"], "expectedPlayerId": str(binding["playerId"]),
                  "expectedStreamId": binding["streamId"], "expectedStateVersion": str(binding["stateVersion"])}
        try:
            native = self._request(action["command"], _intent_binding=binding, **args, **guards)
        except (TimeoutError, RuntimeError) as exc:
            if isinstance(exc, SDKError) and exc.code != "INVALID_RESPONSE":
                raise
            receipt = self._envelope("unknown", None, binding=binding, actionId=action["actionId"],
                                     error=str(exc), retryAllowed=False)
            if not resolving:
                self.unresolved = receipt
            return receipt
        payload = native.get("nativeExecution", native)
        ticket = _integer(payload.get("settlementTicket")) if isinstance(payload, dict) else None
        if ticket is None or ticket <= 0:
            if _action_not_dispatched(payload):
                return self._envelope("rejected", native, binding=binding, ticket=None, retryAllowed=False)
            receipt = self._envelope("unknown", native, binding=binding, retryAllowed=False)
            if not resolving:
                self.unresolved = receipt
            return receipt
        if not resolving:
            self.unresolved = {"ticket": ticket, "binding": binding}
        if wait:
            return self.wait_result(ticket, timeout=timeout, binding=binding)
        return self._envelope("accepted", native, binding=binding, ticket=ticket, retryAllowed=False)

    @staticmethod
    def _envelope(status, native, **extra):
        return {"schema": VERSION, "ok": status not in {"rejected", "unknown", "timeout", "not_confirmed"},
                "status": status, "native": native, **extra}

    @_logged
    def wait_result(self, ticket, *, timeout=30.0, interval=0.25, binding=None):
        if (_integer(ticket) is None or int(ticket) <= 0 or not _finite_seconds(timeout)
                or not _finite_seconds(interval, positive=True)):
            raise SDKError("INVALID_ARGUMENT", "positive ticket and finite positive interval/nonnegative timeout required")
        ticket = int(ticket)
        binding = binding or (self.unresolved or {}).get("binding")
        if binding is None:
            raise SDKError("BINDING_REQUIRED", "provide the original receipt binding when querying a ticket")
        if (not isinstance(binding, dict) or not isinstance(binding.get("episodeId"), str)
                or not binding["episodeId"] or not isinstance(binding.get("streamId"), str)
                or not binding["streamId"] or _integer(binding.get("stateVersion")) is None
                or _integer(binding.get("playerId")) is None):
            raise SDKError("INVALID_BINDING", "receipt requires episode/player/stream/version")
        if binding.get("playerId") != self.player_id or (self.episode_id is not None and binding.get("episodeId") != self.episode_id):
            raise SDKError("RECEIPT_MISMATCH", "receipt belongs to another match or seat")
        if self.unresolved is None:
            # Reconnecting callers can restore the receipt explicitly. Child reward
            # queries must retain an existing parent receipt rather than replace it.
            self.unresolved = {"ticket": ticket, "binding": dict(binding)}
        deadline, last = self.clock() + timeout, None
        while True:
            try:
                last = self._request("settlement", ticket=str(ticket))
            except (TimeoutError, RuntimeError) as exc:
                return self._envelope("unknown", last, ticket=ticket, binding=binding, error=str(exc), retryAllowed=False)
            if last.get("streamId") is not None and last["streamId"] != binding.get("streamId"):
                raise SDKError("STREAM_CHANGED", "ticket response belongs to another event stream")
            if _integer(last.get("ticketId")) != int(ticket):
                return self._envelope("unknown", last, ticket=ticket, binding=binding, retryAllowed=False,
                                      error="native ticket is absent or does not match")
            identities = (last.get("episodeId"), _integer(last.get("controlledPlayerId")),
                          _integer(last.get("beforeStateVersion")))
            expected = (binding["episodeId"], self.player_id, _integer(binding["stateVersion"]))
            if any(value is None for value in identities):
                return self._envelope("unknown", last, ticket=ticket, binding=binding, retryAllowed=False,
                                      error="native receipt lacks episode/seat/before-version; use the integrated DLL")
            if identities != expected:
                raise SDKError("RECEIPT_MISMATCH", "native ticket does not match original episode/seat/version")
            status = last.get("settlementStatus", "unknown")
            if last.get("ok") is not True or status not in {"complete", "pending", "needs_resolution", "rejected", "unknown"}:
                status = "unknown"
            # A native End Turn can leave the controlled map before its public
            # curtain is presented. Poll only this positively identified receipt;
            # never read the next player's map or repeat the write while waiting.
            waiting_for_handoff = (status == "unknown" and last.get("ok") is True
                                   and last.get("action") in ("end_turn", "endturn")
                                   and last.get("settlementComplete") is False
                                   and last.get("actionCompleted") is False
                                   and last.get("effectConfirmation") == "unknown"
                                   and last.get("publicHandoff") is None)
            if waiting_for_handoff:
                if self.clock() >= deadline:
                    return self._envelope("timeout", last, ticket=ticket, binding=binding, retryAllowed=False)
                self.sleep(min(interval, max(0, deadline - self.clock())))
                continue
            if status == "complete":
                # Match the native public-reply contract. A label alone is not
                # completion proof, and contradictory effect flags cannot unlock
                # this original intent as a success or a known negative result.
                effect, completed = last.get("effectConfirmation"), last.get("actionCompleted")
                status = "unknown"
                if last.get("settlementComplete") is True and last.get("streamId") == binding["streamId"]:
                    if effect == "confirmed" and completed is True:
                        status = "confirmed"
                    elif effect == "not_confirmed" and completed is False:
                        status = "not_confirmed"
            if status in {"confirmed", "rejected", "not_confirmed", "unknown", "needs_resolution"}:
                if status in {"confirmed", "rejected", "not_confirmed"}:
                    self._pending_writes = [row for row in self._pending_writes
                        if not (row.get("ticket") == ticket and row.get("binding") == binding)]
                    if (self.unresolved or {}).get("ticket") == ticket:
                        self.unresolved = None
                        self._resolution_required = False
                if status == "needs_resolution" and (self.unresolved or {}).get("ticket") == ticket:
                    self._resolution_required = True
                return self._envelope(status, last, ticket=ticket, binding=binding, retryAllowed=False)
            if self.clock() >= deadline:
                return self._envelope("timeout", last, ticket=ticket, binding=binding, retryAllowed=False)
            self.sleep(min(interval, max(0, deadline - self.clock())))

    @_logged
    def acknowledge_settled_unknown(self, ticket, *, binding, accept_unknown_effect=False):
        """Release only a positively finished receipt, without confirming its effect.

        The caller explicitly accepts an unknown effect and must not repeat the write.
        Pending/timeout/no-ticket receipts remain locked. Native payload is unchanged.
        """
        if accept_unknown_effect is not True:
            raise SDKError("EXPLICIT_ACK_REQUIRED", "explicitly accept the unknown effect; never resend the action")
        if not self.unresolved or self.unresolved.get("ticket") != ticket or self.unresolved.get("binding") != binding:
            raise SDKError("RECEIPT_MISMATCH", "acknowledge only the currently unresolved receipt")
        result = self.wait_result(ticket, binding=binding, timeout=0)
        native = result.get("native") or {}
        if result.get("status") != "unknown" or not (native.get("ok") is True
                and native.get("settlementStatus") == "complete"
                and native.get("settlementComplete") is True and native.get("pipelineIdle") is True
                and native.get("effectConfirmation") == "unknown" and native.get("completionAuthority")):
            if result.get("status") in {"confirmed", "rejected", "not_confirmed"}:
                return result
            raise SDKError("ACTION_NOT_SETTLED", "native completion must be positively established", receipt=result)
        current = self._world()
        current_binding = self._binding(current)
        current_version = _integer(current_binding.get("stateVersion"))
        settled_version = _integer(native.get("stateVersion"))
        if (current_binding["episodeId"] != binding["episodeId"]
                or current_binding["streamId"] != binding["streamId"]
                or current.get("canAct") is not True
                or current_version is None or settled_version is None
                or current_version < settled_version):
            raise SDKError("ACTION_NOT_SETTLED", "controlled map is not ready after native completion")
        self.unresolved = None
        self._resolution_required = False
        self._pending_writes = [row for row in self._pending_writes
                               if not (row.get("ticket") == ticket and row.get("binding") == binding)]
        return {"schema": VERSION, "ok": False, "status": "settled_effect_unknown", "native": native,
                "ticket": ticket, "binding": binding, "retryAllowed": False, "canContinue": True,
                "effectConfirmation": "unknown", "acknowledgedUnknownEffect": True}

    @_logged
    def resume_pending(self, *, timeout=30.0):
        """Query saved original receipts only. An unidentifiable write is never replayed."""
        if not _finite_seconds(timeout):
            raise SDKError("INVALID_ARGUMENT", "finite nonnegative recovery timeout required")
        if self._session_store is None:
            raise SDKError("SESSION_REQUIRED", "resume_pending requires session_path")
        lifecycle = next((r for r in self._pending_writes if r["command"] == "game_lifecycle"), None)
        if lifecycle is not None:
            return self._wait_game_operation({"operationId": lifecycle["args"]["operationId"]}, timeout=timeout)
        if self._setup_unresolved is not None:
            return self.wait_setup(copy.deepcopy(self._setup_unresolved), timeout=timeout)
        uncertain = [row for row in self._pending_writes if row["state"] == "uncertain"]
        if uncertain or self.unresolved is not None and _integer(self.unresolved.get("ticket")) is None:
            return self._envelope("unknown", None, errorCode="RECOVERY_REQUIRED",
                error="the original write has no conclusive ticket; automatic recovery cannot identify it",
                pendingReceipt=copy.deepcopy(self.unresolved), pendingWrites=copy.deepcopy(self._pending_writes),
                retryAllowed=False)
        receipts = list(reversed(copy.deepcopy(self._pending_writes)))
        if self.unresolved is not None and not any(row.get("ticket") == self.unresolved.get("ticket") for row in receipts):
            receipts.append(copy.deepcopy(self.unresolved))
        results = []
        deadline = self.clock() + timeout
        for receipt in receipts:
            if receipt.get("kind", "action") != "action" or _integer(receipt.get("ticket")) is None:
                return self._envelope("unknown", None, errorCode="RECOVERY_REQUIRED",
                    pendingReceipt=copy.deepcopy(self.unresolved), pendingWrites=copy.deepcopy(self._pending_writes),
                    results=results, retryAllowed=False)
            result = self.wait_result(receipt["ticket"], binding=receipt["binding"],
                                      timeout=max(0, deadline - self.clock()))
            results.append(result)
            if result["status"] not in {"confirmed", "rejected", "not_confirmed"}:
                return {**result, "results": results, "pendingReceipt": copy.deepcopy(self.unresolved),
                        "pendingWrites": copy.deepcopy(self._pending_writes)}
        if not results:
            return self._envelope("observed", None, pendingReceipt=None, retryAllowed=False)
        return {**results[-1], "results": results, "pendingReceipt": copy.deepcopy(self.unresolved),
                "pendingWrites": copy.deepcopy(self._pending_writes)}

    @_logged
    def events(self, after_event_id=0, *, stream_id=None, limit=100):
        if _integer(after_event_id) is None or int(after_event_id) < 0 or _integer(limit) is None or not 1 <= int(limit) <= 256:
            raise SDKError("INVALID_ARGUMENT", "nonnegative cursor and limit 1..256 required")
        self._world()
        expected = stream_id or self.stream_id
        if expected != self.stream_id:
            raise SDKError("STREAM_CHANGED", "cursor belongs to another event stream; synchronize first")
        native = self._checked("events", afterEventId=str(after_event_id), limit=str(limit))
        if native.get("streamId") != expected:
            raise SDKError("STREAM_CHANGED", "event stream changed during read")
        return self._envelope("history_gap" if native.get("historyGap") is True else "observed", native)

    @_logged
    def leaderboard(self, mode="read"):
        if mode not in {"open", "read", "close"}:
            raise SDKError("INVALID_ARGUMENT", "leaderboard mode must be open, read, or close")
        world = self._world()
        if mode == "read":
            native = self._public_display_read("leaderboard", mode=mode, _allow_legacy_anonymous=True)
        else:
            binding = self._binding(world)
            native = self._request("leaderboard", mode=mode,
                expectedEpisodeId=binding["episodeId"], expectedPlayerId=str(self.player_id),
                expectedStreamId=binding["streamId"], expectedStateVersion=str(binding["stateVersion"]))
        if native.get("episodeId") is not None and native["episodeId"] != self.episode_id:
            raise SDKError("EPISODE_CHANGED", "leaderboard belongs to another game", native=native)
        if native.get("controlledPlayerId") is not None and _integer(native["controlledPlayerId"]) != self.player_id:
            raise SDKError("SEAT_MISMATCH", "leaderboard belongs to another seat", native=native)
        response = self._envelope(native.get("status", "unavailable"), native)
        response["ok"] = native.get("ok") is True
        return response

    @_logged
    def notifications(self, after_event_id=0, *, stream_id=None, limit=100):
        if _integer(after_event_id) is None or int(after_event_id) < 0 or _integer(limit) is None or not 1 <= int(limit) <= 128:
            raise SDKError("INVALID_ARGUMENT", "nonnegative notification cursor and limit 1..128 required")
        self._world()
        native = self._checked("notifications", after=str(after_event_id), limit=str(limit), **({"stream": stream_id} if stream_id else {}))
        if native.get("episodeId") != self.episode_id or _integer(native.get("controlledPlayerId")) != self.player_id:
            raise SDKError("NOTIFICATION_SCOPE_CHANGED", "notification history belongs to another game or seat", native=native)
        if stream_id is not None and native.get("streamId") != stream_id:
            raise SDKError("STREAM_CHANGED", "notification cursor belongs to another stream", native=native)
        return self._envelope("history_gap" if native.get("historyGap") is True else "observed" if native.get("status") == "ok" else "unavailable", native)

    @_logged
    def result(self, episode_id=None):
        # Explicit archived episode is readable at the main menu without world.
        episode = episode_id or self.episode_id
        native = self._request("result", **({"episode": episode} if episode else {}))
        owner = native.get("controlledPlayerId", (native.get("result") or {}).get("controlledPlayerId"))
        if owner is not None and _integer(owner) != self.player_id:
            raise SDKError("SEAT_MISMATCH", "result belongs to another controlled player", native=native)
        result = self._envelope(native.get("status", "unknown"), native)
        if native.get("ok") is False:
            result["ok"] = False
        return result

    @_logged
    def choose_popup(self, index, *, expected_popup):
        if _integer(index) is None:
            raise SDKError("INVALID_ARGUMENT", "index must be a native integer button choice")
        guards = self._popup_context(expected_popup, acknowledgement_index=int(index))
        try:
            native = self._request("popup", op="choose", index=str(index), **guards)
        except (TimeoutError, RuntimeError) as exc:
            if isinstance(exc, SDKError) and exc.code != "INVALID_RESPONSE":
                raise
            return self._envelope("unknown", None, error=str(exc), retryAllowed=False)
        # Callback invocation is not a game-effect confirmation.
        status = self._ui_write_status(native, self._popup_callback_completed(native))
        return self._envelope(status, native, retryAllowed=False)

    def _popup_context(self, expected_popup, *, acknowledgement_index=None):
        if not isinstance(expected_popup, dict):
            raise SDKError("INVALID_ARGUMENT", "expected_popup must be a fresh popup observation")
        world = self._world()  # ordinary canAct may be false for a pending choice
        current = self._checked("popup_info")
        if current.get("showing") is not True or _hash(current) != _hash(expected_popup):
            # reqId is transport identity, not popup identity.
            def clean(p):
                result = {k: v for k, v in p.items() if k != "reqId"}
                # IL2CPP GetComponent can expose one DiplomacyPopup through its
                # BasicPopupLegacy base wrapper on one read and its concrete
                # wrapper on the next. The native button projection already
                # identifies the concrete type. Normalize only known base
                # wrappers; retain all text/options and reject other changes.
                button_type = (p.get("buttons") or {}).get("popupType")
                if p.get("popupType") in {"PopupBase", "BasicPopup", "BasicPopupLegacy"} and button_type:
                    result["popupType"] = button_type
                return result
            observed, expected = clean(current), clean(expected_popup)
            buttons = observed.get("buttons") or {}
            rows = buttons.get("buttons") or []
            texts, prior_texts = observed.get("visibleTexts"), expected.get("visibleTexts")
            if (observed.get("popupType") == expected.get("popupType") == "IconRewardPopup"
                    and observed.get("title") and observed.get("body")
                    and acknowledgement_index == 0 and buttons.get("complete") is True
                    and len(rows) == 1 and _integer(rows[0].get("id")) == 0
                    and isinstance(texts, list) and isinstance(prior_texts, list)
                    and (texts == [] or prior_texts == [])):
                observed.pop("visibleTexts", None)
                expected.pop("visibleTexts", None)
            if current.get("showing") is not True or observed != expected:
                raise SDKError("POPUP_CHANGED", "popup changed; choose from a fresh observation")
        if world.get("myTurn") is not True:
            # A native notification may pause the opponent's pipeline after our
            # EndTurn (e.g. a rejected treaty). Only acknowledge a single ordinary
            # OK button. An owned visible hotseat recap can also show such a notice
            # while its replayed CurrentPlayer still names the previous human.
            # Actual choices and another human's presentation stay blocked.
            buttons = (current.get("buttons") or {}).get("buttons") or []
            acknowledgement = (world.get("myTurn") is False
                and world.get("popupNeedsChoice") is False
                and current.get("popupType") in {"BasicPopup", "BasicPopupLegacy", "DiplomacyPopup"}
                and acknowledgement_index == -1 and len(buttons) == 1
                and _integer(buttons[0].get("id")) == -1
                and buttons[0].get("closesPopup") in (True, "True"))
            if acknowledgement:
                handoff = self._checked("handoff_status")
                acknowledgement = handoff.get("phase") == "not_hotseat" or (
                    self._handoff_decision_visible(handoff)
                    and handoff.get("recapping") is True
                    and handoff.get("overlayPresented") is False)
            if not acknowledgement:
                raise SDKError("NOT_CONTROLLED_TURN", "outside the controlled turn, only a notification OK in solo play or the owned visible hotseat recap may be acknowledged")
        binding = self._binding(world)
        return {"expectedEpisodeId": binding["episodeId"], "expectedPlayerId": str(self.player_id),
                "expectedStateVersion": str(binding["stateVersion"]), "expectedStreamId": binding["streamId"]}

    @_logged
    def dismiss_resolved_reward(self, reward, *, expected_popup):
        if not isinstance(reward, str) or not reward:
            raise SDKError("INVALID_ARGUMENT", "reward must name the already selected native reward")
        guards = self._popup_context(expected_popup)
        if expected_popup.get("popupType") != "RewardPopup":
            raise SDKError("REWARD_DISPLAY_NOT_CURRENT", "only an already resolved RewardPopup can be dismissed")
        try:
            native = self._request("popup", op="dismiss_resolved_reward", reward=reward, **guards)
        except (TimeoutError, RuntimeError) as exc:
            if isinstance(exc, SDKError) and exc.code != "INVALID_RESPONSE":
                raise
            return self._envelope("unknown", None, error=str(exc), retryAllowed=False)
        status = self._ui_write_status(native, self._popup_callback_completed(native))
        return self._envelope(status, native, retryAllowed=False)

    @_logged
    def advance_pending_popup(self, *, expected_observation):
        """Request the next native pending display once; never choose a reward."""
        if not isinstance(expected_observation, dict) or not isinstance(expected_observation.get("binding"), dict):
            raise SDKError("INVALID_ARGUMENT", "a fresh SDK observation with binding is required")
        observation = self.observe()
        world, popup = observation.get("world") or {}, observation.get("popup") or {}
        if observation.get("binding") != expected_observation["binding"]:
            raise SDKError("STATE_CHANGED", "pending display context changed; observe again")
        count = _integer(world.get("pendingTriggerCountMine"))
        if (observation.get("status") != "observed" or world.get("myTurn") is not True
                or world.get("matchEnded") is True or popup.get("showing") is not False
                or count is None or count <= 0):
            raise SDKError("PENDING_POPUP_NOT_READY", "requires own visible turn, no popup and known pending triggers")
        binding = observation["binding"]
        if not binding.get("streamId") or _integer(binding.get("stateVersion")) is None or binding["stateVersion"] <= 0:
            raise SDKError("VERSION_UNKNOWN", "pending display requires a visible positive state version")
        try:
            native = self._request("popup", op="next", expectedEpisodeId=binding["episodeId"],
                expectedPlayerId=str(self.player_id), expectedStreamId=binding["streamId"],
                expectedStateVersion=str(binding["stateVersion"]))
        except (TimeoutError, RuntimeError) as exc:
            if isinstance(exc, SDKError) and exc.code != "INVALID_RESPONSE":
                raise
            return self._envelope("unknown", None, error=str(exc), retryAllowed=False)
        return self._envelope(self._ui_write_status(native, self._popup_callback_completed(native)),
                              native, retryAllowed=False)

    @_logged
    def resolve_pending(self, *, timeout=10.0, interval=0.25, max_steps=24):
        """Advance known displays and original receipts; return actual choices to caller."""
        if (not _finite_seconds(timeout) or not _finite_seconds(interval, positive=True)
                or _integer(max_steps) is None or isinstance(max_steps, bool) or not 1 <= int(max_steps) <= 128):
            raise SDKError("INVALID_ARGUMENT", "finite timeout, positive interval and max_steps 1..128 required")
        deadline, steps, attempted = self.clock() + timeout, [], set()
        for _ in range(int(max_steps)):
            observation = self.observe()
            world, popup = observation.get("world") or {}, observation.get("popup") or {}
            def finish(status, next_step, **extra):
                return self._envelope(status, None, observation=observation, steps=steps,
                                      nextStep=next_step, retryAllowed=False, **extra)
            if not world or observation.get("status") not in {"observed", "needs_resolution"}:
                return finish(observation.get("status", "unknown"), "observe")
            if world.get("matchEnded") is True:
                return finish("ended", "result")
            if any(row.get("state") == "uncertain" for row in self._pending_writes):
                return finish("unknown", "reconcile_original_request")
            if self.clock() > deadline:
                return finish("timeout", "observe")
            if popup.get("showing") is True:
                buttons = popup.get("buttons") or {}
                rows = buttons.get("buttons") or []
                display = popup.get("rewardDisplay") or {}
                typ = popup.get("popupType")
                if typ == "RewardPopup" and display.get("canDismiss") is True and display.get("selectedReward"):
                    identity = {k: v for k, v in popup.items() if k not in {"reqId", "visibleTexts"}}
                    key = ("dismiss", _hash(identity), _hash(observation["binding"]))
                    operation = lambda: self.dismiss_resolved_reward(display["selectedReward"], expected_popup=popup)
                elif (buttons.get("complete") is True and len(rows) == 1
                        and ((typ == "IconRewardPopup" and _integer(rows[0].get("id")) == 0)
                             or (typ in {"BasicPopup", "BasicPopupLegacy", "DiplomacyPopup"}
                                 and _integer(rows[0].get("id")) == -1 and rows[0].get("closesPopup") in (True, "True")))):
                    choice = _integer(rows[0]["id"])
                    # reqId is transport-only, including in the no-replay key.
                    identity = {k: v for k, v in popup.items() if k not in {"reqId", "visibleTexts"}}
                    key = ("acknowledge", _hash(identity), _hash(observation["binding"]))
                    operation = lambda: self.choose_popup(choice, expected_popup=popup)
                else:
                    return finish("needs_resolution", "choose_city_reward" if typ == "RewardPopup" else "choose_popup")
            elif popup.get("showing") is False and world.get("myTurn") is True and (_integer(world.get("pendingTriggerCountMine")) or 0) > 0:
                key = ("next", _hash(observation["binding"]), world["pendingTriggerCountMine"])
                operation = lambda: self.advance_pending_popup(expected_observation=observation)
            elif self.unresolved is not None or self._pending_writes:
                wait = min(interval, max(0.0, deadline - self.clock()))
                if self._session_store is not None:
                    receipt = self.resume_pending(timeout=wait)
                elif self.unresolved is not None and _integer(self.unresolved.get("ticket")) is not None:
                    receipt = self.wait_result(self.unresolved["ticket"], timeout=wait)
                else:
                    return finish("unknown", "reconcile_original_request")
                steps.append({"operation": "resume", "result": receipt})
                if receipt.get("status") in {"unknown", "not_confirmed", "timeout", "rejected"}:
                    return finish(receipt["status"], "query_original_receipt")
                if receipt.get("status") == "needs_resolution":
                    return finish("needs_resolution", "observe")
                continue
            elif world.get("canAct") is True:
                return finish("ready", "list_actions")
            else:
                return finish("needs_resolution", "observe")
            if key in attempted:
                return finish("needs_resolution", "observe")
            attempted.add(key)
            response = operation()
            steps.append({"operation": key[0], "result": response})
            if response.get("status") != "submitted":
                return finish(response.get("status", "unknown"), "observe")
            remaining = deadline - self.clock()
            if remaining > 0:
                self.sleep(min(interval, remaining))
        return self._envelope("needs_resolution", None, steps=steps, nextStep="observe",
                              retryAllowed=False, reason="resolution step limit reached")

    @_logged
    def handoff_status(self):
        return self._request("handoff_status")

    @_logged
    def handoff_start(self, status):
        if status.get("configuredPlayerId") != self.player_id or status.get("targetPlayerId") != self.player_id:
            raise SDKError("SEAT_MISMATCH", "handoff is not for this configured controlled player")
        if status.get("canStart") is not True or not status.get("handoffId"):
            raise SDKError("HANDOFF_NOT_READY", "handoff cannot be started from this status")
        return self._request("handoff_start", playerId=str(self.player_id), expectedHandoffId=status["handoffId"])

    @_logged
    def watch_handoff(self, *, timeout=30.0, interval=0.25, return_on_human=False):
        """Works after a human's End Turn or when connecting at an existing curtain."""
        if not _finite_seconds(timeout) or not _finite_seconds(interval, positive=True):
            raise SDKError("INVALID_ARGUMENT", "finite nonnegative timeout and positive interval required")
        end, submitted = self.clock() + timeout, set()
        while True:
            status = self.handoff_status()
            if status.get("ok") is not True:
                return self._envelope("unavailable", status)
            if status.get("phase") in {"not_hotseat", "no_active_game", "configuration_required", "ended", "identity_unknown"}:
                return self._envelope(status["phase"], status)
            if status.get("phase") == "human_turn" and return_on_human:
                return self._envelope("human_turn", status)
            if status.get("ready") is True:
                observation = self.observe()
                if observation.get("status") != "observed":
                    return observation
                if (observation.get("popup") or {}).get("showing") is True:
                    observation["status"] = "needs_resolution"
                    return observation
                can_act = (observation.get("world") or {}).get("canAct")
                if can_act is True:
                    return observation
                if can_act is not False:
                    return self._envelope("unknown", observation, retryAllowed=False,
                                          error="controlled world action readiness is unavailable")
                # Native handoff may be ready before the combat sampler finishes
                # its initial frame. Wait within the same deadline, without replay.
            if status.get("needsDecision") is True:
                if self._handoff_decision_visible(status):
                    return self.observe()
                return self._envelope("needs_resolution", status)
            key = status.get("handoffId")
            if status.get("canStart") is True and key not in submitted:
                submitted.add(key)  # even an uncertain callback is never sent twice
                try:
                    reply = self.handoff_start(status)
                except (TimeoutError, RuntimeError) as exc:
                    return self._envelope("unknown", status, error=str(exc), retryAllowed=False)
                if reply.get("ok") is not True:
                    return self._envelope("unknown" if reply.get("callbackSubmitted") is True else "rejected", reply, retryAllowed=False)
            if self.clock() >= end:
                return self._envelope("timeout", status, retryAllowed=False)
            self.sleep(min(interval, max(0, end - self.clock())))
