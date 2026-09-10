"""Opt-in wall-boundary and Python CPU profiling for disposable native runs."""
import cProfile
import pstats
import time


class ControllerProfile:
    def __init__(self, rt, root):
        self.root, self.rows, self.originals = root, {}, []
        self.cpu = cProfile.Profile()
        for owner, name in ((rt, 'persist'),):
            self.wrap(owner, name, False)
        for owner, name in ((rt, 'review'), (rt, 'sync_identity'), (rt, 'native'),
                            (rt.hands, 'advance'), (rt.hands, 'preflight_shell')):
            self.wrap(owner, name, True)

    def wrap(self, owner, name, asynchronous):
        original = getattr(owner, name)
        self.originals.append((owner, name, original))
        row = self.rows.setdefault(name, dict(calls=0, seconds=0.0))
        def finish(began):
            row['calls'] += 1
            row['seconds'] += time.perf_counter()-began
        async def async_call(*args, **kwargs):
            began = time.perf_counter()
            try:
                return await original(*args, **kwargs)
            finally:
                finish(began)
        def sync_call(*args, **kwargs):
            began = time.perf_counter()
            try:
                return original(*args, **kwargs)
            finally:
                finish(began)
        setattr(owner, name, async_call if asynchronous else sync_call)

    def start(self):
        self.cpu.enable()

    def stop(self):
        self.cpu.disable()
        for owner, name, original in self.originals:
            setattr(owner, name, original)
        self.cpu.dump_stats(str(self.root/'controller.pstats'))
        stats = pstats.Stats(self.cpu)
        rows = [dict(file=key[0], line=key[1], function=key[2], calls=value[1],
                     self_seconds=value[2], cumulative_seconds=value[3])
                for key, value in stats.stats.items()]
        return dict(boundaries={name: dict(row) for name, row in self.rows.items()},
            cpu=sorted(rows, key=lambda row: -row['cumulative_seconds'])[:100],
            scope='Completed wall boundaries overlap. Function timings include synchronous I/O and profiler overhead; use unprofiled runs for speed claims.')
