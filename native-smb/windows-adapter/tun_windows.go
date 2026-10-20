// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause
// Techbase modification of tailscale.com v1.104.1 net/tstun/tun_windows.go:
// isolate this application's adapter GUID; keep the upstream default otherwise.

package tstun

import (
	"crypto/sha256"
	"fmt"
	"os"
	"strings"

	"github.com/tailscale/wireguard-go/tun"
	"golang.org/x/sys/windows"
	"golang.zx2c4.com/wireguard/windows/tunnel/winipcfg"
)

func techbaseAdapterGUID(name string) (windows.GUID, error) {
	if !strings.HasPrefix(name, "UGREEN") {
		return windows.GUIDFromString("{37217669-42da-4657-a55b-0d995d328250}")
	}
	digest := sha256.Sum256([]byte("Techbase Windows SMB adapter v1/" + name))
	digest[6] = (digest[6] & 0x0f) | 0x80
	digest[8] = (digest[8] & 0x3f) | 0x80
	return windows.GUIDFromString(fmt.Sprintf("{%x-%x-%x-%x-%x}",
		digest[:4], digest[4:6], digest[6:8], digest[8:10], digest[10:16]))
}

func init() {
	tun.WintunTunnelType = "Tailscale"
	name := "Tailscale"
	for _, arg := range os.Args[1:] {
		if strings.HasPrefix(arg, "--tun=UGREEN") {
			name = strings.TrimPrefix(arg, "--tun=")
		}
	}
	guid, err := techbaseAdapterGUID(name)
	if err != nil {
		panic(err)
	}
	tun.WintunStaticRequestedGUID = &guid
}

func interfaceName(dev tun.Device) (string, error) {
	guid, err := winipcfg.LUID(dev.(*tun.NativeTun).LUID()).GUID()
	if err != nil {
		return "", err
	}
	return guid.String(), nil
}
