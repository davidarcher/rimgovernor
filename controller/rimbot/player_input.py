"""Ephemeral viewer ownership; expiry never resumes colony automation."""
from dataclasses import dataclass
import time
import uuid


@dataclass
class InputLease:
    session: str
    viewer: str
    token: str
    deadline: float
    ready: bool = False

    @classmethod
    def create(cls, session, viewer):
        return cls(session, viewer, uuid.uuid4().hex, time.monotonic() + 15)

    def live(self):
        return self.deadline > time.monotonic()


def require_owner(rt, session, viewer, token):
    lease = getattr(rt, 'player_input', None)
    if (not lease or not lease.ready or not lease.live()
            or (lease.session, lease.viewer, lease.token) != (session, viewer, token)):
        raise ValueError('Player control expired or belongs to another viewer; take control again')
    return lease


def check_player_control(rt, session, viewer, token):
    if getattr(rt, 'player_input', None) is not None or viewer or token:
        require_owner(rt, session, viewer, token)
