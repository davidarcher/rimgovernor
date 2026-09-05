"""Typed, schema-validated native HTTP client generated from the full OpenAPI surface.

This transport does not authorize manager tools. Callers retain action policy.
It never retries writes or turns native errors into invented success responses.
"""
import json
from pathlib import Path

import httpx
from jsonschema import Draft202012Validator, FormatChecker
from pydantic import BaseModel

from .http_models import HttpOperations, RESPONSE_TYPES

DATA = Path(__file__).parent / 'data'
DOCUMENT = json.loads((DATA / 'rimapi.openapi.json').read_text())
OPERATIONS = json.loads((DATA / 'http_operations.json').read_text())


class HttpContractError(RuntimeError):
    pass


def validate_wire(value, schema):
    Draft202012Validator({**schema, 'components': DOCUMENT['components']},
        format_checker=FormatChecker()).validate(value)


def wire(value):
    return value.model_dump(mode='json', by_alias=True, exclude_unset=True) if isinstance(value, BaseModel) else value


class HttpContractClient(HttpOperations):
    def __init__(self, http: httpx.AsyncClient):
        self.http = http

    async def _call(self, operation_id, *, query=None, body=None):
        op = OPERATIONS[operation_id]
        query = wire(query) if query is not None else {}
        validate_wire(query, op['query_schema'])
        if body is None:
            if op['body_required']:
                raise HttpContractError(f'{operation_id} requires a JSON body.')
            missing = set(op['query_fallback_required']) - query.keys()
            if missing:
                raise HttpContractError(f'{operation_id} requires body or query fields: {sorted(missing)}')
        else:
            if not op['body_schema']:
                raise HttpContractError(f'{operation_id} does not accept a JSON body.')
            body = wire(body)
            validate_wire(body, op['body_schema'])
        if operation_id not in RESPONSE_TYPES:
            raise HttpContractError(f'{operation_id} uses a streaming/raw transport; use a streaming client.')
        # Omit JSON entirely when absent; GET-body endpoints retain their native transport.
        options = {'params': query}
        if body is not None:
            options['json'] = body
        response = await self.http.request(op['method'], op['path'], **options)
        media = response.headers.get('content-type', '').split(';')[0].strip().lower()
        if media != 'application/json':
            raise HttpContractError(f'{operation_id}: expected JSON, received HTTP {response.status_code} {media}.')
        payload = response.json()
        contract = op['responses'].get(str(response.status_code))
        if not contract:
            raise HttpContractError(f'{operation_id}: undocumented HTTP status {response.status_code}.')
        schema = contract.get('content', {}).get('application/json', {}).get('schema')
        if schema is None:
            raise HttpContractError(f'{operation_id}: undocumented JSON response.')
        validate_wire(payload, schema)
        if response.is_error or isinstance(payload, dict) and payload.get('success') is False:
            raise HttpContractError(f'{operation_id}: HTTP {response.status_code}: {payload}')
        return RESPONSE_TYPES[operation_id].validate_python(payload)
