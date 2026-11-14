# Tehran 3x-ui server: required Xray and kernel settings

Applied 2026-11-15 on the Tehran panel server (`tehran-server`, 1 vCPU / 1 GB, IPv4 only).
Panel backups taken before the changes: `/root/x-ui-before-sniff-fix-20261115.db` and
`/root/x-ui-before-perf-tuning-20261115.db`.

## Why

* **The server has no IPv6.** Phones often send Cloudflare IPv6 addresses (Darvazeh, Google, …)
  through the tunnel. With sniffing `routeOnly: true` Xray dialled that IPv6 address and the
  connection hung until timeout, so pages loaded only after several retries.
* **QUIC from Iran is unreliable.** Cloudflare advertises HTTP/3, so Chrome sent `api1.daryajan.com`
  requests as QUIC (UDP 443) through the tunnel. From Tehran only 20–50% of UDP 443 probes to Cloudflare
  (0% to Google) get an answer, while TCP has no loss, so QUIC connections stalled and the app showed
  "connection failed". Blocking UDP 443 makes browsers use HTTPS over TCP immediately.
* **REALITY dials its target for every connection.** `www.microsoft.com` takes ~0.5 s to complete
  a TLS handshake from Tehran, which added that delay to every new connection.

## Inbounds (panel → Inbounds)

| Inbound | Setting |
|---|---|
| all three (10080, 8443, 9443) | Sniffing on, `http, tls, quic`, **Route only: off** |
| 8443 REALITY vision, 9443 REALITY xhttp | Target `127.0.0.1:443`, SNI `iran2.daryajan.com` (the server's own Caddy site: "steal from yourself") |
| subscription order (`subSortIndex`) | 8443 = 1, 9443 = 2, 10080 = 3 (the Cloudflare `iran1` route is the slowest for buyers in Iran) |

## Xray template (panel → Xray configs)

```json
"dns": { "servers": ["localhost"], "queryStrategy": "UseIPv4", "disableCache": false, "tag": "dns-internal" },
"routing": { "rules": [ api rule, { "type": "field", "network": "udp", "port": "443", "outboundTag": "blocked" }, ... ] },
"outbounds": [ { "tag": "direct", "protocol": "freedom",
                 "settings": { "domainStrategy": "UseIPv4", "finalRules": [ ... unchanged ... ] } }, ... ]
```

## Kernel (`/etc/sysctl.d/99-darvazeh-vpn.conf`, `/etc/modules-load.d/bbr.conf`)

BBR + `fq`, 16 MB socket buffers, `tcp_mtu_probing=1`, `tcp_slow_start_after_idle=0`,
`tcp_notsent_lowat=16384`, `tcp_fastopen=3`, larger backlogs, `vm.swappiness=10`.

## Measured from the server through each inbound (client1, after the changes)

| Config | New connection to `api1.daryajan.com` | IPv6 destination | Download |
|---|---|---|---|
| REALITY vision 8443 | 0.37 s (was 0.53–0.66 s) | 200 in 0.44 s (was a 12 s timeout) | 141 Mbps |
| REALITY xhttp 9443 | 0.32 s | 200 in 0.45 s | 136 Mbps |
| XHTTP TLS via Caddy | 0.41 s | 200 in 0.38 s | 116 Mbps |

Client tip: existing apps must **update the subscription** to pick up the new SNI and order.
