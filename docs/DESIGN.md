# Design notes

Volume Mixer is one Windows tray executable that serves a web client to phones on the same network. This document explains the architecture and the reasoning behind its two core choices: plain HTTP with token pairing, and event-driven state with aggressive reconnection.

## Principles

- First use: scan a QR code and control the volume in under ten seconds. No app install, no certificate warning, no account.
- Every later use: open the saved page and be live in under two seconds, automatically.
- PC side: one exe, a tray icon, zero configuration, survives reboots.
- Pure LAN. No cloud relay, ever.

## Why plain HTTP

Browsers cannot pin self-signed certificates, so HTTPS on a LAN means a scary interstitial on every phone and nothing gained. The threat model is a home network where WPA2/3 already encrypts the radio; what actually needs protecting is authorization (who may control the audio), and that is solved with a pairing token:

- The server generates a random 128-bit token on first run and stores it in `%LOCALAPPDATA%\VolumeMixer\settings.json`.
- The QR encodes `http://<ip>:<port>/#t=<token>`. The token rides in the URL fragment, so it never appears in HTTP request lines or logs. The client stores it and presents it on every hub connection; the server validates or rejects with 401. Loopback connections are trusted.
- Static files stay open (the HTML is not a secret); only the hub is gated. "Reset pairing" in the tray rotates the token and invalidates every previously scanned code.

The phone's built-in camera app opens the QR directly, which removes any need for an in-app scanner or camera permissions and works identically on iOS and Android.

## Server

A single .NET project (`net10.0-windows...`): WinForms tray + Kestrel + SignalR + NAudio, published as one self-contained exe with the built web client embedded inside it (`ManifestEmbeddedFileProvider`).

- **Audio worker.** Every CoreAudio object lives on one dedicated MTA thread. Hub commands, CoreAudio callbacks, the reconciliation timer and the peak poll are all funneled through a single queue, so mixer state has exactly one writer and there are no COM apartment surprises.
- **Event-driven state.** Session created/expired notifications, per-session volume events, endpoint volume events and default-device changes all push deltas to every connected phone. A reconciliation sweep every 10 seconds guarantees convergence even if an event is missed.
- **Peak meters.** Session and endpoint peaks are polled at 10 Hz, only while at least one phone is connected, and frames stop when everything is silent.
- **Media.** Now-playing metadata, album art, transport controls and the timeline come from the Windows media session API (SMTC). Positions are sent as anchors (position at send time plus playback rate); clients extrapolate locally, so no periodic position traffic exists. Broadcasts are deduplicated against the extrapolated position.
- **Device switching** uses the same `IPolicyConfig` interface every switcher utility relies on. **Mic control** targets the default communications capture device. Twice a second the server lists the apps recording from that mic (its active capture sessions, minus its own); by default the mic row only appears while that list is non-empty, i.e. during calls, naming the app. A capture endpoint only meters while some app streams from it, which during a call is the call app. A phone that pins the row asks the server to hold its own discarded capture stream open so the meter moves outside calls too, except on Bluetooth mics, where opening one would drop the headset into call-quality audio. Sessions on the default communications output are tracked alongside the multimedia one when they differ, since call apps often play there.
- **Networking.** The advertised IP comes from the UDP-connect trick (connect a datagram socket to a public address and read the local endpoint; no packet is sent), which returns the address on the interface with the default route and ignores VPN and virtual adapters. The port is fixed (default 6002) so QR codes, bookmarks and firewall state stay valid. Kestrel binds without admin rights; the only OS prompt is the standard firewall allow on first run, and a tray action repairs the rule if it was denied.
- **Tray.** QR window, copy link, open on this PC, autostart toggle (HKCU Run key), firewall repair, pairing reset, quit. A named mutex keeps the app single-instance; launching a second copy pops the QR window of the first.

## Client

Vue 3 + Vite, built into the server's `wwwroot` and served from inside the exe. No external requests of any kind.

- **Reconnection.** The SignalR retry policy never gives up (250 ms growing to a 5 s cap, forever). Because phones freeze timers in background tabs, recovery is driven by lifecycle events (`visibilitychange`, `pageshow`, `focus`, `online`): the moment the user returns, the client reconnects immediately, bypassing any backoff.
- **Resync by snapshot.** Every (re)connect receives the full mixer state and the client replaces everything it has. Reconnect equals resync, so there is no incremental-sync bug class.
- **Echo suppression.** While a fader is being dragged (and briefly after), inbound updates for that one channel are ignored so the slider never fights the finger. Slider sends are throttled to about one per 75 ms with a trailing send.
- **Local niceties** stored per phone in localStorage: hidden channels, custom channel order, keep-screen-on preference (NoSleep video technique, since the Wake Lock API needs HTTPS).

## Failure behavior

| Scenario | Behavior |
|---|---|
| Phone locked, then unlocked | lifecycle event, immediate reconnect, fresh snapshot, live in about a second |
| Wi-Fi drop or AP roam | `online` event plus infinite retry, auto-recovers |
| PC reboot | autostart, same port and token, phones reconnect unattended |
| PC got a new IP | tray warns and regenerates the QR; phones re-scan |
| App opens or closes on the PC | session notification pushes the change immediately |
| Volume changed on the PC itself | session event pushes to all phones |
| Two phones drag the same fader | last write wins at the server; echo suppression keeps the active phone stable |
| Output device switched | full re-enumerate and snapshot broadcast |
| Port taken by another app | next free port is used and the tray explains that phones must re-scan |
| Firewall allow was denied | "Fix firewall" tray action repairs the rules with one elevated command |

## Repo layout

```
server/VolumeMixer/   .NET tray app: Program (mutex, bootstrap), Tray/, Web/ (host, hub,
                      auth, endpoints), Audio/ (worker, models, PolicyConfig), Media/ (SMTC)
client/               Vue 3 + Vite phone UI (lib/mixer.ts is the store and connection)
.github/workflows/    build on push, release exe on tag
```

Dev loop: run the server with `dotnet run`, then `npm run dev` in `client/`. Vite proxies the hub, icons and media endpoints to `localhost:6002`, so the UI hot-reloads against real audio.
