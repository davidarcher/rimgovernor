"""Resume only known pre-write material shortages after fresh native previews."""
from .colony_plan import Buildings,RoomShell
from .hands import Blocked,room_placements


async def recover_construction(rt, step_id, *, token, direction, limit):
    async with rt.lock:
        await rt.sync_identity()
        plan=rt.current_plan
        step=next((s for s in plan.spec.steps if s.id==step_id),None)
        if step is None:return False
        progress=plan.progress[step_id];goal=plan.colony_goals.get(step.goal_id)
        failure=progress.failure
        if (step.source!='AUTOPILOT' or not isinstance(step.action,(Buildings,RoomShell))
                or not goal or goal.cancelled or progress.state!='blocked' or not failure
                or failure.code not in ('construction_resources','construction_unavailable') or not failure.retryable):return False
        evidence=failure.evidence;revision=plan.revision
        if failure.code=='construction_unavailable' and evidence.get('prewrite') is not True:return False
        def valid():
            return (rt.current_plan is plan and plan.revision==revision and rt.mode=='automate'
                and rt.context_token==token==evidence.get('load_token')
                and rt.chat_revision==direction==evidence.get('direction')
                and step.signature()==evidence.get('signature') and not goal.cancelled)
        if not valid() or len(progress.recovery_history)>=limit:return False
        if any(v.get('confirmed') is not True for v in progress.issued.values()):return False
        status=await rt.game.query('home/status',colonists=False,threats=True)
        tick=status.get('time',{}).get('ticksGame')
        if type(tick) is not int or type(evidence.get('tick')) is not int or tick<evidence['tick']:return False
        if status.get('time',{}).get('paused') is not True:return False
        threats=status.get('threats',{})
        if threats.get('hostileCount')!=0 or threats.get('huntingPredatorCount')!=0:return False
        if not valid():return False
        placements=room_placements(step.action) if isinstance(step.action,RoomShell) else step.action.placements
        try:
            for index,placement in enumerate(placements):
                if str(index) in progress.issued:continue
                await rt.hands.place(rt,placement,progress,str(index),revision,token,direction,preview_only=True)
                if not valid():return False
        except (Blocked,ValueError,InterruptedError):
            return False
        await rt.sync_identity()
        if not valid():return False
        progress.recovery_history.append({'tick':tick,'load_token':token,'failure':failure.model_dump(),
            'confirmed_slots':sorted(progress.issued)})
        progress.state,progress.failure='pending',None
        goal.status,goal.reason='active',''
        goal.last_progress_tick=tick
        plan.revision+=1
        rt.note('construction_recovery','Native construction preconditions recovered; retained confirmed placements',
            step=step_id,attempts=len(progress.recovery_history),limit=limit)
        rt.persist()
        return True
