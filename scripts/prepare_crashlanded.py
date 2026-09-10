"""Prepare an ordinary native Crashlanded start without save/game-state edits."""
from rimgovernor.bridge import gabs_executable
import argparse,asyncio,json,shutil
from pathlib import Path
from rimgovernor.bridge import bridge_session
from rimgovernor.headless import isolated_root,prepare
async def main(args):
    root=isolated_root(args.source_root,args.output)
    config=prepare(root);report={}
    async with bridge_session(gabs_executable(root),config) as bridge:
        try:
            await bridge.core('games_start',gameId=bridge.game_id)
            await bridge.connect()
            names=(await bridge.names(query='debug')).structuredContent
            report['discovery']=names
            assert 'rimworld/start_debug_game_ready' in json.dumps(names), names
            report['start']=(await bridge.call('rimworld/start_debug_game_ready',readiness='visual',pauseIfNeeded=True,timeoutMs=120000)).structuredContent
            await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
            from rimgovernor.bridge import BridgeError
            for arrival in range(30):
                try:
                    report['facts']=(await bridge.call('home/colony_facts',planning=False)).structuredContent
                    break
                except BridgeError as error:
                    if 'No living colonists' not in str(error): raise
                    await bridge.call('rimworld/set_time_speed',speed='Normal',ultraSpeedBoost=False)
                    await asyncio.sleep(.25)
                    await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
            assert report['facts']['colonists']==3, report['facts']
            assert report['facts']['tick']<=600, report['facts']['tick']
            report['save_contract']=(await bridge.detail('rimworld/save_game')).structuredContent
            assert 'saveName' in json.dumps(report['save_contract'])
            report['save']=(await bridge.call('rimworld/save_game',saveName='RimGovernor-crashlanded-start')).structuredContent
            assert report['save']['exists'] is True, report['save']
            source=Path(report['save']['path']).resolve()
            assert source.is_relative_to(root),source
            shutil.copy2(source,root/'profile/Saves/RimGovernor-tribal8-baseline.rws')
            report['save_edits']=[]
            report['scenario']='Native Crashlanded quick-start: Cassandra/Rough, ordinary world and pawn generation'
            report['baseline_name']='RimGovernor-tribal8-baseline.rws is the legacy harness filename; this save contains three Crashlanded colonists.'
            report['outcome']='prepared'
        except Exception as error:
            report['error']=str(error);raise
        finally:
            (root/'crashlanded-preparation.json').write_text(json.dumps(report,indent=2),encoding='utf8')
            await bridge.core('games_stop',gameId=bridge.game_id)
    print(json.dumps({'outcome':report['outcome'],'colonists':report['facts']['colonists'],'tick':report['facts']['tick']}),flush=True)
if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True,help='Existing prepared bridge root containing GABS and a source profile')
    parser.add_argument('--output',type=Path,required=True,help='Fresh isolated bridge root')
    asyncio.run(main(parser.parse_args()))
