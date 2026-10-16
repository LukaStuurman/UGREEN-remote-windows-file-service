package main

import (
	"bytes"
	"context"
	"encoding/json"
	"net"
	"os"
	"testing"
	"time"
)

func TestSMBHandleSaveReplaceAndIdentity(t *testing.T) {
	address := os.Getenv("UGREEN_SMOKE_SMB_ADDR")
	if address == "" {
		t.Skip("Isolated Samba fixture runs in CI")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	conn, err := net.DialTimeout("tcp", address, 3*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	a := &app{}
	secret := fixtureSecret("fixture-test-only")
	defer clear(secret)
	if err = a.mount(ctx, conn, "fixture", secret); err != nil {
		t.Fatal(err)
	}
	defer a.closeSMB()
	call := func(r request, b []byte) response {
		t.Helper()
		value, _, err := a.handleFile(ctx, r, b)
		if err != nil {
			t.Fatalf("%s returned %d: %v", r.Op, errorStatus(err), err)
		}
		return response{Status: 200, Value: value}
	}
	open := func(path string, mode, access, sharing uint32) uint64 {
		t.Helper()
		reply := call(request{Op: "open-handle", Path: path, Access: access, Sharing: sharing, Disposition: mode, Options: 0x40, Attributes: 0x80}, nil)
		encoded, _ := json.Marshal(reply.Value)
		var value struct{ Handle uint64 }
		_ = json.Unmarshal(encoded, &value)
		if value.Handle == 0 {
			t.Fatal("Missing handle")
		}
		return value.Handle
	}
	read := func(id uint64, offset int64, n int) []byte {
		t.Helper()
		r, b := a.handle(request{ID: 1, Op: "handle-read", Handle: id, Offset: offset, Length: n}, nil)
		if r.Status != 200 {
			t.Fatalf("Read returned %d", r.Status)
		}
		return b
	}
	h := open("save-fixture.dwg", 2, 0xc0010000, 7)
	data := make([]byte, 3*1024*1024+37)
	for i := range data {
		data[i] = byte(i * 37 % 251)
	}
	// Out-of-order bodies and last-minute header updates, not just one sequential copy.
	for _, part := range [][2]int{{1048576, 2097152}, {2097152, len(data)}, {0, 1048576}} {
		call(request{Op: "handle-write", Handle: h, Offset: int64(part[0])}, data[part[0]:part[1]])
	}
	header := []byte("AC1032-fixture-header-update")
	copy(data, header)
	call(request{Op: "handle-write", Handle: h}, header)
	call(request{Op: "handle-flush", Handle: h}, nil)
	if !bytes.Equal(read(h, 0, len(data)), data) {
		t.Fatal("Random write/header bytes changed")
	}
	// Reserving larger allocation must not change logical length or append zeros.
	call(request{Op: "handle-reserve", Handle: h, Size: int64(len(data) + 1048576)}, nil)
	if got := read(h, int64(len(data)), 1); len(got) != 0 {
		t.Fatal("Allocation extended logical EOF")
	}
	// SMB FileRenameInformation refuses replacing an open target (MS-FSCC
	// 2.4.42). Preserve that server error; never pre-delete or fake success.
	replacement := open("save-fixture.tmp", 2, 0xc0010000, 7)
	fresh := bytes.Repeat([]byte{0xf1, 0x00, 0x42, 0xff}, 40000)
	call(request{Op: "handle-write", Handle: replacement}, fresh)
	if a.share == nil {
		t.Fatal("SMB session was lost before replacement")
	}
	inspected, inspectErr := (sharedInspector{a.share.WithContext(ctx)}).Lstat("save-fixture.dwg")
	if inspectErr != nil {
		t.Fatalf("Inspect replacement target: %v", inspectErr)
	}
	if inspected.Mode()&(os.ModeSymlink|os.ModeIrregular|os.ModeDevice|os.ModeNamedPipe|os.ModeSocket) != 0 {
		t.Fatalf("Unexpected fixture target mode: %v", inspected.Mode())
	}
	deniedReplace, _ := a.handle(request{ID: 1, Op: "handle-rename", Handle: replacement, Target: "save-fixture.dwg", Replace: true}, nil)
	if deniedReplace.Status != 403 {
		t.Fatalf("Open destination replacement returned %d", deniedReplace.Status)
	}
	if !bytes.Equal(read(h, 0, len(data)), data) {
		t.Fatal("Refused replacement damaged original")
	}
	// CAD's backup sequence renames the original first. The still-open old
	// handle must keep that object when the original pathname is reused.
	call(request{Op: "handle-rename", Handle: h, Target: "save-fixture.bak"}, nil)
	call(request{Op: "handle-rename", Handle: replacement, Target: "save-fixture.dwg", Replace: true}, nil)
	call(request{Op: "handle-delete", Handle: h}, nil)
	call(request{Op: "handle-close", Handle: h}, nil)
	check := open("save-fixture.dwg", 1, 0x80000000, 7)
	if !bytes.Equal(read(check, 0, len(fresh)), fresh) {
		t.Fatal("Closing/deleting old file damaged replacement")
	}
	call(request{Op: "handle-close", Handle: check}, nil)
	// Also require a genuine atomic replace-existing with a closed destination.
	call(request{Op: "handle-rename", Handle: replacement, Target: "save-fixture.tmp"}, nil)
	closedTarget := open("save-fixture.dwg", 2, 0xc0010000, 7)
	call(request{Op: "handle-write", Handle: closedTarget}, []byte{1, 2, 3})
	call(request{Op: "handle-close", Handle: closedTarget}, nil)
	call(request{Op: "handle-rename", Handle: replacement, Target: "save-fixture.dwg", Replace: true}, nil)
	if !bytes.Equal(read(replacement, 0, len(fresh)), fresh) {
		t.Fatal("Atomic replace-existing changed saved content")
	}
	// Metadata changes also follow the file ID after rename.
	modified := time.Date(2025, 1, 2, 3, 4, 5, 0, time.UTC)
	call(request{Op: "handle-times", Handle: replacement, Modified: modified}, nil)
	call(request{Op: "handle-attributes", Handle: replacement, Attributes: 0x20}, nil)
	info := call(request{Op: "handle-stat", Handle: replacement}, nil).Value.(entry)
	if !info.ModifiedUtc.Equal(modified) {
		t.Fatal("Modified time did not follow renamed file")
	}
	call(request{Op: "handle-resize", Handle: replacement, Size: 333}, nil)
	if got := read(replacement, 0, 400); !bytes.Equal(got, fresh[:333]) {
		t.Fatal("Logical truncate failed")
	}
	call(request{Op: "handle-reserve", Handle: replacement, Size: 31}, nil)
	if got := read(replacement, 0, 400); !bytes.Equal(got, fresh[:31]) {
		t.Fatal("Allocation shrink failed")
	}
	// Native SMB sharing violations must be reported, not ignored by our mount.
	locked := open("locked-fixture.tmp", 2, 0xc0010000, 0)
	denied, _ := a.handle(request{ID: 1, Op: "open-handle", Path: "locked-fixture.tmp", Access: 0x80000000, Sharing: 7, Disposition: 1, Options: 0x40}, nil)
	if denied.Status != 423 {
		t.Fatalf("Exclusive handle was not enforced: %d", denied.Status)
	}
	call(request{Op: "handle-delete", Handle: locked}, nil)
	call(request{Op: "handle-close", Handle: locked}, nil)
	readOnly := open("save-fixture.dwg", 1, 0x80000000, 7)
	denied, _ = a.handle(request{ID: 1, Op: "handle-write", Handle: readOnly}, []byte{9})
	if denied.Status != 403 {
		t.Fatalf("Read-only write was not refused: %d", denied.Status)
	}
	denied, _ = a.handle(request{ID: 1, Op: "handle-flush", Handle: readOnly}, nil)
	if denied.Status != 403 {
		t.Fatal("Failed write became a successful flush")
	}
	call(request{Op: "handle-close", Handle: readOnly}, nil)
	call(request{Op: "handle-delete", Handle: replacement}, nil)
	call(request{Op: "handle-close", Handle: replacement}, nil)
	// A persistent root handle must refresh an expired opening context and
	// reject mutation even though metadata queries and close remain permitted.
	opening, cancelOpening := context.WithCancel(ctx)
	value, _, err := a.handleFile(opening, request{Op: "open-handle", Path: "", Access: 0x80000000, Sharing: 7, Disposition: 1, Options: 1}, nil)
	if err != nil {
		t.Fatal(err)
	}
	root := value.(map[string]any)["handle"].(uint64)
	cancelOpening()
	call(request{Op: "handle-stat", Handle: root}, nil)
	denied, _ = a.handle(request{ID: 1, Op: "handle-delete", Handle: root}, nil)
	if denied.Status != 403 {
		t.Fatal("Root handle mutation allowed")
	}
	call(request{Op: "handle-close", Handle: root}, nil)
	// Root and symlink guards also apply to the new handle API.
	for _, r := range []request{{Op: "open-handle", Path: "", Disposition: 5, Access: 0x40000000}, {Op: "open-handle", Path: "../escape", Disposition: 2}, {Op: "open-handle", Path: "blocked/file", Disposition: 2}} {
		r.ID = 1
		reply, _ := a.handle(r, nil)
		if reply.Status == 200 {
			t.Fatal("Handle path guard bypass")
		}
	}
}
