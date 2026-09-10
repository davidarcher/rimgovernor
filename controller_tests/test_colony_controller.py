"""Deterministic scenario replay. Native labor is advanced explicitly by the fixture."""
import asyncio
from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.colony_controller import ColonyController
from rimbot.colony_plan import ColonyPlan, ColonyGoal, PlanSpec, StepProgress
from rimbot.colony_policy import ColonyPolicy, criteria, priority_nodes, allocation, starter_layouts, work_assignment
from rimbot.player_commands import apply_command


def facts(count=3):
    return dict(success=True,tick=100,colonists=count,workers=count,center={'x':30,'z':30},
        foodNutrition=40,nutritionPerDay=count*1.6,foodRunwayDays=40/(count*1.6),
        bedCapacity=0,indoorSleepingCapacity=0,sleepingTemperatureMin=None,sleepingTemperatureMax=None,
        outdoorTemperature=22,resources={'WoodLog':1000},farms=[],cooking=[],forbiddenSupplies=[],
        acquisition=[],foodStorage=False,definitions={
            'Wall':{'available':True,'costs':{'WoodLog':5}}, 'Door':{'available':True,'costs':{'WoodLog':25}},
            'SleepingSpot':{'available':True,'costs':{}},'Campfire':{'available':True,'costs':{'WoodLog':5}},
            'Cooler':{'available':True,'costs':{'ComponentIndustrial':3}},
            'Plant_Rice':{'growDays':3,'harvestNutrition':.3,'fertilityMin':.6}},
        cells=[dict(x=x,z=z,walkable=True,occupied=False,zone=False,fertility=1,supportsLight=True)
               for x in range(8,53) for z in range(8,53)])


def roster(count=3):
    return [dict(thingId=f'Thing_Human{i}',dead=False,downed=False,drafted=False,orderGeneration=0,health={
        'summaryPct':1, 'careObservationVersion':1, 'shouldSeekMedicalRest':False, 'needsTend':False, 'hediffs':[]},
        bio={'skills':[{'name':s,'level':(i*3+j)%12,'passion':'Minor','disabled':False}
                       for j,s in enumerate(('Medicine','Cooking','Construction','Plants','Shooting'))]},
        work={'applies':True,'manualPriorities':True,'types':[dict(name=w,disabled=False,priority=0,priorityStored=0)
            for w in ('Doctor','Cooking','Construction','Growing','PlantCutting','Hunting','Hauling','Cleaning','Patient')]}) for i in range(count)]


