from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock

import pytest

from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.colony_plan import ColonyPlan, PlanSpec, StepProgress
from rimgovernor.hands import Hands
from rimgovernor.projects import ProjectBook


def fixture(facing='east'):
    action = dict(kind='place_buildings', placements=[dict(def_name='Cooler', x=10, z=10, rotation=facing)])
    book = ProjectBook()
    row = book.upsert(dict(title='Cooling', targets=[dict(kind='building', def_name='Cooler',
        x=10, z=10, expected_facing=facing)]))
    plan = ColonyPlan(spec=PlanSpec(steps=[dict(id='cooler', title='Cooling', action=action,
        completion_criteria='Built as planned'), dict(id='next', title='Dependent',
        after=[dict(step='cooler', when='complete')], completion_criteria='Effect observed',
        action=dict(kind='native_operation', tool='home/research', arguments={}))]),
        progress={'cooler': StepProgress(state='complete', project_id=row.id,
            issued={'0': {'confirmed': True}}), 'next': StepProgress()})
    building = dict(thingId='Thing_Cooler', defName='Cooler', status='built',
                    position=dict(x=10, z=10), rotation=facing.title())
    rt = SimpleNamespace(current_plan=plan, projects=book, signal=Mock(), batch=None)
    return rt, building


async def reconcile(rt, building):
    game = SimpleNamespace(query=AsyncMock(return_value={'buildings': [building]}))
    await rt.projects.reconcile(game)
    BridgeRuntime.reconcile_plan(rt)
    return game


@pytest.mark.parametrize('facing', ['north', 'east', 'south', 'west'])
@pytest.mark.parametrize('status', ['built', 'blueprint', 'frame'])
async def test_exact_facing_survives_construction_identity_changes_and_persistence(facing, status):
    rt, building = fixture(facing)
    rt.projects = ProjectBook(rt.projects.dump())
    rt.current_plan = ColonyPlan.model_validate_json(rt.current_plan.model_dump_json())
    building.update(status=status, thingId='Thing_'+status)
    if status != 'built':
        building.update(defName=status, buildDefName='Cooler')
    await reconcile(rt, building)
    assert rt.projects.rows[0].state == ('complete' if status == 'built' else 'pending')
    assert rt.current_plan.progress['cooler'].state == ('complete' if status == 'built' else 'waiting')
    building['rotation'] = 'West' if facing != 'west' else 'East'
    receipts = deepcopy(rt.current_plan.progress['cooler'].issued)
    await reconcile(rt, building)
    assert rt.projects.rows[0].state == 'planned'
    assert rt.current_plan.progress['cooler'].failure.code == 'plan_invalidated'
    assert rt.current_plan.progress['cooler'].issued == receipts
    assert not list(rt.current_plan.ready())


@pytest.mark.parametrize('rotation', [None, '', 1, 'Est', 'Unknown'])
async def test_unknown_facing_holds_then_recovers_on_readback_without_replay(rotation):
    rt, building = fixture()
    building['rotation'] = rotation
    await reconcile(rt, building)
    progress = rt.current_plan.progress['cooler']
    assert progress.state == 'waiting' and progress.failure.code == 'observation_unavailable'
    assert not list(rt.current_plan.ready())
    building['rotation'] = 'EAST'
    await reconcile(rt, building)
    assert progress.state == 'complete' and progress.failure is None
    assert [s.id for s in rt.current_plan.ready()] == ['next']


async def test_legacy_default_rotation_is_not_invented_facing_expectation():
    rt, building = fixture()
    data = rt.projects.dump()
    data[0]['targets'][0].pop('expected_facing')
    assert data[0]['targets'][0]['rotation'] == 0
    rt.projects = ProjectBook(data)
    building.pop('rotation')
    await reconcile(rt, building)
    assert rt.current_plan.progress['cooler'].state == 'complete'


async def test_hands_records_facing_and_does_not_complete_wrongly_rotated_existing_building():
    rt, building = fixture()
    rt.projects = ProjectBook()
    rt.current_plan.progress['cooler'] = StepProgress()
    rt.mode, rt.context_token = 'automate', 'load'
    rt.chat_revision = rt.handled_revision = 0
    rt.persist = rt.note = Mock()
    building['rotation'] = 'West'
    rt.game = SimpleNamespace(query=AsyncMock(return_value={'buildings': [building]}))
    rt.native = AsyncMock()
    await Hands().advance(rt)
    target = rt.projects.rows[0].targets[0]
    assert target.expected_facing == 'east'
    assert rt.projects.rows[0].state == 'planned'
    BridgeRuntime.reconcile_plan(rt)
    assert rt.current_plan.progress['cooler'].failure.code == 'plan_invalidated'
    assert not list(rt.current_plan.ready())
    rt.native.assert_not_awaited()


@pytest.mark.parametrize('facing,index', [('north', 0), ('east', 1), ('south', 2), ('west', 3)])
async def test_invariant_facing_owns_contract_across_game_languages(facing, index):
    rt, building = fixture(facing)
    building.update(rotation='localized display label', rotationInt=index)
    await reconcile(rt, building)
    assert rt.current_plan.progress['cooler'].state == 'complete'
    building['rotationInt'] = None
    await reconcile(rt, building)
    assert rt.current_plan.progress['cooler'].failure.code == 'observation_unavailable'


def test_legacy_facing_is_grounded_only_by_exact_action_targets():
    rt, _ = fixture()
    target = rt.projects.rows[0].targets[0]
    target.expected_facing = None
    target.x += 1
    rt.projects.ground_legacy_targets(rt.current_plan)
    assert target.expected_facing is None
    target.x -= 1
    rt.projects.ground_legacy_targets(rt.current_plan)
    assert target.expected_facing == 'east'
