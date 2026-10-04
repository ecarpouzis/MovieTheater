// turnprobe — exercise the arcade TURN relay without the arcade.
//
// Allocates through turns: (TLS/TCP) exactly as a browser does, binds a "worker" UDP socket on the
// LAN IP, and streams timestamped datagrams in one or both directions at a controlled rate/pattern.
// Both ends live in this process, so one-way delay is measured on one monotonic clock.
package main

import (
	"crypto/hmac"
	"crypto/sha1"
	"crypto/tls"
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"math"
	"net"
	"os"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/pion/turn/v4"
)

var t0 = time.Now()

func now() int64 { return int64(time.Since(t0)) }

type stat struct {
	mu       sync.Mutex
	arrivals []int64 // recv ns
	delays   []int64 // one-way ns
	maxSeq   int64
	reorder  int
	seen     map[int64]bool
	bytes    int64
	first    int64
	last     int64
}

func newStat() *stat { return &stat{seen: map[int64]bool{}, maxSeq: -1} }

func (s *stat) add(b []byte) {
	if len(b) < 16 {
		return
	}
	r := now()
	seq := int64(binary.BigEndian.Uint64(b[0:8]))
	snd := int64(binary.BigEndian.Uint64(b[8:16]))
	s.mu.Lock()
	defer s.mu.Unlock()
	if seq < 0 { // control
		return
	}
	if s.seen[seq] {
		return
	}
	s.seen[seq] = true
	if seq < s.maxSeq {
		s.reorder++
	} else {
		s.maxSeq = seq
	}
	if s.first == 0 {
		s.first = r
	}
	s.last = r
	s.bytes += int64(len(b))
	s.arrivals = append(s.arrivals, r)
	s.delays = append(s.delays, r-snd)
}

func pct(v []int64, p float64) float64 {
	if len(v) == 0 {
		return math.NaN()
	}
	c := append([]int64(nil), v...)
	sort.Slice(c, func(i, j int) bool { return c[i] < c[j] })
	i := int(math.Ceil(p/100*float64(len(c)))) - 1
	if i < 0 {
		i = 0
	}
	return float64(c[i]) / 1e6
}

type report struct {
	Label    string  `json:"label"`
	Dir      string  `json:"dir"`
	Pattern  string  `json:"pattern"`
	TargetMb float64 `json:"targetMbps"`
	Sent     int64   `json:"sent"`
	Recv     int     `json:"recv"`
	LossPct  float64 `json:"lossPct"`
	Reorder  int     `json:"reorder"`
	GotMbps  float64 `json:"gotMbps"`
	DlyP50   float64 `json:"delayP50ms"`
	DlyP95   float64 `json:"delayP95ms"`
	DlyP99   float64 `json:"delayP99ms"`
	DlyMax   float64 `json:"delayMaxMs"`
	GapP99   float64 `json:"gapP99ms"`
	GapMax   float64 `json:"gapMaxMs"`
	Gap100   int     `json:"gaps>100ms"`
	Gap250   int     `json:"gaps>250ms"`
	WriteMax float64 `json:"senderWriteMaxMs"`
	SendLate float64 `json:"senderLateMaxMs"`
}

func summarize(label, dir, pattern string, target float64, sent int64, s *stat, wmax, late int64) report {
	s.mu.Lock()
	defer s.mu.Unlock()
	var gaps []int64
	g100, g250 := 0, 0
	for i := 1; i < len(s.arrivals); i++ {
		g := s.arrivals[i] - s.arrivals[i-1]
		gaps = append(gaps, g)
		if g > 100e6 {
			g100++
		}
		if g > 250e6 {
			g250++
		}
	}
	dur := float64(s.last-s.first) / 1e9
	mb := 0.0
	if dur > 0 {
		mb = float64(s.bytes) * 8 / dur / 1e6
	}
	recv := len(s.arrivals)
	loss := 0.0
	if sent > 0 {
		loss = 100 * float64(sent-int64(recv)) / float64(sent)
	}
	return report{label, dir, pattern, target, sent, recv, loss, s.reorder, mb,
		pct(s.delays, 50), pct(s.delays, 95), pct(s.delays, 99), pct(s.delays, 100),
		pct(gaps, 99), pct(gaps, 100), g100, g250, float64(wmax) / 1e6, float64(late) / 1e6}
}

