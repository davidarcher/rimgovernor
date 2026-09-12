"""Compare paused canonical gear planning facts with the native upkeep read."""

import math


def audit_gear(colony, legacy):
    gear = colony['planning']['observed']['gear']
    assert gear['context'] == colony['context'], 'Gear context differs from colony'
    assert legacy['success'] and int(legacy['tick']) == int(colony['context']['tick'])
    assert legacy['mapId'] == colony['context']['identity']['mapId']
    assert gear['completeness']['page']['complete']
    assert int(gear['completeness']['returned']) == len(gear['pawns']) == colony['colonistCount']
    native = {p['pawn']: p for p in legacy['pawns']}
    typed = {p['pawn']['id']: p for p in gear['pawns']}
    assert len(native) == len(legacy['pawns']) and len(typed) == len(gear['pawns'])
    assert native.keys() == typed.keys(), 'Gear census differs'
    candidates = deficits = blocked = 0
    for pawn_id, row in typed.items():
        ref = native[pawn_id]
        assert row['snapshot']['context'] == gear['context']
        assert row['snapshot']['entityId'] == pawn_id and row['snapshot']['token'] == ref['loadout']
        assert type(row['deficit']) is bool and row['deficit'] == ref['deficit']
        assert row.get('blocker') == ref.get('blocker')
        assert row['completeness']['page']['complete']
        actual = {c['item']['thing']['id']: c for c in row.get('candidates', [])}
        expected = {c['target']: c for c in ref.get('candidates', [])}
        assert len(actual) == len(row.get('candidates', [])) == int(row['completeness']['returned'])
        assert actual.keys() == expected.keys(), 'Eligible candidates differ'
        for target, candidate in actual.items():
            baseline = expected[target]
            assert candidate['item']['thing']['defName'] == baseline['gear']['defName']
            assert math.isclose(candidate['gain'], baseline['gain'], rel_tol=1e-6, abs_tol=1e-7)
            assert candidate['item']['apparel'] == (baseline['kind'] == 'apparel')
            assert candidate['item']['weapon'] == (baseline['kind'] == 'weapon')
        needs = lambda rows: sorted((r['defName'], r.get('stuff') or '', r['reason']) for r in rows)
        assert needs(row.get('replacementNeeds', [])) == needs(ref.get('replacementNeeds', []))
        deficits += row['deficit']
        blocked += row.get('blocker') is not None
        candidates += len(actual)
    return {'pawns': len(typed), 'deficits': deficits, 'blocked': blocked, 'candidates': candidates,
            'same_tick_native_parity': True}
