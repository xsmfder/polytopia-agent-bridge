"""Production boundary between the bridge and the planner/memory: `world` frame -> normalized
legal observation.

Everything the planner is allowed to remember passes through `normalize_world`. It is strict on
purpose: a frame that is incomplete, produced under an unverified identity, filtered with any
failed call, out of order, or carrying audit markers is REJECTED, and a rejection never yields a
partial result that a caller could accidentally store.

Note on markers: `hiddenEntityPolicy` is a CONSTANT legal policy string ("hidden entities are
never reported") emitted by every world frame; it is metadata about the policy, not audit truth,
so it is accepted. Real audit/omniscient payloads and hidden entities are rejected.
"""
from __future__ import annotations

import math
from sdk_values import integer, coordinates, native_identifier

AUDIT_MARKERS = ("omniscient", "hiddenUnits", "hiddenEntities", "hiddenEntityPolicyTruth",
                 "auditTruth", "playersDump", "rawPlayers")

FILTER_PARTS = ("unit", "resource")


class ObservationRejected(Exception):
    """The frame is not a complete, legally filtered observation. No result was produced."""


class StaleObservation(ObservationRejected):
    """The frame is older than the last applied revision; it must be ignored, never applied."""


def _require(cond, msg):
    if not cond:
        raise ObservationRejected(msg)


def parse_coords(value):
    """Strict `(x,y)` -> [x, y]. Anything else is a schema error, not a guess."""
    try:
        return coordinates(value, parentheses_required=True)
    except ValueError:
        raise ObservationRejected("coords must be an Int32 (x,y) pair: %r" % (value,))


def _opt_int(value):
    if value is None or value == "":
        return None
    try:
        return integer(value)
    except (TypeError, ValueError):
        raise ObservationRejected("expected an integer, got %r" % (value,))


def _opt_bool(value, field):
    if value is None:
        return None
    _require(isinstance(value, bool), "%s must be a JSON boolean or null, got %r" % (field, type(value).__name__))
    return value


def _opt_int_strict(value, field):
    if value is None or value == "":
        return None
    try:
        return integer(value)
    except ValueError:
        raise ObservationRejected("%s is not an integer: %r" % (field, value))


PUBLIC_UNIT_STATS = ("health", "maxHealth", "attack", "attackBonus", "defence",
                     "defenceMultiplier", "movement", "movementBonus", "range")
_PUBLIC_UNIT_PAIRS = {
    "kills": ("killsStatus", ("ok", "not-displayed", "unavailable")),
    "veteran": ("veteranStatus", ("ok", "not-displayed", "unavailable")),
    "passengerType": ("passengerStatus", ("ok", "none", "unavailable")),
    "homeCityName": ("homeCityStatus", ("ok", "none", "unavailable")),
}
_PUBLIC_UNIT_PROGRESS = {"progressKind", "progressLimit", "progressTarget"}


def public_visual_effects(value, *, persisted=False):
    """Only native rendered cues; names are not the unit's full mechanical effects."""
    if value is None:
        return None
    if not isinstance(value, dict) or value.get('schema') != 'unit-visual-effects-1':
        if persisted:
            raise ValueError('invalid unit visual effects schema')
        return None
    particles, overlay = value.get('particles'), value.get('overlay')
    particles = particles if isinstance(particles, dict) else {}
    overlay = overlay if isinstance(overlay, dict) else {}
    status = value.get('status')
    forced = 'withheld' if status == 'withheld' else 'unavailable' if status not in ('ok', 'partial') else None
    ps, names = particles.get('status'), None
    if forced is None and ps in ('ok', 'partial') and isinstance(particles.get('types'), list):
        rows = particles['types']
        names = sorted({name for name in rows if native_identifier(name) is not None})
        if len(names) != len(rows):
            ps = 'partial'
    else:
        ps = forced or 'unavailable'
    os, color, strength = overlay.get('status'), None, None
    def finite(x):
        # Unity Color components and strength are float32, including native HDR
        # values. Do not clamp them, but reject nonfinite or unrepresentable input.
        return type(x) in (int, float) and abs(x) <= 3.4028234663852886e38
    if (forced is None and os == 'ok' and isinstance(overlay.get('color'), list)
            and len(overlay['color']) == 4 and all(finite(x) for x in overlay['color'])
            and finite(overlay.get('strength'))):
        color, strength = list(overlay['color']), overlay['strength']
    else:
        os = forced or 'unavailable'
    derived = (forced or ('ok' if ps == os == 'ok' else 'partial'
                         if ps in ('ok', 'partial') or os == 'ok' else 'unavailable'))
    if status == 'partial' and derived == 'ok':
        derived = 'partial'
    result = {'schema': 'unit-visual-effects-1', 'status': derived,
              'particles': {'status': ps, 'types': names},
              'overlay': {'status': os, 'color': color, 'strength': strength}}
    if persisted and result != value:
        raise ValueError('invalid persisted unit visual effects')
    return result


