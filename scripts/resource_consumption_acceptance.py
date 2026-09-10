"""Change ordinary stock after bill admission and verify final consumption protection."""
from rimgovernor.native_scenario import advance_game
import argparse,asyncio,hashlib,inspect,json,shutil,time,traceback
import xml.etree.ElementTree as ET
from pathlib import Path
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.bridge import runtime_file_read
from rimgovernor.campaign_manifest import capture_manifest
from rimgovernor.headless import isolated_root,prepare
from rimgovernor.session_checkpoint import read_checkpoint
from rimgovernor.store import Store
from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready

async def run(args):
    data=read_checkpoint(args.checkpoint)
    root=isolated_root(Path(data['root']),args.output/'bridge')
    shutil.copy2(args.checkpoint.parent/'game.rws',root/'profile/Saves/RimGovernor-tribal8-baseline.rws')
    config=prepare(root);store=Store(args.output/'state.sqlite')
    rt=BridgeRuntime(store,root,fresh=True,headless=True,model_factory=lambda _:NoInference())
    report={'outcome':'failed','checkpoint':str(args.checkpoint),'checkpoint_data':data,'cases':[],'save_edits':[]}
    def record(name,passed,**evidence):
        report['cases'].append(dict(name=name,passed=bool(passed),**evidence))
        (args.output/'progress.json').write_text(json.dumps(report,indent=2))
        print(name+': '+str(bool(passed)),flush=True);assert passed,name
    async def window(ticks):
        if rt.review_task and not rt.review_task.done():await rt.review_task
        clock = await advance_game(rt, ticks, report, timeout=60)
        return clock
    async def snapshot(name):
        await rt.bridge.call('rimworld/save_game',saveName=name)
        path=root/'headless-profile/Saves'/f'{name}.rws';tree=ET.parse(path)
        unfinished=[x for x in tree.findall('.//thing') if x.findtext('def','').startswith('Unfinished')]
        jobs=[{'id':x.findtext('id'),'job':ET.tostring(x.find('jobs'),encoding='unicode')}
            for x in tree.findall('.//thing') if x.findtext('def')=='Human' and x.find('jobs') is not None]
        return {'save':str(path),'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),
            'unfinished':[ET.tostring(x,encoding='unicode') for x in unfinished],'jobs':jobs}
    async def facts():return await rt.game.query('home/colony_facts',planning=True)
    async def bills():return (await rt.game.invoke('home/bills',dict(action='list',bench=bench,dryRun=True)))['benches'][0]['bills']
    async def order():
        for pawn in roster['pawns']:
            try:
                receipt=await rt.game.invoke('home/order',dict(action='work',pawn=pawn['thingId'],target=bench,dryRun=False,watch=False),allow_write=True)
                if receipt.get('job',{}).get('verified') and receipt['job'].get('def')=='DoBill':return receipt
            except Exception:continue
        raise AssertionError('No ordinary DoBill job admitted')
    try:
        report['manifest']=capture_manifest(Path(inspect.getfile(BridgeRuntime)).resolve().parents[2],root,config,{'model':'no inference'})
        report['harness_sha256']=hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        await ready(rt)
        listing=await rt.game.invoke('home/bills',dict(action='list',dryRun=True))
        bench=next(b['thingId'] for b in listing['benches'] if any(x['recipe']=='Make_MeleeWeapon_Club' for x in b['bills']))
        initial_bill=next(x for x in await bills() if x['recipe']=='Make_MeleeWeapon_Club')
        initial=await facts();wood=initial['resources']['WoodLog'];product=initial['resources'].get('MeleeWeapon_Club',0)
        recipe=next(x for x in (await rt.game.invoke('home/bills',dict(action='recipes',bench=bench,dryRun=True)))['recipes'] if x['defName']==initial_bill['recipe'])
        needed=next(x['needed'] for x in recipe['ingredients'][0]['costOptions'] if x['defName']=='WoodLog')
        floor=wood-needed
        rt.current_plan.control['resource_policy']={'WoodLog':{'reserve':floor,'spending':'normal'}}
        from rimgovernor.production_policy import sync_production_policy
        async with rt.lock:await sync_production_policy(rt)
        roster=await rt.game.query('home/list_pawns',colonistsOnly=True,work=True,health=True)
        admitted=await order();record('native_job_admitted',True,receipt=admitted,wood=wood,floor=floor,quantity=needed)
        for i in range(40):
            await window(100)
            snap=await snapshot('RimGovernor-consumption-working')
            report['latest']=snap
            if snap['unfinished']:break
        else:raise AssertionError('No native unfinished work observed before timeout')
        record('native_unfinished_work_observed',True,snapshot=snap)
        unfinished=ET.fromstring(snap['unfinished'][0]);unfinished_id=unfinished.findtext('id')
        assert float(unfinished.findtext('workLeft'))>0
        ingredients=ET.tostring(unfinished.find('ingredients'),encoding='unicode')
        assert sum(int(x.findtext('stackCount','1')) for x in unfinished.findall('ingredients/li') if x.findtext('def')=='WoodLog')==needed
        working=next(x for x in snap['jobs'] if ET.fromstring(x['job']).findtext('curJob/targetB')=='Thing_'+unfinished_id)
        job_id=ET.fromstring(working['job']).findtext('curJob/loadID')
        forbidden=await rt.controller.skills.designator('Designator_Forbid')
        allowed=await rt.controller.skills.designator('Designator_Unforbid')
        stock=await rt.game.query('home/list_things',match='WoodLog',includeHeld=False,maxPositionsPerDef=200)
        positions=next(x['positions'] for x in stock['things'] if x['defName']=='WoodLog')
        cell=None
        for item in positions:
            candidate=item.get('position',item)
            try:
                preview=await rt.inspect_native('rimworld/apply_architect_designator',dict(designatorId=forbidden,
                    x=candidate['x'],z=candidate['z'],keepSelected=False,dryRun=True))
                if preview.get('acceptedCellCount',0)>0:cell=candidate;break
            except Exception:continue
        assert cell is not None,'No ordinary remote wood stack can be forbidden'
        report['forbidden_cell']=cell
        before_forbid=await facts()
        report['ordinary_forbid']=await rt.game.invoke('rimworld/apply_architect_designator',dict(designatorId=forbidden,x=cell['x'],z=cell['z'],keepSelected=False),allow_write=True)
        after_forbid=await facts();same_job=await snapshot('RimGovernor-consumption-stock-changed')
        current=next(x for x in same_job['jobs'] if x['id']==working['id'])
        record('stock_changed_after_admission',after_forbid['resources']['WoodLog']<before_forbid['resources']['WoodLog']
            and ET.fromstring(current['job']).findtext('curJob/loadID')==job_id,
            before=before_forbid['resources'],after=after_forbid['resources'],snapshot=same_job)
        await window(3000)
        refused=await snapshot('RimGovernor-consumption-refused');observed=await facts()
        kept=next((ET.fromstring(x) for x in refused['unfinished'] if ET.fromstring(x).findtext('id')==unfinished_id),None)
        record('consumption_refused_preserves_unfinished',kept is not None and float(kept.findtext('workLeft'))<=0
            and ET.tostring(kept.find('ingredients'),encoding='unicode')==ingredients
            and observed['resources'].get('MeleeWeapon_Club',0)==product,
            snapshot=refused,stock=observed['resources'])
        report['ordinary_unforbid']=await rt.game.invoke('rimworld/apply_architect_designator',dict(designatorId=allowed,x=cell['x'],z=cell['z'],keepSelected=False),allow_write=True)
        await order()
        for i in range(40):
            await window(200);observed=await facts()
            if observed['resources'].get('MeleeWeapon_Club',0)>product:break
        else:raise AssertionError('Restored stock did not permit ordinary completion')
        await window(1200);observed=await facts();final_bill=next(x for x in await bills() if x['billId']==initial_bill['billId'])
        keys=set(initial_bill['config'])-{'productCount','productCountOnMap','productCountStored'}
        record('one_recovered_output_no_duplicate',observed['resources'].get('MeleeWeapon_Club',0)==product+1
            and observed['resources']['WoodLog']>=floor,stock=observed['resources'])
        record('player_bill_settings_preserved',all(initial_bill['config'][k]==final_bill['config'][k] for k in keys)
            and initial_bill['filter']==final_bill['filter'] and initial_bill['suspended']==final_bill['suspended'],before=initial_bill,after=final_bill)
        report['outcome']='passed'
    except Exception as error:report.update(error=str(error),traceback=traceback.format_exc())
    finally:
        rt.mode='manual';await rt.stop();store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2))
    print(json.dumps({k:report.get(k) for k in ('outcome','error')}),flush=True)

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--checkpoint',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    asyncio.run(run(parser.parse_args()))
