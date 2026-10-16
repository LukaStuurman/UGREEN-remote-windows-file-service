package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/binary"
	"encoding/json"
	"net"
	"net/netip"
	"os"
	"strings"
	"tailscale.com/ipn/ipnstate"
	"tailscale.com/types/key"
	"testing"
	"time"
	"unicode/utf16"
)

func TestDestinationIsOnlyAnExactTailnetPeer(t *testing.T) {
	for _, host := range []string{"100.64.0.1", "100.127.255.254", "fixture.example.ts.net"} {
		if !validHost(host) {
			t.Fatal(host)
		}
	}
	for _, host := range []string{"100.63.255.255", "100.128.0.1", "192.0.2.1", "127.0.0.1", "https://fixture.example.ts.net", "fixture.example.ts.net.evil", "bad_thing.ts.net", "../fixture.ts.net", "other.example.com"} {
		if validHost(host) {
			t.Fatal(host)
		}
	}
	peer := &ipnstate.PeerStatus{DNSName: "fixture.example.ts.net.", TailscaleIPs: []netip.Addr{netip.MustParseAddr("100.100.1.2")}}
	status := &ipnstate.Status{Peer: map[key.NodePublic]*ipnstate.PeerStatus{{}: peer}}
	if findPeer(status, "FIXTURE.EXAMPLE.TS.NET") != peer || findPeer(status, "100.100.1.2") != peer || findPeer(status, "other.example.ts.net") != nil {
		t.Fatal("Peer resolution escaped the authenticated peer list")
	}
}
func TestFramingKeepsBinarySeparateAndRejectsUnboundedInput(t *testing.T) {
	data := bytes.Repeat([]byte{0, 10, 13, 0xff, 0x80}, 10000)
	header, _ := json.Marshal(request{ID: 1, Op: "write", DataLength: len(data)})
	stream := append(append(append(header, '\n'), data...), []byte("{\"id\":2,\"op\":\"status\"}\n")...)
	reader := bufio.NewReaderSize(bytes.NewReader(stream), 65536)
	_, actual, err := readFrame(reader)
	if err != nil || !bytes.Equal(actual, data) {
		t.Fatal("Binary frame mismatch", err)
	}
	next, _, err := readFrame(reader)
	if err != nil || next.ID != 2 {
		t.Fatal("Next frame lost", err)
	}
	for _, raw := range []string{"{\"id\":1,\"dataLength\":4194305}\n", "{\"id\":1,\"dataLength\":-1}\n", strings.Repeat("x", 65537) + "\n"} {
		if _, _, err := readFrame(bufio.NewReaderSize(strings.NewReader(raw), 65536)); err == nil {
			t.Fatal("Unbounded frame accepted")
		}
	}
}
func TestRootTraversalAndAlternateStreamsAreRejected(t *testing.T) {
	for _, path := range []string{"../escape", "dir/../escape", "dir\\..\\escape", "/absolute", "dir//child", "dir/./child", "file:stream", "file\x00bad", ""} {
		if _, err := safePath(path, false); err == nil {
			t.Fatal(path)
		}
	}
	if path, err := safePath("", true); err != nil || path != "." {
		t.Fatal(path, err)
	}
	if path, err := safePath("dir\\file", false); err != nil || path != "dir/file" {
		t.Fatal(path, err)
	}
}

type fakeInfo struct {
	os.FileInfo
	mode os.FileMode
}

func (f fakeInfo) Mode() os.FileMode { return f.mode }
func (f fakeInfo) IsDir() bool       { return f.mode.IsDir() }

type fakeStat struct{}

