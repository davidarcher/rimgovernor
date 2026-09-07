from pathlib import Path
from urllib.parse import urlparse
from pydantic import BaseModel, Field, field_validator


class Settings(BaseModel):
    model_url: str = 'http://127.0.0.1:1234/v1'
    model: str = 'qwen3.5-4b'
    reasoning: bool = True
    max_output_tokens: int = Field(default=8192, ge=1024, le=131072)
    model_context_tokens: int = Field(default=65536,ge=8192,le=262144,description='Loaded LM Studio context window. Used to reserve output and bound complete requests.')

    @field_validator('model_url')
    @classmethod
    def local_url(cls, value):
        p = urlparse(value)
        if p.scheme != 'http' or p.hostname not in ('localhost', '127.0.0.1', '::1') or p.username or p.query or p.fragment:
            raise ValueError('Use an HTTP loopback address for the local game/model server.')
        return value.rstrip('/')


DATA_DIR = Path(__import__('os').environ.get('RIMBOT_DATA', '.rimbot')).resolve()
