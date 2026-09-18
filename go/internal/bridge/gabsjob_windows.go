package bridge

import (
	"os"
	"unsafe"

	"golang.org/x/sys/windows"
)

// gabsProcessLease ties a GABS process to this process's lifetime with a
// job object: KILL_ON_JOB_CLOSE ends GABS when the last handle to the job
// closes, which the kernel does for every handle of a process that exits or
// is terminated, so a controller or harness killed without Close leaves no
// GABS behind. SILENT_BREAKAWAY_OK keeps the game GABS launches out of the
// job: it must outlive both, exactly as it does when GABS exits on its own.
type gabsProcessLease struct{ job windows.Handle }

func leaseGABSProcess(process *os.Process) *gabsProcessLease {
	job, err := windows.CreateJobObject(nil, nil)
	if err != nil {
		return nil
	}
	limits := windows.JOBOBJECT_EXTENDED_LIMIT_INFORMATION{}
	limits.BasicLimitInformation.LimitFlags = windows.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | windows.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK
	if _, err := windows.SetInformationJobObject(job, windows.JobObjectExtendedLimitInformation, uintptr(unsafe.Pointer(&limits)), uint32(unsafe.Sizeof(limits))); err != nil {
		_ = windows.CloseHandle(job)
		return nil
	}
	handle, err := windows.OpenProcess(windows.PROCESS_SET_QUOTA|windows.PROCESS_TERMINATE, false, uint32(process.Pid))
	if err != nil {
		_ = windows.CloseHandle(job)
		return nil
	}
	defer windows.CloseHandle(handle)
	if err := windows.AssignProcessToJobObject(job, handle); err != nil {
		_ = windows.CloseHandle(job)
		return nil
	}
	return &gabsProcessLease{job: job}
}

// release closes the job, which kills GABS if it is still running.
func (l *gabsProcessLease) release() {
	if l != nil {
		_ = windows.CloseHandle(l.job)
	}
}
