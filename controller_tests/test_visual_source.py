import io
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from PIL import Image
from rimbot.visual_source import detail_frame, retain_source, read_source
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.store import Store


def pixels():
    image = Image.new('RGB', (320, 240), 'green')
    image.paste('red', (160, 0, 320, 240))
    stream = io.BytesIO(); image.save(stream, format='PNG')
    return stream.getvalue()


def test_detail_uses_source_pixels_and_exact_bounds(tmp_path):
    import base64
    data = pixels(); source = retain_source(tmp_path, data)
    url, bounds = detail_frame(data, dict(left=.5, top=0, right=1, bottom=1))
    image = Image.open(io.BytesIO(base64.b64decode(url.split(',')[1])))
    assert image.size == (160, 240) and image.getpixel((0, 0)) == (255, 0, 0)
    assert bounds == dict(left=.5, top=0, right=1, bottom=1)
    assert read_source(tmp_path, source) == data
    (tmp_path/'visual-sources'/(source['image_sha256']+'.png')).write_bytes(b'changed')
    with pytest.raises(ValueError, match='identity'): read_source(tmp_path, source)


def test_invalid_images_focus_and_paths_rejected(tmp_path):
    with pytest.raises(ValueError): retain_source(tmp_path, b'not png')
    with pytest.raises(ValueError): detail_frame(pixels(), dict(left=0, top=0, right=.01, bottom=.01))
    with pytest.raises(ValueError): read_source(tmp_path, {'image_sha256':'../secret'})


@pytest.mark.asyncio
async def test_camera_movement_during_capture_is_not_undone_or_retained(tmp_path):
    rt = BridgeRuntime(Store(tmp_path/'state.sqlite'), tmp_path)
    path = tmp_path/'capture.png'; path.write_bytes(pixels())
    cameras = iter([dict(mapId=1, mapPosition={'x':1,'z':2}, rootSize=12),
                    dict(mapId=1, mapPosition={'x':2,'z':2}, rootSize=12)])
    async def call(name, **kwargs):
        return SimpleNamespace(structuredContent=next(cameras) if name.endswith('get_camera_state') else {'path':str(path)})
    rt.bridge = SimpleNamespace(call=AsyncMock(side_effect=call))
    try:
        with pytest.raises(ValueError, match='camera changed'): await rt._capture_visual_source()
        assert not (tmp_path/'visual-sources').exists()
        assert not any('set_camera' in call.args[0] or 'move_camera' in call.args[0] for call in rt.bridge.call.call_args_list)
    finally: rt.store.close()


@pytest.mark.asyncio
async def test_source_endpoint_never_substitutes_current_camera(tmp_path):
    import httpx
    from rimbot.bridge_server import create_app
    data = pixels(); source = retain_source(tmp_path, data)
    rt = SimpleNamespace(root=tmp_path, advice={'report':{'source':source}}, camera_bytes=b'new camera')
    app = create_app(rt); app.state.rt = rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app),base_url='http://testserver') as client:
        response = await client.get('/api/visual-reviews/report/source')
        assert response.status_code == 200 and response.content == data
        assert (await client.get('/api/visual-reviews/missing/source')).status_code == 404
        rt.advice.clear()
        assert (await client.get('/api/visual-reviews/report/source')).status_code == 404
