"""Rendered Docker visual evaluation; ordinary blueprint fixtures and local inference only.

Run as the command of rimbot.container_worker so all game/profile files are private.
Scores describe this small paired layout sample, not general visual reliability.
"""
import argparse
import asyncio
import json
import os
import time
from pathlib import Path

from pydantic import BaseModel
from rimbot.bridge import bridge_session, gabs_executable, BridgeError
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.config import Settings, ModelRole, ModelRouting
from rimbot.consultation import structured_tool
from rimbot.review_evidence import ReviewEvidence
from rimbot.store import Store
from rimbot.visual_review import review
from rimbot.visual_source import read_source


class Decision(BaseModel):
    door_present: bool | None
    reason: str


async def decide(rt, evidence):
    result, _ = await rt.router.complete(ModelRole.STRATEGIST, [
        {'role':'system','content':'Evaluate evidence only. No game orders. Call the report tool exactly once, with door_present and reason. Report whether the proposed shell has a door; use null if evidence is insufficient. A blueprint door counts as a proposed door, not a completed building. Native buildDefName identifies the planned building. Do not return plain text.'},
        {'role':'user','content':json.dumps(evidence)}],
        [structured_tool('report','Report the evidence-supported decision.',Decision.model_json_schema())],rt.model_progress)
    calls = result.get('tool_calls', [])
    assert len(calls) == 1 and calls[0]['function']['name'] == 'report', result
    return Decision.model_validate_json(calls[0]['function']['arguments']).model_dump()


