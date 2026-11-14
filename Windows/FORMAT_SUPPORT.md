# Windows format support — review scope

This catalog describes the Windows port's implemented paths. A codec name or filename extension is not playback proof. Portable golden tests verify original sample/frame reconstruction; Windows UI, output devices and system-decoder playback must be checked on Windows. This is not full VLC compatibility.

## Original Luma audio

- **RIFF/WAVE integer PCM:** 8/16/24/32-bit containers, up to eight channels. Samples remain signed, unscaled integers at their source depth. Extensible integer PCM supports valid-bit depths and lossless WAV export for the FLAC port's non-byte depths. Floating-point WAVE, RF64 and arbitrary WAVE codecs are not implemented.
- **G.711 A-law and mu-law WAVE:** original reconstruction to 16-bit PCM, with independent sample goldens.
- **IMA ADPCM WAVE:** original mono/stereo predictor and channel-block reconstruction. Complete aligned blocks and `fact` trimming are supported. Unsupported block geometry, invalid predictors and partial blocks are rejected.
- **Microsoft ADPCM WAVE tag 2:** original mono/stereo 4-bit reconstruction to 16-bit PCM. Standard and declared custom coefficient tables, signed deltas/nibbles, adaptation/clipping, `fact` trimming and complete-header partial final blocks are covered by ten exact original fixture goldens.
- **Native FLAC streams:** original parser and sample reconstruction, depths 4–32 bits, up to eight channels. All 42 existing goldens cover constant/verbatim/fixed/LPC subframes, Rice/escape residuals, wasted bits, stereo decorrelation, timing, CRC8/CRC16 and decoded MD5. Ogg/MP4-wrapped FLAC, ID3 wrappers and unsupported stream changes are rejected.

Audio decoding is a bounded whole-file prototype: at most 256 MiB compressed input and 64 million interleaved samples. WAV export preserves integer precision and source depth; actual Windows audio output and device behavior require native validation.

## Original Luma video

- **RGB24/RGB32 AVI:** original DIB frame parsing, scanline padding and orientation.
- **RLE8 AVI:** original palette/run/literal keyframes. Unsupported delta behavior is rejected.
- **Baseline MJPEG AVI:** original baseline JPEG reconstruction, quantization/Huffman tables, restart handling and bounded color conversion. Progressive JPEG, missing required tables and unsupported fields are rejected.
- **Microsoft Video 1 AVI:** original 8/16-bit solid/two/eight-color and key/delta block reconstruction. Synthetic goldens verify palettes, aliases, exact pixels, immutable checkpoints and random/concurrent seeks. Dynamic AVI palettes and unsupported geometry are rejected.

These AVI paths decode **video only**; they do not provide AVI audio-stream demuxing or A/V synchronization. Whole-file/frame bounds are explicit. Unsupported AVI codecs fail rather than being labeled as an original Luma implementation.

## Windows system playback and unfinished ports

Compressed formats routed through Windows media playback use Microsoft's installed facilities, not a new Luma compressed decoder. Availability depends on the Windows edition, optional media components, installed codecs and each file's profile. A portable test, hardware flag or codec enumeration does not verify this path. In particular, broad MP4/H.264/AAC/HEVC or other native-format compatibility requires real playback, seek and audio checks on the target machine.

The macOS original Matroska/WebM demux/remux path has **not** been ported. Embedded ASS/SSA and PGS subtitle handling, alternate Matroska audio tracks and Opus preroll/trim support from the Mac path are not Windows features yet. SRT/WebVTT text subtitles are separate local-file features. ProRes, VP9, AV1, AC-3/E-AC-3, Opus and other codec families must not be inferred from the Mac support catalog or claimed as verified Windows support.

## Review evidence

`Windows/Luma.Core.Tests` runs the original audio/video goldens and foundation checks. `Windows/build.ps1` additionally requires a successful Windows `--smoke-test` run and PNG output before creating its review ZIP. The workflow artifacts identify the commit, SDK and resolved runtime packages. Inspect that run's logs/screenshots; this document itself does not establish a successful Windows test run.
