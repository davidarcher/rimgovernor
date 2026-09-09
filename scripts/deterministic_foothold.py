"""Run the production deterministic controller in a fresh, isolated colony."""
import argparse
import asyncio
import json
import time
import traceback
import shutil
from pathlib import Path
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.campaign_manifest import capture_manifest
from rimbot.headless import isolated_root, prepare, prepare_rendered
from rimbot.store import Store


class NoInference:
    attempts = 0
    async def complete(self, *_):
        NoInference.attempts += 1
        raise AssertionError('Autopilot attempted inference')
    async def close(self):
        pass


async def run(args):
    NoInference.attempts = 0
    root = isolated_root(args.source_root, args.output/'bridge')
    if args.checkpoint:
        shutil.copy2(args.checkpoint,root/'profile/Saves/RimBot-tribal8-baseline.rws')
    config = prepare_rendered(root) if args.rendered else prepare(root)
    store = Store(args.output/'state.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=not args.rendered, model_factory=lambda _: NoInference())
    report = {'outcome':'error','model_calls':0,'history':[],'save_edits':[]}
    report['start_type']='saved_checkpoint' if args.checkpoint else 'fresh_baseline'
    if args.checkpoint: report['checkpoint_source']=str(args.checkpoint.resolve())
    start = time.monotonic()
    try:
        manifest = capture_manifest(Path(__file__).resolve().parents[1], root, config,
            rt.router.routing.model_dump(mode='json'), profile=root/('profile' if args.rendered else 'headless-profile'))
        (args.output/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf8')
        await rt.start()
        deadline = time.monotonic()+120
        while not rt.connected and time.monotonic()<deadline:
            if rt.phase == 'Connection failed': raise RuntimeError(rt.chat[-1] if rt.chat else rt.phase)
            await asyncio.sleep(1)
        if not rt.connected: raise RuntimeError('Colony connection timed out')
        report['initial_game_tick']=rt.batch.summary.end_tick
        if not args.checkpoint and report['initial_game_tick']>600:
            raise ValueError('Fresh baseline must be within its first 600 game ticks; use --checkpoint for resumed saves')
        rt.current_plan.control.setdefault('policy', {})['execution_speed'] = args.speed
        await rt.set_mode('automate')
        deadline = time.monotonic()+args.seconds
        while time.monotonic()<deadline:
            await asyncio.sleep(5)
            control = rt.current_plan.control
            row = {'elapsed':round(time.monotonic()-start,1),'tick':rt.clock.get('ticksGame'),
                   'status':control.get('status'),'criteria':control.get('criteria'),
                   'mode':rt.mode,'phase':rt.phase,'steps':len(rt.current_plan.spec.steps),
                   'goals':{k:{'status':v.status,'reason':v.reason,'method':v.method} for k,v in rt.current_plan.colony_goals.items()}}
            report['history'].append(row)
            (args.output/'progress.json').write_text(json.dumps(row,indent=2),encoding='utf8')
            print(json.dumps(row),flush=True)
            if rt.counters['model_calls'] or NoInference.attempts: raise AssertionError('Routine controller attempted a model call')
            if rt.mode != 'automate': raise RuntimeError('Controller left Automate: '+str(rt.chat[-1] if rt.chat else rt.phase))
            if (not rt.deliberating and not rt.wake.is_set() and rt.handled_revision >= rt.chat_revision
                    and rt.current_plan.spec.steps and not control.get('simulation_needed')
                    and all(p.state in ('complete','blocked','cancelled') for p in rt.current_plan.progress.values())
                    and any(g.status=='blocked' for g in rt.current_plan.colony_goals.values())):
                report['outcome']='blocked'
                break
            if control.get('status') == 'FOOTHOLD_STABLE':
                report['outcome']='FOOTHOLD_STABLE'
                report['game_ticks_to_foothold']=rt.clock.get('ticksGame')-report['initial_game_tick']
                break
        else:
            report['outcome']='timeout'
    except Exception as error:
        report.update(error=str(error),traceback=traceback.format_exc())
    finally:
        try:
            if rt.connected and rt.bridge:
                async with rt.lock:
                    await rt.halt()
                    await rt.bridge.core('games_stop', gameId=rt.bridge.game_id)
        except Exception as error:
            report['cleanup_error'] = str(error)
        finally:
            await rt.stop()
        # Runtime stops its own GABS/game only through the PID-owned launch profile.
        report['plan']=rt.current_plan.model_dump()
        report['events']=store.history(rt.colony,limit=10000,include_diagnostics=True)
        report['model_calls']=rt.counters['model_calls']
        report['model_attempts']=NoInference.attempts
        report['elapsed_seconds']=round(time.monotonic()-start,2)
        store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2),encoding='utf8')
    return report['outcome']=='FOOTHOLD_STABLE'


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--seconds',type=int,default=900)
    parser.add_argument('--rendered',action='store_true')
    parser.add_argument('--checkpoint',type=Path,help='Debug resume from an unmodified native save; not a fresh-colony acceptance run')
    parser.add_argument('--speed',choices=['Normal','Fast','Superfast'],default='Fast')
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
