"""Paused native shell admission and player-zone-edit acceptance, without inference."""
import argparse
import asyncio
import json
import time
from pathlib import Path

from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.campaign_manifest import capture_manifest
from rimgovernor.colony_plan import CommitSteps, PlanStep, RoomShell
from rimgovernor.construction_preflight import preflight_construction
from rimgovernor.headless import isolated_root, prepare
from rimgovernor.player_commands import apply_command
from rimgovernor.shell_site import ShellSiteRefusal, ZONE_ARGUMENTS, cell_set
from rimgovernor.spatial import room_entrance
from rimgovernor.store import Store


def complete_buildings(census):
    rows = census.get('buildings')
    if (census.get('success') is not True or not isinstance(rows, list)
            or (census.get('skipped') or {}).get('byMaxDetailed') != 0
            or (census.get('counts') or {}).get('detailed') != len(rows)
            or any(not isinstance(row, dict) or not isinstance(row.get('thingId'), str) for row in rows)
            or len({row['thingId'] for row in rows}) != len(rows)):
        raise AssertionError(f'Incomplete native building census: {census}')
    return sorted(rows, key=lambda row: row['thingId'])


def complete_zones(census):
    rows = census.get('zones')
    if (census.get('success') is not True or not isinstance(rows, list)
            or census.get('zoneCount') != len(rows) or census.get('zoneCountOnMap') != len(rows)
            or (census.get('totals') or {}).get('gridSweepFailed') is not False):
        raise AssertionError(f'Incomplete native zone census: {census}')
    identities, occupied = set(), set()
    for row in rows:
        if not isinstance(row, dict) or type(row.get('id')) is not int or row['id'] in identities:
            raise AssertionError(f'Invalid or duplicate native zone identity: {row}')
        identities.add(row['id'])
        listed, grid = cell_set(row.get('cells')), cell_set(row.get('gridCells'))
        if (row.get('consistent') is not True or listed != grid
                or row.get('listedCellCount') != len(listed) or row.get('gridCellCount') != len(grid)
                or row.get('cellsNotListed') != 0 or row.get('gridCellsNotListed') != 0
                or grid & occupied):
            raise AssertionError(f'Incomplete or inconsistent native zone geometry: {row}')
        occupied.update(grid)
    return census


