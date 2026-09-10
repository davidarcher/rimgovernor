"""Lazy, configurable inference roles. This module has no game or plan writer."""
import asyncio
import time
from .config import ModelRole
from .model import LocalModel


class ModelRouter:
    def __init__(self, routing, store, factory=LocalModel):
        self.routing, self.store, self.factory = routing, store, factory
        self.clients = {}
        self.metrics = store.get('model_role_metrics', {})

    def enabled(self, role):
        return ModelRole(role) in self.routing.roles

    async def complete(self, role, messages, tools, progress):
        role = ModelRole(role)
        if not self.enabled(role):
            raise ValueError(f'{role} is not configured; the strategist can decide without it')
        settings = self.routing.roles[role]
        if role not in self.clients:
            self.clients[role] = self.factory(settings)
        start = time.monotonic()
        usage, outcome = {}, 'failed'
        try:
            # Also bound a continuously streaming response, not only socket inactivity.
            async with asyncio.timeout(settings.timeout_seconds):
                result, usage = await self.clients[role].complete(messages, tools, settings.reasoning, progress)
            outcome = 'complete'
            return result, usage
        finally:
            elapsed = time.monotonic()-start
            row = self.metrics.setdefault(role.value, dict(calls=0, failed=0, input_tokens=0,
                output_tokens=0, elapsed_seconds=0, consultations=0, consultations_used=0))
            row['calls'] += 1
            row['failed'] += outcome != 'complete'
            row['input_tokens'] += usage.get('prompt_tokens', 0)
            row['output_tokens'] += usage.get('completion_tokens', 0)
            row['elapsed_seconds'] += elapsed
            row['consultations'] += role != ModelRole.STRATEGIST
            row['output_tokens_per_wall_second'] = row['output_tokens']/max(row['elapsed_seconds'], .001)
            row['model'] = settings.model
            self.store.set('model_role_metrics', self.metrics)

    def used(self, role):
        row = self.metrics.get(str(role))
        if row:
            row['consultations_used'] += 1
            self.store.set('model_role_metrics', self.metrics)

    async def close(self):
        for client in self.clients.values():
            await client.close()
