"""JSON-only player CLI. Run --help for commands; legacy bridgecli is unchanged."""
import argparse
from contextlib import ExitStack
import copy
import json
import sys
from pathlib import Path

from sdk import PolytopiaSDK, SDKError


def _load(path):
    value = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError("CLI input file must contain a JSON object")
    return value


def _capture_session(snapshot, sdk, path):
    """Run before context exit, including exceptions, while the SDK lock is held."""
    pending = copy.deepcopy(getattr(sdk, "unresolved", None))
    writes = copy.deepcopy(getattr(sdk, "_pending_writes", []))
    snapshot.update(path=path, pendingReceipt=pending, pendingWrites=writes,
                    setupReceipt=copy.deepcopy(getattr(sdk, "_setup_unresolved", None)),
                    resolutionRequired=bool(getattr(sdk, "_resolution_required", False)))
    if pending:
        snapshot["nextStep"] = (
            "resume queries the original ticket; never resubmit the pending action"
            if pending.get("ticket") is not None else
            "uncertain write has no ticket; automatic recovery is unavailable; do not resubmit")
    elif writes:
        snapshot["nextStep"] = "resume checks original write receipts; never replay pending writes"


def main(argv=None, sdk_factory=PolytopiaSDK):
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--player", type=int, required=True)
    p.add_argument("--episode")
    p.add_argument("--request-timeout", type=float, default=20)
    p.add_argument("--log", help="optional business-call JSONL log")
    p.add_argument("--session", help="persist pending receipts and hold an exclusive session lock for this call")
    sub = p.add_subparsers(dest="command", required=True)
    obs = sub.add_parser("observe")
    obs.add_argument("--details", action="store_true")
    load = sub.add_parser("load_game", help="restore a native local save; use a fresh session")
    load.add_argument("--mode", choices=["singleplayer", "hotseat"], default="singleplayer")
    load.add_argument("--game-id", help="id from saved_games for hotseat")
    for parser in [load] + [sub.add_parser(name) for name in ("saved_games", "save_game", "return_to_menu", "quit_game")]:
        parser.add_argument("--wait", action="store_true")
        parser.add_argument("--timeout", type=float, default=30)
    lifecycle_wait = sub.add_parser("wait_lifecycle", help="query the original load/save/exit receipt")
    lifecycle_wait.add_argument("file")
    lifecycle_wait.add_argument("--timeout", type=float, default=30)
    sub.add_parser("setup_status", help="read normal single-player menu options without entering a match")
    setup = sub.add_parser("setup_action", help="explicit solo menu administration; outside planner actions")
    setup.add_argument("file", help="fresh setup_status JSON file")
    setup.add_argument("action_id")
    setup.add_argument("--wait", action="store_true")
    setup.add_argument("--timeout", type=float, default=30)
    setup_wait = sub.add_parser("wait_setup", help="query original setup progress without replaying callbacks")
    setup_wait.add_argument("file", help="original setup_action receipt JSON")
    setup_wait.add_argument("--timeout", type=float, default=30)
    preview = sub.add_parser("attack_preview", help="read current native legal attack damage without submitting an attack")
    preview.add_argument("--unit")
    preview.add_argument("--at")
    for name in ("game_info", "unit_properties", "score_snapshot", "threat_map"):
        sub.add_parser(name, help="read current native player-visible information with scope validation")
    move_preview = sub.add_parser("composite_preview", help="read native legal own-unit move path and partial effects")
    move_preview.add_argument("--unit", required=True)
    move_preview.add_argument("--at", required=True)
    move_preview.add_argument("--kind", choices=["move"], default="move")
    tribe_info = sub.add_parser("tribe_info", help="explicit native information view; default read never opens UI")
    tribe_info.add_argument("opponent")
    tribe_info.add_argument("--mode", choices=["open", "read", "close"], default="read")
    actions = sub.add_parser("list_actions")
    actions.add_argument("--scope", default="core", choices=["core", "all", "unit", "tile", "city", "tribe"])
    actions.add_argument("--unit")
    actions.add_argument("--at")
    execute = sub.add_parser("execute")
    execute.add_argument("file", help="list_actions JSON file")
    execute.add_argument("action_id")
    execute.add_argument("--wait", action="store_true")
    execute.add_argument("--timeout", type=float, default=30)
    wait = sub.add_parser("wait_result")
    wait.add_argument("file", help="original SDK receipt JSON file")
    wait.add_argument("--timeout", type=float, default=30)
    resume = sub.add_parser("resume", help="query the session's original pending receipt; never resubmit an action")
    resume.add_argument("--timeout", type=float, default=30)
    ack = sub.add_parser("acknowledge_settled_unknown", help="explicitly accept a finished action's unknown effect; never resend")
    ack.add_argument("file", help="original SDK receipt JSON file")
    ack.add_argument("--accept-unknown-effect", action="store_true", required=True)
    ev = sub.add_parser("events")
    ev.add_argument("--after", type=int, default=0)
    ev.add_argument("--stream")
    ev.add_argument("--limit", type=int, default=100)
    sub.add_parser("result")
    leaderboard = sub.add_parser("leaderboard")
    leaderboard.add_argument("--mode", choices=["open", "read", "close"], default="read")
    notices = sub.add_parser("notifications")
    notices.add_argument("--after", type=int, default=0)
    notices.add_argument("--stream")
    notices.add_argument("--limit", type=int, default=100)
    popup = sub.add_parser("choose_popup")
    popup.add_argument("file", help="observe JSON file containing popup")
    popup.add_argument("index", type=int)
    dismiss = sub.add_parser("dismiss_resolved_reward")
    dismiss.add_argument("file", help="fresh observe JSON containing resolved RewardPopup")
    dismiss.add_argument("reward")
    advance = sub.add_parser("advance_pending_popup", help="show the next owned native pending display once")
    advance.add_argument("file", help="fresh observe JSON with binding")
    resolve = sub.add_parser("resolve_pending", help="advance displays and original receipts; stop at reward choices")
    resolve.add_argument("--timeout", type=float, default=10)
    resolve.add_argument("--max-steps", type=int, default=24)
    sub.add_parser("handoff_status")
    handoff = sub.add_parser("watch_handoff")
    handoff.add_argument("--timeout", type=float, default=30)
    handoff.add_argument("--return-on-human", action="store_true")
    args = p.parse_args(argv)
    session_snapshot = {}
    try:
        if args.command == "resume" and args.session is None:
            raise SDKError("SESSION_REQUIRED", "resume requires --session PATH; it only queries the original receipt")
        with ExitStack() as stack:
            constructor = dict(player_id=args.player, episode_id=args.episode,
                               request_timeout=args.request_timeout, log_path=args.log)
            if args.session is not None:
                constructor["session_path"] = args.session
            sdk = sdk_factory(**constructor)
            if args.session is not None:
                sdk = stack.enter_context(sdk)
                stack.callback(_capture_session, session_snapshot, sdk, args.session)
            if args.command == "observe":
                response = sdk.observe(details=args.details)
            elif args.command == "load_game":
                response = sdk.load_game(mode=args.mode, game_id=args.game_id, wait=args.wait, timeout=args.timeout)
            elif args.command in {"saved_games", "save_game", "return_to_menu", "quit_game"}:
                response = getattr(sdk, args.command)(wait=args.wait, timeout=args.timeout)
            elif args.command == "wait_lifecycle":
                response = sdk.wait_lifecycle(_load(args.file), timeout=args.timeout)
            elif args.command == "setup_status":
                response = sdk.setup_status()
            elif args.command == "setup_action":
                response = sdk.setup_action(args.action_id, expected_setup=_load(args.file), wait=args.wait, timeout=args.timeout)
            elif args.command == "wait_setup":
                response = sdk.wait_setup(_load(args.file), timeout=args.timeout)
            elif args.command == "attack_preview":
                response = sdk.attack_preview(unit=args.unit, at=args.at)
            elif args.command in {"game_info", "unit_properties", "score_snapshot", "threat_map"}:
                response = getattr(sdk, args.command)()
            elif args.command == "composite_preview":
                response = sdk.composite_preview(unit=args.unit, at=args.at, kind=args.kind)
            elif args.command == "tribe_info":
                response = sdk.tribe_info(args.opponent, mode=args.mode)
            elif args.command == "list_actions":
                response = sdk.list_actions(args.scope, unit=args.unit, at=args.at)
            elif args.command == "execute":
                discovery = _load(args.file)
                candidates = discovery.get("actions", [])
                if not isinstance(candidates, list) or any(not isinstance(a, dict) for a in candidates):
                    raise ValueError("discovery actions must be a JSON array of objects")
                proposal = next((a for a in candidates if a.get("actionId") == args.action_id), None)
                if proposal is None:
                    raise SDKError("ACTION_NOT_FOUND", "action_id not found in discovery file")
                response = sdk.execute(proposal, wait=args.wait, timeout=args.timeout)
            elif args.command == "resume":
                response = sdk.resume_pending(timeout=args.timeout)
            elif args.command == "wait_result":
                receipt = _load(args.file)
                response = sdk.wait_result(receipt.get("ticket"), binding=receipt.get("binding"), timeout=args.timeout)
            elif args.command == "acknowledge_settled_unknown":
                receipt = _load(args.file)
                # Reassociate and freshly query the original receipt in this CLI
                # process before invoking the SDK's explicit recovery proof.
                response = sdk.wait_result(receipt.get("ticket"), binding=receipt.get("binding"), timeout=0)
                if response.get("status") == "unknown":
                    response = sdk.acknowledge_settled_unknown(receipt.get("ticket"), binding=receipt.get("binding"),
                        accept_unknown_effect=args.accept_unknown_effect)
            elif args.command == "events":
                response = sdk.events(args.after, stream_id=args.stream, limit=args.limit)
            elif args.command == "result":
                response = sdk.result()
            elif args.command == "leaderboard":
                response = sdk.leaderboard(args.mode)
            elif args.command == "notifications":
                response = sdk.notifications(args.after, stream_id=args.stream, limit=args.limit)
            elif args.command == "advance_pending_popup":
                response = sdk.advance_pending_popup(expected_observation=_load(args.file))
            elif args.command == "resolve_pending":
                response = sdk.resolve_pending(timeout=args.timeout, max_steps=args.max_steps)
            elif args.command == "choose_popup":
                response = sdk.choose_popup(args.index, expected_popup=_load(args.file)["popup"])
            elif args.command == "dismiss_resolved_reward":
                response = sdk.dismiss_resolved_reward(args.reward, expected_popup=_load(args.file)["popup"])
            elif args.command == "handoff_status":
                native = sdk.handoff_status()
                response = sdk._envelope("observed" if native.get("ok") is True else "unavailable", native)
            else:
                response = sdk.watch_handoff(timeout=args.timeout, return_on_human=args.return_on_human)
    except SDKError as exc:
        response = exc.as_dict()
    except (TimeoutError, RuntimeError) as exc:
        response = {"schema": "polytopia-sdk-1", "ok": False, "status": "unknown",
                    "errorCode": "TRANSPORT_UNAVAILABLE", "error": str(exc), "retryAllowed": False}
    except (ValueError, KeyError, OSError) as exc:
        response = SDKError("INVALID_INPUT", str(exc)).as_dict()
    if session_snapshot:
        response = {**response, "session": session_snapshot}
    print(json.dumps(response, ensure_ascii=False))
    return exit_code(response)


def exit_code(response):
    status = response.get("status")
    if status == "rejected":
        return 3
    if status == "timeout":
        return 4
    if status == "state_changed":
        return 6
    if status in {"unknown", "not_confirmed", "unavailable"} or response.get("ok") is False:
        return 5
    if status in {"accepted", "submitted", "opening", "closing", "needs_resolution", "handoff", "human_turn", "history_gap"}:
        return 6
    return 0


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    raise SystemExit(main())