// sender emits packets of `size` bytes. pattern "even" paces evenly at rateMbps; "frames" sends a burst
// of rate/fps bytes every 1/fps, and every kfEvery a keyframe burst of kfBytes.
func sender(write func([]byte) error, rateMbps float64, size int, secs float64, pattern string, fps float64, kfBytes int, kfEvery time.Duration) (sent int64, wmax int64, lateMax int64) {
	buf := make([]byte, size)
	var seq int64
	send := func() {
		binary.BigEndian.PutUint64(buf[0:8], uint64(seq))
		binary.BigEndian.PutUint64(buf[8:16], uint64(now()))
		a := now()
		if err := write(buf); err != nil {
			log.Printf("write: %v", err)
		}
		if d := now() - a; d > wmax {
			wmax = d
		}
		seq++
	}
	start := time.Now()
	end := start.Add(time.Duration(secs * float64(time.Second)))
	switch pattern {
	case "frames":
		perFrame := int(rateMbps * 1e6 / 8 / fps)
		interval := time.Duration(float64(time.Second) / fps)
		nextKf := start
		for i := 0; ; i++ {
			due := start.Add(time.Duration(i) * interval)
			if due.After(end) {
				break
			}
			if d := time.Until(due); d > 0 {
				time.Sleep(d)
			} else if -d > time.Duration(lateMax) {
				lateMax = int64(-d)
			}
			n := perFrame
			if kfBytes > 0 && !due.Before(nextKf) {
				n = kfBytes
				nextKf = due.Add(kfEvery)
			}
			for b := 0; b < n; b += size {
				send()
			}
		}
	default:
		pps := rateMbps * 1e6 / 8 / float64(size)
		for {
			el := time.Since(start)
			if time.Now().After(end) {
				break
			}
			want := int64(el.Seconds() * pps)
			if want-seq > int64(pps) && int64(el-time.Duration(float64(seq)/pps*float64(time.Second))) > lateMax {
				lateMax = int64(el - time.Duration(float64(seq)/pps*float64(time.Second)))
			}
			for seq < want {
				send()
			}
			time.Sleep(500 * time.Microsecond)
		}
	}
	return seq, wmax, lateMax
}