def public_unit_info(value, *, persisted=False):
    """Project only the native unit panel fields; persisted notes must be canonical.

    An unreadable panel field is a domain gap, not a failed map observation. The
    persisted form is stricter so appended native ids/coordinates cannot become
    player notes. None also supports sessions written before this panel existed.
    """
    if value is None:
        return None
    fields = {"schema", "status", "stats", *_PUBLIC_UNIT_PAIRS,
              *(pair[0] for pair in _PUBLIC_UNIT_PAIRS.values())}
    if not isinstance(value, dict) or value.get("schema") != "unit-public-info-1":
        if persisted:
            raise ValueError("invalid public unit panel schema")
        return None
    legacy = not any(field in value for field in _PUBLIC_UNIT_PROGRESS)
    allowed = (fields, fields | _PUBLIC_UNIT_PROGRESS, fields | {'visualEffects'},
               fields | _PUBLIC_UNIT_PROGRESS | {'visualEffects'})
    if persisted and (set(value) not in allowed
                      or not isinstance(value.get("stats"), dict)
                      or set(value["stats"]) != set(PUBLIC_UNIT_STATS)):
        raise ValueError("invalid public unit panel fields")
    status = value.get("status")
    if status not in ("ok", "partial", "unavailable"):
        status = "unavailable"
    result = {"schema": "unit-public-info-1", "status": status}
    for field, (status_field, statuses) in _PUBLIC_UNIT_PAIRS.items():
        field_status = value.get(status_field)
        if field_status not in statuses or status == "unavailable":
            field_status = "unavailable"
        item = value.get(field) if field_status == "ok" else None
        valid = (type(item) is int and item >= 0 if field == "kills" else
                 type(item) is bool if field == "veteran" else
                 isinstance(item, str) and bool(item))
        if field_status == "ok" and not valid:
            item, field_status = None, "unavailable"
        result[field], result[status_field] = item, field_status
        if field_status == "unavailable" and result["status"] == "ok":
            result["status"] = "partial"
    stats = value.get("stats")
    stats = stats if isinstance(stats, dict) else {}
    result["stats"] = {}
    for field in PUBLIC_UNIT_STATS:
        item = stats.get(field) if status != "unavailable" else None
        if (type(item) not in (int, float) or type(item) is float and not math.isfinite(item)
                or field != "movementBonus" and item < 0):
            item = None
        result["stats"][field] = item
        if item is None and result["status"] == "ok":
            result["status"] = "partial"
    if 'visualEffects' in value:
        visual = value['visualEffects']
        if status == 'unavailable' and isinstance(visual, dict):
            visual = {**visual, 'status': 'unavailable'}
        result['visualEffects'] = public_visual_effects(visual, persisted=persisted)
    legacy_result = dict(result)
    kind, limit, target = None, None, None
    if result['killsStatus'] == 'ok' and status != 'unavailable':
        kind = value.get('progressKind')
        if kind not in ('veteran', 'evolve'):
            kind = None
        limit = value.get('progressLimit')
        if type(limit) is not int or limit < 0:
            limit = None
        target = value.get('progressTarget') if kind == 'evolve' else None
        if not isinstance(target, str) or not target:
            target = None
        if kind is None or limit is None or kind == 'evolve' and target is None:
            if result['status'] == 'ok':
                result['status'] = 'partial'
    result.update(progressKind=kind, progressLimit=limit, progressTarget=target)
    if persisted and (legacy_result if legacy else result) != value:
        raise ValueError("invalid public unit panel values")
    return result


# This list is scoped to the bare-world projection. Building candidates live in
# SDK list_actions(scope="city") / observe(details=True).cities; frozen terminal
# results live in SDK.result(). Neither is a missing feature of the whole bridge.
UNIMPLEMENTED_DECISION_FIELDS = (
    "legalBuildOptions",
)


def _public_own_invisible_hint(value):
    if isinstance(value, dict) and value.get("status") == "ok" and type(value.get("present")) is bool:
        return {"present": value["present"], "status": "ok"}
    return {"present": None, "status": "withheld" if isinstance(value, dict) and value.get("status") == "withheld" else "unavailable"}


def _public_tile_style(value):
    if not isinstance(value, dict) or value.get("status") not in ("ok", "partial"):
        return {"tribe": None, "skin": None, "status": "unavailable"}
    tribe, skin = native_identifier(value.get("tribe")), native_identifier(value.get("skin"))
    return {"tribe": tribe, "skin": skin, "status": "ok" if tribe is not None and skin is not None and value["status"] == "ok" else "partial"}


