"""Controlled native campaign metrics acceptance; gated fixture setup, ordinary pawn use/work.

Requires a test build with CampaignMetricsFixture=true installed while every game
is closed. This script never installs DLLs and always stops its owned game.
"""
from rimbot.bridge import gabs_executable
import argparse
import asyncio
import hashlib
import json
import subprocess
import time
import traceback
from pathlib import Path

from rimbot.bridge import bridge_session
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_observation import observe
from rimbot.campaign_metrics import CampaignEvidence, event_metrics
from rimbot.colony_plan import CommitSteps
from rimbot.config import ModelRole
from rimbot.headless import isolated_root, prepare
from rimbot.store import Store
from headless_iterations import FastTrial, sample_metrics


def require(value, evidence):
    if not value:raise AssertionError(evidence)


async def run(args):
    root=isolated_root(args.source,args.output)
    report={'outcome':'failed','revision':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),
            'scope':'Scripted disposable metric acceptance; no autonomous planning claim','snapshots':[],'interventions':[]}
    evidence=CampaignEvidence();began=time.monotonic();report['began_at']=time.time()
    report['thresholds']=evidence.report()['thresholds']
    (root/'thresholds.json').write_text(json.dumps(report['thresholds'],indent=2))
    try:
        async with bridge_session(gabs_executable(root),prepare(root)) as bridge:
            store=Store(root/'state.sqlite');rt=FastTrial(store,root,headless=True)
            try:
                await bridge.core('games_start',gameId=bridge.game_id)
                await bridge.connect()
                await bridge.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',readiness='visual',timeoutMs=90000,ignoreModCompatibility=True)
                await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
                rt.bridge=bridge;rt.game=BridgeGame(bridge);rt.connected=True
                await rt.sync_identity();rt.batch=await observe(rt.game)
                fixture=(await bridge.call('test/campaign_metrics_fixture',action='sleep')).structuredContent
                report['fixture']=fixture
                anchor=(fixture['anchor']['x'],fixture['anchor']['z'])
                async def speed(value):
                    await bridge.call('rimworld/set_time_speed',speed=value,ultraSpeedBoost=False)
                async def sample(phase):
                    capture={}
                    row=await sample_metrics(rt,evidence,anchor,round(time.monotonic()-began,2),capture=capture)
                    report['snapshots'].append(dict(phase=phase,metrics=row,**capture))
                    return row
                async def until(phase,predicate):
                    deadline=time.monotonic()+args.seconds
                    while time.monotonic()<deadline:
                        row=await sample(phase)
                        if predicate(row):return row
                        await asyncio.sleep(.5)
                    raise AssertionError({'unobserved_case':phase,'last_metrics':row})
                baseline=await sample('prepared')
                require(baseline['excess_sleeping_places']==2,baseline)
                require(baseline['functional']['sleeping_capacity_access_verified'] is None,baseline)
                await speed('Fast')
                sleeping=await until('sleeping',lambda r:r['functional']['sleeping_capacity_access_verified'] is True)
                report['sleeping_acceptance']=sleeping
                await speed('Paused')
                report['work_fixture']=(await bridge.call('test/campaign_metrics_fixture',action='work')).structuredContent
                report['interventions'].append('Fixture reset rest/food and work schedules after actual sleeping observations')
                await speed('Normal')
                hauling=await until('hauling',lambda r:r['functional']['storage_access_verified'] is True)
                report['hauling_acceptance']=hauling
                await speed('Paused')
                await rt.set_mode('automate')
                rt.batch=await observe(rt.game)
                cell=fixture['constructionCell']
                proposal=CommitSteps(expected_revision=rt.current_plan.revision,reason='Controlled interruption case',steps=[dict(
                    id='metric-wall',title='Metric wall',completion_criteria='Exact wall built',
                    action=dict(kind='place_buildings',placements=[dict(x=cell['x'],z=cell['z'],def_name='Wall',materials=['WoodLog'])]))])
                await rt.commit_strategy(proposal.decision(rt.current_plan),actor=ModelRole.STRATEGIST,
                                         expected_token=rt.context_token,expected_revision=rt.chat_revision)
                await rt.hands.advance(rt)
                require(rt.current_plan.progress['metric-wall'].issued,rt.current_plan.model_dump())
                issued=await sample('issued')
                require(issued['pending_ids'],issued)
                await rt.set_mode('manual')
                interrupted=await sample('interrupted')
                require('metric-wall' in interrupted['interrupted_step_ids'],interrupted)
                require(interrupted['pending_ids']==issued['pending_ids'],interrupted)
                await rt.hands.advance(rt)
                require((await sample('still-interrupted'))['pending_ids']==issued['pending_ids'],'Manual replay changed work')
                await rt.set_mode('automate')
                await rt.hands.advance(rt)
                require((await sample('resumed'))['pending_ids']==issued['pending_ids'],'Resume duplicated work')
                await speed('Fast')
                def completed(row):
                    return any(b['status']=='built' and b['defName']=='Wall' and b['position']['x']==cell['x'] and b['position']['z']==cell['z']
                               for b in report['snapshots'][-1]['buildings']['buildings'])
                report['construction_acceptance']=await until('construction',completed)
                await speed('Paused')
                orders=await rt.game.invoke('rimworld/list_architect_designators',{'categoryId':'Orders'})
                deconstruct=next(d for d in orders['designators'] if d['className']=='RimWorld.Designator_Deconstruct')
                for bed in fixture['beds'][8:]:
                    await rt.native('rimworld/apply_architect_designator',dict(designatorId=deconstruct['id'],
                        x=bed['position']['x'],z=bed['position']['z'],dryRun=False,keepSelected=False))
                await speed('Fast')
                removed=await until('removal',lambda r:r['nearby_sleeping_capacity']==8 and r['excess_sleeping_places']==0)
                removed_ids={identity for row in evidence.samples for identity in row['removed_completed_ids']}
                require(len(removed_ids)>=2,removed_ids)
                report['removal_acceptance']=removed
                await speed('Paused')
                report['rest_fixture']=(await bridge.call('test/campaign_metrics_fixture',action='rest')).structuredContent
                await speed('Fast')
                report['combined_capacity_acceptance']=await until('combined-capacity',lambda r:r['functional']['usable_capacity_verified'] is True)
                native_events=store.history(rt.colony,limit=10000,include_diagnostics=True)
                useful=[e for e in native_events if e['kind']=='campaign_native' and e.get('useful_order_receipt')]
                require(useful and useful[0]['tool']=='home/place_building',useful)
                require(event_metrics(native_events,began_at=report['began_at'])['first_useful_order_seconds'] is not None,native_events)
                report['outcome']='passed'
            finally:
                if rt.bridge:
                    try:await rt.halt()
                    except Exception as error:report['cleanup_error']=str(error)
                report['metrics']=evidence.report()
                report['events']=store.history(rt.colony,limit=10000,include_diagnostics=True)
                report['telemetry']=event_metrics(report['events'],began_at=report['began_at'],role_metrics=rt.router.metrics)
                await rt.router.close();store.close()
                await bridge.core('games_stop',gameId=bridge.game_id)
    except Exception as error:
        report.update(outcome='failed',error=str(error),traceback=traceback.format_exc())
    finally:
        report['elapsed_seconds']=round(time.monotonic()-began,2)
        (root/'metrics-result.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
        print(json.dumps({'outcome':report['outcome'],'error':report.get('error'),'path':str(root/'metrics-result.json')}),flush=True)
    return report['outcome']=='passed'


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--seconds',type=int,default=120,help='Maximum wall seconds per observed case')
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
