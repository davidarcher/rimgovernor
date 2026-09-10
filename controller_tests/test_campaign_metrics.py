from types import SimpleNamespace

import pytest

from rimgovernor.campaign_metrics import CampaignEvidence, intent_metrics
from rimgovernor.colony_plan import ColonyPlan, PlanSpec, StepProgress


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


def native_room_fixture():
    plan, summary, buildings, zones = fixture()
    zones['zones'][0].update(listedCellCount=9,slotGroupCellCount=9,haulGridCellCount=9,cellsImpassable=0,cellsNotStandable=0,contiguous=True,filter={'allowedDefCount':2})
    room = dict(properRoom=True,openRoofCount=0,psychologicallyOutdoors=False,touchesMapEdge=False,
                bedCount=10,skipped=[],beds=[dict(defName='SleepingSpot',position=dict(x=i,z=0),medical=False,forPrisoners=False) for i in range(10)])
    pawns = dict(pawns=[dict(thingId=f'p{i}',job='LayDown',dead=False,isColonist=True,isPrisoner=False,position=dict(x=i,z=0)) for i in range(8)])
    return plan,summary,buildings,zones,dict(rooms=[room]),pawns


def test_actual_sheltered_use_and_storage_configuration_keep_access_unknown():
    plan,summary,buildings,zones,rooms,pawns = native_room_fixture()
    row=CampaignEvidence().sample(plan,summary,(0,0),buildings,zones,1,rooms=rooms,pawns=pawns)['functional']
    assert row['roofed_sleeping_places']==10 and row['roofed_count_complete']
    assert len(row['sheltered_sleeping_pawn_ids'])==8
    assert row['storage_configured'] is True
    assert row['usable_capacity_verified'] is None
    assert row['storage_access_verified'] is None


@pytest.mark.parametrize('defect',['unroofed','prisoner','medical','not_built','outside_bed'])
def test_sheltered_use_requires_exact_native_bed_and_eligible_room(defect):
    plan,summary,buildings,zones,rooms,pawns = native_room_fixture()
    if defect=='unroofed':rooms['rooms'][0]['openRoofCount']=1
    elif defect in ('prisoner','medical'):
        for b in rooms['rooms'][0]['beds']:b['forPrisoners' if defect=='prisoner' else 'medical']=True
    elif defect=='not_built':
        for b in buildings['buildings']:b['status']='blueprint'
    else:
        for p in pawns['pawns']:p['position']['z']=1
    row=CampaignEvidence().sample(plan,summary,(0,0),buildings,zones,1,rooms=rooms,pawns=pawns)['functional']
    assert row['sheltered_sleeping_pawn_ids']==[]


def test_unknown_room_and_storage_fields_are_not_zero_or_usable():
    plan,summary,buildings,zones,rooms,pawns = native_room_fixture()
    rooms['rooms'][0]['openRoofCount']=None
    zones['zones'][0]['haulGridCellCount']=None
    result=CampaignEvidence().sample(plan,summary,(0,0),buildings,zones,1,rooms=rooms,pawns=pawns)['functional']
    assert not result['roofed_count_complete']
    assert result['storage_configured'] is None and result['usable_capacity_verified'] is None
    zones['zones'][0]['filter']['allowedDefCount']=0
    result=CampaignEvidence().sample(plan,summary,(0,0),buildings,zones,2,rooms=rooms,pawns=pawns)['functional']
    assert result['storage_configured'] is False


def test_first_progress_requires_change_and_completion_requires_baseline():
    from copy import deepcopy
    plan,summary,buildings,zones,rooms,pawns = native_room_fixture()
    pawns['pawns'][0]['job']='DoBill'
    evidence=CampaignEvidence()
    evidence.sample(plan,summary,(0,0),buildings,zones,0,pawns=pawns)
    evidence.sample(plan,summary,(0,0),buildings,zones,5,pawns=pawns)
    assert evidence.report()['first_pawn_progress_seconds'] is None
    assert evidence.report()['first_observed_completion_seconds'] is None
    changed=deepcopy(pawns);changed['pawns'][0]['position']['z']=2
    changed_buildings=deepcopy(buildings);changed_buildings['buildings'].append(dict(thingId='new',defName='Wall',status='built',position=dict(x=0,z=6)))
    evidence.sample(plan,summary,(0,0),changed_buildings,zones,10,pawns=changed)
    assert evidence.report()['first_pawn_progress_seconds']==10
    assert evidence.report()['first_observed_completion_seconds']==10


def test_exact_attempt_fingerprints_failures_latency_and_replay():
    from rimgovernor.campaign_metrics import event_metrics
    rows=[dict(id=i,at=100+i,kind='planner_tool',tool='inspect',arguments={'x':1},outcome='rejected' if i==1 else 'returned',elapsed_seconds=i) for i in range(1,4)]
    rows += [dict(id=4,at=104,kind='campaign_native',tool='home/bills',arguments={'action':'add'},outcome='returned',elapsed_seconds=2,useful_order_receipt=True),
             dict(id=5,at=105,kind='planner_tool',tool='inspect',arguments={'truncated':True},outcome='cancelled'),
             dict(id=6,at=106,kind='human',text='Pause this'),
             dict(id=7,at=107,kind='campaign_budget',budget={'compacted':True,'budget_units':4000})]
    result=event_metrics(rows,began_at=100)
    calls=result['calls']['planner_tool']
    assert calls['attempts']==4 and calls['rejected']==1 and calls['cancelled']==1
    assert calls['repeated_identical_attempts']==2 and calls['fingerprint_unknown']==1
    assert calls['latency_seconds']==dict(count=3,total=6,median=2,maximum=3)
    assert result['first_useful_order_seconds']==4
    assert result['context_compactions']==1 and len(result['interventions'])==1
    assert len(result['replay'])==7
    assert result['model_roles'] is None


