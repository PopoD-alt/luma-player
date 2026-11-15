# Luma local sharing protocol v1

Desktop sharing is off by default. Starting it exposes only the current playlist
to paired clients on the local network, until sharing is stopped. No router port
forwarding, cloud account, or whole-library download endpoint is used. HTTP is
for trusted home networks; video playback necessarily buffers bytes on the phone.
The Android client does not persist the streamed media as a downloaded file.

Discovery: Bonjour/DNS-SD service `_luma-player._tcp.` with TXT `v=1` and `id`
(a non-secret instance identifier). Manual entry of the desktop's displayed
`http://<LAN-address>:<port>` must also work when multicast is unavailable.

Requests are HTTP/1.1 with bounded headers/body, no CORS, no directory serving,
and no arbitrary input paths. Desktop implementations bind a random available
port. Only local/private/link-local/loopback source addresses are accepted.

- `GET /v1/info` is public: `{ "version":1,"name":"Luma on …","id":"…","pairingRequired":true }`.
- `POST /v1/pair`, JSON `{ "code":"123456" }`, checks the desktop's displayed
  random six-digit code, expiry, and rate limits. Success returns
  `{ "token":"<random 256-bit token>","name":"…" }`. Tokens are in memory
  only, revoked when sharing stops, and never logged. Invalid codes get 401;
  too many attempts get 429. Pairing codes expire after 10 minutes.
- `GET /v1/playlist`, header `Authorization: Bearer <token>`, returns
  `{ "version":1,"name":"…","items":[{"id":"opaque-id","title":"…",
  "durationSeconds":0,"resumeSeconds":0,"mimeType":"video/mp4",
  "streamPath":"/v1/media/opaque-id?token=<token>"}],"truncated":false,"totalItems":1 }`.
  Wire limits are 1,000 items, 512 UTF-16 code units per title (valid Unicode),
  finite times from zero to 31,536,000 seconds, and 4 MiB of JSON. A bounded
  response sets `truncated:true` and preserves the available `totalItems` count;
  clients show that limit rather than pretending the entire playlist appeared.
- `GET` or `HEAD /v1/media/<opaque-id>?token=<token>` streams the original
  playlist file directly from disk. The token is permitted in this URL because
  native media players do not consistently support Authorization headers.
  Implement single HTTP byte ranges, `206`/`Content-Range`, `416` for invalid
  or unsatisfiable ranges, `Accept-Ranges: bytes`, exact `Content-Length`,
  and connection-close semantics. Do not load the whole file in memory.

Every authenticated request rechecks the live playlist. Removed/missing media
is 404. Clients never receive absolute filesystem paths. No upload, delete,
remote filesystem browsing or desktop playback-control endpoint is included.
Use constant-time token comparison and bounded simultaneous connections. Stop
must cancel listeners, discovery, active streams and revoke pairing tokens.

Android refreshes the playlist while connected. Playback uses the stream URL
and resume position, supports seeking via byte ranges, and stays on the phone;
the host must remain on, the desktop app open and sharing enabled. The companion
streams desktop original files on the same trusted Wi-Fi using discovery or a
manual desktop URL; it does not provide a standalone local-file picker.

Request/header reads have short deadlines. Authenticated media writes allow up
to 15 minutes of backpressure for a paused reader, while stop/removal still cancels
immediately. Both Mac and Windows hold an idle-system-sleep prevention request
while media streams are active, releasing it after the last stream ends. Merely
enabling sharing does not hold that request. Forced sleep/lid closure still
interrupts streams; the request does not keep the desktop app running.

Interop checks: `python3 Scripts/run-android-integration.py` starts the production
portable/Windows implementation. On an Apple Silicon Mac,
`python3 Scripts/run-android-integration.py --desktop macos` starts the production
Mac implementation. Both share original synthetic fixtures only and keep pairing
metadata in a temporary private file. `--exercise-video-recovery` is a debug-only
negative test that withholds initial video output once and requires successful
software recovery; normal playback tests keep the hardware-first path.

Verification is checkpoint-specific. Mac's approved bundled runtime and Windows
checkpoint `df964b2` have native sharing/playback evidence, including the Windows
active-stream power request. Windows custom runtime's compiler/linker probe
passed; its full source build and native/independent audit remain pending.
Android's custom ARM64 library component is
independently approved for private owner review, but the later stock-runtime x64
diagnostics identified blocking first-use font initialization. The background
preparation fix passes cold-cache ARM64 tests; its fresh quiet x64 rerun remains
pending. Component approval does not imply production publication approval or
universal format/device compatibility.
