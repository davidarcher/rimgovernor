"""Test-only speed benchmark. Normal controller/model safety policy is unchanged.
Renders normally; omits dashboard captures. Does NOT claim headless performance.
"""
import argparse
import asyncio
import json
import time
from pathlib import Path
from rimbot.bridge import bridge_session, BridgeError

async def main(seconds,repeats):
    root=Path('.rimbot/bridge').resolve();rows=[]
    async with bridge_session(root/'gabs/gabs-v1.1.1-windows-amd64/gabs.exe',root/'config') as bridge:
        await bridge.core('games_start',gameId=bridge.game_id);await bridge.connect()
        try:
            for speed in ('Normal','Superfast','Ultrafast'):
                for repeat in range(repeats):
                    await bridge.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',readiness='visual',timeoutMs=90000)
                    await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
                    before=(await bridge.call('home/status',colonists=False,threats=False)).structuredContent['time']['ticksGame']
                    start=time.perf_counter()
                    completed=True
                    try:
                        receipt=(await bridge.call('rimworld/play_for',durationMs=int(seconds*1000),speed=speed,forceRequestedSpeed=True)).structuredContent
                    except BridgeError as error:
                        if 'paused externally' not in str(error):
                            raise
                        completed=False
                        receipt=error.result.structuredContent
                    elapsed=time.perf_counter()-start
                    after=(await bridge.call('home/status',colonists=False,threats=False)).structuredContent['time']
                    assert after['paused'],after
                    row={'completed':completed,'speed':speed,'repeat':repeat+1,'ticks':after['ticksGame']-before,'elapsed_seconds':elapsed,
                        'tps':(after['ticksGame']-before)/elapsed,'rendering':'normal','dashboard_captures':False,'native_receipt':receipt}
                    row['native_tps'] = receipt['advancedTicks']/(receipt['elapsedMs']/1000) if receipt.get('elapsedMs') else None
                    rows.append(row)
                    print(f"{speed}: {row['tps']:.0f} TPS ({row['ticks']} ticks / {elapsed:.2f}s; {'full sample' if completed else 'interrupted by external/game pause'})",flush=True)
        finally:
            await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
            (root/'speed-benchmark.json').write_text(json.dumps(rows,indent=2),encoding='utf8')

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--seconds',type=float,default=5);parser.add_argument('--repeats',type=int,default=2)
    args=parser.parse_args()
    if not 1<=args.seconds<=30 or not 1<=args.repeats<=10:parser.error('Use 1..30 seconds and 1..10 repeats')
    asyncio.run(main(args.seconds,args.repeats))
