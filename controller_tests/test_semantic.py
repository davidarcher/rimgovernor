import json
import pytest
from pydantic import ValidationError
from rimbot.semantic_models import ObjectiveProposal,WorkObjective
from rimbot.semantic import retain_project,reconcile_projects
from rimbot.strategies import StrategyLibrary
from rimbot.planner import domains_for
from rimbot.contracts import Proposal,Action


def objective(**kw):
    return WorkObjective(kind='construction',outcome='Sleeping capacity',success_signals=['Usable sleeping capacity meets colonist count'],**kw)


def test_objective_contract_cannot_contain_native_actions():
    with pytest.raises(ValidationError):ObjectiveProposal(summary='x',actions=[])
    with pytest.raises(ValidationError):WorkObjective(kind='build_kitchen',outcome='x',success_signals=['x'])


def test_project_identity_and_native_progress_are_not_goal_completion():
    memory={'work':[]}
    p=retain_project(memory,'Infrastructure',objective())
    assert retain_project(memory,'Infrastructure',objective()) is p
    with pytest.raises(ValueError):retain_project(memory,'Survival',objective(project_id=p['project_id']))
    p['work_ids']=['a'];p['status']='awaiting_work';memory['work']=[{'id':'a','status':'issued'}]
    reconcile_projects(memory)
    assert p['status']=='awaiting_work'
    memory['work'][0]['status']='complete'
    assert reconcile_projects(memory) and p['status']=='orders_verified'
    assert p['status']!='complete'
    memory['work']=[]
    assert reconcile_projects(memory) and p['status']=='needs_review'


def test_strategy_retrieval_is_bounded_relevant_and_versioned():
    lib=StrategyLibrary()
    results=lib.search('sleeping beds shelter capacity',2)
    assert results[0]['id']=='sleeping-capacity' and len(results)<=2
    assert all(e['version']>=1 and e['verify'] and e['reconsider'] for e in results)
    assert lib.search('zzzzunmatched')==[]
    with pytest.raises(ValueError):lib.search('food',100)


async def test_managers_only_get_objective_submission_and_relevant_guidance(colony):
    rt,_=colony;rt.cycle_generation=rt.generation
    async def complete(messages,tools,*args):
        assert [t['function']['name'] for t in tools]==['submit']
        context=json.loads(messages[1]['content'])
        assert context['strategy_guidance'][0]['id']=='sleeping-capacity'
        assert 'capabilities' not in context
        assert all('sources' not in entry for entry in context['strategy_guidance'])
        return {'role':'assistant','content':ObjectiveProposal(summary='Sleep',objectives=[objective()]).model_dump_json()},{}
    rt.model.complete=complete
    result=await rt.planner.ask('Infrastructure',{'assigned_task':'sleeping beds shelter capacity','capabilities':[]},ObjectiveProposal)
    assert result.objectives[0].kind=='construction'


async def test_executor_scope_rejects_unrelated_commands(colony):
    rt,_=colony
    assert domains_for('Executor:storage')==('zone_stockpile',)
    with pytest.raises(ValueError):domains_for('Executor:invented')
    action=Action(title='Unrelated access change',endpoint='post_things_set_forbidden',arguments={'map_id':7,'thing_ids':[101],'forbidden':False})
    with pytest.raises(ValueError):rt.planner.validate_submission('Executor:storage',Proposal(summary='x',actions=[action]))
    assert rt.planner.validate_submission('Executor:supply_access',Proposal(summary='x',actions=[action])).actions

async def test_executor_failure_does_not_block_another_approved_project(colony):
    from rimbot.semantic import semantic_review
    from rimbot.semantic_models import ObjectiveDecision as Decision
    from rimbot.model import ModelError
    rt,game=colony;rt.mode='automate';rt.cycle_generation=rt.generation
    async def ask(role,context,contract,thinking):
        if contract is ObjectiveProposal:
            assert game.writes==[]
            return ObjectiveProposal(summary='Prepare supplies',objectives=[
                WorkObjective(kind='storage',outcome='Organize storage',success_signals=['Configured zone']),
                WorkObjective(kind='supply_access',outcome='Allow nearby timber',success_signals=['Timber allowed'])])
        if contract is Decision:
            assert game.writes==[]
            return Decision(response='Prepare supplies',accepted=['Infrastructure:0','Infrastructure:1'])
        if role=='Executor:storage':raise ModelError('Storage details unresolved')
        assert role=='Executor:supply_access' and thinking == rt.settings.reasoning
        return Proposal(summary='Allow timber',actions=[Action(title='Allow timber',endpoint='post_things_set_forbidden',arguments={'map_id':7,'thing_ids':[101],'forbidden':False})])
    rt.planner.ask=ask
    await semantic_review(rt,{'assignments':{'Infrastructure':'Prepare supplies'}},['Infrastructure'])
    assert game.writes==['things/set-forbidden']
    assert [p['status'] for p in rt.memory['projects']]==['needs_review','orders_verified']
    assert rt.memory['work'][0]['project_id']==rt.memory['projects'][1]['project_id']


