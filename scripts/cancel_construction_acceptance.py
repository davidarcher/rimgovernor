"""Exact native construction cancellation; independent of semantic interpretation."""
import argparse
import asyncio
import json
from pathlib import Path
from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.bridge import BridgeError
from rimbot.headless import isolated_root,prepare
from rimbot.store import Store


async def run(args):
    root=isolated_root(args.source_root,args.output/'bridge');prepare(root)
    store=Store(args.output/'state.sqlite');rt=BridgeRuntime(store,root,fresh=True,headless=True)
    report={'outcome':'failed','cases':[],'scope':'Native blueprint cancellation only; no frame-refund or semantic acceptance'}
    def record(name,passed,**evidence):
        report['cases'].append(dict(name=name,passed=bool(passed),**evidence))
        (args.output/'progress.json').write_text(json.dumps(report,indent=2))
        assert passed,name
    async def listed():
        return await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
    async def cancel(arguments):
        return (await rt.bridge.call('home/cancel_construction',**arguments)).structuredContent
    try:
        await ready(rt)
        facts=await rt.game.query('home/colony_facts',planning=True)
        candidates=sorted((c for c in facts['cells'] if c['walkable'] and not c['occupied']),
            key=lambda c:(c['x']-facts['center']['x'])**2+(c['z']-facts['center']['z'])**2)
        targets=[]
        for definition in ('Wall','Wall','SleepingSpot'):
            for cell in candidates:
                if any(t['position']['x']==cell['x'] and t['position']['z']==cell['z'] for t in targets):continue
                request={'defName':definition,'x':cell['x'],'z':cell['z'],'rotation':'north','dryRun':True}
                if definition=='Wall':request['stuff']='WoodLog'
                preview=await rt.game.invoke('home/place_building',request)
                if preview.get('canPlace') is not True:continue
                await rt.game.invoke('home/place_building',dict(request,dryRun=False),allow_write=True)
                observed=await listed()
                target=next(b for b in observed['buildings'] if b['position']['x']==cell['x'] and b['position']['z']==cell['z']
                    and (b.get('buildDefName') or b['defName'])==definition)
                targets.append(target);break
            else:raise AssertionError('No native legal fixture placement')
        identity=rt.identity
        target,neighbor,completed=targets
        arguments={'colonyId':identity['colonyId'],'loadToken':identity['loadToken'],'mapId':identity['mapId'],
            'thing':target['thingId'],'expectedDef':'Wall','expectedStuff':'WoodLog',
            'x':target['position']['x'],'z':target['position']['z'],'dryRun':True}
        preview=await cancel(arguments)
        record('preview_preserves_order',preview.get('success') and not preview.get('applied')
            and any(b['thingId']==target['thingId'] for b in (await listed())['buildings']),receipt=preview)
        for name,change in [('load',{'loadToken':'stale'}),('map',{'mapId':-1}),('colony',{'colonyId':'different'}),
                            ('position',{'x':arguments['x']+1}),('definition',{'expectedDef':'Door'}),
                            ('material',{'expectedStuff':'Steel'}),('completed',{'thing':completed['thingId'],
                                'expectedDef':'SleepingSpot','expectedStuff':'','x':completed['position']['x'],'z':completed['position']['z']})]:
            refused=False
            try:await cancel(dict(arguments,**change,dryRun=False))
            except BridgeError:refused=True
            present={b['thingId'] for b in (await listed())['buildings']}
            record('refuse_'+name,refused and all(t['thingId'] in present for t in targets))
        receipt=await cancel(dict(arguments,dryRun=False))
        present={b['thingId'] for b in (await listed())['buildings']}
        record('cancel_exact_blueprint',receipt.get('removed') is True and target['thingId'] not in present
            and neighbor['thingId'] in present and completed['thingId'] in present,receipt=receipt)
        refused=False
        try:await cancel(dict(arguments,dryRun=False))
        except BridgeError:refused=True
        record('repeat_refuses_without_retargeting',refused and neighbor['thingId'] in {b['thingId'] for b in (await listed())['buildings']})
        report['outcome']='passed'
    except Exception as error:
        report['error']=str(error)
        raise
    finally:
        await rt.stop();store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2))
    return report['outcome']=='passed'


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    raise SystemExit(0 if asyncio.run(asyncio.wait_for(run(parser.parse_args()),180)) else 1)
