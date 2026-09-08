"""Test-only boosted Ultrafast comparison; its native ceiling is 9,000 TPS."""
import asyncio
import json
import time
from pathlib import Path
from native_render_smoke import game_window
from rimbot.bridge import bridge_session
from rimbot.headless import prepare


async def main():
    root=Path('.rimbot/bridge').resolve()
    rows=[]
    for headless in (False,True):
        config=prepare(root) if headless else root/'config'
        async with bridge_session(root/'gabs/gabs-v1.1.1-windows-amd64/gabs.exe',config) as bridge:
            async def call(name,**args):return (await bridge.call(name,**args)).structuredContent
            await bridge.core('games_start',gameId=bridge.game_id);await bridge.connect()
            window=None
            try:
                for mode in (['headless'] if headless else ['rendered','suspended']):
                    for repeat in range(2):
                        await call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',readiness='visual',timeoutMs=90000,ignoreModCompatibility=headless)
                        await call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
                        if not headless:
                            user,window=game_window()
                            user.ShowWindowAsync(window,6)
                            # Previous render lease must expire before the suspended sample.
                            if mode=='suspended':await asyncio.sleep(12)
                            state=await call('home/render_demand',seconds=20 if mode=='rendered' else 0)
                            assert state['suspended']==(mode=='suspended'),state
                        before=await call('home/status',colonists=False,threats=False)
                        start=time.perf_counter()
                        receipt=await call('rimworld/set_time_speed',speed='Ultrafast',ultraSpeedBoost=True)
                        warnings=[]
                        for _ in range(10):
                            await asyncio.sleep(1)
                            status=await call('home/status',colonists=False,threats=False)
                            if status['time']['paused']:
                                letters=await call('rimworld/list_letters')
                                listed=letters.get('letters',[])
                                if len(listed)==1 and listed[0]['label']=='Ancient danger' and not warnings:
                                    warnings.append(listed[0]['id'])
                                    await call('rimworld/set_time_speed',speed='Ultrafast',ultraSpeedBoost=True)
                                else:raise RuntimeError(f'Unexpected benchmark pause: {letters}')
                        await call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
                        elapsed=time.perf_counter()-start
                        after=await call('home/status',colonists=False,threats=False)
                        ticks=after['time']['ticksGame']-before['time']['ticksGame']
                        row={'mode':mode,'repeat':repeat+1,'ticks':ticks,'seconds':round(elapsed,3),
                             'tps':round(ticks/elapsed,1),'pause_warnings':warnings,'speed_receipt':receipt}
                        rows.append(row)
                        (root/'throughput.json').write_text(json.dumps(rows,indent=2),encoding='utf8')
                        print({k:v for k,v in row.items() if k!='speed_receipt'},flush=True)
            finally:
                await call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
                if window is not None:user.ShowWindowAsync(window,9)


if __name__=='__main__':asyncio.run(main())
