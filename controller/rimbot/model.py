"""Streaming OpenAI-compatible local inference; Qwen thinking is on/off."""
import json
import time
import httpx


class ModelError(RuntimeError):
    pass


class LocalModel:
    def __init__(self, settings, transport=None):
        self.settings = settings
        self.reasoning_style = 'standard'
        self.http = httpx.AsyncClient(base_url=settings.model_url, timeout=httpx.Timeout(600, connect=10), transport=transport, trust_env=False)

    async def close(self):
        await self.http.aclose()

    async def complete(self, messages, tools, thinking, progress, _retried=False):
        effort = ('medium' if thinking else 'none') if self.reasoning_style == 'standard' else ('on' if thinking else 'off')
        body = {'model':self.settings.model, 'messages':messages, 'stream':True,
                'stream_options':{'include_usage':True}, 'temperature':0.35,
                'max_tokens':self.settings.max_output_tokens,
                'reasoning_effort':effort,
                'chat_template_kwargs':{'enable_thinking':thinking}}
        if tools:
            body.update(tools=tools, tool_choice='auto')
        text, reasoning, calls, usage, finish = '', 0, {}, {}, None
        last = 0
        try:
            async with self.http.stream('POST', '/chat/completions', json=body) as r:
                if r.status_code != 200:
                    detail = (await r.aread()).decode()
                    # Retry only an explicit parameter rejection, before generation.
                    # Older local servers accept on/off; compatible endpoints use levels.
                    if r.status_code == 400 and not _retried and 'reasoning_effort' in detail:
                        import re
                        supported = detail.split('Supported', 1)[-1]
                        if re.search(r'\bon\b', supported) and re.search(r'\boff\b', supported):
                            self.reasoning_style = 'toggle'
                            return await self.complete(messages, tools, thinking, progress, _retried=True)
                    raise ModelError(f'Local model HTTP {r.status_code}: {detail[:600]}')
                async for line in r.aiter_lines():
                    if not line.startswith('data:'):
                        continue
                    raw = line[5:].strip()
                    if raw == '[DONE]':
                        break
                    chunk = json.loads(raw)
                    usage = chunk.get('usage') or usage
                    for choice in chunk.get('choices', []):
                        finish = choice.get('finish_reason') or finish
                        delta = choice.get('delta', {})
                        text += delta.get('content') or ''
                        reasoning += len(delta.get('reasoning_content') or delta.get('reasoning') or '')
                        for c in delta.get('tool_calls') or []:
                            entry = calls.setdefault(c['index'], {'id':'', 'type':'function', 'function':{'name':'','arguments':''}})
                            if c.get('id'):
                                entry['id'] = c['id']
                            f = c.get('function', {})
                            if f.get('name'):
                                entry['function']['name'] += f['name']
                            entry['function']['arguments'] += f.get('arguments') or ''
                    if time.monotonic() - last > 0.5:
                        await progress({'phase':'Thinking' if reasoning and not text and not calls else 'Preparing decisions', 'output_chars':len(text)+sum(len(c['function']['arguments']) for c in calls.values()), 'reasoning_chars':reasoning})
                        last = time.monotonic()
        except httpx.HTTPError as e:
            raise ModelError(f'LM Studio connection failed: {e}') from e
        if finish == 'length':
            raise ModelError('Model output limit reached; incomplete decisions were not executed. Increase output tokens in Settings or simplify this review.')
        if finish not in ('stop', 'tool_calls'):
            raise ModelError(f'Model stream ended without a complete response ({finish}).')
        return {'role':'assistant', 'content':text or None, **({'tool_calls':list(calls.values())} if calls else {})}, usage
