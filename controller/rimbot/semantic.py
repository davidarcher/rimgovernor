"""Daily strategic coordination; routine specialists execute owned projects directly."""
import uuid
import asyncio
import time
from .contracts import Decision, Proposal
from .semantic_models import ObjectiveProposal, ObjectiveDecision
from .model import ModelError


def reconcile_projects(memory):
    changed=False
    work={w['id']:w for w in memory.get('work',[])}
    for project in memory.get('projects',[]):
        if project.get('status') not in ('awaiting_work','orders_verified'):continue
        ids=project.get('work_ids',[])
        if not ids:continue
        rows=[work[i] for i in ids if i in work]
        if len(rows)!=len(ids):status='needs_review'
        elif all(w['status']=='complete' for w in rows):
            # Supply access supports another system; it cannot verify that system's orders.
            support={'post_things_set_forbidden','orders_unforbid_all'}
            only_support=all(w.get('action',{}).get('endpoint')=='post_work_settings' or (project['kind']!='supply_access' and w.get('action',{}).get('endpoint') in support) for w in rows)
            status='needs_review' if only_support else 'orders_verified'
            if only_support:project['progress_note']='Supporting settings/supply orders verified; the main project still needs orders or an observed outcome.'
        elif any(w['status'] in ('rejected','deferred','unresolved') for w in rows):status='needs_review'
        else:status='awaiting_work'
        if status=='orders_verified':project.pop('progress_note',None)
        if project.get('status')!=status:project['status']=status;changed=True
    return changed


def retain_project(memory,owner,objective):
    projects=memory.setdefault('projects',[])
    existing=next((p for p in projects if p['project_id']==objective.project_id),None) if objective.project_id else None
    if objective.project_id and (existing is None or existing['owner']!=owner):raise ValueError('Project must reference an existing objective owned by this manager')
    if existing is None:
        existing=next((p for p in projects if p.get('status')!='retired' and p['kind']==objective.kind and p['outcome'].casefold().strip()==objective.outcome.casefold().strip()),None)
    if existing is None:
        existing={'project_id':uuid.uuid4().hex[:12],'owner':owner,'work_ids':[],'created_at':time.time()};projects.append(existing)
    if existing.get('kind') and existing['kind']!=objective.kind:
        existing.setdefault('previous_work_ids',[]).extend(existing['work_ids'])
        existing['work_ids']=[]
    existing.update(objective.model_dump(exclude={'project_id'}),status='approved')
    existing.setdefault('feedback',[])
    return existing


async def arbitrate_objectives(rt,context):
    decision=await rt.planner.ask('Administrator: approve semantic objectives',context,ObjectiveDecision,rt.settings.reasoning)
    rt.planner.validate_submission('Administrator',decision,context)
    return decision


