package main

import (
	"context"
	"errors"
	"strings"

	"tailscale.com/client/local"
	"tailscale.com/ipn"
)

// Only fixed, non-sensitive messages may leave the private component. Control
// errors can contain auth keys, auth URLs and account data: never return/log them.
type authFailure int

const (
	authUnavailable authFailure = iota
	authRejected
	authTimedOut
	authBusy
)

func (e authFailure) Error() string {
	switch e {
	case authRejected:
		return "Tailscale heeft de verbindingssleutel geweigerd. Gebruik een geldige, ongebruikte auth key voor het juiste netwerk."
	case authTimedOut:
		return "De registratie bij Tailscale duurde te lang. Er is nog geen bevestiging ontvangen. Controleer je internetverbinding en probeer opnieuw."
	case authBusy:
		return "Er loopt al een Tailscale-registratie. Wacht tot die is afgerond."
	default:
		return "De Tailscale-registratie is niet gelukt. Controleer je internetverbinding en probeer opnieuw."
	}
}

func classifyAuthError(message string) authFailure {
	text := strings.ToLower(message)
	for _, marker := range []string{"invalid authkey", "invalid auth key", "authkey expired", "auth key expired", "expired authkey", "expired auth key", "authkey not found", "auth key not found", "authkey already", "auth key already"} {
		if strings.Contains(text, marker) {
			return authRejected
		}
	}
	return authUnavailable
}

func registrationError(ctx context.Context) error {
	if errors.Is(ctx.Err(), context.DeadlineExceeded) {
		return authTimedOut
	}
	return authUnavailable
}

func registrationComplete(state string) bool {
	return state == "Running" || state == "NeedsMachineAuth"
}

func registerWithKey(ctx context.Context, lc *local.Client, key string) error {
	// Start installs a fresh control client with this auth key, but a new node
	// without a persisted node key does NOT start registration just by calling
	// Start. Match tsnet's own startup sequence and explicitly trigger login.
	if err := lc.Start(ctx, ipn.Options{AuthKey: key}); err != nil {
		return registrationError(ctx)
	}
	watcher, err := lc.WatchIPNBus(ctx, ipn.NotifyNoPrivateKeys)
	if err != nil {
		return registrationError(ctx)
	}
	defer watcher.Close()
	status, err := lc.Status(ctx)
	if err != nil {
		return registrationError(ctx)
	}
	if registrationComplete(status.BackendState) {
		return nil
	}
	// Despite its name, this is also the official auth-key registration trigger.
	// The configured key is used by the new control client. No browser is opened,
	// and no BrowseToURL, account data or raw notification is sent to the UI.
	if err := lc.StartLoginInteractive(ctx); err != nil {
		return registrationError(ctx)
	}
	for {
		notification, err := watcher.Next()
		if err != nil {
			return registrationError(ctx)
		}
		if notification.ErrMessage != nil {
			return classifyAuthError(*notification.ErrMessage)
		}
		if notification.State != nil && registrationComplete(notification.State.String()) {
			return nil
		}
	}
}
