import pytest
from rimbot.semantic import semantic_review,retain_project
from rimbot.semantic_models import WorkObjective,ObjectiveProposal,ObjectiveDecision
from rimbot.contracts import Proposal,Action

def setup(rt):
    rt.mode='automate';rt.cycle_generation=rt.generation
    p=retain_project(rt.memory,'Infrastructure',WorkObjective(kind='supply_access',outcome='Allow timber',success_signals=['Timber allowed']))
    rt.memory['last_admin_day']=(rt.last_tick or 0)//60000
    return p

async def test_routine_review_directly_executes_owned_project(colony):
    rt,game=colony;p=setup(rt);calls=[]
    async def ask(role,context,contract,thinking):
        calls.append(role)
        assert contract is Proposal and context['project_owner']=='Infrastructure'
        return Proposal(summary='Allow timber',actions=[Action(title='Allow timber',endpoint='post_things_set_forbidden',arguments={'map_id':7,'thing_ids':[101],'forbidden':False})])
    rt.planner.ask=ask
    await semantic_review(rt,{},['Infrastructure'])
    assert calls==['Executor:supply_access'] and game.writes==['things/set-forbidden']
    assert p['status']=='orders_verified'

async def test_conflict_requests_admin_without_executing(colony):
    rt,game=colony;p=setup(rt)
    async def ask(*args):return Proposal(summary='Need priority choice',escalation_reason='Two approved projects require the same scarce supply')
    rt.planner.ask=ask
    await semantic_review(rt,{},[])
    assert rt.memory['admin_requested'] and p['status']=='needs_review' and not game.writes

async def test_daily_admin_kept_project_runs_without_reproposal(colony):
    rt,game=colony;p=setup(rt);rt.memory['last_admin_day']=-1;calls=[]
    async def ask(role,context,contract,thinking):
        calls.append(role)
        if contract is ObjectiveProposal:return ObjectiveProposal(summary='Existing project sufficient')
        if contract is ObjectiveDecision:return ObjectiveDecision(response='Continue existing work',keep_projects=[p['project_id']])
        return Proposal(summary='No new order needed')
    rt.planner.ask=ask
    await semantic_review(rt,{},['Infrastructure'])
    assert calls[-1]=='Executor:supply_access' and len(calls)==3
    assert rt.memory['last_admin_day']==(rt.last_tick or 0)//60000

async def test_identical_unknown_order_is_not_resent(colony):
    rt,game=colony;setup(rt)
    action=Action(title='Allow timber',endpoint='post_things_set_forbidden',arguments={'map_id':7,'thing_ids':[101],'forbidden':False})
    rt.memory['work']=[{'id':'pending','status':'unknown','action':action.model_dump()}]
    await rt.execute(action,'Executor:supply_access')
    assert not game.writes and len(rt.memory['work'])==1

async def test_specialist_command_tools_do_not_promise_another_approval(colony):
    rt,_=colony;setup(rt)
    async def complete(messages,tools,*args):
        commands=[t for t in tools if t['function']['name']=='post_things_set_forbidden']
        assert commands and 'no additional administrator approval' in commands[0]['function']['description']
        return {'role':'assistant','content':'{"summary":"Nothing needed","actions":[]}'},{}
    rt.model.complete=complete
    await rt.planner.ask('Executor:supply_access',{},Proposal,False)

async def test_admin_schema_separates_candidates_from_existing_projects(colony):
    import json
    from rimbot.semantic_models import ObjectiveDecision,WorkObjective
    rt,_=colony;rt.cycle_generation=rt.generation
    objective=WorkObjective(kind='construction',outcome='Beds',success_signals=['Beds exist']).model_dump()
    context={'semantic_objectives':True,'proposals':{'Infrastructure:0':{'owner':'Infrastructure','objective':objective}},'projects':[]}
    async def complete(messages,tools,*args):
        schema=next(t['function']['parameters'] for t in tools if t['function']['name']=='submit')['properties']
        assert schema['accepted']['items']['enum']==['Infrastructure:0']
        assert schema['deferred']['propertyNames']['enum']==['Infrastructure:0']
        assert schema['retire_projects']['maxProperties']==0
        assert schema['keep_projects']['maxItems']==0
        return {'role':'assistant','content':json.dumps({'response':'Proceed','accepted':['Infrastructure:0']})},{}
    rt.model.complete=complete
    result=await rt.planner.ask('Administrator: approve semantic objectives',context,ObjectiveDecision)
    assert result.accepted==['Infrastructure:0']

async def test_failed_admin_cannot_block_existing_approval_or_accept_new_work(colony):
    from rimbot.model import ModelError
    rt,game=colony;p=setup(rt);rt.memory['last_admin_day']=-1
    async def ask(role,context,contract,thinking):
        if contract is ObjectiveProposal:return ObjectiveProposal(summary='New request',objectives=[WorkObjective(kind='construction',outcome='New room',success_signals=['Room exists'])])
        if contract is ObjectiveDecision:raise ModelError('Invalid administrator submission')
        assert context['project']['project_id']==p['project_id']
        return Proposal(summary='Continue supply order',actions=[Action(title='Allow timber',endpoint='post_things_set_forbidden',arguments={'map_id':7,'thing_ids':[101],'forbidden':False})])
    rt.planner.ask=ask
    await semantic_review(rt,{},['Infrastructure'])
    assert game.writes==['things/set-forbidden'] and len(rt.memory['projects'])==1


async def test_reworded_invalid_objective_does_not_stall_other_departments(colony):
    import json
    from rimbot.model import ModelError
    rt,_=colony;rt.cycle_generation=rt.generation;calls=[]
    async def complete(messages,tools,*args):
        schema=tools[0]['function']['parameters']['properties']['objectives']['items']
        assert {'crop_def','target_cells'}<=set(schema['required'])
        calls.append(1)
        payload={'summary':'Attempt '+str(len(calls)),'objectives':[{'kind':'growing','outcome':'Grow food','success_signals':['Food grows']}]}
        return {'role':'assistant','tool_calls':[{'id':str(len(calls)),'type':'function','function':{'name':'submit','arguments':json.dumps(payload)}}]},{}
    rt.model.complete=complete
    with pytest.raises(ModelError,match='same invalid submission three times'):
        await rt.planner.ask('Survival',{},ObjectiveProposal,False)
    assert len(calls)==3
