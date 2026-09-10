from copy import deepcopy
from types import SimpleNamespace
import pytest
from rimgovernor.colony_plan import ColonyPlan, ColonyGoal
from rimgovernor.research import prerequisite_queue, refresh, method, selected, eligible_researchers, validate_dispatch


def project(name, deps=()):
    return dict(defName=name, prerequisites=list(deps), hiddenPrerequisites=[], hidden=False,
                knowledgeCategory=None, canStartNow=not deps, requiredResearchBuilding=None, requiredResearchFacilities=[])


def test_prerequisites_are_native_bounded_and_topological():
    graph = {str(i): project(str(i), [str(i-1)] if i else []) for i in range(12)}
    assert prerequisite_queue(graph, [], ['11']) == list(map(str, range(8)))
    assert prerequisite_queue(graph, ['0', '1'], ['4']) == ['2', '3', '4']
    graph['0']['hiddenPrerequisites'] = ['11']
    with pytest.raises(ValueError, match='Cyclic'):
        prerequisite_queue(graph, [], ['11'])


@pytest.mark.parametrize('change', ['missing', 'unknown', 'hidden', 'category'])
def test_unknown_research_blocks(change):
    graph = {'A': project('A', ['B']), 'B': project('B')}
    if change == 'missing': graph.pop('B')
    if change == 'unknown': graph['B'].pop('prerequisites')
    if change == 'hidden': graph['B']['hidden'] = True
    if change == 'category': graph['B']['knowledgeCategory'] = 'Entities'
    with pytest.raises(ValueError): prerequisite_queue(graph, [], ['A'])


class Runtime:
    def __init__(self):
        self.current_plan = ColonyPlan(colony_goals={'EnsureCooking': ColonyGoal(priority_class=2, evidence={'required_capabilities': ['FueledStove']})})
        self.context_token, self.chat_revision = 'load1', 1
        self.identity = dict(colonyId='colony', loadToken='load1', mapId=1)
        self.controller = SimpleNamespace(policy=SimpleNamespace(blocked_after_ticks=100))
        self.snapshot = dict(success=True, available=[project('A')], locked=[project('B', ['A'])],
                             finished=[], current=None, researchBenches={'anyPowered': True, 'benches': [dict(defName='SimpleResearchBench', powered=True, facilities=[])]})
        self.capability = dict(known=True, prerequisites=['B'], researchReady=False, availableNow=False)
        self.game = SimpleNamespace(invoke=self.invoke)
        self.calls = []
    async def invoke(self, name, args):
        assert name == 'home/research'
        assert args.get('dryRun') is True
        self.calls.append(args)
        return {'capability': deepcopy(self.capability)} if 'capability' in args else deepcopy(self.snapshot)
    async def ensure_context(self, token):
        assert token == self.context_token


def inputs():
    return (dict(tick=1, definitions={'FueledStove': {'available': False}}),
            [dict(thingId='pawn', work={'applies': True, 'types': [dict(name='Research', disabled=False, priority=1)]})],
            [('EnsureCooking', 2)])


@pytest.mark.asyncio
async def test_explicit_project_uses_shared_prerequisites_and_dispatch_ownership():
    rt=Runtime();facts,people,_=inputs()
    rt.current_plan.colony_goals={'EnsureResearch':ColonyGoal(priority_class=3,target={'project':'B'})}
    await refresh(rt,facts,people,[])
    _,actions=await method(rt)
    assert actions[0]['arguments']['set']=='A'
    await validate_dispatch(rt,actions[0]['arguments'])
    rt.current_plan.colony_goals['EnsureResearch'].target['project']='Other'
    with pytest.raises(ValueError):await validate_dispatch(rt,actions[0]['arguments'])


@pytest.mark.asyncio
async def test_missing_basic_laboratory_uses_shared_native_construction(monkeypatch):
    rt=Runtime();facts,people,nodes=inputs()
    facts['definitions']['SimpleResearchBench']={'available':True}
    rt.snapshot['researchBenches']['benches']=[]
    await refresh(rt,facts,people,nodes)
    goal=rt.current_plan.colony_goals['EnsureResearch']
    assert goal.status=='active' and goal.evidence['research']['build_laboratory']=='SimpleResearchBench'
    from unittest.mock import AsyncMock
    placement=AsyncMock(return_value={'kind':'place_buildings','placements':[{'def_name':'SimpleResearchBench','x':1,'z':2}]})
    monkeypatch.setattr('rimgovernor.development.placement',placement)
    _,actions=await method(rt,facts)
    assert actions[0]['kind']=='place_buildings'
    placement.assert_awaited_once_with(rt,facts,'SimpleResearchBench',indoors=True,goal=goal)


