"""Approve semantic objectives, then execute one native-system project at a time."""
import uuid
import asyncio
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
        elif all(w['status']=='complete' for w in rows):status='orders_verified'
        elif any(w['status'] in ('rejected','deferred','unresolved') for w in rows):status='needs_review'
        else:status='awaiting_work'
        if project.get('status')!=status:project['status']=status;changed=True
    return changed


def retain_project(memory,owner,objective):
    projects=memory.setdefault('projects',[])
    existing=next((p for p in projects if p['project_id']==objective.project_id),None) if objective.project_id else None
    if objective.project_id and (existing is None or existing['owner']!=owner):raise ValueError('Project must reference an existing objective owned by this manager')
    if existing is None:
        existing=next((p for p in projects if p.get('status')!='retired' and p['kind']==objective.kind and p['outcome'].casefold().strip()==objective.outcome.casefold().strip()),None)
    if existing is None:
        existing={'project_id':uuid.uuid4().hex[:12],'owner':owner,'work_ids':[]};projects.append(existing)
    if existing.get('kind') and existing['kind']!=objective.kind:
        existing.setdefault('previous_work_ids',[]).extend(existing['work_ids'])
        existing['work_ids']=[]
    existing.update(objective.model_dump(exclude={'project_id'}),status='approved')
    existing.setdefault('feedback',[])
    return existing


async def semantic_review(rt,context,roles):
    proposals={}
    shared=await rt.manager_context(context)
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
    if not proposals:
        raise ModelError('No department submitted an objective review.')
    candidates={f'{owner}:{i}':{'owner':owner,'objective':objective,'blockers':proposal['blockers']} for owner,proposal in proposals.items() for i,objective in enumerate(proposal['objectives'])}
    decision_context={**shared,'projects':projects,'proposals':candidates,'semantic_objectives':True}
    decision=await rt.planner.ask('Administrator: approve semantic objectives',decision_context,ObjectiveDecision,rt.settings.reasoning)
    rt.planner.validate_submission('Administrator',decision,decision_context)
    rt.note('arbitration',decision.response,role='Administrator',accepted=decision.accepted,deferred=decision.deferred)
    rt.reply(decision.response)
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
    for project in approved:
        rt.check_generation()
        if rt.mode!='automate':break
        role='Executor:'+project['kind']
        try:
            fresh=await rt.manager_context(context)
            fresh={k:v for k,v in fresh.items() if k not in ('plans','assignments')}
            fresh.update(project=project,projects=[p for p in rt.memory['projects'] if p.get('status')!='retired'],assigned_task=project['outcome'])
            project['status']='inspecting';rt.persist()
            batch=await rt.planner.ask(role,fresh,Proposal,rt.settings.reasoning)
            project['feedback']=batch.blockers
            rt.note('execution',batch.summary,role=role,project_id=project['project_id'],orders=len(batch.actions),blockers=batch.blockers)
            seen=set()
            for action in batch.actions:
                import json
                identity=(action.endpoint,json.dumps(action.arguments,sort_keys=True))
                if identity in seen:continue
                seen.add(identity)
                before={w['id'] for w in rt.memory['work']}
                try:await rt.execute(action,role)
                finally:
                    for w in rt.memory['work']:
                        if w['id'] not in before:
                            w['project_id']=project['project_id'];project['work_ids'].append(w['id'])
                    rt.persist()
            project['status']='awaiting_work' if batch.actions else 'needs_review'
            reconcile_projects(rt.memory)
        except asyncio.CancelledError:
            project['status']='needs_review';project['feedback']=['Execution interrupted; inspect existing orders before continuing.']
            raise
        except (ModelError,ValueError,RuntimeError) as error:
            project['status']='needs_review';project['feedback']=[str(error)]
            rt.note('error',str(error),role=role,project_id=project['project_id'])
        finally:rt.persist()