class Replay:
    def __init__(self,count=3):
        self.facts=facts(count)
        self.people=roster(count)
        self.current_plan=ColonyPlan()
        self.mode='automate'
        self.context_token='colony:1:load'
        self.identity={'colonyId':'colony','mapId':1,'loadToken':'load'}
        self.chat_revision=self.handled_revision=0
        self.wake=asyncio.Event()
        self.events=[]
        self.controller=ColonyController(self)
        self.game=SimpleNamespace(query=self.query,invoke=self.invoke)
        self.strategic_state=SimpleNamespace(decided=lambda:None)
        self.batch=SimpleNamespace(summary=SimpleNamespace(pawns=[SimpleNamespace(thing_id=p['thingId'],dead=False,
            downed=False,bleeding=False,needs_tend=False,armed=i<2) for i,p in enumerate(self.people)],
            hostile_count=0,hunting_predator_count=0),native={'buildings':{'powerNets':[],'resourceDeficit':[]},'zones':{}})
    def note(self,kind,text,**details): self.events.append(dict(kind=kind,text=text,**details))
    def persist(self): pass
    async def ensure_context(self,token): assert token==self.context_token
    async def query(self,name,**args):
        if name=='home/colony_facts': return deepcopy(self.facts)
        if name=='home/list_pawns': return {'pawns':deepcopy(self.people)}
        raise AssertionError(name)
    async def inspect_native(self,name,args):
        if name=='home/place_building': return {'canPlace':True}
        raise AssertionError(name)
    async def invoke(self,name,args,**kwargs):
        from test_construction_preflight import native_reply
        return native_reply(name,args,canPlace=True)
    async def commit_strategy(self,decision,**args):
        assert args['expected_token']==self.context_token and args['expected_revision']==self.chat_revision
        self.current_plan.commit(decision,actor=args['actor'],tick=self.facts['tick'])
    def labor(self):
        self.facts['tick']+=600
        for step in self.current_plan.ready():
            p=self.current_plan.progress[step.id]
            p.state='waiting'
            p.issued={'0':{'confirmed':True}}
        # Accepted blueprints/commands have no functional effect until this
        # explicit simulated labor boundary. Receipt-only stability is tested below.
        for step in self.current_plan.spec.steps:
            p=self.current_plan.progress[step.id]
            if p.state!='waiting': continue
            action=step.action
            if action.kind=='build_room_shell': self.facts['roofed']=True
            elif action.kind=='place_buildings':
                for b in action.placements:
                    if b.def_name=='SleepingSpot': self.facts['bedCapacity']+=1
                    if b.def_name=='Campfire': self.facts['cooking']=[dict(id='Thing_Campfire1',usable=True,recipes=['CookMealSimple'],bills=[])]
            elif action.kind=='create_zone':
                if action.zone_type=='growing': self.facts['farms']=[dict(edible=True,usableCells=100,growingCells=100)]
                else: self.facts['foodStorage']=True
            elif action.kind=='native_operation':
                if action.tool=='home/pawn_config':
                    pawn=next(p for p in self.people if p['thingId']==action.arguments['pawn'])
                    for pair in action.arguments['work'].split(','):
                        name,value=pair.split('=')
                        w=next(w for w in pawn['work']['types'] if w['name']==name)
                        w['priority']=w['priorityStored']=int(value)
                elif action.tool=='home/bills': self.facts['cooking'][0]['bills']=[dict(recipe='CookMealSimple',suspended=False)]
            p.state='complete'
        if self.facts.get('roofed'):
            self.facts['indoorSleepingCapacity']=self.facts['bedCapacity']
            self.facts['sleepingTemperatureMin']=self.facts['sleepingTemperatureMax']=22


@pytest.mark.asyncio
@pytest.mark.parametrize('offset',[0,2,4])
async def test_fresh_colony_reaches_stable_without_any_model(offset):
    rt=Replay()
    # Obstacles change which template ranks first while retaining viable ordinary terrain.
    for cell in rt.facts['cells']:
        if cell['x']==25+offset and 20<cell['z']<28: cell['occupied']=True
    for _ in range(30):
        await rt.controller.cycle()
        if rt.current_plan.control.get('status')=='FOOTHOLD_STABLE': break
        rt.labor()
    assert rt.current_plan.control['status']=='FOOTHOLD_STABLE'
    assert all(rt.current_plan.control['criteria'].values())
    assert all(step.source=='AUTOPILOT' for step in rt.current_plan.spec.steps)
    assert any(e['kind']=='bootstrap_stability_reached' for e in rt.events)
    size=len(rt.current_plan.spec.steps)
    for _ in range(3): await rt.controller.cycle()
    assert len(rt.current_plan.spec.steps)==size


@pytest.mark.asyncio
async def test_command_acceptance_never_certifies_roofed_shelter():
    rt=Replay()
    await rt.controller.cycle()
    for progress in rt.current_plan.progress.values(): progress.state='complete'
    await rt.controller.cycle()
    assert rt.current_plan.control['status']!='FOOTHOLD_STABLE'
    assert not rt.current_plan.control['criteria']['shelter']


def test_hysteresis_and_unknown_do_not_clear_risk():
    f=facts();f.update(criticalPatients=[],hostiles=0,armed=2,workCoverage=True,powerRequired=False)
    latches={}; policy=ColonyPolicy()
    f['foodRunwayDays']=2
    assert ('EnsureFoodSupply',2) in priority_nodes(f,latches,policy)
    for value in (3.1,2.9,6.9,None):
        f['foodRunwayDays']=value
        priority_nodes(f,latches,policy)
        assert latches['food']
    f['foodRunwayDays']=7.1
    priority_nodes(f,latches,policy)
    assert not latches['food']
    f['sleepingTemperatureMin']=None
    assert not criteria(f,policy)['temperature']


