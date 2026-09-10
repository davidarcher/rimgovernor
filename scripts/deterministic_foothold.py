"""Run the production deterministic controller in a fresh, isolated colony."""
from dataclasses import dataclass, asdict
import math
import argparse
import asyncio
import json
import time
import traceback
import shutil
import hashlib
import xml.etree.ElementTree as ET
from pathlib import Path
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.bridge import runtime_file_read
from rimbot.campaign_manifest import capture_manifest
from rimbot.headless import isolated_root, prepare, prepare_rendered
from rimbot.store import Store


STABILITY_GATES=frozenset(('sleeping','shelter','food','production','storage','cooking','temperature','power','medical','defense','work'))


class NoInference:
    attempts = 0
    async def complete(self, *_):
        NoInference.attempts += 1
        raise AssertionError('Autopilot attempted inference')
    async def close(self):
        pass


@dataclass
class StabilityWindow:
    required_ticks: int
    first_stable_tick: int | None = None
    window_start_tick: int | None = None
    last_tick: int | None = None
    stable_ticks: int = 0
    longest_stable_ticks: int = 0
    losses: int = 0
    max_observation_gap: int = 0
    gap_resets: int = 0

    def observe(self, tick, status, gates, losses):
        # Use the tick belonging to the facts, never a newer live clock tick.
        if tick is None: return False
        if self.last_tick is not None and tick < self.last_tick:
            raise ValueError('Game observations moved backward; a rewound episode cannot certify stability')
        gap=0 if self.last_tick is None else tick-self.last_tick
        self.max_observation_gap=max(self.max_observation_gap,gap)
        stable=status=='FOOTHOLD_STABLE' and STABILITY_GATES.issubset(gates) and all(v is True for v in gates.values())
        if not stable or losses!=self.losses or gap>6000:
            if gap>6000: self.gap_resets+=1
            self.window_start_tick=None
            self.stable_ticks=0
        self.last_tick,self.losses=tick,losses
        if not stable: return False
        if self.first_stable_tick is None: self.first_stable_tick=tick
        if self.window_start_tick is None: self.window_start_tick=tick
        self.stable_ticks=tick-self.window_start_tick
        self.longest_stable_ticks=max(self.longest_stable_ticks,self.stable_ticks)
        return self.stable_ticks>=self.required_ticks


def stability_days(value):
    days=float(value)
    if not math.isfinite(days) or not 0<=days<=30:
        raise argparse.ArgumentTypeError('Stability days must be finite and between 0 and 30')
    return days


def sample_food_acceptance(acceptance, observer, facts, target_days):
    harvests=sorted((p for p in observer['production']
        if p['plant'] in ('Plant_Rice','Plant_Potato','Plant_Corn') and p['count']>0),key=lambda p:p['tick'])
    acceptance['crop_harvests']=harvests
    stocks=[s for s in (facts.get('foodSupply') or {}).get('stocks',[])
        if s.get('defName') in ('RawRice','RawPotatoes','RawCorn') and s.get('eaters') and s.get('count',0)>0]
    tick=facts.get('tick')
    if harvests and tick is not None and tick>=harvests[0]['tick']:
        if stocks:acceptance.setdefault('crop_stock_observations',[]).append(dict(tick=tick,stocks=stocks))
        runway=facts.get('foodRunwayDays')
        if target_days is not None and runway is not None and runway>=target_days:
            acceptance.setdefault('target_observations',[]).append(dict(tick=tick,runway=runway))
    acceptance['passed']=(bool(harvests) and harvests[-1]['tick']-harvests[0]['tick']>=60000
        and bool(acceptance.get('crop_stock_observations'))
        and (target_days is None or bool(acceptance.get('target_observations'))))


