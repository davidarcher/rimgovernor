from rimbot.strategic_state import player_directions


def test_game_events_and_model_summaries_cannot_displace_player_objective():
    chat = [{'kind': 'human', 'text': 'Build shelter', 'revision': 1}]
    chat.extend({'kind': kind, 'text': 'Observed activity', 'revision': n}
                for n in range(2, 42) for kind in ('clock_event', 'summary'))
    assert player_directions(chat)['items'] == [{'revision': 1, 'text': 'Build shelter'}]
    chat.append({'kind': 'human', 'text': 'Use the existing ruins', 'revision': 42})
    assert [r['text'] for r in player_directions(chat)['items']] == [
        'Build shelter', 'Use the existing ruins']


def test_direction_limit_keeps_latest_corrections_and_reports_omissions():
    chat = [{'kind': 'human', 'text': str(n), 'revision': n} for n in range(15)]
    result = player_directions(chat)
    assert [r['revision'] for r in result['items']] == list(range(3, 15))
    assert result['omitted'] == 3
    assert player_directions([])['items'] == []