@pytest.mark.parametrize('growing, expected', [(14, False), (15, True), (16, True)])
def test_production_gate_counts_growing_cells_across_edible_farms(growing, expected):
    f = facts(count=3)
    f['farms'] = [
        dict(edible=True, usableCells=100, growingCells=15),
        dict(edible=True, usableCells=100, growingCells=growing),
        dict(edible=False, usableCells=100, growingCells=100),
        dict(usableCells=100, growingCells=100),
        dict(edible=True, usableCells=100),
    ]
    assert criteria(f, ColonyPolicy())['production'] is expected


def test_production_gate_requires_growing_crops():
    f = facts(count=3)
    assert criteria(f, ColonyPolicy())['production'] is False
    f['farms'] = [dict(edible=True, usableCells=100, growingCells=0)]
    assert criteria(f, ColonyPolicy())['production'] is False


def test_production_gate_requires_colonists():
    f = facts()
    f['colonists'] = 0
    assert criteria(f, ColonyPolicy())['production'] is False


def test_unissued_and_native_reservations_do_not_double_spend():
    p=ColonyPlan(control={'costs':{'first':{'0':{'WoodLog':60}}}},progress={'first':StepProgress()})
    f={'resources':{'WoodLog':100},'constructionDeficit':{}}
    assert allocation(p,f,{'WoodLog':50},ColonyPolicy(),survival=True)=={'WoodLog':10}
    p.progress['first'].issued={'0':{'confirmed':True}}
    f['constructionDeficit']={'WoodLog':60}
    assert allocation(p,f,{'WoodLog':50},ColonyPolicy(),survival=True)=={'WoodLog':10}


@pytest.mark.asyncio
async def test_raid_suspends_and_resumes_existing_goals_without_duplicates():
    rt=Replay(); await rt.controller.cycle()
    goal=rt.current_plan.colony_goals['EnsureWorkAssignments']
    rt.batch.summary.hostile_count=2
    await rt.controller.cycle()
    assert goal.status=='suspended'
    assert list(rt.current_plan.ready())==[]
    rt.batch.summary.hostile_count=0
    await rt.controller.cycle()
    assert goal.status=='active'
    assert sum(s.goal_id=='EnsureWorkAssignments' for s in rt.current_plan.spec.steps)==3


@pytest.mark.asyncio
async def test_player_cancellation_and_resource_policy_survive_replay():
    rt=Replay(); await rt.controller.cycle()
    await apply_command(rt,{'kind':'CancelGoal','goal':'EnsureFoodSupply'},token=rt.context_token,revision=0)
    await apply_command(rt,{'kind':'ModifyResourcePolicy','resource':'ComponentIndustrial','spending':'defense_only'},token=rt.context_token,revision=0)
    restored=ColonyPlan.model_validate_json(rt.current_plan.model_dump_json())
    rt.current_plan=restored
    await rt.controller.cycle()
    assert restored.colony_goals['EnsureFoodSupply'].cancelled
    assert restored.control['resource_policy']['ComponentIndustrial']['spending']=='defense_only'


@pytest.mark.asyncio
async def test_player_food_target_updates_same_autopilot_goal():
    rt=Replay(); await rt.controller.cycle()
    before=len(rt.current_plan.colony_goals)
    await apply_command(rt,{'kind':'CreateGoal','goal':'EnsureFoodSupply','food_days':20},token=rt.context_token,revision=0)
    assert len(rt.current_plan.colony_goals)==before
    assert rt.current_plan.colony_goals['EnsureFoodSupply'].source=='PLAYER'
    assert rt.current_plan.control['policy']['food_target_days']==20


def test_work_assignment_respects_disabled_work_and_is_stable():
    pawns=roster()
    next(w for w in pawns[0]['work']['types'] if w['name']=='Doctor')['disabled']=True
    first,covered=work_assignment(pawns)
    assert covered and 'Doctor' not in first[pawns[0]['thingId']]
    assert first==work_assignment(list(reversed(pawns)))[0]


