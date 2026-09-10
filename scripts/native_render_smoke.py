"""Windows rendered-process test: demand expiry, live ticks, wake and TPS samples.
Close the dashboard controller first. No models, speed boost, or saved mutations.
"""
from rimbot.bridge import gabs_executable
import asyncio
import ctypes
import json
import time
from pathlib import Path
from rimbot.bridge import bridge_session


def game_window():
    user=ctypes.windll.user32
    handles=[]
    callback=ctypes.WINFUNCTYPE(ctypes.c_bool,ctypes.c_void_p,ctypes.c_void_p)
    def visit(handle,_):
        text=ctypes.create_unicode_buffer(512)
        user.GetWindowTextW(ctypes.c_void_p(handle),text,512)
        if text.value.startswith('RimWorld'):handles.append(handle)
        return True
    user.EnumWindows(callback(visit),0)
    if len(handles)!=1:raise RuntimeError(f'Expected one RimWorld window, found {len(handles)}')
    return user,ctypes.c_void_p(handles[0])


async def main():
    root=Path('.rimbot/bridge').resolve()
    evidence={'samples':[]}
    async with bridge_session(gabs_executable(root),root/'config') as bridge:
        await bridge.core('games_start',gameId=bridge.game_id);await bridge.connect()
        await bridge.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',readiness='visual',timeoutMs=90000)
        user,window=game_window()
        async def native(name,**args):return (await bridge.call(name,**args)).structuredContent
        try:
            await native('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
            user.ShowWindowAsync(window,6)
            await asyncio.sleep(1)
            state=await native('home/render_demand',seconds=0)
            assert state['suspended'] and not state['windowVisible'],state
            await native('home/render_demand',seconds=2)
            await asyncio.sleep(.3)
            state=await native('home/render_demand',seconds=0)
            assert not state['suspended'],state
            image=await native('rimworld/take_screenshot',fileName='render-demand-smoke',includeTargets=False,suppressMessage=True)
            assert Path(image['path']).read_bytes().startswith(b'\x89PNG\r\n\x1a\n')
            await asyncio.sleep(3)
            assert (await native('home/render_demand',seconds=0))['suspended']
            evidence['wake_and_expiry']=True
            await native('rimworld/set_time_speed',speed='Superfast',ultraSpeedBoost=False)
            # Warm up and handle only the baseline's known sealed-ruin proximity warning.
            await asyncio.sleep(4)
            status=await native('home/status')
            if status['time']['paused']:
                letters=await native('rimworld/list_letters')
                assert not letters.get('truncated') and len(letters['letters'])==1 and letters['letters'][0]['label']=='Ancient danger',letters
                evidence['fixture_warning']=letters['letters'][0]['id']
                await native('rimworld/set_time_speed',speed='Superfast',ultraSpeedBoost=False)
            for enabled in (True,False,True):
                # Allow the preceding short lease to expire before a suspended sample.
                if not enabled:await asyncio.sleep(4)
                await native('home/render_demand',seconds=3 if enabled else 0)
                before=await native('home/status');start=time.monotonic()
                for _ in range(5):
                    await native('home/render_demand',seconds=3 if enabled else 0)
                    await asyncio.sleep(1)
                after=await native('home/status');elapsed=time.monotonic()-start
                state=await native('home/render_demand',seconds=0)
                assert not after['time']['paused'],after['time']
                ticks=after['time']['ticksGame']-before['time']['ticksGame']
                assert ticks>0 and state['suspended']!=enabled,state
                sample={'rendering':enabled,'ticks':ticks,'seconds':round(elapsed,3),'tps':round(ticks/elapsed,1)}
                evidence['samples'].append(sample);print(sample,flush=True)
            user.ShowWindowAsync(window,9)
            await asyncio.sleep(1)
            state=await native('home/render_demand',seconds=0)
            assert state['windowVisible'] and not state['suspended'],state
            evidence['restore']=True
            print('PASS: demand wake/expiry, simulation while suspended, window restore',flush=True)
        finally:
            await native('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
            user.ShowWindowAsync(window,9)
            (root/'render-smoke.json').write_text(json.dumps(evidence,indent=2),encoding='utf8')


if __name__=='__main__':asyncio.run(main())

