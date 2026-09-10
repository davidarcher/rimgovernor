"""Lose an actual successful non-idempotent zone receipt before paired restart."""
import asyncio
from copy import deepcopy


async def prepare_uncertain_zone(rt,mixed):
    identity=mixed['zone'];step=next(s for s in rt.current_plan.spec.steps if s.id==identity)
    before=await rt.game.query('home/list_zones',includeCells=True,maxCellsPerZone=10000)
    original=rt.game.invoke;captured=[]
    async def lose_receipt(name,arguments,*args,**kwargs):
        result=await original(name,arguments,*args,**kwargs)
        if name=='home/zone_cells' and arguments.get('op')=='create' and arguments.get('dryRun') is False:
            assert result.get('success') is True and result.get('cellsAccepted',0)>0,result
            captured.append(deepcopy(result))
            raise TimeoutError('Acceptance fixture lost the successful native zone-create receipt')
        return result
    rt.game.invoke=lose_receipt
    rt.manual_execution=(rt.context_token,rt.chat_revision,rt.current_plan.revision)
    try:await rt.hands.advance(rt,max_operations=1,only_ids={identity})
    finally:
        rt.game.invoke=original;rt.manual_execution=None
    assert len(captured)==1,captured
    progress=rt.current_plan.progress[identity]
    assert progress.state=='blocked' and progress.issued=={'0':{'confirmed':False}},progress.model_dump()
    after=await rt.game.query('home/list_zones',includeCells=True,maxCellsPerZone=10000)
    prior={str(z['id']) for z in before['zones']}
    added=[z for z in after['zones'] if str(z['id']) not in prior]
    assert len(added)==1 and added[0]['label']==step.action.label,added
    assert {(c['x'],c['z']) for c in added[0]['gridCells']}=={c for p in step.action.patches for c in p.cells()}
    assert added[0]['plantDef']==step.action.crop
    rt.persist()
    return {'step':identity,'action':step.model_dump(),'progress':progress.model_dump(),
        'lost_native_receipt':captured[0],'native_zone':added[0],'zones':after['zones'],
        'old_token':rt.context_token,'old_direction':rt.chat_revision}


async def verify_uncertain_zone(rt,evidence):
    assert rt.mode=='manual' and rt.context_token!=evidence['old_token']
    progress=rt.current_plan.progress[evidence['step']]
    assert progress.model_dump()==evidence['progress']
    observed=await rt.game.query('home/list_zones',includeCells=True,maxCellsPerZone=10000)
    assert observed['zones']==evidence['zones']
    # Deliver the interrupted queue with its original authority. Reload must discard it.
    rt.manual_requests=[(evidence['step'],evidence['old_token'],evidence['old_direction'])]
    await rt.execute_manual_requests()
    await asyncio.sleep(1)
    assert rt.mode=='manual' and not rt.manual_requests and rt.counters['actions']==0
    after=await rt.game.query('home/list_zones',includeCells=True,maxCellsPerZone=10000)
    assert after['zones']==evidence['zones']
    assert rt.current_plan.progress[evidence['step']].model_dump()==evidence['progress']
    return {'native_zone_id':evidence['native_zone']['id'],'exact_zone_readback':True,
        'uncertain_progress_preserved':True,'stale_queue_discarded':True,'native_actions':0,'mode':rt.mode}
