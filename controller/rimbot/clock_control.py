"""Native, renewable game-clock supervision; no model turn or thinking budget."""
import asyncio
import uuid
from .bridge import BridgeError, runtime_file_read

TOOL = 'home/supervised_play'
HOLD_REASONS = frozenset({'external_pause', 'external_speed_changed', 'lease_expired',
    'session_changed', 'unavailable', 'watcher_error', 'start_refused', 'force_paused',
    'event_journal_error', 'hunting_route_unsafe'})


class PlayClock:
    def __init__(self, bridge, store=None, context=None, *, test_acceleration=False):
        self.bridge = bridge
        self.test_acceleration = test_acceleration
        self.owner = 'rimbot-' + uuid.uuid4().hex
        self.epoch = 0
        self.cursor = 0
        self.hold = None
        self.acknowledged_stop = None
        self.state = {}
        self.lock = asyncio.Lock()
        self.store, self.context = store, context
        self.source_key = 'clock-source:'+context.rsplit(':', 1)[0] if context else None
        saved = store.get(self.source_key, {}) if store and context else {}
        self.cursor = saved.get('cursor', 0)
        self.epoch = saved.get('epoch', 0)
        previous = saved.get('context')
        self.reloaded = bool(previous and previous != context)
        if store and previous and previous != context:
            with store.transaction():
                old = store.get('clock-inbox:'+previous, [])
                pending = store.get('clock-inbox:'+context, [])
                store.set('clock-inbox:'+context, pending+[row for row in old if row not in pending])
                store.set('clock-inbox:'+previous, [])

    def record(self, rows=(), cursor=None):
        if self.store and self.context:
            with self.store.transaction():
                key = 'clock-inbox:'+self.context
                pending = self.store.get(key, [])
                pending.extend(rows)
                self.store.set(key, pending)
                self.store.set(self.source_key,
                    {'cursor': self.cursor if cursor is None else cursor, 'epoch': self.epoch, 'context': self.context})

    async def call(self, **arguments):
        if arguments.get('op') in ('status', 'events'):
            reply = await runtime_file_read(self.bridge.call, TOOL, **arguments)
        else:
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

    async def change(self, speed, *, mode='colony', ignored_hostiles='', ignored_downed='', max_ticks=None, surgical_recovery='', medical_rest=''):
        if speed not in ('Paused', 'Normal', 'Fast', 'Superfast'):
            raise ValueError('Choose Paused, Normal, Fast or Superfast')
        if mode not in ('colony', 'combat'):
            raise ValueError('Choose colony or combat clock monitoring')
        if max_ticks is not None and (type(max_ticks) is not int or not 1 <= max_ticks <= 1800000):
            raise ValueError('Native execution budget must be 1..1800000 game ticks')
        if medical_rest and (type(max_ticks) is not int or not 1<=max_ticks<=600):
            raise ValueError('Medical rest monitoring requires 1..600 game ticks')
        if self.test_acceleration and speed != 'Paused' and max_ticks is None:
            raise ValueError('Test acceleration requires a native tick budget')
        async with self.lock:
            state = await self.call(op='status')
            self.absorb(state)
            if speed != 'Paused' and self.hold:
                raise ValueError('Clock held: '+self.hold+'. The player must enable Automate again to resume.')
            if state.get('active') and state.get('owner') != self.owner:
                raise ValueError('Another controller owns the native clock; waiting for its lease to expire')
            if speed != 'Paused' and max_ticks is not None and state.get('nativeTickBoundary') is not True:
                raise ValueError('Installed native clock lacks tick boundaries; update the observation companion before automatic execution')
            if self.test_acceleration and speed != 'Paused' and state.get('nativeTestAcceleration') is not True:
                raise ValueError('Installed native clock lacks supervised test acceleration')
            if speed == 'Paused':
                if state.get('active'):
                    try:
                        result = await self.call(op='pause', owner=self.owner, epoch=state['epoch'])
                    except BridgeError as error:
                        # The native tick callback may finish this exact window
                        # between status and pause. Observe; never replay the write.
                        if 'Owner/epoch mismatch or no active supervisor' not in error.detail:
                            raise
                        latest = await self.call(op='status')
                        if not (latest.get('owner') == self.owner and latest.get('epoch') == state['epoch']
                                and latest.get('active') is False and latest.get('paused') is True
                                and latest.get('pauseVerified') is True and latest.get('sessionChanged') is False
                                and latest.get('stopReason') == 'tick_budget'):
                            raise
                        status = (await self.bridge.call('home/status', colonists=False, threats=False)).structuredContent
                        if status.get('time', {}).get('paused') is not True:
                            raise
                        result = dict(latest, pauseReconciled=True)
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
                speed='Ultrafast' if self.test_acceleration else speed, mode=mode, hostileWithin=40,
                ignoredHostileIds=ignored_hostiles, ignoredDownedColonistIds=ignored_downed,
                injuryStopCooldownMs=0, **({'maxTicks': max_ticks} if max_ticks is not None else {}),
                **({'surgicalRecoveryIds': surgical_recovery} if surgical_recovery else {}),
                **({'medicalRestIds': medical_rest} if medical_rest else {}),
                **({'testAcceleration': True} if self.test_acceleration else {}))
            self.epoch = result['epoch']
            self.record()
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
            newest = state.get('newestCursor')
            if type(newest) is int and self.cursor > newest:
                # A paired save can precede the controller's final journal read.
                # Preserve the discontinuity before reading the restored journal;
                # never send an invalid cursor or silently discard pending events.
                if not self.reloaded or state.get('active'):
                    self.hold = 'event_journal_error'
                    raise ValueError('Native event cursor regressed within the current load')
                gap = dict(kind='event_gap', detail='Restored native journal precedes the saved controller cursor.',
                           saved_cursor=self.cursor, restored_cursor=newest)
                self.cursor = 0
                self.reloaded = False
                self.record([gap])
                return [gap]
            self.reloaded = False
            batch = await self.call(op='events', afterCursor=self.cursor, limit=128)
            rows = [row for row in batch['events'] if row['epoch'] == self.epoch]
            if batch.get('gap'):
                rows.insert(0, {'kind': 'event_gap', 'detail': 'Native event history overflowed; inspect current colony state.'})
            self.record(rows, batch['nextCursor'])
            self.cursor = batch['nextCursor']
            return rows
