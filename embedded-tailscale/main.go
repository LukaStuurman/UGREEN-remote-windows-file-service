// UGREEN Remote Drive's private, pipe-only Tailscale/SMB transport.
package main

import (
	"bufio"
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"flag"
	"io"
	"log"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
	"unicode/utf16"

	smb "github.com/hirochachacha/go-smb2"
	"tailscale.com/ipn/ipnstate"
	"tailscale.com/tsnet"
)

const maxData = 4 << 20

type request struct {
	Handle      uint64    `json:"handle,omitempty"`
	Access      uint32    `json:"access,omitempty"`
	Sharing     uint32    `json:"sharing,omitempty"`
	Disposition uint32    `json:"disposition,omitempty"`
	Options     uint32    `json:"options,omitempty"`
	Attributes  uint32    `json:"attributes,omitempty"`
	ID          int64     `json:"id"`
	Op          string    `json:"op"`
	Host        string    `json:"host,omitempty"`
	User        string    `json:"user,omitempty"`
	Path        string    `json:"path,omitempty"`
	Target      string    `json:"target,omitempty"`
	Offset      int64     `json:"offset,omitempty"`
	Length      int       `json:"length,omitempty"`
	Size        int64     `json:"size,omitempty"`
	Modified    time.Time `json:"modified,omitempty"`
	Replace     bool      `json:"replace,omitempty"`
	Directory   bool      `json:"directory,omitempty"`
	DataLength  int       `json:"dataLength,omitempty"`
}
type response struct {
	ID         int64  `json:"id"`
	Status     int    `json:"status"`
	Error      string `json:"error,omitempty"`
	Value      any    `json:"value,omitempty"`
	DataLength int    `json:"dataLength,omitempty"`
}
type entry struct {
	Name        string    `json:"name"`
	IsDirectory bool      `json:"isDirectory"`
	Size        int64     `json:"size"`
	CreatedUtc  time.Time `json:"createdUtc"`
	ModifiedUtc time.Time `json:"modifiedUtc"`
}
type app struct {
	handlesMu   sync.Mutex
	handles     map[uint64]*openFile
	nextHandle  uint64
	unlockState func()
	authMu      sync.Mutex
	appendMu    sync.Mutex
	mu          sync.RWMutex
	server      *tsnet.Server
	host        string
	share       *smb.Share
	session     *smb.Session
	conn        net.Conn
	stateDir    string
}

func validHost(host string) bool {
	if addr, err := netip.ParseAddr(host); err == nil {
		return netip.MustParsePrefix("100.64.0.0/10").Contains(addr)
	}
	if len(host) > 253 || !strings.HasSuffix(strings.ToLower(host), ".ts.net") {
		return false
	}
	for _, label := range strings.Split(host, ".") {
		if len(label) == 0 || len(label) > 63 || label[0] == '-' || label[len(label)-1] == '-' {
			return false
		}
		for _, c := range label {
			if !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-') {
				return false
			}
		}
	}
	return true
}