async def semantic_review(rt,context,roles):
    day=(rt.last_tick or 0)//60000
    active_projects=[p for p in rt.memory.get('projects',[]) if p.get('status')!='retired']
    admin_due=context.get('administration_required') or rt.memory.get('last_admin_day')!=day or rt.memory.get('admin_requested')
    if not admin_due:
        await execute_projects(rt,context,active_projects)
        return
    proposals={}
    shared=await rt.manager_context(context)
    if rt.memory.get('admin_requested'):shared['coordination_request']=rt.memory['admin_requested']
    shared['cancelled_projects']=[{'kind':p['kind'],'outcome':p['outcome']} for p in rt.memory.get('projects',[]) if p.get('cancelled_by_player')][-30:]
    projects=[p for p in rt.memory.get('projects',[]) if p.get('status')!='retired']
    semaphore=asyncio.Semaphore(rt.settings.manager_parallelism)
    active=set()
    async def propose(role):
        async with semaphore:
            active.add(role)
            await rt.progress(active_roles=sorted(active))
            try:
                fresh={**shared,'assigned_task':context.get('assignments',{}).get(role),
                       'projects':projects}
                fresh.pop('assignments',None)
                proposal=await rt.planner.ask(role,fresh,ObjectiveProposal,rt.settings.reasoning)
                proposals[role]=proposal.model_dump()
                rt.note('proposal',proposal.summary,role=role,proposal=proposal.model_dump(),semantic=True)
            except (ModelError,ValueError) as error:
                rt.note('error',str(error),role=role)
            finally:
                active.discard(role)
                await rt.progress(active_roles=sorted(active))
    await asyncio.gather(*(propose(role) for role in dict.fromkeys(roles)))
    # Keep stable ordering independent of response completion timing.
    proposals={role:proposals[role] for role in roles if role in proposals}
    if not proposals and not projects:
        raise ModelError('No department submitted an objective review.')
    candidates={f'{owner}:{i}':{'owner':owner,'objective':objective,'blockers':proposal['blockers']} for owner,proposal in proposals.items() for i,objective in enumerate(proposal['objectives'])}
    work={w['id']:w for w in rt.memory['work']}
    for project in projects:
        states=sorted((i,work.get(i,{}).get('status','missing')) for i in project['work_ids'])
        previous=project.get('reviewed_order_states')
        project['reviews_without_order_change']=project.get('reviews_without_order_change',0)+1 if previous==states else 0
        project['reviewed_order_states']=states
        project['age_days']=round((time.time()-project['created_at'])/86400,2) if project.get('created_at') else None
    # Unblocked routine setup does not require another strategic approval turn.
    routine=[]
    for key,candidate in list(candidates.items()):
        if candidate['objective']['kind'] in ('supply_access','storage','growing') and not candidate['blockers']:
            objective=ObjectiveProposal.model_validate({'summary':'Routine setup','objectives':[candidate['objective']]}).objectives[0]
            routine.append(retain_project(rt.memory,candidate['owner'],objective))
            del candidates[key]
    if routine:
        rt.persist()
        rt.note('info','Routine setup proceeds without administrator approval.',role='Colony')
        await execute_projects(rt,context,routine)
        projects=[p for p in rt.memory['projects'] if p.get('status')!='retired']
    if routine and not candidates and all(p in routine for p in projects) and not context.get('administration_required'):
        rt.memory['last_admin_day']=day;rt.persist()
        return
    shared=await rt.manager_context(shared)
    shared=await rt.observe_resources(shared)
    decision_context={**shared,'projects':projects,'proposals':candidates,'semantic_objectives':True}
    try:
        decision=await arbitrate_objectives(rt,decision_context)
    except ModelError as error:
        if not projects:raise
        rt.check_generation()
        rt.note('error','Strategic review failed; continuing only existing approved projects. '+str(error),role='Administrator')
        rt.memory['last_admin_day']=day
        rt.memory['admin_requested']=False
        rt.persist()
        await execute_projects(rt,context,projects)
        return
    summary=decision.response if len(decision.response)<=650 else decision.response[:647]+'...'
    rt.note('arbitration',summary,explanation=decision.response,role='Administrator',accepted=decision.accepted,deferred=decision.deferred,retired=decision.retire_projects,kept=len(decision.keep_projects))
    rt.reply(summary)
    rt.check_generation()
    if rt.mode!='automate':return
    for project in projects:
        if project['project_id'] in decision.retire_projects:
            project['status']='retired';project['feedback']=[decision.retire_projects[project['project_id']]]
    if decision.updates or decision.retire_projects:
        rt.memory['last_daily_day']=None
    approved=[]
    for key in decision.accepted:
        candidate=candidates[key]
        objective=decision.updates.get(key) or ObjectiveProposal.model_validate({'summary':'Approved','objectives':[candidate['objective']]}).objectives[0]
        existing=next((p for p in projects if p['project_id']==objective.project_id),None)
        owner=existing['owner'] if existing else candidate['owner']
        project=retain_project(rt.memory,owner,objective)
        if project not in approved:approved.append(project)
    active_ids={i for p in rt.memory['projects'] if p.get('status')!='retired' for i in p['work_ids']}
    obsolete_ids={i for p in rt.memory['projects'] for i in (p['work_ids'] if p.get('status')=='retired' else p.get('previous_work_ids',[]))}
    for work in rt.memory['work']:
        if work['id'] in obsolete_ids-active_ids and work['status'] not in ('complete','dismissed'):
            work['status']='dismissed';work['detail']='Project retired or reclassified; stopped tracking this order. Game orders unchanged.'
    rt.persist()
    rt.memory['last_admin_day']=day
    rt.memory['admin_requested']=False
    rt.persist()
    # Kept projects continue even when no department restates their objective.
    scheduled=[p for p in approved+[p for p in projects if p.get('status')!='retired' and p not in approved] if p not in routine]
    await execute_projects(rt,context,scheduled)


