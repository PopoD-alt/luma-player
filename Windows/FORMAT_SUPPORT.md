# Windows format support — review scope

This document and [CodecSupport.json](CodecSupport.json) describe the Windows port's current paths. The Windows inventory is separate from the macOS catalog: it tracks 48 non-exhaustive groups, including separate AIFF/AIFC, AU and CAF entries. These are development labels, not an exact codec count or a VLC parity percentage. A recognized extension or installed codec is not playback proof. Original sample/frame reconstruction, container conversion, Windows presentation and audible output need separate evidence. Full VLC compatibility is unfinished.

## Original Luma audio

- **RIFF/WAVE integer PCM:** 8/16/24/32-bit containers, up to eight channels. Samples remain signed, unscaled integers at their source depth. Extensible integer PCM supports valid-bit depths and lossless WAV export for the FLAC port's non-byte depths. Floating-point WAVE, RF64 and arbitrary WAVE codecs are not implemented.
- **G.711 A-law and mu-law WAVE:** original reconstruction to 16-bit PCM, with independent sample goldens.
- **IMA ADPCM WAVE:** original mono/stereo predictor and channel-block reconstruction. Complete aligned blocks and `fact` trimming are supported. Unsupported block geometry, invalid predictors and partial blocks are rejected.
- **Microsoft ADPCM WAVE tag 2:** original mono/stereo 4-bit reconstruction to 16-bit PCM. Standard and declared custom coefficient tables, signed deltas/nibbles, adaptation/clipping, `fact` trimming and complete-header partial final blocks are covered by ten exact original fixture goldens.
- **Native FLAC streams:** original parser and sample reconstruction, depths 4–32 bits, up to eight channels. All 42 existing goldens cover constant/verbatim/fixed/LPC subframes, Rice/escape residuals, wasted bits, stereo decorrelation, timing, CRC8/CRC16 and decoded MD5. Ogg/MP4-wrapped FLAC, ID3 wrappers and unsupported stream changes are rejected.
- **AIFF/AIFC:** original signed PCM 8/16/24/32-bit reconstruction, with `NONE`, `twos` and little-endian `sowt` AIFC variants, plus AIFC G.711 A-law/μ-law. Arbitrary Common/Sound Data chunk order, odd chunk padding, SSND offsets and bounded disk-block tails are handled. AIFC requires its supported format-version chunk. Sample rates must be positive normalized 80-bit values representing integer Hz; fractional rates, floating-point audio and other compressed AIFC variants are unsupported.
- **Sun AU/SND:** original big-endian signed PCM 8/16/24/32-bit and G.711 A-law/μ-law, including annotation offsets and unknown-length payloads. Floating-point and other compressed AU encodings are unsupported.

Audio decoding is a bounded whole-file prototype: at most 256 MiB input and 64 million interleaved samples. AIFF/AIFC/AU allow up to eight channels and integer sample rates of 1–1,048,575 Hz; G.711 reconstructs 16-bit samples. WAV export preserves integer precision and source depth. The AIFF/AIFC/AU suite checks 60 exact format/depth/channel vectors plus malformed/truncated inputs and additional eight-channel cases. Exact samples and successful WAV generation do not establish audible playback on a Windows device.

## Original Luma video

- **RGB24/RGB32 AVI:** original DIB frame parsing, scanline padding and orientation.
- **RLE8 AVI:** original palette/run/literal keyframes. Unsupported delta behavior is rejected.
- **Baseline MJPEG AVI:** original baseline JPEG reconstruction, quantization/Huffman tables, restart handling and bounded color conversion. Progressive JPEG, missing required tables and unsupported fields are rejected.
- **Microsoft Video 1 AVI:** original 8/16-bit solid/two/eight-color and key/delta block reconstruction. Synthetic goldens verify palettes, aliases, exact pixels, immutable checkpoints and random/concurrent seeks. Dynamic AVI palettes and unsupported geometry are rejected.

These AVI paths decode **video only**; they do not provide AVI audio-stream demuxing or A/V synchronization. Whole-file/frame bounds are explicit. Unsupported AVI codecs fail rather than being labeled as an original Luma implementation.

## Original Matroska container conversion

The Windows source now includes original EBML demuxing, bounded timeline adaptation and ISO BMFF/MP4 writing for supported local **MKV/MKA** files. It retains compressed bytes; **it does not implement new AVC, HEVC or AAC decoders**. Those bytes are subsequently decoded through installed Windows media facilities.

- One AVC/H.264 or HEVC/H.265 video track, with validated `avcC`/`hvcC` configuration and length-prefixed samples. The output retains packet coding order, presentation timestamps, random-access markers and relative track starts.
- Selectable AAC-LC audio tracks, or audio off. One selected mono/stereo/5.1 track is written at a time. AAC-LC requires 1,024-sample frames and 7,350–64,000 Hz, with matching AudioSpecificConfig. HE-AAC/SBR/PS, other channel layouts and AAC extensions are rejected by the muxer. MP3, FLAC, PCM, Opus, AC-3/E-AC-3 and other Matroska audio mappings are not implemented in this path.
- Embedded `S_TEXT/UTF8`, `S_TEXT/WEBVTT` and ASS/SSA packets can become selectable plain-text captions. Literal angle-bracket text is preserved while known markup is removed. ASS/SSA supports readable text, escapes and drawing suppression; it does not reproduce fonts, layout, positioning, animation, karaoke or vector graphics.
- PGS packet carriage and track metadata are retained, but **Windows PGS bitmap decoding/rendering is not implemented**. Preserved packets do not count as visible subtitle support. VobSub/DVB rendering remains missing.

