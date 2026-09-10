"""One-time migration of a paused legacy server through the existing GABS transport."""
from rimbot.bridge import gabs_executable
import argparse
import asyncio
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import time
import uuid
import httpx
from rimbot.bridge import bridge_session
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_observation import observe
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.session_checkpoint import create_checkpoint, profile_path, stop_for_restart
from rimbot.store import Store
from rimbot.windows_process import ProcessHandle
from rimbot.colony_plan import ColonyPlan


def retain_game_process(runtime):
    if (runtime.get('launchMode') != 'DirectPath' or runtime.get('pidRole') != 'workload'
            or runtime.get('stopProcessName') or type(runtime.get('gamePid')) is not int
            or type(runtime.get('pidStartTime')) is not int or runtime['pidStartTime'] <= 0):
        raise ValueError('Native session lacks exact process birth ownership; regenerate its private profile')
    # GABS Windows fingerprints are the same raw FILETIME used by ProcessHandle.
    return ProcessHandle(runtime['gamePid'], runtime['pidStartTime'])


def read_game_claim(config, runtime):
    claim = json.loads((Path(config)/'rimbot-trial/runtime.json').read_text(encoding='utf8'))
    if claim.get('gamePid') != runtime.get('gamePid'):
        raise ValueError('Native process changed while inspecting ownership')
    return claim


def boundary(work, report, phase):
    report['phase'] = phase
    report['recovery'] = ('resume_checkpoint' if phase in ('game_stopped', 'controller_stopped', 'replacement_started')
                          else 'inspect_checkpoint_and_native' if phase == 'game_stop_pending'
                          else 'explicit_takeover_recovery' if phase in ('takeover_pending', 'taken_over', 'save_pending', 'saved')
                          else 'original_controller')
    temporary = work/'report.pending'
    with temporary.open('w', encoding='utf8') as stream:
        json.dump(report, stream, indent=2)
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(work/'report.json')


def copy_database(source, destination):
    deadline=time.monotonic()+5
    def progress(*_):
        if time.monotonic()>deadline: raise ValueError('Legacy database is busy; migration aborted')
    with sqlite3.connect(Path(source).resolve().as_uri()+'?mode=ro',uri=True,timeout=1) as old:
        with sqlite3.connect(destination) as new: old.backup(new,pages=64,progress=progress,sleep=.05)


def validate_saved_state(saved, state):
    if not saved or saved.get('current_plan',{}).get('revision') != state['currentPlan']['revision']:
        raise ValueError('Database does not match the live colony plan')
    plan=ColonyPlan.model_validate(saved['current_plan'])
    if ({k:v.model_dump() for k,v in plan.colony_goals.items()} != state['currentPlan']['colonyGoals']
            or plan.control != state['currentPlan']['controller']
            or saved.get('chat',[])[-80:] != state['feed']):
        raise ValueError('Database goals, policy or conversation differ from the live state')
    if saved.get('draft_owners'): raise ValueError('Legacy owned drafts must be released before migration')
    handled=min(saved.get('handled_revision',-1),plan.control.get('interpreted_player_revision',0))
    if any(e.get('kind')=='human' and e.get('revision',0)>handled for e in saved.get('chat',[])):
        raise ValueError('A player request is still pending; finish it before migration')


def validate_restored_state(restored, state, checkpoint):
    # Labels/help can evolve with the upgraded code; effective policy cannot.
    if (restored['mode']!='manual' or not restored['game']['paused']
            or restored['sessionId'].rsplit(':',1)[0]!=state['sessionId'].rsplit(':',1)[0]
            or restored['sessionId']==state['sessionId']
            or restored['game']['tick'] not in (checkpoint['tick'],checkpoint['tick']+1)
            or restored['currentPlan']!=state['currentPlan']
            or restored['autopilotSettings']['values']!=state['autopilotSettings']['values']
            or restored['autopilotSettings']['version']!=state['autopilotSettings']['version']
            or restored['feed']!=state['feed']):
        raise ValueError('Restored state mismatch; checkpoint retained and automation remains off')


