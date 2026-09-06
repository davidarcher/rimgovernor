async def test_initial_pause_and_resume(colony):
    rt,game=colony
    rt.mode='automate'
    await rt.pause_initial_planning()
    assert game.paused
    await rt.resume_initial_planning()
    assert not game.paused

async def test_preserve_player_pause_and_other_colony(colony):
    rt,game=colony
    rt.mode='automate';game.paused=True
    await rt.pause_initial_planning();await rt.resume_initial_planning()
    assert game.paused
    game.paused=False
    await rt.pause_initial_planning()
    game.session='another-colony'
    await rt.resume_initial_planning()
    assert game.paused
