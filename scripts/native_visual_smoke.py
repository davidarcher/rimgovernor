"""Rendered, blind layout review against an intentionally doorless blueprint shell.

The fixture places ordinary blueprints, never instant buildings. No model orders.
Camera framing belongs to this disposable test; the reviewer must leave it alone.
"""
from rimbot.bridge import gabs_executable
import asyncio
import json
import time
import re
from pathlib import Path
from rimbot.bridge import bridge_session, BridgeError
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.bridge_observation import observe
from rimbot.config import Settings, ModelRole, ModelRouting
from rimbot.store import Store


async def main():
    root=Path('.rimbot/bridge').resolve()
    settings=Settings(model='qwen3.5-9b',reasoning=False,max_output_tokens=2048,timeout_seconds=180)
    routing=ModelRouting(roles={ModelRole.STRATEGIST:settings,ModelRole.ARCHITECT:settings})
    evidence={}
    async with bridge_session(gabs_executable(root),root/'config') as bridge:
        await bridge.core('games_start',gameId=bridge.game_id);await bridge.connect()
        await bridge.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',readiness='visual',
            ignoreModCompatibility=True,timeoutMs=90000)
        await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
        store=Store(root/'visual-smoke.sqlite');rt=BridgeRuntime(store,root,routing=routing)
        rt.bridge=bridge;rt.game=BridgeGame(bridge)
        try:
            await rt.sync_identity();rt.batch=await observe(rt.game)
            p=rt.batch.summary.pawns[0].position
            selected=None
            for dx,dz in ((10,0),(-12,0),(0,10),(0,-12),(15,10)):
                cells=[(p.x+dx+x,p.z+dz+z) for z in range(7) for x in range(7) if x in (0,6) or z in (0,6)]
                try:
                    for x,z in cells:
                        result=await rt.game.invoke('home/place_building',dict(defName='Wall',stuff='WoodLog',rotation='north',x=x,z=z,dryRun=True),allow_write=False)
                        if not result.get('rotations') or not all(r.get('accepted') is True for r in result['rotations']):
                            raise ValueError('Refused fixture cell')
                except (BridgeError,ValueError):continue
                selected=cells;break
            assert selected,'No legal fixture footprint'
            for x,z in selected:
                result=await rt.game.invoke('home/place_building',dict(defName='Wall',stuff='WoodLog',rotation='north',x=x,z=z,dryRun=False),allow_write=True)
                assert result.get('success') is True,result
            evidence['fixture']={'wall_cells':selected,'door_cells':[],'kind':'blueprints'}
            await bridge.call('rimworld/jump_camera_to_cell',x=selected[0][0]+3,z=selected[0][1]+3)
            await bridge.call('rimworld/set_camera_zoom',rootSize=12)
            await asyncio.sleep(1)
            before=(await bridge.call('rimworld/get_camera_state')).structuredContent
            started=time.monotonic()
            result=await rt.visual_review('Review the visible proposed building layout for practical usability problems.',
                expected_token=rt.context_token,expected_revision=rt.chat_revision)
            evidence.update(result=result,elapsed_seconds=time.monotonic()-started,metrics=rt.router.metrics)
            after=(await bridge.call('rimworld/get_camera_state')).structuredContent
            evidence['camera_before']=before;evidence['camera_after']=after
            assert all(before[k]==after[k] for k in ('mapId','mapPosition','rootSize')),'Camera position or zoom changed'
            evidence['viewport_changed']=before['viewRect']!=after['viewRect']
            text=json.dumps(result['report']).lower()
            evidence['acceptance']={'door_concern_mentioned':bool(re.search(r'\b(door|doorway|entrance|entry)\b',text)),
                'automated_check_only':True,'requires_human_review':True,
                'note':'Protocol success is not visual accuracy. Inspect concerns against the screenshot and native fixture.'}
            status=await rt.game.query('home/status')
            assert status['time']['paused'] and status['time']['ticksGame']==rt.batch.summary.end_tick
            assert rt.current_plan.revision==0
            print(json.dumps(evidence,indent=2),flush=True)
        finally:
            (root/'visual-smoke.json').write_text(json.dumps(evidence,indent=2),encoding='utf8')
            await rt.router.close();store.close()


if __name__=='__main__':asyncio.run(main())

