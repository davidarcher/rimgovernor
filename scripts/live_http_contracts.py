"""Read-only checks of representative full-contract responses against a loaded game."""
import argparse
import asyncio
import json
from datetime import datetime, timezone
from pathlib import Path

import httpx

from rimbot.http_contract import HttpContractClient, OPERATIONS


async def run(url):
    report = {'time': datetime.now(timezone.utc).isoformat(), 'base_url': url, 'mutations': 0, 'checks': []}
    async with httpx.AsyncClient(base_url=url, timeout=20) as http:
        client = HttpContractClient(http)
        maps = await client.get_v1_maps()
        if not maps.data:
            raise RuntimeError('Load a colony before running these read-only checks.')
        map_id = maps.data[0].id
        report['map_id'] = map_id
        report['checks'].append({'path': '/api/v1/maps', 'passed': True})
        for path in ['game/state', 'colonists', 'colonists/detailed', 'map/things', 'map/rooms',
                'map/zones', 'map/buildings', 'resources/summary', 'resources/stored',
                'resources/storages/summary', 'research/tree', 'camera/stream/status',
                'ui/alerts', 'work-list', 'traders/defs', 'def/all', 'map/animals']:
            operation_id = next(n for n, o in OPERATIONS.items() if o['path'] == '/api/v1/' + path and o['method'] == 'GET')
            op = OPERATIONS[operation_id]
            query = {'map_id': map_id} if 'map_id' in op['query_schema']['properties'] else {}
            if path == 'def/all':
                query = {'include': 'things_defs,terrain_defs'}
            try:
                result = await client._call(operation_id, query=query)
                report['checks'].append({'path': op['path'], 'passed': True, 'model': type(result).__name__})
            except Exception as error:
                report['checks'].append({'path': op['path'], 'passed': False, 'error': str(error)[:2000]})
    destination = Path('.rimbot/http-contract-tests') / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S')
    destination.mkdir(parents=True, exist_ok=True)
    (destination/'report.json').write_text(json.dumps(report, indent=2) + '\n')
    failures = [c for c in report['checks'] if not c['passed']]
    print(f'{len(report["checks"])-len(failures)}/{len(report["checks"])} read-only checks passed. {destination}/report.json')
    if failures:
        raise SystemExit(1)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--url', default='http://127.0.0.1:8765')
    asyncio.run(run(parser.parse_args().url))
