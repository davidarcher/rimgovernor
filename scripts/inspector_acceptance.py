"""Read-only B12 contract acceptance on a separately owned baseline game."""
from rimgovernor.bridge import gabs_executable
import argparse
import asyncio
import json
import subprocess
from pathlib import Path

from rimgovernor.bridge import bridge_session
from rimgovernor.bridge_game import BridgeGame, for_model
from rimgovernor.headless import isolated_root, prepare


async def run(args):
    root=isolated_root(args.source,args.output)
    report={'reads':{},'schemas':{},'outcome':'failed',
            'revision':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),
            'working_tree_dirty':bool(subprocess.check_output(['git','status','--porcelain'],text=True).strip())}
    try:
        async with bridge_session(gabs_executable(root),prepare(root)) as bridge:
            try:
                await bridge.core('games_start',gameId=bridge.game_id)
                await bridge.connect()
                await bridge.call('rimworld/load_game_ready',saveName='RimGovernor-tribal8-baseline',
                                  readiness='visual',timeoutMs=90000,ignoreModCompatibility=True)
                await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
                if args.fixture:
                    fixture=(await bridge.call('test/inspector_fixture')).structuredContent
                    report['fixture']=fixture
                    assert fixture.get('success') is True,fixture
                    # Let RimWorld register newly spawned power components.
                    await bridge.call('rimworld/set_time_speed',speed='Normal',ultraSpeedBoost=False)
                    await asyncio.sleep(1)
                    await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
                game=BridgeGame(bridge)
                async def read(label,tool,arguments):
                    report['schemas'][tool]=await game.describe(tool)
                    value=await game.invoke(tool,arguments)
                    report['reads'][label]={'tool':tool,'arguments':arguments,'native':value,
                                            'model':for_model(value,tool)}
                    assert value.get('success') is True,(label,value)
                    compact=report['reads'][label]['model']
                    assert not compact.get('requires_narrower_query'),(label,compact)
                    for key in ('notes','filters','skipped','pawnsFiltered','totalCount','truncated'):
                        if key in value:assert compact[key]==value[key],(label,key)
                    return value
                pawns=await read('pawns','home/list_pawns',{'colonistsOnly':True})
                assert len(pawns['pawns'])==8,pawns
                for block in ('health','work','schedule','relations'):
                    result=await read('pawn_'+block,'home/list_pawns',
                                      {'colonistsOnly':True,'nameFilter':pawns['pawns'][0]['name'],block:True})
                    assert result['pawns'] and all(block in pawn for pawn in result['pawns']),result
                    assert 'filters' in result and 'pawnsFiltered' in result,result
                buildings=await read('buildings','home/list_buildings',
                                     {'aggregate':False,'maxDetailed':2,'billIngredients':True,'inspect':True})
                assert all('thingId' in b and 'rotation' in b for b in buildings['buildings']),buildings
                assert 'powerNets' in buildings and 'powerSummary' in buildings,buildings
                await read('bills','home/bills',{'action':'list','dryRun':True})
                zones=await read('zones','home/list_zones',{'filter':True,'includeCells':True,'maxCellsPerZone':1})
                assert 'anomalies' in zones and 'filters' in zones,zones
                await read('alerts','rimworld/list_alerts',{})
                if args.fixture:
                    for name in ('Cooler','Vent','Battery','TableButcher'):
                        listing=await read('fixture_'+name,'home/list_buildings',
                            {'match':name,'billIngredients':True})
                        expected=[b for b in fixture['buildings'] if b['defName']==name]
                        rows={b['thingId']:b for b in listing['buildings']}
                        if name in ('Cooler','Vent'):
                            for building in expected:
                                thermal=rows[building['thingId']]['thermalSides']
                                assert thermal['readable'] is True,thermal
                                sides={s['side']:s for s in thermal['sides']}
                                dx,dz=[(0,1),(1,0),(0,-1),(-1,0)][building['rotation']]
                                for label,sign in ([('exhaust',1),('intake',-1)] if name=='Cooler'
                                                   else [('front',1),('back',-1)]):
                                    side=sides[label]
                                    assert side['position']=={'x':building['position']['x']+sign*dx,
                                                              'z':building['position']['z']+sign*dz},side
                                    assert side['fogged'] is False and side['impassable'] is False,side
                        if name=='TableButcher':
                            bill=rows[expected[0]['thingId']]['bills'][0]
                            assert bill['ingredients'] and bill['blockedBy'] and bill['canRunNow'] is False,bill
                        if name=='Battery':
                            assert any('isolatedBattery' in n['flags'] for n in listing['powerNets']),listing
                    zone=next(z for z in zones['zones'] if z['id']==fixture['zoneId'])
                    assert zone['listedCellCount']==9 and zone['cellsNotListed']==8,zone
                    assert zone['gridCellCount']==9 and zone['gridCellsNotListed']==8,zone
                    assert zone['consistent'] is True,zone
                    conflict=next(z for z in zones['zones'] if z['id']==fixture['conflictingZoneId'])
                    assert conflict['consistent'] is False and conflict['phantomCellCount']==1,conflict
                    assert zones['anomalies'] and 'filter' in zone,zones
                    probe=fixture['boundaryProbe']
                    assert probe['readable'] is True,probe
                    assert any(s['inBounds'] is False for s in probe['sides']),probe
                    assert all(s['impassable'] is None for s in probe['sides']
                               if not s['inBounds'] or s['fogged']),probe
                report['outcome']='passed'
            finally:
                await bridge.core('games_kill',gameId=bridge.game_id)
    except Exception as error:
        report['error']=repr(error)
        raise
    finally:
        (root/'inspector-result.json').write_text(json.dumps(report,indent=2),encoding='utf8')


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',type=Path,default=Path('.rimgovernor/bridge'))
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--fixture',action='store_true',help='Requires a temporary InspectorFixture build; creates disposable test objects.')
    asyncio.run(run(parser.parse_args()))
