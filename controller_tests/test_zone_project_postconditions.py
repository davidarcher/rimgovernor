from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock

import pytest

from rimbot.bridge_runtime import BridgeRuntime
from rimbot.colony_plan import ColonyPlan, PlanSpec, StepProgress
from rimbot.projects import ProjectBook
from rimbot.hands import Hands


def fixture():
    action = dict(kind='create_zone', zone_type='growing', crop='Plant_Rice',
                  label='Food', patches=[dict(x=10, z=10, width=2, height=1)])
    book = ProjectBook()
    row = book.upsert(dict(title='Food', targets=[dict(kind='zone', zone_id='7',
        zone_patches=action['patches'], zone_type='growing', crop='Plant_Rice')]))
    plan = ColonyPlan(spec=PlanSpec(steps=[dict(id='field', title='Food', action=action,
        completion_criteria='Zone matches'), dict(id='next', title='Dependent',
        after=[dict(step='field', when='complete')], completion_criteria='Effect observed',
        action=dict(kind='native_operation', tool='home/research', arguments={}))]),
        progress={'field': StepProgress(state='complete', project_id=row.id,
            issued={'0': {'confirmed': True, 'receipt': {'zone': '7'}}}), 'next': StepProgress()})
    cells = [dict(x=10, z=10), dict(x=11, z=10)]
    zone = dict(id=7, type='Zone_Growing', plantDef='Plant_Rice', plantDefExplicitlySet=True,
        cells=cells, gridCells=deepcopy(cells), listedCellCount=2, gridCellCount=2,
        cellsNotListed=0, gridCellsNotListed=0, consistent=True)
    census = dict(success=True, zoneCount=1, zoneCountOnMap=1,
                  totals={'gridSweepFailed': False}, zones=[zone])
    rt = SimpleNamespace(current_plan=plan, projects=book, signal=Mock(), batch=None)
    return rt, census


async def reconcile(rt, census):
    game = SimpleNamespace(query=AsyncMock(return_value=census))
    await rt.projects.reconcile(game)
    BridgeRuntime.reconcile_plan(rt)
    return game


@pytest.mark.parametrize('edit', ['shrink', 'move', 'expand', 'crop', 'type', 'deleted'])
async def test_native_zone_edit_invalidates_completed_action_and_blocks_dependents_after_restart(edit):
    rt, census = fixture()
    await reconcile(rt, census)
    assert [step.id for step in rt.current_plan.ready()] == ['next']
    receipts = deepcopy(rt.current_plan.progress['field'].issued)
    rt.current_plan = ColonyPlan.model_validate_json(rt.current_plan.model_dump_json())
    rt.projects = ProjectBook(rt.projects.dump())
    zone = census['zones'][0]
    if edit in ('shrink', 'move', 'expand'):
        cells = zone['cells'][:1] if edit == 'shrink' else [dict(x=30, z=30)] if edit == 'move' else [*zone['cells'], dict(x=12, z=10)]
        zone.update(cells=cells, gridCells=deepcopy(cells), listedCellCount=len(cells), gridCellCount=len(cells))
    if edit == 'crop':
        zone['plantDef'] = 'Plant_Corn'
    if edit == 'type':
        zone['type'] = 'Zone_Stockpile'
        zone.pop('plantDefExplicitlySet')
    if edit == 'deleted':
        census.update(zones=[], zoneCount=0, zoneCountOnMap=0)
    game = await reconcile(rt, census)
    assert rt.projects.rows[0].state == 'planned'
    progress = rt.current_plan.progress['field']
    assert progress.state == 'blocked' and progress.failure.code == 'plan_invalidated'
    assert progress.issued == receipts
    assert not list(rt.current_plan.ready())
    game.query.assert_awaited_once_with('home/list_zones', includeCells=True,
                                       includeContents=False, maxCellsPerZone=10000)


@pytest.mark.parametrize('change', [
    lambda r: r.pop('success'), lambda r: r.update(zoneCountOnMap=2),
    lambda r: r['totals'].update(gridSweepFailed=True),
    lambda r: r['zones'][0].pop('gridCells'),
    lambda r: r['zones'][0].update(gridCellsNotListed=1),
    lambda r: r['zones'][0].update(gridCells=[dict(x=10, z=10)]),
    lambda r: r['zones'][0].update(consistent=False),
    lambda r: r['zones'][0].update(plantDef=None),
    lambda r: r['zones'][0].pop('plantDef'),
])
async def test_unavailable_zone_evidence_holds_dependencies_then_recovers_without_replay(change):
    rt, census = fixture()
    good = deepcopy(census)
    change(census)
    receipts = deepcopy(rt.current_plan.progress['field'].issued)
    await reconcile(rt, census)
    progress = rt.current_plan.progress['field']
    assert progress.state == 'waiting' and progress.failure.code == 'observation_unavailable'
    assert not list(rt.current_plan.ready())
    await reconcile(rt, good)
    assert progress.state == 'complete' and progress.failure is None
    assert progress.issued == receipts
    assert [step.id for step in rt.current_plan.ready()] == ['next']


async def test_full_zone_census_is_shared_across_exact_targets():
    rt, census = fixture()
    other = deepcopy(census['zones'][0])
    other['id'] = 8
    census['zones'].append(other)
    census.update(zoneCount=2, zoneCountOnMap=2)
    target = rt.projects.rows[0].targets[0].model_dump()
    target['zone_id'] = '8'
    rt.projects.upsert(dict(title='Other field', targets=[target]))
    game = await reconcile(rt, census)
    assert all(row.state == 'complete' for row in rt.projects.rows)
    assert game.query.await_count == 1


async def test_hands_persists_zone_contract_for_later_player_edit_detection():
    rt, census = fixture()
    census['zones'][0]['label'] = 'Food'
    rt.projects = ProjectBook()
    rt.current_plan.progress['field'] = StepProgress()
    rt.mode, rt.context_token = 'automate', 'load'
    rt.chat_revision = rt.handled_revision = 0
    rt.persist = rt.note = Mock()
    rt.game = SimpleNamespace(query=AsyncMock(side_effect=[{'zones': []}, census]))
    rt.inspect_native = AsyncMock(return_value={'cellsAccepted': 2})
    rt.native = AsyncMock(return_value={'receipt': {'cellsAccepted': 2}})
    await Hands().advance(rt)
    assert rt.current_plan.progress['field'].state == 'complete'
    target = rt.projects.rows[0].targets[0]
    assert target.zone_patches == rt.current_plan.spec.steps[0].action.patches
    assert (target.zone_type, target.crop) == ('growing', 'Plant_Rice')
    assert rt.native.await_count == 1
    census['zones'][0]['plantDef'] = 'Plant_Corn'
    await reconcile(rt, census)
    assert rt.current_plan.progress['field'].failure.code == 'plan_invalidated'
    assert not list(rt.current_plan.ready())
    assert rt.native.await_count == 1
