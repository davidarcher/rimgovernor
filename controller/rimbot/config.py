from pathlib import Path
from urllib.parse import urlparse
from pydantic import BaseModel, Field, field_validator


class Settings(BaseModel):
    rimapi_url: str = 'http://127.0.0.1:8765'
    model_url: str = 'http://127.0.0.1:1234/v1'
    model: str = 'qwen/qwen3.5-9b'
    reasoning: bool = True
    max_output_tokens: int = Field(default=8192, ge=1024, le=131072)
    context_chars: int = Field(default=60000, ge=16000, le=400000)
    review_ticks: int = Field(default=15000, ge=600, le=60000)
    poll_seconds: float = Field(default=5, ge=2, le=60)
    map_id: int | None = None

    @field_validator('rimapi_url', 'model_url')
    @classmethod
    def local_url(cls, value):
        p = urlparse(value)
        if p.scheme != 'http' or p.hostname not in ('localhost', '127.0.0.1', '::1') or p.username or p.query or p.fragment:
            raise ValueError('Use an HTTP loopback address for the local game/model server.')
        return value.rstrip('/')


DATA_DIR = Path(__import__('os').environ.get('RIMBOT_DATA', '.rimbot')).resolve()
