"""Regenerate/check G01 state baseline using repository tests and disposable SQLite only.

Run with the controller test environment. No native connection or user database is read.
--write updates intentional JSON fixtures; the default checks for source/fixture drift.
"""
import argparse
import ast
import asyncio
import json
from pathlib import Path
import re
import runpy
import sys
import tempfile
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'controller'))
REVISION = '28d115ee2e87dea536737f87b19b445a6c5b4b10'
FIXTURE = 'contracts/fixtures/state-baseline.json'


def source(path, symbol=None):
    result = {'path': path}
    if symbol:
        tree = ast.parse((ROOT / path).read_text(encoding='utf-8'))
        node = next(n for n in ast.walk(tree) if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef)) and n.name == symbol)
        result.update(symbol=symbol, line=node.lineno)
    return result


def build():
    items = []

    def row(identity, category, path, symbol, notes, chunk='G01.04b', package='store', fixtures=True, native=()):
        items.append(dict(id=identity, category=category, source=source(path, symbol),
                          go_package='go/internal/' + package, owner_chunk=chunk,
                          fixtures=([FIXTURE, 'controller_tests/test_plan_archive.py', 'controller_tests/test_event_delivery.py',
                                     'controller_tests/test_session_checkpoint.py'] if fixtures else []), native_scenarios=list(native),
                          status='pending', notes=notes))

    store_path = 'controller/rimgovernor/store.py'
    store_text = (ROOT / store_path).read_text(encoding='utf-8')
    schema = next(n.value for n in ast.walk(ast.parse(store_text)) if isinstance(n, ast.Constant)
                  and isinstance(n.value, str) and 'CREATE TABLE' in n.value)
    for statement in schema.split(';'):
        match = re.search(r'CREATE (TABLE|INDEX) IF NOT EXISTS (\w+)', statement)
        if match:
            kind, name = match.groups()
            row('sqlite.' + name, 'store_' + kind.lower(), store_path, '__init__',
                'State agent owns SQLite parity; test_plan_archive.py and test_event_delivery.py. DDL: ' + ' '.join(statement.split()))
    row('sqlite.migration', 'migration', store_path, '__init__',
        'State agent: initialization uses idempotent CREATE TABLE/INDEX; no numbered migration ledger or PRAGMA user_version. Preserve existing rows and partial diagnostic-history index. G01.04b must define format checks; no Go compatibility established.')
    for symbol, note in [
        ('transaction', 'Nested SAVEPOINT/RELEASE; rollback removes queued callbacks; outermost successful commit publishes callbacks.'),
        ('archive_and_set', 'Archive immutable gzip UTF-8 JSON records and compact state snapshot atomically; mismatched existing records refuse.'),
        ('decision', 'Compressed model request/result records; retain latest 64 decisions across colonies.'),
        ('history', 'Colony-scoped ascending returned history after descending bounded query; diagnostics hidden by default.'),
    ]:
        row('store.' + symbol, 'persistence_boundary', store_path, symbol,
            note + ' State agent acceptance: controller_tests/test_event_delivery.py and controller_tests/test_plan_archive.py.')
    for symbol, note in [
        ('bind_archive', 'Missing action/method archive blocks admission after restart; colony and method epoch scope identity.'),
        ('prepare_archive', 'Prepare immutable action, method and hunting evidence records without modifying live state; combat references remain pinned. Legacy hunting metadata follows previously archived actions.'),
        ('finish_archive', 'In-memory compaction only after outermost SQLite commit; retain exact receipts and recovery history.'),
    ]:
        row('archive.' + symbol, 'archive_boundary', 'controller/rimgovernor/plan_archive.py', symbol,
            note + ' State agent acceptance: controller_tests/test_plan_archive.py.')
    for symbol, note in [
        ('create_checkpoint', 'Pause/cleanup and identity-direction-tick recheck; native XML save then SQLite backup, SHA256 hashes and version=1 manifest publication last.'),
        ('read_checkpoint', 'Validate version, ownership, root/save name and both artifact hashes before use.'),
        ('prepare_resume', 'Copy paired SQLite into new resume directory; never mutate checkpoint backup.'),
        ('stop_for_restart', 'Recheck Manual/session/direction/tick; owned game stops, attached session detaches only.'),
        ('delete_checkpoint', 'Refuse active resume pair, changed session/hashes, closing session or unexpected files; unpublish manifest first.'),
        ('install_saved_game', 'Owned resume installs verified save into private profile; attached resume cannot replace native game.'),
        ('list_checkpoints', 'List retained valid and damaged checkpoint pairs without deleting artifacts.'),
    ]:
        row('checkpoint.' + symbol, 'checkpoint_boundary', 'controller/rimgovernor/session_checkpoint.py', symbol,
            note + ' State/integration agents acceptance: controller_tests/test_session_checkpoint.py; native pair replay pending.',
            native=('scripts/session_checkpoint_acceptance.py', 'scripts/attached_checkpoint_acceptance.py'))
    for identity, path, symbol, note, chunk, package in [
        ('bridge_snapshot', 'bridge_runtime.py', 'persist', 'bridge:<colony>:<map> stores chat, legacy plan, projects, chat/handled revisions, load-scoped drafts, current_plan, strategic_state and advice atomically with archives.', 'G01.04b', 'store'),
        ('load_restore', 'bridge_runtime.py', 'sync_identity', 'Restore same colony/map intent; new load enters Manual, increments direction and interrupts pending chat; stale draft ownership discarded.', 'G01.06', 'runtime'),
        ('chat_dedup', 'bridge_runtime.py', 'steer', 'Request acknowledgment, history, player direction and snapshot share a transaction; changed text for old request ID refuses.', 'G01.04b', 'store'),
        ('clock_delivery', 'bridge_runtime.py', 'receive_clock_events', 'Consume durable inbox with snapshot/history transaction; failed delivery keeps inbox and invalidates authority.', 'G01.06', 'runtime'),
        ('clock_source', 'clock_control.py', '__init__', 'clock-source:<colony>:<map> retains cursor/epoch/context; move unconsumed old-load inbox to current load atomically.', 'G01.04b', 'store'),
        ('clock_inbox', 'clock_control.py', 'record', 'clock-inbox:<context> fetched events and source cursor commit together before delivery.', 'G01.04b', 'store'),
        ('model_metrics', 'model_router.py', '__init__', 'model_role_metrics is unscoped persisted model role performance; model agent owns metric parity.', 'G01.08', 'model'),
    ]:
        row('state.' + identity, 'saved_state', 'controller/rimgovernor/' + path, symbol,
            note + ' Owning chunk agent acceptance: controller_tests/test_event_delivery.py; model metric acceptance belongs to G01.08.', chunk, package)
    row('action.signature', 'identity_encoding', 'controller/rimgovernor/colony_plan.py', 'signature',
        'State agent owns exact Python action signature serialization and cancelled fingerprints. Representative signature included; Unicode/numeric/null cross-language matrix remains G01.04a acceptance.', 'G01.04a', 'domain')
    row('native.paired_state_fixture', 'fixture_gap', 'scripts/mixed_checkpoint_fixture.py', None,
        'Integration agent owns licensed native mixed checkpoint acceptance. No real native save, journal or live database captured; synthetic tests cannot establish native restart or pawn completion.', 'G01.11', 'testkit', False,
        ('scripts/mixed_checkpoint_fixture.py', 'scripts/uncertain_checkpoint_fixture.py'))

    from rimgovernor.store import Store
    archive = runpy.run_path(str(ROOT / 'controller_tests/test_plan_archive.py'))
    observations = runpy.run_path(str(ROOT / 'controller_tests/test_bridge_observation.py'))
    delivery = runpy.run_path(str(ROOT / 'controller_tests/test_event_delivery.py'))
    recovery_path = 'controller_tests/test_observation_recovery.py'
    recovery = runpy.run_path(str(ROOT / recovery_path))
    fixture_rows = []

    def fixture(identity, category, path, symbol, value, note):
        fixture_rows.append(dict(id=identity, category=category, source=source(path, symbol), value=value, notes=note))

    fixture('observation.native', 'observation', 'controller_tests/test_bridge_observation.py', 'native', observations['native'](),
            'Exact synthetic native helper output; unknown nulls and known zeros retained. No live recording.')
    plan = archive['retired_plan']()
    fixture('plan.action', 'plan', 'controller_tests/test_plan_archive.py', 'action',
            {'step': archive['action']('action-0').model_dump(mode='json'), 'signature': archive['action']('action-0').signature()},
            'Exact action helper with identity action-0; signature is unnormalized.')
    with tempfile.TemporaryDirectory(prefix='rimgovernor-state-baseline-') as directory:
        store = Store(Path(directory) / 'archive.sqlite')
        try:
            archive['persist'](store, plan)
            fixture('archive.action', 'receipt', 'controller_tests/test_plan_archive.py', 'persist', store.retired_action('colony', 'action-0'),
                    'Exact first archived record from retired_plan(); accepted synthetic receipt does not prove pawn work.')
            fixture('archive.snapshot', 'saved_state', 'controller_tests/test_plan_archive.py', 'persist',
                    {key: value for key, value in store.get('plan').items() if key != 'history'},
                    'Projection of compact persisted plan from 73-action test helper: history omitted to keep fixture small. All included fields exact; this is not a complete importable database.')
            assert store.db.execute('PRAGMA integrity_check').fetchone()[0] == 'ok'
        finally:
            store.close()
        rt = delivery['runtime'](Path(directory) / 'chat.sqlite')
        try:
            with patch('rimgovernor.store.time.time', return_value=1000.0):
                receipt = asyncio.run(rt.steer('Protect food', request_id='request-1', session_id=rt.context_token))
            fixture('api.chat_ack', 'api_response', 'controller_tests/test_event_delivery.py', 'test_lost_chat_acknowledgment_is_deduplicated_after_reopen', receipt,
                    'Exact steer response used by chat API; injected wall-clock=1000.0 only. No HTTP envelope claimed.')
            fixture('state.chat_request', 'saved_state', 'controller_tests/test_event_delivery.py', 'test_lost_chat_acknowledgment_is_deduplicated_after_reopen',
                    {key: json.loads(value) for key, value in rt.store.db.execute('SELECT key,value FROM state ORDER BY key')},
                    'Exact disposable SQLite state after test request, including acknowledgment and runtime snapshot.')
        finally:
            rt.store.close()
    tree = ast.parse((ROOT / recovery_path).read_text(encoding='utf-8'))
    function = next(n for n in tree.body if isinstance(n, ast.FunctionDef))
    # Execute the existing test setup and first reconciliation, before its first assertion.
    statements = function.body[:next(i for i, n in enumerate(function.body) if isinstance(n, ast.Assert))]
    exec(compile(ast.Module(body=statements, type_ignores=[]), recovery_path, 'exec'), recovery)
    fixture('failure.observation', 'failure', recovery_path, function.name, recovery['progress'].model_dump(mode='json'),
            'Exact waiting progress after failed read reconciliation; issued slot retained, no retry.')
    manifest = dict(version=1, source_revision=REVISION, items=items)
    fixtures = dict(version=1, source_revision=REVISION, normalization='Only chat fixture wall-clock injection at 1000.0; no post-hoc normalization.', items=fixture_rows)
    return {'contracts/state-inventory.json': manifest, FIXTURE: fixtures}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--write', action='store_true')
    mode.add_argument('--check', action='store_true', help='Check only (the default).')
    args = parser.parse_args()
    for name, value in build().items():
        path = ROOT / name
        encoded = json.dumps(value, indent=2, ensure_ascii=False) + '\n'
        if args.write:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(encoded, encoding='utf-8', newline='\n')
        elif not path.exists() or path.read_text(encoding='utf-8') != encoded:
            raise SystemExit('State inventory drift: ' + name + '; inspect source change before --write')
        print(('Wrote ' if args.write else 'Verified ') + name + ': ' + str(len(value['items'])) + ' items')


if __name__ == '__main__':
    main()
