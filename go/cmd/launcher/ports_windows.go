//go:build windows

package main

import (
	"encoding/binary"
	"fmt"
	"unsafe"

	"golang.org/x/sys/windows"
)

var getExtendedTCPTable = windows.NewLazySystemDLL("iphlpapi.dll").NewProc("GetExtendedTcpTable")

// listeners are the processes listening on port, IPv4 or IPv6.
func listeners(port int) ([]Owner, error) {
	seen := map[int]bool{}
	var owners []Owner
	for _, family := range []struct {
		af             uint32
		row, pid, port int // row size; pid and local port offsets
	}{{windows.AF_INET, 24, 20, 8}, {windows.AF_INET6, 56, 52, 20}} {
		table, err := tcpListenerTable(family.af)
		if err != nil {
			return nil, err
		}
		n := int(binary.LittleEndian.Uint32(table))
		for i := 0; i < n; i++ {
			row := table[4+i*family.row:]
			// The port is in network byte order in the low 16 bits.
			if int(binary.BigEndian.Uint16(row[family.port:])) != port {
				continue
			}
			pid := int(binary.LittleEndian.Uint32(row[family.pid:]))
			if !seen[pid] {
				seen[pid] = true
				owners = append(owners, Owner{PID: pid, Path: processPath(pid)})
			}
		}
	}
	return owners, nil
}

// tcpListenerTable is TCP_TABLE_OWNER_PID_LISTENER for af.
func tcpListenerTable(af uint32) ([]byte, error) {
	const tcpTableOwnerPIDListener = 3
	size := uint32(4096)
	for {
		buf := make([]byte, size)
		r, _, _ := getExtendedTCPTable.Call(uintptr(unsafe.Pointer(&buf[0])), uintptr(unsafe.Pointer(&size)), 0, uintptr(af), tcpTableOwnerPIDListener, 0)
		switch windows.Errno(r) {
		case 0:
			return buf, nil
		case windows.ERROR_INSUFFICIENT_BUFFER:
			continue
		default:
			return nil, fmt.Errorf("GetExtendedTcpTable: %w", windows.Errno(r))
		}
	}
}

// processPath is pid's executable, "" when it is gone or withheld.
func processPath(pid int) string {
	h, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION, false, uint32(pid))
	if err != nil {
		return ""
	}
	defer windows.CloseHandle(h)
	var code uint32
	if windows.GetExitCodeProcess(h, &code) != nil || code != 259 { // STILL_ACTIVE
		return ""
	}
	buf := make([]uint16, windows.MAX_LONG_PATH)
	n := uint32(len(buf))
	if windows.QueryFullProcessImageName(h, 0, &buf[0], &n) != nil {
		return ""
	}
	return windows.UTF16ToString(buf[:n])
}
