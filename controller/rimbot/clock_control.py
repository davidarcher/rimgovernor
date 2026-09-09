"""Native, renewable game-clock supervision; no model turn or thinking budget."""
import asyncio
import uuid

TOOL = 'home/supervised_play'
HOLD_REASONS = frozenset({'external_pause', 'external_speed_changed', 'lease_expired',
    'session_changed', 'unavailable', 'watcher_error', 'start_refused', 'force_paused'})


class PlayClock:
    def __init__(self, bridge):
        self.bridge = bridge
        self.owner = 'rimbot-' + uuid.uuid4().hex
        self.epoch = 0
        self.cursor = 0
        self.hold = None
        self.acknowledged_stop = None
        self.state = {}
        self.lock = asyncio.Lock()

    async def call(self, **arguments):
        reply = await self.bridge.call(TOOL, **arguments)
        result = reply.structuredContent
        if not isinstance(result, dict) or result.get('success') is not True:
            raise ValueError('Native clock did not confirm the request')
        return result

    def absorb(self, state):
        self.state = state
        if (state.get('owner') == self.owner and state.get('stopReason') in HOLD_REASONS
                and (state.get('epoch'), state.get('stopReason')) != self.acknowledged_stop):
            self.hold = state['stopReason']

    def allow_resume(self):
        self.acknowledged_stop = (self.state.get('epoch'), self.state.get('stopReason'))
        self.hold = None

    async def pause_for_dialog(self):
        """Stop our running lease before a modal can be mistaken for a player pause.

        Closing the dialog never resumes time or acknowledges an external hold.
        """
        async with self.lock:
            state = await self.call(op='status')
            self.absorb(state)
            if self.hold:
                raise ValueError('Clock held: '+self.hold+'; enable Automate before opening a dialog')
            if state.get('active'):
                if state.get('owner') != self.owner:
                    raise ValueError('Another controller owns the clock; no dialog opened')
                state = await self.call(op='pause', owner=self.owner, epoch=state['epoch'])
                self.absorb(state)
            status = (await self.bridge.call('home/status', colonists=False, threats=False)).structuredContent
            if not isinstance(status, dict) or status.get('time', {}).get('paused') is not True:
                raise ValueError('Dialog preparation requires a verified pause; no dialog opened')
            return state

    async def change(self, speed, *, mode='colony', ignored_hostiles='', ignored_downed='', max_ticks=None):
        if speed not in ('Paused', 'Normal', 'Fast', 'Superfast'):
            raise ValueError('Choose Paused, Normal, Fast or Superfast')
        if mode not in ('colony', 'combat'):
            raise ValueError('Choose colony or combat clock monitoring')
        if max_ticks is not None and (type(max_ticks) is not int or not 1 <= max_ticks <= 1800000):
            raise ValueError('Native execution budget must be 1..1800000 game ticks')
        async with self.lock:
            state = await self.call(op='status')
            self.absorb(state)
            if speed != 'Paused' and self.hold:
                raise ValueError('Clock held: '+self.hold+'. The player must enable Automate again to resume.')
            if state.get('active') and state.get('owner') != self.owner:
                raise ValueError('Another controller owns the native clock; waiting for its lease to expire')
            if speed != 'Paused' and max_ticks is not None and state.get('nativeTickBoundary') is not True:
                raise ValueError('Installed native clock lacks tick boundaries; update the observation companion before automatic execution')
            if speed == 'Paused':
                if state.get('active'):
                    result = await self.call(op='pause', owner=self.owner, epoch=state['epoch'])
                else:
                    await self.bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
                    status = (await self.bridge.call('home/status', colonists=False, threats=False)).structuredContent
                    if status.get('time', {}).get('paused') is not True:
                        raise ValueError('Native game pause was not confirmed')
                    result = dict(state, paused=True, pauseVerified=True)
                self.absorb(result)
                return result
            # Replace an existing epoch to apply the requested monitoring profile.
            if state.get('active'):
                await self.call(op='pause', owner=self.owner, epoch=state['epoch'])
            if not self.epoch:
                self.cursor = state.get('newestCursor', 0)
            result = await self.call(op='start', owner=self.owner, leaseMs=15000,
                speed=speed, mode=mode, hostileWithin=40,
                ignoredHostileIds=ignored_hostiles, ignoredDownedColonistIds=ignored_downed,
                injuryStopCooldownMs=0, **({'maxTicks': max_ticks} if max_ticks is not None else {}))
            self.epoch = result['epoch']
            self.absorb(result)
            return result

    async def poll(self):
        async with self.lock:
            state = await self.call(op='status')
            self.absorb(state)
            if state.get('active') and state.get('owner') == self.owner:
                try:
                    self.absorb(await self.call(op='heartbeat', owner=self.owner, epoch=state['epoch'], leaseMs=15000))
                except Exception:
                    # A native danger stop can race the heartbeat. Preserve that
                    # event instead of misreporting it as a connection failure.
                    latest = await self.call(op='status')
                    self.absorb(latest)
                    if latest.get('active'):
                        raise
            if not self.epoch:
                return []
            batch = await self.call(op='events', afterCursor=self.cursor, limit=128)
            self.cursor = batch['nextCursor']
            rows = [row for row in batch['events'] if row['epoch'] == self.epoch]
            if batch.get('gap'):
                rows.insert(0, {'kind': 'event_gap', 'detail': 'Native event history overflowed; inspect current colony state.'})
            return rows
