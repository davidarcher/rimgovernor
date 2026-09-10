"""Observe a newly constructed supply room, roof, filtered zone and delivered medicine."""
import argparse
import asyncio
import json
import time
import traceback
from pathlib import Path

from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready
from rimbot.bridge_observation import observe
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.campaign_manifest import capture_manifest
from rimbot.colony_plan import ColonyGoal, CommitSteps
from rimbot.colony_upkeep import upkeep_nodes, upkeep_method, reconcile_upkeep
from rimbot.headless import isolated_root, prepare
from rimbot.store import Store


async def run(args):
    root = isolated_root(args.source_root, args.output / 'bridge')
    config = prepare(root)
    store = Store(args.output / 'state.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=True, model_factory=lambda _: NoInference())
    report = dict(outcome='failed', samples=[], actions=[], scope='Open ground, wood, exposed medicine and enabled workers are fixture inputs. Room, roof and delivery require ordinary pawn work.')

    async def sample():
        facts = await rt.game.query('home/colony_facts', planning=True)
        report['samples'].append(facts)
        assert not facts['upkeep']['errors'], facts['upkeep']['errors']
        upkeep_nodes(facts, rt.current_plan.control)
        state = rt.current_plan.control['upkeep']['SecureSupplies']
        state['targets'] = [r for r in state['targets'] if r['id'] == report['setup']['medicine']]
        return facts

    async def advance():
        status = await rt.game.query('home/status', colonists=False, threats=True)
        assert status['threats'].get('hostileCount') == 0 and status['threats'].get('huntingPredatorCount') == 0
        from rimbot.native_scenario import advance_game
        await advance_game(rt, 400, report)
        rt.batch = await observe(rt.game)
        await rt.projects.reconcile(rt.game, plan=rt.current_plan)
        rt.reconcile_plan()

    try:
        report['manifest'] = capture_manifest(Path(__file__).resolve().parents[1], root, config, {'model': 'no inference'})
        await ready(rt)
        rt.execution_task = asyncio.current_task()
        report['setup'] = (await rt.bridge.call('test/storeroom_setup', constructionFailure=True)).structuredContent
        assert report['setup']['success'], report['setup']
        goal = rt.current_plan.colony_goals['SecureSupplies'] = ColonyGoal(priority_class=3)
        for expected in ('build_room_shell', 'create_zone', 'native_operation'):
            deadline = time.monotonic() + args.seconds
            compiled = None
            while time.monotonic() < deadline:
                facts = await sample()
                roster = await rt.game.query('home/list_pawns', colonistsOnly=True, work=True, health=True)
                compiled = await upkeep_method(rt, 'SecureSupplies', facts, roster['pawns'])
                if compiled:
                    break
                assert goal.evidence.get('waiting_for_storage_roof'), 'Storage method returned no action without a roof wait'
                await advance()
            assert compiled and compiled[1][0]['kind'] == expected, compiled
            method, actions = compiled
            steps, costs = rt.controller.skills.steps('SecureSupplies', method, actions, facts)
            await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                reason='Native storeroom acceptance', steps=steps).decision(rt.current_plan),
                actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
            goal.steps.extend(s.id for s in steps)
            goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
            rt.current_plan.control.setdefault('costs', {}).update(costs)
            rt.handled_revision = rt.chat_revision
            rt.mode = 'automate'
            await rt.hands.advance(rt)
            rt.mode = 'manual'
            progress = rt.current_plan.progress[steps[-1].id]
            deadline = time.monotonic() + args.seconds
            while progress.state in ('pending', 'executing', 'waiting') and time.monotonic() < deadline:
                await advance()
                facts = await sample()
                reconcile_upkeep(rt, facts)
                if progress.state in ('pending', 'executing'):
                    rt.handled_revision = rt.chat_revision
                    rt.mode = 'automate'
                    await rt.hands.advance(rt)
                    rt.mode = 'manual'
            assert progress.state == 'complete', progress.model_dump()
            report['actions'].append(dict(action=actions, progress=progress.model_dump()))
            if expected == 'create_zone':
                capacity = next(r for r in (await sample())['upkeep']['storageCapacity']
                                if r['item'] == report['setup']['medicine'])
                assert capacity['unreservedCoveredCapacity'] >= 5 and capacity['acceptingCells'] > 0, capacity
                report['filtered_capacity'] = capacity
            print(expected + ': native completion observed', flush=True)
        facts = await sample()
        lineage = facts['upkeep']['construction']
        for step in rt.current_plan.spec.steps:
            if step.action.kind != 'build_room_shell':
                continue
            for receipt in rt.current_plan.progress[step.id].issued.values():
                origin = receipt.get('placed_thing_id')
                assert receipt.get('confirmed') and receipt.get('outcome') == 'placed' and origin
                row = next(r for r in lineage if r['origin'] == origin)
                assert row['stage'] == 'built' and row['present'] and row['blocker'] is None, row
                assert row['current'] != row['origin'], 'Blueprint receipt cannot be the finished wall identity'
        report['construction_lineage'] = lineage
        assert sum(r['failures'] for r in lineage) >= 1, 'Declared native construction fumble was not exercised'
        from rimbot.construction_ownership import owned_buildings
        report['owned_buildings'] = owned_buildings(rt.current_plan, facts)
        assert len(report['owned_buildings']) == len(lineage), 'Every completed room piece needs both receipt and native lineage'
        if getattr(args, 'home_coverage', False):
            from home_coverage_acceptance import verify as verify_home
            await verify_home(rt, report)
        if args.wall_upgrade:
            from wall_upgrade_fixture import verify_upgrade
            await verify_upgrade(rt, report, args.seconds, corner=getattr(args, 'corner', False))
            if getattr(args, 'material_loss', False):
                await verify_upgrade(rt, report, args.seconds, material_loss=True, corner=getattr(args, 'corner', False))
            else:
                await verify_upgrade(rt, report, args.seconds, interrupt=True, corner=getattr(args, 'corner', False))
            facts = await sample()
            lineage = facts['upkeep']['construction']
        report['support_previews'] = []
        wall = None
        for candidate in [r for r in lineage if r['definition'] == 'Wall' and r['present']][:20]:
            preview = await rt.game.invoke('home/roof_support', dict(target=candidate['current']))
            report['support_previews'].append(preview)
            if preview['supportWithoutTarget'] is True and preview['checkedRoofs'] > 0:
                wall, report['support'] = candidate, preview
                break
        assert wall is not None, 'No fully observed supported wall is available for the positive safety case'
        delivery = report['actions'][-1]['progress']['issued']['0']['postcondition']['hauling']
        assert delivery['complete'] and delivery['originalCount'] == 5 and delivery['requiredCount'] >= 5
        assert delivery['source'] == report['setup']['medicine'] and not delivery['blocker']
        report['quantity_contract'] = (await rt.bridge.call('test/haul_quantity_contract')).structuredContent
        assert report['quantity_contract']['success'], report['quantity_contract']
        from rimbot.session_checkpoint import create_checkpoint, stop_for_restart, prepare_resume
        rt.execution_task = None
        report['before_restart'] = await rt.game.query('home/colony_facts', planning=True)
        checkpoint = await create_checkpoint(rt, rt.context_token)
        report['checkpoint'] = checkpoint
        before_plan, old_token = rt.current_plan.model_dump(), rt.context_token
        await stop_for_restart(rt, rt.context_token, checkpoint['manifest_path'])
        await rt.stop(); store.close()
        _, resumed_state = prepare_resume(checkpoint['manifest_path'])
        store = Store(resumed_state / 'bridge.sqlite')
        rt = BridgeRuntime(store, root, fresh=True, headless=True, resume=checkpoint['manifest_path'],
                           model_factory=lambda _: NoInference())
        await ready(rt)
        assert rt.mode == 'manual' and rt.context_token != old_token
        assert rt.current_plan.model_dump() == before_plan, 'Paired restart changed completed plan or receipts'
        after_restart = await rt.game.query('home/colony_facts', planning=True)
        report['after_restart'] = after_restart
        if getattr(args, 'home_coverage', False):
            await verify_home(rt, report, after_restart=True)
        if args.wall_upgrade:
            held = report['wall_upgrade_material_loss' if getattr(args, 'material_loss', False) else 'wall_upgrade_interruption']['held_original']
            assert any(r['id'] == held for r in after_restart['upkeep']['structures'])
            assert after_restart['upkeep']['wallRemoval'] == report['before_restart']['upkeep']['wallRemoval']
        assert after_restart['upkeep']['construction'] == report['before_restart']['upkeep']['construction']
        before_records = {r['id']: r for r in report['before_restart']['upkeep']['hauling']}
        after_records = {r['id']: r for r in after_restart['upkeep']['hauling']}
        for identity, before in before_records.items():
            # Cached references are deliberately not serialized. Completed proof
            # needs no surviving item; pending portions must resolve exact IDs.
            after = after_records[identity]
            assert {k: v for k, v in before.items() if k != 'portions'} == {k: v for k, v in after.items() if k != 'portions'}
            assert [(p['id'], p['count']) for p in before['portions']] == [(p['id'], p['count']) for p in after['portions']]
        pending = after_records[report['quantity_contract']['pending']]
        assert not pending['complete'] and not pending['blocker'] and all(p['resolved'] for p in pending['portions'])
        from resumed_haul_acceptance import deliver
        await deliver(rt, report, report['quantity_contract']['pending'], args.seconds)
        print('paired restart: saved construction and quantity identities verified', flush=True)
        report['replacement'] = (await rt.bridge.call('test/replace_lineage_wall', target=wall['current'])).structuredContent
        assert report['replacement']['success']
        after = (await rt.game.query('home/colony_facts', planning=True))['upkeep']['construction']
        assert next(r for r in after if r['origin'] == wall['origin'])['present'] is False
        assert not any(r['current'] == report['replacement']['replacement'] for r in after)
        report['sole_holder'] = (await rt.bridge.call('test/isolated_roof_holder')).structuredContent
        assert report['sole_holder']['success'] and report['sole_holder']['nativeSupported']
        report['unsupported_removal'] = await rt.game.invoke('home/roof_support', dict(target=report['sole_holder']['wall']))
        assert report['unsupported_removal']['supportWithoutTarget'] is False
        assert report['unsupported_removal']['blocker'] == 'Removing this wall would leave unsupported roof'
        assert rt.counters['model_calls'] == 0
        report['outcome'] = 'passed'
    except Exception as error:
        report.update(error=str(error), traceback=traceback.format_exc())
        try:
            report['native_attention'] = (await rt.bridge.core('games_get_attention', gameId=rt.bridge.game_id)).structuredContent
        except Exception as attention_error:
            report['attention_error'] = str(attention_error)
    finally:
        report['plan'] = rt.current_plan.model_dump()
        rt.mode, rt.execution_task = 'manual', None
        await rt.stop()
        store.close()
        (args.output / 'result.json').write_text(json.dumps(report, indent=2))
    print(json.dumps({k: report.get(k) for k in ('outcome', 'error')}), flush=True)
    return report['outcome'] == 'passed'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--seconds', type=int, default=240)
    parser.add_argument('--wall-upgrade', action='store_true', help='Require native stonecutting, guarded wall replacement and backup removal')
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
