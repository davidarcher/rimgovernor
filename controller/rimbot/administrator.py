"""Administrator investigation, durable notes and direct use of shared execution."""
from typing import Literal
from pydantic import Field
from .contracts import Contract, Action, Proposal, ManagerName
from .semantic_models import WorkObjective, ObjectiveProposal
from .base_plan import SiteRequest
from .enclosure import Enclosure


class MemoryRead(Contract):
    search: str = Field(default='', max_length=200)
    limit: int = Field(default=6, ge=1, le=20)


class ColonyRead(Contract):
    section: Literal['projects','work','zones','regions','plan']
    offset: int = Field(default=0,ge=0)
    limit: int = Field(default=6,ge=1,le=20)


class MemoryWrite(Contract):
    key: str = Field(min_length=1, max_length=80, pattern=r'^[a-zA-Z0-9_-]+$')
    note: str = Field(max_length=1600, description='Concise finding, uncertainty or lesson; empty deletes this key. Never treat a hypothesis as a native fact.')
    evidence: list[str] = Field(default_factory=list, max_length=6, description='Source URLs or observed facts supporting the note.')


class GoalCreate(Contract):
    objective: WorkObjective
    advisor: ManagerName = 'Infrastructure'
    priority: Literal['urgent', 'high', 'normal', 'low'] = 'normal'


class DirectOrder(Contract):
    project_id: str = Field(description='Existing active objective. Create a goal first if none fits.')
    action: Action = Field(description='An existing native command, with arguments obtained from describe. Executes now through shared validation and tracking; no specialist model call.')


class GoalSite(Contract):
    project_id: str
    site: SiteRequest


class GoalEnclosure(Contract):
    project_id: str
    enclosure: Enclosure


def active(rt):
    rt.check_generation()
    if not rt.colony: raise ValueError('Load a colony first.')
    if rt.mode != 'automate': raise ValueError('Automation is off; no changes made.')


def recall(rt, request):
    words = request.search.casefold().split()
    matches = [v for k, v in rt.memory.get('administrator_notes', {}).items()
               if all(word in (k + ' ' + v['note']).casefold() for word in words)]
    return {'items': sorted(matches, key=lambda v: v['tick'], reverse=True)[:request.limit],
            'total': len(matches), 'evidence': 'Administrator recollections; verify changing facts against live state.'}


def inspect(rt, request):
    if request.section=='plan':
        return {'plans':rt.memory.get('plans'),'player_direction':rt.memory.get('direction',[]),
                'evidence':'Saved intentions, not proof of game outcomes.'}
    source=rt.memory.get('spatial_layout',{}) if request.section in ('zones','regions') else rt.memory
    rows=source.get(request.section,[])
    end=request.offset+request.limit
    return {'items':rows[request.offset:end], 'total':len(rows), 'offset':request.offset,
            'next_offset':end if end<len(rows) else None, 'tick':rt.last_tick,
            'evidence':'Controller tracking and reservations. Query native state to check current game outcomes.'}


def remember(rt, request):
    active(rt)
    notes = rt.memory.setdefault('administrator_notes', {})
    if not request.note.strip():
        removed = notes.pop(request.key, None) is not None
        rt.persist()
        return {'deleted': removed}
    if request.key not in notes and len(notes) >= 64:
        raise ValueError('Notebook has 64 entries. Consolidate or delete obsolete entries before adding one.')
    notes[request.key] = {**request.model_dump(), 'tick': rt.last_tick or 0}
    rt.persist()
    rt.note('administrator_memory', request.note, role='Administrator', key=request.key, evidence=request.evidence)
    return {'saved': request.key, 'tick': rt.last_tick or 0}


def create_goal(rt, request):
    active(rt)
    context = {'projects': rt.memory.get('projects', [])}
    rt.planner.validate_submission('Administrator', ObjectiveProposal(summary='Administrator goal', objectives=[request.objective]), context)
    from .semantic import retain_project
    old = next((p for p in context['projects'] if p['project_id'] == request.objective.project_id), None)
    project = retain_project(rt.memory, old['owner'] if old else request.advisor, request.objective, request.priority)
    rt.persist()
    rt.note('administrator_goal', project['outcome'], role='Administrator', project_id=project['project_id'])
    return {'project': project, 'queued': True, 'orders_issued': False}


async def execute(rt, request):
    active(rt)
    project = next((p for p in rt.memory.get('projects', []) if p['project_id'] == request.project_id), None)
    if project is None or project.get('status') in ('retired', 'suspended') or project.get('admin_hold'):
        raise ValueError('Direct execution requires an active, unheld project.')
    from .project_dependencies import check_dependencies
    if not await check_dependencies(rt, project): raise ValueError('Project prerequisites are not ready.')
    context = {'project': project, 'spatial_reservations': rt.memory.get('spatial_layout', {})}
    proposal = rt.planner.validate_submission('Executor:' + project['kind'], Proposal(summary=request.action.title, actions=[request.action]), context)
    await rt.planner.validate_observation(proposal, context=context)
    # Same reservation checks, write lock, native validation, post-write observation
    # and project linkage as immediate specialist orders. No nested model call.
    from .routine import execute_routine
    result = await execute_routine(rt, context, request.action, 'Administrator')
    from .semantic import reconcile_projects
    reconcile_projects(rt.memory)
    rt.persist()
    return result


async def spatial(rt, name, request):
    active(rt)
    project = next((p for p in rt.memory.get('projects', []) if p['project_id'] == request.project_id), None)
    if project is None or project.get('status') in ('retired','suspended') or project.get('admin_hold'):
        raise ValueError('An active unheld project is required.')
    if name == 'reserve_goal_site':
        if project['kind'] not in ('construction','growing','storage'): raise ValueError('Project does not require a spatial site.')
        from .base_plan import reserve_site
        return await reserve_site(rt, project, request.site)
    if project['kind'] != 'construction': raise ValueError('An enclosure requires a construction objective.')
    from .enclosure import compile_enclosure
    action = await compile_enclosure(rt, project, request.enclosure)
    if action is None: return {'already_enclosed': True}
    return await execute(rt, DirectOrder(project_id=request.project_id, action=action))


SCHEMAS = {
    'inspect_colony': ('Inspect saved goals, work receipts, strategy or spatial reservations. Paged; native query supplies live game facts.', ColonyRead),
    'memory_read': ('Search this colony’s administrator notebook. Notes are fallible and may be stale.', MemoryRead),
    'memory_write': ('Save/update a colony-scoped note with evidence; empty note deletes. Persists across reviews and restarts.', MemoryWrite),
    'create_goal': ('Create or revise an approved semantic objective directly, without waiting for an advisor proposal. Does not place orders.', GoalCreate),
    'execute_order': ('Execute a described native command now for an active project. Uses the shared executor checks and observed receipts, without another model turn.', DirectOrder),
    'reserve_goal_site': ('Reserve an increment within the existing base plan for a goal; uses the shared spatial resolver. Does not build.', GoalSite),
    'build_enclosure': ('Compile and issue a complete wall/door perimeter for a reserved room using observed native definitions and materials. No specialist model call.', GoalEnclosure),
}


async def dispatch(rt, name, args):
    request = SCHEMAS[name][1].model_validate(args)
    if name == 'inspect_colony': return inspect(rt, request)
    if name == 'memory_read': return recall(rt, request)
    if name == 'memory_write': return remember(rt, request)
    if name == 'create_goal': return create_goal(rt, request)
    if name in ('reserve_goal_site','build_enclosure'): return await spatial(rt, name, request)
    return await execute(rt, request)
