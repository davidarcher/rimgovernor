"""Disposable native melee/health-stop test against an existing wild animal.

No spawned threats, damage injection, boosted time, healing or model calls.
This tests execution and interruption, not autonomous combat tactics.
"""
import asyncio
import argparse
import json
from pathlib import Path
from rimbot.bridge import bridge_session, BridgeError
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_observation import observe
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.headless import prepare
from rimbot.store import Store


async def tend_wounded(rt, evidence):
    """Use native ground tending and require an observed treated wound."""
    pawns=(await rt.game.query('home/list_pawns',colonistsOnly=True,health=True))['pawns']
    patients=[p for p in pawns if p['health']['needsTend'] and not p['dead']]
    assert patients,'Combat produced no patient needing treatment'
    # Retreat to the starting group before treatment; do not hold a patient
    # motionless next to the animal that just bit them.
    patient=patients[0]
    home=pawns[0]['position']
    move=await rt.native('home/order',dict(action='goto',pawn=patient['thingId'],
        x=home['x'],z=home['z'],dryRun=False))
    destination=move['receipt']['job']['targetA']['position']
    await rt.control_clock('Superfast',mode='combat')
    deadline=asyncio.get_running_loop().time()+25;arrived=False
    while asyncio.get_running_loop().time()<deadline:
        await rt.supervisor.poll()
        state=(await rt.game.invoke('home/order',dict(action='resolve',pawn=patient['thingId'],dryRun=True)))['pawn']
        if state['position']==destination:
            arrived=True;break
        if not rt.supervisor.state.get('active'):break
        await asyncio.sleep(.2)
    evidence['retreat']=dict(arrived=arrived,pawn=state,clock=rt.supervisor.state)
    await rt.control_clock('Paused')
    assert arrived,'Patient could not retreat safely; do not start stationary treatment under attack'
    selected=None
    for patient in patients:
        # Hold the mobile patient still. This is an owned fixture draft, not damage injection.
        await rt.native('home/order',dict(action='draft',pawn=patient['thingId'],dryRun=False))
        for doctor in pawns:
            if doctor['thingId']==patient['thingId']:continue
            args=dict(action='tend',pawn=doctor['thingId'],target=patient['thingId'],
                dryRun=True)
            try:
                preview=await rt.game.invoke('home/order',args,allow_write=False)
            except BridgeError as error:
                evidence.setdefault('tend_refusals',[]).append(str(error))
                continue
            if preview.get('success'):
                selected=patient,args;break
        if selected:break
    assert selected,'No eligible native tend order'
    patient,args=selected
    evidence['patient_before']=patient
    evidence['tend_receipt']=(await rt.native('home/order',dict(args,dryRun=False)))['receipt']
    assert evidence['tend_receipt']['job']['verified']
    await rt.control_clock('Superfast',mode='combat')
    deadline=asyncio.get_running_loop().time()+45
    treated=None;events=[]
    while asyncio.get_running_loop().time()<deadline:
        events.extend(await rt.supervisor.poll())
        rows=(await rt.game.query('home/list_pawns',colonistsOnly=True,health=True))['pawns']
        current=next((p for p in rows if p['thingId']==patient['thingId']),None)
        if current and not current['dead'] and not current['health']['needsTend']:
            before=sum(bool(h['isTended']) for h in patient['health']['hediffs'])
            after=sum(bool(h['isTended']) for h in current['health']['hediffs'])
            if after>before:
                treated=current;break
        if not rt.supervisor.state.get('active'):break
        await asyncio.sleep(.25)
    evidence.update(patient_after=treated,medical_events=events,medical_clock=rt.supervisor.state)
    await rt.control_clock('Paused')
    assert treated,'No observed completed treatment; issuing a tend job is not treatment'
    cleanup=await rt.stand_down(list(rt.draft_owners),expected_token=rt.context_token,
        expected_revision=rt.chat_revision,expected_plan_revision=rt.current_plan.revision)
    evidence['medical_cleanup']=cleanup
    assert not cleanup['failed'] and not rt.draft_owners and rt.mode=='automate'
    print('PASS: native wound treated, no tending remains, owned medical drafts released',flush=True)