@pytest.mark.asyncio
async def test_runner_records_ambiguous_native_failure_without_calling_it_a_refusal(monkeypatch):
    import importlib.util
    from pathlib import Path
    from unittest.mock import AsyncMock, Mock
    from rimgovernor.bridge_runtime import BridgeRuntime
    spec=importlib.util.spec_from_file_location('campaign_runner',Path(__file__).parents[1]/'scripts/headless_iterations.py')
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    rt=object.__new__(module.FastTrial);rt.note=Mock()
    monkeypatch.setattr(BridgeRuntime,'native',AsyncMock(side_effect=TimeoutError('lost readback')))
    with pytest.raises(TimeoutError):await rt.native('home/bills',{'action':'add','dryRun':False})
    fields=rt.note.call_args.kwargs
    assert fields['outcome']=='failed' and fields['useful_order_receipt'] is False
    assert fields['elapsed_seconds']>=0


def test_actual_sleeping_capacity_deduplicates_bed_occupancy():
    plan,summary,buildings,zones,rooms,pawns=native_room_fixture()
    evidence=CampaignEvidence()
    row=evidence.sample(plan,summary,(0,0),buildings,zones,0,rooms=rooms,pawns=pawns)['functional']
    assert row['observed_sheltered_sleeping_capacity']==8
    assert row['sleeping_capacity_access_verified'] is True
    for pawn in pawns['pawns']:pawn['position']={'x':0,'z':0}
    row=evidence.sample(plan,summary,(0,0),buildings,zones,1,rooms=rooms,pawns=pawns)['functional']
    assert row['observed_sheltered_sleeping_capacity']==1
    assert row['sleeping_capacity_access_verified'] is None


def test_native_stockpile_survives_plan_replacement():
    plan,summary,buildings,zones=fixture()
    plan=ColonyPlan()
    row=CampaignEvidence().sample(plan,summary,(0,0),buildings,zones,1)
    assert row['stockpile'] is True and row['usable'] is True
    assert row['intended_zone_cells']==0


def test_fresh_pawn_death_overrides_cached_living_summary():
    plan,summary,buildings,zones,rooms,pawns=native_room_fixture()
    for p in pawns['pawns']:p['dead']=True
    row=CampaignEvidence().sample(plan,summary,(0,0),buildings,zones,1,pawns=pawns)
    assert row['living_colonists']==0 and row['usable'] is False


def test_exact_observed_hauling_item_must_be_spawned_in_configured_zone():
    from copy import deepcopy
    plan,summary,buildings,zones,rooms,pawns=native_room_fixture()
    carrier=pawns['pawns'][0];carrier.update(job='HaulToCell',carriedThingId='rice-1')
    evidence=CampaignEvidence()
    evidence.sample(plan,summary,(0,0),buildings,zones,0,pawns=pawns)
    after=deepcopy(pawns);after['pawns'][0].update(job='Wait_Wander',carriedThingId=None)
    items={'things':[{'defName':'RawRice','positions':[{'thingId':'rice-1','spawned':False,'x':0,'z':2}]}]}
    assert evidence.sample(plan,summary,(0,0),buildings,zones,1,pawns=after,items=items)['functional']['storage_access_verified'] is None
    items['things'][0]['positions'][0]['spawned']=True
    row=evidence.sample(plan,summary,(0,0),buildings,zones,2,pawns=after,items=items)['functional']
    assert row['storage_access_verified'] is True
    assert row['observed_storage_deliveries'][0]['pawn_id']=='p0'
    assert row['usable_capacity_verified'] is None


def test_manual_pending_work_is_reported_as_interrupted():
    plan,summary,buildings,zones=fixture()
    plan.progress['beds'].state='waiting'
    row=CampaignEvidence().sample(plan,summary,(0,0),buildings,zones,1,mode='manual')
    assert row['interrupted_step_ids']==['beds']


def test_blocked_storage_cells_do_not_inflate_functional_capacity():
    plan,summary,buildings,zones,rooms,pawns=native_room_fixture()
    zones['zones'][0]['cellsImpassable']=1
    row=CampaignEvidence().sample(plan,summary,(0,0),buildings,zones,1,rooms=rooms,pawns=pawns)['functional']
    assert row['storage'][0]['usable_cell_lower_bound']==8
    assert row['storage_configured'] is False and row['usable_capacity_verified'] is False


@pytest.mark.asyncio
async def test_production_sampler_uses_fresh_food_and_generic_owned_items(monkeypatch):
    import importlib.util
    from pathlib import Path
    from unittest.mock import AsyncMock
    spec=importlib.util.spec_from_file_location('fresh_campaign_runner',Path(__file__).parents[1]/'scripts/headless_iterations.py')
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    plan,summary,buildings,zones,rooms,pawns=native_room_fixture()
    fresh=SimpleNamespace(summary=SimpleNamespace(pawns=summary.pawns,supplies=[]))
    observed=AsyncMock(return_value=fresh);monkeypatch.setattr(module,'observe',observed)
    game=SimpleNamespace(query=AsyncMock(side_effect=[buildings,zones,rooms,pawns,{'things':[]}]))
    rt=SimpleNamespace(current_plan=plan,batch=SimpleNamespace(summary=summary),game=game,
                       sync_identity=AsyncMock(),context_token='load',mode='automate')
    row=await module.sample_metrics(rt,CampaignEvidence(),(0,0),1)
    observed.assert_awaited_once_with(game)
    assert row['starting_food_allowed'] is False and row['usable'] is False
    call=game.query.call_args_list[-1]
    assert call.args==('home/list_things',)
    assert call.kwargs['ownership']=='ours' and call.kwargs['includeHeld'] is True
    assert 'match' not in call.kwargs