@pytest.mark.asyncio
async def test_player_field_is_existing_work_and_cancellation_suppresses_recreation():
    rt=Replay()
    await apply_command(rt,{'kind':'CreateZone','intent_id':'food-expansion','zone':{
        'kind':'create_zone','zone_type':'growing','label':'Player rice','crop':'Plant_Rice',
        'patches':[{'x':10,'z':10,'width':8,'height':8}]}},token=rt.context_token,revision=0)
    await rt.controller.cycle()
    assert not any(s.goal_id=='EnsureFoodSupply' for s in rt.current_plan.spec.steps)
    await apply_command(rt,{'kind':'CancelGoal','goal':'intent-food-expansion'},token=rt.context_token,revision=0)
    rt.current_plan=ColonyPlan.model_validate_json(rt.current_plan.model_dump_json())
    await rt.controller.cycle()
    assert rt.current_plan.colony_goals['EnsureFoodSupply'].cancelled
    await apply_command(rt,{'kind':'CreateGoal','goal':'EnsureFoodSupply'},token=rt.context_token,revision=0)
    assert not rt.current_plan.colony_goals['EnsureFoodSupply'].cancelled


def test_completed_native_actions_retire_without_losing_receipts_or_pending_work():
    from rimbot.colony_plan import CommitSteps,PlanStep
    plan=ColonyPlan()
    def step(i):
        return PlanStep(id=f'a{i}',title='Configure',source='AUTOPILOT',completion_criteria='Native readback',
            action={'kind':'native_operation','tool':'home/pawn_config','arguments':{'pawn':f'Thing_Human{i}',
                    'work':'Growing=1','dryRun':False}})
    for i in range(100):
        proposal=CommitSteps(expected_revision=plan.revision,reason='Coverage',steps=[step(i)]).decision(plan)
        plan.commit(proposal,actor='strategist',tick=i)
        plan.progress[f'a{i}']=StepProgress(state='complete',issued={'0':{'confirmed':True}})
    assert len(plan.spec.steps)<80 and len(plan.progress)==100
    assert plan.control['retired_steps']['a0']['source']=='AUTOPILOT'
    assert plan.progress['a0'].issued['0']['confirmed']


@pytest.mark.asyncio
async def test_native_food_process_survives_controller_restart_without_duplicate_fields():
    rt=Replay()
    for _ in range(30):
        await rt.controller.cycle()
        rt.labor()
        if rt.current_plan.control.get('status')=='FOOTHOLD_STABLE': break
    rt.current_plan=ColonyPlan()
    rt.facts.update(foodNutrition=1,foodRunwayDays=.2,butchering=[{'id':'ButcherSpot1',
        'bills':[{'recipe':'ButcherCorpseFlesh','suspended':False}]}])
    await rt.controller.cycle()
    assert rt.current_plan.control['simulation_needed'] is True
    assert not rt.current_plan.spec.steps
    assert rt.current_plan.colony_goals['EnsureFoodSupply'].status=='active'


@pytest.mark.asyncio
async def test_unknown_medical_observation_cannot_certify_stability():
    rt=Replay()
    rt.batch.summary.pawns[0].bleeding=None
    await rt.controller.cycle()
    assert rt.current_plan.control['criteria']['medical'] is False
    assert rt.current_plan.colony_goals['CriticalMedical'].status=='blocked'
    assert 'unavailable' in rt.current_plan.colony_goals['CriticalMedical'].reason


def test_spare_capable_pawn_supplies_second_grower_without_displacing_specialists():
    people=roster(8)
    for pawn in people: pawn['equipment']={'armed':True,'primary':{'ranged':True}}
    assigned,covered=work_assignment(people)
    assert covered
    assert sum(w.get('Growing')==1 for w in assigned.values())==2
    assert sum(w.get('Hunting')==1 for w in assigned.values())==2
    for work in ('Cooking','Construction','Doctor'):
        assert sum(w.get(work)==1 for w in assigned.values())==1


