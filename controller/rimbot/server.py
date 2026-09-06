import asyncio
import base64
import json
import os
import time
import uuid
from contextlib import asynccontextmanager
from pathlib import Path
from urllib.parse import urlparse
from fastapi import FastAPI, HTTPException, Request, WebSocket, WebSocketDisconnect
from fastapi.responses import FileResponse, JSONResponse, Response, StreamingResponse
from fastapi.staticfiles import StaticFiles
from starlette.middleware.trustedhost import TrustedHostMiddleware
from .config import DATA_DIR, Settings
from .runtime import Runtime
from .store import Store
from .video import Video


def create_app(runtime=None):
    @asynccontextmanager
    async def lifespan(app):
        if runtime is None:
            app.state.rt = Runtime(Store(DATA_DIR/'colony.sqlite'))
        else:
            app.state.rt = runtime
        app.state.video = Video(app.state.rt)
        await app.state.rt.start()
        yield
        await app.state.video.close()
        await app.state.rt.stop()
        if runtime is None:
            app.state.rt.store.close()

    app = FastAPI(title='RimBot', lifespan=lifespan)

    @app.get('/api/rimapi/openapi.json', include_in_schema=False)
    async def rimapi_contract():
        # Available even while the game is closed; this is the pinned native contract.
        return FileResponse(Path(__file__).parent / 'data/rimapi.openapi.json', media_type='application/json')

    app.add_middleware(TrustedHostMiddleware, allowed_hosts=['localhost','127.0.0.1','[::1]','testserver'])

    @app.middleware('http')
    async def local_ui(request: Request, call_next):
        if request.method not in ('GET','HEAD'):
            origin = request.headers.get('origin')
            same_origin = not origin or urlparse(origin).netloc == request.headers.get('host')
            if request.headers.get('x-rimbot') != '1' or not same_origin:
                return JSONResponse({'detail':'Use the local RimBot dashboard.'},status_code=403)
        result = await call_next(request)
        result.headers['X-Content-Type-Options'] = 'nosniff'
        result.headers['Referrer-Policy'] = 'no-referrer'
        result.headers['Content-Security-Policy'] = "default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'"
        return result

    @app.exception_handler(ValueError)
    async def invalid(request, exc):
        return JSONResponse({'detail':str(exc)}, status_code=400)

    @app.get('/api/state')
    async def state(request: Request):
        return request.app.state.rt.public()

    @app.get('/api/health')
    async def health():
        return {'service':'rimbot','version':'0.2.0','source_root':str(Path(__file__).resolve().parents[2]),'pid':os.getpid()}

    @app.post('/api/control')
    async def control(request: Request):
        await request.app.state.rt.set_mode((await request.json())['mode'])
        return {'ok':True}

    @app.post('/api/steer')
    async def steer(request: Request):
        await request.app.state.rt.steer((await request.json())['text'])
        return {'ok':True}

    @app.post('/api/strategy')
    async def strategy(request: Request):
        request.app.state.rt.launch_review(strategy=True)
        return {'ok':True}

    @app.post('/api/settings')
    async def settings(request: Request):
        await request.app.state.video.close()
        await request.app.state.rt.configure(Settings.model_validate(await request.json()))
        return {'ok':True}

    @app.post('/api/goals')
    async def goals(request: Request):
        rt = request.app.state.rt
        data = await request.json()
        text = str(data.get('text','')).strip()
        if not text or len(text)>500:
            raise ValueError('Give the goal a short description.')
        rt.memory['goals'].append({'id':uuid.uuid4().hex[:12],'text':text,'status':'active'})
        rt.persist()
        if rt.mode == 'automate':
            await rt.steer('New objective: '+text)
        return {'ok':True}

    @app.delete('/api/goals/{goal_id}')
    async def delete_goal(goal_id: str, request: Request):
        rt = request.app.state.rt
        rt.memory['goals'] = [g for g in rt.memory['goals'] if g['id'] != goal_id]
        rt.persist()
        await rt.cancel()
        return {'ok':True}

    @app.delete('/api/work/{work_id}')
    async def dismiss(work_id: str, request: Request):
        rt = request.app.state.rt
        for work in rt.memory['work']:
            if work['id'] == work_id:
                work['status'] = 'dismissed'
                work['detail'] = 'Removed from tracking; game orders unchanged'
        rt.persist()
        return {'ok':True}

    @app.get('/api/capabilities')
    async def capabilities(request: Request):
        rt = request.app.state.rt
        return {'revision':rt.catalog.revision, 'endpoints':rt.catalog.listing()}

    @app.get('/api/events')
    async def events(request: Request):
        rt = request.app.state.rt
        queue = asyncio.Queue(maxsize=100)
        rt.listeners.add(queue)
        async def stream():
            try:
                yield 'event: ready\ndata: {}\n\n'
                while not await request.is_disconnected():
                    try:
                        event = await asyncio.wait_for(queue.get(),15)
                        yield 'data: '+json.dumps(event)+'\n\n'
                    except TimeoutError:
                        yield ': keepalive\n\n'
            finally:
                rt.listeners.discard(queue)
        return StreamingResponse(stream(),media_type='text/event-stream',headers={'Cache-Control':'no-cache'})

    @app.post('/api/camera')
    async def camera(request: Request):
        rt = request.app.state.rt
        if not rt.connected:
            raise ValueError('Connect a colony before capturing its camera.')
        result = await rt.api.call('post_camera_screenshot',{'format':'jpeg','quality':65,'width':1280,'height':720,'hide_ui':True},write=False)
        uri = result.get('image',{}).get('data_uri','')
        if not uri.startswith(('data:image/jpeg;base64,','data:image/png;base64,')):
            raise ValueError('RIMAPI returned no usable screenshot.')
        return {'image':uri,'at':time.time()}

    @app.get('/api/history')
    async def export(request: Request):
        rt = request.app.state.rt
        return Response('\n'.join(json.dumps(e) for e in rt.store.history(rt.colony,100000,include_diagnostics=True)), media_type='application/x-ndjson', headers={'Content-Disposition':'attachment; filename="rimbot-history.jsonl"'})

    @app.get('/api/strategies')
    async def strategies(request: Request, q: str = '', limit: int = 3):
        library=request.app.state.rt.strategies
        if not 1<=limit<=5:raise ValueError('limit must be 1..5')
        return {'entries':library.search(q,limit) if q else [e.model_dump() for e in library.entries]}

    @app.get('/api/semantic/schema')
    async def semantic_schema():
        from .semantic_models import ObjectiveProposal
        return ObjectiveProposal.model_json_schema()

    @app.get('/api/manager-activity')
    async def manager_activity(request: Request):
        rt=request.app.state.rt
        rows=rt.store.db.execute("SELECT id,at,kind,data FROM events WHERE colony=? AND kind IN ('model_call','model_failure','tool_result','proposal','execution','execution_plan','arbitration','escalation','action','error','model_diagnostic') ORDER BY id DESC LIMIT 300",(rt.colony,)).fetchall()
        return {'colony':rt.colony,'events':[dict(id=r[0],at=r[1],kind=r[2],**json.loads(r[3])) for r in rows]}

    @app.get('/api/diagnostics')
    async def diagnostics(request: Request):
        rt=request.app.state.rt
        rows=rt.store.db.execute("SELECT id,at,data FROM events WHERE colony=? AND kind='model_diagnostic' ORDER BY id DESC LIMIT 100",(rt.colony,)).fetchall()
        return [dict(id=r[0],at=r[1],**json.loads(r[2])) for r in rows]

    @app.websocket('/api/video')
    async def video(ws: WebSocket):
        origin = ws.headers.get('origin','')
        if urlparse(origin).netloc != ws.headers.get('host'):
            await ws.close(code=1008)
            return
        await ws.accept()
        queue = None
        try:
            queue = await ws.app.state.video.subscribe()
            # Watch disconnects even when RimWorld stops producing frames.
            disconnect = asyncio.create_task(ws.receive())
            try:
                while True:
                    frame = asyncio.create_task(queue.get())
                    done,_ = await asyncio.wait([frame,disconnect],return_when=asyncio.FIRST_COMPLETED)
                    if disconnect in done:
                        frame.cancel()
                        await asyncio.gather(frame,return_exceptions=True)
                        break
                    await ws.send_bytes(frame.result())
            finally:
                disconnect.cancel()
                await asyncio.gather(disconnect,return_exceptions=True)
        except (WebSocketDisconnect, RuntimeError):
            pass
        except Exception as e:
            await ws.send_json({'error':str(e)[:500]})
            await ws.close()
        finally:
            if queue is not None:
                await ws.app.state.video.unsubscribe(queue)

    @app.get('/rimapi/api/v1/events')
    async def game_events(request: Request):
        # One game event connection in Runtime, fan out to dashboard widgets.
        rt=request.app.state.rt
        queue=asyncio.Queue(maxsize=100)
        rt.listeners.add(queue)
        async def stream():
            try:
                yield 'event: connected\ndata: {}\n\n'
                while not await request.is_disconnected():
                    try:
                        e=await asyncio.wait_for(queue.get(),15)
                        if e.get('event_type'):
                            yield 'event: '+e['event_type']+'\ndata: '+json.dumps(e.get('data',{}))+'\n\n'
                    except TimeoutError:
                        yield ': keepalive\n\n'
            finally:
                rt.listeners.discard(queue)
        return StreamingResponse(stream(),media_type='text/event-stream')

    @app.api_route('/rimapi/api/v1/{path:path}',methods=['GET','POST','PUT','DELETE'])
    async def dashboard_proxy(path: str,request: Request):
        rt=request.app.state.rt
        route='/api/v1/'+path
        entry=next((e for e in rt.catalog.entries.values() if e['path']==route and e['method']==request.method),None)
        if entry is None:
            raise HTTPException(404,'This RIMAPI endpoint is not in the pinned contract catalog.')
        params=dict(request.query_params)
        params.pop('_',None)
        if 'map_id' in entry['schema'].get('properties',{}):
            mid=rt.observation.get('map',{}).get('id')
            if mid is None:
                raise HTTPException(503,'Load a colony before inspecting its map.')
            # Upstream dashboard hardcoded map 0. Use the actually selected map.
            params['map_id']=str(mid)
        body=await request.json() if await request.body() else None
        if request.method!='GET':
            player_controls={'/api/v1/game/speed','/api/v1/select','/api/v1/deselect','/api/v1/open-tab','/api/v1/camera/change/position','/api/v1/camera/change/zoom'}
            if route not in player_controls:
                args={**params,**(body or {})}
                for k,v in list(args.items()):
                    prop=entry['schema'].get('properties',{}).get(k,{})
                    if prop.get('type')=='integer' and isinstance(v,str): args[k]=int(v)
                    if prop.get('type')=='boolean' and isinstance(v,str): args[k]=v.lower()=='true'
                rt.catalog.validate(entry['name'],args,True)
            await rt.cancel()
        try:
            result=await rt.api.request(request.method,route,params=params,body=body,cache_ttl=2 if request.method=='GET' else 0)
        except RuntimeError as e:
            raise HTTPException(502,str(e)) from e
        return {'success':True,'data':result,'errors':[],'warnings':[]}

    static = Path(__file__).parent/'static'
    app.mount('/assets',StaticFiles(directory=static/'assets',check_dir=False),name='assets')

    @app.get('/')
    async def home():
        return FileResponse(static/'index.html')

    return app
