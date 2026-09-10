"""Disposable native smoke: save identity, an instant zone, draft cleanup and ticks.
Run with the dashboard controller and RimWorld closed. Never invokes a model.
"""
from rimbot.bridge import gabs_executable
import asyncio
import json
from pathlib import Path
from rimbot.bridge import bridge_session
from rimbot.bridge_game import BridgeGame
from rimbot.projects import ProjectBook

async def main():
    root=Path('.rimbot/bridge').resolve()
    evidence={}
    async with bridge_session(gabs_executable(root),root/'config') as bridge:
        await bridge.core('games_start',gameId=bridge.game_id)
        await bridge.connect()
        await bridge.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',readiness='visual',timeoutMs=90000)
        await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
        game=BridgeGame(bridge)
        first=await game.query('home/colony_identity')
        await bridge.call('rimworld/save_game',saveName='RimBot-native-identity-check')
        await bridge.call('rimworld/load_game_ready',saveName='RimBot-native-identity-check',readiness='visual',timeoutMs=90000)
        second=await game.query('home/colony_identity')
        assert first['colonyId']==second['colonyId'] and first['loadToken']!=second['loadToken'],(first,second)
        evidence['saved_identity']={'same_colony':True,'new_load_token':True}
        status=await game.query('home/status')
        pawn=status['colonists'][0];cell=pawn['position'];identity=pawn['thingId']
        created=False
        try:
            await game.invoke('home/zone_cells',{'op':'create','zoneType':'stockpile','label':'Migration probe','x':cell['x'],'z':cell['z'],'width':1,'height':1,'dryRun':False},allow_write=True)
            created=True
            zones=await game.query('home/list_zones')
            zone=next(z for z in zones['zones'] if z['label']=='Migration probe')
            book=ProjectBook();project=book.upsert({'title':'Probe','targets':[{'kind':'zone','zone_id':str(zone['id'])}]})
            await book.reconcile(game)
            assert project.state=='complete',project
            evidence['instant_zone']=project.model_dump()
        finally:
            if created:await game.invoke('home/zone_cells',{'op':'delete','zone':'Migration probe','dryRun':False},allow_write=True)
        drafted=False
        try:
            await game.invoke('home/order',{'action':'draft','pawn':identity,'dryRun':False},allow_write=True);drafted=True
            status=await game.query('home/status')
            assert next(p for p in status['colonists'] if p['thingId']==identity)['drafted']
        finally:
            if drafted:await game.invoke('home/order',{'action':'undraft','pawn':identity,'dryRun':False},allow_write=True)
        status=await game.query('home/status');assert not next(p for p in status['colonists'] if p['thingId']==identity)['drafted']
        evidence['draft_cleanup']='native draft and undraft observed'
        before=status['time']['ticksGame']
        try:
            await game.invoke('rimworld/set_time_speed',{'speed':'Normal','ultraSpeedBoost':False},allow_write=True)
            await asyncio.sleep(1)
        finally:await game.invoke('rimworld/set_time_speed',{'speed':'Paused','ultraSpeedBoost':False},allow_write=True)
        status=await game.query('home/status')
        assert status['time']['paused'] and status['time']['ticksGame']>before
        evidence['clock_ticks']=status['time']['ticksGame']-before
        evidence['traders']=await game.invoke('home/trade',{'action':'list_traders'})
        (root/'migration-smoke.json').write_text(json.dumps(evidence,indent=2),encoding='utf8')
        print('PASS: save identity; zone observed immediately; draft/undraft; time advanced and paused; trader query',flush=True)

if __name__=='__main__':asyncio.run(main())
