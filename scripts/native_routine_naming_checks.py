"""Read-only naming census assertions around disposable native dialog setup."""
import json


async def verify_naming(wire, call, identity, output):
    request = {'scope': {'expectedIdentity': identity}, 'planning': True,
               'requestedDefinitionNames': ['Plant_Rice']}
    async def read(label, expected):
        reply = await wire('naming-' + label, 'observations_read_colony_facts', request)
        facts = reply['observed']
        (output / ('naming-' + label + '.json')).write_text(json.dumps(reply), encoding='utf8')
        naming_issues = [r for r in facts.get('issues', []) if r['field'] == 'naming']
        if expected:
            assert facts['naming'] == {'windowId': opened['windowId']}
            assert not naming_issues
        else:
            assert 'naming' not in facts
            assert len(naming_issues) == 1
            assert naming_issues[0]['unavailable']['reason'] == 'UNAVAILABLE_REASON_NOT_APPLICABLE'
        return facts['context']
    before = await read('absent', False)
    opened = await call('naming-open', 'test/modal_fixture', {'action': 'open', 'kind': 'combined'})
    assert opened['success']
    assert await read('present', True) == before
    replacement = await call('naming-replace', 'test/modal_fixture', {'action': 'replace'})
    assert replacement['success']
    assert await read('replaced', False) == before
    return {'absent': True, 'present': True, 'replacement_is_not_naming': True,
            'identity_tick_unchanged': True, 'names_confirmed': False}
