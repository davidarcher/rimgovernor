import json
import pytest
from rimgovernor.knowledge import search_knowledge, read_knowledge


@pytest.mark.parametrize('question,expected', [
    ('poor soil gravel potatoes', 'poor-soil-food'),
    ('bedroom room sizing dimensions', 'room-sizing'),
    ('first days startup landing', 'first-days'),
])
def test_practical_questions_find_relevant_cards(question, expected):
    result = search_knowledge(question)
    assert expected in [card['id'] for card in result['matches']]
    assert len(result['matches']) <= 3
    assert all('approach' not in card for card in result['matches'])


def test_read_preserves_sources_and_does_not_mutate_cache():
    result = read_knowledge('poor-soil-food')
    assert result['card']['sources'][0]['url'].startswith('https://rimworldwiki.com/')
    assert result['card']['sources'][0]['checked_on']
    result['card']['approach'].clear()
    assert read_knowledge('poor-soil-food')['card']['approach']
    assert len(json.dumps(read_knowledge('room-sizing'))) < 12500


@pytest.mark.parametrize('id', ['../../config', 'missing', '/etc/passwd'])
def test_read_rejects_paths_and_unknown_ids(id):
    with pytest.raises(ValueError):
        read_knowledge(id)


@pytest.mark.parametrize('query', ['', ' ', 'x' * 301])
def test_query_budget(query):
    with pytest.raises(ValueError):
        search_knowledge(query)


def test_no_match_is_explicit_and_old_api_is_removed():
    assert search_knowledge('zyzzyva')['matches'] == []
    assert 'post_pawn_edit_status' not in json.dumps(read_knowledge('first-days'))
