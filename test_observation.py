"""Production-path acceptance tests: normalize_world -> EnemyMemory (codex-dsh-007).

Everything here goes through the REAL production path (observation.normalize_world then
EnemyMemory.observe) with frames in the same shape `world` emits. No game is started.
Run: python test_observation.py   (exit 0 = all pass)
"""
import copy
import json
import sys

from observation import normalize_world, ObservationRejected, parse_coords
from sightings import EnemyMemory, StaleObservation, STATUS_DESTROYED, STATUS_UNOBSERVED

_p = _f = 0


def check(cond, label, detail=None):
    global _p, _f
    if cond:
        _p += 1
        print("PASS  " + label)
    else:
        _f += 1
        print("FAIL  " + label + ("" if detail is None else "  :: %s" % (detail,)))


def world_frame(units=(), resources=(), turn=10, rev=1, me=1, ctx="ep-0f3a9c21b7d4e5f6",
                completeness="complete", unit_status="complete", resource_status="complete",
                identity=True, ok=True, **extra):
    frame = {
        "ok": ok,
        "controlledIdentityVerified": identity,
        "controlledIdentitySource": "GameManager.LocalPlayer",
        "observationRevision": rev,
        "matchContext": ctx,
        "completeness": completeness,
        "localPlayerId": me,
        "turn": turn,
        "units": list(units),
        "resources": list(resources),
        "improvements": [{"coords": "(1,1)", "owner": me, "isMine": True,
                          "improvementType": "City", "inSight": True, "level": "2",
                          "population": "1"}],
        "tiles": [{"c": "(1,1)", "terrain": "Field", "owner": me},
                  {"c": "(2,2)", "terrain": "Forest", "owner": 0}],
        "hiddenEntityPolicy": "hidden_entities_never_reported",   # legal constant metadata
        "resourceVisibilityAuthority": "TileData.GetResource(gameState, playerId)",
        "resourceCategoryPermissions": {"status": "ok", "visibleCategories": ["Fish", "Fruit", "Game"],
                                       "hiddenCategories": ["Crop", "Metal", "Starfish"],
                                       "unknownCategories": [], "categoryCount": 9, "definiteCount": 9},
        "trainOptions": {"status": "ok", "cities": [{"coords": "(1,1)", "status": "ok", "options": [
            {"unitType": "Warrior", "cost": 2, "trainable": True}]}]},
        "researchableTechs": {"status": "ok", "authority": "game instance", "count": 1,
                             "items": [{"tech": "Organization", "cost": 5, "costStatus": "ok",
                                       "affordable": None}]},
        "visibilityFilters": {
            "unit": {"signatureAvailable": True, "attempted": 41, "succeeded": 41, "failed": 0,
                     "status": unit_status},
            "resource": {"signatureAvailable": True, "attempted": 41, "succeeded": 41, "failed": 0,
                         "status": resource_status},
            "snapshotStatus": completeness,
        },
    }
    frame.update(extra)
    return frame


def unit(owner=2, utype="Warrior", coords="(5,5)", hp="100", uid="42", moved=False, attacked=False):
    return {"id": uid, "owner": owner, "type": utype, "coords": coords, "health": hp,
            "moved": moved, "attacked": attacked, "canMove": True, "canAttack": True}


def res(coords="(3,3)", rtype="Crop", owner=1):
    return {"coords": coords, "resourceType": rtype, "owner": owner, "inSight": True}


