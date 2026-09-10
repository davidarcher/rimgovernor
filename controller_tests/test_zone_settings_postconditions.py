from copy import deepcopy

import pytest

from rimbot.projects import ProjectBook, zone_settings
from test_zone_project_postconditions import fixture, reconcile


def stockpile():
    rt, census = fixture()
    zone = census['zones'][0]
    zone.pop('plantDefExplicitlySet')
    zone.update(type='Zone_Stockpile', priority='Important', filter={'contract': dict(
        version=1, allowedDefs=['MealSimple', 'WoodLog'], disallowedSpecial=['AllowRotten'],
        hitPoints=[0, 1], quality=[0, 6], mentalBreakChance=[0, 1])})
    target = rt.projects.rows[0].targets[0]
    target.crop, target.zone_type = None, 'stockpile'
    target.zone_settings = zone_settings(zone)
    return rt, census


@pytest.mark.parametrize('field,value', [('allowedDefs', ['MealSimple']),
    ('disallowedSpecial', []), ('hitPoints', [.5, 1]), ('quality', [2, 6]),
    ('mentalBreakChance', [0, .5]), ('priority', 'Critical')])
async def test_real_filter_fields_invalidate_dependencies_across_persistence(field, value):
    rt, census = stockpile()
    await reconcile(rt, census)
    receipts = deepcopy(rt.current_plan.progress['field'].issued)
    rt.projects = ProjectBook(rt.projects.dump())
    zone = census['zones'][0]
    (zone if field == 'priority' else zone['filter']['contract'])[field] = value
    await reconcile(rt, census)
    assert rt.current_plan.progress['field'].failure.code == 'plan_invalidated'
    assert rt.current_plan.progress['field'].issued == receipts
    assert not list(rt.current_plan.ready())


@pytest.mark.parametrize('field', ['allowSow', 'allowCut'])
async def test_sowing_and_cutting_are_maintained_postconditions(field):
    rt, census = fixture()
    census['zones'][0].update(allowSow=True, allowCut=True)
    rt.projects.rows[0].targets[0].zone_settings = dict(allowSow=True, allowCut=True)
    await reconcile(rt, census)
    census['zones'][0][field] = False
    await reconcile(rt, census)
    assert rt.current_plan.progress['field'].failure.code == 'plan_invalidated'


async def test_unknown_filter_cannot_be_certified_by_unchanged_display_summary():
    rt, census = stockpile()
    good = deepcopy(census)
    census['zones'][0]['filter'].pop('contract')
    await reconcile(rt, census)
    assert rt.current_plan.progress['field'].failure.code == 'observation_unavailable'
    await reconcile(rt, good)
    assert rt.current_plan.progress['field'].state == 'complete'


def test_legacy_zone_contract_uses_exact_durable_action_not_observed_edits():
    rt, _ = fixture()
    target = rt.projects.rows[0].targets[0]
    target.zone_patches, target.zone_type, target.crop = [], None, None
    rt.projects.ground_legacy_targets(rt.current_plan)
    assert target.zone_patches == rt.current_plan.spec.steps[0].action.patches
    assert target.crop == 'Plant_Rice'
    assert target.zone_settings is None  # No historical settings were recorded.
    assert rt.projects.rows[0].source_step == 'field'


def test_ambiguous_legacy_zone_owner_cannot_invent_expectations():
    rt, _ = fixture()
    target = rt.projects.rows[0].targets[0]
    target.zone_patches = []
    rt.current_plan.progress['next'].project_id = rt.projects.rows[0].id
    rt.projects.ground_legacy_targets(rt.current_plan)
    assert target.zone_patches == []


@pytest.mark.parametrize('existing', [False, True])
async def test_unavailable_stockpile_preview_refuses_before_any_write(existing):
    from types import SimpleNamespace
    from unittest.mock import AsyncMock
    from rimbot.colony_plan import Zone, StepProgress
    from rimbot.hands import Hands

    action = Zone(zone_type='stockpile', label='Supplies',
                  patches=[dict(x=10, z=10, width=1, height=1)])
    zone = dict(id=7, label='Supplies', type='Zone_Stockpile',
                gridCells=[dict(x=10, z=10)])
    rt = SimpleNamespace(game=SimpleNamespace(query=AsyncMock(
        return_value={'zones': [zone] if existing else []})),
        inspect_native=AsyncMock(return_value={'cellsAccepted': 1}), native=AsyncMock())
    with pytest.raises(ValueError, match='Exact native stockpile settings unavailable'):
        await Hands().zone(rt, action, StepProgress(), '0', 0, 'load', 0)
    rt.native.assert_not_awaited()
