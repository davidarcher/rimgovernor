from types import SimpleNamespace

import pytest

from rimbot.campaign_metrics import CampaignEvidence, intent_metrics
from rimbot.colony_plan import ColonyPlan, PlanSpec, StepProgress


def fixture():
    plan = ColonyPlan(spec=PlanSpec(steps=[
        dict(id='beds',title='Beds',completion_criteria='Built',action=dict(
            kind='place_buildings',placements=[dict(x=i,z=0,def_name='SleepingSpot') for i in range(10)])),
        dict(id='stores',title='Stores',completion_criteria='Created',action=dict(
            kind='create_zone',zone_type='stockpile',label='Stores',patches=[dict(x=0,z=2,width=3,height=3)]))]),
        progress={'beds':StepProgress(state='complete',issued={str(i):dict(confirmed=True) for i in range(10)}),
                  'stores':StepProgress(state='complete',issued={'0':dict(confirmed=True,cells=9)})})
    summary = SimpleNamespace(pawns=[SimpleNamespace(dead=False) for _ in range(8)],
                              supplies=[SimpleNamespace(def_name='Pemmican',owned_unforbidden_units=10)])
    buildings = dict(buildings=[dict(thingId=str(i),defName='SleepingSpot',status='built',position=dict(x=i,z=0)) for i in range(10)])
    zones = dict(zones=[dict(id=1,label='Stores',type='Zone_Stockpile',gridCellCount=9,
                           gridCells=[dict(x=x,z=z) for x in range(3) for z in range(2,5)])])
    return plan, summary, buildings, zones


def test_native_completions_overshoot_and_removed_work_override_stale_plan():
    plan, summary, buildings, zones = fixture()
    evidence = CampaignEvidence()
    first = evidence.sample(plan,summary,(0,0),buildings,zones,1)
    assert first['usable'] and first['excess_sleeping_places'] == 2
    buildings['buildings'] = buildings['buildings'][:7]
    later = evidence.sample(plan,summary,(0,0),buildings,zones,2)
    assert not later['usable']
    assert later['accepted_building_effects'] == 10
    assert later['nearby_completed_objects'] == 7
    assert later['removed_completed_ids'] == ['7','8','9']
    assert evidence.report()['thresholds']['sleeping_places'] == 8


def test_interrupted_project_separates_intent_attempt_accept_and_completion():
    plan, summary, buildings, zones = fixture()
    plan.progress['beds'] = StepProgress(state='blocked',issued={'0':dict(confirmed=True),'1':dict(confirmed=False)})
    buildings['buildings'] = [dict(buildings['buildings'][0],status='blueprint')]
    row = CampaignEvidence().sample(plan,summary,(0,0),buildings,zones,1)
    assert row['intended_building_objects'] == 10
    assert row['attempted_building_objects'] == 2
    assert row['accepted_building_effects'] == 1
    assert row['nearby_completed_objects'] == 0
    assert not row['usable']


def test_removed_zone_and_wrong_type_do_not_pass_stale_completion():
    plan, summary, buildings, zones = fixture()
    zones['zones'][0]['type'] = 'Zone_Growing'
    assert not CampaignEvidence().sample(plan,summary,(0,0),buildings,zones,1)['usable']
    assert not CampaignEvidence().sample(plan,summary,(0,0),buildings,{'zones':[]},1)['usable']


@pytest.mark.parametrize('partial', ['buildings','zone_cells','zones'])
def test_incomplete_native_readbacks_are_unknown(partial):
    plan, summary, buildings, zones = fixture()
    if partial == 'buildings':
        buildings['skipped'] = {'byMaxDetailed':1}
    elif partial == 'zones':
        zones['truncated'] = True
    else:
        zones['zones'][0]['gridCells'].pop()
    with pytest.raises(ValueError):
        CampaignEvidence().sample(plan,summary,(0,0),buildings,zones,1)


def test_zone_unconfirmed_receipt_does_not_imply_accepted_cells():
    plan, _, _, _ = fixture()
    plan.progress['stores'].issued['0'] = dict(confirmed=False)
    assert intent_metrics(plan)['attempted_zone_cells'] == 9
    assert intent_metrics(plan)['accepted_zone_cells'] == 0
