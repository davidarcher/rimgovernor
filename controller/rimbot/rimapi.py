import asyncio
import copy
import json
import time
import httpx


class APIError(RuntimeError):
    pass


def unwrap(body):
    if isinstance(body, dict):
        if body.get('success', body.get('Success')) is False:
            raise APIError(str(body.get('errors') or body.get('error') or body.get('Error') or body))
        if 'data' in body:
            return body['data']
        if 'Data' in body:
            return body['Data']
    return body


class RimAPI:
    def __init__(self, url, catalog, transport=None):
        self.catalog = catalog
        self.http = httpx.AsyncClient(base_url=url, timeout=20, transport=transport, trust_env=False)
        self.lock = asyncio.Lock()
        self.cache = {}
        self.read_cache = {}

    def invalidate(self, definitions=False):
        self.read_cache.clear()
        if definitions:
            self.cache.clear()

    async def close(self):
        await self.http.aclose()

    async def request(self, method, path, *, params=None, body=None, cache_ttl=0):
        # RIMAPI executes on the game thread. Avoid piling concurrent requests
        # onto that thread; do not retry uncertain writes.
        async with self.lock:
            key=(path,json.dumps(params or {},sort_keys=True),json.dumps(body,sort_keys=True))
            cached=self.read_cache.get(key)
            if method=='GET' and cache_ttl and cached and time.monotonic()-cached[0]<cache_ttl:
                return copy.deepcopy(cached[1])
            if method!='GET':
                self.invalidate()
            try:
                r = await self.http.request(method, path, params=params, json=body)
                r.raise_for_status()
                result=unwrap(r.json())
                if method=='GET':
                    if len(self.read_cache)>512:
                        self.read_cache.clear()
                    self.read_cache[key]=(time.monotonic(),copy.deepcopy(result))
                return result
            except httpx.HTTPStatusError as e:
                raise APIError(f'RIMAPI {e.response.status_code}: {e.response.text[:800]}') from e
            except (httpx.HTTPError, ValueError) as e:
                raise APIError(f'RIMAPI unavailable or invalid response: {e}') from e

    async def discover(self):
        self.catalog.discover(await self.request('GET', '/api/v1/docs', params={'format':'json'}))

    async def call(self, name, args, *, write=None, fresh=False):
        e = self.catalog.validate(name, args, write)
        cache_key = (name, json.dumps(args, sort_keys=True))
        # Definition data changes at a mod/session boundary, not every review.
        definition = '/def/' in e['path'] or name in ('get_work_list', 'get_time_assignments')
        if definition and not fresh and cache_key in self.cache:
            return self.cache[cache_key]
        query_keys = e.get('query_keys', [])
        params = args if e['transport'] == 'query' else {k:v for k,v in args.items() if k in query_keys}
        body = {k:v for k,v in args.items() if k not in query_keys} if e['transport'] == 'json' else None
        params = {k: str(v).lower() if isinstance(v, bool) else v for k,v in params.items()}
        data = await self.request(e['method'], e['path'], params=params, body=body)
        if definition:
            self.cache[cache_key] = data
        return data


def rows(value, key=None):
    if isinstance(value, list):
        return value
    if isinstance(value, dict):
        if key and isinstance(value.get(key), list):
            return value[key]
        lists = [v for v in value.values() if isinstance(v, list)]
        if len(lists) == 1:
            return lists[0]
    return []


def compact(value, limit=18000):
    """Valid JSON paging beats truncating a giant JSON string mid-object."""
    raw = json.dumps(value, separators=(',', ':'), ensure_ascii=False)
    if len(raw) <= limit:
        return value
    if isinstance(value, list):
        kept = []
        size = 0
        for item in value:
            n = len(json.dumps(item))
            if kept and size+n > limit:
                break
            if n > limit:
                item = compact(item, limit)
            kept.append(item)
            size += n
        return {'items': kept, 'total': len(value), 'shown': len(kept), 'more': True}
    if isinstance(value, dict):
        return {k: compact(v, max(300, limit // max(1,len(value)))) for k,v in value.items()}
    return str(value)[:limit] + '… [shortened]'


async def snapshot(api, map_id=None):
    game = await api.call('get_game_state', {}, fresh=True)
    maps = rows(await api.call('get_maps', {}, fresh=True))
    if not maps or game.get('program_state') not in (None, 'Playing'):
        raise APIError('Open a colony in RimWorld to connect.')
    selected = next((m for m in maps if m['id'] == map_id), None) if map_id is not None else next((m for m in maps if m.get('is_player_home')), maps[0])
    if selected is None:
        raise APIError('Selected map is no longer loaded. Choose a map in Settings.')
    mid = selected['id']
    out = {'game': game, 'map': selected, 'maps': maps, 'observed_at': time.time(), 'errors': {}}
    for key, name in [('pawns','get_colonists_detailed'), ('resources','get_resources_summary'),
                      ('storage','get_resources_stored'), ('buildings','get_map_buildings'),
                      ('rooms','get_map_rooms'), ('zones','get_map_zones'), ('alerts','get_ui_alerts'),
                      ('research','get_research_summary'), ('weather','get_map_weather'),
                      ('power','get_map_power_info'), ('farm','get_map_farm_summary')]:
        try:
            endpoint = api.catalog.get(name, False)
            args = {'map_id': mid} if 'map_id' in endpoint['schema']['properties'] else {}
            out[key] = await api.call(name, args, fresh=True)
        except (APIError, ValueError) as e:
            out['errors'][key] = str(e)
    if 'pawns' not in out:
        raise APIError('Cannot observe colonists: '+out['errors'].get('pawns','unknown error'))
    return out