def normalize_world(frame):
    """Return a normalized observation, or raise without side effects.

    The returned dict is the ONLY thing the memory and the planner may consume: it is built by an
    explicit safe projection, never by passing the world frame through. Legitimate decision fields
    are mapped one by one with type checks; anything not proven player-visible is left out.
    """
    _require(isinstance(frame, dict), "frame must be a mapping")

    for marker in AUDIT_MARKERS:
        _require(not frame.get(marker), "audit/omniscient field present: %s" % marker)

    _require(frame.get("ok") is True, "world frame did not succeed")
    _require(frame.get("controlledIdentityVerified") is True,
             "controlled identity is not verified; private observation withheld")
    revision = frame.get("observationRevision")
    _require(isinstance(revision, int) and not isinstance(revision, bool),
             "observationRevision must be an integer")
    match_ctx = frame.get("episodeId") or frame.get("matchContext")
    _require(isinstance(match_ctx, str) and match_ctx, "episodeId/matchContext is missing")
    # An episode id must be an opaque local token: a seed-shaped context is refused outright.
    _require("seed" not in match_ctx.lower(), "episode context looks seed-derived and is refused")
    _require(frame.get("completeness") == "complete",
             "world reports completeness=%r" % (frame.get("completeness"),))

    filters = frame.get("visibilityFilters")
    _require(isinstance(filters, dict), "visibilityFilters missing")
    for part in FILTER_PARTS:
        stat = filters.get(part)
        _require(isinstance(stat, dict), "visibilityFilters.%s missing" % part)
        _require(stat.get("status") == "complete",
                 "%s filter status is %r, so the snapshot is not a complete filtered view"
                 % (part, stat.get("status")))
        _require(isinstance(stat.get("attempted"), int) and not isinstance(stat.get("attempted"), bool)
                 and stat.get("attempted") >= 0,
                 "%s filter attempted count is invalid" % part)

    me = frame.get("localPlayerId")
    _require(me is not None, "localPlayerId missing")
    me_i = _opt_int_strict(me, "localPlayerId")
    _require(me_i is not None, "localPlayerId missing")

    units = frame.get("units")
    _require(isinstance(units, list), "units must be a list")

    own_units, visible_enemies = [], []
    for u in units:
        _require(isinstance(u, dict), "unit entry must be a mapping")
        owner, utype = u.get("owner"), u.get("type")
        _require(owner is not None, "a unit entry has no owner")
        _require(isinstance(utype, str) and utype, "a unit entry has no type")
        pos = parse_coords(u.get("coords"))
        owner_i = _opt_int_strict(owner, "unit.owner")
        _require(owner_i is not None, "a unit entry has no owner")
        entry = {"owner": owner_i, "unitType": utype, "coords": pos,
                 "hp": _opt_int(u.get("health")),
                 # public kill counter (UnitState.xp): player-visible and the observable source used
                 # to certify that a kill really happened. Unreadable stays None, never 0.
                 "xp": _opt_int(u.get("xp"))}
        properties = u.get("properties")
        entry["publicInfo"] = public_unit_info(properties.get("publicInfo")
                                                if isinstance(properties, dict) else None)
        if owner_i == me_i:
            # Own units may carry an execution identity and their own action state: the game itself
            # reports these for the controlled player, and a planner needs them to decide.
            entry["executionId"] = None if u.get("id") is None else str(u.get("id"))
            entry["moved"] = _opt_bool(u.get("moved"), "unit.moved")
            entry["attacked"] = _opt_bool(u.get("attacked"), "unit.attacked")
            entry["canMove"] = _opt_bool(u.get("canMove"), "unit.canMove")
            entry["canAttack"] = _opt_bool(u.get("canAttack"), "unit.canAttack")
            entry["maxHealth"] = _opt_int(u.get("maxHealth"))
            entry["promotionLevel"] = _opt_int(u.get("promotionLevel"))
            entry["adjacentInvisibleThreat"] = _public_own_invisible_hint(
                properties.get("adjacentInvisibleThreat") if isinstance(properties, dict) else None)
            own_units.append(entry)
        else:
            # An enemy's internal id and its remaining-move flags are NOT player knowledge.
            visible_enemies.append(entry)

    resources = []
    for r in frame.get("resources") or []:
        _require(isinstance(r, dict), "resource entry must be a mapping")
        rtype = r.get("resourceType")
        _require(isinstance(rtype, str) and rtype, "a resource entry has no type")
        resources.append({"coords": parse_coords(r.get("coords")), "resourceType": rtype,
                          "owner": _opt_int_strict(r.get("owner"), "resource.owner")})

    tiles = []
    for t in frame.get("tiles") or []:
        _require(isinstance(t, dict), "tile entry must be a mapping")
        if "explored" in t:
            _require(t["explored"] is True, "an explicitly unexplored or uncertified tile cannot publish terrain")
        tiles.append({"coords": parse_coords(t.get("c")), "terrain": t.get("terrain"),
                      "owner": _opt_int_strict(t.get("owner"), "tile.owner"),
                      "water": _opt_bool(t.get("water"), "tile.water"),
                      "land": _opt_bool(t.get("land"), "tile.land"),
                      "inSight": _opt_bool(t.get("inSight"), "tile.inSight"),
                      "explored": t.get("explored"),
                      "currentlyVisible": _opt_bool(t.get("currentlyVisible"), "tile.currentlyVisible"),
                      "lastObservedTurn": _opt_int(t.get("lastObservedTurn")),
                      "lastObservedRevision": _opt_int(t.get("lastObservedRevision")),
                      "visualStyle": _public_tile_style(t.get("visualStyle"))})

    improvements = []
    for c in frame.get("improvements") or []:
        _require(isinstance(c, dict), "improvement entry must be a mapping")
        improvements.append({"coords": parse_coords(c.get("coords")),
                             "owner": _opt_int_strict(c.get("owner"), "improvement.owner"),
                             "isMine": _opt_bool(c.get("isMine"), "improvement.isMine"),
                             "improvementType": c.get("improvementType"),
                             "inSight": _opt_bool(c.get("inSight"), "improvement.inSight"),
                             "level": _opt_int(c.get("level")),
                             "population": _opt_int(c.get("population"))})

    # A present canonical null is explicit unknown, not permission to substitute
    # an old alias. Legacy alias fallback applies only when the canonical key is absent.
    owned_techs = frame["ownedTechs"] if "ownedTechs" in frame else frame.get("availableTechs")
    _require(owned_techs is None or isinstance(owned_techs, list), "ownedTechs must be a list")
    for t in owned_techs or []:
        _require(isinstance(t, str), "ownedTechs entries must be strings")
    owned_techs_status = frame.get("ownedTechsStatus", "ok" if owned_techs is not None else "unavailable")
    if owned_techs is None or owned_techs_status not in ("ok", "partial"):
        owned_techs, owned_techs_status = None, "unavailable"

    # Resource-category permissions are a FILTER-level gate: a partial/unavailable permission set
    # means the resource view cannot be certified at all, so the whole observation is refused.
    cats = frame.get("resourceCategoryPermissions")
    _require(isinstance(cats, dict), "resourceCategoryPermissions missing")
    _require(cats.get("status") == "ok",
             "resource category permissions are %r, so resources cannot be certified"
             % (cats.get("status"),))
    _require(isinstance(cats.get("visibleCategories"), list), "visibleCategories missing")
    for c in cats["visibleCategories"]:
        _require(isinstance(c, str), "visibleCategories entries must be strings")

    # The researchable set is a DOMAIN field: when it is unsupported that is reported as such and the
    # rest of the observation stays usable. An unavailable economy field must never be mistaken for
    # an empty board, and no cost is ever computed locally.
    research = frame.get("researchableTechs")
    if not isinstance(research, dict):
        # a missing research domain is a domain gap: withhold it, keep the legal board
        research = {"status": "unavailable"}
    rstatus = research.get("status")
    if rstatus not in ("ok", "partial", "unavailable"):
        rstatus = "unavailable"
    ritems = research.get("items")
    # An unavailable/partial domain legitimately omits `items`, and a partial item may carry a null
    # tech name: those are DOMAIN gaps, so they are withheld in the projection rather than rejecting
    # an otherwise legal board. (Resource/filter/identity gates still reject as before.)
    if ritems is None:
        ritems = []
    if not isinstance(ritems, list):
        ritems = []
        rstatus = "unavailable"

    # Legally visible destruction events, passed through: memory may only confirm a kill from such
    # an event, never from absence of a sighting.
    destroyed = []
    for d in frame.get("destroyed") or []:
        _require(isinstance(d, dict), "destroyed entry must be a mapping")
        _require(d.get("owner") is not None, "a destroyed entry has no owner")
        _require(isinstance(d.get("unitType"), str) and d["unitType"],
                 "a destroyed entry has no unit type")
        owner_i = _opt_int_strict(d.get("owner"), "destroyed.owner")
        _require(owner_i is not None, "a destroyed entry has no owner")
        destroyed.append({"owner": owner_i, "unitType": d["unitType"],
                          "coords": parse_coords(d.get("coords"))})

    # --- train options: an explicit whitelist projection -------------------------------
    # A DOMAIN field: an unsupported or failed train domain must NOT reject an otherwise legal
    # board, and it must never be dressed up as an "ok empty menu". Only options whose identity,
    # city membership and price all validate are handed to the planner.
    my_city_coords, explored_coords = set(), set()
    for t in tiles:
        if t.get("coords"):
            explored_coords.add(tuple(t["coords"]))
    for c in improvements:
        if c.get("isMine") is True and c.get("owner") == me_i and isinstance(c.get("improvementType"), str) \
                and c["improvementType"].casefold() == "city" and c.get("coords"):
            my_city_coords.add(tuple(c["coords"]))

    train = frame.get("trainOptions")
    reason_codes = []
    tstatus_in = train.get("status") if isinstance(train, dict) else None
    if not isinstance(train, dict):
        tstatus, proj_cities = "unavailable", []
        reason_codes.append("train-options-missing")
    elif tstatus_in not in ("ok", "partial", "unavailable"):
        tstatus, proj_cities = "unavailable", []
        reason_codes.append("train-status-invalid")
    elif tstatus_in == "unavailable":
        tstatus, proj_cities = "unavailable", []
    else:
        tstatus = tstatus_in
        raw_cities = train.get("cities")
        if not isinstance(raw_cities, list):
            raw_cities = []
            if tstatus != "unavailable":
                tstatus, reason_codes = "partial", reason_codes + ["train-cities-missing"]
        proj_cities = []
        seen_city, seen_option = set(), set()
        for city in raw_cities:
            if not isinstance(city, dict):
                tstatus = "partial"
                reason_codes.append("city-not-a-mapping")
                continue
            try:
                ccoords = parse_coords(city.get("coords"))
            except ObservationRejected:
                tstatus = "partial"
                reason_codes.append("city-coords-invalid")
                continue
            key = tuple(ccoords)
            if key not in explored_coords or key not in my_city_coords:
                # an unverified / other-player / unexplored / non-City tile never reaches the planner
                tstatus = "partial"
                reason_codes.append("city-not-my-visible-city")
                continue
            if key in seen_city:
                tstatus = "partial"
                reason_codes.append("duplicate-city")
                continue
            seen_city.add(key)
            opts_out, city_bad = [], False
            raw_opts = city.get("options")
            if not isinstance(raw_opts, list):
                city_bad = True
                reason_codes.append("city-options-missing")
                raw_opts = []
            for opt in raw_opts:
                if not isinstance(opt, dict):
                    city_bad = True
                    reason_codes.append("option-not-a-mapping")
                    continue
                uname = opt.get("unitType")
                if native_identifier(uname) is None:
                    city_bad = True
                    reason_codes.append("unit-type-invalid")
                    continue
                # TrainOptions already verifies the exact native UnitData.Type enum,
                # command owner/city and current native price. A static Python type
                # file cannot re-certify that menu and may omit newer valid types.
                opt_key = (key, uname)
                if opt_key in seen_option:
                    city_bad = True
                    reason_codes.append("duplicate-option")
                    continue
                seen_option.add(opt_key)
                cost = opt.get("cost")
                try:
                    cost = integer(cost)
                except ValueError:
                    city_bad = True
                    reason_codes.append("cost-invalid")
                    continue
                if cost < 0:
                    city_bad = True
                    reason_codes.append("cost-negative")
                    continue
                if opt.get("trainable") is not True:
                    city_bad = True
                    reason_codes.append("trainable-not-true")
                    continue
                opts_out.append({"unitType": uname, "cost": cost, "trainable": True})
            raw_status = city.get("status")
            # A raw failed/unknown/partial city is never promoted to ok, and its options are
            # withheld: an unavailable domain must not present stale candidates as executable.
            if raw_status not in ("ok", "partial", "failed", "unknown"):
                city_bad = True
                reason_codes.append("city-status-invalid")
            if raw_status != "ok":
                city_bad = True
                reason_codes.append("city-status-" + str(raw_status))
            if raw_status in ("failed", "unknown"):
                opts_out = []
            proj_cities.append({"coords": ccoords, "options": opts_out,
                                "status": "ok" if (raw_status == "ok" and not city_bad) else "partial"})
            if city_bad:
                tstatus = "partial"
        if tstatus == "ok" and (not proj_cities and raw_cities):
            tstatus = "partial"
            reason_codes.append("all-cities-dropped")

    # --- research: keep the safe status, never the native diagnostics -------------------
    # research: an unavailable/partial domain must not reject the legal board either. Items with a
    # null/unresolved tech name are withheld rather than published.
    if rstatus not in ("ok", "partial", "unavailable"):
        rstatus = "unavailable"
    r_items_out = []
    for it in ritems:
        if not isinstance(it, dict):
            if rstatus == "ok":
                rstatus = "partial"
            continue
        nm = it.get("tech")
        cost = it.get("cost")
        if native_identifier(nm) is None:
            if rstatus == "ok":
                rstatus = "partial"
            continue
        try:
            cost = integer(cost)
        except ValueError:
            if rstatus == "ok":
                rstatus = "partial"
            continue
        if cost < 0:
            if rstatus == "ok":
                rstatus = "partial"
            continue
        affordable = it.get("affordable")
        if affordable is not None and not isinstance(affordable, bool):
            if rstatus == "ok":
                rstatus = "partial"
            affordable = None
        r_items_out.append({"tech": nm, "cost": cost, "affordable": affordable})
    if rstatus == "unavailable":
        r_items_out = []
    research_proj = {"status": rstatus, "items": r_items_out}

    # --- legalMoveTargets: explicit whitelist, own units only --------------------------
    own_by_id = {}
    for _u in own_units:
        if _u.get("executionId") is not None:
            own_by_id[str(_u["executionId"])] = _u

    mv_reasons = []
    mv_raw = frame.get("legalMoveTargets")
    if not isinstance(mv_raw, dict):
        move_proj = {"status": "unavailable", "units": [], "reasonCodes": ["legal-move-missing"]}
    elif mv_raw.get("status") not in ("ok", "partial", "unavailable", "withheld"):
        move_proj = {"status": "unavailable", "units": [],
                     "reasonCodes": ["legal-move-status-invalid"]}
    else:
        own_by_id = {}
        for u in own_units:
            if u.get("executionId") is not None:
                own_by_id[str(u["executionId"])] = u
        raw_units = mv_raw.get("units")
        if not isinstance(raw_units, list):
            raw_units = []
            mv_reasons.append("legal-move-units-missing")
        seen_ids, proj_units, ok_units = set(), [], 0
        for u in raw_units:
            entry = {"executionId": None, "from": None, "status": "unavailable", "targets": []}
            if not isinstance(u, dict):
                mv_reasons.append("move-unit-not-a-mapping")
                proj_units.append(entry)
                continue
            uid = u.get("executionId")
            ustatus = u.get("status")
            key = str(uid)
            # The id must belong to THIS frame's own units. An entry that does not match is dropped
            # ENTIRELY: projecting it (even as "withheld") would leak another side's internal id.
            mine = own_by_id.get(key)
            if not isinstance(uid, str) or mine is None:
                mv_reasons.append("move-unit-not-own")
                continue
            entry["executionId"] = uid
            if key in seen_ids:
                entry["status"] = "withheld"
                mv_reasons.append("move-duplicate-unit-id")
                proj_units.append(entry)
                continue
            seen_ids.add(key)
            try:
                frm = parse_coords(u.get("from"))
            except ObservationRejected:
                entry["status"] = "withheld"
                mv_reasons.append("move-from-invalid")
                proj_units.append(entry)
                continue
            if frm != mine.get("coords"):
                entry["status"] = "withheld"
                mv_reasons.append("move-from-mismatch")
                proj_units.append(entry)
                continue
            entry["from"] = frm
            if ustatus != "ok":
                # Only the fixed vocabulary is published: any other raw value is "unavailable".
                entry["status"] = ustatus if ustatus in ("partial", "unavailable", "withheld") \
                    else "unavailable"
                proj_units.append(entry)
                continue
            targets, seen_t, bad = [], set(), False
            raw_targets = u.get("targets")
            if not isinstance(raw_targets, list):
                raw_targets, bad = [], True
                mv_reasons.append("move-targets-missing")
            for t in raw_targets:
                try:
                    tc = parse_coords(t)
                except ObservationRejected:
                    bad = True
                    mv_reasons.append("move-target-invalid")
                    continue
                tkey = tuple(tc)
                if tkey not in explored_coords:
                    bad = True
                    mv_reasons.append("move-target-unexplored")
                    continue
                if tkey in seen_t:
                    bad = True
                    mv_reasons.append("move-target-duplicate")
                    continue
                seen_t.add(tkey)
                targets.append(tc)
            if bad:
                entry["status"] = "withheld"
                mv_reasons.append("move-unit-withheld")
            else:
                entry["status"] = "ok"
                entry["targets"] = targets
                ok_units += 1
            proj_units.append(entry)
        overall = mv_raw.get("status")
        if overall == "ok" and (ok_units != len(proj_units) or mv_reasons):
            overall = "partial"
        move_proj = {"status": overall, "units": proj_units,
                     "reasonCodes": sorted(set(mv_reasons))}

    # --- trainLimits: my own visible cities only ---------------------------------------
    tl_reasons = []
    tl_raw = frame.get("trainLimits")
    if not isinstance(tl_raw, dict):
        limits_proj = {"status": "unavailable", "cities": [], "reasonCodes": ["train-limits-missing"]}
    elif tl_raw.get("status") not in ("ok", "partial", "unavailable"):
        limits_proj = {"status": "unavailable", "cities": [],
                       "reasonCodes": ["train-limits-status-invalid"]}
    else:
        raw_cities = tl_raw.get("cities")
        if not isinstance(raw_cities, list):
            raw_cities = []
            tl_reasons.append("train-limits-cities-missing")
        seen_city, proj_cities, ok_cities = set(), [], 0
        for c in raw_cities:
            if not isinstance(c, dict):
                tl_reasons.append("limit-city-not-a-mapping")
                continue
            try:
                ccoords = parse_coords(c.get("coords"))
            except ObservationRejected:
                tl_reasons.append("limit-city-coords-invalid")
                continue
            ckey = tuple(ccoords)
            if ckey not in explored_coords or ckey not in my_city_coords:
                tl_reasons.append("limit-city-not-my-visible-city")
                continue
            if ckey in seen_city:
                tl_reasons.append("limit-city-duplicate")
                continue
            seen_city.add(ckey)
            count, capacity, can_another = c.get("supportedUnitCount"), c.get("capacity"), \
                c.get("canSupportAnotherUnit")
            good = True
            for value, label in ((count, "count"), (capacity, "capacity")):
                if value is not None and (isinstance(value, bool) or not isinstance(value, int)
                                          or value < 0):
                    good = False
                    tl_reasons.append("limit-%s-invalid" % label)
            if can_another is not None and not isinstance(can_another, bool):
                good = False
                tl_reasons.append("limit-can-support-invalid")
            raw_status = c.get("status")
            if raw_status != "ok":
                good = False
                tl_reasons.append("limit-city-status-not-ok")   # fixed token, no raw echo
            entry = {"coords": ccoords,
                     "supportedUnitCount": count if (isinstance(count, int)
                        and not isinstance(count, bool) and count >= 0) else None,
                     "capacity": capacity if (isinstance(capacity, int)
                        and not isinstance(capacity, bool) and capacity >= 0) else None,
                     "canSupportAnotherUnit": can_another if isinstance(can_another, bool) else None}
            # a determinate count/capacity that contradicts the game's own boolean is withheld
            if entry["supportedUnitCount"] is not None and entry["capacity"] is not None \
                    and entry["canSupportAnotherUnit"] is not None:
                full_by_numbers = entry["supportedUnitCount"] >= entry["capacity"]
                if full_by_numbers == entry["canSupportAnotherUnit"]:
                    good = False
                    entry["canSupportAnotherUnit"] = None
                    tl_reasons.append("limit-capacity-contradicts-flag")
            all_known = (entry["supportedUnitCount"] is not None
                         and entry["capacity"] is not None
                         and entry["canSupportAnotherUnit"] is not None)
            entry["status"] = "ok" if (good and all_known) else "partial"
            if entry["status"] == "ok":
                ok_cities += 1
            proj_cities.append(entry)
        overall = tl_raw.get("status")
        # any dropped/invalid city downgrades the domain: it must not stay a clean ok
        if overall == "ok" and (ok_cities != len(proj_cities) or tl_reasons):
            overall = "partial"
        limits_proj = {"status": overall, "cities": proj_cities,
                       "reasonCodes": sorted(set(tl_reasons))}

    # --- legalAttackTargets: explicit whitelist, own units, CURRENTLY VISIBLE enemies only -----
    at_reasons = []
    own_id_counts = {}
    for _u in own_units:
        _k = _u.get("executionId")
        if _k is not None:
            own_id_counts[str(_k)] = own_id_counts.get(str(_k), 0) + 1
    at_raw = frame.get("legalAttackTargets")
    enemy_coords = {tuple(u["coords"]) for u in visible_enemies if u.get("coords")}
    if not isinstance(at_raw, dict):
        attack_proj = {"status": "unavailable", "units": [], "reasonCodes": ["legal-attack-missing"]}
    elif at_raw.get("status") not in ("ok", "partial", "unavailable", "withheld"):
        attack_proj = {"status": "unavailable", "units": [],
                       "reasonCodes": ["legal-attack-status-invalid"]}
    else:
        raw_units = at_raw.get("units")
        if not isinstance(raw_units, list):
            raw_units = []
            at_reasons.append("legal-attack-units-missing")
        raw_counts = {}
        for _e in raw_units:
            if isinstance(_e, dict) and isinstance(_e.get("executionId"), str):
                raw_counts[_e["executionId"]] = raw_counts.get(_e["executionId"], 0) + 1
        overall_in = at_raw.get("status")
        proj_units, seen_ids, ok_units = [], set(), 0
        for u in raw_units:
            if not isinstance(u, dict):
                at_reasons.append("attack-unit-not-a-mapping")
                continue
            uid = u.get("executionId")
            key = str(uid)
            mine = own_by_id.get(key)
            # an entry that is not a unique own unit is dropped ENTIRELY (no raw id leak)
            if not isinstance(uid, str) or mine is None:
                at_reasons.append("attack-unit-not-own")
                continue
            if key in seen_ids or raw_counts.get(key, 0) > 1:
                at_reasons.append("attack-duplicate-unit-id")
                continue
            if own_id_counts.get(key, 0) != 1:
                # an ambiguous own id can never be certified for an attack
                at_reasons.append("attack-own-id-not-unique")
                continue
            seen_ids.add(key)
            entry = {"executionId": uid, "from": None, "status": "withheld", "targets": []}
            try:
                frm = parse_coords(u.get("from"))
            except ObservationRejected:
                at_reasons.append("attack-from-invalid")
                proj_units.append(entry)
                continue
            if frm != mine.get("coords"):
                at_reasons.append("attack-from-mismatch")
                proj_units.append(entry)
                continue
            entry["from"] = frm
            ustatus = u.get("status")
            if ustatus != "ok":
                entry["status"] = (ustatus if ustatus in ("partial", "unavailable", "withheld")
                                   else "unavailable")
                proj_units.append(entry)
                continue
            raw_targets = u.get("targets")
            targets, seen_t, bad = [], set(), False
            if not isinstance(raw_targets, list):
                raw_targets, bad = [], True
                at_reasons.append("attack-targets-missing")
            for t in raw_targets:
                try:
                    tc = parse_coords(t)
                except ObservationRejected:
                    bad = True
                    at_reasons.append("attack-target-invalid")
                    continue
                tkey = tuple(tc)
                if tkey in seen_t:
                    bad = True
                    at_reasons.append("attack-target-duplicate")
                    continue
                seen_t.add(tkey)
                if tkey not in enemy_coords:
                    # a target that is not a CURRENTLY VISIBLE enemy is never published
                    bad = True
                    at_reasons.append("attack-target-not-currently-visible")
                    continue
                targets.append(tc)
            if overall_in in ("unavailable", "withheld"):
                # the domain is not trustworthy: publish the status but never the targets
                entry["status"] = "unavailable" if overall_in == "unavailable" else "withheld"
                entry["targets"] = []
                at_reasons.append("attack-domain-" + str(overall_in))
                proj_units.append(entry)
                continue
            # Non-empty candidates require known current action permission. An unknown
            # own state is not permission, and moved is not an independent attack predicate.
            gate_ok = (frame.get("canAct") is True and frame.get("myTurn") is True
                       and mine.get("attacked") is False
                       and mine.get("canAttack") is True)
            if targets and not gate_ok:
                # non-empty candidates contradicting the snapshot's own action state are withheld
                entry["status"] = "withheld"
                entry["targets"] = []
                at_reasons.append("attack-candidate-contradicts-action-state")
                proj_units.append(entry)
                continue
            if bad:
                entry["status"] = "withheld"
                at_reasons.append("attack-unit-withheld")
            else:
                entry["status"] = "ok"
                entry["targets"] = targets
                ok_units += 1
            proj_units.append(entry)
        overall = at_raw.get("status")
        if overall == "ok" and (ok_units != len(proj_units) or at_reasons):
            overall = "partial"
        attack_proj = {"status": overall, "units": proj_units,
                       "reasonCodes": sorted(set(at_reasons))}

    # --- public fog marker domains: siege fire + Elyrion ruin rainbow ---------------------------
    # Only coords + displayState + observedTurn/observationRevision may ever be published, and only
    # when the domain is a real `ok` with a real list and consistent metadata. Marker coords are
    # deliberately NOT required to be in `tiles`: this independent signal is never pruned by tile
    # membership, and it never upgrades explored/inSight or adds resources/units/cities.
    _MARKER_DOMAIN_STATUS = ("ok", "partial", "unavailable", "unsupported")
    _MARKER_DOMAIN_REASONS = {
        "marker-domain-missing", "marker-domain-not-a-mapping", "marker-type-mismatch",
        "marker-status-unknown", "markers-not-a-list", "marker-not-a-mapping",
        "marker-coords-invalid", "marker-displaystate-unknown", "marker-duplicate",
        "marker-metadata-mismatch", "marker-domain-metadata-mismatch", "one-or-more-markers-unusable",
    }

    def _fixed_marker_reason(value, fallback):
        return value if isinstance(value, str) and value in _MARKER_DOMAIN_REASONS else fallback

    def _project_marker_domain(raw_domain, expected_type):
        reasons = []
        if raw_domain is None:
            return {"markerType": expected_type, "status": "unavailable", "markers": [],
                    "reasonCodes": ["marker-domain-missing"]}
        if not isinstance(raw_domain, dict):
            return {"markerType": expected_type, "status": "unavailable", "markers": [],
                    "reasonCodes": ["marker-domain-not-a-mapping"]}
        if raw_domain.get("markerType") != expected_type:
            # a domain may never be mapped onto the other domain's identity
            return {"markerType": expected_type, "status": "withheld", "markers": [],
                    "reasonCodes": ["marker-type-mismatch"]}
        rstatus = raw_domain.get("status")
        if rstatus not in _MARKER_DOMAIN_STATUS:
            return {"markerType": expected_type, "status": "unavailable", "markers": [],
                    "reasonCodes": ["marker-status-unknown"]}
        if rstatus in ("unavailable", "unsupported"):
            return {"markerType": expected_type, "status": rstatus, "markers": [],
                    "reasonCodes": [_fixed_marker_reason(raw_domain.get("reasonCode"),
                                                        "one-or-more-markers-unusable")]}
        domain_turn = raw_domain.get("observedTurn", f_turn)
        domain_revision = raw_domain.get("observationRevision", f_rev)
        if (not isinstance(f_turn, int) or isinstance(f_turn, bool) or f_turn < 0
                or not isinstance(f_rev, int) or isinstance(f_rev, bool) or f_rev <= 0
                or not isinstance(domain_turn, int) or isinstance(domain_turn, bool)
                or not isinstance(domain_revision, int) or isinstance(domain_revision, bool)
                or domain_turn != f_turn or domain_revision != f_rev):
            return {"markerType": expected_type, "status": "withheld", "markers": [],
                    "reasonCodes": ["marker-domain-metadata-mismatch"]}
        raw_markers = raw_domain.get("markers")
        if not isinstance(raw_markers, list):
            return {"markerType": expected_type, "status": "withheld", "markers": [],
                    "reasonCodes": ["markers-not-a-list"]}
        markers, seen, bad = [], set(), False
        for m in raw_markers:
            if not isinstance(m, dict):
                bad = True
                reasons.append("marker-not-a-mapping")
                continue
            display = m.get("displayState")
            if display not in ("displayed", "fading"):
                bad = True
                reasons.append("marker-displaystate-unknown")
                continue
            try:
                coord = parse_coords(m.get("coords"))
            except ObservationRejected:
                bad = True
                reasons.append("marker-coords-invalid")
                continue
            key = tuple(coord)
            if key in seen:
                bad = True
                reasons.append("marker-duplicate")
                continue
            seen.add(key)
            mt = m.get("observedTurn")
            mr = m.get("observationRevision")
            if (not isinstance(mt, int) or isinstance(mt, bool) or not isinstance(mr, int)
                    or isinstance(mr, bool) or mt != f_turn or mr != f_rev):
                bad = True
                reasons.append("marker-metadata-mismatch")
                continue
            markers.append({"coords": coord, "displayState": display,
                            "observedTurn": f_turn, "observationRevision": f_rev})
        if bad:
            # a single unusable item withholds the WHOLE domain: never a silent ok-empty, and never
            # one surviving first item pretending the omitted markers disappeared
            return {"markerType": expected_type, "status": "partial", "markers": [],
                    "reasonCodes": sorted(set(reasons)) or ["one-or-more-markers-unusable"]}
        if rstatus == "ok":
            return {"markerType": expected_type, "status": "ok", "markers": markers,
                    "reasonCodes": []}
        # partial from the reader: keep the accurate status but withhold every coordinate
        return {"markerType": expected_type, "status": rstatus, "markers": [],
                "reasonCodes": [_fixed_marker_reason(raw_domain.get("reasonCode"),
                                                    "one-or-more-markers-unusable")]}

    f_turn = frame.get("turn")
    if not isinstance(f_turn, int) or isinstance(f_turn, bool):
        f_turn = None
    f_rev = frame.get("observationRevision")
    if not isinstance(f_rev, int) or isinstance(f_rev, bool):
        f_rev = None
    siege_proj = _project_marker_domain(frame.get("publicSiegeMarkers"), "siege")
    rainbow_proj = _project_marker_domain(frame.get("publicRuinRainbowMarkers"), "ruin_rainbow")

    phase = frame.get("currentState")
    _require(phase is None or native_identifier(phase) is not None, "currentState must be a native name or null")
    return {
        "controlledPlayerId": me_i,
        "matchId": match_ctx,                  # opaque local episode token
        "observationRevision": revision,
        "turn": _opt_int(frame.get("turn")),
        "currentState": phase,
        "matchEnded": _opt_bool(frame.get("matchEnded"), "matchEnded"),
        "completeness": "complete",

        # --- own decision state (legally reported for the controlled player) ---
        "stars": _opt_int_strict(frame.get("stars"), "stars"),
        "ownedTechs": list(owned_techs) if owned_techs is not None else None,
        "ownedTechsStatus": owned_techs_status,
        "legalMoveTargets": move_proj,
        "legalAttackTargets": attack_proj,
        "publicSiegeMarkers": siege_proj,
        "publicRuinRainbowMarkers": rainbow_proj,
        "trainLimits": limits_proj,
        "trainOptions": {"status": tstatus, "cities": proj_cities,
                         "reasonCodes": sorted(set(reason_codes))},
        "researchableTechs": research_proj,
        "resourceCategories": {
            "visible": list(cats["visibleCategories"]),
            "hidden": list(cats.get("hiddenCategories") or []),
            "unknown": list(cats.get("unknownCategories") or []),
        },
        "myTurn": _opt_bool(frame.get("myTurn"), "myTurn"),
        "canAct": _opt_bool(frame.get("canAct"), "canAct"),
        "canActReason": frame.get("canActReason"),
        "pendingTriggerCountMine": _opt_int_strict(frame.get("pendingTriggerCountMine"),
                                                   "pendingTriggerCountMine"),
        "popupShowing": _opt_bool(frame.get("popupShowing"), "popupShowing"),
        "popupNeedsChoice": _opt_bool(frame.get("popupNeedsChoice"), "popupNeedsChoice"),

        # --- legally visible board state ---
        "units": visible_enemies,       # visible enemies only, no internal identity
        "ownUnits": own_units,          # execution identity + own action state
        "resources": resources,         # already filtered by the game's own rule
        "tiles": tiles,
        "improvements": improvements,

        # --- legally visible events (memory may only confirm a kill from such an event) ---
        "destroyed": destroyed,

        # --- fields outside this bare-world projection; see UNIMPLEMENTED_DECISION_FIELDS ---
        "unimplemented": list(UNIMPLEMENTED_DECISION_FIELDS),
    }
