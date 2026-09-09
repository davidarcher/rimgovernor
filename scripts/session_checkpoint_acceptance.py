"""Restart an isolated native game and verify paired colony/controller progress."""
import argparse
import asyncio
import json
import time
from pathlib import Path
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.headless import isolated_root, prepare_rendered
from rimbot.player_commands import apply_command
from rimbot.session_checkpoint import create_checkpoint, prepare_resume, stop_for_restart
from rimbot.store import Store


async def ready(rt):
    await rt.start()
    deadline=time.monotonic()+120
    while not rt.connected:
        if rt.phase=='Connection failed' or time.monotonic()>deadline:
            raise RuntimeError(str(rt.chat[-1] if rt.chat else rt.phase))
        await asyncio.sleep(.5)


async def main(args):
    root=isolated_root(args.source_root,args.output/'bridge')
    if args.rendered: prepare_rendered(root)
    store=Store(args.output/'initial.sqlite')
    rt=BridgeRuntime(store,root,fresh=True,headless=not args.rendered)
    report={}
    try:
        await ready(rt)
        initial=rt.batch.summary.end_tick
        await apply_command(rt,{'kind':'CreateGoal','goal':'EnsureFoodSupply','food_days':20},
                            token=rt.context_token,revision=rt.chat_revision)
        rt.reply('Checkpoint acceptance: preserve this conversation.')
        await rt.bridge.call('rimworld/set_time_speed',speed='Fast',ultraSpeedBoost=False)
        await asyncio.sleep(2)
        checkpoint=await create_checkpoint(rt,rt.context_token)
        assert checkpoint['tick']>initial
        report.update(checkpoint=checkpoint,initial_tick=initial,old_token=rt.context_token,
                      plan=rt.current_plan.model_dump(),chat=rt.chat)
        await stop_for_restart(rt,rt.context_token,checkpoint['manifest_path'])
    finally:
        await rt.stop();store.close()
    data,state=prepare_resume(checkpoint['manifest_path'])
    store=Store(state/'bridge.sqlite')
    resumed=BridgeRuntime(store,root,fresh=True,headless=not args.rendered,resume=checkpoint['manifest_path'])
    try:
        await ready(resumed)
        assert resumed.mode=='manual' and resumed.context_token!=report['old_token']
        assert resumed.batch.summary.end_tick in (data['tick'], data['tick']+1)
        assert resumed.current_plan.model_dump()==report['plan']
        assert resumed.chat==report['chat']
        assert not resumed.draft_owners and resumed.counters['model_calls']==0
        report.update(outcome='PASS',resumed_tick=resumed.batch.summary.end_tick,new_token=resumed.context_token)
        print('PASS: native tick, identity, PLAYER goal, policy and conversation preserved; new load in Manual',flush=True)
    finally:
        await resumed.stop();store.close()
        (args.output/'report.json').write_text(json.dumps(report,indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--rendered',action='store_true',help='Verify the visible private profile instead of headless mode')
    asyncio.run(main(parser.parse_args()))