@pytest.mark.asyncio
async def test_controller_retains_research_returned_by_native_preparation(monkeypatch):
    from test_colony_controller import Replay
    rt=Replay()
    async def prepared(runtime,*args):
        runtime.current_plan.colony_goals['EnsureResearch']=ColonyGoal(priority_class=3)
        return [('EnsureResearch',3)]
    monkeypatch.setattr('rimgovernor.research.refresh',prepared)
    from unittest.mock import AsyncMock
    monkeypatch.setattr('rimgovernor.research.method',AsyncMock(return_value=None))
    await rt.controller.cycle()
    assert rt.current_plan.colony_goals['EnsureResearch'].status!='complete'


@pytest.mark.asyncio
async def test_selection_waits_for_labor_and_verifies_unlock():
    rt = Runtime(); facts, people, nodes = inputs()
    assert await refresh(rt, facts, people, nodes) == [('EnsureResearch', 3)]
    name, actions = await method(rt)
    assert actions[0]['arguments'] == dict(set='A', expectedCurrent='', watch=False, dryRun=False, **rt.identity)
    selected(rt, 'A')
    rt.snapshot['current'] = dict(project('A'), progress=1)
    await refresh(rt, facts, people, nodes)
    assert await method(rt) is None
    assert rt.current_plan.colony_goals['EnsureResearch'].status == 'active'
    rt.snapshot.update(current=None, finished=['A'])
    rt.snapshot['locked'][0]['canStartNow'] = True
    await refresh(rt, facts, people, nodes)
    assert (await method(rt))[1][0]['arguments']['set'] == 'B'
    selected(rt, 'B')
    rt.snapshot.update(current=None, finished=['A', 'B'])
    await refresh(rt, facts, people, nodes)
    assert rt.current_plan.colony_goals['EnsureResearch'].status == 'blocked'
    rt.capability.update(researchReady=True, availableNow=True)
    assert await refresh(rt, facts, people, nodes) == []
    assert rt.current_plan.colony_goals['EnsureResearch'].status == 'complete'


@pytest.mark.asyncio
@pytest.mark.parametrize('case', ['player', 'cleared', 'load', 'direction', 'power', 'worker', 'override', 'stalled'])
async def test_research_holds_preserve_player_and_unknowns(case):
    rt = Runtime(); facts, people, nodes = inputs()
    selected(rt, 'A')
    rt.snapshot['current'] = dict(project('A'), progress=1)
    if case == 'player': rt.snapshot['current']['defName'] = 'PlayerChoice'
    if case == 'cleared': rt.snapshot['current'] = None
    if case == 'load': rt.context_token = 'load2'
    if case == 'direction': rt.current_plan.control['player_direction'] = 1
    if case == 'power': rt.snapshot['researchBenches']['benches'][0]['powered'] = None
    if case == 'worker': people[0]['downed'] = True
    if case == 'override': rt.current_plan.control['work_overrides'] = {'pawn': {'Research': 0}}
    if case == 'stalled':
        await refresh(rt, facts, people, nodes)
        facts['tick'] = 200
    await refresh(rt, facts, people, nodes)
    assert rt.current_plan.colony_goals['EnsureResearch'].status == 'blocked'
    assert rt.current_plan.colony_goals['EnsureResearch'].evidence['research']['blocker']


@pytest.mark.asyncio
async def test_obsolete_goal_drops_queue_without_replacing_native_project():
    rt = Runtime(); facts, people, nodes = inputs()
    await refresh(rt, facts, people, nodes)
    selected(rt, 'A'); rt.snapshot['current'] = dict(project('A'), progress=1)
    rt.current_plan.colony_goals['EnsureCooking'].cancelled = True
    assert await refresh(rt, facts, people, []) == []
    assert rt.current_plan.colony_goals['EnsureResearch'].evidence['research']['queue'] == []
    assert all('set' not in call for call in rt.calls)


@pytest.mark.asyncio
async def test_no_speculative_research_reads_without_need():
    rt = Runtime()
    assert await refresh(rt, {'definitions': {}}, [], []) == []
    assert not rt.calls


def test_laboratory_power_and_facilities_belong_to_the_same_bench():
    from rimgovernor.research import usable_laboratories
    target = dict(requiredResearchBuilding='HiTech', requiredResearchFacilities=['Analyzer'])
    benches = {'benches': [dict(defName='Simple', powered=True, facilities=[dict(defName='Analyzer', active=True)]),
                           dict(defName='HiTech', powered=False, facilities=[])]}
    assert usable_laboratories(target, benches) == []
    benches['benches'][1].update(powered=True, facilities=[dict(defName='Analyzer', active=True)])
    assert usable_laboratories(target, benches) == [benches['benches'][1]]


