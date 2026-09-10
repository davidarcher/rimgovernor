import sqlite3
from types import SimpleNamespace
from unittest.mock import Mock
from scripts.worker_storage import storage_options, export_storage, release_storage


def test_volume_exports_only_after_stop_and_checks_database(tmp_path):
    calls = []
    def command(*args, **kwargs):
        calls.append(args)
        if args[0] == 'cp':
            (tmp_path/'run').mkdir()
            db = sqlite3.connect(tmp_path/'run/throughput.sqlite')
            db.execute('CREATE TABLE evidence(value TEXT)')
            db.execute("INSERT INTO evidence VALUES ('retained')")
            db.commit()
            db.close()
        return SimpleNamespace(stdout='true' if args[0] == 'inspect' else '', returncode=0, stderr='')
    options, state = storage_options(command, tmp_path, 'private-worker', 'volume')
    assert 'type=volume,source=private-worker-state,target=/worker' in options
    assert export_storage(command, 'private-worker', tmp_path, state)
    assert [c[0] for c in calls] == ['volume', 'inspect', 'stop', 'cp']
    assert state['database_integrity'] == 'ok'
    release_storage(command, state, False)
    assert state['retained']
    release_storage(command, state, True)
    assert calls[-1] == ('volume', 'rm', 'private-worker-state') and not state['retained']


def test_export_failure_preserves_volume_for_recovery(tmp_path):
    command = Mock(side_effect=RuntimeError('copy failed'))
    state = dict(kind='volume', volume='private-state', retained=True)
    assert not export_storage(command, 'private-worker', tmp_path, state)
    release_storage(command, state, False)
    assert state['retained'] and not state['exported']
    assert command.call_count == 1


def test_bind_comparison_keeps_existing_output_contract(tmp_path):
    command = Mock()
    options, state = storage_options(command, tmp_path, 'private-worker', 'bind')
    assert str(tmp_path) in options[1]
    assert export_storage(command, 'private-worker', tmp_path, state)
    release_storage(command, state, True)
    command.assert_not_called()
