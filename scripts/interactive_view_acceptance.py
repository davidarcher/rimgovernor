"""Serve the real dashboard with read-only native acceptance diagnostics in a private worker.

This runner is deliberately separate from the production entry point. Browser
operations use production endpoints; the harness records independent native reads.
"""
import argparse
import json
import time
import os
from pathlib import Path

from fastapi import Request
import uvicorn
from rimbot.bridge_server import create_app
from rimbot.headless import rendered_headless_mismatch


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--port', type=int, default=8787)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    app = create_app()
    allowed = {'home/status', 'home/colony_identity', 'home/colony_facts',
               'rimworld/get_camera_state', 'rimworld/get_selection_semantics',
               'rimworld/get_ui_layout', 'rimworld/list_architect_categories',
               'rimworld/list_architect_designators', 'rimworld/get_map_target_info',
               'rimworld/get_context_menu_options', 'rimworld/get_ui_state', 'home/list_zones'}

    def record(value):
        with (args.output / 'native.jsonl').open('a', encoding='utf8') as stream:
            stream.write(json.dumps(dict(at=time.time(), **value), default=str) + '\n')

    @app.post('/test/read')
    async def read(request: Request):
        body = await request.json()
        tool = body['tool']
        if tool not in allowed:
            raise ValueError('Acceptance read is not allowlisted')
        rt = request.app.state.rt
        async with rt.lock:
            result = (await rt.bridge.call(tool, **body.get('arguments', {}))).structuredContent
        record({'tool': tool, 'result': result})
        return result

    @app.get('/test/discover')
    async def discover(request: Request):
        rt = request.app.state.rt
        result = (await rt.bridge.names(query=request.query_params.get('query', 'input'))).structuredContent
        record({'discovery': result})
        return result

    @app.get('/test/detail')
    async def detail(request: Request):
        rt = request.app.state.rt
        result = (await rt.bridge.detail(request.query_params['tool'])).structuredContent
        record({'schema': result})
        return result

    @app.get('/test/cost')
    async def cost(request: Request):
        rows = []
        for directory in Path('/proc').iterdir():
            if not directory.name.isdigit():
                continue
            try:
                name = (directory / 'comm').read_text().strip()
                if name not in ('RimWorldLinux', 'python', 'Xvfb'):
                    continue
                fields = (directory / 'stat').read_text().split(') ', 1)[1].split()
                rows.append({'pid': int(directory.name), 'name': name,
                    'cpuSeconds': (int(fields[11]) + int(fields[12])) / os.sysconf('SC_CLK_TCK'),
                    'residentBytes': int(fields[21]) * os.sysconf('SC_PAGE_SIZE')})
            except (FileNotFoundError, PermissionError):
                continue
        result = {'processes': rows, 'video': request.app.state.video.status()}
        record({'cost': result})
        return result

    @app.post('/test/load')
    async def reload(request: Request):
        rt = request.app.state.rt
        await rt.set_mode('manual')
        async with rt.lock:
            result = (await rt.bridge.call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline',
                readiness='visual', timeoutMs=90000,
                ignoreModCompatibility=rendered_headless_mismatch(Path(os.environ['RIMBOT_BRIDGE_ROOT'])))).structuredContent
            await rt.sync_identity()
        record({'load': result, 'session': rt.context_token})
        return {'session': rt.context_token}

    @app.post('/test/evidence')
    async def evidence(request: Request):
        value = await request.json()
        record({'browser': value})
        return {'recorded': True}

    uvicorn.run(app, host='0.0.0.0', port=args.port)


if __name__ == '__main__':
    main()