async def test_deferred_objective_cannot_reach_executor(colony):
    from rimbot.semantic import semantic_review
    from rimbot.semantic_models import ObjectiveDecision as Decision
    rt,game=colony;rt.mode='automate';rt.cycle_generation=rt.generation
    async def ask(role,context,contract,thinking):
        if contract is ObjectiveProposal:return ObjectiveProposal(summary='Sleep',objectives=[objective()])
        assert contract is Decision
        return Decision(response='Keep current work',deferred={'Infrastructure:0':'Existing work adequate'})
    rt.planner.ask=ask
    await semantic_review(rt,{},['Infrastructure'])
    assert not game.writes and not rt.memory['projects']


def test_long_explanation_does_not_invalidate_approval_data():
    from rimbot.semantic_models import ObjectiveDecision as Decision
    decision=Decision(response='x'*1000,accepted=['Infrastructure'])
    assert len(decision.response)>650 and decision.accepted==['Infrastructure']
    with pytest.raises(ValidationError):Decision(response=42,accepted=['Infrastructure'])

async def test_manager_parallelism_is_bounded_and_orders_wait_for_arbitration(colony):
    import asyncio
    from rimbot.semantic import semantic_review
    from rimbot.semantic_models import ObjectiveDecision as Decision
    rt,game=colony;rt.mode='automate';rt.cycle_generation=rt.generation
    rt.settings.manager_parallelism=2
    active=0;peak=0;finished=[]
    async def ask(role,context,contract,thinking):
        nonlocal active,peak
        assert not game.writes
        if contract is ObjectiveProposal:
            active+=1;peak=max(peak,active)
            await asyncio.sleep(.02)
            active-=1;finished.append(role)
            return ObjectiveProposal(summary='No new objective')
        assert len(finished)==3 and active==0
        return Decision(response='Current work adequate',accepted=[])
    rt.planner.ask=ask
    await semantic_review(rt,{},['Infrastructure','Survival','Workforce'])
    assert peak==2 and rt.status['active_roles']==[] and not game.writes

async def test_executor_checks_native_definition_requirements_before_retaining_draft(colony):
    from types import SimpleNamespace
    rt,_=colony
    class Definition:
        def_name='AnimalBed'
        def model_dump(self):return {'def_name':self.def_name,'is_bed':True,'bed_humanlike':False}
    original=rt.api.call
    async def call(name,args,**kwargs):
        if name=='construction_definitions':return SimpleNamespace(items=[Definition()])
        return await original(name,args,**kwargs)
    rt.api.call=call
    action=Action(title='Wrong bed',endpoint='construction_place',arguments={'map_id':7,'buildings':[{'def_name':'AnimalBed','stuff_def_name':'Cloth','rotation':0,'position':{'x':100,'z':100}}]})
    with pytest.raises(ValueError,match='does not meet approved definition requirement'):
        await rt.planner.validate_observation(Proposal(summary='x',actions=[action]),context={'project':{'definition_requirements':{'bed_humanlike':True}}})


def test_decision_context_bounds_rows_without_turning_omissions_into_zero():
    from rimbot.decision_context import decision_context
    source={'resource_overview':{'supplies':{'items':[{'quantity':i} for i in range(10)],'total_groups':15,'omitted_groups':5}},'construction_work':{'total':8,'sites':[{'thing_id':i} for i in range(8)],'workers':[]}}
    result=decision_context(source,{})
    assert result['resource_overview']['supplies']['omitted_groups']==5
    assert result['resource_overview']['supplies']['total_groups']==15
    assert result['construction_work']['omitted']==4
    assert len(source['resource_overview']['supplies']['items'])==10


async def test_streamed_server_error_preserves_actual_context_failure(colony):
    import httpx
    from rimbot.model import LocalModel,ModelError
    rt,_=colony
    def respond(request):return httpx.Response(200,text='data: {"error":{"message":"Context size has been exceeded."}}\n\ndata: [DONE]\n\n')
    model=LocalModel(rt.settings,transport=httpx.MockTransport(respond))
    async def progress(_):pass
    try:
        with pytest.raises(ModelError,match='Context size has been exceeded'):
            await model.complete([],[],False,progress)
    finally:await model.close()


@pytest.mark.parametrize(('query','expected'), [
    ('food on poor gravel soil','poor-soil-food'),
    ('kitchen food poisoning butcher','kitchen-cleanliness'),
    ('first days landing setup','first-days'),
])
def test_practical_strategy_retrieval(query,expected):
    entries=StrategyLibrary().search(query)
    assert entries[0]['id']==expected
    assert all(e['sources'] for e in entries)

