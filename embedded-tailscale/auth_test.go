package main

import (
	"context"
	"encoding/json"
	"errors"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"tailscale.com/client/local"
	"tailscale.com/derp/derpserver"
	"tailscale.com/ipn"
	"tailscale.com/ipn/store"
	"tailscale.com/net/netns"
	"tailscale.com/net/stun"
	"tailscale.com/tailcfg"
	"tailscale.com/tsnet"
	"tailscale.com/tstest/integration/testcontrol"
	"tailscale.com/types/key"
)

const fixtureAuthKey = "tskey-auth-offline-fixture-not-a-real-auth-key"

func TestRegistrationStartsLoginAndObservesActualResult(t *testing.T) {
	for _, mode := range []string{"running", "approval", "rejected", "timeout"} {
		t.Run(mode, func(t *testing.T) {
			var started, watched, triggered atomic.Bool
			notifications := make(chan ipn.Notify, 1)
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				switch r.URL.Path {
				case "/localapi/v0/start":
					var opts ipn.Options
					if json.NewDecoder(r.Body).Decode(&opts) != nil || opts.AuthKey != fixtureAuthKey {
						t.Error("Explicit fixture auth key was not supplied to Start")
					}
					started.Store(true)
					w.WriteHeader(http.StatusNoContent)
				case "/localapi/v0/watch-ipn-bus":
					if !started.Load() {
						t.Error("Watcher opened before the new control client")
					}
					watched.Store(true)
					w.Header().Set("Content-Type", "application/json")
					w.WriteHeader(http.StatusOK)
					w.(http.Flusher).Flush()
					select {
					case notification := <-notifications:
						_ = json.NewEncoder(w).Encode(notification)
						w.(http.Flusher).Flush()
					case <-r.Context().Done():
						return
					}
					<-r.Context().Done()
				case "/localapi/v0/status":
					_, _ = w.Write([]byte(`{"BackendState":"NeedsLogin"}`))
				case "/localapi/v0/login-interactive":
					if !started.Load() || !watched.Load() {
						t.Error("Login triggered without auth key or error observer")
					}
					triggered.Store(true)
					w.WriteHeader(http.StatusNoContent)
					state := ipn.Running
					if mode == "approval" {
						state = ipn.NeedsMachineAuth
					}
					if mode == "rejected" {
						message := "invalid authkey: " + fixtureAuthKey + " https://login.example.invalid/private"
						notifications <- ipn.Notify{ErrMessage: &message}
					} else if mode != "timeout" {
						notifications <- ipn.Notify{State: &state}
					}
				default:
					t.Error("Unexpected LocalAPI operation")
					w.WriteHeader(http.StatusNotFound)
				}
			}))
			defer server.Close()
			lc := &local.Client{Dial: func(ctx context.Context, _, _ string) (net.Conn, error) {
				return (&net.Dialer{}).DialContext(ctx, "tcp", server.Listener.Addr().String())
			}}
			ctx, cancel := context.WithTimeout(context.Background(), time.Second)
			defer cancel()
			err := registerWithKey(ctx, lc, fixtureAuthKey)
			if !triggered.Load() {
				t.Fatal("Start alone did not trigger registration")
			}
			switch mode {
			case "running", "approval":
				if err != nil {
					t.Fatal(err)
				}
			case "rejected":
				if !errors.Is(err, authRejected) {
					t.Fatalf("Expected sanitized rejection, got %v", err)
				}
			case "timeout":
				if !errors.Is(err, authTimedOut) {
					t.Fatalf("Expected timeout, got %v", err)
				}
			}
			if err != nil && (strings.Contains(err.Error(), fixtureAuthKey) || strings.Contains(err.Error(), "https://")) {
				t.Fatal("Control error leaked")
			}
		})
	}
}

func TestAuthFailureNeverReturnsRawControlMessages(t *testing.T) {
	for _, raw := range []string{"invalid authkey: " + fixtureAuthKey, "authkey expired", "auth key already used", "network problem https://login.example.invalid/private"} {
		err := classifyAuthError(raw)
		if strings.Contains(err.Error(), raw) || strings.Contains(err.Error(), "https://") || strings.Contains(err.Error(), fixtureAuthKey) {
			t.Fatal("Raw control-plane data exposed")
		}
	}
}

