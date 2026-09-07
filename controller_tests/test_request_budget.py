import json
import pytest
from rimbot.request_budget import fit_request,project_context,encoded_size


def test_spatial_compaction_is_lossless_and_supply_role_omits_map_plan():
    from rimbot.request_budget import merge_patches
    patches=[{'x1':x,'x2':x,'z1':z,'z2':z} for x in range(30) for z in range(4) if (x,z)!=(10,2)]
    def cells(rows):return {(x,z) for r in rows for x in range(r['x1'],r['x2']+1) for z in range(r['z1'],r['z2']+1)}
    packed=merge_patches(patches)
    assert cells(packed)==cells(patches) and len(packed)<10
    context={'project':{'kind':'construction'},'spatial_reservations':{'corridors':[{'patches':patches}]}}
    assert cells(project_context(context)['spatial_reservations']['corridors'][0]['patches'])==cells(patches)
    assert len(context['spatial_reservations']['corridors'][0]['patches'])==119
    context['project']['kind']='supply_access'
    context['resource_overview']={'supplies':{'items':[{'id':1}]},'animals':{'items':[{'id':2}]}}
    result=project_context(context)
    assert 'spatial_reservations' not in result and 'animals' not in result['resource_overview']
    assert result['resource_overview']['supplies']['items'][0]['id']==1

def test_executor_omits_duplicate_catalog_without_changing_source_or_game_facts():
    context={'project':{'kind':'construction'},'capabilities':{'read':['construction_state'],'propose':['construction_place']},
             'construction_state':{'revision':'current','buildings':[]}}
    projected=project_context(context)
    assert 'capabilities' not in projected
    assert projected['construction_state']==context['construction_state']
    assert context['capabilities']['propose']==['construction_place']
    assert 'capabilities' in project_context({'capabilities':context['capabilities']})

def test_oversized_context_and_tools_preserve_schema_and_call_pairs():
    tools=[{'type':'function','function':{'name':'submit','description':'d'*9000,'parameters':{'type':'object','properties':{'title':{'type':'string'},'cells':{'type':'array','items':{'type':'integer'}}},'required':['title','cells']}}}]
    messages=[{'role':'system','content':'Follow native facts'},{'role':'user','content':json.dumps({'projects':[{'project_id':str(i),'history':'x'*10000} for i in range(100)]})}]
    for i in range(3):messages += [{'role':'assistant','tool_calls':[{'id':str(i),'type':'function','function':{'name':'query','arguments':'{}'}}]},{'role':'tool','tool_call_id':str(i),'content':json.dumps({'rows':['y'*9000]*40})}]
    m,t,info=fit_request(messages,tools,32768,8192)
    assert info['compacted'] and encoded_size(m)+encoded_size(t)<=info['input_budget']
    assert 'title' in t[0]['function']['parameters']['properties']
    ids={c['id'] for row in m for c in row.get('tool_calls',[])}
    assert all(row['tool_call_id'] in ids for row in m if row['role']=='tool')
    assert len(messages)==8 and len(tools[0]['function']['description'])==9000

def test_project_history_is_replaced_by_bounded_current_progress():
    p={'project_id':'a','outcome':'Build','work_ids':['x']*1000,'progress':{'orders':[{'status':'complete'}]*1000,'roof':{'roofed_cells':56}}}
    result=project_context({'project':p})['project']
    assert 'work_ids' not in result and result['progress']['order_counts']=={'complete':1000}
    assert result['progress']['roof']['roofed_cells']==56
    assert len(p['progress']['orders'])==1000

def test_unshrinkable_request_is_rejected_locally():
    with pytest.raises(ValueError,match='cannot fit safely'):fit_request([{'role':'system','content':'x'*100000}],[],16384,8192)


async def test_context_stream_failure_retries_once_with_smaller_budget():
    import httpx
    from rimbot.model import LocalModel
    from rimbot.config import Settings
    requests=[]
    def respond(request):
        requests.append(json.loads(request.content))
        if len(requests)==1:return httpx.Response(200,text='data: '+json.dumps({'error':{'message':'Context size has been exceeded.'}})+'\n\n')
        return httpx.Response(200,text='data: '+json.dumps({'choices':[{'delta':{'content':'ok'},'finish_reason':'stop'}]})+'\n\ndata: [DONE]\n\n')
    async def progress(value):pass
    model=LocalModel(Settings(),transport=httpx.MockTransport(respond))
    try:
        result,_=await model.complete([{'role':'user','content':'hello'}],[],False,progress)
        assert result['content']=='ok' and len(requests)==2
        assert model.context_limit==32768
    finally:await model.close()