async def test_admin_reuses_project_and_retires_duplicate_without_game_deletion(colony):
    from rimbot.semantic import semantic_review
    from rimbot.semantic_models import ObjectiveDecision
    rt,game=colony;rt.mode='automate';rt.cycle_generation=rt.generation
    first=retain_project(rt.memory,'Survival',objective())
    second=retain_project(rt.memory,'Infrastructure',WorkObjective(kind='care',outcome='Beds please',success_signals=['Beds exist']))
    second['work_ids']=['wrong-care']
    rt.memory['work'].append({'id':'wrong-care','status':'issued'})
    async def ask(role,context,contract,thinking):
        if contract is ObjectiveProposal:
            assert len(context['projects'])==2
            return ObjectiveProposal(summary='Sleeping',objectives=[WorkObjective(kind='care',outcome='Beds now',success_signals=['Beds exist'])])
        if contract is ObjectiveDecision:
            return ObjectiveDecision(response='Continue one construction project',accepted=['Survival:0'],keep_projects=[first['project_id']],retire_projects={second['project_id']:'Duplicate with wrong executor'},updates={'Survival:0':objective(project_id=first['project_id'])})
        assert role=='Executor:construction'
        return Proposal(summary='Needs site inspection',blockers=['No verified placement yet'])
    rt.planner.ask=ask
    await semantic_review(rt,{},['Survival'])
    assert len(rt.memory['projects'])==2
    assert second['status']=='retired'
    assert rt.memory['work'][0]['status']=='dismissed'
    assert first['status']=='needs_review'
    assert not game.writes


def test_correcting_kind_does_not_count_old_unrelated_orders_as_progress():
    memory={'work':[]}
    p=retain_project(memory,'Survival',WorkObjective(kind='care',outcome='Beds',success_signals=['Beds exist']))
    p['work_ids']=['bed-rest-order']
    retain_project(memory,'Survival',objective(project_id=p['project_id']))
    assert p['work_ids']==[] and p['previous_work_ids']==['bed-rest-order']


async def test_admin_cannot_forget_existing_projects_or_update_twice(colony):
    from rimbot.semantic_models import ObjectiveDecision
    rt,_=colony
    p=retain_project(rt.memory,'Infrastructure',objective())
    context={'projects':[p],'proposals':{str(i):{'objective':objective(project_id=p['project_id']).model_dump()} for i in range(2)}}
    decision=ObjectiveDecision(response='x',accepted=['0'],deferred={'1':'duplicate'})
    rt.planner.validate_submission('Administrator',decision,context)
    assert decision.keep_projects==[p['project_id']]
    with pytest.raises(ValueError,match='at most one continuation'):
        rt.planner.validate_submission('Administrator',ObjectiveDecision(response='x',accepted=['0','1']),context)
    with pytest.raises(ValueError,match='at most one continuation'):
        rt.planner.validate_submission('Administrator',ObjectiveDecision(response='x',accepted=['0','1'],keep_projects=[p['project_id']]),context)


def test_minor_plantable_terrain_survives_decision_projection():
    from rimbot.decision_context import decision_context
    rows=[{'def_name':name,'fertility':fertility,'nearby_cells':1} for name,fertility in [('Water',0),('Ancient',.05),('Sand',.1),('Gravel',.7),('Soil',1)]]
    result=decision_context({'resource_overview':{'terrain':{'items':rows,'total_groups':5,'omitted_groups':0},'food_crops':{'items':[{'def_name':'Rice','min_fertility':.7}],'total_groups':1,'omitted_groups':0}},'construction_work':{'total':0}}, {})
    assert result['resource_overview']['terrain']['items']==rows
    assert result['labor_state']['queued_construction_sites']==0
    assert [t['def_name'] for t in result['crop_land_comparison']['crops'][0]['matching_nearby_terrain']]==['Gravel','Soil']

def test_supporting_supply_order_does_not_verify_construction_project():
    memory={'work':[]}
    p=retain_project(memory,'Infrastructure',objective())
    p.update(status='awaiting_work',work_ids=['allow'])
    memory['work']=[{'id':'allow','status':'complete','action':{'endpoint':'orders_unforbid_all'}}]
    reconcile_projects(memory)
    assert p['status']=='needs_review' and 'Supporting' in p['progress_note']
    p['kind']='supply_access';p['status']='awaiting_work'
    reconcile_projects(memory)
    assert p['status']=='orders_verified'


def test_manual_priority_toggle_does_not_verify_work_assignment_project():
    memory={'projects':[{'project_id':'p','kind':'work_assignment','status':'awaiting_work','work_ids':['w']}], 'work':[{'id':'w','status':'complete','action':{'endpoint':'post_work_settings'}}]}
    assert reconcile_projects(memory)
    assert memory['projects'][0]['status']=='needs_review'


async def test_omitted_candidates_require_an_explicit_decision(colony):
    from rimbot.semantic_models import ObjectiveDecision
    rt,_=colony
    context={'proposals':{k:{'objective':objective().model_dump()} for k in ['a','b']},'projects':[]}
    with pytest.raises(ValueError,match='Missing candidate IDs: b'):
        rt.planner.validate_submission('Administrator',ObjectiveDecision(response='Proceed with a',accepted=['a']),context)
