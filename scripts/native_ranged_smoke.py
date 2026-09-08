"""Disposable headless equip/readback/ranged-hit acceptance test, no model calls."""
import asyncio
import json
from pathlib import Path
from rimbot.bridge import bridge_session, BridgeError
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
        store=Store(root/'ranged-smoke.sqlite');rt=BridgeRuntime(store,root,headless=True)
        rt.bridge=bridge;rt.game=BridgeGame(bridge)
        await rt.sync_identity();rt.batch=await observe(rt.game);rt.mode='automate'
        try:
            try:
                await rt.game.query('home/list_things',category='invalid-test-category')
                raise AssertionError('Unknown item category was silently accepted')
            except BridgeError as error:
                assert 'Unknown category' in str(error)
            first=rt.batch.summary.pawns[0].position
            weapons=await rt.game.query('home/list_things',category='weapons',includeHeld=False,
                x=first.x,z=first.z,radius=25,maxPositionsPerDef=30)
            assert weapons['things'] and all(r.get('weapon') for r in weapons['things']),weapons
            selected=None
            for row in weapons['things']:
                if not row['weapon']['ranged']:continue
                for item in row['positions']:
                    for pawn in rt.batch.summary.pawns:
                        args=dict(action='equip',pawn=pawn.thing_id,target=item['thingId'],dryRun=True)
                        try:
                            preview=await rt.game.invoke('home/order',args,allow_write=False)
                        except BridgeError as error:
                            evidence.setdefault('rejected_equips',[]).append(dict(pawn=pawn.thing_id,reason=str(error)))
                            continue
                        if preview.get('success'):
                            selected=args,row,item;break
                    if selected:break
                if selected:break
            assert selected,'No eligible colonist and nearby ranged weapon'
            equip,row,item=selected;evidence['weapon']=row
            issued=await rt.native('home/order',dict(equip,dryRun=False));evidence['equip_receipt']=issued['receipt']
            assert issued['receipt']['job']['verified'],issued
            await rt.control_clock('Superfast')
            deadline=asyncio.get_running_loop().time()+20;equipped=None
            while asyncio.get_running_loop().time()<deadline:
                await rt.supervisor.poll()
                state=(await rt.game.invoke('home/order',dict(action='resolve',pawn=equip['pawn'],dryRun=True)))['pawn']
                if state.get('weapon',{} ) and state['weapon'].get('thingId')==item['thingId']:
                    equipped=state;break
                if not rt.supervisor.state.get('active'):break
                await asyncio.sleep(.2)
            await rt.control_clock('Paused')
            assert equipped and equipped['weapon']['ranged'],'Equip job did not become actual equipped weapon'
            evidence['equipped']=equipped
            print('Equipped:',equip['pawn'],equipped['weapon']['label'],flush=True)
            animals=(await rt.game.query('home/list_pawns',animalsOnly=True,includeColonists=False,
                withinOfColonists=50,health=True))['pawns']
            candidates=sorted((p for p in animals if p.get('wild') and not p.get('downed')),
                key=lambda p:max(abs(p['position']['x']-equipped['position']['x']),abs(p['position']['z']-equipped['position']['z'])))
            target=None;destination=None
            # Approach the nearest existing animal using a normal drafted move.
            # Offset is test positioning, not a production combat tactic.
            for candidate in candidates:
                destination=None
                for dx,dz in ((2,0),(-2,0),(0,2),(0,-2)):
                    move=dict(action='goto',pawn=equip['pawn'],x=candidate['position']['x']+dx,
                        z=candidate['position']['z']+dz,dryRun=True)
                    try:
                        if (await rt.game.invoke('home/order',move,allow_write=False)).get('success'):
                            destination=move;break
                    except BridgeError:
                        continue
                if destination:break
            assert destination,'No reachable firing position'
            evidence['move']=await rt.native('home/order',dict(destination,dryRun=False))
            actual_destination=evidence['move']['receipt']['job']['targetA']['position']
            await rt.control_clock('Superfast',ignored_hostiles=candidate['thingId'])
            deadline=asyncio.get_running_loop().time()+25
            arrived=False
            while asyncio.get_running_loop().time()<deadline:
                await rt.supervisor.poll()
                position=(await rt.game.invoke('home/order',dict(action='resolve',pawn=equip['pawn'],dryRun=True)))['pawn']['position']
                if position==actual_destination:
                    arrived=True;break
                if not rt.supervisor.state.get('active'):
                    state=rt.supervisor.state
                    # This disposable fixture crosses the proximity warning for
                    # a sealed ancient ruin. Explicit test-driver acknowledgment;
                    # production still requires the player to release this hold.
                    if (state.get('stopReason')=='external_pause'
                            and 'Ancient danger' in state.get('stopDetail','')
                            and not evidence.get('acknowledged_ancient_warning')):
                        evidence['acknowledged_ancient_warning']=state['stopDetail']
                        rt.supervisor.allow_resume()
                        await rt.control_clock('Superfast',ignored_hostiles=candidate['thingId'])
                    else:break
                await asyncio.sleep(.2)
            evidence['movement_result']=dict(position=position,destination=destination,clock=rt.supervisor.state)
            await rt.control_clock('Paused')
            assert arrived,('Pawn did not reach firing position',evidence['movement_result'])
            candidates=(await rt.game.query('home/list_pawns',animalsOnly=True,includeColonists=False,
                withinOfColonists=50,health=True))['pawns']
            for candidate in candidates:
                if not candidate.get('wild') or candidate.get('downed') or candidate.get('dead'):continue
                attack=dict(action='attack',pawn=equip['pawn'],target=candidate['thingId'],mode='ranged',dryRun=True)
                try:
                    preview=await rt.game.invoke('home/order',attack,allow_write=False)
                except BridgeError as error:
                    evidence.setdefault('rejected_targets',[]).append(dict(target=candidate['thingId'],reason=str(error)))
                    continue
                if preview.get('success'):
                    target=candidate;break
            assert target,'No legal ranged target in fixture'
            evidence['target_before']=target
            issued=await rt.native('home/order',dict(attack,dryRun=False));evidence['attack_receipt']=issued['receipt']
            assert issued['receipt']['job']['verified'] and issued['receipt']['job']['mode']=='ranged'
            await rt.control_clock('Superfast',ignored_hostiles=target['thingId'])
            deadline=asyncio.get_running_loop().time()+45;hit=None;events=[]
            while asyncio.get_running_loop().time()<deadline:
                events.extend(await rt.supervisor.poll())
                rows=(await rt.game.query('home/list_pawns',animalsOnly=True,includeColonists=False,withinOfColonists=60,health=True))['pawns']
                current=next((p for p in rows if p['thingId']==target['thingId']),None)
                if current and current['health']['hediffCount']>target['health']['hediffCount']:
                    hit=current;break
                if current is None or not rt.supervisor.state.get('active'):break
                await asyncio.sleep(.25)
            evidence['final_clock']=rt.supervisor.state
            evidence['last_target']=current
            evidence['final_pawn']=await rt.game.query('home/list_pawns',colonistsOnly=True,health=True,equipment=True)
            await rt.control_clock('Paused');evidence.update(target_after=hit,events=events)
            assert hit,'No observed ranged injury; a missing target or an issued attack is not a verified hit'
            result=await rt.stand_down([equip['pawn']],expected_token=rt.context_token,
                expected_revision=rt.chat_revision,expected_plan_revision=rt.current_plan.revision)
            assert not result['failed'];evidence['cleanup']=result
            print('PASS: weapon equipped, ranged attack verified, target injury observed, draft released',flush=True)
        finally:
            await rt.halt();await rt.router.close();store.close()
            (root/'ranged-smoke.json').write_text(json.dumps(evidence,indent=2),encoding='utf8')


if __name__=='__main__':asyncio.run(main())
