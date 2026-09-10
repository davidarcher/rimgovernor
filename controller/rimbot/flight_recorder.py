"""Opt-in bounded native timeline; requests reach durable storage before dispatch."""
import hashlib
import json
import os
from pathlib import Path
import threading
import time
import uuid
from contextlib import contextmanager
from contextvars import ContextVar


_action_context = ContextVar('rimbot_recording_action', default={})


@contextmanager
def recording_action(action_id, goal_id):
    token = _action_context.set(dict(action_id=action_id, goal_id=goal_id) if action_id else {})
    try:
        yield
    finally:
        _action_context.reset(token)


class FlightRecorder:
    def __init__(self, path, *, segment_bytes=8*1024*1024, segments=8, payload_bytes=256*1024):
        if segment_bytes < 1024 or segments < 2 or payload_bytes < 128:
            raise ValueError('Recorder bounds are too small')
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.segment_bytes, self.segments, self.payload_bytes = segment_bytes, segments, payload_bytes
        self.lock = threading.Lock()
        self._stream = None
        self._size = 0
        self.sequence = 0
        self.context = {}
        self.elapsed = 0.0
        self.phase_seconds = dict(lock=0.0, encode=0.0, rotate=0.0, write=0.0, fsync=0.0)
        self.records = 0
        self.truncated = 0
        self.rotations = 0
        self.durable_records = 0
        self.run = os.environ.get('RIMBOT_RUN_ID', uuid.uuid4().hex)
        self.event('coverage', coverage='All BridgeClient.core requests, responses and exceptions, including background reads. Runtime snapshots at persistence boundaries. Requests/errors/outcomes are fsynced; response rows are flushed and become durable at the next durable record or rotation. A host crash can leave an explicit unmatched request. No in-game per-frame/pawn transition trace or model token recording.')

    def event(self, kind, *, context=None, durable=True, **payload):
        began = time.perf_counter()
        with self.lock:
            encoding = time.perf_counter()
            self.phase_seconds['lock'] += encoding-began
            encoded = json.dumps(payload, default=str, separators=(',', ':')).encode()
            if len(encoded) > self.payload_bytes:
                correlation = {key:value for key,value in payload.items()
                               if key in ('request','tool','category') and
                               (isinstance(value,int) or isinstance(value,str) and len(value)<=256)}
                payload = dict(truncated=True, original_bytes=len(encoded), sha256=hashlib.sha256(encoded).hexdigest(),
                               preview=encoded[:self.payload_bytes].decode('utf8', errors='replace'), **correlation)
                self.truncated += 1
                encoded = json.dumps(payload, default=str, separators=(',', ':')).encode()
            self.sequence += 1
            row = dict(version=1, run=self.run, scenario=os.environ.get('RIMBOT_SCENARIO'),
                       sequence=self.sequence, wall_time=time.time(), monotonic=time.monotonic(),
                       kind=kind, context={**(self.context if context is None else context), **_action_context.get()})
            # Reuse the payload encoding used for the size/hash check. Responses
            # can be large; serializing them again adds no evidence.
            header = json.dumps(row, default=str, separators=(',', ':')).encode()
            line = header[:-1]+b',"payload":'+encoded+b'}\n'
            self.phase_seconds['encode'] += time.perf_counter()-encoding
            rotated = time.perf_counter()
            if self._stream is None:
                self._stream = self.path.open('ab')
                self._size = self._stream.tell()
            if self._size >= self.segment_bytes:
                os.fsync(self._stream.fileno())
                self._stream.close()
                self._stream = None
                oldest = self.path.with_name(self.path.name+f'.{self.segments-1}')
                oldest.unlink(missing_ok=True)
                for index in range(self.segments-2, 0, -1):
                    source = self.path.with_name(self.path.name+f'.{index}')
                    if source.exists():
                        source.replace(self.path.with_name(self.path.name+f'.{index+1}'))
                self.path.replace(self.path.with_name(self.path.name+'.1'))
                self.rotations += 1
                self._size = 0
            self.phase_seconds['rotate'] += time.perf_counter()-rotated
            written = time.perf_counter()
            if self._stream is None:
                self._stream = self.path.open('ab')
            self._stream.write(line)
            self._stream.flush()
            self._size += len(line)
            self.phase_seconds['write'] += time.perf_counter()-written
            if durable:
                synced = time.perf_counter()
                os.fsync(self._stream.fileno())
                self.phase_seconds['fsync'] += time.perf_counter()-synced
                self.durable_records += 1
            self.records += 1
            self.elapsed += time.perf_counter()-began
            return self.sequence

    def close(self):
        """Finish the current segment durably; later events can reopen it."""
        with self.lock:
            if self._stream is not None:
                try:
                    self._stream.flush()
                    os.fsync(self._stream.fileno())
                finally:
                    self._stream.close()
                    self._stream = None

    def __del__(self):
        stream = getattr(self, '_stream', None)
        if stream is not None:
            stream.close()

    def stats(self):
        return dict(records=self.records, truncated=self.truncated, rotations=self.rotations,
                    durable_records=self.durable_records,
                    recording_seconds=self.elapsed, retention_segments=self.segments,
                    phase_seconds=dict(self.phase_seconds),
                    segment_bytes=self.segment_bytes, payload_bytes=self.payload_bytes)


_recorder = None


def recorder():
    global _recorder
    path = os.environ.get('RIMBOT_FLIGHT_RECORDER')
    if path and (_recorder is None or _recorder.path != Path(path)):
        if _recorder is not None:
            _recorder.close()
        _recorder = FlightRecorder(path)
    return _recorder if path else None


def read_timeline(path):
    path = Path(path)
    files = sorted((p for p in path.parent.glob(path.name+'.*') if p.suffix[1:].isdigit()),
                   key=lambda p: int(p.suffix[1:]), reverse=True)
    if path.exists():
        files.append(path)
    previous = None
    for file in files:
        for number, line in enumerate(file.read_bytes().splitlines(), 1):
            try:
                row = json.loads(line)
                if (not isinstance(row, dict) or not isinstance(row.get('sequence'), int) or 'kind' not in row
                        or not isinstance(row.get('payload'), dict) or not isinstance(row.get('context'), dict)):
                    raise ValueError('Invalid timeline record')
            except ValueError:
                yield dict(kind='recording_gap', file=str(file), line=number, reason='Incomplete or corrupt record')
                continue
            sequence = row['sequence']
            if previous is None and sequence != 1 or previous is not None and sequence != previous+1:
                yield dict(kind='recording_gap', reason='Retention or sequence discontinuity', before=sequence, after=previous)
            previous = sequence
            yield row
