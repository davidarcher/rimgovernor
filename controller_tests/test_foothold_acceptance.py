from argparse import ArgumentTypeError
import pytest
from scripts.deterministic_foothold import StabilityWindow, stability_days

GATES={name:True for name in ('sleeping','shelter','food','production','storage','cooking','temperature','power','medical','defense','work')}


def test_stability_counts_observed_game_ticks_not_repeated_paused_samples():
    window=StabilityWindow(120000)
    for _ in range(100): assert not window.observe(100,'FOOTHOLD_STABLE',GATES,0)
    for tick in range(3100,120100,3000): assert not window.observe(tick,'FOOTHOLD_STABLE',GATES,0)
    assert window.observe(120100,'FOOTHOLD_STABLE',GATES,0)
    assert window.stable_ticks==120000


def test_loss_and_recovery_between_samples_cannot_credit_old_window():
    window=StabilityWindow(6000)
    assert not window.observe(100,'FOOTHOLD_STABLE',GATES,0)
    assert not window.observe(3100,'FOOTHOLD_STABLE',GATES,0)
    assert not window.observe(6100,'FOOTHOLD_STABLE',GATES,1)
    assert window.window_start_tick==6100 and window.stable_ticks==0
    assert window.observe(12100,'FOOTHOLD_STABLE',GATES,1)


def test_unknown_gate_gap_and_rewind_cannot_certify_stability():
    window=StabilityWindow(6000)
    window.observe(100,'FOOTHOLD_STABLE',GATES,0)
    assert not window.observe(3100,'FOOTHOLD_STABLE',dict(GATES,food=None),0)
    assert not window.observe(6100,'FOOTHOLD_STABLE',GATES,0)
    assert not window.observe(18100,'FOOTHOLD_STABLE',GATES,0)
    assert window.gap_resets==1 and window.stable_ticks==0
    with pytest.raises(ValueError,match='backward'):window.observe(100,'FOOTHOLD_STABLE',GATES,0)


def test_establishment_mode_still_requires_every_native_gate():
    window=StabilityWindow(0)
    assert not window.observe(100,'FOOTHOLD_STABLE',{},0)
    assert window.observe(100,'FOOTHOLD_STABLE',GATES,0)


@pytest.mark.parametrize('value',['-1','nan','inf','31'])
def test_invalid_stability_duration(value):
    with pytest.raises(ArgumentTypeError):stability_days(value)