@pytest.mark.asyncio
@pytest.mark.parametrize('distance,expected',[(20,'attack'),(60,'draft')])
async def test_small_manhunter_method_uses_native_orders_and_owned_cleanup(distance,expected):
    rt=Replay()
    rt.draft_owners={}
    rt.current_plan.colony_goals['ActiveCombat']=ColonyGoal(priority_class=0)
    for person in rt.people: person['bio'].update(incapableOfRead=True,incapableOfTags=[])
    enemy=dict(thingId='Thing_Hare1',hostile=True,animal=True,predator=False,mentalState='Manhunter',
        animals={'bodySize':.2},nearestColonistDistance=distance)
    rt.game.query=AsyncMock(return_value={'pawns':[enemy]})
    method,actions=await rt.controller.skills.compile('ActiveCombat',rt.facts,rt.people)
    assert len(actions)==2 and all(a['arguments']['action']==expected for a in actions)
    assert all(a['tool']=='home/order' and a['arguments']['dryRun'] is False for a in actions)
    rt.current_plan.colony_goals['ActiveCombat'].evidence['methods'][method]=['a','b']
    assert await rt.controller.skills.compile('ActiveCombat',rt.facts,rt.people) is None
    enemy['animals']['bodySize']=2
    from rimbot.colony_skills import SkillBlocked
    with pytest.raises(SkillBlocked,match='exceeds'):
        await rt.controller.skills.compile('ActiveCombat',rt.facts,rt.people)


def test_cleanup_preempts_routine_work_after_threat_clears():
    f=facts(); f.update(hostiles=0,cleanupPawns=['Thing_Human1'])
    assert ('RestoreWorkers',1) in priority_nodes(f,{},ColonyPolicy())
    f['hostiles']=1
    assert ('RestoreWorkers',1) not in priority_nodes(f,{},ColonyPolicy())


@pytest.mark.asyncio
async def test_initial_naming_uses_exact_native_suggestions_in_shared_plan():
    rt=Replay()
    rt.facts['colonyNaming']={'windowId':42,'factionName':'Native faction','settlementName':'Native town'}
    await rt.controller.cycle()
    step=rt.current_plan.spec.steps[0]
    assert step.goal_id=='ConfirmColonyNames' and step.source=='AUTOPILOT'
    assert step.action.tool=='home/confirm_colony_names'
    assert step.action.arguments==dict(rt.facts['colonyNaming'],dryRun=False)
    assert rt.current_plan.colony_goals['ConfirmColonyNames'].status=='active'
    rt.current_plan.progress[step.id].state='complete'
    await rt.controller.cycle()
    assert rt.current_plan.colony_goals['ConfirmColonyNames'].status=='active'
    rt.facts['colonyNaming']=None
    await rt.controller.cycle()
    assert rt.current_plan.colony_goals['ConfirmColonyNames'].status=='complete'


@pytest.mark.asyncio
async def test_predation_response_requires_confirmed_colony_prey():
    from rimbot.colony_skills import SkillBlocked
    rt=Replay();rt.draft_owners={}
    rt.current_plan.colony_goals['ActiveCombat']=ColonyGoal(priority_class=0)
    for person in rt.people: person['bio'].update(incapableOfRead=True,incapableOfTags=[])
    fox=dict(thingId='Thing_Fox1',hostile=False,animal=True,predator=True,
        animals={'bodySize':.6},nearestColonistDistance=20)
    rt.game.query=AsyncMock(return_value={'pawns':[fox]})
    with pytest.raises(SkillBlocked):
        await rt.controller.skills.compile('ActiveCombat',rt.facts,rt.people)
    rt.batch.native['status_after']={'threats':{'huntingPredators':[
        {'thingId':'Thing_Fox1','preyIsOurs':True,'predatorIsOurs':False}]}}
    _,actions=await rt.controller.skills.compile('ActiveCombat',rt.facts,rt.people)
    assert len(actions)==2 and all(a['arguments']['target']=='Thing_Fox1' for a in actions)


