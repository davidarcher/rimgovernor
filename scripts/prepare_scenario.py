"""Generate an ordinary scenario-editor start with the test-only setup fixture."""
from rimbot.bridge import gabs_executable
import argparse
import asyncio
import hashlib
import json
import shutil
import xml.etree.ElementTree as ET
from pathlib import Path
from rimbot.bridge import BridgeError,bridge_session
from rimbot.headless import isolated_root,prepare


async def run(args):
    root=isolated_root(args.source_root,args.output)
    config=prepare(root)
    report={'outcome':'failed','save_edits':[], 'settings':{
        'scenario':args.scenario,'count':args.count,'seed':args.seed}}
    installation=Path(json.loads((config/'config.json').read_text())['games']['rimbot-trial']['workingDir'])
    dll=installation/'Mods/RimBotObservations/BridgeTools/Observations/RimBot.Observations.BridgeTools.dll'
    report['fixture_dll_sha256']=hashlib.sha256(dll.read_bytes()).hexdigest()
    async with bridge_session(gabs_executable(root),config) as bridge:
        try:
            await bridge.core('games_start',gameId=bridge.game_id)
            await bridge.connect()
            report['contract']=(await bridge.detail('test/configure_start')).structuredContent
            report['definitions_before']=(await bridge.call('test/list_start_scenarios')).structuredContent
            report['armed']=(await bridge.call('test/configure_start',**report['settings'])).structuredContent
            report['start']=(await bridge.call('rimworld/start_debug_game_ready',readiness='visual',
                pauseIfNeeded=True,timeoutMs=120000)).structuredContent
            await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
            for _ in range(30):
                try:
                    report['facts']=(await bridge.call('home/colony_facts',planning=True)).structuredContent
                    break
                except BridgeError as error:
                    if 'No living colonists' not in str(error):raise
                    await bridge.call('rimworld/set_time_speed',speed='Normal',ultraSpeedBoost=False)
                    await asyncio.sleep(.25)
                    await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
            assert report['facts']['colonists']==args.count,report.get('facts')
            assert report['facts']['tick']<=600,report['facts']['tick']
            report['roster']=(await bridge.call('home/list_pawns',colonistsOnly=True,work=True)).structuredContent
            report['definitions_after']=(await bridge.call('test/list_start_scenarios')).structuredContent
            assert report['definitions_before']['scenarios']==report['definitions_after']['scenarios'], 'Global scenario definitions were changed'
            report['save']=(await bridge.call('rimworld/save_game',saveName='RimBot-scenario-start')).structuredContent
            assert report['save']['exists'] is True
            source=Path(report['save']['path']).resolve()
            assert source.is_relative_to(root)
            saved=ET.parse(source).getroot()
            report['saved_pawn_counts']=[n.text for n in saved.findall('.//scenario//pawnCount')]
            assert str(args.count) in report['saved_pawn_counts'],report['saved_pawn_counts']
            report['saved_pawn_choice_counts']=[n.text for n in saved.findall('.//scenario//pawnChoiceCount')]
            assert any(args.count<=int(n)<=10 for n in report['saved_pawn_choice_counts'])
            target=root/'profile/Saves/RimBot-tribal8-baseline.rws'
            shutil.copy2(source,target)
            report['sha256']=hashlib.sha256(source.read_bytes()).hexdigest()
            assert report['sha256']==hashlib.sha256(target.read_bytes()).hexdigest()
            # A deliberately refused operation raises bridge attention. Run it
            # after saving so no later game command needs that hold acknowledged.
            try:
                await bridge.call('test/configure_start',**report['settings'])
            except BridgeError as error:
                assert 'Only a fresh main-menu process can configure a start' in str(error),str(error)
                report['live_reconfiguration_refused']=str(error)
            else:
                raise AssertionError('Setup accepted an existing colony')
            report['baseline_name']='Legacy harness filename; use the recorded scenario, seed and native pawn count.'
            report['outcome']='prepared'
        except Exception as error:
            report['error']=str(error)
            raise
        finally:
            (root/'scenario-preparation.json').write_text(json.dumps(report,indent=2),encoding='utf8')
            await bridge.core('games_stop',gameId=bridge.game_id)
    print(json.dumps({'outcome':report['outcome'],'count':report['facts']['colonists'],
        'tick':report['facts']['tick'],'sha256':report['sha256']}),flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--scenario',required=True,help='Native ScenarioDef name')
    parser.add_argument('--count',type=int,choices=range(1,11),required=True)
    parser.add_argument('--seed',required=True)
    asyncio.run(run(parser.parse_args()))
