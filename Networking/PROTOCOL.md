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

- `GET /v1/info` is public: `{ "version":1,"name":"Luma on …","id":"…","pairingRequired":true,"progressReporting":true }`.
  `progressReporting` is an optional capability: missing/false means the client
  must not send progress updates to that older/read-only host.
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
- New hosts add optional per-item `computerProgressSeconds`,
  `computerUpdatedAtUnixSeconds`, `computerCompleted`, `mobileProgressSeconds`,
  `mobileDurationSeconds`, `mobileUpdatedAtUnixSeconds` and `mobileCompleted`.
  Unknown phone progress is omitted/null; zero is a known beginning position.
  Older hosts remain readable: clients fall back to `resumeSeconds` for the
  computer position. `resumeSeconds` retains its legacy resume/restart semantics;
  the separate computer progress can show a completed item without resuming at
  its end. Timestamps are server-generated Unix seconds (fractions allowed),
  finite and between zero and 253,402,300,799. Completion fields are booleans.
- `POST /v1/progress`, header `Authorization: Bearer <token>`, JSON
  `{ "itemId":"opaque-id","positionSeconds":120,"durationSeconds":1200,"completed":false }`,
  reports only the phone's playback position. Body size is at most 4,096 bytes;
  IDs must refer to a live playlist item. Times must be finite numbers in the
  existing one-year range, and completion must be a boolean. The host clamps the
  position to its known media duration; a phone-reported duration is informational
  and never replaces authoritative desktop media metadata. When the host duration
  is unknown it may retain the bounded value separately as `mobileDurationSeconds`.
  Success returns status200 and version1 JSON with accepted `positionSeconds`,
  `durationSeconds`, `completed` and `updatedAtUnixSeconds`.
  Auth/session and item liveness are rechecked before persistence; stopped,
  expired/revoked or removed sessions/items cannot update the library. Invalid
  data is400, missing media404, oversized bodies400/413, invalid authentication401,
  and more than30 reports per token per minute429. Clients coalesce reports and
  normally send every5seconds while playing, plus final pause/seek/leave updates.
  Progress writes never open arbitrary paths or trigger desktop play/pause/seek.
- `GET` or `HEAD /v1/media/<opaque-id>?token=<token>` streams the original
  playlist file directly from disk. The token is permitted in this URL because
  native media players do not consistently support Authorization headers.
  Implement single HTTP byte ranges, `206`/`Content-Range`, `416` for invalid
  or unsatisfiable ranges, `Accept-Ranges: bytes`, exact `Content-Length`,
  and connection-close semantics. Do not load the whole file in memory.

Every authenticated request rechecks the live playlist. Removed/missing media
is 404. Clients never receive absolute filesystem paths. No upload, delete,
remote filesystem browsing or desktop playback-control endpoint is included.
The only authenticated write is bounded progress metadata for a live item.
Use constant-time token comparison and bounded simultaneous connections. Stop
must cancel listeners, discovery, active streams and revoke pairing tokens.

Android refreshes the playlist while connected. Playback uses the stream URL
and resume position, supports seeking via byte ranges, and stays on the phone;
the host must remain on, the desktop app open and sharing enabled. The companion
streams desktop original files on the same trusted Wi-Fi using discovery or a
manual desktop URL; it does not provide a standalone local-file picker.

Both device positions remain distinct. Interfaces label Computer and Phone,
including completion and last position. Opening an item on Android prefers its
existing session-local checkpoint, otherwise the latest available saved device
position. Moving an already-running player to the other device's position requires
an explicit resume action; arriving progress reports never seek a live player. Phone reports persist on the host with the library. Sharing
must be enabled for fresh cross-device updates; this is not cloud synchronization.

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

Verification is checkpoint-specific. Mac's approved bundled runtime and Windows'
actual app-local route pass playback/sharing checks without an installed VLC
fallback. Android's approved ARM64 debug owner copy passes both production
servers, true-cold preparation, real native clock/seeking, surface restoration,
fullscreen and independent re-signing. Earlier quiet x64 checks passed in
workflow43767190446; currentx64emulator Wi-Fi loss is recorded in the Android
guide and is not a playback pass. Physical Wi-Fi discovery, firewalls and speaker/A/V
synchronization remain target-device review; these fixture checks do not imply
universal format/device compatibility or production publication approval.