func (fakeStat) Lstat(path string) (os.FileInfo, error) {
	if path == "safe" {
		return fakeInfo{mode: os.ModeDir}, nil
	}
	if path == "safe/link" {
		return fakeInfo{mode: os.ModeSymlink}, nil
	}
	return nil, os.ErrNotExist
}
func TestIntermediateLinksCannotBeTraversedOrOverwritten(t *testing.T) {
	for _, path := range []string{"safe/link", "safe/link/secret"} {
		if guard(fakeStat{}, path, true) == nil {
			t.Fatal(path)
		}
	}
	if err := guard(fakeStat{}, "safe/new", true); err != nil {
		t.Fatal(err)
	}
	if err := guard(fakeStat{}, "safe/missing/child", true); err == nil {
		t.Fatal("Missing intermediate directory accepted")
	}
}
func TestAuthorizationRequiresAnExplicitAuthKey(t *testing.T) {
	if !validAuthKey("tskey-auth-012345678901234567890123456789") {
		t.Fatal("Fixture key syntax rejected")
	}
	for _, value := range []string{"", "https://login.tailscale.com/a/fixture", "tskey-client-012345678901234567890123456789", "tskey-auth-012345678901234567890\n"} {
		if validAuthKey(value) {
			t.Fatal("Non-auth-key accepted")
		}
	}
	a := &app{}
	reply, _ := a.handle(request{ID: 1, Op: "authorize"}, []byte("fixture-secret"))
	if reply.Status == 200 || strings.Contains(reply.Error, "fixture-secret") {
		t.Fatal("Invalid authorization succeeded or leaked")
	}
	status, _ := a.status(context.Background())
	encoded, _ := json.Marshal(status)
	if bytes.Contains(encoded, []byte("authUrl")) {
		t.Fatal("Login URL exposed")
	}
}
func fixtureSecret(text string) []byte {
	units := utf16.Encode([]rune(text))
	data := make([]byte, len(units)*2)
	for i, u := range units {
		binary.LittleEndian.PutUint16(data[2*i:], u)
	}
	return data
}
func TestSMBFixtureRoundTripAndShareBoundary(t *testing.T) {
	address := os.Getenv("UGREEN_SMOKE_SMB_ADDR")
	if address == "" {
		t.Skip("Isolated Samba fixture runs in Linux CI only")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
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
	call := func(r request, data []byte) (response, []byte) {
		t.Helper()
		r.ID = 1
		reply, payload := a.handle(r, data)
		if reply.Status != 200 {
			t.Fatalf("%s: %d", r.Op, reply.Status)
		}
		return reply, payload
	}
	call(request{Op: "stat"}, nil)
	call(request{Op: "mkdir", Path: "roundtrip"}, nil)
	call(request{Op: "create", Path: "roundtrip/bytes"}, nil)
	data := make([]byte, maxData)
	for i := range data {
		data[i] = byte(i % 251)
	}
	call(request{Op: "write", Path: "roundtrip/bytes"}, data)
	_, actual := call(request{Op: "read", Path: "roundtrip/bytes", Length: len(data)}, nil)
	if !bytes.Equal(actual, data) {
		t.Fatal("File bytes changed")
	}
	call(request{Op: "write", Path: "roundtrip/bytes", Offset: -1}, []byte("end"))
	_, actual = call(request{Op: "read", Path: "roundtrip/bytes", Offset: int64(len(data)), Length: 3}, nil)
	if string(actual) != "end" {
		t.Fatal("Append failed")
	}
	call(request{Op: "times", Path: "roundtrip/bytes", Modified: time.Date(2025, 1, 2, 3, 4, 5, 0, time.UTC)}, nil)
	call(request{Op: "resize", Path: "roundtrip/bytes", Size: 17}, nil)
	call(request{Op: "rename", Path: "roundtrip/bytes", Target: "roundtrip/renamed"}, nil)
	call(request{Op: "list", Path: "roundtrip"}, nil)
	call(request{Op: "space"}, nil)
	for _, r := range []request{{Op: "delete", Path: "", Directory: true}, {Op: "read", Path: "../other-share/secret", Length: 1}, {Op: "read", Path: "blocked/secret", Length: 1}} {
		r.ID = 1
		reply, _ := a.handle(r, nil)
		if reply.Status == 200 {
			t.Fatal("Share guard bypass")
		}
	}
	call(request{Op: "delete", Path: "roundtrip/renamed"}, nil)
	call(request{Op: "delete", Path: "roundtrip", Directory: true}, nil)
}