async def execute_projects(rt,context,scheduled):
    from .spatial import prepare_layout, validate_orders, SPATIAL_KINDS
    spatial_error=None
    try: await prepare_layout(rt,context,scheduled)
    except (ModelError,ValueError,RuntimeError) as error:
        spatial_error=str(error)
        rt.note("error",spatial_error,role="Architect")
    await rt.resume_initial_planning()
    for project in scheduled:
        rt.check_generation()
        if rt.mode!='automate':break
        role='Executor:'+project['kind']
        try:
            if spatial_error and project['kind'] in SPATIAL_KINDS:raise ValueError(spatial_error)
            if project['kind'] in SPATIAL_KINDS:
                layout=rt.memory.get('spatial_layout',{})
                if not any(project['project_id'] in r['project_ids'] for r in layout.get('regions',[])):
                    raise ValueError('Waiting for architect: '+layout.get('deferred',{}).get(project['project_id'],'No valid site reserved.'))
            fresh=await rt.manager_context(context)
            fresh=await rt.observe_resources(fresh)
            fresh={k:v for k,v in fresh.items() if k not in ('plans','assignments')}
            fresh.update(project_owner=project['owner'],project=project,projects=[p for p in rt.memory['projects'] if p.get('status')!='retired'],assigned_task=project['outcome'])
            fresh['spatial_reservations']=rt.memory.get('spatial_layout',{})
            project['status']='inspecting';rt.persist()
            batch=await rt.planner.ask(role,fresh,Proposal,rt.settings.reasoning)
            if batch.escalation_reason:
                rt.memory['admin_requested']=batch.escalation_reason
                project['status']='needs_review';project['feedback']=[batch.escalation_reason]
                rt.note('escalation',batch.escalation_reason,role=project['owner'],project_id=project['project_id'])
                rt.last_review=-100000
                continue
            project['feedback']=batch.blockers
            rt.note('execution_plan',f'Prepared {len(batch.actions)} orders; not yet executed.',role=role,project_id=project['project_id'],orders=len(batch.actions),model_summary=batch.summary,blockers=batch.blockers)
            await validate_orders(rt,project,batch.actions)
            before_batch={w['id'] for w in rt.memory['work']}
            seen=set()
            for action in batch.actions:
                import json
                identity=(action.endpoint,json.dumps(action.arguments,sort_keys=True))
                if identity in seen:continue
                seen.add(identity)
                before={w['id'] for w in rt.memory['work']}
                try:
                    await validate_orders(rt,project,[action],complete=False)
                    await rt.execute(action,role)
                    failed=[w for w in rt.memory['work'] if w['id'] not in before and w['status'] in ('deferred','rejected','unknown')]
                    if failed:raise ValueError('Construction/order batch stopped after an unconfirmed order: '+failed[0]['detail']+'. Reinspect and submit the remaining complete plan; later orders were not sent.')
                finally:
                    for w in rt.memory['work']:
                        if w['id'] not in before:
                            w['project_id']=project['project_id'];project['work_ids'].append(w['id'])
                    rt.persist()
            from collections import Counter
            outcomes=Counter(w['status'] for w in rt.memory['work'] if w['id'] not in before_batch)
            labels={'complete':'verified complete','issued':'sent; awaiting verification','deferred':'deferred; not sent','unknown':'outcome unknown','rejected':'rejected'}
            receipt='; '.join(f'{count} {labels.get(status,status)}' for status,count in outcomes.items()) or 'No new orders recorded.'
            rt.note('execution',receipt,role=role,project_id=project['project_id'],outcomes=dict(outcomes),results=[{k:w[k] for k in ('id','title','status')} for w in rt.memory['work'] if w['id'] not in before_batch],blockers=batch.blockers)
            project['status']='awaiting_work' if batch.actions else 'needs_review'
            reconcile_projects(rt.memory)
        except asyncio.CancelledError:
            project['status']='needs_review';project['feedback']=['Execution interrupted; inspect existing orders before continuing.']
            raise
        except (ModelError,ValueError,RuntimeError) as error:
            project['status']='needs_review';project['feedback']=[str(error)]
            rt.note('error',str(error),role=role,project_id=project['project_id'])
        finally:rt.persist()
