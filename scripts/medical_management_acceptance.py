"""Disposable native B23 care and surgery outcomes; no model calls."""
import argparse
import asyncio
import json
import time
import traceback
from pathlib import Path
from rimbot.bridge import bridge_session, gabs_executable
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_observation import observe
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.colony_plan import ColonyGoal, CommitSteps
from rimbot.colony_policy import derive
from rimbot.campaign_manifest import capture_manifest
from rimbot.headless import prepare
from rimbot.medical_management import care_state
from rimbot.player_commands import apply_command
from rimbot.store import Store


async def run(args):
    root = args.root.resolve()
    config = prepare(root)
    report = dict(passed=False, case=args.case, model_calls=0, checks=[], samples=[])
    report['manifest'] = capture_manifest(Path(__file__).resolve().parents[1], root, config,
        {'mode':'scripted deterministic care and explicit surgery; zero model calls'})
    began = time.monotonic()
    def save():
        (root/'medical-management-result.json').write_text(json.dumps(report, indent=2), encoding='utf8')
    def check(name, condition, **evidence):
        report['checks'].append(dict(name=name, passed=bool(condition), **evidence)); save()
        assert condition, (name, evidence)
        print('PASS '+name, flush=True)
    store = Store(root/'medical-management.sqlite')
    async with bridge_session(gabs_executable(root), config) as bridge:
        rt = BridgeRuntime(store, root, headless=True)
        rt.bridge, rt.game = bridge, BridgeGame(bridge)
        async def refresh():
            await rt.sync_identity()
            rt.batch = await observe(rt.game)
            rt.reconcile_plan()
            people = (await rt.game.query('home/list_pawns', colonistsOnly=True, bio=True, work=True, health=True))['pawns']
            facts = derive(rt.batch, await rt.game.query('home/colony_facts'), rt.controller.policy)
            facts['longTermMedical'] = care_state(people, rt.current_plan.colony_goals.get('MaintainMedicalCare', ColonyGoal(priority_class=2)).evidence.get('health'))
            if facts.get('colonyNaming'):
                await rt.native('home/confirm_colony_names', dict(facts['colonyNaming'], dryRun=False))
            report['samples'].append(dict(tick=facts['tick'], people=[{k:p.get(k) for k in
                ('thingId','job','downed','drafted','health')} for p in people]))
            report['plan'] = rt.current_plan.model_dump(); save()
            rt.handled_revision = rt.chat_revision; rt.wake.clear()
            return facts, people
        async def window():
            assert time.monotonic()-began < args.seconds, 'Acceptance wall timeout'
            from rimbot.surgery import recovery_patients
            start = await rt.supervisor.change('Superfast', max_ticks=6000, surgical_recovery=recovery_patients(rt))
            assert start.get('active'), start
            async with asyncio.timeout(120):
                while rt.supervisor.state.get('active'):
                    await rt.supervisor.poll(); await asyncio.sleep(.2)
            report['clock'] = dict(rt.supervisor.state); save()
            return await refresh()
        async def tend():
            facts, people = await refresh()
            goal = rt.current_plan.colony_goals.setdefault('CriticalMedical', ColonyGoal(priority_class=1))
            if any(rt.current_plan.progress[s].state in ('pending','executing','waiting') for s in goal.steps):
                return
            if not any(p['health']['needsTend'] for p in people):
                return
            selected = await rt.controller.skills.compile('CriticalMedical', facts, people)
            if selected is None: return
            method, actions = selected
            steps, costs = rt.controller.skills.steps('CriticalMedical', method, actions, facts)
            await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                reason='Native medical acceptance', steps=steps).decision(rt.current_plan), actor='strategist',
                expected_token=rt.context_token, expected_revision=rt.chat_revision)
            goal.steps.extend(s.id for s in steps); goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
            rt.current_plan.control.setdefault('costs', {}).update(costs)
            await rt.hands.advance(rt)
        try:
            await bridge.core('games_start', gameId=bridge.game_id); await bridge.connect()
            await bridge.call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline', readiness='visual',
                ignoreModCompatibility=True, timeoutMs=90000)
            await bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
            await rt.sync_identity(); rt.mode = 'automate'
            setup = (await bridge.call('test/medical_management_setup', disease=args.case=='care',
                failSurgery=args.case=='failure')).structuredContent
            report['setup'] = setup; save(); assert setup['success'], setup
            facts, people = await refresh()
            if args.case == 'care':
                goal = rt.current_plan.colony_goals.setdefault('MaintainMedicalCare', ColonyGoal(priority_class=2))
                method, actions = await rt.controller.skills.compile('MaintainMedicalCare', facts, people)
                steps, costs = rt.controller.skills.steps('MaintainMedicalCare', method, actions, facts)
                await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                    reason='Native recovery acceptance', steps=steps).decision(rt.current_plan), actor='strategist',
                    expected_token=rt.context_token, expected_revision=rt.chat_revision)
                goal.steps.extend(s.id for s in steps)
                goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
                rt.current_plan.control.setdefault('costs', {}).update(costs)
                for step in steps:
                    await rt.hands.advance(rt)
                    await refresh()
                    assert rt.current_plan.progress[step.id].state == 'complete'
                facts, people = await refresh()
                check('recovery_work_enabled_by_hands', all(any(w['name']=='PatientBedRest' and w['priority']>0
                    for w in p['work']['types']) for p in people if p['thingId'] in setup['patients']))
                check('native_disease_evidence', all(any(h.get('immunizable') is True and h.get('id')
                    for h in p['health']['hediffs']) for p in people if p['thingId'] in setup['patients']))
            catalog = await rt.inspect_native('home/medical_operations', dict(patient=setup['surgical'], dryRun=True))
            report['catalog'] = catalog; save()
            option = next(r for r in catalog['recipes'] if r['recipe']=='InstallPegLeg' and r['part']==setup['part'])
            check('native_surgery_eligibility', option['supported'], option=option)
            result = await apply_command(rt, dict(kind='RequestSurgery', patient=setup['surgical'],
                recipe=option['recipe'], part=option['part']), token=rt.context_token, revision=rt.chat_revision)
            await rt.hands.advance(rt)
            surgery_id = result['step']
            check('surgery_waits_for_health', rt.current_plan.progress[surgery_id].state=='waiting',
                progress=rt.current_plan.progress[surgery_id].model_dump())
            if args.case == 'shortage':
                receipt = dict(rt.current_plan.progress[surgery_id].issued['0'])
                await bridge.call('test/medical_management_change', op='supplies-off')
                await window()
                check('lost_medicine_keeps_operation_pending', rt.current_plan.progress[surgery_id].state=='waiting')
                observed = await rt.inspect_native('home/medical_operations', dict(patient=setup['surgical'], dryRun=True))
                option = next(r for r in observed['recipes'] if r['recipe']=='InstallPegLeg' and r['part']==setup['part'])
                check('medicine_shortage_observed', option['hasPermittedMedicine'] is False, option=option)
                await bridge.call('test/medical_management_change', op='supplies-on')
                await bridge.call('test/medical_management_change', op='doctors-off')
                await window()
                check('lost_staff_keeps_operation_pending', rt.current_plan.progress[surgery_id].state=='waiting')
                await bridge.call('test/medical_management_change', op='doctors-on')
                check('shortage_preserves_exact_bill', rt.current_plan.progress[surgery_id].issued['0'] == receipt)
            for _ in range(65):
                if args.case == 'care': await tend()
                facts, people = await window()
                if args.case == 'failure' and rt.current_plan.progress[surgery_id].state == 'blocked':
                    progress = rt.current_plan.progress[surgery_id]
                    check('native_failed_operation_not_completed', progress.failure.code in
                        ('surgery_failed_or_cancelled', 'patient_dead'), progress=progress.model_dump(), people=people)
                    count = rt.counters['actions']
                    await refresh()
                    check('failed_operation_never_reissued', rt.counters['actions'] == count)
                    report['passed'] = True
                    return
                if args.case == 'shortage' and rt.current_plan.progress[surgery_id].state == 'complete':
                    check('same_operation_completed_after_shortages', True, progress=rt.current_plan.progress[surgery_id].model_dump())
                    report['passed'] = True
                    return
                if args.case == 'failure':
                    assert rt.current_plan.progress[surgery_id].state != 'complete', 'Zero-success fixture unexpectedly succeeded'
                for pawn, owner in list(rt.draft_owners.items()):
                    if owner == rt.context_token and not any(p['thingId']==pawn and p.get('job')=='TendPatient' for p in people):
                        await rt.stand_down([pawn], expected_token=rt.context_token, expected_revision=rt.chat_revision,
                            expected_plan_revision=rt.current_plan.revision)
                patients = [p for p in people if p['thingId'] in setup['patients']]
                recovered = len(patients)==2 and all(p.get('dead') is False and
                    not any(h['defName']=='Flu' for h in p['health']['hediffs']) for p in patients)
                if recovered and rt.current_plan.progress[surgery_id].state=='complete': break
                assert rt.current_plan.progress[surgery_id].state != 'blocked', rt.current_plan.progress[surgery_id]
            assert args.case == 'care', 'Negative scenario did not reach its required outcome'
            check('native_surgical_health_change', rt.current_plan.progress[surgery_id].state=='complete',
                progress=rt.current_plan.progress[surgery_id].model_dump())
            check('both_diseases_recovered', recovered, patients=patients)
            check('recovery_bed_use_observed', all(any(p['thingId']==identity and p['health'].get('inBed')
                for sample in report['samples'] for p in sample['people']) for identity in setup['patients']))
            report['passed'] = True
        except BaseException:
            report['error'] = traceback.format_exc(); raise
        finally:
            save()
            await rt.router.close()
            try: await bridge.core('games_stop', gameId=bridge.game_id)
            finally: store.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--seconds', type=int, default=1800)
    parser.add_argument('--case', choices=('care', 'shortage', 'failure'), default='care')
    asyncio.run(run(parser.parse_args()))