func startLocalRelay(t *testing.T) *tailcfg.DERPMap {
	t.Helper()
	relay := derpserver.New(key.NewNode(), func(string, ...any) {})
	https := httptest.NewTLSServer(derpserver.Handler(relay))
	udp, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.ParseIP("127.0.0.1")})
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan struct{})
	go func() {
		defer close(done)
		buffer := make([]byte, 65536)
		for {
			count, source, err := udp.ReadFromUDPAddrPort(buffer)
			if err != nil {
				return
			}
			id, err := stun.ParseBindingRequest(buffer[:count])
			if err == nil {
				_, _ = udp.WriteToUDPAddrPort(stun.Response(id, source), source)
			}
		}
	}()
	t.Cleanup(func() { https.CloseClientConnections(); https.Close(); relay.Close(); _ = udp.Close(); <-done })
	return &tailcfg.DERPMap{Regions: map[tailcfg.DERPRegionID]*tailcfg.DERPRegion{1: {
		RegionID: 1, RegionCode: "fixture", Nodes: []*tailcfg.DERPNode{{
			Name: "fixture", RegionID: 1, HostName: "127.0.0.1", IPv4: "127.0.0.1", IPv6: "none",
			STUNPort: udp.LocalAddr().(*net.UDPAddr).Port, DERPPort: https.Listener.Addr().(*net.TCPAddr).Port,
			InsecureForTests: true, STUNTestIP: "127.0.0.1",
		}},
	}}}
}

// Exercise the real tsnet/control-client registration exchange on disposable CI
// runners only. All identities, state, keys and control responses are synthetic;
// no real tailnet, user credentials, production DERP map or NAS is involved.
func TestRealTsnetRegistersNewMachineWithKeyAfterInitialNeedsLogin(t *testing.T) {
	if os.Getenv("GITHUB_ACTIONS") != "true" {
		t.Skip("Isolated tsnet startup is restricted to disposable CI runners")
	}
	t.Setenv("TS_NO_LOGS_NO_SUPPORT", "true")
	netns.SetEnabled(false)
	t.Cleanup(func() { netns.SetEnabled(true) })
	for _, mode := range []string{"running", "approval", "rejected"} {
		t.Run(mode, func(t *testing.T) {
			derpMap := startLocalRelay(t)
			control := &testcontrol.Server{RequireAuthKey: fixtureAuthKey, RequireMachineAuth: mode == "approval",
				DERPMap: derpMap, Logf: func(string, ...any) {}}
			control.HTTPTestServer = httptest.NewUnstartedServer(control)
			control.HTTPTestServer.Start()
			t.Cleanup(control.HTTPTestServer.Close)
			directory := t.TempDir()
			state, err := store.NewFileStore(func(string, ...any) {}, filepath.Join(directory, "synthetic-test-state.json"))
			if err != nil {
				t.Fatal("Fixture state initialization failed")
			}
			s := &tsnet.Server{Dir: directory, Store: state, ControlURL: control.HTTPTestServer.URL,
				Hostname: "ugreen-auth-fixture", Logf: func(string, ...any) {}, UserLogf: func(string, ...any) {}}
			if err := s.Start(); err != nil {
				t.Fatalf("Fixture tsnet failed to start: %v", err)
			}
			t.Cleanup(func() { _ = s.Close() })
			lc, err := s.LocalClient()
			if err != nil {
				t.Fatal(err)
			}
			ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
			defer cancel()
			key := fixtureAuthKey
			if mode == "rejected" {
				key += "-wrong"
			}
			err = registerWithKey(ctx, lc, key)
			if mode == "rejected" {
				if !errors.Is(err, authRejected) || control.NumNodes() != 0 {
					t.Fatalf("Rejected key must not register a node: %v", err)
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			if control.NumNodes() != 1 {
				t.Fatal("Valid key failed to create exactly one machine")
			}
			status, err := lc.Status(ctx)
			if err != nil {
				t.Fatal(err)
			}
			want := "Running"
			if mode == "approval" {
				want = "NeedsMachineAuth"
			}
			if status.BackendState != want {
				t.Fatalf("State %s; want %s", status.BackendState, want)
			}
			if mode == "approval" {
				nodeKey := control.AllNodes()[0].Key
				if !control.CompleteDeviceApproval(control.HTTPTestServer.URL, control.HTTPTestServer.URL+"/admin", &nodeKey) {
					t.Fatal("Fixture approval failed")
				}
				for status.BackendState != "Running" && ctx.Err() == nil {
					time.Sleep(50 * time.Millisecond)
					status, err = lc.Status(ctx)
					if err != nil {
						t.Fatal(err)
					}
				}
				if status.BackendState != "Running" || control.NumNodes() != 1 {
					t.Fatal("Approval did not finish without another key or duplicate node")
				}
			}
		})
	}
}
