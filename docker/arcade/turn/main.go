// arcade-turn — a minimal, locked-down TURN relay for the MovieTheater arcade.
//
// WHY THIS EXISTS: a client on a guest/isolated SSID (or a hostile remote network) can reach Ziggy on
// the public-IP TCP hairpin but NOT via direct or hairpinned UDP to a worker. WebRTC then stalls at
// "negotiating" because ICE never completes. This relay is the last-resort ICE path: the client reaches
// it over TURNS (TLS/TCP) — the one route that works — and it forwards to the worker over the LAN. See
// docs/arcade/turn-relay.md.
//
// SECURITY MODEL (both are load-bearing — a default TURN install is an open internet proxy):
//   1. Ephemeral auth. Credentials are minted by the SITE per join using the coturn/REST scheme
//      (username="<expiry>:<userId>", password=base64(HMAC-SHA1(secret, username))). We recompute the
//      same HMAC from the shared secret and reject once the embedded expiry passes. Must byte-match
//      MovieTheaterConfiguration.ArcadeTurnSecret. See MovieTheater.Core.ArcadeTurnCredential.
//   2. Peer allowlist. TURN permissions are IP-scoped, so we permit relaying ONLY to the worker/Ziggy
//      addresses and deny everything else — otherwise a credential holder could relay into the LAN.
//
// TURNS only (TLS/TCP): a UDP TURN listener would hit the very UDP-hairpin wall the isolated client
// already fails on, so it would not help. Do not add a plain-UDP listener expecting it to.
package main

import (
	"crypto/hmac"
	"crypto/sha1"
	"crypto/tls"
	"encoding/base64"
	"flag"
	"log"
	"net"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/pion/turn/v4"
)

func env(key, def string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return def
}

func main() {
	var (
		listen    = flag.String("listen", env("TURN_LISTEN", ":5349"), "TLS/TCP listen address for turns")
		realm     = flag.String("realm", env("TURN_REALM", "arcade.carpouzis.com"), "TURN realm")
		secret    = flag.String("secret", env("TURN_SECRET", ""), "shared secret (matches ArcadeTurnSecret)")
		relayIP   = flag.String("relay-ip", env("TURN_RELAY_IP", "192.168.68.69"), "address the relay uses to reach the worker (the LAN IP the worker can answer on)")
		certFile  = flag.String("cert", env("TURN_CERT", ""), "TLS certificate PEM (for turn hostname)")
		keyFile   = flag.String("key", env("TURN_KEY", ""), "TLS private key PEM")
		allowed   = flag.String("allowed-peers", env("TURN_ALLOWED_PEERS", "192.168.68.69,98.15.249.217"), "comma-separated peer IPs the relay may reach")
	)
	flag.Parse()

	if *secret == "" {
		log.Fatal("arcade-turn: -secret (TURN_SECRET) is required — an unauthenticated relay is an open proxy")
	}
	if *certFile == "" || *keyFile == "" {
		log.Fatal("arcade-turn: -cert and -key are required — turns needs a publicly-trusted cert for the turn hostname")
	}

	// Peer allowlist. Deny by default; permit only the explicitly listed worker/Ziggy addresses.
	allow := map[string]bool{}
	for _, ip := range strings.Split(*allowed, ",") {
		if ip = strings.TrimSpace(ip); ip != "" {
			allow[ip] = true
		}
	}

	cert, err := tls.LoadX509KeyPair(*certFile, *keyFile)
	if err != nil {
		log.Fatalf("arcade-turn: load cert: %v", err)
	}
	tlsListener, err := tls.Listen("tcp", *listen, &tls.Config{Certificates: []tls.Certificate{cert}})
	if err != nil {
		log.Fatalf("arcade-turn: listen %s: %v", *listen, err)
	}

	server, err := turn.NewServer(turn.ServerConfig{
		Realm: *realm,
		// AuthHandler: recompute the REST-scheme password from the shared secret and enforce expiry.
		AuthHandler: func(username, realm string, srcAddr net.Addr) ([]byte, bool) {
			exp, _, ok := parseUsername(username)
			if !ok || time.Now().Unix() > exp {
				log.Printf("auth: reject %q from %s (bad/expired)", username, srcAddr)
				return nil, false
			}
			mac := hmac.New(sha1.New, []byte(*secret))
			mac.Write([]byte(username))
			password := base64.StdEncoding.EncodeToString(mac.Sum(nil))
			return turn.GenerateAuthKey(username, realm, password), true
		},
		ListenerConfigs: []turn.ListenerConfig{{
			Listener: tlsListener,
			RelayAddressGenerator: &turn.RelayAddressGeneratorStatic{
				RelayAddress: net.ParseIP(*relayIP), // reported to the worker (peer); must be LAN-reachable by it
				Address:      "0.0.0.0",
			},
			// PermissionHandler: relay ONLY to allow-listed peers. This is what keeps the server from
			// being usable as a proxy into the rest of the network.
			PermissionHandler: func(sourceAddr net.Addr, peerIP net.IP) bool {
				if allow[peerIP.String()] {
					return true
				}
				log.Printf("perm: deny relay to %s from %s (not allow-listed)", peerIP, sourceAddr)
				return false
			},
		}},
	})
	if err != nil {
		log.Fatalf("arcade-turn: server: %v", err)
	}
	defer server.Close()

	log.Printf("arcade-turn: turns listening on %s realm=%q relay-ip=%s allowed-peers=%s",
		*listen, *realm, *relayIP, *allowed)
	select {}
}

// parseUsername splits the REST-scheme "<expiryUnix>:<userId>" username. Returns the expiry.
func parseUsername(u string) (expiry int64, userID string, ok bool) {
	i := strings.IndexByte(u, ':')
	if i <= 0 {
		return 0, "", false
	}
	exp, err := strconv.ParseInt(u[:i], 10, 64)
	if err != nil {
		return 0, "", false
	}
	return exp, u[i+1:], true
}
