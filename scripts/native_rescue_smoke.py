"""Private native carry-to-bed acceptance with ordinary unarmed fixture combat.

No health/save edits. Death, unrelated danger or missing transit/delivery fail.
Fixture injury acknowledgements are explicit and never enable production recovery.
"""
import argparse
import asyncio
from pathlib import Path
from rimbot.bridge import BridgeError
from rimbot.bridge_observation import observe
from rimbot.player_commands import apply_command


async def run_rescue(rt, evidence, chat=False):
    async def roster():
        return (await rt.game.query('home/list_pawns',colonistsOnly=True,
            includeDead=True,health=True,equipment=True,bio=True))['pawns']
    people=await roster()
    healthy=[p for p in people if not any(p.get(k) for k in ('dead','downed','drafted','mentalState'))]
    attackers=[p for p in healthy if (p.get('equipment') or {}).get('armed') is False
        and (p.get('bio') or {}).get('incapableOfRead') is True
        and 'Violent' not in p['bio'].get('incapableOfTags',[])]
    assert attackers and len(healthy)>=3,'Need an ordinary unarmed actor and two healthy colonists'
    patients=[p for p in healthy if any(t.get('defName')=='Wimp' and t.get('suppressed') is False
        for t in (p.get('bio') or {}).get('traits',[]))]
    assert patients,'Fixture requires an observed native Wimp trait to down a patient without severe injuries'
    patient=patients[0]
    actor=next(p for p in attackers if p['thingId']!=patient['thingId'])
    rescuer=next(p for p in healthy if p['thingId'] not in (actor['thingId'],patient['thingId']))
    evidence['rescue_before']=dict(actor=actor,patient=patient,rescuer=rescuer)
    print('Fixture:',actor['name'],'uses ordinary unarmed combat to down',patient['name'],flush=True)
    origin=patient['position'];bed=None
    for dx,dz in ((12,0),(-12,0),(0,12),(0,-12),(16,4),(-16,-4)):
        args=dict(defName='SleepingSpot',x=origin['x']+dx,z=origin['z']+dz,rotation='north',dryRun=True)
        try: preview=await rt.game.invoke('home/place_building',args,allow_write=False)
        except BridgeError: continue
        if preview.get('canPlace') is not True:continue
        evidence['bed_order']=await rt.native('home/place_building',dict(args,dryRun=False))
        buildings=(await rt.game.query('home/list_buildings',match='SleepingSpot',aggregate=False,playerOnly=True))['buildings']
        bed=next((b for b in buildings if b['position']['x']==args['x'] and b['position']['z']==args['z']
            and not b.get('isBlueprint') and not b.get('isFrame')),None)
        if bed:break
    assert bed,'No ordinary completed sleeping spot for rescue'
    evidence['bed']=bed
    await rt.native('home/order',dict(action='draft',pawn=patient['thingId'],dryRun=False,watch=False))
    await rt.native('home/order',dict(action='attack',pawn=actor['thingId'],target=patient['thingId'],
        mode='melee',dryRun=False,watch=False))
    deadline=asyncio.get_running_loop().time()+180
    evidence['fixture_stops']=[]
    await rt.control_clock('Superfast',mode='combat')
    while asyncio.get_running_loop().time()<deadline:
        events=await rt.supervisor.poll();people=await roster()
        patient=next(p for p in people if p['thingId']==patient['thingId'])
        assert not patient['dead'],'Ordinary fixture combat killed the patient'
        if patient['downed']:break
        state=rt.supervisor.state
        if not state.get('active'):
            evidence['fixture_stops'].append(dict(state=state,events=events,patient=patient))
            allowed=(state.get('stopReason') in ('colonist_injury','colonist_health')
                and state.get('stopDetail','').startswith(patient['name']+' ')
                and sum(p['name']==patient['name'] for p in people)==1
                and all(not p.get('dead') and not p.get('downed') for p in people if p['thingId']!=patient['thingId']))
            if state.get('stopReason') in ('letter_pause','external_pause') and 'Ancient danger' in state.get('stopDetail',''):
                allowed=not evidence.get('acknowledged_ancient_warning')
                evidence['acknowledged_ancient_warning']=state['stopDetail']
            assert allowed,('Unrelated fixture interruption',state)
            rt.supervisor.allow_resume();await rt.control_clock('Superfast',mode='combat')
        await asyncio.sleep(.1)
    await rt.control_clock('Paused')
    assert patient['downed'],'No living downed patient from ordinary combat within the bound'
    evidence['downed_patient']=patient
    print('Living downed patient observed:',patient['thingId'],flush=True)
    evidence['downed_attack_refusals']=[]
    for dry in (True,False):
        try:
            refusal=await rt.game.invoke('home/order',dict(action='attack',pawn=actor['thingId'],
                target=patient['thingId'],mode='melee',requireStandingTarget=True,dryRun=dry,watch=False),allow_write=not dry)
            assert refusal.get('success') is False,refusal
        except BridgeError as error:
            refusal=str(error)
            assert 'standing' in refusal.lower(),refusal
        evidence['downed_attack_refusals'].append(dict(dryRun=dry,result=refusal))
    cleanup=await rt.release_drafts()
    assert not cleanup['failed'],cleanup
    evidence['fixture_cleanup']=cleanup
    args=dict(action='rescue',pawn=rescuer['thingId'],target=patient['thingId'],dryRun=True,watch=False)
    preview=await rt.game.invoke('home/order',args,allow_write=False)
    assert preview.get('success') is True,preview
    evidence['rescue_preview']=preview
    rt.batch=await observe(rt.game)
    if chat:
        before={s.id for s in rt.current_plan.spec.steps}
        prompt='Inspect current health and rescue the most urgent downed colonist to bed. Choose an able rescuer. Do not order treatment or any other work.'
        await rt.steer(prompt)
        await rt.planner.play_bridge()
        evidence['triage_chat']=dict(prompt=prompt,messages=rt.chat,routing=rt.router.routing.model_dump(mode='json'))
        selected=[s for s in rt.current_plan.spec.steps if s.id not in before]
        assert len(selected)==1 and selected[0].action.kind=='native_operation',selected
        step=selected[0]
        assert step.action.completion=='patient_in_bed' and step.action.arguments['target']==patient['thingId']
        rescuer=next(p for p in people if p['thingId']==step.action.arguments['pawn'])
        identity=step.id
    else:
        result=await apply_command(rt,dict(kind='RescuePawn',pawn=rescuer['thingId'],patient=patient['thingId']),
            token=rt.context_token,revision=rt.chat_revision)
        identity=result['step']
    rt.handled_revision=rt.chat_revision;await rt.hands.advance(rt)
    progress=rt.current_plan.progress[identity]
    assert progress.state=='waiting',progress
    evidence['rescue_issued']=progress.model_dump()
    rt.supervisor.allow_resume()
    await rt.control_clock('Normal',mode='combat',ignored_downed=patient['thingId'])
    evidence['transit']=[];delivered=None
    deadline=asyncio.get_running_loop().time()+120
    while asyncio.get_running_loop().time()<deadline:
        events=await rt.supervisor.poll();people=await roster()
        carrier=next((p for p in people if p['thingId']==rescuer['thingId']),None)
        current=next((p for p in people if p['thingId']==patient['thingId']),None)
        if carrier and carrier.get('carriedThingId')==patient['thingId']:evidence['transit'].append(carrier)
        if current and current.get('dead') is False and (current.get('health') or {}).get('inBed') is True:
            delivered=current;break
        if not rt.supervisor.state.get('active'):
            evidence['delivery_stop']=dict(state=rt.supervisor.state,events=events,people=people);break
        await asyncio.sleep(.1)
    await rt.control_clock('Paused');evidence['delivered_patient']=delivered
    assert delivered and delivered['health'].get('bedThingId'),'No living patient delivered into a native bed'
    assert evidence['transit'],'Delivery lacked an observed exact carried identity'
    rt.batch=await observe(rt.game);rt.reconcile_plan()
    evidence['rescue_completed']=progress.model_dump()
    assert progress.state=='complete',progress
    print('PASS: ordinary downing, exact carried patient, living bed delivery and shared completion',flush=True)


if __name__=='__main__':
    from native_combat_smoke import main
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--prepared-root',type=Path,required=True)
    parser.add_argument('--chat',action='store_true',help='Require actual configured local-model patient/rescuer selection')
    args=parser.parse_args()
    asyncio.run(main(root=args.prepared_root,rescue=True,rescue_chat=args.chat))