def main():
    print("=== normalizer: strict schema, no partial results ===")
    check(parse_coords("(5,5)") == [5, 5], "coords parse", None)
    for bad in ("5,5", "(5)", "(a,b)", None, 5):
        try:
            parse_coords(bad)
            check(False, "bad coords rejected: %r" % (bad,))
        except ObservationRejected:
            check(True, "bad coords rejected: %r" % (bad,))

    o = normalize_world(world_frame(units=[unit()]))
    check(o["units"][0]["unitType"] == "Warrior" and o["units"][0]["hp"] == 100,
        "type/health are mapped to unitType/hp", o["units"])
    check(o["units"][0]["coords"] == [5, 5], "string coords become an int pair", o["units"])
    check("id" not in o["units"][0] and "executionId" not in o["units"][0],
        "an enemy's internal id is not carried into the planner view", o["units"])

    own = normalize_world(world_frame(units=[unit(owner=1, uid="7")]))
    check(own["ownUnits"][0]["executionId"] == "7",
        "own units keep an execution identity", own["ownUnits"])
    check(o["controlledPlayerId"] == 1 and o["matchId"] == "ep-0f3a9c21b7d4e5f6"
          and o["observationRevision"] == 1, "identity/match/revision are carried", None)

    print("=== normalizer: rejects without producing a partial observation ===")
    reject_cases = [
        ("identity unverified", world_frame(identity=False)),
        ("world not ok", world_frame(ok=False)),
        ("missing matchContext", world_frame(ctx="")),
        ("completeness incomplete", world_frame(completeness="incomplete")),
        ("unit filter failed", world_frame(unit_status="failed")),
        ("unit filter signature_unavailable", world_frame(unit_status="signature_unavailable")),
        ("resource filter not_attempted", world_frame(resource_status="not_attempted")),
        ("revision missing", world_frame(rev=None)),
        ("omniscient payload", world_frame(omniscient=True)),
        ("hidden entities", world_frame(hiddenUnits=[1, 2])),
        ("bad unit coords", world_frame(units=[unit(coords="nope")])),
    ]
    for label, frame in reject_cases:
        try:
            normalize_world(frame)
            check(False, "rejected: %s" % label)
        except ObservationRejected:
            check(True, "rejected: %s" % label)
    # the constant policy marker is NOT audit truth
    try:
        normalize_world(world_frame(units=[unit()]))
        check(True, "constant hiddenEntityPolicy metadata is accepted")
    except ObservationRejected as exc:
        check(False, "constant hiddenEntityPolicy metadata is accepted", str(exc))

    print("=== memory: partial/failed observations never freeze or clear anything ===")
    m = EnemyMemory("ep-0f3a9c21b7d4e5f6", 1)
    m.observe(normalize_world(world_frame(units=[unit()], turn=10, rev=1)))
    check(len(m.enemy_memory()) == 1, "baseline sighting recorded", None)
    for label, frame in (("filter failed", world_frame(units=[], turn=11, rev=2, unit_status="failed")),
                         ("incomplete", world_frame(units=[], turn=11, rev=2, completeness="incomplete")),
                         ("identity unverified", world_frame(units=[], turn=11, rev=2, identity=False))):
        try:
            m.observe(normalize_world(frame))
            check(False, "memory untouched by %s" % label)
        except ObservationRejected:
            d = m.enemy_memory()[0]
            check(d["status"] == "visible" and d["lastSeenTurn"] == 10,
                  "memory untouched by %s (not read as lost sight)" % label, d)

    print("=== revisions: duplicate is idempotent, older is rejected ===")
    m.observe(normalize_world(world_frame(units=[unit(hp="90")], turn=10, rev=1)))
    check(m.enemy_memory()[0]["hp"] == 100, "a duplicate revision does not re-apply", m.enemy_memory())
    # Consecutive revisions only: a gap means the stream was not applied continuously, and a
    # "continuity" claim would then be an inference rather than evidence.
    m.observe(normalize_world(world_frame(units=[unit(hp="80")], turn=11, rev=2)))
    check(m.enemy_memory()[0]["hp"] == 80 and m.enemy_memory()[0]["status"] == "visible",
          "a newer CONTIGUOUS revision refreshes hp", m.enemy_memory())
    try:
        m.observe(normalize_world(world_frame(units=[unit(hp="20")], turn=10, rev=1)))
        check(False, "an older revision is rejected")
    except StaleObservation:
        d = m.enemy_memory()[0]
        check(d["hp"] == 80 and d["lastSeenObservationRevision"] == 2,
              "an older revision cannot roll back hp/revision", d)

    print("=== a revision gap downgrades continuity to a candidate ===")
    m2g = EnemyMemory("ep-0f3a9c21b7d4e5f6", 1)
    m2g.observe(normalize_world(world_frame(units=[unit()], turn=10, rev=1)))
    m2g.observe(normalize_world(world_frame(units=[unit(hp="70")], turn=13, rev=4)))
    memg = m2g.enemy_memory()
    check(len(memg) == 2 and memg[1]["association"] == "candidate",
          "skipped revisions cannot claim continuity; the new record is a candidate", memg)
    check(memg[0]["lastSeenTurn"] == 10 and memg[0]["hp"] == 100,
          "the old record keeps its frozen values", memg[0])

    print("=== losing sight freezes; same tile later is a CANDIDATE, not a confirmation ===")
    m2 = EnemyMemory("ep-0f3a9c21b7d4e5f6", 1)
    m2.observe(normalize_world(world_frame(units=[unit()], turn=10, rev=1)))
    m2.observe(normalize_world(world_frame(units=[], turn=11, rev=2)))
    d = m2.enemy_memory()[0]
    check(d["status"] == STATUS_UNOBSERVED and d["observationLost"] is True,
          "losing sight freezes the record", d)
    m2.observe(normalize_world(world_frame(units=[unit(uid="99", hp="100")], turn=12, rev=3)))
    mem = m2.enemy_memory()
    check(len(mem) == 2, "same tile/type/owner after a gap creates a NEW sighting", mem)
    check(mem[1]["association"] == "candidate" and mem[1]["candidateOf"] == 1,
          "the new record is an explicitly labelled candidate", mem[1])
    check(mem[0]["status"] == STATUS_UNOBSERVED and mem[0]["lastSeenTurn"] == 10,
          "the old record stays frozen with its original last-seen turn", mem[0])
    check(all("traceToken" not in x and "traceTokenDebug" not in x for x in mem),
          "no raw trace token is exposed to the planner", mem)

    print("=== confirmedDestroyed is terminal and cannot be revived ===")
    m3 = EnemyMemory("ep-0f3a9c21b7d4e5f6", 1)
    m3.observe(normalize_world(world_frame(units=[unit()], turn=10, rev=1)))
    m3.observe(normalize_world(world_frame(
        units=[], turn=11, rev=2,
        destroyed=[{"owner": 2, "unitType": "Warrior", "coords": "(5,5)"}])))
    check(m3.enemy_memory()[0]["status"] == STATUS_DESTROYED, "a legally associated kill is confirmed",
          m3.enemy_memory())
    m3.observe(normalize_world(world_frame(units=[unit(uid="77")], turn=12, rev=3)))
    mem = m3.enemy_memory()
    check(mem[0]["status"] == STATUS_DESTROYED, "the destroyed record is never revived", mem[0])
    check(len(mem) == 2 and mem[1]["association"] == "candidate",
          "the re-appearance is a new candidate sighting", mem)

    print("=== confirmedDestroyed requires a just-observed record; ambiguous stays unresolved ===")
    m3b = EnemyMemory("ep-0f3a9c21b7d4e5f6", 1)
    m3b.observe(normalize_world(world_frame(units=[unit()], turn=10, rev=1)))
    m3b.observe(normalize_world(world_frame(units=[], turn=11, rev=2, destroyed=[
        {"owner": 2, "unitType": "Warrior", "coords": "(5,5)"}])))
    check(m3b.enemy_memory()[0]["status"] == STATUS_DESTROYED,
          "a kill event in the revision right after the sighting is associated", m3b.enemy_memory())

    m4 = EnemyMemory("ep-0f3a9c21b7d4e5f6", 1)
    m4.observe(normalize_world(world_frame(units=[unit()], turn=10, rev=1)))
    m4.observe(normalize_world(world_frame(units=[], turn=11, rev=2)))
    m4.observe(normalize_world(world_frame(units=[], turn=12, rev=3, destroyed=[
        {"owner": 2, "unitType": "Warrior", "coords": "(9,9)"}])))
    check(m4.enemy_memory()[0]["status"] == STATUS_UNOBSERVED,
          "a destruction event at another place changes nothing", m4.enemy_memory())
    check(len(m4.unresolved_destroyed_events) == 1, "it is kept as unresolved instead", None)
    # a gap between the sighting and the kill event means the identity link is not supportable
    m4b = EnemyMemory("ep-0f3a9c21b7d4e5f6", 1)
    m4b.observe(normalize_world(world_frame(units=[unit()], turn=10, rev=1)))
    m4b.observe(normalize_world(world_frame(units=[], turn=11, rev=2)))
    m4b.observe(normalize_world(world_frame(units=[], turn=12, rev=3, destroyed=[
        {"owner": 2, "unitType": "Warrior", "coords": "(5,5)"}])))
    check(m4b.enemy_memory()[0]["status"] == STATUS_UNOBSERVED
          and len(m4b.unresolved_destroyed_events) == 1,
          "a kill after a gap is NOT tied to that sighting", m4b.enemy_memory())

    print("=== isolation: invalid identity must not clear, a real switch must ===")
    m5 = EnemyMemory("ep-0f3a9c21b7d4e5f6", 1)
    m5.observe(normalize_world(world_frame(units=[unit()], turn=10, rev=1)))
    try:
        m5.observe(normalize_world(world_frame(units=[], turn=11, rev=2, identity=False)))
    except ObservationRejected:
        pass
    check(len(m5.enemy_memory()) == 1 and m5.reset_reason is None,
          "an unverified identity raises without clearing memory", m5.reset_reason)
    m5.observe(normalize_world(world_frame(units=[], turn=1, rev=1, ctx="ep-9a1b2c3d4e5f6071")))
    check(len(m5.enemy_memory()) == 0 and m5.reset_reason == "identity_or_match_changed",
          "a different match resets memory", m5.enemy_memory())

    print("=== same-turn tech effect: only a complete legal observation discloses resources ===")
    no_tech = normalize_world(world_frame(resources=[], turn=10, rev=1))
    check(no_tech["resources"] == [], "nothing visible without the tech", no_tech["resources"])
    after = normalize_world(world_frame(resources=[res(rtype="Crop")], turn=10, rev=2))
    check(len(after["resources"]) == 1 and after["resources"][0]["resourceType"] == "Crop",
          "the next observation in the SAME turn discloses it", after["resources"])
    pending = world_frame(resources=[res(rtype="Crop")], turn=10, rev=3)
    pending["completeness"] = "incomplete"
    try:
        normalize_world(pending)
        check(False, "an unconfirmed/incomplete observation does not disclose")
    except ObservationRejected:
        check(True, "an unconfirmed/incomplete observation does not disclose")
    unexplored = world_frame(resources=[], turn=10, rev=4)
    check(normalize_world(unexplored)["tiles"] and
          all(t["coords"] != [7, 7] for t in normalize_world(unexplored)["tiles"]),
          "unexplored tiles are simply absent (no resource knowledge is inferred)", None)

    print("=== no hidden-resource leak through the shape of the response ===")
    empty = normalize_world(world_frame(resources=[]))
    hidden = normalize_world(world_frame(resources=[]))   # hidden resource present in truth, filtered out
    check(empty["resources"] == hidden["resources"] == [],
          "present-but-hidden and absent produce the identical legal response", None)

    print("=== episode identity: opaque, no seed, no cross-game sharing ===")
    probe = normalize_world(world_frame(units=[unit()]))
    joined = json.dumps(probe)
    check("seed" not in joined.lower(), "the normalized context carries no seed", None)
    check(probe["matchId"].startswith("ep-"), "episode id is the opaque local token", probe["matchId"])
    check("0x" not in joined and "Pointer" not in joined, "no raw pointer is exposed", None)
    mA = EnemyMemory("ep-AAAAAAAAAAAAAAAA", 1)
    mA.observe(normalize_world(world_frame(units=[unit()], ctx="ep-AAAAAAAAAAAAAAAA", rev=1)))
    before = len(mA.enemy_memory())
    mA.observe(normalize_world(world_frame(units=[unit()], ctx="ep-BBBBBBBBBBBBBBBB", rev=1)))
    check(before == 1 and len(mA.enemy_memory()) == 1
          and mA.reset_reason == "identity_or_match_changed",
          "a different episode resets memory instead of inheriting sightings", mA.reset_reason)
    try:
        normalize_world(world_frame(ctx="seed=7"))
        check(False, "a seed-shaped context is refused")
    except ObservationRejected:
        check(True, "a seed-shaped context is refused")

    print("=== the standard observation keeps the legitimate decision fields ===")
    rich = world_frame(units=[], turn=12, rev=1)
    rich["stars"] = "15"
    rich["ownedTechs"] = ["Farming", "Riding"]
    rich["myTurn"] = True
    rich["canAct"] = False
    rich["canActReason"] = "a popup is open"
    rich["popupShowing"] = True
    rich["popupNeedsChoice"] = True
    rich["pendingTriggerCountMine"] = 1
    mine = unit(owner=1, uid="7")
    mine["canMove"] = True
    mine["canAttack"] = False
    mine["promotionLevel"] = "2"
    rich["units"] = [mine]
    rich["improvements"] = [{"coords": "(1,1)", "owner": 1, "isMine": True,
                             "improvementType": "City", "inSight": True, "level": "2",
                             "population": "1"}]
    rich["resources"] = [res()]
    rich["tiles"] = [{"c": "(1,1)", "terrain": "Field", "owner": 1, "water": False, "inSight": True}]
    ro = normalize_world(rich)
    for field in ("stars", "ownedTechs", "myTurn", "canAct", "canActReason", "popupShowing",
                  "popupNeedsChoice", "pendingTriggerCountMine", "improvements", "tiles", "resources"):
        check(field in ro, "decision field retained: %s" % field, None)
    check(ro["stars"] == 15 and ro["ownedTechs"] == ["Farming", "Riding"],
          "stars/ownedTechs mapped", (ro["stars"], ro["ownedTechs"]))
    check(ro["canAct"] is False and ro["popupShowing"] is True,
          "canAct/popup mapped as booleans", None)
    ou = ro["ownUnits"][0]
    check(ou["canMove"] is True and ou["canAttack"] is False and ou["moved"] is False,
          "own unit action fields are real booleans", ou)
    check(ou["promotionLevel"] == 2, "own unit promotion level mapped to int", ou)
    check(ro["improvements"][0]["isMine"] is True and ro["improvements"][0]["level"] == 2,
          "improvement flags/value mapped", ro["improvements"][0])
    check(isinstance(ro["unimplemented"], list)
          and "legalMoveTargets" not in ro["unimplemented"]
          and "trainLimits" not in ro["unimplemented"]
          and "legalAttackTargets" not in ro["unimplemented"]
          and "legalBuildOptions" in ro["unimplemented"]
          and "terminalState" not in ro["unimplemented"]
          and "unitMaxHealth" not in ro["unimplemented"],
          "unimplemented list matches reality (implemented fields removed)", ro["unimplemented"])
    for bad in ({"myTurn": "true"}, {"canAct": "false"}, {"popupShowing": "True"}):
        frame = world_frame()
        frame.update(bad)
        try:
            normalize_world(frame)
            check(False, "non-boolean flag refused: %s" % list(bad.keys()))
        except ObservationRejected:
            check(True, "non-boolean flag refused: %s" % list(bad.keys()))

    print("=== sample-to-sample enemy identity is an assumption, never a proof ===")
    m5b = EnemyMemory("ep-0f3a9c21b7d4e5f6", 1)
    m5b.observe(normalize_world(world_frame(units=[unit()], turn=10, rev=1)))
    m5b.observe(normalize_world(world_frame(units=[unit()], turn=11, rev=2)))
    rec = m5b.enemy_memory()[0]
    check(rec["identityCertainty"] == "assumed",
          "a contiguous sample link is labelled as an assumption", rec)
    check("assumed" in (rec["associationEvidence"] or ""),
          "the evidence string says assumption, not proof", rec)
    check(rec["association"] != "confirmed", "no sighting claims a confirmed identity",
          rec["association"])
    m5b.observe(normalize_world(world_frame(units=[], turn=12, rev=3)))
    m5b.observe(normalize_world(world_frame(units=[unit()], turn=13, rev=4)))
    recs = m5b.enemy_memory()
    check(len(recs) == 2 and recs[1]["association"] == "candidate"
          and recs[1]["identityCertainty"] == "assumed",
          "a re-appearance after a gap is a candidate with an assumed identity", recs)

    print("=== trainOptions: explicit whitelist projection (codex-dsh-014) ===")
    tf = normalize_world(world_frame())
    check("trainOptions" in tf, "trainOptions is projected to the planner", None)
    check(tf["trainOptions"]["status"] == "ok" and tf["trainOptions"]["cities"]
          and tf["trainOptions"]["cities"][0]["coords"] == [1, 1],
          "a real own explored City option survives the projection", tf["trainOptions"])
    check(tf["trainOptions"]["cities"][0]["options"][0]["cost"] == 2
          and tf["trainOptions"]["cities"][0]["options"][0]["trainable"] is True,
          "unitType/cost/trainable are real values", tf["trainOptions"]["cities"][0]["options"])
    bad = world_frame()
    bad["trainOptions"]["cities"][0]["coords"] = "T1"
    bp = normalize_world(bad)
    check(bp["trainOptions"]["status"] == "partial" and bp["trainOptions"]["cities"] == [],
          "an unverified city identifier (T1) never reaches the planner", bp["trainOptions"])
    other = world_frame()
    other["trainOptions"]["cities"][0]["coords"] = "(2,2)"       # explored but NOT my City
    op = normalize_world(other)
    check(op["trainOptions"]["cities"] == [] and op["trainOptions"]["status"] == "partial",
          "an explored tile that is not my City is dropped", op["trainOptions"])
    bcost = world_frame()
    bcost["trainOptions"]["cities"][0]["options"][0]["cost"] = True
    cp = normalize_world(bcost)
    check(cp["trainOptions"]["cities"] and not cp["trainOptions"]["cities"][0]["options"]
          and cp["trainOptions"]["status"] == "partial",
          "a boolean cost is refused and the domain degrades to partial", cp["trainOptions"])
    un = world_frame()
    un["trainOptions"] = {"status": "unavailable"}
    up = normalize_world(un)
    check(up["trainOptions"]["status"] == "unavailable" and up["tiles"] and len(up["tiles"]) == 2,
          "an unavailable train domain keeps the legal map observation", up["trainOptions"])
    miss = world_frame()
    del miss["trainOptions"]
    mp = normalize_world(miss)
    check(mp["trainOptions"]["status"] == "unavailable"
          and "train-options-missing" in mp["trainOptions"]["reasonCodes"] and mp["tiles"],
          "a missing train domain is reported, not fatal", mp["trainOptions"])
    check("reason" not in mp["researchableTechs"],
          "no native research diagnostic is projected", list(mp["researchableTechs"].keys()))


    print("=== legalMoveTargets: UI highlight set, own units only (codex-dsh-019) ===")
    mvf = world_frame(units=[{"id": "7", "owner": 1, "type": "Warrior", "coords": "(1,1)",
                              "health": "10", "moved": False, "attacked": False,
                              "canMove": True, "canAttack": True}])
    mvf["legalMoveTargets"] = {"status": "ok", "units": [
        {"executionId": "7", "from": "(1,1)", "status": "ok", "targets": ["(2,2)"]}]}
    mp = normalize_world(mvf)
    check(mp["legalMoveTargets"]["status"] == "ok"
          and mp["legalMoveTargets"]["units"]
          and mp["legalMoveTargets"]["units"][0]["targets"] == [[2, 2]],
          "a real own-unit highlight set reaches the planner", mp["legalMoveTargets"])

    bad_id = world_frame(units=[{"id": "7", "owner": 1, "type": "Warrior", "coords": "(1,1)",
                                 "health": "10", "moved": False, "attacked": False,
                                 "canMove": True, "canAttack": True}])
    bad_id["legalMoveTargets"] = {"status": "ok", "units": [
        {"executionId": "99", "from": "(1,1)", "status": "ok", "targets": []}]}
    bp2 = normalize_world(bad_id)
    check(bp2["legalMoveTargets"]["units"] == []
          and bp2["legalMoveTargets"]["status"] != "ok",
          "an id that is not an own unit is dropped entirely, never an ok empty set",
          bp2["legalMoveTargets"])

    bad_from = world_frame(units=[{"id": "7", "owner": 1, "type": "Warrior", "coords": "(1,1)",
                                   "health": "10", "moved": False, "attacked": False,
                                   "canMove": True, "canAttack": True}])
    bad_from["legalMoveTargets"] = {"status": "ok", "units": [
        {"executionId": "7", "from": "(2,2)", "status": "ok", "targets": []}]}
    check(normalize_world(bad_from)["legalMoveTargets"]["units"][0]["status"] == "withheld",
          "a from that is not the unit's real coords is withheld", None)

    unexpl = world_frame(units=[{"id": "7", "owner": 1, "type": "Warrior", "coords": "(1,1)",
                                 "health": "10", "moved": False, "attacked": False,
                                 "canMove": True, "canAttack": True}])
    unexpl["legalMoveTargets"] = {"status": "ok", "units": [
        {"executionId": "7", "from": "(1,1)", "status": "ok", "targets": ["(9,9)"]}]}
    up2 = normalize_world(unexpl)
    check(up2["legalMoveTargets"]["units"][0]["status"] == "withheld"
          and up2["legalMoveTargets"]["units"][0]["targets"] == [],
          "an unexplored target withholds the unit instead of publishing it", up2["legalMoveTargets"])

    nullmv = world_frame()
    nmp = normalize_world(nullmv)
    check(nmp["legalMoveTargets"]["status"] == "unavailable" and nmp["tiles"],
          "a missing move domain stays unavailable and keeps the board", nmp["legalMoveTargets"])

    nonok = world_frame(units=[{"id": "7", "owner": 1, "type": "Warrior", "coords": "(1,1)",
                                "health": "10", "moved": False, "attacked": False,
                                "canMove": True, "canAttack": True}])
    nonok["legalMoveTargets"] = {"status": "ok", "units": [
        {"executionId": "7", "from": "(1,1)", "status": "unavailable", "targets": []}]}
    np2 = normalize_world(nonok)
    check(np2["legalMoveTargets"]["status"] == "partial"
          and np2["legalMoveTargets"]["units"][0]["status"] == "unavailable",
          "a non-ok unit keeps its status, never an ok empty set", np2["legalMoveTargets"])

    print("=== trainLimits: my own visible cities only (codex-dsh-019) ===")
    tlf = world_frame()
    tlf["trainLimits"] = {"status": "ok", "cities": [
        {"coords": "(1,1)", "status": "ok", "supportedUnitCount": 1, "capacity": 3,
         "canSupportAnotherUnit": True}]}
    tp = normalize_world(tlf)
    check(tp["trainLimits"]["status"] == "ok"
          and tp["trainLimits"]["cities"][0]["capacity"] == 3
          and tp["trainLimits"]["cities"][0]["canSupportAnotherUnit"] is True,
          "my own city capacity/occupancy reaches the planner", tp["trainLimits"])

    tlother = world_frame()
    tlother["trainLimits"] = {"status": "ok", "cities": [
        {"coords": "(2,2)", "status": "ok", "supportedUnitCount": 1, "capacity": 3,
         "canSupportAnotherUnit": True}]}
    op2 = normalize_world(tlother)
    check(op2["trainLimits"]["cities"] == [] and op2["trainLimits"]["status"] == "partial",
          "another player's / non-City tile never enters trainLimits", op2["trainLimits"])

    tlb = world_frame()
    tlb["trainLimits"] = {"status": "ok", "cities": [
        {"coords": "(1,1)", "status": "ok", "supportedUnitCount": True, "capacity": 3,
         "canSupportAnotherUnit": True}]}
    tbp = normalize_world(tlb)
    check(tbp["trainLimits"]["cities"][0]["supportedUnitCount"] is None
          and tbp["trainLimits"]["cities"][0]["status"] == "partial",
          "a boolean count is refused (never read as 1)", tbp["trainLimits"]["cities"][0])

    tlu = world_frame()
    tlu["trainLimits"] = {"status": "ok", "cities": [
        {"coords": "(1,1)", "status": "ok", "supportedUnitCount": None, "capacity": None,
         "canSupportAnotherUnit": None}]}
    tup = normalize_world(tlu)
    check(tup["trainLimits"]["cities"][0]["status"] == "partial"
          and tup["trainLimits"]["status"] == "partial",
          "unknown occupancy never claims the city is full", tup["trainLimits"])

    tlm = world_frame()
    tlm["trainLimits"] = {"status": "unavailable"}
    tmp = normalize_world(tlm)
    check(tmp["trainLimits"]["status"] == "unavailable" and tmp["tiles"],
          "an unavailable limits domain keeps the legal board", tmp["trainLimits"])

    resfilter = world_frame()
    resfilter["visibilityFilters"]["resource"]["failed"] = 1
    resfilter["visibilityFilters"]["resource"]["status"] = "failed"
    try:
        normalize_world(resfilter)
        check(False, "a failed resource filter still rejects the whole board")
    except ObservationRejected:
        check(True, "a failed resource filter still rejects the whole board")



    print("=== explicit-whitelist failure paths (codex-dsh-020) ===")
    def _mvframe(uid="7", myid="7", frm="(1,1)"):
        f = world_frame(units=[{"id": myid, "owner": 1, "type": "Warrior", "coords": "(1,1)",
                                "health": "10", "moved": False, "attacked": False,
                                "canMove": True, "canAttack": True}])
        f["legalMoveTargets"] = {"status": "ok", "units": [
            {"executionId": uid, "from": frm, "status": "ok", "targets": []}]}
        return f

    alien = normalize_world(_mvframe(uid="ENEMY-42"))
    check(alien["legalMoveTargets"]["units"] == []
          and alien["legalMoveTargets"]["status"] != "ok",
          "an unmatched id is DROPPED entirely, never echoed to the planner",
          alien["legalMoveTargets"])
    weird = world_frame(units=[{"id": "7", "owner": 1, "type": "Warrior", "coords": "(1,1)",
                                "health": "10", "moved": False, "attacked": False,
                                "canMove": True, "canAttack": True}])
    weird["legalMoveTargets"] = {"status": "ok", "units": [
        {"executionId": "7", "from": "(1,1)", "status": "RAW-DIAGNOSTIC", "targets": []}]}
    wp = normalize_world(weird)
    check(wp["legalMoveTargets"]["units"][0]["status"] == "unavailable",
          "a non-enum unit status becomes unavailable, never a raw passthrough", wp)

    negl = world_frame()
    negl["trainLimits"] = {"status": "ok", "cities": [
        {"coords": "(1,1)", "status": "ok", "supportedUnitCount": -3, "capacity": 3,
         "canSupportAnotherUnit": True}]}
    np = normalize_world(negl)
    check(np["trainLimits"]["cities"][0]["supportedUnitCount"] is None,
          "a negative count becomes null, not a leaked negative", np["trainLimits"]["cities"][0])

    rm = world_frame()
    del rm["researchableTechs"]
    rp = normalize_world(rm)
    check(rp["researchableTechs"]["status"] == "unavailable" and rp["tiles"],
          "a missing research domain is withheld, not a board rejection", rp["researchableTechs"])

    rneg = world_frame()
    rneg["researchableTechs"]["items"][0]["cost"] = -5
    check(not normalize_world(rneg)["researchableTechs"]["items"],
          "a negative research cost is withheld", None)

    raf = world_frame()
    raf["researchableTechs"]["items"][0]["affordable"] = "True"
    rafo = normalize_world(raf)
    check(rafo["researchableTechs"]["items"][0]["affordable"] is None
          and rafo["researchableTechs"]["status"] == "partial",
          "a non-boolean affordable is nulled and the domain degrades",
          rafo["researchableTechs"])



    print("=== legalAttackTargets: UI chain, visible enemies only (codex-dsh-025) ===")
    def _atframe(enemy=True, tgt="(2,2)", uid="7", frm="(1,1)", ustatus="ok", extra_unit=None):
        units = [{"id": "7", "owner": 1, "type": "Warrior", "coords": "(1,1)", "health": "10",
                  "moved": False, "attacked": False, "canMove": True, "canAttack": True}]
        if enemy:
            units.append({"id": "42", "owner": 2, "type": "Warrior", "coords": "(2,2)",
                          "health": "10", "moved": False, "attacked": False, "canMove": True,
                          "canAttack": True})
        f = world_frame(units=units, canAct=True, myTurn=True)
        f["legalAttackTargets"] = {"status": "ok", "units": [
            {"executionId": uid, "from": frm, "status": ustatus, "targets": [tgt]}]}
        return f

    good = normalize_world(_atframe())
    check(good["legalAttackTargets"]["status"] == "ok"
          and good["legalAttackTargets"]["units"][0]["targets"] == [[2, 2]],
          "a real own-unit attack target on a VISIBLE enemy reaches the planner",
          good["legalAttackTargets"])

    blind = normalize_world(_atframe(enemy=False))
    check(blind["legalAttackTargets"]["units"][0]["status"] == "withheld"
          and blind["legalAttackTargets"]["units"][0]["targets"] == []
          and blind["legalAttackTargets"]["status"] != "ok",
          "a target that is not currently visible is withheld, never published",
          blind["legalAttackTargets"])

    alien = normalize_world(_atframe(uid="ENEMY-77"))
    check(alien["legalAttackTargets"]["units"] == [],
          "a non-own execution id is dropped entirely (no raw id leak)", alien["legalAttackTargets"])

    badfrom = normalize_world(_atframe(frm="(3,3)"))
    check(badfrom["legalAttackTargets"]["units"][0]["status"] == "withheld",
          "a from that is not the unit's real coords is withheld", badfrom["legalAttackTargets"])

    weird = normalize_world(_atframe(ustatus="RAW-DIAGNOSTIC"))
    check(weird["legalAttackTargets"]["units"][0]["status"] == "unavailable",
          "a non-enum unit status becomes unavailable", weird["legalAttackTargets"])

    empty = world_frame(units=[{"id": "7", "owner": 1, "type": "Warrior", "coords": "(1,1)",
                                "health": "10", "moved": True, "attacked": True,
                                "canMove": False, "canAttack": False}])
    empty["legalAttackTargets"] = {"status": "ok", "units": [
        {"executionId": "7", "from": "(1,1)", "status": "ok", "targets": []}]}
    ep = normalize_world(empty)
    check(ep["legalAttackTargets"]["status"] == "ok"
          and ep["legalAttackTargets"]["units"][0]["targets"] == [],
          "a genuinely empty attack menu is ok with an empty list", ep["legalAttackTargets"])

    miss = world_frame()
    mp = normalize_world(miss)
    check(mp["legalAttackTargets"]["status"] == "unavailable" and mp["tiles"],
          "a missing attack domain keeps the legal board", mp["legalAttackTargets"])



    print("=== attack projection boundaries (codex-dsh-026) ===")
    def _at(units, targets=None, at_status="ok", can_act=True, my_turn=True, dupe_own=False):
        own = [{"id": "7", "owner": 1, "type": "Warrior", "coords": "(1,1)", "health": "10",
                "moved": False, "attacked": False, "canMove": True, "canAttack": True}]
        if dupe_own:
            own.append(dict(own[0]))
        own.append({"id": "42", "owner": 2, "type": "Warrior", "coords": "(2,2)", "health": "10",
                    "moved": False, "attacked": False, "canMove": True, "canAttack": True})
        f = world_frame(units=own)
        f["canAct"] = can_act
        f["myTurn"] = my_turn
        f["legalAttackTargets"] = {"status": at_status, "units": units}
        return f

    entry = {"executionId": "7", "from": "(1,1)", "status": "ok", "targets": ["(2,2)"]}
    dupentry = normalize_world(_at([entry, dict(entry)]))
    check(all(u["status"] != "ok" for u in dupentry["legalAttackTargets"]["units"])
          and dupentry["legalAttackTargets"]["status"] != "ok",
          "a duplicated raw entry id leaves NO ok target", dupentry["legalAttackTargets"])
    dupown = normalize_world(_at([entry], dupe_own=True))
    check(all(u["status"] != "ok" for u in dupown["legalAttackTargets"]["units"]),
          "a duplicated OWN unit id is never certified for attack", dupown["legalAttackTargets"])
    down = normalize_world(_at([entry], at_status="unavailable"))
    check(all(not u["targets"] for u in down["legalAttackTargets"]["units"])
          and down["legalAttackTargets"]["status"] != "ok",
          "an unavailable attack domain publishes no targets", down["legalAttackTargets"])
    contra = normalize_world(_at([entry], can_act=None))
    check(contra["legalAttackTargets"]["units"][0]["status"] == "withheld"
          and contra["legalAttackTargets"]["units"][0]["targets"] == [],
          "non-empty candidates contradicting canAct=None are withheld", contra["legalAttackTargets"])
    moved = [{"id": "7", "owner": 1, "type": "Warrior", "coords": "(1,1)", "health": "10",
              "moved": True, "attacked": False, "canMove": False, "canAttack": True},
             {"id": "42", "owner": 2, "type": "Warrior", "coords": "(2,2)", "health": "10",
              "moved": False, "attacked": False, "canMove": True, "canAttack": True}]
    mf = world_frame(units=moved, canAct=True, myTurn=True)
    mf["legalAttackTargets"] = {"status": "ok", "units": [entry]}
    mp2 = normalize_world(mf)
    check(mp2["legalAttackTargets"]["status"] == "ok"
          and mp2["legalAttackTargets"]["units"][0]["targets"] == [[2, 2]],
          "a unit that has MOVED but can still attack keeps its targets "
          "(moved is not the attack gate)", mp2["legalAttackTargets"])


    print()
    print("passed=%d failed=%d" % (_p, _f))
    return 0 if _f == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
