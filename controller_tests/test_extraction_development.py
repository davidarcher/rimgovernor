import pytest
from rimbot.colony_plan import ColonyGoal, ColonyPlan, PlanStep, StepProgress
from rimbot.colony_skills import SkillBlocked
from rimbot.extraction_development import development_method, drilling_policy
from rimbot.player_commands import CreateGoal
from rimbot.production_policy import observe_mining_progress


def fixture():
    goal = ColonyGoal(source='PLAYER', priority_class=3, target={'resource':'Plasteel', 'quantity':10, 'deep_extraction':True})
    site = dict(defName='DeepDrill', x=10, z=20, rotation='north', eligible=True, workTypes=[{'name':'Mining'}])
    infrastructure = dict(scannersActive=True, definitions=[dict(defName='DeepDrill', method='drill',
        available=True, costs={'Steel':100}, research=[])], sites=[site], drills=[], owned=[])
    sources = dict(infrastructure=infrastructure, storage=dict(capacity=75, stackLimit=75, deepPortion=5,
        haulers=['worker'], candidates=[]))
    return goal, sources, site


def test_deep_extraction_requires_explicit_goal_and_native_prerequisites():
    assert not CreateGoal(kind='CreateGoal', goal='MaintainResource', resource='Steel', quantity=1).deep_extraction
    with pytest.raises(ValueError): CreateGoal(kind='CreateGoal', goal='EnsureBasicPower', deep_extraction=True)
    goal, sources, _ = fixture()
    goal.target['deep_extraction'] = False
    with pytest.raises(SkillBlocked, match='explicit'): development_method(goal, sources, {})
    goal.target['deep_extraction'] = True
    sources['infrastructure']['definitions'][0]['available'] = False
    with pytest.raises(SkillBlocked, match='research'): development_method(goal, sources, {})
    sources['infrastructure']['definitions'][0]['available'] = True
    sources['infrastructure']['sites'] = []
    with pytest.raises(SkillBlocked, match='power, safe access'): development_method(goal, sources, {})


def test_storage_precedes_shared_construction_and_only_committed_facilities_get_policy():
    goal, sources, site = fixture()
    sources['storage'].update(capacity=0, candidates=[dict(x=1,z=2)])
    _, actions = development_method(goal, sources, {})
    assert actions[0]['kind'] == 'create_zone' and actions[0]['allow'] == ['Plasteel']
    sources['storage']['capacity'] = 75
    facts = {}
    method, actions = development_method(goal, sources, facts)
    assert actions[0]['kind'] == 'place_buildings'
    assert facts['definitions']['DeepDrill']['costs'] == {'Steel':100}
    plan = ColonyPlan(colony_goals={'MaintainResource-Plasteel':goal})
    assert drilling_policy(plan) == ''
    step = PlanStep(id='drill', title='Drill', goal_id='MaintainResource-Plasteel', action=actions[0], completion_criteria='Native building')
    plan.spec.steps.append(step); plan.progress[step.id] = StepProgress()
    assert drilling_policy(plan) == ''
    plan.progress[step.id].issued['0'] = {'confirmed': True}
    assert drilling_policy(plan) == 'DeepDrill/10/20/Plasteel/10'
    goal.cancelled = True
    assert drilling_policy(plan).endswith('/0')
    goal.cancelled = False
    goal.status = 'blocked'
    assert drilling_policy(plan).endswith('/0')
    goal.status = 'active'
    goal.evidence['methods'] = {method:['drill']}
    # Power observations changing cannot silently recreate a cancelled facility.
    site['powerW'] = 300
    with pytest.raises(SkillBlocked, match='interrupted'): development_method(goal, sources, {})


def test_owned_native_output_and_depletion_choose_fresh_facility_without_adopting_player_drills():
    goal, sources, site = fixture()
    infrastructure = sources['infrastructure']
    infrastructure['drills'] = [dict(thingId='old',resource='Plasteel',available=True,forbidden=False)]
    infrastructure['owned'] = [dict(thingId='old',defName='DeepDrill',x=9,z=20)]
    assert development_method(goal, sources, {}) is None
    infrastructure['drills'][0]['resource'] = 'ChunkGranite'
    _, actions = development_method(goal, sources, {})
    assert actions[0]['placements'][0]['x'] == 10
    infrastructure['drills'][0].update(resource='Plasteel',forbidden=True)
    with pytest.raises(SkillBlocked, match='forbidden'): development_method(goal, sources, {})


def test_drill_work_progress_is_not_stock_and_old_tick_cannot_reopen_watchdog():
    goal, sources, _ = fixture()
    sources.update(tick=10, sources=[])
    sources['infrastructure'].update(owned=[{'thingId':'owned'}], drills=[{'thingId':'owned','progress':.1}])
    assert not observe_mining_progress(goal, sources)
    sources['infrastructure']['drills'][0]['progress'] = .2
    sources['tick'] = 20
    assert observe_mining_progress(goal, sources)
    assert goal.last_progress_tick == 20 and 'stock' not in goal.evidence
    sources['infrastructure']['drills'][0]['progress'] = .4
    sources['tick'] = 15
    assert not observe_mining_progress(goal, sources)


def test_depleted_drill_power_release_waits_for_native_switch_and_respects_cancellation():
    goal, sources, _ = fixture()
    infrastructure = sources['infrastructure']
    drill = dict(thingId='old', resource='ChunkGranite', switchOn=True, flickWorkers=['worker'], flickDesignated=False)
    infrastructure.update(drills=[drill], owned=[dict(thingId='old',depleted=True)], flickWorkType={'name':'BasicWorker'})
    method, actions = development_method(goal, sources, {})
    assert actions[0]['tool'] == 'home/building_config' and actions[0]['arguments']['power'] == 'off'
    goal.evidence['methods'] = {method:['switch']}
    drill['flickDesignated'] = True
    assert development_method(goal, sources, {}) is None
    assert goal.evidence['work_types'] == [{'name':'BasicWorker'}]
    drill['flickDesignated'] = False
    with pytest.raises(SkillBlocked, match='interrupted'): development_method(goal, sources, {})
    drill['switchOn'] = False
    _, actions = development_method(goal, sources, {})
    assert actions[0]['kind'] == 'place_buildings'