async def run(args):
    root = Path(os.environ['RIMBOT_BRIDGE_ROOT'])
    output = Path(args.output); output.mkdir(parents=True, exist_ok=False)
    settings = Settings(model=args.model, model_url=os.environ['RIMBOT_MODEL_URL'], reasoning=False,
                        temperature=0, max_output_tokens=2048, timeout_seconds=300)
    routing = ModelRouting(roles={ModelRole.STRATEGIST:settings,ModelRole.ARCHITECT:settings})
    store = Store(output/'state.sqlite')
    rt = BridgeRuntime(store, root, routing=routing)
    complete = rt.router.complete
    serial = 0
    async def recorded_complete(role, messages, tools, progress):
        nonlocal serial
        serial += 1
        value, usage = await complete(role, messages, tools, progress)
        (output/f'model-{serial:02}.json').write_text(json.dumps(dict(role=str(role),response=value,usage=usage),indent=2))
        return value, usage
    rt.router.complete = recorded_complete
    report = {'passed':False,'model':settings.model_dump(),'cases':[],
              'scope':'Two paired proposed layouts; native blueprint reads, camera ownership, visual advice and exact evidence recall. No completed pawn-work claim.'}
    started = time.monotonic()
    def save():
        (output/'result.json').write_text(json.dumps(report,indent=2),encoding='utf8')
    try:
        async with bridge_session(gabs_executable(root),root/'config') as bridge:
            rt.bridge=bridge; rt.game=BridgeGame(bridge)
            await bridge.core('games_start',gameId=bridge.game_id)
            await bridge.connect()
            try:
                for has_door in (False, True):
                    case={'expected_door':has_door}; report['cases'].append(case); save()
                    await bridge.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',readiness='visual',ignoreModCompatibility=True,timeoutMs=90000)
                    await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
                    await rt.sync_identity()
                    status=await rt.game.query('home/status',colonists=False,threats=False)
                    tick=status['time']['ticksGame']
                    pawn=(await rt.game.query('home/list_pawns',colonistsOnly=True))['pawns'][0]['position']
                    chosen=None
                    for dx,dz in ((15,10),(10,0),(-12,0),(0,10),(0,-12),(20,0),(-20,0)):
                        cells=[dict(defName='Door' if has_door and x==3 and z==0 else 'Wall',stuff='WoodLog',rotation='north',x=pawn['x']+dx+x,z=pawn['z']+dz+z)
                               for z in range(7) for x in range(7) if x in (0,6) or z in (0,6)]
                        try:
                            site=await rt.game.query('home/get_cells_plus',x=pawn['x']+dx-1,z=pawn['z']+dz-1,
                                width=9,height=9,fields='fogged,walkable,passable',sparse=False)
                            assert site.get('cellCount')==81 and site.get('cellsOmitted')==0
                            assert {'fogged','walkable','passable'} <= set(site.get('fieldsApplied',[]))
                            assert all(c.get('fogged',False) is False and c.get('walkable') is True and c.get('passable') is True for c in site['cells'])
                            for cell in cells:
                                preview=await rt.game.invoke('home/place_building',dict(cell,dryRun=True))
                                assert preview.get('rotations') and all(r.get('accepted') is True for r in preview['rotations'])
                        except (BridgeError,AssertionError): continue
                        chosen=cells; break
                    assert chosen,'No legal fixture footprint'
                    case['visible_site']=site
                    for cell in chosen:
                        result=await rt.game.invoke('home/place_building',dict(cell,dryRun=False),allow_write=True)
                        assert result.get('success') is True,result
                    # Independent native readback establishes the proposed layout.
                    buildings=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
                    positions={(c['x'],c['z']) for c in chosen}
                    rows=[b for b in buildings['buildings'] if (b['position']['x'],b['position']['z']) in positions]
                    assert len(rows)==24,rows
                    observed={(b['position']['x'],b['position']['z']):b for b in rows}
                    assert all(observed[(c['x'],c['z'])]['isBlueprint'] and
                               observed[(c['x'],c['z'])]['buildDefName']==c['defName'] for c in chosen),rows
                    case['fixture']=chosen; case['native_buildings']=rows
                    await bridge.call('rimworld/jump_camera_to_cell',x=chosen[0]['x']+3,z=chosen[0]['z']+3)
                    await bridge.call('rimworld/set_camera_zoom',rootSize=12)
                    await asyncio.sleep(1)
                    before=(await bridge.call('rimworld/get_camera_state')).structuredContent
                    image,source=await rt._capture_visual_source()
                    data=read_source(root,source)
                    (output/('door.png' if has_door else 'doorless.png')).write_bytes(data)
                    question='Review the visible proposed building shell for practical layout and entrance problems. Distinguish proposed blueprints from completed buildings.'
                    wide=await review(rt.router,question,image,source,rt.model_progress)
                    case['wide']=wide;save()
                    # A native player camera edit after capture must survive adviser inference.
                    original_complete=rt.router.complete
                    async def player_camera_during_inference(*values):
                        await bridge.call('rimworld/move_camera',deltaX=3,deltaZ=0)
                        case['player_camera']=(await bridge.call('rimworld/get_camera_state')).structuredContent
                        return await original_complete(*values)
                    rt.router.complete=player_camera_during_inference
                    try:
                        paired=await rt.visual_review(question,expected_token=rt.context_token,expected_revision=rt.chat_revision)
                    finally:
                        rt.router.complete=original_complete
                    case['paired']=paired;save()
                    after=(await bridge.call('rimworld/get_camera_state')).structuredContent
                    case['camera_before']=before;case['camera_after']=after
                    assert all(case['player_camera'][k]==after[k] for k in ('mapId','mapPosition','rootSize'))
                    assert before['mapPosition'] != after['mapPosition']
                    assert paired['source']['camera']['mapPosition']==before['mapPosition']
                    # Advice-only decisions measure whether the visual report conveys the actual distinction.
                    case['wide_decision']=await decide(rt,wide['report']);save()
                    case['paired_decision']=await decide(rt,paired['report']);save()
                    # Simulate lost tool text after compaction; the exact native result is recovered through the production index.
                    evidence=ReviewEvidence()
                    identity=evidence.add('native_home__list_buildings',{'fixture':'proposed shell'},rows)
                    index=evidence.index('list_buildings')
                    case['without_recall']=await decide(rt,{'earlier_native_result':'Compacted out of conversation','index':index});save()
                    exact=evidence.read(identity)
                    case['with_recall']=await decide(rt,exact);save()
                    assert exact['result']==rows
                    case['evidence_index']=index;case['recalled']=exact
                    current=await rt.game.query('home/status',colonists=False,threats=False)
                    assert current['time']['paused'] and current['time']['ticksGame']==tick
                    assert rt.current_plan.revision==0 and not rt.current_plan.spec.steps
                    case['unchanged_tick']=tick;case['no_model_orders']=True
                    print('Completed layout',has_door,flush=True);save()
                report['scores']={arm:sum(c[arm]['door_present'] is c['expected_door'] for c in report['cases'])/len(report['cases'])
                                  for arm in ('wide_decision','paired_decision','without_recall','with_recall')}
                report['advice_delta']=report['scores']['paired_decision']-report['scores']['wide_decision']
                report['recall_delta']=report['scores']['with_recall']-report['scores']['without_recall']
                report['abstentions']={arm:sum(c[arm]['door_present'] is None for c in report['cases'])
                                      for arm in report['scores']}
                report['score_note']='Exact door classification rate; null is an unresolved safe abstention, not a false claim. The two-case sample does not establish general reliability.'
                report['passed']=True
                report['requires_visual_inspection']=True
            finally:
                report['game_cleanup']=(await bridge.core('games_stop',gameId=bridge.game_id)).model_dump(mode='json')
    except Exception as error:
        report['error']=repr(error)
        raise
    finally:
        report['elapsed_seconds']=time.monotonic()-started
        report['metrics']=rt.router.metrics
        await rt.router.close();store.close();save()


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',default='/worker/evaluation')
    parser.add_argument('--model',default=os.environ.get('RIMBOT_MODEL','qwen3.5-4b'))
    asyncio.run(run(parser.parse_args()))
