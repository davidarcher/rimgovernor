import asyncio
from copy import deepcopy
from pathlib import Path
import sys
import pytest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/"scripts"))
from native_combat_acceptance import CombatScenarioClock, healthy_candidates, attack_request, terminal, overridden_attack
from native_typed_clock_acceptance import TypedScenarioClock


def pawn():
    return {"pawn":{"id":"Human1","snapshot":{"token":"attacker"}},"dead":False,"downed":False,"drafted":False,
        "health":{"summaryFraction":1.0},"biography":{"disabledWorkTags":[]}}


def test_candidate_requires_conscious_health_and_native_violence_capability():
    row=pawn(); assert healthy_candidates([row])==[row]
    row["biography"]["disabledWorkTags"]=["Violent"]
    assert healthy_candidates([row])==[]
    row=pawn();row["health"]["summaryFraction"]=.5005
    with pytest.raises(AssertionError):healthy_candidates([row])
    row=pawn();row["downed"]=True
    with pytest.raises(AssertionError):healthy_candidates([row])


def test_ranged_candidate_requires_native_shooting_capability():
    row = pawn()
    row["biography"]["disabledWorkTags"] = ["Shooting"]
    assert healthy_candidates([row]) == [row]
    assert healthy_candidates([row], ranged=True) == []


def test_ranged_terminal_requires_the_exact_ranged_job():
    receipt, progress, victim = combat()
    with pytest.raises(AssertionError):
        terminal(progress, receipt, victim, "Hare1", ranged=True)
    receipt["applied"]["observed"]["job"]["jobDef"] = "AttackStatic"
    progress["completed"]["evidence"]["job"]["jobDef"] = "AttackStatic"
    terminal(progress, receipt, victim, "Hare1", ranged=True)
    victim["downed"] = False
    with pytest.raises(AssertionError):
        terminal(progress, receipt, victim, "Hare1", ranged=True)


def test_attack_request_preserves_both_exact_cas_and_explicit_guards():
    target={"pawn":{"id":"Hare1","snapshot":{"token":"target"}}}
    request=attack_request({"mapId":0},{"context":{"nativeGeneration":"4"},"leaseId":"lease"},pawn(),target,3)
    command=request["operation"]["attackTarget"]
    assert command=={"pawn":{"entityId":"Human1","expectedSnapshotToken":"attacker"},"target":{"entityId":"Hare1","expectedSnapshotToken":"target"},
        "mode":"ATTACK_MODE_MELEE","requireHostile":True,"requireStanding":True,"requireCombatHealth":True}


def combat():
    effect={"pawnId":"Human1","jobId":58,"jobDef":"AttackMelee","targetA":{"thingId":"Hare1"},"verified":True,
        "verifiedReason":"Native positive damage by this exact attacker/job caused the observed target downing."}
    receipt={"applied":{"observed":{"job":deepcopy(effect)}}}
    progress={"completeInspection":True,"completed":{"evidence":{"job":effect}}}
    victim={"pawn":{"id":"Hare1"},"dead":False,"downed":True}
    return receipt,progress,victim


@pytest.mark.parametrize("bad",["unrelated_job","unrelated_attacker","unrelated_target","not_terminal","pending","no_causal_reason","incomplete"])
def test_terminal_requires_attributed_job_and_observed_native_outcome(bad):
    receipt,progress,victim=combat();terminal(progress,receipt,victim,"Hare1")
    effect=progress["completed"]["evidence"]["job"]
    if bad=="unrelated_job":effect["jobId"]=59
    elif bad=="unrelated_attacker":effect["pawnId"]="Human2"
    elif bad=="unrelated_target":effect["targetA"]["thingId"]="Hare2"
    elif bad=="not_terminal":victim["downed"]=False
    elif bad=="pending":progress["pending"]=progress.pop("completed")
    elif bad=="no_causal_reason":effect["verifiedReason"]="The target is dead."
    else:progress["completeInspection"]=False
    with pytest.raises(AssertionError):terminal(progress,receipt,victim,"Hare1")


def test_combat_clock_translates_only_exact_committed_targets(monkeypatch):
    calls=[]
    async def change(self,speed,**arguments):calls.append(arguments);return arguments
    async def control(self,method,request):return request
    monkeypatch.setattr(TypedScenarioClock,"change",change)
    monkeypatch.setattr(TypedScenarioClock,"control",control)
    clock=CombatScenarioClock(None,{"mapId":0},"owner",{},["Hare1","Hare2"])
    asyncio.run(clock.change("Superfast",max_ticks=240,mode="combat",ignored_hostiles="Hare1,Hare2"))
    assert calls==[{"max_ticks":240}]
    with pytest.raises(AssertionError):asyncio.run(clock.change("Superfast",max_ticks=240,mode="combat",ignored_hostiles="unknown"))
    request={"policy":{"mode":"WATCH_MODE_COLONY","healthDropFraction":.1}}
    translated=asyncio.run(clock.control("start",request))
    assert translated["policy"]=={"mode":"WATCH_MODE_COMBAT","healthDropFraction":.1,"acknowledgedHostileIds":["Hare1","Hare2"]}
    assert request["policy"]["mode"]=="WATCH_MODE_COLONY"


@pytest.mark.parametrize("bad",["claim_retained","same_snapshot","wrong_job","not_interrupted"])
def test_player_override_requires_lost_claim_new_snapshot_and_actual_player_job(bad):
    before={"pawn":{"id":"Human1","snapshot":{"token":"before"}}}
    after={"pawn":{"id":"Human1","snapshot":{"token":"after"}},"drafted":True,"draftClaim":{"unowned":{}},
        "job":{"loadId":"60","defName":"Wait_Combat"}}
    external={"success":True,"accepted":True,"jobId":60,"jobDef":"Wait_Combat"}
    progress={"completeInspection":True,"unsuccessful":{"reason":"UNSUCCESSFUL_REASON_INTERRUPTED"}}
    overridden_attack(progress,before,after,external)
    if bad=="claim_retained":after["draftClaim"]={"owned":{"claimId":"old"}}
    elif bad=="same_snapshot":after["pawn"]["snapshot"]["token"]="before"
    elif bad=="wrong_job":after["job"]["loadId"]="61"
    else:progress["unsuccessful"]["reason"]="UNSUCCESSFUL_REASON_TARGET_DEAD"
    with pytest.raises(AssertionError):overridden_attack(progress,before,after,external)
