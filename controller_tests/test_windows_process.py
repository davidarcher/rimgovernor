"""Exercise real Windows suspension and recovery using disposable Python workers."""
import os
import subprocess
import sys
import time
import pytest
from rimbot.windows_process import ProcessHandle

pytestmark=pytest.mark.skipif(os.name!='nt',reason='Windows retained process handles')


def wait_for(predicate):
    deadline=time.monotonic()+8
    while time.monotonic()<deadline:
        if predicate(): return
        time.sleep(.05)
    raise AssertionError('Worker did not reach expected state')


def test_pause_and_parent_crash_resume_exact_worker(tmp_path):
    beat=tmp_path/'beat';pidfile=tmp_path/'pid'
    worker=subprocess.Popen([sys.executable,'-c',
        f'import os,time,pathlib; pathlib.Path({str(pidfile)!r}).write_text(str(os.getpid())); '
        f'p=pathlib.Path({str(beat)!r});\nwhile True: p.write_text(str(time.time())); time.sleep(.1)'])
    wait_for(pidfile.exists)
    target=ProcessHandle(int(pidfile.read_text()))
    parent=None
    try:
        wait_for(beat.exists)
        with pytest.raises(ValueError,match='birth'): ProcessHandle(target.pid,target.birth+1)
        with target.paused(tmp_path/'normal'):
            time.sleep(.15);value=beat.read_text();time.sleep(.3)
            assert beat.read_text()==value
        wait_for(lambda:beat.read_text()!=value)
        parentpid=tmp_path/'parent'
        owner=subprocess.Popen([sys.executable,'-c',
            'import os,time;from pathlib import Path;from rimbot.windows_process import ProcessHandle;'
            f'Path({str(parentpid)!r}).write_text(str(os.getpid()));h=ProcessHandle({target.pid});'
            f'\nwith h.paused(Path({str(tmp_path/"crash")!r})): time.sleep(60)'])
        wait_for((tmp_path/'crash.ready').exists)
        parent=ProcessHandle(int(parentpid.read_text()))
        time.sleep(.15);value=beat.read_text();time.sleep(.3)
        assert beat.read_text()==value
        parent.terminate()
        wait_for(lambda:beat.read_text()!=value)
        owner.wait(timeout=5)
    finally:
        if parent:
            if parent.alive(): parent.terminate()
            parent.close()
        target.resume();target.terminate();target.close()
        worker.wait(timeout=5)
