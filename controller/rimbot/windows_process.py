"""Retained process handles for the one-time Windows legacy migration."""
import ctypes
from ctypes import wintypes
from contextlib import contextmanager
import os
from pathlib import Path
import subprocess
import sys
import time


class ProcessHandle:
    def __init__(self, pid, birth=None):
        if os.name != 'nt': raise ValueError('Legacy process migration currently requires Windows')
        self.pid = int(pid)
        self.kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        self.native = ctypes.WinDLL('ntdll')
        self.kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.kernel.OpenProcess.restype = wintypes.HANDLE
        for name in ('CloseHandle', 'TerminateProcess', 'WaitForSingleObject', 'GetProcessTimes'):
            getattr(self.kernel, name).argtypes = {
                'CloseHandle':[wintypes.HANDLE], 'TerminateProcess':[wintypes.HANDLE,wintypes.UINT],
                'WaitForSingleObject':[wintypes.HANDLE,wintypes.DWORD],
                'GetProcessTimes':[wintypes.HANDLE,*([ctypes.POINTER(wintypes.FILETIME)]*4)]}[name]
        for name in ('NtSuspendProcess', 'NtResumeProcess'):
            getattr(self.native,name).argtypes=[wintypes.HANDLE]
            getattr(self.native,name).restype=wintypes.LONG
        self.handle=self.kernel.OpenProcess(0x100000|0x1000|0x0800|0x0001,False,self.pid)
        if not self.handle: raise ctypes.WinError(ctypes.get_last_error())
        times=[wintypes.FILETIME() for _ in range(4)]
        if not self.kernel.GetProcessTimes(self.handle,*(ctypes.byref(t) for t in times)):
            self.close();raise ctypes.WinError(ctypes.get_last_error())
        self.birth=(times[0].dwHighDateTime<<32)|times[0].dwLowDateTime
        if birth is not None and self.birth != int(birth):
            self.close();raise ValueError('Process birth time changed')

    def alive(self): return self.kernel.WaitForSingleObject(self.handle,0)==258
    def resume(self):
        if self.alive(): self.native.NtResumeProcess(self.handle)
    def terminate(self):
        if not self.kernel.TerminateProcess(self.handle,0): raise ctypes.WinError(ctypes.get_last_error())
        if self.kernel.WaitForSingleObject(self.handle,30000)!=0: raise ValueError('Old controller did not exit')
    def close(self):
        if self.handle: self.kernel.CloseHandle(self.handle);self.handle=None

    @contextmanager
    def paused(self, marker):
        marker=Path(marker).resolve()
        # A detached, bounded watchdog releases the controller if migration crashes.
        parent=ProcessHandle(os.getpid())
        try:
            subprocess.Popen([sys.executable,'-m','rimbot.windows_process',str(self.pid),str(self.birth),
                str(parent.pid),str(parent.birth),str(marker)],stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,
                creationflags=subprocess.DETACHED_PROCESS|subprocess.CREATE_NEW_PROCESS_GROUP)
        finally: parent.close()
        deadline=time.monotonic()+5
        while not marker.with_suffix('.ready').exists() and time.monotonic()<deadline: time.sleep(.05)
        if not marker.with_suffix('.ready').exists():
            marker.touch();raise ValueError('Migration recovery watchdog did not start')
        if self.native.NtSuspendProcess(self.handle)!=0:
            marker.touch();raise ValueError('Could not pause the old controller process')
        try: yield
        finally:
            self.resume()
            marker.touch()


def watchdog():
    target=ProcessHandle(sys.argv[1],sys.argv[2])
    try: parent=ProcessHandle(sys.argv[3],sys.argv[4])
    except Exception:
        target.resume();target.close();return
    marker=Path(sys.argv[5]);deadline=time.monotonic()+90
    marker.with_suffix('.ready').touch()
    try:
        while not marker.exists() and parent.alive() and time.monotonic()<deadline:
            time.sleep(.2)
        if not marker.exists(): target.resume()
    finally: target.close();parent.close()


if __name__=='__main__': watchdog()