async def migrate(args):
    root=args.root.resolve();source=args.source.resolve()
    url=f'http://127.0.0.1:{args.port}'
    async with httpx.AsyncClient(base_url=url,timeout=30) as client:
        health=(await client.get('/api/health')).json()
        if health.get('source_root')!=str(source) or health.get('backend')!='rimbridge':
            raise ValueError('Port does not belong to the requested source checkout')
        process=ProcessHandle(health['pid'])
        game_process = None
        try:
            if not args.recover_disconnected:
                response=await client.post('/api/control',json={'mode':'manual'},headers={'X-RimBot':'1'})
                response.raise_for_status()
            state=(await client.get('/api/state')).json()
            if not state['connected'] or state['mode']!='manual' or not state['game']['paused']:
                raise ValueError('Legacy server is not connected and paused in Manual')
            profile_path(root,state['headless'])
            current=(await client.get('/api/health')).json()
            if current!=health or not process.alive(): raise ValueError('Controller identity changed')
            work=root/'migrations'/uuid.uuid4().hex;work.mkdir(parents=True)
            report={'old_pid':process.pid,'old_birth':process.birth,'old_session':state['sessionId']}
            print('Recovery artifacts: '+str(work),flush=True)
            with process.paused(work/'released'):
                boundary(work, report, 'controller_suspended')
                async with asyncio.timeout(60):
                    copy_database(args.database,work/'working.sqlite')
                    boundary(work, report, 'database_copied')
                    store=Store(work/'working.sqlite')
                    try:
                        colony=state['sessionId'].rsplit(':',1)[0]
                        validate_saved_state(store.get('bridge:'+colony),state)
                        config=root/('config-headless' if state['headless'] else 'config')
                        async with bridge_session(gabs_executable(root, config),config) as bridge:
                            status=(await bridge.core('games_status',gameId=bridge.game_id)).structuredContent
                            runtime=status.get('diagnostics',{}).get('runtime',{})
                            game_process = retain_game_process(read_game_claim(config, runtime))
                            # Explicit handoff only after the legacy writer is paused.
                            report.update(outcome='HANDOFF_PENDING',state=state,database=str(args.database.resolve()))
                            report.update(game_pid=game_process.pid, game_birth=game_process.birth)
                            boundary(work, report, 'takeover_pending')
                            await bridge.core('games_connect',gameId=bridge.game_id,forceTakeover=True)
                            boundary(work, report, 'taken_over')
                            rt=BridgeRuntime(store,root,fresh=True,headless=state['headless'])
                            rt.bridge=bridge;rt.game=BridgeGame(bridge)
                            try:
                                await rt.sync_identity()
                                if rt.context_token!=state['sessionId']: raise ValueError('Native colony does not match legacy state')
                                rt.batch=await observe(rt.game);rt.connected=True
                                if rt.public()['currentPlan'] != state['currentPlan']:
                                    raise ValueError('Persisted action progress differs from the legacy controller')
                                if rt.batch.summary.end_tick != state['game']['tick']:
                                    raise ValueError('Native tick changed since the legacy snapshot')
                                if not game_process.alive(): raise ValueError('Native game exited during takeover')
                                boundary(work, report, 'save_pending')
                                checkpoint=await create_checkpoint(rt,rt.context_token)
                                report.update(checkpoint=checkpoint,game_pid=runtime['gamePid'])
                                boundary(work, report, 'saved')
                                if not game_process.alive(): raise ValueError('Native game exited before stop')
                                boundary(work, report, 'game_stop_pending')
                                await stop_for_restart(rt,rt.context_token,checkpoint['manifest_path'])
                                boundary(work, report, 'game_stopped')
                                process.terminate()
                                boundary(work, report, 'controller_stopped')
                            finally: await rt.router.close()
                    finally: store.close()
            env=dict(os.environ,PYTHONPATH=str(source/'controller'),RIMBOT_MODEL=state['chatModel'])
            env.pop('RIMBOT_RESUME_CHECKPOINT',None)
            with (work/'restart.out.log').open('w') as out,(work/'restart.err.log').open('w') as err:
                subprocess.Popen([sys.executable,'-m','rimbot','--resume',checkpoint['manifest_path'],'--port',str(args.port)],
                    cwd=source,env=env,stdin=subprocess.DEVNULL,stdout=out,stderr=err,
                    creationflags=subprocess.CREATE_NO_WINDOW|subprocess.CREATE_NEW_PROCESS_GROUP)
            boundary(work, report, 'replacement_started')
            deadline=time.monotonic()+120
            while time.monotonic()<deadline:
                await asyncio.sleep(.5)
                try:
                    restored=(await client.get('/api/state')).json()
                except httpx.HTTPError: continue
                if restored.get('connected'):
                    new_health=(await client.get('/api/health')).json()
                    if new_health['pid']==health['pid'] or new_health['source_root']!=str(source):
                        raise ValueError('Replacement server identity mismatch')
                    validate_restored_state(restored,state,checkpoint)
                    report.update(outcome='PASS',new_session=restored['sessionId'],resumed_tick=restored['game']['tick'])
                    break
            else: raise ValueError('New controller did not become ready; checkpoint and logs retained in '+str(work))
            (work/'report.json').write_text(json.dumps(report,indent=2))
            print(json.dumps({k:v for k,v in report.items() if k!='state'},indent=2))
        except Exception as error:
            if 'work' in locals():
                report.update(outcome='INTERRUPTED',error=str(error))
                (work/'report.json').write_text(json.dumps(report,indent=2))
                print('Migration interrupted. Native progress and recovery artifacts are retained at '+str(work),file=sys.stderr)
                print('After ownership handoff the legacy connection cannot be restored automatically. '
                      'If its native game is still paused, retry with --recover-disconnected; '
                      'if the game has stopped, use the retained checkpoint with python -m rimbot --resume.',file=sys.stderr)
            raise
        finally:
            if game_process: game_process.close()
            process.close()


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port',type=int,default=8787)
    parser.add_argument('--root',type=Path,required=True,help='Existing owned bridge root')
    parser.add_argument('--database',type=Path,required=True,help='Existing legacy bridge.sqlite')
    parser.add_argument('--source',type=Path,required=True,help='Verified checkout serving the legacy port and receiving the upgraded session')
    parser.add_argument('--recover-disconnected',action='store_true',help='Retry an interrupted handoff whose legacy server is already Manual and paused; native identity, tick and persisted state must still match')
    asyncio.run(migrate(parser.parse_args()))
