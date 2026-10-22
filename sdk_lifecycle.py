"""Local save/load/exit receipts. Never restart, kill, or replay a timed-out write."""
import copy
import uuid


class GameLifecycleMixin:
    def _finish_lifecycle_write(self, intent, response):
        if response.get("status") == "rejected" and response.get("callbackSubmitted") is False:
            self._pending_writes.remove(intent)
            return
        if not self._lifecycle_matches(intent, response):
            return
        intent["state"] = "receipt"
        intent["lifecycleReceipt"] = copy.deepcopy(response)
        if response.get("status") == "confirmed":
            self._pending_writes.remove(intent)

    def _lifecycle_matches(self, intent, native):
        return (isinstance(native, dict) and native.get("schema") == "game-lifecycle-1"
                and native.get("operationId") == intent["args"]["operationId"]
                and native.get("operation") == intent["args"]["operation"]
                and type(native.get("controlledPlayerId")) is int
                and native["controlledPlayerId"] == self.player_id
                and type(native.get("pid")) is int and native["pid"] > 0)

    def _game_operation(self, operation, *, game_id=None, wait=False, timeout=30):
        from sdk import SDKError, _finite_seconds
        if not _finite_seconds(timeout):
            raise SDKError("INVALID_ARGUMENT", "finite nonnegative lifecycle timeout required")
        if operation == "load_hotseat":
            try:
                game_id = str(uuid.UUID(game_id))
                if uuid.UUID(game_id).int == 0:
                    raise ValueError()
            except (ValueError, TypeError, AttributeError):
                raise SDKError("INVALID_ARGUMENT", "game_id must be a GUID from the native hotseat menu")
        elif game_id is not None:
            raise SDKError("INVALID_ARGUMENT", "game_id is only used for hotseat loading")
        if operation.startswith("load_") and self.episode_id is not None:
            raise SDKError("FRESH_SESSION_REQUIRED", "load into a new SDK session; do not reuse old episode bindings or enemy memory")
        if operation in {"save", "return_to_menu"}:
            self._world()  # Bind the intended episode before any native mutation.
        args = {"operation": operation, "operationId": uuid.uuid4().hex,
                "playerId": str(self.player_id), "gameId": game_id or "",
                "expectedEpisodeId": self.episode_id or ""}
        try:
            native = self._request("game_lifecycle", **args)
        except (TimeoutError, RuntimeError) as ex:
            if isinstance(ex, SDKError) and ex.code not in {"INVALID_RESPONSE"}:
                raise
            return self._envelope("unknown", None, operationId=args["operationId"],
                operation=operation, error=str(ex), retryAllowed=False)
        intent = {"args": args}
        if native.get("status") == "rejected" and native.get("callbackSubmitted") is False:
            status = "rejected"
        elif self._lifecycle_matches(intent, native):
            status = native.get("status") if native.get("status") in {"confirmed", "submitted", "unknown"} else "unknown"
        else:
            status = "unknown"
        result = self._envelope(status, native, operationId=args["operationId"], operation=operation, retryAllowed=False)
        if wait and status == "submitted":
            return self._wait_game_operation(result, timeout=timeout)
        return result

    def _wait_game_operation(self, receipt, *, timeout=30, interval=0.25):
        from sdk import SDKError, _finite_seconds
        if not _finite_seconds(timeout) or not _finite_seconds(interval, positive=True):
            raise SDKError("INVALID_ARGUMENT", "finite lifecycle timeout and positive interval required")
        if not isinstance(receipt, dict) or not isinstance(receipt.get("operationId"), str):
            raise SDKError("INVALID_ARGUMENT", "original lifecycle receipt required")
        operation_id = receipt["operationId"]
        row = next((r for r in self._pending_writes if r["command"] == "game_lifecycle"
                    and r["args"].get("operationId") == operation_id), None)
        if row is None:
            raise SDKError("LIFECYCLE_RECEIPT_REQUIRED", "no pending lifecycle intent in this session; never replay the operation")
        deadline = self.clock() + timeout
        last = row.get("lifecycleReceipt")
        while True:
            if row["args"]["operation"] == "quit":
                reader = getattr(self.transport, "lifecycle_receipt", None)
                durable = reader(operation_id) if callable(reader) else None
                if durable is not None and self._lifecycle_matches(row, durable):
                    last = durable
                    self._finish_lifecycle_write(row, durable)
            # Only the queue owner's liveness can prove exit; transport failure cannot.
            if row["args"]["operation"] == "quit" and last and last.get("quitScheduled") is True:
                probe = getattr(self.transport, "bound_process_exited", None)
                if callable(probe) and probe(last["pid"]) is True:
                    self._pending_writes.remove(row)
                    return self._envelope("confirmed", last, operationId=operation_id, operation="quit",
                        processExited=True, exitCause="not_verified", retryAllowed=False)
            try:
                native = self._request("game_lifecycle_status", operationId=operation_id, playerId=str(self.player_id))
            except (TimeoutError, RuntimeError) as ex:
                if isinstance(ex, SDKError) and ex.code != "INVALID_RESPONSE":
                    raise
                native = None
            if native is not None:
                if not self._lifecycle_matches(row, native):
                    return self._envelope("unknown", native, operationId=operation_id,
                        errorCode="LIFECYCLE_SCOPE_MISMATCH", retryAllowed=False)
                last = native
                self._finish_lifecycle_write(row, native)
                if native.get("status") in {"confirmed", "unknown"}:
                    return self._envelope(native["status"], native, operationId=operation_id,
                        operation=row["args"]["operation"], retryAllowed=False)
            if self.clock() >= deadline:
                return self._envelope("timeout", last, operationId=operation_id,
                    operation=row["args"]["operation"], retryAllowed=False)
            self.sleep(min(interval, max(0, deadline - self.clock())))
