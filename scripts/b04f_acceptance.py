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
            people=(await rt.game.query('home/list_pawns',colonistsOnly=True,bio=True,work=True,health=True,equipment=True,needs=True))['pawns']
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
                elif args.case=='development' and 'has started to roam away!' in clock.get('stopDetail',''):
                    check('fixture_roaming_notice_acknowledged',True,clock=clock)
                    rt.supervisor.allow_resume()
                elif args.case=='development' and clock.get('stopReason')=='notification_batch' and 'Quest available:' in clock.get('stopDetail',''):
                    check('fixture_optional_quest_notice_acknowledged',True,clock=clock)
                    rt.supervisor.allow_resume()
                elif clock.get('stopReason') not in (None,'requested_pause'):
                    facts,_=await refresh()
                    naming=facts.get('colonyNaming')
                    if naming:
                        await rt.native('home/confirm_colony_names',dict(naming,dryRun=False))
                        rt.supervisor.allow_resume()
                    elif combat and not (await refresh())[0].get('hostiles'):
                        pass
                    elif clock.get('stopReason')=='force_paused':
                        report['native_checkpoint']=(await bridge.call('rimworld/save_game',saveName='B04f-continuation')).structuredContent
                        targets=await rt.game.invoke('rimworld/get_screen_targets',{})
                        report['paused_ui']=targets;save()
                        research=rt.current_plan.colony_goals.get('EnsureResearch')
                        completed=research and research.target.get('project') in facts.get('development',{}).get('research',{}).get('finished',[])
                        windows=targets.get('targets',{}).get('windows',[])
                        assert completed and len(windows)==1 and windows[0].get('type')=='Verse.Dialog_NodeTree' and windows[0].get('dismissTargetId'),targets
                        # The fixture explicitly acknowledges its completed research;
                        # ordinary automation must retain the external modal hold.
                        await rt.set_mode('automate')
                        await rt.native('rimworld/click_screen_target',{'targetId':windows[0]['dismissTargetId']},reconcile=False)
                        check('native_research_dialog_closed',not (await rt.game.invoke('rimworld/get_ui_state',{})).get('windows'),ui=targets)
                        rt.supervisor.allow_resume()
                    else:
                        raise AssertionError('Native guard stopped fixture: '+str(clock))
            return await refresh()
        async def issue(identity, selected=None):
            facts,people=await refresh()
            goal=rt.current_plan.colony_goals.setdefault(identity,ColonyGoal(priority_class=0 if identity=='ActiveCombat' else 2))
            goal.status='active'
            if identity=='EnsureResearch' and selected is None:
                from rimbot.research import refresh as prepare_research
                await prepare_research(rt,facts,people,[])
                await issue('EnsureWorkAssignments')
                facts,people=await refresh()
                await prepare_research(rt,facts,people,[])
            compiled=selected or await rt.controller.skills.compile(identity,facts,people)
            if compiled is None:return []
            method,actions=compiled
            if goal.method_seen(method):return []
            steps,costs=rt.controller.skills.steps(identity,method,actions,facts)
            if not steps:return []
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
            if not ids:return
            equipment=bool(ids) and all(getattr(s.action,'completion',None) in ('pawn_equipped','pawn_at_position')
                for s in rt.current_plan.spec.steps if s.id in ids)
            while any(rt.current_plan.progress[i].state!='complete' for i in ids):
                rt.handled_revision=rt.chat_revision;rt.wake.clear()
                await rt.hands.advance(rt)
                await window(max_ticks=120 if equipment else None)
                assert all(rt.current_plan.progress[i].state!='blocked' for i in ids), phase
            check(phase,True,steps={i:rt.current_plan.progress[i].model_dump() for i in ids})
            if args.case=='development':
                report['native_checkpoint']=(await bridge.call('rimworld/save_game',saveName='B04f-continuation')).structuredContent
                save()
        try:
            await bridge.core('games_start',gameId=bridge.game_id);await bridge.connect()
            if args.new_crashlanded:
                report['native_start']=(await bridge.call('rimworld/start_debug_game_ready',readiness='visual',pauseIfNeeded=True,timeoutMs=120000)).structuredContent
                census={}
                for _ in range(30):
                    try:
                        census=(await bridge.call('home/colony_facts',planning=False)).structuredContent
                        if census.get('colonists')==3:break
                    except BridgeError as error:
                        if 'No living colonists' not in str(error):raise
                    await bridge.call('rimworld/set_time_speed',speed='Normal',ultraSpeedBoost=False)
                    await asyncio.sleep(.25)
                    await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
                check('ordinary_crashlanded_start',census.get('colonists')==3)
            else:
                await bridge.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',readiness='visual',ignoreModCompatibility=True,timeoutMs=90000)
            await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
            await rt.sync_identity();rt.mode='automate'
            if args.case != 'recurring-harvest': await setup('stocks')
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
                before=facts['colonists']
                for _ in range(5):
                    facts,_=await refresh()
                    if facts['indoorSleepingCapacity']>before:break
                    ids=await issue('EnsureExpansion')
                    await finish_steps(ids,'expansion_work');await window()
                facts,_=await refresh()
                check('ordinary_expansion',facts['indoorSleepingCapacity']>before,capacity=facts['indoorSleepingCapacity'])
                goal=rt.current_plan.colony_goals.setdefault('EnsureResearch',ColonyGoal(priority_class=4))
                projects=['Electricity','ComplexFurniture']
                if args.research_project:projects.append(args.research_project)
                if args.new_crashlanded:
                    facts,_=await refresh()
                    projects.append(min(facts['development']['research']['available'],key=lambda p:(p['cost'],p['defName']))['defName'])
                for project in projects:
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
                if not any(p['defName']=='StandingLamp' for p in facts['development']['power']):
                    load=await placement(rt,facts,'StandingLamp',indoors=True)
                    await finish_steps(await issue('EnsureBasicPower',('fixture-load',[load])),'ordinary_electrical_load')
                facts,_=await refresh()
                target=next(p for p in facts['development']['power'] if p['defName']=='StandingLamp')
                producer=next((p for p in facts['development']['power'] if p['defName']=='WoodFiredGenerator'),None)
                if producer:
                    site=dict(x=producer['x'],z=producer['z'])
                else:
                    generator=await placement(rt,facts,'WoodFiredGenerator',indoors=False,near=dict(x=target['x']+15,z=target['z']),radius=4)
                    site=generator['placements'][0]
                    await finish_steps(await issue('EnsureBasicPower',('fixture-generator',[generator])),'ordinary_generator')
                for _ in range(30):
                    facts,_=await refresh()
                    if facts['powerRequired'] and facts['powerHeadroom']>=0:break
                    ids=await issue('EnsureBasicPower')
                    if ids:await finish_steps(ids,'ordinary_connection')
                    await window()
                facts,_=await refresh()
                power=facts['development']['power']
                load_row=next(p for p in power if p['id']==target['id'])
                generator_row=next(p for p in power if p['defName']=='WoodFiredGenerator'
                    and p['x']==site['x'] and p['z']==site['z'])
                conduits=[b for b in facts['development']['furniture'] if b['defName']=='PowerConduit']
                check('native_connected_power',facts['powerRequired'] and facts['powerHeadroom']>=0
                    and load_row['powered'] and load_row['net']==generator_row['net']
                    and generator_row['outputW']>0 and bool(conduits),power=power,conduits=conduits)
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
                facts,_=await refresh()
                shelter=next(c for c in facts['cells'] if c.get('walkable') is True and not c.get('occupied')
                    and 22<=abs(c['x']-facts['center']['x'])<=24 and abs(c['z']-facts['center']['z'])<=2)
                retreat=dict(kind='native_operation',tool='home/order',arguments=dict(action='goto',pawn=patient,
                    x=shelter['x'],z=shelter['z'],watch=False),completion='pawn_at_position')
                await finish_steps(await issue('EnsureBasicDefense',('fixture-patient-position',[retreat])),'native_patient_positioned')
                await setup('wound',patient)
                await setup('opponents',crew[0]['thingId'])
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
            elif args.case=='recurring-harvest':
                from rimbot.native_scenario import advance_game
                await issue('EnsureWorkAssignments')
                target=(await setup('harvest-plant'))['plant']
                await bridge.call('test/food_observe')
                rt.supervisor.test_acceleration=True
                rt.current_plan.colony_goals['EnsureFoodSupply']=ColonyGoal(priority_class=2,source='PLAYER',target={'food_days':7})
                completed=[]
                for harvest in range(2):
                    if harvest: await setup('harvest-regrowth',target)
                    facts,people=await refresh()
                    plant=next(p for p in facts['acquisition'] if p['id']==target)
                    compiled=await rt.controller.skills.compile('EnsureFoodSupply',dict(facts,acquisition=[plant]),people)
                    check('exact_plant_method_'+str(harvest),compiled and compiled[1][0]['tool']=='home/acquire_resource')
                    ids=await issue('EnsureFoodSupply',compiled)
                    check('native_designation_'+str(harvest),bool(ids) and all(rt.current_plan.progress[i].state=='complete' for i in ids))
                    receipts={i:rt.current_plan.progress[i].model_dump() for i in ids}
                    products=[]
                    for _ in range(40):
                        await advance_game(rt,600,report)
                        facts,_=await refresh()
                        observed=(await bridge.call('test/food_observe')).structuredContent
                        products=[p for p in observed['production'] if p.get('x')==plant['x']
                            and p.get('z')==plant['z'] and p.get('plant')=='Plant_Berry' and p.get('count',0)>0]
                        if len(products)>harvest:break
                    check('ordinary_harvest_finished_'+str(harvest),len(products)>harvest,plant=target,products=products)
                    completed.append(receipts)
                first=next(iter(completed[0]))
                archived=store.retired_action(rt.colony,first)
                check('prior_harvest_receipt_preserved',archived and archived['progress']==completed[0][first],archive=archived)
                report['harvest_receipts']=completed
            elif args.case=='medical-rest':
                from rimbot.native_scenario import advance_game,ScenarioInterrupted
                from rimbot.colony_policy import priority_nodes
                await issue('EnsureWorkAssignments')
                patient=people[0]['thingId']
                await setup('resting-patient',patient)
                facts,people=await refresh()
                subject=next(p for p in people if p['thingId']==patient)
                check('native_stable_rest_observed',subject['downed'] and subject['health'].get('stableRestEligible') is True,
                    patient=subject)
                check('rest_shares_survival_priority',('CriticalMedical',2) in priority_nodes(facts,{},rt.controller.policy))
                rt.current_plan.control['facts']=facts
                rt.current_plan.colony_goals['MaintainMedicalCare']=ColonyGoal(priority_class=2)
                untracked=await rt.supervisor.change('Superfast',max_ticks=120)
                check('ordinary_downed_guard_remains_active',not untracked['active'] and untracked['stopReason']=='colonist_downed',clock=untracked)
                rt.supervisor.test_acceleration=True
                initial_food=subject['needs']['food']
                for _ in range(30):
                    await advance_game(rt,600,report,medical_rest=True)
                    facts,people=await refresh()
                    rt.current_plan.control['facts']=facts
                    subject=next(p for p in people if p['thingId']==patient)
                    if subject['needs']['food']>initial_food+.3:break
                check('ordinary_caregiver_fed_resting_patient',subject['needs']['food']>initial_food+.3,
                    before=initial_food,patient=subject)
                check('feeding_does_not_claim_patient_recovered',subject['downed'] is True and patient in facts['criticalPatients'])
                await setup('resting-injury',patient)
                # Retain the preceding certificate to exercise the native recheck.
                try:
                    await advance_game(rt,120,report,medical_rest=True,expected_letters=())
                except ScenarioInterrupted:
                    stopped=report['simulation'][-1]['interruptions'][-1]['clock']
                    check('new_injury_invalidates_rest_certificate',stopped['stopReason'] in ('colonist_downed','colonist_injury','medical_rest_changed'),clock=stopped)
                else:
                    raise AssertionError('New tending/bleeding need did not stop medical-rest monitoring')
            elif args.case=='equipment-observation':
                from rimbot.native_scenario import advance_game
                from rimbot.colony_plan import Failure
                equipment=await setup('combat-equipment')
                facts,people=await refresh()
                pawn=next(p for p in people if not p.get('drafted') and not p.get('downed') and not p.get('dead'))
                action=dict(kind='native_operation',tool='home/order',completion='pawn_equipped',
                    arguments=dict(action='equip',pawn=pawn['thingId'],target=equipment['weapons'][0],watch=False))
                ids=await issue('EnsureBasicDefense',('fixture-observed-equipment',[action]))
                check('equipment_order_waits_for_native_work',bool(ids) and all(
                    rt.current_plan.progress[i].state=='waiting' for i in ids),
                    progress={i:rt.current_plan.progress[i].model_dump() for i in ids})
                # Model a lost transport acknowledgement after native dispatch.
                # The game continues the original job; recovery may only observe it.
                for identity in ids:
                    progress=rt.current_plan.progress[identity]
                    progress.issued['0']['confirmed']=False
                    progress.state='blocked'
                    progress.failure=Failure(code='native_failure',detail='Fixture withheld equipment acknowledgement')
                retained={i:rt.current_plan.progress[i].model_dump() for i in ids}
                report['withheld_receipts']=retained
                rt.supervisor.test_acceleration=True
                for _ in range(30):
                    await refresh()
                    if all(rt.current_plan.progress[i].state=='complete' for i in ids):break
                    await advance_game(rt,120,report)
                await refresh()
                check('equipment_observed_without_reissuing',all(rt.current_plan.progress[i].state=='complete' for i in ids),
                    progress={i:rt.current_plan.progress[i].model_dump() for i in ids})
                check('original_uncertain_receipts_preserved',all(
                    rt.current_plan.progress[i].issued==retained[i]['issued'] for i in ids))
            elif args.case=='refused-preview':
                from rimbot.native_scenario import advance_game
                from rimbot.order_refusal import refused_preview
                await setup('combat-equipment')
                ids = await issue('EnsureBasicDefense')
                rt.supervisor.test_acceleration = True
                for _ in range(30):
                    facts, people = await refresh()
                    armed = [p for p in people if (p.get('equipment') or {}).get('primary')]
                    if armed: break
                    await advance_game(rt, 600, report)
                check('ordinary_weapon_equipped', bool(armed))
                pawn = armed[0]
                weapon = pawn['equipment']['primary']['thingId']
                original_compile = rt.controller.skills.compile
                attempted = []
                async def refused_method(identity, facts, people):
                    if not attempted:
                        attempted.append(identity)
                        try:
                            await rt.inspect_native('home/order', dict(action='equip',
                                pawn=pawn['thingId'], target=weapon, dryRun=True, watch=False))
                        except BridgeError as error:
                            check('held_weapon_preview_refused_before_write', refused_preview(error) is not None,
                                  native=error.result.structuredContent)
                            raise
                        raise AssertionError('Held weapon unexpectedly passed pickup preview')
                    return await original_compile(identity, facts, people)
                rt.controller.skills.compile = refused_method
                try:
                    await rt.controller.cycle()
                    check('refusal_keeps_automation_alive', rt.mode == 'automate' and bool(attempted))
                    blocked = rt.current_plan.colony_goals[attempted[0]]
                    check('refusal_retained_on_goal', bool(blocked.evidence.get('order_preview_refusal')))
                    await rt.controller.cycle()
                    check('subsequent_review_completes', rt.mode == 'automate')
                finally:
                    rt.controller.skills.compile = original_compile
                from rimbot.mood_control import assess, method as need_method
                from rimbot.colony_skills import SkillBlocked
                # Stale controller input exercises native admission without changing needs/jobs.
                stale = dict(pawn, drafted=False, downed=False, jobPlayerForced=False,
                    jobLoadId=-2, schedule={'current':'Anything'}, mentalState=None,
                    needs={'mood':.1, 'breakThresholdMinor':.35, 'rest':.1, 'food':.8, 'joy':.8})
                identity = 'EnsureMood-'+pawn['thingId']
                rt.current_plan.colony_goals[identity] = ColonyGoal(priority_class=2)
                try:
                    await need_method(rt, identity, {'mood':assess([stale], [], {})}, [stale])
                except SkillBlocked:
                    evidence = rt.current_plan.colony_goals[identity].evidence.get('need_preview_refusals', {})
                    check('stale_need_preview_refused_without_global_stop', bool(evidence), native=evidence)
                else:
                    raise AssertionError('Stale need identity unexpectedly passed admission')
                await rt.controller.cycle()
                check('review_after_need_refusal_completes', rt.mode == 'automate')
            elif args.case=='drafted-medical':
                from rimbot.native_scenario import advance_game
                # One player-owned draft stays protected throughout recovery.
                protected = people[-1]['thingId']
                await bridge.call('home/order', action='draft', pawn=protected, dryRun=False, watch=False)
                rt.current_plan.control.setdefault('player_draft_overrides', {})[protected] = True
                owned = [p['thingId'] for p in people[:-1]]
                for pawn in owned:
                    await rt.native('home/order', dict(action='draft', pawn=pawn, dryRun=False, watch=False))
                patient = owned[0]
                await setup('wound', patient)
                facts, people = await refresh()
                check('all_doctors_initially_drafted', all(p['drafted'] for p in people))
                check('combat_cleared_before_release', facts['hostiles'] == 0)
                ids = await issue('CriticalMedical')
                check('medical_uses_owned_stand_down', len(ids) == 1 and
                    next(s for s in rt.current_plan.spec.steps if s.id == ids[0]).action.kind == 'stand_down')
                facts, people = await refresh()
                check('player_draft_preserved', next(p for p in people if p['thingId'] == protected)['drafted'])
                check('owned_workers_released', all(not p['drafted'] for p in people if p['thingId'] in owned))
                ids = await issue('CriticalMedical')
                check('treatment_dispatched_after_fresh_read', bool(ids) and all(
                    next(s for s in rt.current_plan.spec.steps if s.id == i).action.completion == 'patient_tended' for i in ids))
                rt.supervisor.test_acceleration = True
                for _ in range(30):
                    facts, people = await refresh()
                    if not next(p for p in people if p['thingId'] == patient)['health']['needsTend']:
                        break
                    await advance_game(rt, 600, report)
                facts, people = await refresh()
                check('native_treatment_completed', not next(p for p in people if p['thingId'] == patient)['health']['needsTend'])
                check('player_still_drafted_after_treatment', next(p for p in people if p['thingId'] == protected)['drafted'])
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
            report['native_checkpoint']=(await bridge.call('rimworld/save_game',saveName='B04f-continuation')).structuredContent
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
    parser.add_argument('--case',choices=['development','combat','medical','drafted-medical','refused-preview','equipment-observation','medical-rest','recurring-harvest','health'],required=True)
    parser.add_argument('--seconds',type=int,default=1800)
    parser.add_argument('--new-crashlanded',action='store_true',help='Use ordinary native Crashlanded generation and starting technology, without save edits')
    parser.add_argument('--research-project',help='Require this native project on a saved-colony continuation')
    asyncio.run(run(parser.parse_args()))