async def run(args):
    NoInference.attempts = 0
    window=StabilityWindow(math.ceil(args.stability_days*60000))
    root = isolated_root(args.source_root, args.output/'bridge')
    if args.checkpoint:
        shutil.copy2(args.checkpoint,root/'profile/Saves/RimBot-tribal8-baseline.rws')
    config = prepare_rendered(root) if args.rendered else prepare(root)
    store = Store(args.output/'state.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=not args.rendered, model_factory=lambda _: NoInference())
    report = {'outcome':'error','model_calls':0,'history':[],'save_edits':[]}
    lifecycle_days=getattr(args,'lifecycle_days',None)
    join_count=getattr(args,'join_count',0)
    report['join_incidents']=[]
    if lifecycle_days is not None:
        from lifecycle_measurement import ledger_sample,bed_use_sample
        report['lifecycle']={'scope':'Native goal lifecycle and bed use; food/survival gates scored separately',
            'days':lifecycle_days,'ledger':[],'bed_use':[],'boundaries':[]}
        original_note=rt.note
        def measured_note(kind,*values,**data):
            if kind=='clock_event' and data.get('native_event',{}).get('kind') in ('long_event','force_pause_cleared','tick_budget'):
                report['lifecycle']['boundaries'].append({'event':data['native_event'],
                    'native_clock':dict(rt.supervisor.state),'window_deadline':rt.execution_window_end,'plan':ledger_sample(rt,args.output/'state.sqlite')})
            return original_note(kind,*values,**data)
        rt.note=measured_note
    report['start_type']='saved_checkpoint' if args.checkpoint else 'fresh_baseline'
    if args.checkpoint: report['checkpoint_source']=str(args.checkpoint.resolve())
    start = time.monotonic()
    try:
        manifest = capture_manifest(Path(__file__).resolve().parents[1], root, config,
            rt.router.routing.model_dump(mode='json'), profile=root/('profile' if args.rendered else 'headless-profile'),
            source_snapshot=getattr(args,'source_snapshot',False))
        (args.output/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf8')
        await rt.start()
        deadline = time.monotonic()+120
        while not rt.connected and time.monotonic()<deadline:
            if rt.phase == 'Connection failed': raise RuntimeError(rt.chat[-1] if rt.chat else rt.phase)
            await asyncio.sleep(1)
        if not rt.connected: raise RuntimeError('Colony connection timed out')
        if getattr(args,'food_observer',False):
            report['food_observer']=(await runtime_file_read(rt.bridge.call,'test/food_observe')).structuredContent
        rt.supervisor.test_acceleration = getattr(args, 'accelerated', False)
        report['test_acceleration'] = rt.supervisor.test_acceleration
        report['initial_game_tick']=rt.batch.summary.end_tick
        report['starting_colonists']=sorted(p.thing_id for p in rt.batch.summary.pawns if not p.dead)
        initial_token=rt.context_token
        if not args.checkpoint and report['initial_game_tick']>600:
            raise ValueError('Fresh baseline must be within its first 600 game ticks; use --checkpoint for resumed saves')
        if getattr(args,'archive_fixture',False):
            from session_checkpoint_acceptance import prepare_native_archive
            report['archive_fixture']=await prepare_native_archive(rt)
        if getattr(args,'recovery_fixture',False):
            from construction_recovery_acceptance import exercise_recovery
            report['recovery_fixture']=await exercise_recovery(rt)
        rt.current_plan.control.setdefault('policy', {})['execution_speed'] = args.speed
        if getattr(args,'disable_hunting',False):
            from rimbot.player_commands import apply_command
            if rt.review_task and not rt.review_task.done():await rt.review_task
            rt.execution_task=asyncio.current_task()
            try:
                people=(await rt.game.query('home/list_pawns',colonistsOnly=True,work=True))['pawns']
                report['crop_scope']={'hunting':'Disabled through explicit persistent player work settings',
                    'scope':'Crop-focused acceptance; native hunting and unsafe-route pauses are separate cases'}
                for pawn in people:
                    if not any(w['name']=='Hunting' and w.get('disabled') is False for w in pawn['work']['types']):continue
                    await apply_command(rt,dict(kind='SetWorkPriority',pawn=pawn['thingId'],
                        work_type='Hunting',priority=0),token=rt.context_token,revision=rt.chat_revision)
                    await rt.execute_manual_requests()
                checked=(await rt.game.query('home/list_pawns',colonistsOnly=True,work=True))['pawns']
                assert all(not w.get('priority') for p in checked for w in p['work']['types'] if w['name']=='Hunting')
                report['crop_scope']['work_readback']=checked
            finally:
                rt.execution_task=None
        if getattr(args,'food_target_days',None) is not None:
            from rimbot.player_commands import apply_command
            report['food_target']=await apply_command(rt,dict(kind='CreateGoal',goal='EnsureFoodSupply',
                food_days=args.food_target_days),token=rt.context_token,revision=rt.chat_revision)
        from startup_milestones import StartupMilestones
        milestones = StartupMilestones(rt)
        await rt.set_mode('automate')
        deadline = time.monotonic()+args.seconds
        while time.monotonic()<deadline:
            await asyncio.sleep(5)
            milestones.sample(rt)
            report['startup_milestones'] = milestones.report()
            control = rt.current_plan.control
            facts=control.get('facts',{})
            losses=store.db.execute("SELECT COUNT(*) FROM events WHERE colony=? AND kind='bootstrap_stability_lost'",(rt.colony,)).fetchone()[0]
            passed=window.observe(facts.get('tick'),control.get('status'),control.get('criteria',{}),losses)
            row = {'elapsed':round(time.monotonic()-start,1),'tick':rt.clock.get('ticksGame'),
                   'status':control.get('status'),'criteria':control.get('criteria'),
                   'mode':rt.mode,'phase':rt.phase,'steps':len(rt.current_plan.spec.steps),
                   'execution':dict(resume_after_review=rt.resume_after_review,deliberating=rt.deliberating,
                       wake=rt.wake.is_set(),handled_revision=rt.handled_revision,chat_revision=rt.chat_revision,
                       clock_hold=rt.supervisor.hold,window_end=rt.execution_window_end),
                   'threats':rt.batch.native.get('status_after',{}).get('threats',{}),
                   'colonists':facts.get('colonists'),'indoor_sleeping':facts.get('indoorSleepingCapacity'),
                   'usable_farm_cells':sum(f.get('usableCells',0) for f in facts.get('farms',[]) if f.get('edible')),
                   'food':{k:facts.get(k) for k in ('foodSupply','foodForecast','foodNutrition',
                       'nutritionPerDay','foodRunwayDays','foodClimate','foodCrop','foodCorpses','farms','cooking')},
                   'stability':asdict(window),
                   'goals':{k:{'status':v.status,'reason':v.reason,'method':v.method} for k,v in rt.current_plan.colony_goals.items()}}
            report['history'].append(row)
            if getattr(args,'food_observer',False):
                report['food_observer']=(await runtime_file_read(rt.bridge.call,'test/food_observe')).structuredContent
                (args.output/'food-observer.json').write_text(json.dumps(report['food_observer'],indent=2),encoding='utf8')
                assert not report['food_observer']['truncated']
                report.setdefault('food_acceptance',{'target_observations':[]})
                acceptance=report['food_acceptance']
                if args.food_target_days is not None:
                    assert rt.current_plan.colony_goals['EnsureFoodSupply'].target['food_days']==args.food_target_days
                    assert rt.controller.policy.food_target_days==args.food_target_days
                sample_food_acceptance(acceptance,report['food_observer'],facts,args.food_target_days)
                (args.output/'food-acceptance.json').write_text(json.dumps(acceptance,indent=2),encoding='utf8')
            if lifecycle_days is not None:
                async with rt.lock:
                    report['lifecycle']['ledger'].append(ledger_sample(rt,args.output/'state.sqlite'))
                    report['lifecycle']['bed_use'].append(await bed_use_sample(rt))
            if join_count and len(report['join_incidents'])<join_count and facts.get('tick',0)>=50000:
                async with rt.lock:
                    await rt.supervisor.change('Paused')
                    preview=(await rt.bridge.call('test/join_incident',dryRun=True)).structuredContent
                    report['join_incidents'].append({'preview':preview})
                    if preview.get('eligible') is not True:
                        raise AssertionError('Ordinary joining incident native prerequisites refused: '+str(preview))
                    joined=(await rt.bridge.call('test/join_incident',dryRun=False)).structuredContent
                    report['join_incidents'][-1]['result']=joined
                    if not joined.get('joined'):raise AssertionError('Native joining incident did not add a colonist')
                    rt.wake.set()
            (args.output/'progress.json').write_text(json.dumps(row,indent=2),encoding='utf8')
            print(json.dumps(row),flush=True)
            if rt.context_token!=initial_token: raise RuntimeError('Colony/load identity changed during acceptance')
            living={p.thing_id for p in rt.batch.summary.pawns if not p.dead}
            missing=set(report['starting_colonists'])-living
            if missing: raise RuntimeError('Starting colonists died or left the observed colony: '+str(sorted(missing)))
            if rt.counters['model_calls'] or NoInference.attempts: raise AssertionError('Routine controller attempted a model call')
            if rt.mode != 'automate': raise RuntimeError('Controller left Automate: '+str(rt.chat[-1] if rt.chat else rt.phase))
            if (not rt.deliberating and not rt.wake.is_set() and rt.handled_revision >= rt.chat_revision
                    and rt.current_plan.spec.steps and not control.get('simulation_needed')
                    and all(p.state in ('complete','blocked','cancelled') for p in rt.current_plan.progress.values())
                    and any(g.status=='blocked' for g in rt.current_plan.colony_goals.values())):
                report['outcome']='blocked'
                break
            if lifecycle_days is not None and facts.get('tick',0)-report['initial_game_tick']>=math.ceil(lifecycle_days*60000):
                report['outcome']='LIFECYCLE_WINDOW'
                break
            if passed and lifecycle_days is None and (not getattr(args,'food_observer',False)
                    or report['food_acceptance']['passed']):
                report['outcome']='SUSTAINED_FOOTHOLD' if window.required_ticks else 'FOOTHOLD_STABLE'
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
            if hasattr(rt,'task'):
                try: await rt.stop()
                except Exception as error: report['runtime_cleanup_error']=str(error)
        # Runtime stops its own GABS/game only through the PID-owned launch profile.
        report['stability']=asdict(window)
        if window.first_stable_tick is not None:
            report['game_ticks_to_foothold']=window.first_stable_tick-report['initial_game_tick']
        report['plan']=rt.current_plan.model_dump()
        report['events']=store.history(rt.colony,limit=10000,include_diagnostics=True)
        report['model_calls']=rt.counters['model_calls']
        report['model_attempts']=NoInference.attempts
        report['elapsed_seconds']=round(time.monotonic()-start,2)
        if lifecycle_days is not None:
            profile=root/('profile' if args.rendered else 'headless-profile')
            report['lifecycle']['autosaves']=[]
            for save in (profile/'Saves').glob('*.rws'):
                if save.name=='RimBot-tribal8-baseline.rws':continue
                xml=ET.parse(save).getroot()
                report['lifecycle']['autosaves'].append({'name':save.name,'tick':int(xml.findtext('.//tickManager/ticksGame')),
                    'sha256':hashlib.sha256(save.read_bytes()).hexdigest()})
        store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2),encoding='utf8')
    return (report['outcome'] in ('FOOTHOLD_STABLE','SUSTAINED_FOOTHOLD','LIFECYCLE_WINDOW')
            and not any(report.get(k) for k in ('error','cleanup_error','runtime_cleanup_error'))
            and report['model_calls']==0 and report['model_attempts']==0)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--source-snapshot',action='store_true',help='Fingerprint packaged source bytes when running in a Docker image without Git metadata')
    parser.add_argument('--food-target-days',type=float,help='Explicit persistent player food target, validated through CreateGoal')
    parser.add_argument('--food-observer',action='store_true',help='Record actual native crop and recipe products through the optional read-only FoodObservationFixture')
    parser.add_argument('--disable-hunting',action='store_true',help='Use ordinary persistent player work settings to isolate crop-focused acceptance; does not certify mixed hunting/crop autonomy')
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--seconds',type=int,default=1800)
    parser.add_argument('--stability-days',type=stability_days,default=2,help='Consecutive observed stable game days after bootstrap; 0 checks establishment only (default: 2)')
    parser.add_argument('--rendered',action='store_true')
    parser.add_argument('--checkpoint',type=Path,help='Debug resume from an unmodified native save; not a fresh-colony acceptance run')
    parser.add_argument('--lifecycle-days',type=stability_days,help='Measure bounded native lifecycle/ledger/bed use over this duration; does not require or certify sustained food gates')
    parser.add_argument('--archive-fixture',action='store_true',help='Archive a natively completed work-setting action and method before measuring sustained immutable retention')
    parser.add_argument('--recovery-fixture',action='store_true',help='Before the campaign, use ordinary stock forbidding to record a verified prewrite construction recovery in the same persistent ledger')
    parser.add_argument('--join-count',type=int,choices=range(4),default=0,help='After tick 50000 request up to three ordinary test-only WandererJoin incidents; requires native CanFireNow and the separate incident fixture')
    parser.add_argument('--speed',choices=['Normal','Fast','Superfast'],default='Fast')
    parser.add_argument('--accelerated',action='store_true',help='Disposable test: bounded supervised native Ultrafast boost; requires updated companion')
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
