// arcade-turn — a minimal, locked-down TURN relay for the MovieTheater arcade.
//
// WHY THIS EXISTS: a client on a guest/isolated SSID (or a hostile remote network) can reach Ziggy on
// the public-IP TCP hairpin but NOT via direct or hairpinned UDP to a worker. WebRTC then stalls at
// "negotiating" because ICE never completes. This relay is the last-resort ICE path: the client reaches
// it over TURNS (TLS/TCP) — the one route that works — and it forwards to the worker over the LAN. See
// docs/arcade/turn-relay.md.
//
// SECURITY MODEL (both are load-bearing — a default TURN install is an open internet proxy):
//  1. Ephemeral auth. Credentials are minted by the SITE per join using the coturn/REST scheme
//     (username="<expiry>:<userId>", password=base64(HMAC-SHA1(secret, username))). We recompute the
//     same HMAC from the shared secret and reject once the embedded expiry passes. Must byte-match
//     MovieTheaterConfiguration.ArcadeTurnSecret. See MovieTheater.Core.ArcadeTurnCredential.
//  2. Peer allowlist. TURN permissions are IP-scoped, so we permit relaying ONLY to the worker/Ziggy
//     addresses and deny everything else — otherwise a credential holder could relay into the LAN.
//     Enforced on every relay SOCKET (allowlistConn); by default a non-listed peer's permission is
//     granted-but-blackholed rather than 403'd — see the PermissionHandler comment for why.
//
// Two 2026-10-04 fixes live here (docs/arcade/turn-relay.md "Relay defects of 2026-10-04"): the relay
// socket's receive buffer (keyframe bursts were dropped → 1-3 s stalls) and blackhole deny mode (403s made
// Chrome flap the PeerConnection to failed, which the room page took for a crash loop).
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
		listen   = flag.String("listen", env("TURN_LISTEN", ":5349"), "TLS/TCP listen address for turns")
		realm    = flag.String("realm", env("TURN_REALM", "arcade.carpouzis.com"), "TURN realm")
		secret   = flag.String("secret", env("TURN_SECRET", ""), "shared secret (matches ArcadeTurnSecret)")
		relayIP  = flag.String("relay-ip", env("TURN_RELAY_IP", "192.168.68.69"), "address the relay uses to reach the worker (the LAN IP the worker can answer on)")
		certFile = flag.String("cert", env("TURN_CERT", ""), "TLS certificate PEM (for turn hostname)")
		keyFile  = flag.String("key", env("TURN_KEY", ""), "TLS private key PEM")
		allowed  = flag.String("allowed-peers", env("TURN_ALLOWED_PEERS", "192.168.68.69"), "comma-separated peer IPs the relay may reach") // the WORKER's address; the old WAN entry died with the 2026-09-01 CGNAT cutover
		rcvBuf   = flag.Int("relay-rcvbuf", 4<<20, "SO_RCVBUF for each allocation's UDP relay socket (bytes; 0 = OS default, which is 64 KiB on Windows and DROPS keyframe bursts)")
		quiet    = flag.Bool("quiet", false, "do not log allocation/permission/channel lifecycle events")
		denyMode = flag.String("deny-mode", env("TURN_DENY_MODE", "blackhole"), "how a non-allow-listed peer is refused: blackhole (grant the TURN permission, drop its traffic at the relay socket) or reject (403 the permission — makes Chrome flap the PeerConnection to failed, see lifecycle notes)")
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
	if *denyMode != "blackhole" && *denyMode != "reject" {
		log.Fatalf("arcade-turn: -deny-mode must be blackhole or reject, got %q", *denyMode)
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
		EventHandler: lifecycleLog(*quiet),
		ListenerConfigs: []turn.ListenerConfig{{
			Listener: tlsListener,
			RelayAddressGenerator: &bufferedRelayGenerator{
				RelayAddressGeneratorStatic: &turn.RelayAddressGeneratorStatic{
					RelayAddress: net.ParseIP(*relayIP), // reported to the worker (peer); must be LAN-reachable by it
					Address:      "0.0.0.0",
				},
				rcvBuf: *rcvBuf,
				allow:  allow, // enforced on every relay socket, in BOTH deny modes
			},
			// PermissionHandler: relay ONLY to allow-listed peers. This is what keeps the server from
			// being usable as a proxy into the rest of the network.
			//
			// In blackhole mode (the default) a non-allow-listed peer still GETS its TURN permission, and
			// the allowlist is enforced one layer down, on the relay socket itself (allowlistConn drops every
			// datagram to or from it). Why not just 403 it (reject mode, the pre-2026-10-04 behaviour): the
			// worker advertises candidates the relay must never reach (its WireGuard 10.9.0.2, its Tailscale
			// 100.100.38.103, its CGNAT srflx). Chrome turns a 403 CreatePermission into an INSTANTLY failed
			// candidate pair; when those candidates trickle in before the LAN one (they come first), every
			// pair the PeerConnection has is failed for a few milliseconds and connectionState goes
			// connecting -> failed, once per refused candidate. The room page counts two such failures
			// inside 25 s as a crash loop and kills the room ("This game keeps crashing right after
			// launch"). That race was the intermittent "relay room never connects". A granted-but-dropped
			// permission makes those pairs time out quietly instead, which the PeerConnection never
			// surfaces while its LAN pair is checking.
			PermissionHandler: func(sourceAddr net.Addr, peerIP net.IP) bool {
				if allow[peerIP.String()] {
					return true
				}
				if *denyMode == "blackhole" {
					log.Printf("perm: blackhole %s from %s (not allow-listed: permission granted, traffic dropped at the relay socket)", peerIP, sourceAddr)
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

	log.Printf("arcade-turn: turns listening on %s realm=%q relay-ip=%s allowed-peers=%s relay-rcvbuf=%d deny-mode=%s",
		*listen, *realm, *relayIP, *allowed, *rcvBuf, *denyMode)
	select {}
}

// bufferedRelayGenerator is RelayAddressGeneratorStatic plus a large receive buffer on every relay socket.
//
// WHY (2026-10-04, the 1-3 s relay stalls): pion/turn reads each allocation's UDP relay socket in ONE
// goroutine (internal/allocation Allocation.packetHandler) and writes every datagram synchronously into the
// client's TLS/TCP stream before reading the next. The worker sends a video keyframe as a back-to-back burst
// of 100-300+ KB, far faster than that loop drains, and the socket's OS-default receive buffer — 64 KiB on
// Windows — overflows and silently drops the rest of the burst. Measured with the turnlab probe: a 64 KB
// burst after idle passes 0 % loss, 128 KB loses 10 %, 300 KB loses 25 %. The browser NACKs the holes, the
// retransmits arrive as another burst and are dropped the same way, libwebrtc waits out its 3 s
// frame-wait and asks for a keyframe — a bigger burst again. That is the 1 s/2 s/3 s gap ladder and the
// keyframe-request storm. A buffer of a few MiB absorbs the burst and lets the loop catch up.
//
// It also wraps every relay socket in allowlistConn, so the peer allowlist holds at the socket no matter
// what the permission layer decided.
type bufferedRelayGenerator struct {
	*turn.RelayAddressGeneratorStatic
	rcvBuf int
	allow  map[string]bool
}

func (g *bufferedRelayGenerator) AllocatePacketConn(network string, requestedPort int) (net.PacketConn, net.Addr, error) {
	conn, addr, err := g.RelayAddressGeneratorStatic.AllocatePacketConn(network, requestedPort)
	if err != nil {
		return conn, addr, err
	}
	if g.rcvBuf > 0 {
		if b, ok := conn.(interface{ SetReadBuffer(int) error }); ok {
			if e := b.SetReadBuffer(g.rcvBuf); e != nil {
				log.Printf("relay: SetReadBuffer(%d) on %s: %v", g.rcvBuf, conn.LocalAddr(), e)
			}
		} else {
			log.Printf("relay: %T has no SetReadBuffer — relay socket keeps the OS default buffer", conn)
		}
	}
	return &allowlistConn{PacketConn: conn, allow: g.allow}, addr, nil
}

// allowlistConn is the relay socket with the peer allowlist enforced on every datagram: writes to a
// non-allow-listed address are dropped (reported as sent, so pion does not log an error per packet) and
// datagrams from one are discarded before pion sees them.
type allowlistConn struct {
	net.PacketConn
	allow map[string]bool // read-only after startup
}

func (c *allowlistConn) permitted(a net.Addr) bool {
	u, ok := a.(*net.UDPAddr)
	return ok && c.allow[u.IP.String()]
}

func (c *allowlistConn) WriteTo(p []byte, addr net.Addr) (int, error) {
	if !c.permitted(addr) {
		return len(p), nil
	}
	return c.PacketConn.WriteTo(p, addr)
}

func (c *allowlistConn) ReadFrom(p []byte) (int, net.Addr, error) {
	for {
		n, addr, err := c.PacketConn.ReadFrom(p)
		if err != nil || c.permitted(addr) {
			return n, addr, err
		}
	}
}

// lifecycleLog makes allocation/permission/channel lifecycle visible in turn.log. Before this the log held
// only permission DENIALS and close errors, so a room whose ICE never completed was indistinguishable from
// one that worked.
func lifecycleLog(quiet bool) turn.EventHandler {
	h := turn.EventHandler{
		OnAuth: func(src, _ net.Addr, _, username, _, method string, ok bool) {
			if !ok {
				log.Printf("auth: FAIL %s %q from %s", method, username, src)
			}
		},
		OnAllocationError: func(src, _ net.Addr, _, msg string) {
			log.Printf("alloc: error from %s: %s", src, msg)
		},
	}
	if quiet {
		return h
	}
	h.OnAllocationCreated = func(src, _ net.Addr, _, username, _ string, relay net.Addr, _ int) {
		log.Printf("alloc: + %s for %s (%s)", relay, src, username)
	}
	h.OnAllocationDeleted = func(src, _ net.Addr, _, username, _ string) {
		log.Printf("alloc: - for %s (%s)", src, username)
	}
	h.OnPermissionCreated = func(src, _ net.Addr, _, _, _ string, relay net.Addr, peer net.IP) {
		log.Printf("perm: + %s on %s for %s", peer, relay, src)
	}
	h.OnChannelCreated = func(src, _ net.Addr, _, _, _ string, relay, peer net.Addr, ch uint16) {
		log.Printf("chan: + 0x%x %s -> %s for %s", ch, relay, peer, src)
	}
	return h
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