func main() {
	var (
		dial        = flag.String("dial", "127.0.0.1:5349", "TCP address to dial (what the client connects to)")
		sni         = flag.String("sni", "turn.carpouzis.com", "TLS SNI / verify name")
		insecure    = flag.Bool("insecure", false, "skip cert verification")
		secretFile  = flag.String("secretfile", `D:\ArcadeStorage\turn\secret.txt`, "shared secret file")
		realm       = flag.String("realm", "arcade.carpouzis.com", "realm")
		peerIP      = flag.String("peer", "192.168.68.69", "peer (worker-side) IP to bind")
		rates       = flag.String("rates", "0.2,2,8,16,30", "comma list of Mbps")
		size        = flag.Int("size", 1200, "packet bytes")
		secs        = flag.Float64("secs", 8, "seconds per step")
		dirs        = flag.String("dirs", "down,up", "down=peer->client (video direction), up=client->peer")
		pattern     = flag.String("pattern", "even", "even|frames")
		fps         = flag.Float64("fps", 60, "frames per second for -pattern frames")
		kf          = flag.Int("kf", 0, "keyframe burst bytes (frames pattern)")
		kfEvery     = flag.Duration("kfevery", 2*time.Second, "keyframe interval")
		label       = flag.String("label", "", "label for report lines")
		holdOnly    = flag.Duration("hold", 0, "just allocate and hold for this long (lifecycle tests)")
		inboundFrom = flag.String("inbound-from", "", "security test: create a permission for this IP, send 20 datagrams FROM it to the relay address, report how many reached the client")
	)
	flag.Parse()
	raw, err := os.ReadFile(*secretFile)
	if err != nil {
		log.Fatal(err)
	}
	secret := strings.TrimSpace(strings.TrimPrefix(string(raw), "\ufeff"))
	user := fmt.Sprintf("%d:probe", time.Now().Add(time.Hour).Unix())
	mac := hmac.New(sha1.New, []byte(secret))
	mac.Write([]byte(user))
	pass := base64.StdEncoding.EncodeToString(mac.Sum(nil))

	tc := time.Now()
	conn, err := tls.DialWithDialer(&net.Dialer{Timeout: 5 * time.Second}, "tcp", *dial, &tls.Config{ServerName: *sni, InsecureSkipVerify: *insecure})
	if err != nil {
		log.Fatalf("tls dial %s: %v", *dial, err)
	}
	log.Printf("tls up to %s (local %s) in %v", conn.RemoteAddr(), conn.LocalAddr(), time.Since(tc).Round(time.Millisecond))
	cl, err := turn.NewClient(&turn.ClientConfig{
		STUNServerAddr: "127.0.0.1:3478", TURNServerAddr: "127.0.0.1:3478", // nominal: STUNConn ignores the address
		Conn: turn.NewSTUNConn(conn), Username: user, Password: pass, Realm: *realm,
	})
	if err != nil {
		log.Fatal(err)
	}
	defer cl.Close()
	if err := cl.Listen(); err != nil {
		log.Fatal(err)
	}
	ta := time.Now()
	relay, err := cl.Allocate()
	if err != nil {
		log.Fatalf("allocate: %v", err)
	}
	defer relay.Close()
	log.Printf("allocated relay %s in %v", relay.LocalAddr(), time.Since(ta).Round(time.Millisecond))

	peer, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.ParseIP(*peerIP)})
	if err != nil {
		log.Fatal(err)
	}
	defer peer.Close()
	_ = peer.SetReadBuffer(8 << 20)
	peerAddr := peer.LocalAddr()

	var cur struct {
		sync.Mutex
		down, up *stat
	}
	relayAddrCh := make(chan net.Addr, 1)
	go func() { // client side reader (down direction)
		b := make([]byte, 2048)
		for {
			n, _, err := relay.ReadFrom(b)
			if err != nil {
				return
			}
			cur.Lock()
			s := cur.down
			cur.Unlock()
			if s != nil {
				s.add(b[:n])
			}
		}
	}()
	go func() { // peer side reader (up direction)
		b := make([]byte, 2048)
		once := false
		for {
			n, from, err := peer.ReadFrom(b)
			if err != nil {
				return
			}
			if !once {
				once = true
				relayAddrCh <- from
			}
			cur.Lock()
			s := cur.up
			cur.Unlock()
			if s != nil {
				s.add(b[:n])
			}
		}
	}()
	if *inboundFrom != "" {
		evil, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.ParseIP(*inboundFrom)})
		if err != nil {
			log.Fatal(err)
		}
		_, _ = relay.WriteTo(make([]byte, 16), evil.LocalAddr()) // CreatePermission for that IP (granted or 403)
		time.Sleep(300 * time.Millisecond)
		s := newStat()
		cur.Lock()
		cur.down = s
		cur.Unlock()
		pkt := make([]byte, 100)
		for i := 0; i < 20; i++ {
			binary.BigEndian.PutUint64(pkt, uint64(i))
			binary.BigEndian.PutUint64(pkt[8:], uint64(now()))
			_, _ = evil.WriteTo(pkt, relay.LocalAddr())
			time.Sleep(10 * time.Millisecond)
		}
		time.Sleep(500 * time.Millisecond)
		fmt.Printf("inbound-from %s -> relay %s: %d of 20 datagrams reached the client\n", *inboundFrom, relay.LocalAddr(), len(s.arrivals))
		return
	}
	// Handshake: client->peer creates the permission and tells the peer the relayed source address.
	hello := make([]byte, 16)
	binary.BigEndian.PutUint64(hello, uint64(math.MaxUint64))
	tp := time.Now()
	var relayFrom net.Addr
	for i := 0; i < 50 && relayFrom == nil; i++ {
		_, _ = relay.WriteTo(hello, peerAddr)
		select {
		case relayFrom = <-relayAddrCh:
		case <-time.After(100 * time.Millisecond):
		}
	}
	if relayFrom == nil {
		log.Fatalf("peer never heard from the relay")
	}
	log.Printf("permission + first datagram via relay in %v (relay source %s)", time.Since(tp).Round(time.Millisecond), relayFrom)
	if *holdOnly > 0 {
		time.Sleep(*holdOnly)
		return
	}
	time.Sleep(300 * time.Millisecond)

	for _, d := range strings.Split(*dirs, ",") {
		for _, rs := range strings.Split(*rates, ",") {
			var r float64
			fmt.Sscanf(rs, "%g", &r)
			s := newStat()
			cur.Lock()
			if d == "down" {
				cur.down, cur.up = s, nil
			} else {
				cur.up, cur.down = s, nil
			}
			cur.Unlock()
			var write func([]byte) error
			if d == "down" {
				write = func(b []byte) error { _, e := peer.WriteTo(b, relayFrom); return e }
			} else {
				write = func(b []byte) error { _, e := relay.WriteTo(b, peerAddr); return e }
			}
			sent, wmax, late := sender(write, r, *size, *secs, *pattern, *fps, *kf, *kfEvery)
			time.Sleep(1500 * time.Millisecond) // drain
			rep := summarize(*label, d, *pattern, r, sent, s, wmax, late)
			j, _ := json.Marshal(rep)
			fmt.Println(string(j))
		}
	}
}