This remains a whole-file remux prototype: **512 MiB input/retained packet budget and 500,000 packets**, one Segment, known-size Clusters, at most one video track, and bounded audio/subtitle counts. Remuxing completes before playback and needs temporary disk space. Video dimensions are bounded to 8,192 per axis and 16 million pixels. Reordered AVC/HEVC requires a random-access start, precise DefaultDuration, a complete constant-frame-rate presentation timeline and at most 16 frames of reorder lag. Open-GOP/preroll and reordered variable-frame-rate variants are unsupported. AAC timestamp precision must be finer than half a frame; gaps, overlaps and nonpositive declared packet durations fail explicitly.

The MP4 writer additionally bounds payloads to 512 MiB, individual samples to 16 MiB and movie timelines to 24 hours; nanosecond sample-table fields impose further duration/composition-offset limits. Encryption/content encodings, linked Segments, CodecDelay/preroll/trim, unsupported track operations and WebM DocType are not implemented. Non-square display dimensions, crop/stereo/alpha/projection variants and HDR/colour/display-metadata preservation are outside the supported scope. Unsupported enabled tracks fail the original remux path rather than being silently discarded.

Embedded text is bounded to 100,000 packets per track, 1 MiB per packet, 16,384 displayed characters per cue and four million displayed characters per track. Missing/zero cue durations are not invented; malformed UTF-8 and negative durations fail explicitly.

Portable structural/timing tests cover the new path. Independent native decoding on macOS matched all 360 AVC frames, 360 reordered AVC frames and 60 frames each of the HEVC 8/10-bit fixtures against their sources, and retained a 250 ms AAC relative start within 25 ms. This validates those generated containers. **Inspect the exact Windows review artifact for Matroska, B-frame and HEVC playback results; macOS decoding does not establish Windows decoder availability. Audible A/V synchronization requires real-device testing.**

## Windows system playback and remaining gaps

Build 3 routes HEVC and AAC with more than two channels to the modern Media Foundation engine. Native A/V synchronization and downmixing remain the system engine's responsibility. Local MP4 inspection reads `hvc1`/`hev1` configuration and effective AAC channels from AudioSpecificConfig, even when the legacy sample entry says stereo. A bounded, validated `hev1` to `hvc1` compatibility copy preserves compressed bytes and leaves the original untouched. It refuses in-band HEVC parameter sets, unsupported/fragmented tables, encryption and external references; it is not a decoder or universal HEVC repair. See the [Windows README](README.md#hevc--aac-51-troubleshooting) for diagnostics and temporary disk requirements.

MP4 `tx3g` and `text` plain captions support bounded sample tables and supported edit offsets. Styling and arbitrary subtitle codecs are outside this path. Caption-only unsupported variants are skipped with warnings; structural A/V errors and external references are still rejected. The new synthetic `Luma-HEVC10-AAC51.mp4` is original Main10/1920×804/25fps video with authored silent AAC-LC/48kHz/5.1 audio. Independent macOS decoding verifies its validated `hvc1` copy; the original `hev1` alias fails that decoder. This separates container-tag compatibility from compressed video decoding. Neither result establishes Windows HEVC availability or audible 5.1 playback.

Compressed formats routed through Windows media playback use Microsoft's installed facilities. Availability depends on the Windows edition, optional media components, installed codecs and each file's profile. The existing Windows CI verified load, video dimensions, seeking and a resumed clock for one synthetic AVC MP4 fixture. Only that narrow native route is marked `system` in the Windows catalog. AAC/HEVC container handling is described separately above; broad MP4/MOV, AAC, HEVC, MP3, ALAC, WMA/WMV, AV1/VP9 or other installed-codec compatibility must not be inferred from a filename or the macOS catalog.

CAF is separate from the new original AIFF/AIFC/AU paths and remains unverified. ProRes/H.263 macOS evidence does not transfer to Windows. Missing or unverified families include MPEG video, MPEG-4 Part 2/DivX/Xvid, RealVideo, Theora/Dirac, DV, JPEG2000, Cinepak/Indeo, Dolby/DTS/TrueHD, Vorbis/Opus/Speex, APE/WavPack/Musepack, AMR/GSM, MIDI/tracker formats and others in the catalog. This list is not exhaustive and does not mean an arbitrary third-party Windows installation cannot decode a listed family; it means Luma has no verified equivalent path to claim here.

## Review evidence

`Windows/Luma.Core.Tests` runs original audio/video, AIFF/AIFC/AU, Matroska demux, timeline/subtitle, MP4 mux and integration checks. `Windows/build.ps1` additionally requires a successful Windows `--smoke-test` run and PNG output before creating its review ZIP. The workflow artifacts identify the commit, SDK, resolved runtime packages and per-path smoke results. Inspect evidence for the exact revision; an older successful Windows artifact does not validate newly added paths.

Software output checks are separate from physical sound: the previous hosted Windows run had zero active audio render endpoints and reported original audio output as unverified. Neither portable decoded samples nor the independent macOS container check proves sound reaches a Windows speaker or that real-device A/V synchronization is correct. Keep that result explicit until hardware testing supplies evidence.
