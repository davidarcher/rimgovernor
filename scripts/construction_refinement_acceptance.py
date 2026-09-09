"""Exact construction relocation, player interruption and paired-load cancellation."""
import argparse
import asyncio
import json
from pathlib import Path
from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.headless import isolated_root, prepare
from rimbot.store import Store
from rimbot.player_commands import apply_command
from rimbot.colony_plan import ColonyGoal, CommitSteps
from rimbot.campaign_manifest import capture_manifest
from rimbot.config import Settings
from rimbot.session_checkpoint import create_checkpoint, prepare_resume, stop_for_restart
from rimbot.shelter_handoff import safe_rotation


async def run(args):
    root = isolated_root(args.source_root, args.output/'bridge')
    configuration = prepare(root)
    store = Store(args.output/'initial.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=True,
                       settings=Settings(model=args.model, timeout_seconds=90))
    report = {'outcome':'failed', 'cases':[], 'scope':'Native relocation and interrupted cancellation; pawn construction and freezer cooling are separate acceptance'}
    def record(name, passed, **evidence):
        report['cases'].append(dict(name=name, passed=bool(passed), **evidence))
        (args.output/'progress.json').write_text(json.dumps(report, indent=2))
        print(name+': '+str(bool(passed)), flush=True)
        assert passed, name
    async def command(**payload):
        return await apply_command(rt, payload, token=rt.context_token, revision=rt.chat_revision)
    async def listed():
        result = await rt.game.query('home/list_buildings', aggregate=False, playerOnly=True)
        assert result.get('success') and not result.get('skipped', {}).get('byMaxDetailed')
        return result['buildings']
    async def chat(prompt):
        before = rt.counters['model_calls']
        await rt.steer(prompt)
        revision = rt.chat_revision
        async with asyncio.timeout(180):
            while rt.current_plan.control.get('interpreted_player_revision',0)<revision or rt.deliberating:
                await asyncio.sleep(.5)
        await rt.execute_manual_requests()
        report.setdefault('chat', []).append(dict(prompt=prompt, revision=revision,
            messages=[m for m in rt.chat if m.get('revision')==revision],
            calls=rt.counters['model_calls']-before))
        assert rt.counters['model_calls'] > before
    try:
        (args.output/'manifest.json').write_text(json.dumps(capture_manifest(Path(__file__).resolve().parents[1],
            root, configuration, rt.router.routing.model_dump(mode='json')), indent=2))
        await ready(rt)
        facts = await rt.game.query('home/colony_facts', planning=True)
        while facts.get('forbiddenSupplies'):
            goal=rt.current_plan.colony_goals.setdefault('AllowStartingSupplies',ColonyGoal(priority_class=2,source='PLAYER'))
            method,actions=await rt.controller.skills.compile('AllowStartingSupplies',facts,[])
            steps,_=rt.controller.skills.steps('AllowStartingSupplies',method,actions,facts)
            await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                reason='Allow native starting supplies for construction refinement',steps=steps).decision(rt.current_plan),
                actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
            for _ in steps:
                rt.manual_requests.extend((s.id,rt.context_token,rt.chat_revision) for s in steps
                    if rt.current_plan.progress[s.id].state=='pending')
                await rt.execute_manual_requests()
            assert all(rt.current_plan.progress[s.id].state=='complete' for s in steps)
            goal.evidence.setdefault('methods',{})[method]=[s.id for s in steps]
            facts=await rt.game.query('home/colony_facts',planning=True)
        cells=[]
        for cell in sorted(facts['cells'], key=lambda c:(c['x']-facts['center']['x'])**2+(c['z']-facts['center']['z'])**2):
            if not cell.get('walkable') or cell.get('occupied'): continue
            if any(abs(cell['x']-p['x'])+abs(cell['z']-p['z'])<3 for p in cells): continue
            preview=await rt.game.invoke('home/place_building',dict(defName='Wall',stuff='WoodLog',
                x=cell['x'],z=cell['z'],rotation='north',dryRun=True))
            if preview.get('canPlace') and any(safe_rotation(row) for row in preview.get('rotations',[])):
                cells.append(dict(x=cell['x'],z=cell['z']))
            if len(cells)==9:break
        assert len(cells)==9
        def buildings(indices):
            return {'kind':'place_buildings','placements':[dict(def_name='Wall',materials=['WoodLog'],**cells[i]) for i in indices]}
        source=await command(kind='PlaceBuildings',buildings=buildings([0,1]))
        await rt.execute_manual_requests()
        neighbor=await command(kind='PlaceBuildings',buildings=buildings([2]))
        await rt.execute_manual_requests()
        original={b['thingId'] for b in await listed() if any(b.get('position',{})==cells[i] for i in (0,1))}
        unrelated={b['thingId'] for b in await listed() if b.get('position',{})==cells[2]}
        assert len(original)==2 and len(unrelated)==1
        await command(kind='ModifyResourcePolicy',resource='WoodLog',spending='stop')
        before=rt.current_plan.model_dump()
        refused=False
        try:await command(kind='RelocateConstruction',intent_id=source['step'],replacement=buildings([3,4]))
        except ValueError:refused=True
        record('policy_refusal_preserves_originals',refused and before==rt.current_plan.model_dump()
            and original|unrelated <= {b['thingId'] for b in await listed()})
        await command(kind='ModifyResourcePolicy',resource='WoodLog',spending='normal')
        prompt=('Relocate construction intent '+source['step']+'. Remove its old pending blueprints and place the '
                'replacement walls using this exact inspected construction specification: '+json.dumps(buildings([3,4]))+
                '. Preserve all unrelated construction. Use RelocateConstruction, not a separate unrelated building order.')
        await chat(prompt)
        relocations=[s for s in rt.current_plan.spec.steps if s.id.startswith('relocate-build-')]
        record('local_model_selects_relocation',len(relocations)==1)
        replacement=relocations[0]
        for _ in range(4):await rt.execute_manual_requests()
        present={b['thingId'] for b in await listed()}
        record('relocation_old_removed_new_issued',not original&present and unrelated<=present
            and rt.current_plan.progress[replacement.id].state=='waiting'
            and len(rt.current_plan.progress[replacement.id].issued)==2,
            replacement=replacement.model_dump(), progress=rt.current_plan.progress[replacement.id].model_dump())
        second=await command(kind='PlaceBuildings',buildings=buildings([5,6]))
        await rt.execute_manual_requests()
        removal=await command(kind='CancelConstruction',intent_id=second['step'])
        targets=next(s.action.targets for s in rt.current_plan.spec.steps if s.id==removal['step'])
        native=rt.native
        async def lost(name, arguments, **kwargs):
            result=await native(name,arguments,**kwargs)
            if name=='home/cancel_construction':raise ConnectionError('Acceptance: lost successful cancellation receipt')
            return result
        rt.native=lost
        await rt.execute_manual_requests()
        rt.native=native
        remaining={t.thing for t in targets}&{b['thingId'] for b in await listed()}
        record('lost_receipt_stops_without_replay',len(remaining)==1 and rt.current_plan.progress[removal['step']].state=='blocked')
        await chat('Stop future work for now. Keep all the existing blueprints and frames in place. Do not cancel construction.')
        record('player_preservation_keeps_remaining_orders',remaining|unrelated <= {b['thingId'] for b in await listed()})
        checkpoint=await create_checkpoint(rt,rt.context_token)
        old_token=rt.context_token
        report['checkpoint']=checkpoint
        await stop_for_restart(rt,rt.context_token,checkpoint['manifest_path'])
        await rt.stop();store.close()
        _,state=prepare_resume(checkpoint['manifest_path'])
        store=Store(state/'bridge.sqlite')
        rt=BridgeRuntime(store,root,fresh=True,headless=True,resume=checkpoint['manifest_path'],
            settings=Settings(model=args.model,timeout_seconds=90))
        await ready(rt)
        record('paired_load_preserves_remaining_native_orders',rt.mode=='manual' and rt.context_token!=old_token
            and remaining|unrelated <= {b['thingId'] for b in await listed()} and rt.counters['actions']==0)
        # Explicitly attempt the old accepted action in the new context. Its
        # native identity contract must block even if a caller queues it again.
        rt.manual_requests.append((removal['step'],rt.context_token,rt.chat_revision))
        progress=rt.current_plan.progress[removal['step']]
        progress.state='pending'
        await rt.execute_manual_requests()
        record('old_load_cancellation_cannot_retarget',progress.state=='blocked' and
            remaining|unrelated <= {b['thingId'] for b in await listed()} and rt.counters['actions']==0,
            failure=progress.failure.model_dump())
        await chat('Cancel construction of '+second['step']+'. Remove its remaining pending blueprints and frames. Preserve unrelated construction and completed buildings.')
        present={b['thingId'] for b in await listed()}
        record('fresh_local_request_after_load_removes_only_remaining',not remaining&present and unrelated<=present
            and rt.mode=='manual' and (await rt.game.query('home/status',colonists=False,threats=False))['time']['paused'])
        report['outcome']='passed'
    except Exception as error:
        report['error']=repr(error)
        raise
    finally:
        report['plan']=rt.current_plan.model_dump()
        await rt.stop();store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--model',default='qwen3.5-4b')
    args=parser.parse_args()
    asyncio.run(asyncio.wait_for(run(args),900))
