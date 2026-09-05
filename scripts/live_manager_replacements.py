"""Disposable-game regression: player replacements invalidate an AI construction batch.

Requires --execute; leaves fixture sleeping spots. Save/reload the user's colony
around this test. Uses a real Infrastructure model and Administrator by default.
"""
import argparse
import asyncio
import json
import time
from pathlib import Path

import httpx
from rimbot.config import Settings
from rimbot.runtime import Runtime
from rimbot.store import Store
from rimbot.contracts import Proposal
from rimbot.native_models import ConstructionRequest, Placement, Cell


async def run():
    folder=Path('.rimbot/replacement-tests')/time.strftime('%Y%m%d-%H%M%S')
    folder.mkdir(parents=True)
    settings=Settings.model_validate(json.loads(Path('.rimbot/manager-update/player-state.json').read_text())['settings']) if Path('.rimbot/manager-update/player-state.json').exists() else Settings()
    rt=Runtime(Store(folder/'trace.sqlite'),settings)
    report={'passed':False};started=time.monotonic()
    try:
        await rt.poll();rt.cycle_generation=rt.generation;rt.mode='automate'
        mid=rt.observation['map']['id']
        context=await rt.manager_context({'player_direction':'Provide one single colonist sleeping spot per colonist near colony_focus. Use free instant sleeping spots. No beds or other facilities in this test. Respect existing player sleeping spots; once enough exist, submit no additional construction. Do not request Workforce for instant placements.'})
        focus=context['colony_focus'];assert focus is not None
        before=await rt.api.call('construction_state',{'map_id':mid})
        assert not before.buildings,'Start this fixture on a fresh quicktest without player construction.'
        rooms=await rt.api.call('construction_rooms',{'map_id':mid,'near':focus,'cell_limit':16,'offset':0,'limit':4})
        assert rooms.items and rooms.items[0].visible_cell
        sample=rooms.items[0].visible_cell
        assert (sample.x-focus['x'])**2+(sample.z-focus['z'])**2<100,'Room sample drifted to map edge.'
        proposals,decision=await asyncio.wait_for(rt.coordinate(context,['Infrastructure']),180)
        actions=[action for role in decision.accepted for action in Proposal.model_validate(proposals[role]).actions]
        assert actions and all(a.endpoint=='construction_place' for a in actions),proposals
        report['initial_proposals']=proposals;report['initial_decision']=decision.model_dump()
        for action in actions:
            assert action.observation_basis==before.revision
            for building in action.arguments['buildings']:
                assert building['def_name']=='SleepingSpot'
                p=building['position'];assert abs(p['x']-focus['x'])<=12 and abs(p['z']-focus['z'])<=12
        # Simulate the player's correction with distinct normal native placements,
        # after the model drafted its orders and before those drafts execute.
        planned={(b['position']['x'],b['position']['z']) for a in actions for b in a.arguments['buildings']}
        player=[];needed=rt.observation['game']['colonist_count']
        for dz in range(-5,6,3):
            for dx in range(-5,6,2):
                x,z=focus['x']+dx,focus['z']+dz
                if (x,z) in planned:continue
                candidate=Placement(def_name='SleepingSpot',stuff_def_name='',position=Cell(x=x,z=z),rotation=0)
                batch=ConstructionRequest(map_id=mid,buildings=player+[candidate])
                if (await rt.api.native.inspect(batch)).accepted:player.append(candidate)
                if len(player)==needed:break
            if len(player)==needed:break
        assert len(player)==needed
        placed=await rt.api.native.place(ConstructionRequest(map_id=mid,buildings=player))
        assert placed.accepted and all(i.state=='built' for i in placed.items)
        player_ids={i.thing_id for i in placed.items}
        await rt.api.request('POST','/api/v1/game/speed',params={'speed':1})
        for action in actions:await rt.execute(action,'Infrastructure')
        await rt.api.request('POST','/api/v1/game/speed',params={'speed':0})
        current=await rt.api.call('construction_state',{'map_id':mid})
        assert {b.thing_id for b in current.buildings}==player_ids,'Stale AI order added construction.'
        assert all(w['status']=='deferred' for w in rt.memory['work'])
        report['stale_orders_rejected']=True;report['player_spots']=placed.model_dump()
        proposals,decision=await asyncio.wait_for(rt.coordinate(context,['Infrastructure']),180)
        assert not any(p['actions'] for p in proposals.values()),proposals
        report['fresh_proposals']=proposals;report['fresh_decision']=decision.model_dump()
        assert len((await rt.api.call('construction_state',{'map_id':mid})).buildings)==needed
        report['passed']=True
    except BaseException as error:
        report['error']=str(error);raise
    finally:
        report.update(seconds=round(time.monotonic()-started,2),counters=rt.counters)
        (folder/'report.json').write_text(json.dumps(report,indent=2))
        print(f'Passed: {report["passed"]}. Evidence: {folder}/report.json',flush=True)
        await rt.stop();rt.store.close()


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--execute',action='store_true',required=True);parser.parse_args()
    asyncio.run(run())
