"""Bounded, on-demand access to checked-in strategy guidance, not game state."""
from copy import deepcopy
from functools import lru_cache
import json
from pathlib import Path
import re


_ROOT = Path(__file__).parent / 'data' / 'strategies'
_STOP = set('a an and are can do for how i in is it of on or the to with should'.split())


def _words(text):
    return set(re.findall(r'[a-z0-9]+', text.lower())) - _STOP


@lru_cache(maxsize=1)
def _cards():
    cards = {}
    for path in sorted(_ROOT.glob('*.json')):
        card = json.loads(path.read_text(encoding='utf-8'))
        if card['id'] != path.stem or card['id'] in cards:
            raise ValueError('Invalid strategy catalog ID')
        if len(json.dumps(card, ensure_ascii=False)) > 12000:
            raise ValueError(f'Strategy card exceeds retrieval budget: {path.stem}')
        cards[card['id']] = card
    return cards


def search_knowledge(query: str):
    if not isinstance(query, str) or not query.strip() or len(query) > 300:
        raise ValueError('Provide a strategy question of 1–300 characters')
    terms = _words(query)
    ranked = []
    for card in _cards().values():
        title = _words(card['title'] + ' ' + card['id'])
        tags = _words(' '.join(card['tags']))
        body = _words(' '.join(card.get('applies_when', []) + card.get('approach', [])))
        score = len(terms & title) * 4 + len(terms & tags) * 3 + len(terms & body)
        if score:
            ranked.append((score, card))
    ranked.sort(key=lambda item: (-item[0], item[1]['id']))
    return {'source': 'local_strategy_library', 'matches': [
        {'id': card['id'], 'title': card['title'], 'applies_when': card['applies_when']}
        for _, card in ranked[:3]],
        'more_matches': len(ranked) > 3,
        'note': 'Read a matching card for guidance and dated sources. No match is not proof that no strategy exists.'}


def read_knowledge(id: str):
    # Lookup only: model input is never interpreted as a file path.
    if not isinstance(id, str) or id not in _cards():
        raise ValueError('Unknown strategy ID; search_knowledge first')
    return {'source': 'local_strategy_library', 'card': deepcopy(_cards()[id]),
            'note': 'Cached guidance, not live game facts or a fresh wiki check. Verify eligibility, resources and geometry through native tools.'}