@pytest.mark.asyncio
async def test_work_assignment_reconciles_after_equipping_hunter():
    rt=Replay()
    goal=rt.current_plan.colony_goals['EnsureWorkAssignments']=ColonyGoal(priority_class=2)
    first, actions=await rt.controller.skills.compile('EnsureWorkAssignments',rt.facts,rt.people)
    goal.evidence['methods'][first]=['completed-assignment']
    assert await rt.controller.skills.compile('EnsureWorkAssignments',rt.facts,rt.people) is None
    rt.people[0]['equipment']={'primary':{'ranged':True}}
    second, actions=await rt.controller.skills.compile('EnsureWorkAssignments',rt.facts,rt.people)
    assert second!=first
    assert any('Hunting=1' in a['arguments']['work'] for a in actions)


def test_layout_rejects_walkable_marsh_without_building_support():
    state=facts()
    for cell in state['cells']:
        cell['supportsLight']=cell['x']<27
    layouts=starter_layouts(state)
    assert layouts
    assert all(layout['room']['x']+layout['room']['width']<=27 for layout in layouts)
    for cell in state['cells']: cell.pop('supportsLight')
    assert starter_layouts(state)==[]


def test_fragmented_soil_uses_small_disjoint_patches_and_never_blocks_shelter():
    state=facts(10)
    for cell in state['cells']:
        cell['fertility']=1 if cell['x']%4 in (0,1) and cell['z']%4 in (0,1) else .1
    layouts=starter_layouts(state)
    assert layouts
    for layout in layouts:
        assert layout['farms'] and len(layout['farms'])<=32
        room=layout['room'];used=set()
        room_cells={(x,z) for x in range(room['x'],room['x']+9) for z in range(room['z'],room['z']+9)}
        for patch in layout['farms']:
            cells={(x,z) for x in range(patch['x'],patch['x']+patch['width']) for z in range(patch['z'],patch['z']+patch['height'])}
            assert not cells&(used|room_cells)
            assert all(x%4 in (0,1) and z%4 in (0,1) for x,z in cells)
            used|=cells
    for cell in state['cells']:cell['fertility']=.1
    layouts=starter_layouts(state)
    assert layouts and all(layout['farms']==[] and layout['farm'] is None for layout in layouts)


@pytest.mark.asyncio
async def test_native_resource_work_enables_capable_workers_and_preserves_player_override():
    rt=Replay()
    for pawn in rt.people:
        pawn['work']['types'].append(dict(name='Mining',disabled=False,priority=0,priorityStored=0))
        pawn['bio']['skills'].append(dict(name='Mining',level=8,disabled=False))
    goal=ColonyGoal(priority_class=3, target={'resource':'Steel','quantity':100})
    goal.evidence['work_types']=[{'name':'Mining','skills':['Mining']}]
    rt.current_plan.colony_goals['MaintainResource-Steel']=goal
    rt.current_plan.colony_goals['EnsureWorkAssignments']=ColonyGoal(priority_class=2)
    compiled=await rt.controller.skills.compile('EnsureWorkAssignments',rt.facts,rt.people)
    assert any('Mining=1' in a['arguments']['work'] for a in compiled[1])
    rt.current_plan.control['work_overrides']={p['thingId']:{'Mining':0} for p in rt.people}
    from rimbot.colony_skills import SkillBlocked
    with pytest.raises(SkillBlocked,match='player work overrides'):
        await rt.controller.skills.compile('EnsureWorkAssignments',rt.facts,rt.people)
    goal.cancelled=True
    compiled=await rt.controller.skills.compile('EnsureWorkAssignments',rt.facts,rt.people)
    assert all('Mining=1' not in a['arguments']['work'] for a in compiled[1])


def test_resource_work_selects_another_owner_when_player_disables_one_worker():
    people=roster()
    for pawn in people:
        pawn['work']['manualPriorities']=False
        pawn['work']['types'].append(dict(name='Mining',disabled=False,priority=0,priorityStored=0))
        pawn['bio']['skills'].append(dict(name='Mining',level=10,disabled=False))
    first,_=work_assignment(people,{'Mining':'Mining'})
    owner=next(p for p,work in first.items() if work.get('Mining')==1)
    changed,covered=work_assignment(people,{'Mining':'Mining'},{owner:{'Mining':0}})
    assert covered and changed[owner]['Mining']==0
    assert any(work.get('Mining')==1 for p,work in changed.items() if p!=owner)