// Resolve only from our authenticated tailnet's peer list, never public DNS.
func findPeer(status *ipnstate.Status, host string) *ipnstate.PeerStatus {
	for _, peer := range status.Peer {
		if strings.EqualFold(strings.TrimSuffix(peer.DNSName, "."), host) {
			return peer
		}
		for _, ip := range peer.TailscaleIPs {
			if ip.String() == host {
				return peer
			}
		}
	}
	return nil
}
func (a *app) start(host string) error {
	a.mu.Lock()
	defer a.mu.Unlock()
	if !validHost(host) {
		return os.ErrInvalid
	}
	if a.server != nil {
		if a.host != host {
			return os.ErrInvalid
		}
		return nil
	}
	unlock, err := lockState(a.stateDir)
	if err != nil {
		return err
	}
	store, err := openState(filepath.Join(a.stateDir, "identity.dpapi"))
	if err != nil {
		unlock()
		return err
	}
	server := &tsnet.Server{Dir: a.stateDir, Hostname: "ugreen-remote-drive", Store: store,
		Logf: func(string, ...any) {}, UserLogf: func(string, ...any) {}}
	if err = server.Start(); err != nil {
		server.Close()
		unlock()
		return err
	}
	a.server = server
	a.unlockState = unlock
	a.host = host
	return nil
}
func (a *app) status(ctx context.Context) (any, error) {
	a.mu.RLock()
	defer a.mu.RUnlock()
	if a.server == nil {
		return map[string]any{"state": "NotStarted", "authenticated": false}, nil
	}
	lc, err := a.server.LocalClient()
	if err != nil {
		return nil, err
	}
	status, err := lc.Status(ctx)
	if err != nil {
		return nil, err
	}
	peer := findPeer(status, a.host)
	kind := "unknown"
	if peer != nil {
		if peer.CurAddr != "" {
			kind = "direct"
		} else if peer.PeerRelay != "" {
			kind = "peer-relay"
		} else if peer.Relay != "" {
			kind = "relay"
		}
	}
	return map[string]any{"state": status.BackendState,
		"nasFound": peer != nil, "ready": status.BackendState == "Running" && peer != nil,
		"authenticated": a.share != nil, "connectionType": kind}, nil
}
func decodeSecret(secret []byte) string {
	units := make([]uint16, len(secret)/2)
	for i := range units {
		units[i] = binary.LittleEndian.Uint16(secret[i*2:])
	}
	defer clear(units)
	return string(utf16.Decode(units))
}
func validAuthKey(key string) bool {
	if !strings.HasPrefix(key, "tskey-auth-") || len(key) < 30 || len(key) > 1024 {
		return false
	}
	for _, c := range key {
		if !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_') {
			return false
		}
	}
	return true
}
func (a *app) authorize(ctx context.Context, secret []byte) error {
	if !a.authMu.TryLock() {
		return authBusy
	}
	defer a.authMu.Unlock()
	if len(secret) == 0 || len(secret) > 2048 || len(secret)%2 != 0 {
		return os.ErrInvalid
	}
	key := decodeSecret(secret)
	if !validAuthKey(key) {
		return os.ErrInvalid
	}
	a.mu.RLock()
	defer a.mu.RUnlock()
	if a.server == nil {
		return os.ErrInvalid
	}
	lc, err := a.server.LocalClient()
	if err != nil {
		return err
	}
	return registerWithKey(ctx, lc, key)
}
func (a *app) connect(ctx context.Context, user string, secret []byte) error {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.server == nil || len(secret) == 0 || len(secret) > 2560 || len(secret)%2 != 0 || len(user) == 0 || len(user) > 513 {
		return os.ErrInvalid
	}
	lc, err := a.server.LocalClient()
	if err != nil {
		return err
	}
	status, err := lc.Status(ctx)
	if err != nil {
		return err
	}
	peer := findPeer(status, a.host)
	if status.BackendState != "Running" || peer == nil || len(peer.TailscaleIPs) == 0 {
		return errors.New("tailnet-not-ready")
	}
	// No public IP, LAN fallback, subnet routing, proxy, or arbitrary destination.
	conn, err := a.server.Dial(ctx, "tcp", net.JoinHostPort(peer.TailscaleIPs[0].String(), "445"))
	if err != nil {
		return err
	}
	return a.mount(ctx, conn, user, secret)
}
func (a *app) mount(ctx context.Context, conn net.Conn, user string, secret []byte) error {
	units := make([]uint16, len(secret)/2)
	for i := range units {
		units[i] = binary.LittleEndian.Uint16(secret[i*2:])
	}
	password := string(utf16.Decode(units))
	clear(units)
	domain := ""
	if i := strings.IndexByte(user, '\\'); i >= 0 {
		domain = user[:i]
		user = user[i+1:]
	}
	initiator := &smb.NTLMInitiator{User: user, Password: password, Domain: domain}
	session, err := (&smb.Dialer{Initiator: initiator}).DialContext(ctx, conn)
	initiator.Password = ""
	password = ""
	if err != nil {
		conn.Close()
		return err
	}
	share, err := session.WithContext(ctx).Mount("Techbase")
	if err == nil {
		_, err = share.WithContext(ctx).Lstat(".")
	}
	if err != nil {
		session.Logoff()
		conn.Close()
		return err
	}
	a.closeSMB()
	a.conn = conn
	a.session = session
	a.share = share
	return nil
}
func (a *app) closeSMB() {
	if a.conn != nil {
		a.conn.Close()
	}
	a.handlesMu.Lock()
	a.handles = nil
	a.handlesMu.Unlock()
	a.share = nil
	a.session = nil
	a.conn = nil
}
func safePath(raw string, allowRoot bool) (string, error) {
	raw = strings.ReplaceAll(raw, "\\", "/")
	if raw == "" || raw == "/" {
		if allowRoot {
			return ".", nil
		}
		return "", os.ErrPermission
	}
	if strings.HasPrefix(raw, "/") {
		return "", os.ErrPermission
	}
	for _, c := range raw {
		if c < 32 || c == 127 || c == ':' {
			return "", os.ErrPermission
		}
	}
	for _, part := range strings.Split(raw, "/") {
		if part == "" || part == "." || part == ".." {
			return "", os.ErrPermission
		}
	}
	return raw, nil
}
func guard(share interface {
	Lstat(string) (os.FileInfo, error)
}, path string, missingLeaf bool) error {
	if path == "." {
		return nil
	}
	parts := strings.Split(path, "/")
	for i := range parts {
		info, err := share.Lstat(strings.Join(parts[:i+1], "/"))
		if err != nil {
			if i == len(parts)-1 && missingLeaf && errorStatus(err) == 404 {
				return nil
			}
			return err
		}
		if info.Mode()&(os.ModeSymlink|os.ModeIrregular|os.ModeDevice|os.ModeNamedPipe|os.ModeSocket) != 0 {
			return os.ErrPermission
		}
		if i < len(parts)-1 && !info.IsDir() {
			return os.ErrPermission
		}
	}
	return nil
}
func toEntry(info os.FileInfo, root bool) entry {
	name := info.Name()
	if root {
		name = ""
	}
	created := info.ModTime().UTC()
	if stat, ok := info.Sys().(*smb.FileStat); ok {
		created = stat.CreationTime.UTC()
	}
	return entry{name, info.IsDir(), info.Size(), created, info.ModTime().UTC()}
}
func (a *app) fileOp(ctx context.Context, r request, data []byte) (any, []byte, error) {
	a.mu.RLock()
	defer a.mu.RUnlock()
	if a.share == nil {
		return nil, nil, os.ErrPermission
	}
	if r.Op == "write" && r.Offset == -1 {
		a.appendMu.Lock()
		defer a.appendMu.Unlock()
	}
	share := sharedInspector{a.share.WithContext(ctx)}
	path, err := safePath(r.Path, r.Op == "stat" || r.Op == "list" || r.Op == "space")
	if err != nil {
		return nil, nil, err
	}
	if err = guard(share, path, r.Op == "create" || r.Op == "mkdir"); err != nil {
		return nil, nil, err
	}
	switch r.Op {
	case "stat":
		info, err := share.Lstat(path)
		if err != nil {
			return nil, nil, err
		}
		return toEntry(info, path == "."), nil, nil
	case "list":
		infos, err := share.ReadDir(path)
		if err != nil {
			return nil, nil, err
		}
		entries := []entry{}
		for _, info := range infos {
			if info.Mode()&(os.ModeSymlink|os.ModeIrregular|os.ModeDevice|os.ModeNamedPipe|os.ModeSocket) == 0 {
				entries = append(entries, toEntry(info, false))
			}
		}
		return entries, nil, nil
	case "read":
		if r.Offset < 0 || r.Length < 0 || r.Length > maxData {
			return nil, nil, os.ErrInvalid
		}
		f, err := share.Open(path)
		if err != nil {
			return nil, nil, err
		}
		defer f.Close()
		data := make([]byte, r.Length)
		n, err := f.ReadAt(data, r.Offset)
		if errors.Is(err, io.EOF) {
			err = nil
		}
		if err != nil {
			return nil, nil, err
		}
		if n < 0 || n > len(data) {
			return nil, nil, os.ErrInvalid
		}
		return nil, data[:n], err
	case "write":
		if r.Offset < -1 {
			return nil, nil, os.ErrInvalid
		}
		f, err := share.OpenFile(path, os.O_WRONLY, 0600)
		if err != nil {
			return nil, nil, err
		}
		defer f.Close()
		offset := r.Offset
		if offset == -1 {
			info, e := share.Lstat(path)
			if e != nil {
				return nil, nil, e
			}
			offset = info.Size()
		}
		n, err := f.WriteAt(data, offset)
		return map[string]int{"written": n}, nil, err
	case "create":
		f, err := share.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
		if err != nil {
			return nil, nil, err
		}
		return nil, nil, f.Close()
	case "mkdir":
		return nil, nil, share.Mkdir(path, 0700)
	case "resize":
		if r.Size < 0 {
			return nil, nil, os.ErrInvalid
		}
		f, err := share.OpenFile(path, os.O_WRONLY, 0600)
		if err != nil {
			return nil, nil, err
		}
		defer f.Close()
		return nil, nil, f.Truncate(r.Size)
	case "rename":
		target, err := safePath(r.Target, false)
		if err != nil {
			return nil, nil, err
		}
		if err = guard(share, target, true); err != nil {
			return nil, nil, err
		}
		if !r.Replace {
			if _, err = share.Lstat(target); err == nil {
				return nil, nil, os.ErrExist
			} else if errorStatus(err) != 404 {
				return nil, nil, err
			}
		}
		// go-smb2 Rename refuses existing destinations atomically; never delete first.
		return nil, nil, share.Rename(path, target)
	case "delete":
		info, err := share.Lstat(path)
		if err != nil {
			return nil, nil, err
		}
		if info.IsDir() != r.Directory {
			return nil, nil, os.ErrInvalid
		}
		return nil, nil, share.Remove(path)
	case "times":
		return nil, nil, share.Chtimes(path, r.Modified, r.Modified)
	case "space":
		info, err := share.Statfs(".")
		if err != nil {
			return nil, nil, err
		}
		unit := info.BlockSize() * info.FragmentSize()
		return map[string]uint64{"available": info.AvailableBlockCount() * unit, "total": info.TotalBlockCount() * unit, "free": info.FreeBlockCount() * unit}, nil, nil
	}
	return nil, nil, os.ErrInvalid
}
func errorStatus(err error) int {
	if os.IsNotExist(err) {
		return 404
	}
	if os.IsExist(err) {
		return 409
	}
	if os.IsPermission(err) {
		return 403
	}
	if errors.Is(err, os.ErrInvalid) {
		return 400
	}
	var response *smb.ResponseError
	if errors.As(err, &response) {
		switch response.Code {
		case 0xc000006d, 0xc000006a, 0xc0000064:
			return 401
		case 0xc0000034, 0xc000003a:
			return 404
		case 0xc0000022:
			return 403
		case 0x8000002d, 0xc0000279:
			return 403
		case 0xc0000035:
			return 409
		case 0xc0000101:
			return 422
		case 0xc0000043:
			return 423
		case 0xc0000033, 0xc00000ba, 0xc0000103:
			return 400
		}
	}
	return 503
}
func (a *app) handle(r request, data []byte) (response, []byte) {
	ctx, cancel := context.WithTimeout(context.Background(), 25*time.Second)
	defer cancel()
	reply := response{ID: r.ID, Status: 200}
	var err error
	var output []byte
	switch r.Op {
	case "open-handle", "handle-stat", "handle-read", "handle-write", "handle-flush", "handle-resize", "handle-reserve", "handle-rename", "handle-delete", "handle-close", "handle-times", "handle-attributes":
		reply.Value, output, err = a.handleFile(ctx, r, data)
	case "configure":
		err = a.start(r.Host)
	case "status":
		reply.Value, err = a.status(ctx)
	case "authorize":
		err = a.authorize(ctx, data)
	case "connect":
		err = a.connect(ctx, r.User, data)
	case "logout":
		a.mu.Lock()
		a.closeSMB()
		if a.server != nil {
			lc, e := a.server.LocalClient()
			if e == nil {
				err = lc.Logout(ctx)
			} else {
				err = e
			}
		}
		a.mu.Unlock()
	default:
		reply.Value, output, err = a.fileOp(ctx, r, data)
	}
	if err != nil {
		reply.Status = errorStatus(err)
		var authErr authFailure
		if errors.As(err, &authErr) && authErr == authRejected {
			reply.Status = 401
		}
		if reply.Status == 503 && r.Op != "configure" && r.Op != "status" && r.Op != "authorize" {
			a.mu.Lock()
			a.closeSMB()
			a.mu.Unlock()
		}
		reply.Error = "De Tailscale/NAS-aanvraag is mislukt."
		if r.Op == "authorize" {
			if errors.As(err, &authErr) {
				reply.Error = authErr.Error()
			} else if errors.Is(err, os.ErrInvalid) {
				reply.Error = "Vul een geldige Tailscale auth key in die begint met tskey-auth-."
			}
		}
		output = nil
	}
	reply.DataLength = len(output)
	return reply, output
}
func readFrame(reader *bufio.Reader) (request, []byte, error) {
	var r request
	header, err := reader.ReadSlice('\n')
	if err != nil {
		return r, nil, err
	}
	if err = json.Unmarshal(header, &r); err != nil {
		return r, nil, err
	}
	if r.ID < 1 || r.DataLength < 0 || r.DataLength > maxData {
		return r, nil, os.ErrInvalid
	}
	data := make([]byte, r.DataLength)
	_, err = io.ReadFull(reader, data)
	return r, data, err
}
func serve(a *app, input io.Reader, output io.Writer) {
	reader := bufio.NewReaderSize(input, 64<<10)
	var write sync.Mutex
	var pending sync.WaitGroup
	slots := make(chan struct{}, 8)
	for {
		r, data, err := readFrame(reader)
		if err != nil {
			break
		}
		slots <- struct{}{}
		pending.Add(1)
		go func() {
			defer pending.Done()
			defer func() { <-slots }()
			defer clear(data)
			reply, payload := a.handle(r, data)
			write.Lock()
			defer write.Unlock()
			if json.NewEncoder(output).Encode(reply) == nil && len(payload) > 0 {
				output.Write(payload)
			}
		}()
	}
	// Parent pipe closure closes the transport, so pending network work can finish.
	a.mu.Lock()
	a.closeSMB()
	a.mu.Unlock()
	pending.Wait()
}
func main() {
	log.SetOutput(io.Discard)
	stateDir := flag.String("state-dir", "", "private app state directory")
	flag.Parse()
	if *stateDir == "" {
		os.Exit(2)
	}
	for _, key := range []string{"TS_AUTHKEY", "TS_AUTH_KEY", "TS_CLIENT_SECRET", "TSNET_FORCE_LOGIN"} {
		os.Unsetenv(key)
	}
	os.Setenv("TS_NO_LOGS_NO_SUPPORT", "true")
	a := &app{stateDir: *stateDir}
	defer func() {
		if a.server != nil {
			a.server.Close()
		}
		if a.unlockState != nil {
			a.unlockState()
		}
	}()
	serve(a, os.Stdin, os.Stdout)
}
