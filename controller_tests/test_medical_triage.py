from unittest.mock import AsyncMock

import pytest

from rimbot.colony_plan import ColonyGoal, PlanStep, StepProgress, ColonyPlan, CommitSteps
from rimbot.medical_triage import treatment_pairs
from rimbot.medical_triage import threats_cleared
from rimbot.colony_skills import SkillBlocked
from test_colony_controller import Replay


def fixture():
    rt = Replay(4)
    rt.current_plan.colony_goals['CriticalMedical'] = ColonyGoal(priority_class=1)
    for p in rt.people:
        p.update(job='Wait')
        p['health'].update(needsTend=False)
    rt.people[0]['health'].update(needsTend=True, hoursUntilDeathFromBloodLoss=10)
    rt.people[1]['health'].update(needsTend=True, hoursUntilDeathFromBloodLoss=2)
    rt.facts.update(medicalKnown=True, criticalPatients=['Thing_Human0', 'Thing_Human1'])
    return rt


def test_only_exact_incapacitated_threat_census_allows_medical_cleanup():
    status = {'blocks': {'threats': True}, 'time': {'paused': True},
        'counts': {'hostileCount': 1, 'huntingPredatorCount': 0},
        'threats': {'hostiles': [{'thingId': 'raider', 'downed': True}]}}
    assert threats_cleared(status)
    status['threats']['hostiles'].append({'thingId': 'unlisted', 'downed': False})
    assert not threats_cleared(status)
    status['threats']['hostiles'].pop()
    status['counts']['hostileCount'] = 2
    assert not threats_cleared(status)
    status['counts']['hostileCount'] = None
    assert not threats_cleared(status)


def test_urgent_patient_first_and_doctors_respect_player_and_self_tend():
    rt = fixture()
    pairs = list(treatment_pairs(rt.people, rt.facts['criticalPatients'], {}))
    assert pairs[0][0] == 'Thing_Human1'
    assert all(patient != doctor for patient, doctor in pairs)
    blocked = {'player_draft_overrides': {'Thing_Human3': False},
               'work_overrides': {'Thing_Human2': {'Doctor': 0}}}
    assert all(doctor not in ('Thing_Human2', 'Thing_Human3')
               for _, doctor in treatment_pairs(rt.people, rt.facts['criticalPatients'], blocked))


@pytest.mark.asyncio
async def test_unavailable_doctor_uses_next_native_approved_candidate():
    rt = fixture()
    rt.inspect_native = AsyncMock(side_effect=[{'success': False, 'reason': 'unreachable'}, {'success': True}])
    method, actions = await rt.controller.skills.compile('CriticalMedical', rt.facts, rt.people)
    assert method == 'tend-Thing_Human1'
    assert actions[0]['completion'] == 'patient_tended'
    assert rt.inspect_native.await_count == 2
    assert len(rt.current_plan.colony_goals['CriticalMedical'].evidence['triage']['refusals']) == 1


@pytest.mark.asyncio
async def test_existing_treatment_is_not_preempted_by_competing_patient():
    rt = fixture()
    rt.people[3]['job'] = 'TendPatient'
    rt.inspect_native = AsyncMock()
    assert await rt.controller.skills.compile('CriticalMedical', rt.facts, rt.people) is None
    rt.inspect_native.assert_not_awaited()


@pytest.mark.asyncio
async def test_no_eligible_doctor_retains_explicit_hold():
    rt = fixture()
    for p in rt.people:
        p['downed'] = True
    with pytest.raises(SkillBlocked, match='No available'):
        await rt.controller.skills.compile('CriticalMedical', rt.facts, rt.people)


@pytest.mark.asyncio
async def test_owned_combat_drafts_release_before_fresh_treatment_selection():
    rt = fixture()
    rt.facts['hostiles'] = 0
    rt.draft_owners = {p['thingId']: rt.context_token for p in rt.people}
    for p in rt.people:
        p['drafted'] = True
    rt.current_plan.control['player_draft_overrides'] = {'Thing_Human3': True}
    rt.inspect_native = AsyncMock(return_value={'success': True})
    method, actions = await rt.controller.skills.compile('CriticalMedical', rt.facts, rt.people)
    assert actions == [{'kind': 'stand_down', 'pawn_ids': ['Thing_Human0', 'Thing_Human1', 'Thing_Human2']}]
    rt.inspect_native.assert_not_awaited()
    for p in rt.people[:3]:
        p['drafted'] = False
    _, actions = await rt.controller.skills.compile('CriticalMedical', rt.facts, rt.people)
    assert actions[0]['completion'] == 'patient_tended'


