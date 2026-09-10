import os
from pathlib import Path
from urllib.parse import urlparse
from pydantic import BaseModel, Field, field_validator, ConfigDict
from enum import StrEnum
from typing import Literal


class Settings(BaseModel):
    model_config = ConfigDict(extra='forbid')
    provider: Literal['local_openai'] = 'local_openai'
    model_url: str = 'http://127.0.0.1:1234/v1'
    model: str = 'qwen3.5-4b'
    reasoning: bool = True
    max_output_tokens: int = Field(default=8192, ge=1024, le=131072)
    model_context_tokens: int = Field(default=65536,ge=8192,le=262144,description='Loaded LM Studio context window. Used to reserve output and bound complete requests.')
    temperature: float = Field(default=0.35, ge=0, le=2)
    timeout_seconds: float = Field(default=600, ge=1, le=3600)

    @field_validator('model_url')
    @classmethod
    def local_url(cls, value):
        p = urlparse(value)
        hosts = {'localhost', '127.0.0.1', '::1'}
        if os.environ.get('RIMGOVERNOR_ALLOW_DOCKER_HOST_MODEL') == '1':
            hosts.add('host.docker.internal')
        if p.scheme != 'http' or p.hostname not in hosts or p.username is not None or p.password is not None or p.query or p.fragment:
            raise ValueError('Use HTTP loopback or explicitly enable the Docker host for local inference.')
        return value.rstrip('/')


class ModelRole(StrEnum):
    STRATEGIST = 'strategist'
    ANALYST = 'analyst'
    ARCHITECT = 'architect'
    CRITIC = 'critic'


class ModelRouting(BaseModel):
    model_config = ConfigDict(extra='forbid')
    roles: dict[ModelRole, Settings]


def load_model_routing(primary: Settings, path=None):
    import json
    if path:
        routing = ModelRouting.model_validate(json.loads(Path(path).read_text(encoding='utf8')))
        if ModelRole.STRATEGIST not in routing.roles:
            raise ValueError('Configure a strategist; auxiliary roles are optional')
        return routing
    return ModelRouting(roles={ModelRole.STRATEGIST: primary})

DATA_DIR = Path(__import__('os').environ.get('RIMGOVERNOR_DATA', '.rimgovernor')).resolve()
