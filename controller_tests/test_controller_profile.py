from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock
import pytest
from scripts.controller_profile import ControllerProfile


async def test_profile_preserves_results_errors_and_restores_boundaries(tmp_path):
    rt = SimpleNamespace(persist=Mock(return_value='saved'), review=AsyncMock(return_value='reviewed'),
        sync_identity=AsyncMock(), native=AsyncMock(side_effect=ValueError('native refusal')),
        hands=SimpleNamespace(advance=AsyncMock(), preflight_shell=AsyncMock()))
    persist, review, native = rt.persist, rt.review, rt.native
    profile = ControllerProfile(rt, tmp_path)
    profile.start()
    try:
        assert rt.persist() == 'saved'
        assert await rt.review() == 'reviewed'
        with pytest.raises(ValueError, match='native refusal'):
            await rt.native()
    finally:
        report = profile.stop()
    assert rt.persist is persist and rt.review is review and rt.native is native
    assert all(report['boundaries'][name]['calls'] == 1 for name in ('persist', 'review', 'native'))
    assert (tmp_path/'controller.pstats').stat().st_size > 0
