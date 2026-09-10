"""Private ordinary raid entry followed by deterministic defense and stand-down.

The separate test assembly invokes an eligible native incident. All subsequent
orders and clock windows come from the shared controller; no damage/gear edits.
"""
import argparse
import asyncio
from pathlib import Path
from rimbot.bridge_observation import observe


async def run_raid(rt,evidence):
    preview=(await rt.bridge.call('test/combat_incident',dryRun=True)).structuredContent
    assert preview.get('eligible') is True and preview.get('applied') is False,preview
    setup=(await rt.bridge.call('test/combat_incident',dryRun=False)).structuredContent
    evidence['raid_setup']=dict(preview=preview,issued=setup)
    assert setup.get('applied') is True and len(setup.get('added',[]))==1,setup
    target=setup['added'][0]['id']
    assert setup['added'][0]['hostile'] is True,setup
    async def target_state():
        pawns=(await rt.game.query('home/list_pawns',includeDead=True,health=True,equipment=True,animals=True))['pawns']
        enemy=next((p for p in pawns if p['thingId']==target),None)
        if enemy is None:
            # The spawned-pawn census excludes bodies. Resolve follows the exact
            # corpse's InnerPawn identity; absence alone never certifies defeat.
            resolved=await rt.inspect_native('home/order',dict(action='resolve',target=target,dryRun=True))
            enemy=resolved.get('target')
            assert enemy and enemy.get('thingId')==target,resolved
        return enemy,pawns
    initial,roster=await target_state();evidence['enemy_before']=initial
    colonist_ids={p['thingId'] for p in roster if p.get('isColonist') is True}
    assert colonist_ids,'No observed colonists in the raid fixture'
    evidence['colonists_before']=[p for p in roster if p['thingId'] in colonist_ids]
    print('Ordinary hostile raid:',target,(initial.get('equipment') or {}).get('primaryLabel'),flush=True)
    evidence['raid_windows']=[];deadline=asyncio.get_running_loop().time()+600
    fought=False;cleared=False
    while asyncio.get_running_loop().time()<deadline:
        await rt.refresh_clock_events()
        assert rt.mode=='automate',('External direction retained',rt.mode,rt.supervisor.state)
        rt.batch=await observe(rt.game);rt.reconcile_plan()
        enemy,people=await target_state()
        current={p['thingId']:p for p in people}
        assert all(p in current and current[p].get('dead') is False and current[p].get('downed') is False
            for p in colonist_ids),'Colonist missing or incapacitated; bounded defense failed'
        if enemy and (enemy.get('dead') is True or enemy.get('downed') is True):
            cleared=True;evidence['enemy_after']=enemy
        await rt.controller.cycle()
        goal=rt.current_plan.colony_goals.get('ActiveCombat')
        evidence['raid_windows'].append(dict(tick=rt.batch.summary.end_tick,enemy=enemy,
            goal=goal.model_dump() if goal else None,control=rt.current_plan.control.get('combat')))
        print('Raid review:',rt.batch.summary.end_tick,'defeated:',cleared,'goal:',goal.status if goal else None,flush=True)
        if goal and goal.status=='blocked':raise AssertionError(goal.reason)
        # Continue ordinary post-combat treatment until its owned doctor draft
        # is also released; combat stand-down can finish before that labor.
        rt.resume_after_review=True
        for _ in range(16):
            await rt.advance_execution()
            if (rt.wake.is_set() or rt.supervisor.state.get('active')
                    or rt.mode!='automate' or not rt.current_plan.ready()):
                break
        evidence['raid_windows'][-1]['progress']={k:v.model_dump() for k,v in rt.current_plan.progress.items()}
        evidence['raid_windows'][-1]['phase']=rt.phase
        fighting=rt.current_plan.control.get('combat',{})
        fought=fought or bool(fighting.get('steps') and all(
            rt.current_plan.progress[s].state=='complete' for s in fighting['steps'])
            and any(s.action.kind=='native_operation' and s.action.arguments.get('action')=='attack'
                for s in rt.current_plan.spec.steps if s.id in fighting['steps']))
        if cleared:
            cleanup=[s for s in rt.current_plan.spec.steps if s.action.kind=='stand_down' and s.source=='AUTOPILOT']
            if cleanup and not rt.draft_owners and all(rt.current_plan.progress[s.id].state=='complete' for s in cleanup):
                evidence['strategy_stand_down']=[dict(step=s.model_dump(),progress=rt.current_plan.progress[s.id].model_dump()) for s in cleanup]
                break
        if rt.wake.is_set() and not rt.supervisor.state.get('active'):
            continue  # A finished method needs a controller review before its clock window.
        async with asyncio.timeout(60):
            while rt.supervisor.state.get('active'):
                events=await rt.supervisor.poll()
                if events:
                    rt.clock_events.extend(events);rt.receive_clock_events()
                await asyncio.sleep(.2)
        if not cleared and not rt.supervisor.state.get('active'):
            stop=rt.supervisor.state.get('stopReason')
            assert stop in ('tick_budget','requested_pause','hostile','colonist_injury','colonist_health'),rt.supervisor.state
    await rt.control_clock('Paused')
    assert cleared and fought,'No ordinary autonomous raid victory observed'
    assert evidence.get('strategy_stand_down') and not rt.draft_owners,'No strategy-selected owned cleanup'
    assert rt.counters['model_calls']==0,'Routine defense unexpectedly invoked inference'
    evidence['colonists_after']=[current[p] for p in sorted(colonist_ids)]
    print('PASS: native hostile defeat, deterministic combat and strategy-selected stand-down',flush=True)


if __name__=='__main__':
    from native_combat_smoke import main
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--prepared-root',type=Path,required=True)
    args=parser.parse_args()
    asyncio.run(main(root=args.prepared_root,raid=True))
