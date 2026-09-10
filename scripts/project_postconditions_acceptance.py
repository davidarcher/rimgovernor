"""Native zone/facing edits and paired restart through the shared plan and Hands."""
import argparse
import asyncio
from copy import deepcopy
import json
from pathlib import Path
import traceback

from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.colony_plan import CommitSteps, Decision, PlanStep
from rimbot.headless import isolated_root, prepare
from rimbot.session_checkpoint import create_checkpoint, prepare_resume, stop_for_restart
from rimbot.store import Store


async def run(args):
    args.output.mkdir(parents=True, exist_ok=False)
    root = isolated_root(args.source_root, args.output/'bridge')
    prepare(root)
    store = Store(args.output/'state.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=True, model_factory=lambda _: NoInference())
    report = dict(outcome='failed', cases=[], save_edits=[])
    def record(name, passed, **evidence):
        report['cases'].append(dict(name=name, passed=bool(passed), **evidence))
        (args.output/'progress.json').write_text(json.dumps(report, indent=2))
        print(name+': '+str(bool(passed)), flush=True)
        assert passed, name
    async def reconcile():
        await rt.projects.reconcile(rt.game, plan=rt.current_plan)
        rt.reconcile_plan()
    async def zone_read():
        return await rt.game.query('home/list_zones', includeCells=True, maxCellsPerZone=10000, filter=True)
    async def edit_zone(identity, **arguments):
        return await rt.game.invoke('home/zone_cells', dict(zone=identity, dryRun=False, **arguments), allow_write=True)
    async def resume_restored(identity):
        before = rt.counters['actions']
        await rt.commit_strategy(Decision(expected_revision=rt.current_plan.revision, disposition='continue',
            assessment='Requested native state restored', rationale='Player explicitly resumes the restored project',
            reply='Resume restored project', retry_steps=[identity]), actor='strategist',
            expected_token=rt.context_token, expected_revision=rt.chat_revision)
        rt.manual_requests.append((identity, rt.context_token, rt.chat_revision))
        await rt.execute_manual_requests()
        await reconcile()
        assert rt.counters['actions'] == before
    async def checkpoint_reload():
        nonlocal rt, store
        rt.execution_task = None
        checkpoint = await create_checkpoint(rt, rt.context_token)
        expected = rt.current_plan.model_dump()
        projects = rt.projects.dump()
        token = rt.context_token
        await stop_for_restart(rt, token, checkpoint['manifest_path'])
        await rt.stop(); store.close()
        data, state = prepare_resume(checkpoint['manifest_path'])
        store = Store(state/'bridge.sqlite')
        rt = BridgeRuntime(store, root, fresh=True, headless=True, resume=checkpoint['manifest_path'],
                           model_factory=lambda _: NoInference())
        await ready(rt)
        record('paired_restart_preserves_exact_contracts_and_receipts', rt.current_plan.model_dump() == expected
            and rt.projects.dump() == projects and rt.context_token != token and rt.mode == 'manual'
            and rt.batch.summary.end_tick in (data['tick'], data['tick']+1) and rt.counters['actions'] == 0,
            checkpoint=checkpoint, plan=expected, projects=projects)
        rt.execution_task = asyncio.current_task()
    try:
        await ready(rt)
        rt.execution_task = asyncio.current_task()
        facts = await rt.game.query('home/colony_facts', planning=True)
        candidates = sorted((c for c in facts['cells'] if c['walkable'] and not c['occupied'] and not c.get('zone')),
            key=lambda c: (c['x']-facts['center']['x'])**2+(c['z']-facts['center']['z'])**2)
        used = set()
        steps = []
        for identity, kind in [('farm', 'growing'), ('stock', 'stockpile')]:
            for c in candidates:
                cell = (c['x'], c['z'])
                if cell in used: continue
                request = dict(op='create', zoneType=kind, label='B07 '+identity, x=cell[0], z=cell[1],
                    width=1, height=1, dryRun=True, **({'plant': 'Plant_Rice'} if kind == 'growing' else {'preset': 'food', 'priority': 'Important'}))
                try:
                    preview = await rt.inspect_native('home/zone_cells', request)
                    if preview.get('cellsAccepted') != 1: continue
                except ValueError: continue
                used.add(cell)
                action = dict(kind='create_zone', zone_type=kind, label='B07 '+identity,
                    patches=[dict(x=cell[0], z=cell[1], width=1, height=1)],
                    **({'crop': 'Plant_Rice'} if kind == 'growing' else {'preset': 'food', 'priority': 'Important'}))
                steps.append(PlanStep(id=identity, title=identity, source='PLAYER', action=action, completion_criteria='Exact native contract'))
                break
            else: raise AssertionError('No legal '+kind+' cell')
        for rotation in ('north', 'east', 'south', 'west'):
            for c in candidates:
                cell = (c['x'], c['z'])
                if any(max(abs(cell[0]-x), abs(cell[1]-z)) < 3 for x, z in used): continue
                preview = await rt.inspect_native('home/place_building', dict(defName='SleepingSpot', x=cell[0], z=cell[1], rotation=rotation, dryRun=True))
                if not preview.get('canPlace'): continue
                used.add(cell)
                steps.append(PlanStep(id=rotation, title=rotation+' sleeping spot', source='PLAYER',
                    action=dict(kind='place_buildings', placements=[dict(def_name='SleepingSpot', x=cell[0], z=cell[1], rotation=rotation)]),
                    completion_criteria='Exact built facing'))
                break
            else: raise AssertionError('No legal rotated footprint')
        steps.append(PlanStep(id='dependent', title='Dependent research inspection', source='PLAYER',
            after=[dict(step=s.id, when='complete') for s in steps], completion_criteria='Native research read',
            action=dict(kind='native_operation', tool='home/research', arguments={'dryRun': True})))
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision, reason='Native B07 postconditions', steps=steps).decision(rt.current_plan),
            actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
        rt.manual_requests.extend((s.id, rt.context_token, rt.chat_revision) for s in steps[:-1])
        await rt.execute_manual_requests()
        await reconcile()
        record('native_zones_and_all_four_built_facings', all(rt.current_plan.progress[s.id].state == 'complete' for s in steps[:-1]),
               plan=rt.current_plan.model_dump(), zones=await zone_read(), projects=rt.projects.dump())
        await checkpoint_reload()
        await reconcile()
        record('fresh_load_rechecks_exact_postconditions', [s.id for s in rt.current_plan.ready()] == ['dependent'])
        original_receipts = {s.id: deepcopy(rt.current_plan.progress[s.id].issued) for s in steps[:-1]}
        original_projects = rt.projects.dump()
        zone_ids = {s.id: next(p for p in rt.projects.rows if p.id == rt.current_plan.progress[s.id].project_id).targets[0].zone_id for s in steps[:2]}
        for identity, edit, restore in [
            ('stock', dict(op='filter', priority='Critical'), dict(op='filter', priority='Important')),
            ('stock', dict(op='filter', preset='nothing'), dict(op='filter', preset='food')),
            ('farm', dict(op='crop', plant='Plant_Potato'), dict(op='crop', plant='Plant_Rice')),
            ('farm', dict(op='settings', sow=False), dict(op='settings', sow=True)),
            ('farm', dict(op='settings', cut=False), dict(op='settings', cut=True)),
        ]:
            receipt = await edit_zone(zone_ids[identity], **edit)
            await reconcile()
            progress = rt.current_plan.progress[identity]
            record('native_edit_blocks_'+identity+'_'+next(k for k in edit if k != 'op'), progress.state == 'blocked'
                and progress.failure.code == 'plan_invalidated' and not list(rt.current_plan.ready())
                and progress.issued == original_receipts[identity], receipt=receipt, zones=await zone_read())
            await edit_zone(zone_ids[identity], **restore)
            await resume_restored(identity)
            record('explicit_restoration_verified_without_replay', progress.state == 'complete' and progress.issued == original_receipts[identity])
        extra = next(c for c in candidates if (c['x'], c['z']) not in used)
        extra_cell = f"{extra['x']},{extra['z']}"
        await edit_zone(zone_ids['farm'], op='add', cells=extra_cell)
        await reconcile()
        record('native_zone_expansion_invalidates_exact_geometry', rt.current_plan.progress['farm'].failure.code == 'plan_invalidated', zones=await zone_read())
        await edit_zone(zone_ids['farm'], op='remove', cells=extra_cell)
        await resume_restored('farm')
        record('native_geometry_restoration_keeps_original_receipt', rt.current_plan.progress['farm'].state == 'complete'
            and rt.current_plan.progress['farm'].issued == original_receipts['farm'])
        east = next(s for s in steps if s.id == 'east').action.placements[0]
        deconstruct = await rt.controller.skills.designator('Designator_Deconstruct')
        await rt.game.invoke('rimworld/apply_architect_designator', dict(designatorId=deconstruct,
            x=east.x, z=east.z, keepSelected=False, dryRun=False), allow_write=True)
        await rt.game.invoke('home/place_building', dict(defName='SleepingSpot', x=east.x, z=east.z,
            rotation='west', dryRun=False), allow_write=True)
        await reconcile()
        record('native_replacement_with_wrong_facing_invalidates_dependency', rt.current_plan.progress['east'].state == 'blocked'
            and rt.current_plan.progress['east'].failure.code == 'plan_invalidated'
            and rt.current_plan.progress['east'].issued == original_receipts['east'],
            buildings=await rt.game.query('home/list_buildings', match='SleepingSpot', aggregate=False, playerOnly=True))
        await edit_zone(zone_ids['farm'], op='remove', cells=';'.join(f'{p.x},{p.z}' for p in steps[0].action.patches))
        await reconcile()
        record('native_zone_removal_invalidates_exact_target', rt.current_plan.progress['farm'].failure.code == 'plan_invalidated', zones=await zone_read())
        await checkpoint_reload()
        record('held_edit_survives_restart_without_duplicate_orders', rt.current_plan.progress['farm'].state == 'blocked'
            and rt.current_plan.progress['farm'].issued == original_receipts['farm'] and rt.counters['actions'] == 0)
        old_token, old_revision = rt.context_token, rt.chat_revision
        before = rt.counters['actions']
        await rt.bridge.call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline', readiness='visual', timeoutMs=90000)
        await rt.sync_identity()
        rt.manual_requests = [('dependent', old_token, old_revision)]
        await rt.execute_manual_requests()
        record('native_rewind_discards_stale_dependent_request', rt.mode == 'manual' and rt.context_token != old_token
            and not rt.manual_requests and rt.counters['actions'] == before, zones=await zone_read())
        report.update(outcome='passed', model_calls=rt.counters['model_calls'])
    except Exception as error:
        report.update(error=str(error), traceback=traceback.format_exc(), plan=rt.current_plan.model_dump())
    finally:
        rt.mode, rt.execution_task = 'manual', None
        await rt.stop(); store.close()
        report['cleanup'] = 'owned runtime stopped'
        (args.output/'result.json').write_text(json.dumps(report, indent=2))
    print(json.dumps({k: report.get(k) for k in ('outcome', 'error')}), flush=True)
    if report['outcome'] != 'passed': raise SystemExit(1)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    asyncio.run(run(parser.parse_args()))
