"""Disposable native melee/health-stop test against an existing wild animal.

No spawned threats, damage injection, boosted time, healing or model calls.
This tests execution and interruption, not autonomous combat tactics.
"""
import asyncio
import json
from pathlib import Path
from rimbot.bridge import bridge_session
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_observation import observe
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.headless import prepare
from rimbot.store import Store


async def main():
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
        finally:
            await rt.halt();await rt.router.close();store.close()
            (root/'combat-smoke.json').write_text(json.dumps(evidence,indent=2),encoding='utf8')


if __name__=='__main__':asyncio.run(main())