async def main(tend=False):
    root=Path('.rimbot/bridge').resolve();evidence={}
    async with bridge_session(root/'gabs/gabs-v1.1.1-windows-amd64/gabs.exe',prepare(root)) as bridge:
        await bridge.core('games_start',gameId=bridge.game_id);await bridge.connect()
        await bridge.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',readiness='visual',ignoreModCompatibility=True,timeoutMs=90000)
        await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
        store=Store(root/'combat-smoke.sqlite');rt=BridgeRuntime(store,root,headless=True)
        rt.bridge=bridge;rt.game=BridgeGame(bridge)
        await rt.sync_identity();rt.batch=await observe(rt.game);rt.mode='automate'
        try:
            animals=(await rt.game.query('home/list_pawns',animalsOnly=True,includeColonists=False,
                withinOfColonists=50,health=True))['pawns']
            candidates=[]
            for target in animals:
                if not target.get('wild') or target.get('downed') or target.get('dead'):continue
                for pawn in rt.batch.summary.pawns:
                    distance=max(abs(pawn.position.x-target['position']['x']),abs(pawn.position.z-target['position']['z']))
                    candidates.append((distance,pawn.thing_id,target))
            selected=None
            for _,pawn,target in sorted(candidates,key=lambda row:row[0]):
                args=dict(action='attack',pawn=pawn,target=target['thingId'],mode='melee',dryRun=True)
                preview=await rt.game.invoke('home/order',args,allow_write=False)
                if preview.get('success'):
                    selected=args,target;break
            assert selected,'No reachable wild-animal melee probe on this fixture'
            args,target=selected;evidence['target_before']=target
            print('Probe:',args['pawn'],'attacks',target['name'],target['position'],flush=True)
            issued=await rt.native('home/order',dict(args,dryRun=False))
            evidence['issued']=issued['receipt']
            assert issued['receipt'].get('job',{}).get('verified') is True,issued
            assert args['pawn'] in rt.draft_owners
            # Acknowledge this selected animal if it retaliates; keep health stops.
            await rt.control_clock('Superfast',mode='colony',ignored_hostiles=target['thingId'])
            deadline=asyncio.get_running_loop().time()+45
            events=[];observed=None
            while asyncio.get_running_loop().time()<deadline:
                events.extend(await rt.supervisor.poll())
                current=await rt.game.query('home/list_pawns',health=True,withinOfColonists=60)
                observed=next((p for p in current['pawns'] if p['thingId']==target['thingId']),None)
                state=rt.supervisor.state
                if (tend and state.get('stopReason')=='external_pause'
                        and 'Ancient danger' in state.get('stopDetail','')
                        and not evidence.get('acknowledged_ancient_warning')):
                    # Explicit disposable-test acknowledgment; no production auto-resume.
                    evidence['acknowledged_ancient_warning']=state['stopDetail']
                    rt.supervisor.allow_resume()
                    await rt.control_clock('Superfast',mode='colony',ignored_hostiles=target['thingId'])
                    continue
                if not rt.supervisor.state.get('active') or observed is None or observed.get('downed') or observed.get('dead'):
                    break
                await asyncio.sleep(.25)
            await rt.control_clock('Paused')
            evidence.update(events=events,target_after=observed,clock=rt.supervisor.state)
            outcome=('unobserved' if observed is None else 'dead' if observed.get('dead') else
                     'downed' if observed.get('downed') else 'active')
            evidence['outcome']=outcome
            evidence['health_stop']=any(e['kind'] in ('colonist_injury','colonist_downed','colonist_health') for e in events)
            evidence['target_health_changed']=observed is not None and observed.get('health')!=target.get('health')
            assert evidence['health_stop'] or evidence['target_health_changed'] or outcome in ('downed','dead'),evidence
            revision=rt.chat_revision
            rt.clock_events.extend(events);rt.receive_clock_events()
            if evidence['health_stop']:
                assert rt.wake.is_set() and rt.chat_revision>revision and rt.mode=='automate'
                assert any(e['kind'].startswith('native.colonist_') for e in rt.strategic_state.pending)
                assert not any('--allow-injured' in e['detail'] for e in events)
            evidence['planner_event_delivered']=rt.chat_revision>revision
            print('Observed:',outcome,'health stop:',evidence['health_stop'],'target health changed:',evidence['target_health_changed'],flush=True)
            result=await rt.stand_down([args['pawn']],expected_token=rt.context_token,
                expected_revision=rt.chat_revision,expected_plan_revision=rt.current_plan.revision)
            assert not result['failed'] and rt.mode=='automate',result
            evidence['cleanup']=result
            print('PASS: native attack produced combat evidence; selected draft released; game paused',flush=True)
            if tend:
                await tend_wounded(rt,evidence)
        finally:
            await rt.halt();await rt.router.close();store.close()
            (root/('medical-smoke.json' if tend else 'combat-smoke.json')).write_text(json.dumps(evidence,indent=2),encoding='utf8')


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tend',action='store_true',help='Continue into native wound treatment and medical draft cleanup')
    asyncio.run(main(parser.parse_args().tend))
