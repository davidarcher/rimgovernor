from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimgovernor.colony_plan import ColonyPlan, ColonyGoal
from rimgovernor.colony_upkeep import upkeep_nodes
from rimgovernor.medical_reserves import reserve_evidence, reserve_method


def facts(count=0):
    return dict(tick=10, colonists=2, resources={'MedicineHerbal': count}, policyResources={'MedicineHerbal': 'herbal medicine'},
        upkeep=dict(version=1, tick=10, errors={}, items=[dict(id='Thing_Med1', defName='MedicineHerbal',
            medicine=True, count=count, forbidden=False, perishable=True, rotTicks=10000)]))


def test_reserves_have_entry_and_recovery_hysteresis_and_preserve_unknowns():
    control = {}
    assert reserve_evidence(facts(3), control) == []
    assert reserve_evidence(facts(1), control)[0]['count'] == 5
    assert reserve_evidence(facts(3), control)[0]['count'] == 3
    f = facts(6)
    f['upkeep']['errors']['items'] = 'unavailable'
    assert reserve_evidence(f, control) is None and control['medical_reserve_active']
    assert reserve_evidence(facts(6), control) == []


@pytest.mark.parametrize('field,value', [('forbidden', True), ('rotTicks', 0)])
def test_forbidden_or_expired_medicine_cannot_certify_reserve(field, value):
    f = facts(20)
    f['upkeep']['items'][0][field] = value
    assert reserve_evidence(f, {})[0]['stock'] == 0


@pytest.mark.asyncio
async def test_reserve_method_uses_normal_resource_acquisition_without_care_settings():
    f = facts(1)
    plan = ColonyPlan(colony_goals={'MaintainMedicalReserves': ColonyGoal(priority_class=3)})
    upkeep_nodes(f, plan.control)
    rt = SimpleNamespace(current_plan=plan, identity=dict(colonyId='colony', loadToken='load', mapId=0),
        game=SimpleNamespace(invoke=AsyncMock(return_value=dict(success=True, sources=[
            dict(thingId='Plant_Healroot1', resource='MedicineHerbal', x=10, z=11,
                 designated=False, **{'yield': 5}, workTypes=[dict(name='PlantCutting', skills=['Plants'])])]))))
    _, actions = await reserve_method(rt, f)
    assert len(actions) == 1 and actions[0]['tool'] == 'home/acquire_resource'
    assert plan.colony_goals['MaintainMedicalReserves'].target == dict(resource='MedicineHerbal', quantity=6)
    assert actions[0]['arguments']['resource'] == 'MedicineHerbal'
