# Manual connectivity matrix for civsharp.nickthorsen.com:47830 (T356)

The project owner's relay: Docker Compose, TLS and ALPN `unciv-relay/1` from the relay itself (setup B, see SETUP.md "Worked example"), NPM's Let's Encrypt certificate mounted read-only.
Status words: **VERIFIED** (someone ran it and reported the result), **VERIFIED-LOCAL** (run against a local relay binary with a self-made certificate, not the real server),
**UNVERIFIED** (nobody has run it, or the run was not independent), **BLOCKED** (needs something that does not exist yet). Nothing here is assumed: a row stays UNVERIFIED until a result is written next to it.

Open rows R2-R4, R7-R9 and R13 are tracked by ticket T394 (T355 for R10-R12). Honest limits of what is recorded: the owner's check below ran from a machine sharing the owner's public IP, so it did not prove that the port is reachable from the Internet
(router hairpinning or LAN routing could have made it work) and it did not test any game client.

| # | Check | Command or action | Status | Evidence / who |
|---|---|---|---|---|
| R1 | TLS handshake, trusted certificate, ALPN, from the owner's own network | `openssl s_client -connect civsharp.nickthorsen.com:47830 -alpn unciv-relay/1 </dev/null` | VERIFIED (not independent) | Owner, 2026-10-31: trusted Let's Encrypt `*.nickthorsen.com` certificate, `Verify return code: 0`, `ALPN protocol: unciv-relay/1`. Ran on a machine sharing the owner's public IP. |
| R2 | The same R1 from a different network (mobile data, a VPS, a friend's home) | same command | UNVERIFIED | Nobody has run it. This is the row that proves the port forward and firewall. |
| R3 | `GET /v1/info` and `/healthz` on the server itself | `curl -s http://127.0.0.1:47831/v1/info` (must show `"contact":"me@nickthorsen.com"`) and `/healthz` | UNVERIFIED on the real server | Before 2026-10-31 the owner reported `contact=""`; the owner then set `UNCIV_RELAY_CONTACT` and restarts; the new value was not reported back. |
| R4 | Container health and restart policy | `docker compose ps` shows `healthy`; `docker compose restart relay` returns to healthy; reboot of the host brings it back (`restart: unless-stopped`) | UNVERIFIED | Owner reported the deployment and policy, not the results of these commands. |
| R5 | Local-binary equivalent of R1/R3: relay with a PEM chain, `-alpn unciv-relay/1` negotiates, `-alpn h2` is refused (`no application protocol`), `/v1/info` shows the contact, `/healthz` is `ok` | see "Local run" below | VERIFIED-LOCAL | Agent T356, 2026-10-31, relay built from this commit, self-signed certificate for `civsharp.test`, via `openssl s_client -CAfile`. Not the real domain or the real certificate. |
| R6 | Certificate swap picked up without a restart (renewal path) | replace `fullchain.pem`/`privkey.pem`, watch for `certificate-reloaded` | VERIFIED-LOCAL | Agent T356: the relay logged `certificate-reloaded reason=ok` after the files were replaced. The reviewer (T356 review) repeated it with certbot-style relative symlinks `live/npm-1/*.pem` -> `archive/npm-1/*1.pem`, repointed to `*2.pem` as a renewal does: `certificate-reloaded` and the new serial was served, no restart. NOT verified on the real server through NPM (row R9), and not inside a container. |
| R7 | Dedicated-server join through the real relay: `Unciv.Server --port 0 --relay unciv-relay://civsharp.nickthorsen.com:47830 --list --print-invite`, then `curl .../v1/games` on the server lists it | SETUP.md step 8 | UNVERIFIED | Nobody has run it against the real server. |
| R8 | Client matrix: a real game host and joiner through the relay from two different networks | Linux client | UNVERIFIED | No one has run it. |
| R9 | NPM renewal: after the next real renewal the relay serves the new certificate (`openssl s_client ... | openssl x509 -noout -enddate`) and the container user can read the renewed key | wait for a renewal (about 60 days after issue) | UNVERIFIED | Cannot be run before then; the read permission of the key for uid 10001 in the owner's mount is also unrecorded. |
| R10 | Windows client reaches the relay | T355 | BLOCKED (T355) | Needs a Windows machine; tracked by T355. |
| R11 | macOS client reaches the relay | T355 | BLOCKED (T355) | Needs a macOS machine; tracked by T355. |
| R12 | Android client reaches the relay (mobile data, then Wi-Fi) | T355 | BLOCKED (T355) | Needs a device; tracked by T355. |
| R13 | IPv6 (AAAA record, if any) answers like R1 | `openssl s_client -connect '[addr]:47830' ...` | UNVERIFIED | Not run; only matters if the domain has an AAAA record. |

## How to run the rows that are open

From a machine on a different network than the server:

```
openssl s_client -connect civsharp.nickthorsen.com:47830 -servername civsharp.nickthorsen.com -alpn unciv-relay/1 </dev/null 2>&1 | grep -E "subject=|Verif|ALPN"
```

Expected: `Verification: OK`, `Verify return code: 0 (ok)`, `ALPN protocol: unciv-relay/1`. Failure shapes: a timeout means the router or firewall does not forward 47830; `connection refused` means
nothing publishes it (`RELAY_TUNNEL_PUBLISH=0.0.0.0:47830`); a verification error means the wrong certificate path or an expired certificate. If the public IP of that machine is the owner's, the row is still not independent.

On the server: `curl -s http://127.0.0.1:47831/v1/info`, `docker compose ps`, `docker compose logs --tail 50 relay`.

## Local run (what R5 and R6 did)

Relay built with `dotnet publish Unciv.Relay -c Release`, started with `UNCIV_RELAY_CERT`/`_KEY` pointing at a self-made `fullchain.pem`/`privkey.pem` laid out as `live/npm-1/`, `UNCIV_RELAY_PUBLIC_NAME=civsharp.test`,
`UNCIV_RELAY_CONTACT=me@nickthorsen.com`, loopback ports 47930/47931. Observed: `/healthz` -> `ok rooms=0 splices=0`; `/v1/info` -> `"contact":"me@nickthorsen.com"`;
`openssl s_client -alpn unciv-relay/1` -> `Verification: OK`, `ALPN protocol: unciv-relay/1`; `-alpn h2` -> `tlsv1 alert no application protocol`; replacing the PEM files -> `certificate-reloaded reason=ok`.
Docker itself was not available on the agent's machine (Podman only), so the container, the read-only mount and `restart: unless-stopped` were not exercised by this run.

Recording a result: change the row's Status and Evidence in this file in the same commit that adds the evidence (who ran it, when, from which network); never delete an UNVERIFIED row.
