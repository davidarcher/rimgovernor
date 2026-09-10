"""Immutable source pixels and detail framing without native camera writes."""
import base64
import hashlib
import io
from pathlib import Path

from PIL import Image

from .visual_review import Region


def decode_png(data):
    if not data.startswith(b'\x89PNG\r\n\x1a\n') or len(data) > 12*1024*1024:
        raise ValueError('Expected a bounded native PNG screenshot')
    with Image.open(io.BytesIO(data)) as image:
        if image.width * image.height > 3840*2160:
            raise ValueError('Native screenshot exceeds the pixel budget')
        image.load()
        return image.convert('RGB')


def image_url(data):
    return 'data:image/png;base64,' + base64.b64encode(data).decode()


def retain_source(root, data):
    image = decode_png(data)
    digest = hashlib.sha256(data).hexdigest()
    directory = Path(root)/'visual-sources'
    directory.mkdir(parents=True, exist_ok=True)
    path = directory/(digest+'.png')
    if not path.exists():
        path.write_bytes(data)
    return {'image_sha256': digest, 'width': image.width, 'height': image.height}


def detail_frame(data, focus=None):
    image = decode_png(data)
    region = Region.model_validate(focus or dict(left=.25, top=.25, right=.75, bottom=.75))
    box = (int(region.left*image.width), int(region.top*image.height),
           int(region.right*image.width), int(region.bottom*image.height))
    if box[2]-box[0] < 32 or box[3]-box[1] < 32:
        raise ValueError('Detail frame must contain at least 32 pixels per side')
    crop = image.crop(box)
    stream = io.BytesIO()
    crop.save(stream, format='PNG')
    # Rounded pixel edges are the authoritative transform back to the source.
    bounds = dict(left=box[0]/image.width, top=box[1]/image.height,
                  right=box[2]/image.width, bottom=box[3]/image.height)
    return image_url(stream.getvalue()), bounds


def read_source(root, source):
    digest = source.get('image_sha256', '')
    if len(digest) != 64 or any(c not in '0123456789abcdef' for c in digest):
        raise ValueError('Invalid source image identity')
    try:
        data = (Path(root)/'visual-sources'/(digest+'.png')).read_bytes()
    except FileNotFoundError:
        raise ValueError('Source image is no longer available') from None
    if hashlib.sha256(data).hexdigest() != digest:
        raise ValueError('Source image failed its identity check')
    return data