@pytest.mark.asyncio
async def test_prepared_research_cannot_outlive_cancelled_owner_or_changed_direction():
    from rimgovernor.research import validate_dispatch
    rt = Runtime(); facts, people, nodes = inputs()
    await refresh(rt, facts, people, nodes)
    _, actions = await method(rt)
    await validate_dispatch(rt, actions[0]['arguments'])
    rt.current_plan.colony_goals['EnsureCooking'].cancelled = True
    with pytest.raises(ValueError, match='obsolete'):
        await validate_dispatch(rt, actions[0]['arguments'])
    rt.current_plan.colony_goals['EnsureCooking'].cancelled = False
    rt.current_plan.control['player_direction'] = 1
    with pytest.raises(ValueError, match='invalidated'):
        await validate_dispatch(rt, actions[0]['arguments'])


@pytest.mark.asyncio
async def test_research_state_round_trip_does_not_reselect_owned_project():
    rt = Runtime(); facts, people, nodes = inputs()
    await refresh(rt, facts, people, nodes)
    selected(rt, 'A')
    rt.snapshot['current'] = dict(project('A'), progress=23)
    rt.current_plan = ColonyPlan.model_validate_json(rt.current_plan.model_dump_json())
    await refresh(rt, facts, people, nodes)
    assert await method(rt) is None
    assert rt.current_plan.colony_goals['EnsureResearch'].evidence['research']['current']['progress'] == 23


def test_unavailable_construction_records_a_research_bottleneck():
    from rimgovernor.colony_skills import ColonySkills, SkillBlocked
    rt = Runtime()
    with pytest.raises(SkillBlocked, match='No available observed construction definition'):
        ColonySkills(rt).steps('EnsureCooking', 'stove',
            [{'kind':'place_buildings', 'placements':[{'def_name':'LockedBench','x':4,'z':4}]}],
            {'definitions':{'LockedBench':{'available':False}}})
    assert 'LockedBench' in rt.current_plan.colony_goals['EnsureCooking'].evidence['required_capabilities']


@pytest.mark.parametrize('state', ['cancelled', 'advisor'])
def test_cancelled_and_advisory_pending_steps_cannot_request_research(state):
    from rimgovernor.colony_plan import PlanStep, StepProgress
    from rimgovernor.research import needs
    rt = Runtime(); facts, people, nodes = inputs()
    goal = rt.current_plan.colony_goals['EnsureCooking']
    if state == 'cancelled': goal.cancelled = True
    else: goal.source = 'LLM_ADVISOR'
    rt.current_plan.spec.steps.append(PlanStep(id='pending', title='stove', goal_id='EnsureCooking', completion_criteria='Native building exists',
        action={'kind':'place_buildings','placements':[{'def_name':'FueledStove','x':4,'z':4}]}))
    rt.current_plan.progress['pending'] = StepProgress()
    assert needs(rt.current_plan, facts, nodes) == []


def test_research_work_demand_ends_with_the_goal():
    from rimgovernor.production_policy import required_resource_work
    rt = Runtime()
    goal = rt.current_plan.colony_goals['EnsureResearch'] = ColonyGoal(priority_class=3,
        evidence={'research':{'queue':['A']}})
    assert required_resource_work(rt.current_plan) == {'Research':'Intellectual'}
    goal.cancelled = True
    assert required_resource_work(rt.current_plan) == {}


def test_research_shares_development_capacity_until_native_work_finishes():
    from rimgovernor.development_priorities import arbitrate, committed_projects
    from rimgovernor.colony_policy import ColonyPolicy
    from test_colony_controller import roster
    rt = Runtime()
    plan = rt.current_plan
    goal = plan.colony_goals['EnsureResearch'] = ColonyGoal(priority_class=3,
        evidence={'research':{'queue':['A'], 'current':None}})
    plan.colony_goals['EnsureFoodStorage'] = ColonyGoal(priority_class=3)
    nodes = [('EnsureResearch',3),('EnsureFoodStorage',3)]
    facts = dict(tick=1,foodStorage=False)
    _, admitted = arbitrate(plan, facts, roster(), nodes, ColonyPolicy(max_development_projects=1),
                            context='load', direction=0)
    assert len(admitted) == 1
    goal.evidence['research']['current'] = {'defName':'A','progress':1}
    plan.control['research'] = {'owned':'A'}
    _, admitted = arbitrate(plan, facts, roster(), nodes, ColonyPolicy(max_development_projects=1),
                            context='load', direction=0)
    assert admitted == {'EnsureResearch'}
    assert committed_projects(plan) == {'EnsureResearch'}
    goal.status = 'complete'
    assert committed_projects(plan) == set()
