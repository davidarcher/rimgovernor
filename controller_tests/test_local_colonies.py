from types import SimpleNamespace
from unittest.mock import Mock

import httpx
import pytest

from rimgovernor import local_colonies as directory


def worker(**changes):
    value = {'Id': 'abc', 'Name': '/winter-test',
             'Config': {'Entrypoint': ['python', '-m', 'rimgovernor.container_worker'],
                        'Env': ['RIMGOVERNOR_DISPLAY=xvfb']},
             'State': {'Running': True, 'StartedAt': '2026-09-10T00:00:00Z'},
             'NetworkSettings': {'Ports': {'8787/tcp': [{'HostIp': '127.0.0.1', 'HostPort': '45678'}]}}}
    value.update(changes)
    return value


def test_worker_identity_and_local_ports():
    entries = directory.worker_entries([worker(), worker(Config={'Entrypoint': ['pytest']}),
                                        worker(State={'Running': False})])
    assert len(entries) == 1
    assert entries[0]['url'] == 'http://127.0.0.1:45678/'
    assert entries[0]['name'] == 'winter-test'
    assert entries[0]['display'] == 'xvfb'


@pytest.mark.parametrize('binding', [None, [], [{'HostIp': '192.168.1.3', 'HostPort': '45678'}],
                                    [{'HostIp': '127.0.0.1', 'HostPort': '99999'}]])
def test_unpublished_or_nonlocal_workers_remain_visible_without_link(binding):
    entries = directory.worker_entries([worker(NetworkSettings={'Ports': {'8787/tcp': binding}})])
    assert len(entries) == 1
    assert entries[0]['url'] is None


def test_labels_support_named_workers():
    entries = directory.worker_entries([worker(Config={'Labels': {'io.rimgovernor.colony': '1',
                                                                  'io.rimgovernor.colony.name': 'Winter'}})])
    assert entries[0]['name'] == 'Winter'


def test_docker_failure_is_distinct_from_no_workers(monkeypatch):
    monkeypatch.setattr(directory.Path, 'is_socket', lambda self: False)
    monkeypatch.setattr(directory, 'docker_executable', Mock(side_effect=OSError()))
    assert directory.colonies()['colonies'] is None
    monkeypatch.setattr(directory, 'docker_executable', lambda: 'docker')
    run = Mock(return_value=SimpleNamespace(stdout=''))
    monkeypatch.setattr(directory.subprocess, 'run', run)
    assert directory.colonies() == {'colonies': [], 'error': None}
    assert run.call_args.args[0] == ['docker', 'ps', '-q']


def test_socket_discovery_only_reads_and_tolerates_finished_workers(monkeypatch):
    monkeypatch.setattr(directory.Path, 'is_socket', lambda self: True)
    requests = []
    def respond(request):
        requests.append((request.method, request.url.path))
        if request.url.path == '/containers/json':
            return httpx.Response(200, json=[{'Id': 'abc'}, {'Id': 'finished'}])
        if request.url.path == '/containers/finished/json':
            return httpx.Response(404)
        return httpx.Response(200, json=worker())
    client = httpx.Client(transport=httpx.MockTransport(respond), base_url='http://docker')
    monkeypatch.setattr(directory.httpx, 'Client', lambda **kwargs: client)
    result = directory.colonies()
    assert result['error'] is None
    assert result['colonies'][0]['url'] == 'http://127.0.0.1:45678/'
    assert requests == [('GET', '/containers/json'), ('GET', '/containers/abc/json'),
                        ('GET', '/containers/finished/json')]


@pytest.mark.asyncio
async def test_directory_serves_without_runtime(monkeypatch):
    monkeypatch.setattr(directory.Path, 'is_socket', lambda self: False)
    monkeypatch.setattr(directory, 'docker_executable', Mock(side_effect=OSError()))
    app = directory.create_directory_app()
    assert not hasattr(app.state, 'rt')
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver') as client:
        assert (await client.get('/')).headers['location'] == '/colonies'
        response = await client.get('/api/colonies')
        assert response.status_code == 200 and response.json()['error']
        assert response.headers['cache-control'] == 'no-store'
        assert (await client.post('/api/control')).status_code == 404