async def run(args):
    args.output.mkdir(parents=True, exist_ok=False)
    root = isolated_root(args.source_root, args.output/'bridge')
    configuration = prepare(root)
    store = Store(args.output/'state.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=True,
                       model_factory=lambda _: NoInference())
    report = dict(outcome='failed', cases=[], scope='Paused native shell admission and '
                  'zone-edit refusal; no pawn construction, route traversal or survival claim')
    started_runtime = False

    def record(name, passed, **evidence):
        report['cases'].append(dict(name=name, passed=bool(passed), **evidence))
        (args.output/'progress.json').write_text(json.dumps(report, indent=2))
        print(f'{name}: {bool(passed)}', flush=True)
        if not passed:
            raise AssertionError(name)

    async def buildings():
        rows = await rt.game.query('home/list_buildings', aggregate=False, playerOnly=True)
        return complete_buildings(rows)

    async def zones():
        return complete_zones(await rt.game.query('home/list_zones', **ZONE_ARGUMENTS))

    async def zone_write(**arguments):
        arguments.update(watch=False)
        preview = await rt.game.invoke('home/zone_cells', dict(arguments, dryRun=True))
        if preview.get('success') is not True:
            raise AssertionError(f'Native fixture zone preview refused: {preview}')
        result = await rt.game.invoke('home/zone_cells', dict(arguments, dryRun=False), allow_write=True)
        if result.get('success') is not True:
            raise AssertionError(f'Native fixture zone write refused: {result}')
        return result

    try:
        manifest = capture_manifest(Path(__file__).resolve().parents[1], root, configuration,
                                    rt.router.routing.model_dump(mode='json'))
        (args.output/'manifest.json').write_text(json.dumps(manifest, indent=2))
        started_runtime = True
        await ready(rt)
        initial_status = await rt.game.query('home/status', colonists=False, threats=False)
        initial_tick = initial_status['time']['ticksGame']
        record('initial_manual_pause', rt.mode == 'manual' and initial_status['time']['paused'],
               status=initial_status)
        facts = await rt.game.query('home/colony_facts', planning=True)
        shell = rt.controller.skills.shell(await rt.controller.skills.layout(facts))
        step = PlanStep(id='spatial-probe', title='Spatial acceptance shell', action=shell,
                        source='PLAYER', purpose='shelter', completion_criteria='Native shell observed')
        plan_before = rt.current_plan.model_dump(mode='json')
        buildings_before = await buildings()
        actions_before = rt.counters['actions']
        spec = CommitSteps(expected_revision=rt.current_plan.revision, reason='Native spatial probe',
                           steps=[step]).decision(rt.current_plan).plan
        started = time.monotonic()
        await preflight_construction(spec, rt.current_plan, rt.game)
        preflight_seconds = time.monotonic()-started
        record('native_baseline_preflight_preserves_plan_and_orders',
               rt.current_plan.model_dump(mode='json') == plan_before
               and await buildings() == buildings_before and rt.counters['actions'] == actions_before,
               shell=shell, preflight_seconds=preflight_seconds)

        if args.projected:
            timings = []
            for _ in range(3):
                began = time.monotonic()
                await preflight_construction(spec, rt.current_plan, rt.game)
                timings.append(time.monotonic()-began)
            record('repeated_native_preflight', True, seconds=timings)
            proposed = spec.model_copy(deep=True)
            (x, z), (dx, dz) = room_entrance(step.action)
            outside = (x+dx, z+dz)
            pocket = [(outside[0]+dx, outside[1]+dz),
                      (outside[0]+dz, outside[1]+dx), (outside[0]-dz, outside[1]-dx)]
            proposed.steps.append(PlanStep(id='projected-pocket', title='Native projected obstruction',
                completion_criteria='Native walls observed', source='PLAYER', action=dict(
                    kind='place_buildings', placements=[dict(def_name='Wall', x=a, z=b,
                        materials=['WoodLog']) for a, b in pocket])))
            refusal = None
            try:
                await preflight_construction(proposed, rt.current_plan, rt.game)
            except ShellSiteRefusal as error:
                refusal = dict(code=error.code, detail=str(error), evidence=error.evidence)
            record('native_projected_walls_cannot_seal_shell_entrance',
                refusal is not None and refusal['code'] == 'disconnected_entrance', refusal=refusal)
            record('projected_refusal_preserves_plan_and_orders',
                rt.current_plan.model_dump(mode='json') == plan_before and await buildings() == buildings_before)

        # Locate a real sowable interior cell using the native zone preview.
        bounds = RoomShell.model_validate(shell).bounds
        existing_zones = await zones()
        occupied = {(cell['x'], cell['z']) for zone in existing_zones['zones']
                    for cell in zone['gridCells']}
        fixture_cell = None
        for x, z in bounds.cells():
            if x in (bounds.x, bounds.x+bounds.width-1) or z in (bounds.z, bounds.z+bounds.height-1):
                continue
            if (x, z) in occupied:
                continue
            request = dict(op='create', zoneType='growing', label='B06 enclosed farm',
                           x=x, z=z, width=1, height=1, watch=False, dryRun=True)
            preview = await rt.game.invoke('home/zone_cells', request)
            if preview.get('success') is True and preview.get('cellsAccepted') == 1:
                fixture_cell = dict(x=x, z=z, width=1, height=1)
                break
        if fixture_cell is None:
            raise AssertionError('No native growing-zone fixture cell in selected shell interior')

        stockpile_receipt = await zone_write(op='create', zoneType='stockpile',
                                             label='B06 indoor stockpile', **fixture_cell)
        stockpile_census = await zones()
        stockpile = next(row for row in stockpile_census['zones'] if row['label'] == 'B06 indoor stockpile')
        record('native_stockpile_wholly_inside_shell',
               {(c['x'], c['z']) for c in stockpile['gridCells']} == {(fixture_cell['x'], fixture_cell['z'])},
               receipt=stockpile_receipt, census=stockpile_census)
        started = time.monotonic()
        await preflight_construction(spec, rt.current_plan, rt.game)
        preflight_seconds = time.monotonic()-started
        record('interior_stockpile_admitted_without_orders',
               rt.current_plan.model_dump(mode='json') == plan_before
               and await buildings() == buildings_before and rt.counters['actions'] == actions_before,
               preflight_seconds=preflight_seconds)
        await zone_write(op='delete', zone=str(stockpile['id']))

        farm_receipt = await zone_write(op='create', zoneType='growing', label='B06 enclosed farm', **fixture_cell)
        farm_census = await zones()
        farm = next(row for row in farm_census['zones'] if row['label'] == 'B06 enclosed farm')
        record('native_farm_wholly_inside_shell',
               {(c['x'], c['z']) for c in farm['gridCells']} == {(fixture_cell['x'], fixture_cell['z'])},
               receipt=farm_receipt, census=farm_census)
        refusal = None
        started = time.monotonic()
        try:
            await apply_command(rt, dict(kind='BuildRoom', intent_id='spatial-farm-refusal',
                                        room=shell, purpose='shelter'),
                                token=rt.context_token, revision=rt.chat_revision)
        except ShellSiteRefusal as error:
            refusal = dict(code=error.code, detail=str(error), evidence=error.evidence)
        record('shared_admission_refuses_enclosed_native_farm',
               refusal is not None and refusal['code'] == 'existing_zone', refusal=refusal,
               admission_seconds=time.monotonic()-started)
        record('refusal_preserves_plan_orders_and_zone',
               rt.current_plan.model_dump(mode='json') == plan_before
               and await buildings() == buildings_before and rt.counters['actions'] == actions_before
               and (await zones())['zones'] == farm_census['zones'])

        final_status = await rt.game.query('home/status', colonists=False, threats=False)
        record('paused_tick_and_zero_inference', final_status['time']['paused']
               and final_status['time']['ticksGame'] == initial_tick and rt.mode == 'manual'
               and rt.counters['model_calls'] == 0 and NoInference.attempts == 0, status=final_status)
        report['outcome'] = 'passed'
    except Exception as error:
        report['error'] = f'{type(error).__name__}: {error}'
        report['error_evidence'] = getattr(error, 'evidence', None)
        print(json.dumps(report['error_evidence']), flush=True)
        raise
    finally:
        report.update(plan=rt.current_plan.model_dump(mode='json'), counters=rt.counters,
                      model_attempts=NoInference.attempts)
        try:
            if started_runtime:
                await rt.stop()
        finally:
            store.close()
            (args.output/'result.json').write_text(json.dumps(report, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True, help='Must not exist')
    parser.add_argument('--seconds', type=int, default=300, help='Whole trial timeout including startup')
    parser.add_argument('--projected', action='store_true', help='Require native projected wall obstruction and repeated preflight timings')
    args = parser.parse_args()
    asyncio.run(asyncio.wait_for(run(args), args.seconds))