@pytest.mark.asyncio
@pytest.mark.parametrize('hostiles,owner', [(1, 'current'), (None, 'current'), (0, 'old')])
async def test_medical_cleanup_preserves_combat_and_foreign_ownership(hostiles, owner):
    rt = fixture()
    rt.facts['hostiles'] = hostiles
    rt.draft_owners = {p['thingId']: rt.context_token if owner == 'current' else 'old-load' for p in rt.people}
    for p in rt.people:
        p['drafted'] = True
    with pytest.raises(SkillBlocked, match='No available'):
        await rt.controller.skills.compile('CriticalMedical', rt.facts, rt.people)


@pytest.mark.asyncio
async def test_repeat_tending_while_another_patient_keeps_goal_active_survives_reload():
    rt = fixture()
    rt.inspect_native = AsyncMock(return_value={'success': True})
    goal = rt.current_plan.colony_goals['CriticalMedical']
    for episode in range(3):
        method, actions = await rt.controller.skills.compile('CriticalMedical', rt.facts, rt.people)
        assert method == 'tend-Thing_Human1' + (f'-{episode}' if episode else '')
        identity = f'treatment-{episode}'
        step = PlanStep(id=identity, title='Treat', goal_id='CriticalMedical',
            source='AUTOPILOT', action=actions[0], completion_criteria='Native tending observed')
        prior = {k: v.model_dump() for k, v in rt.current_plan.progress.items()}
        rt.current_plan.commit(CommitSteps(expected_revision=rt.current_plan.revision, reason='Fresh native retend eligibility',
            steps=[step]).decision(rt.current_plan), actor='strategist', tick=100 + episode * 30000)
        assert all(rt.current_plan.progress[k].model_dump() == v for k, v in prior.items())
        rt.current_plan.progress[identity] = StepProgress(state='complete', issued={'0': {'confirmed':True}})
        goal.steps.append(identity)
        goal.evidence.setdefault('methods', {})[method] = [identity]
        rt.current_plan = ColonyPlan.model_validate_json(rt.current_plan.model_dump_json())
        goal = rt.current_plan.colony_goals['CriticalMedical']
    assert len(rt.current_plan.spec.steps) == 3


@pytest.mark.asyncio
async def test_repeat_tending_reports_lost_staff_instead_of_waiting_forever():
    rt = fixture()
    goal = rt.current_plan.colony_goals['CriticalMedical']
    goal.evidence['methods'] = {'tend-'+p: [] for p in rt.facts['criticalPatients']}
    for p in rt.people:
        p['downed'] = True
    with pytest.raises(SkillBlocked, match='No available'):
        await rt.controller.skills.compile('CriticalMedical', rt.facts, rt.people)


@pytest.mark.parametrize('state,confirmed,source', [('waiting',True,'AUTOPILOT'), ('blocked',False,'AUTOPILOT'),
    ('complete',False,'AUTOPILOT'), ('complete',True,'PLAYER')])
def test_repeat_treatment_cannot_replace_pending_uncertain_or_player_intent(state, confirmed, source):
    rt = fixture()
    step = PlanStep(id='first', title='Tend', source=source, goal_id='CriticalMedical',
        action={'kind':'native_operation','tool':'home/order','completion':'patient_tended',
                'arguments':{'action':'tend','pawn':'Thing_Doctor','target':'Thing_Patient'}},
        completion_criteria='Health observed')
    rt.current_plan.spec.steps = [step]
    rt.current_plan.progress['first'] = StepProgress(state=state, issued={'0': {'confirmed':confirmed}})
    again = step.model_copy(update={'id':'again'})
    with pytest.raises(ValueError, match='identical intent'):
        rt.current_plan.commit(CommitSteps(expected_revision=0, reason='Retend', steps=[again]).decision(rt.current_plan),
            actor='strategist', tick=500)
