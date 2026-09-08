import importlib.util
from pathlib import Path
from unittest.mock import AsyncMock

import pytest

spec = importlib.util.spec_from_file_location('headless_iterations', Path(__file__).parents[1]/'scripts/headless_iterations.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def trial(n, outcome='usable_foothold', **overrides):
    return dict(iteration=n, outcome=outcome, revision='fixed', model='local', direction='shelter', **overrides)


def test_streak_uses_trial_order_and_resets_on_failure_or_interruption():
    results = [trial(3), trial(1), trial(2, 'not_usable'), trial(4)]
    assert module.consecutive_passes(results) == 2
    assert module.consecutive_passes([trial(1), trial(2, interrupted=True), trial(3)]) == 1


def test_changed_build_model_or_objective_cannot_form_a_fixed_campaign_streak():
    for field in ('revision', 'model', 'direction'):
        results = [trial(1), trial(2), trial(3)]
        results[1][field] = 'changed'
        assert module.consecutive_passes(results) == 1
    assert module.consecutive_passes([trial(1), trial(2), trial(3)]) == 3


def test_cleanup_failure_breaks_even_legacy_uninterrupted_streak():
    results = [trial(1), trial(2, interrupted=False, cleanup_error='stop timed out'), trial(3)]
    assert module.consecutive_passes(results) == 1
    assert module.consecutive_passes([trial(i, cleanup_error='game still running') for i in (1, 2, 3)]) == 0


def test_missing_and_duplicate_iterations_are_not_consecutive_successes():
    assert module.consecutive_passes([trial(1), trial(3), trial(5)]) == 1
    assert module.consecutive_passes([trial(1), trial(3), trial(4)]) == 2
    assert module.consecutive_passes([trial(1), trial(1), trial(1)]) == 0
    assert module.consecutive_passes([trial(1), trial(2), trial(3), trial(3)]) == 2


def test_fixed_build_acceptance_requires_matching_manifests():
    legacy = [trial(1), trial(2), trial(3)]
    assert module.consecutive_passes(legacy, require_manifest=True) == 0
    assert module.consecutive_passes([trial(1)]) == 1
    fixed = [trial(i, manifest_fingerprint='same-inputs') for i in (1, 2, 3)]
    assert module.consecutive_passes(fixed, require_manifest=True) == 3
    fixed[1]['manifest_fingerprint'] = 'different-dll'
    assert module.consecutive_passes(fixed, require_manifest=True) == 1


@pytest.mark.asyncio
@pytest.mark.parametrize('outcome,useful', [('placed', True), ('already_present', False),
                                          ('preview', False), ('refused', False)])
async def test_construction_usefulness_reads_the_nested_native_receipt(monkeypatch, outcome, useful):
    response = {'receipt': {'outcome': outcome}, 'observed_after': {}}
    monkeypatch.setattr(module.BridgeRuntime, 'native', AsyncMock(return_value=response))
    rt = object.__new__(module.FastTrial)
    events = []
    rt.note = lambda *args, **kwargs: events.append(kwargs)
    assert await rt.native('home/place_building', {'dryRun': False}) is response
    assert events[-1]['outcome'] == 'returned'
    assert events[-1]['useful_order_receipt'] is useful


@pytest.mark.asyncio
async def test_ambiguous_construction_failure_is_not_useful_receipt(monkeypatch):
    monkeypatch.setattr(module.BridgeRuntime, 'native', AsyncMock(side_effect=RuntimeError('lost receipt')))
    rt = object.__new__(module.FastTrial)
    events = []
    rt.note = lambda *args, **kwargs: events.append(kwargs)
    with pytest.raises(RuntimeError, match='lost receipt'):
        await rt.native('home/place_building', {'dryRun': False})
    assert events[-1]['outcome'] == 'failed'
    assert events[-1]['useful_order_receipt'] is False
