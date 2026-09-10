"""Isolated B04f methods with explicit test-only setup and real pawn outcomes."""
import argparse
import asyncio
import json
import time
from pathlib import Path
from rimbot.bridge import BridgeError, bridge_session, gabs_executable
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_observation import observe
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.campaign_manifest import capture_manifest
from rimbot.colony_plan import ColonyGoal, CommitSteps, PlanStep
from rimbot.colony_policy import derive
from rimbot.development import placement
from rimbot.headless import prepare
from rimbot.store import Store


async def run(args):
    root = args.root.resolve()
    config = prepare(root)
    report = {'passed':False,'case':args.case,'setup':[],'checks':[],'snapshots':[]}
    report['manifest'] = capture_manifest(Path(__file__).resolve().parents[1],root,config,{'mode':'zero inference'})
    deadline = time.monotonic()+args.seconds
    def save():
        (root/'b04f-result.json').write_text(json.dumps(report,indent=2),encoding='utf8')
    def check(name, passed, **evidence):
        report['checks'].append(dict(name=name,passed=bool(passed),**evidence));save()
        print(name+': '+str(bool(passed)),flush=True)
        assert passed,name
    async with bridge_session(gabs_executable(root),config) as bridge:
        store = Store(root/'b04f.sqlite')
        rt = BridgeRuntime(store,root,headless=True)
        rt.bridge=bridge;rt.game=BridgeGame(bridge)
        async def refresh():
            await rt.sync_identity()
            await rt.refresh_clock_events()
            rt.batch=await observe(rt.game)
            await rt.projects.reconcile(rt.game)
            rt.reconcile_plan()
            rt.handled_revision=rt.chat_revision;rt.wake.clear()
            facts=derive(rt.batch,await rt.game.query('home/colony_facts',planning=True),rt.controller.policy)
            people=(await rt.game.query('home/list_pawns',colonistsOnly=True,bio=True,work=True,health=True,equipment=True))['pawns']
            report['latest']={'tick':facts['tick'],'threats':rt.batch.native.get('status_after',{}).get('threats'),
                'development':facts.get('development'), 'people':[
                    {k:p.get(k) for k in ('thingId','job','jobTarget','drafted','downed','mentalState','position','health')}
                    for p in people]}
            save()
            return facts,people
        async def setup(op,pawn=''):
            value=(await bridge.call('test/b04f_setup',op=op,pawn=pawn)).structuredContent
            report['setup'].append(value);save()
            assert value['success']
            return value
        async def window(combat=False, max_ticks=None):
            assert time.monotonic()<deadline,'Native acceptance wall-clock bound expired'
            if combat:
                rt.resume_after_review=True
                rt.current_plan.control['simulation_needed']=True
                await rt.advance_execution()
            else:
                from rimbot.production_policy import sync_production_policy
                await sync_production_policy(rt)
                await rt.supervisor.change('Superfast',max_ticks=max_ticks or (12000 if args.case=='development' else 6000))
            async with asyncio.timeout(180):
                while True:
                    events=await rt.supervisor.poll()
                    if not rt.supervisor.state.get('active'):break
                    await asyncio.sleep(.2)
            clock=dict(rt.supervisor.state)
            report['snapshots'].append({'clock':clock,'events':events})
            save()
            if clock.get('stopReason') not in ('tick_budget','tick_budget_reached','tick_deadline','hostiles_cleared'):
                # Record and explicitly acknowledge only the ordinary naming/ancient
                # warning fixture. Combat and patient injury guards are not suppressed.
                if 'Ancient danger' in clock.get('stopDetail',''):
                    rt.supervisor.allow_resume()
                elif clock.get('stopReason') not in (None,'requested_pause'):
                    facts,_=await refresh()
                    naming=facts.get('colonyNaming')
                    if naming:
                        await rt.native('home/confirm_colony_names',dict(naming,dryRun=False))
                        rt.supervisor.allow_resume()
                    elif combat and not (await refresh())[0].get('hostiles'):
                        pass
                    else:
                        raise AssertionError('Native guard stopped fixture: '+str(clock))
            return await refresh()
        async def issue(identity, selected=None):
            facts,people=await refresh()
            goal=rt.current_plan.colony_goals.setdefault(identity,ColonyGoal(priority_class=0 if identity=='ActiveCombat' else 2))
            goal.status='active'
            compiled=selected or await rt.controller.skills.compile(identity,facts,people)
            if compiled is None:return []
            method,actions=compiled
            if goal.method_seen(method):return []
            steps,costs=rt.controller.skills.steps(identity,method,actions,facts)
            await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                reason='B04f native method acceptance',steps=steps).decision(rt.current_plan),actor='strategist',
                expected_token=rt.context_token,expected_revision=rt.chat_revision)
            goal.steps.extend(s.id for s in steps)
            goal.evidence.setdefault('methods',{})[method]=[s.id for s in steps]
            rt.current_plan.control.setdefault('costs',{}).update(costs)
            if identity=='ActiveCombat':
                rt.current_plan.control['combat']={'target':goal.evidence['combat_target'],
                    'targets':goal.evidence['combat_targets'],'pawns':[a['arguments']['pawn'] for a in actions],
                    'steps':[s.id for s in steps]}
            for _ in range(32):
                rt.handled_revision=rt.chat_revision;rt.wake.clear()
                await rt.hands.advance(rt)
                if all(rt.current_plan.progress[s.id].state not in ('pending','executing') for s in steps):break
            for s in steps:
                assert rt.current_plan.progress[s.id].state in ('pending','waiting','complete'),rt.current_plan.progress[s.id]
            return [s.id for s in steps]
        async def finish_steps(ids, phase):
            equipment=bool(ids) and all(getattr(s.action,'completion',None)=='pawn_equipped'
                for s in rt.current_plan.spec.steps if s.id in ids)
            while any(rt.current_plan.progress[i].state!='complete' for i in ids):
                rt.handled_revision=rt.chat_revision;rt.wake.clear()
                await rt.hands.advance(rt)
                await window(max_ticks=120 if equipment else None)
                assert all(rt.current_plan.progress[i].state!='blocked' for i in ids), phase
            check(phase,True,steps={i:rt.current_plan.progress[i].model_dump() for i in ids})
        try:
            await bridge.core('games_start',gameId=bridge.game_id);await bridge.connect()
            await bridge.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',readiness='visual',ignoreModCompatibility=True,timeoutMs=90000)
            await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
            await rt.sync_identity();rt.mode='automate'
            await setup('stocks')
            facts,people=await refresh()
            check('native_order_history_available',all(type(p.get('orderGeneration')) is int for p in people))
            if args.case=='development':
                await setup('development-settings')
                await issue('EnsureWorkAssignments')
                await issue('AllowStartingSupplies')
                for _ in range(5):
                    facts,_=await refresh()
                    if facts['indoorSleepingCapacity']>=facts['colonists']:break
                    ids=await issue('EnsureInitialShelter')
                    await finish_steps(ids,'shelter_work')
                    await window()
                check('ordinary_roofed_shelter',facts['indoorSleepingCapacity']>=facts['colonists'],facts=facts)
                before=facts['indoorSleepingCapacity']
                for _ in range(5):
                    facts,_=await refresh()
                    if facts['indoorSleepingCapacity']>before:break
                    ids=await issue('EnsureExpansion')
                    await finish_steps(ids,'expansion_work');await window()
                facts,_=await refresh()
                check('ordinary_expansion',facts['indoorSleepingCapacity']>before,capacity=facts['indoorSleepingCapacity'])
                goal=rt.current_plan.colony_goals.setdefault('EnsureResearch',ColonyGoal(priority_class=4))
                for project in ('Electricity','ComplexFurniture'):
                    goal.target['project']=project
                    for _ in range(180):
                        facts,_=await refresh()
                        if project in facts['development']['research']['finished']:break
                        await issue('EnsureWorkAssignments')
                        ids=await issue('EnsureResearch')
                        if ids:await finish_steps(ids,'research_method')
                        await window()
                    facts,_=await refresh()
                    check('ordinary_research_completed',project in facts['development']['research']['finished'],research=facts['development']['research'])
                goal.status='complete'
                await issue('EnsureWorkAssignments')
                load=await placement(rt,facts,'Heater',indoors=True)
                await finish_steps(await issue('EnsureBasicPower',('fixture-load',[load])),'ordinary_electrical_load')
                facts,_=await refresh()
                target=facts['development']['power'][0]
                generator=await placement(rt,facts,'WoodFiredGenerator',indoors=False,near=dict(x=target['x']+15,z=target['z']),radius=4)
                await finish_steps(await issue('EnsureBasicPower',('fixture-generator',[generator])),'ordinary_generator')
                for _ in range(30):
                    facts,_=await refresh()
                    if facts['powerRequired'] and facts['powerHeadroom']>=0:break
                    ids=await issue('EnsureBasicPower')
                    if ids:await finish_steps(ids,'ordinary_connection')
                    await window()
                facts,_=await refresh()
                check('native_connected_power',facts['powerRequired'] and facts['powerHeadroom']>=0,power=facts['development']['power'])
                from rimbot.development import development_nodes
                for _ in range(20):
                    facts,_=await refresh()
                    if ('EnsureComfort',4) not in development_nodes(facts):break
                    ids=await issue('EnsureComfort')
                    await finish_steps(ids,'ordinary_comfort')
                facts,_=await refresh()
                check('native_comfort_complete',('EnsureComfort',4) not in development_nodes(facts),furniture=facts['development']['furniture'])
            elif args.case=='combat':
                patient=people[0]['thingId']
                await rt.native('home/order',dict(action='draft',pawn=patient,dryRun=False))
                equipment=await setup('combat-equipment')
                crew=sorted(people[1:],key=lambda p:(-max((s.get('level') or 0 for s in p['bio']['skills']
                    if s['name'] in ('Melee','Shooting')),default=0),p['thingId']))[:4]
                actions=[dict(kind='native_operation',tool='home/order',arguments=dict(action='equip',pawn=p['thingId'],target=w,watch=False),completion='pawn_equipped')
                    for p,w in zip(crew,equipment['weapons'])]
                await finish_steps(await issue('EnsureBasicDefense',('fixture-equipment',actions)),'native_squad_equipped')
                await setup('wound',patient)
                await setup('opponents')
                ids=await issue('ActiveCombat')
                await issue('CriticalMedical')
                targets=rt.current_plan.control['combat']['targets']
                check('multiple_opponents_dispatched',len(targets)==2,targets=targets)
                for _ in range(40):
                    facts,_=await refresh()
                    if facts['hostiles']==0:break
                    await window(combat=True)
                facts,_=await refresh()
                check('native_squad_victory',facts['hostiles']==0,threats=rt.batch.native.get('status_after'))
                result=await rt.stand_down(rt.current_plan.control['combat']['pawns'],expected_token=rt.context_token,expected_revision=rt.chat_revision,expected_plan_revision=rt.current_plan.revision)
                check('squad_draft_cleanup',not result['failed'],result=result)
                for _ in range(12):
                    _,people=await refresh()
                    if not next(p for p in people if p['thingId']==patient)['health']['needsTend']:break
                    await window()
                check('combat_triage_completed',not next(p for p in people if p['thingId']==patient)['health']['needsTend'])
            elif args.case=='medical':
                patients=[p['thingId'] for p in people[:2]]
                for patient in patients:
                    await rt.native('home/order',dict(action='draft',pawn=patient,dryRun=False))
                    await setup('wound',patient)
                ids=await issue('CriticalMedical')
                check('triage_order_issued',len(ids)==1)
                original=rt.current_plan.spec.steps[-1]
                doctor=original.action.arguments['pawn']
                await setup('unavailable-doctor',doctor)
                await refresh()
                await rt.recover_medical(original.id,expected_token=rt.context_token,expected_revision=rt.chat_revision,limit=3)
                check('unavailable_doctor_replaced',rt.current_plan.progress[original.id].state=='cancelled',goal=rt.current_plan.colony_goals['CriticalMedical'].model_dump())
                rt.handled_revision=rt.chat_revision;rt.wake.clear();await rt.hands.advance(rt)
                for _ in range(20):
                    facts,people=await refresh()
                    if all(not p['health']['needsTend'] for p in people if p['thingId'] in patients):break
                    await window()
                    await issue('CriticalMedical')
                facts,people=await refresh()
                check('competing_patients_treated',all(not p['health']['needsTend'] for p in people if p['thingId'] in patients),people=people)
                patient=next(p['thingId'] for p in people if not p.get('mentalState') and not p['downed'])
                await setup('wound',patient)
                goal=rt.current_plan.colony_goals['CriticalMedical'];goal.reopen_methods();goal.attempts+=1
                ids=await issue('CriticalMedical');step=next(s for s in rt.current_plan.spec.steps if s.id==ids[0])
                await setup('external-order',step.action.arguments['pawn'])
                await refresh();before=rt.counters['actions']
                await rt.recover_medical(step.id,expected_token=rt.context_token,expected_revision=rt.chat_revision,limit=3)
                check('external_player_order_preserved',rt.counters['actions']==before and rt.current_plan.progress[step.id].state=='blocked')
            else:
                await setup('opponents')
                ids=await issue('ActiveCombat')
                facts,people=await refresh()
                extra=next(p for p in people if p['thingId'] not in rt.current_plan.control['combat']['pawns'])
                original=next(s for s in rt.current_plan.spec.steps if s.id==ids[0])
                action=original.action.model_copy(deep=True);action.arguments['pawn']=extra['thingId']
                preview=await rt.inspect_native('home/order',dict(action.arguments,dryRun=True,requireCombatHealth=True))
                check('healthy_native_preview',preview.get('success') is True)
                reinforcement=PlanStep.model_validate(dict(original.model_dump(),id='health-dispatch-probe',action=action.model_dump(),after=[]))
                await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                    reason='Prepare an ordinary reinforcement before health changes',steps=[reinforcement]).decision(rt.current_plan),
                    actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
                await setup('low-health',people[0]['thingId'])
                facts,people=await refresh()
                from rimbot.combat_health import combat_health_hold
                check('native_health_hold',bool(combat_health_hold({'pawns':people})),people=people)
                from rimbot.colony_skills import SkillBlocked
                refused=False
                try:await rt.controller.skills.compile('ActiveCombat',facts,people)
                except SkillBlocked:refused=True
                check('native_health_compilation_hold',refused)
                try:
                    raced=(await bridge.call('home/order',**dict(action.arguments,dryRun=False,requireCombatHealth=True))).structuredContent
                except BridgeError as error:
                    raced=error.result.structuredContent
                check('native_dispatch_rechecks_after_health_read',raced.get('success') is False and not raced.get('applied'),result=raced)
                before=rt.counters['actions']
                await rt.hands.advance(rt)
                failure=rt.current_plan.progress[reinforcement.id].failure
                check('native_health_dispatch_hold',rt.counters['actions']==before and failure and failure.code=='combat_health_hold')
                start=rt.supervisor.epoch
                for _ in range(3):
                    await refresh()
                    rt.resume_after_review=True;rt.current_plan.control['simulation_needed']=True
                    await rt.advance_execution()
                check('unchanged_health_never_rearms',rt.supervisor.epoch==start and bool(rt.current_plan.control.get('execution_hold')))
                tick=facts['tick']
                for _ in range(3):
                    state=await rt.supervisor.change('Normal',mode='combat',ignored_hostiles=','.join(rt.current_plan.control['combat']['targets']),max_ticks=600)
                    check('native_clock_admission_rechecks_health',not state.get('active'),clock=state)
                check('native_clock_admission_no_ticks',(await refresh())[0]['tick']==tick)
            report['passed']=True
        except BaseException as error:
            report['error']=repr(error)
            raise
        finally:
            await rt.halt();await rt.router.close();store.close()
            report['cleanup']=(await bridge.core('games_stop',gameId=bridge.game_id)).model_dump(mode='json')
            save()


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root',type=Path,required=True)
    parser.add_argument('--case',choices=['development','combat','medical','health'],required=True)
    parser.add_argument('--seconds',type=int,default=1800)
    asyncio.run(run(parser.parse_args()))
