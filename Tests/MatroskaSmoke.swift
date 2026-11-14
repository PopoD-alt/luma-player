import Foundation
import AVFoundation
import CoreMedia
import AudioToolbox
import CoreVideo

private struct FixtureSample {
    var bytes: Data
    var pts: Double
    var duration: Double
    var keyframe: Bool
    var decodeTime: Double? = nil
}

private struct FixtureTextPacket {
    let start: Double
    let duration: Double
    let bytes: Data
}

private struct FixtureTextTrack {
    let number: UInt8
    let codec: String
    let name: String
    let language: String
    let packets: [FixtureTextPacket]
    var isDefault = false
    var isForced = false
    var codecPrivate = Data()
}

private func fail(_ detail: String) -> NSError { NSError(domain: "MatroskaSmoke", code: 1, userInfo: [NSLocalizedDescriptionKey: detail]) }

@main struct MatroskaSmoke {
    static func main() async throws {
        setbuf(stdout, nil)
        let fixtureRoot = URL(fileURLWithPath: CommandLine.arguments.dropFirst().first ?? "Tests/Fixtures", isDirectory: true)
        let temp = FileManager.default.temporaryDirectory.appendingPathComponent("Luma-Matroska-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: temp, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temp) }
        let videoAsset = AVURLAsset(url: fixtureRoot.appendingPathComponent("Luma-Demo.mp4"))
        let videoTrack = try await videoAsset.loadTracks(withMediaType: .video).first!
        print("Reading AVC fixture")
        let (video, videoFormat) = try compressedSamples(asset: videoAsset, track: videoTrack)
        let dims = CMVideoFormatDescriptionGetDimensions(videoFormat)
        guard let atoms = CMFormatDescriptionGetExtension(videoFormat, extensionKey: kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms) as? [String: Any],
              let avcC = atoms["avcC"] as? Data else { throw fail("Native fixture lacks avcC") }
        let audioURL = temp.appendingPathComponent("tone.m4a")
        guard let exporter = AVAssetExportSession(asset: AVURLAsset(url: fixtureRoot.appendingPathComponent("Luma-Tone.wav")), presetName: AVAssetExportPresetAppleM4A) else { throw fail("AAC fixture encoder unavailable") }
        exporter.outputURL = audioURL; exporter.outputFileType = .m4a
        print("Encoding AAC fixture")
        await exporter.export()
        guard exporter.status == .completed else { throw exporter.error ?? fail("AAC fixture encoding failed") }
        let audioAsset = AVURLAsset(url: audioURL)
        let audioTrack = try await audioAsset.loadTracks(withMediaType: .audio).first!
        print("Reading AAC fixture")
        let (audio, audioFormat) = try compressedSamples(asset: audioAsset, track: audioTrack)
        let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(audioFormat)!.pointee
        let rates = [96000,88200,64000,48000,44100,32000,24000,22050,16000,12000,11025,8000,7350]
        guard let frequencyIndex = rates.firstIndex(of: Int(asbd.mSampleRate)) else { throw fail("Unexpected fixture AAC sample rate") }
        let config = UInt16(2 << 11 | frequencyIndex << 7 | Int(asbd.mChannelsPerFrame) << 3)
        let asc = Data([UInt8(config >> 8), UInt8(config & 255)])
        print("Fixture: \(video.count) AVC frames, \(audio.count) AAC packets, \(asbd.mSampleRate) Hz, \(asbd.mChannelsPerFrame) channels")

        for mode in 0...3 {
            let input = temp.appendingPathComponent("lace-\(mode).mkv")
            let output = temp.appendingPathComponent("lace-\(mode).mov")
            let data = makeMatroska(video: video, audio: audio, avcC: avcC, asc: asc,
                width: Int(dims.width), height: Int(dims.height), sampleRate: asbd.mSampleRate,
                channels: Int(asbd.mChannelsPerFrame), laceMode: mode)
            try data.write(to: input)
            print("Remuxing lace \(mode)")
            let result = try await MatroskaRemuxer.remux(input: input, output: output)
            precondition(result.codecSummary.contains("system decoding"))
            precondition(abs(result.duration - 12.0) < 0.06, "Timeline duration changed: \(result.duration)")
            print("Native decoding output lace \(mode)")
            let (videoStart, audioStart, videoCount, audioCount) = try await decodedOutput(output)
            print("Decoded starts: video=\(videoStart), audio=\(audioStart)")
            precondition(abs(videoStart) < 0.003, "Video start changed: \(videoStart)")
            precondition(abs((audioStart - videoStart) - 0.25) < 0.003, "A/V offset changed: \(audioStart - videoStart)")
            precondition(videoCount == video.count && audioCount > 0, "Native decode did not produce both tracks")
            print("PASS: lace \(mode), signed negative relative timestamps, SimpleBlock/BlockGroup, native decode \(videoCount) video frames / \(audioCount) audio frames, A/V offset \(audioStart - videoStart)s")
            if mode == 0 {
                let existing = try Data(contentsOf: output)
                do { _ = try await MatroskaRemuxer.remux(input: input, output: output); throw fail("Existing output was accepted") }
                catch let error as MatroskaRemuxError { precondition(error.localizedDescription.contains("already exists")) }
                let after = try Data(contentsOf: output)
                precondition(after == existing)
            }
        }
        for (name, configHex, profile) in [("Luma-HEAACv1.m4a", "131056e598", "HE-AAC"),
                                            ("Luma-HEAACv2.m4a", "130856e59d4880", "HE-AAC v2")] {
            let source = AVURLAsset(url: fixtureRoot.appendingPathComponent(name))
            let sourceTrack = try await source.loadTracks(withMediaType: .audio).first!
            let (packets, _) = try compressedSamples(asset: source, track: sourceTrack)
            let hex = Array(configHex)
            let heConfig = Data(stride(from: 0, to: hex.count, by: 2).map { UInt8(String(hex[$0...$0 + 1]), radix: 16)! })
            let input = temp.appendingPathComponent(name + ".mkv")
            let output = temp.appendingPathComponent(name + ".mov")
            // Deliberately encode OutputSamplingFrequency before SamplingFrequency.
            try makeMatroska(video: video, audio: packets, avcC: avcC, asc: heConfig,
                width: Int(dims.width), height: Int(dims.height), sampleRate: 24000,
                channels: 2, laceMode: 1, outputSampleRate: 48000).write(to: input)
            let result = try await MatroskaRemuxer.remux(input: input, output: output)
            let (videoStart, audioStart, videoCount, audioCount) = try await decodedOutput(output, audioSampleRate: 48000, audioChannels: 2)
            // AVAssetReader includes the leading edit's silence in PCM output.
            let expectedAudio = packets.count * 2048 + 12000
            print("HE decode: \(profile), \(packets.count) packets, \(videoCount) video / \(audioCount) audio frames, expected ~\(expectedAudio), offset \(audioStart - videoStart)")
            guard result.codecSummary.contains(profile) && abs(result.duration - 12) < 0.06 else { throw fail("HE profile/duration changed") }
            guard abs(audioStart - videoStart - 0.25) < 0.003 else { throw fail("HE A/V offset changed") }
            guard videoCount == video.count && abs(audioCount - expectedAudio) <= 2048 else { throw fail("HE decode frame count \(audioCount), expected ~\(expectedAudio)") }
            let outputAsset = AVURLAsset(url: output)
            let outputAudio = try await outputAsset.loadTracks(withMediaType: .audio).first!
            let descriptions = try await outputAudio.load(.formatDescriptions)
            guard let richest = CMAudioFormatDescriptionGetRichestDecodableFormat(descriptions[0])?.pointee.mASBD,
                  richest.mSampleRate == 48000, richest.mChannelsPerFrame == 2,
                  richest.mFormatID == (profile == "HE-AAC v2" ? kAudioFormatMPEG4AAC_HE_V2 : kAudioFormatMPEG4AAC_HE) else { throw fail("HE format layers were lost during remux") }
            print("PASS: \(profile) original Matroska remux, native 48kHz stereo decode \(audioCount) samples, A/V offset retained, reversed sample-rate metadata")
        }
        let reorderedAsset = AVURLAsset(url: fixtureRoot.appendingPathComponent("Luma-BFrames.mp4"))
        let reorderedTrack = try await reorderedAsset.loadTracks(withMediaType: .video).first!
        let (reorderedVideo, reorderedFormat) = try compressedSamples(asset: reorderedAsset, track: reorderedTrack)
        guard zip(reorderedVideo, reorderedVideo.dropFirst()).contains(where: { $1.pts < $0.pts }),
              let reorderedAtoms = CMFormatDescriptionGetExtension(reorderedFormat, extensionKey: kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms) as? [String: Any],
              let reorderedConfig = reorderedAtoms["avcC"] as? Data else { throw fail("B-frame fixture does not contain reordered AVC") }
        let reorderedInput = temp.appendingPathComponent("bframes.mkv")
        let reorderedOutput = temp.appendingPathComponent("bframes.mov")
        try makeMatroska(video: reorderedVideo, audio: audio, avcC: reorderedConfig, asc: asc,
            width: Int(dims.width), height: Int(dims.height), sampleRate: asbd.mSampleRate,
            channels: Int(asbd.mChannelsPerFrame), laceMode: 1).write(to: reorderedInput)
        _ = try await MatroskaRemuxer.remux(input: reorderedInput, output: reorderedOutput)
        let (reorderedStart, reorderedAudioStart, reorderedCount, _) = try await decodedOutput(reorderedOutput)
        guard reorderedCount == reorderedVideo.count && abs(reorderedStart) < 0.003 && abs(reorderedAudioStart - reorderedStart - 0.25) < 0.003 else { throw fail("Reordered AVC lost frames or changed A/V offset") }
        print("PASS: original synthetic AVC B-frames, native decoded all \(reorderedCount) frames, A/V offset retained, rounded BlockDuration accepted")

        for depth in [8, 10] {
            let name = "Luma-HEVC\(depth)"
            let hevcAsset = AVURLAsset(url: fixtureRoot.appendingPathComponent(name + ".mov"))
            guard let hevcTrack = try await hevcAsset.loadTracks(withMediaType: .video).first else { throw fail("Missing generated HEVC fixture") }
            let (hevcVideo, hevcFormat) = try compressedSamples(asset: hevcAsset, track: hevcTrack)
            let hevcDimensions = CMVideoFormatDescriptionGetDimensions(hevcFormat)
            let fps = Double(try await hevcTrack.load(.nominalFrameRate))
            let hvcc = try Data(contentsOf: fixtureRoot.appendingPathComponent(name + ".hvcC"))
            guard hvcc.count >= 23, Int(hvcc[17] & 7) + 8 == depth,
                  CMFormatDescriptionGetMediaSubType(hevcFormat) == kCMVideoCodecType_HEVC,
                  hevcVideo.count == 60, fps > 0 else { throw fail("HEVC fixture bit depth, codec, count or timing is wrong") }
            let input = temp.appendingPathComponent(name + ".mkv")
            let output = temp.appendingPathComponent(name + ".mov")
            try makeMatroska(video: hevcVideo, audio: audio, avcC: hvcc, asc: asc,
                width: Int(hevcDimensions.width), height: Int(hevcDimensions.height), sampleRate: asbd.mSampleRate,
                channels: Int(asbd.mChannelsPerFrame), laceMode: 3,
                videoCodec: "V_MPEGH/ISO/HEVC", videoFrameDuration: 1 / fps).write(to: input)
            let result = try await MatroskaRemuxer.remux(input: input, output: output)
            let (videoStart, audioStart, videoCount, audioCount) = try await decodedOutput(output)
            // These video fixtures are two seconds; their three-second audio
            // legitimately determines the movie's longer duration.
            guard result.codecSummary.contains("HEVC"), result.duration > 2.9, result.duration < 3.5,
                  videoCount == 60, audioCount > 0,
                  abs(audioStart - videoStart - 0.25) < 0.003 else { throw fail("HEVC\(depth) remux lost frames, duration or synchronization") }
            print("PASS: HEVC\(depth) Matroska → native decode \(videoCount) video frames / \(audioCount) audio samples, A/V offset retained, movie duration \(result.duration)s")
        }

        let assText = FixtureTextTrack(number: 4, codec: "S_TEXT/ASS", name: "ASS Demo", language: "en", packets: [
            FixtureTextPacket(start: 0.75, duration: 1.25, bytes: Data(#"1,0,Default,,0,0,0,,{\an8}Example\NLine, with commas &amp; more"#.utf8))
        ], isDefault: true, codecPrivate: Data("[Script Info]\nScriptType: v4.00+\n[Events]\n".utf8))
        let utf8Text = FixtureTextTrack(number: 5, codec: "S_TEXT/UTF8", name: "UTF8 Demo", language: "en", packets: [
            FixtureTextPacket(start: 0.9, duration: 0.8, bytes: Data("<i>Original caption</i>".utf8))
        ], isForced: true)
        let multipleInput = temp.appendingPathComponent("alternate-audio-text.mkv")
        let multipleOutput = temp.appendingPathComponent("alternate-audio-text.mov")
        try makeMatroska(video: video, audio: audio, avcC: avcC, asc: asc,
            width: Int(dims.width), height: Int(dims.height), sampleRate: asbd.mSampleRate,
            channels: Int(asbd.mChannelsPerFrame), laceMode: 3,
            secondAudio: audio, secondAudioOffset: 0.75, textTracks: [assText, utf8Text]).write(to: multipleInput)
        let multipleResult = try await MatroskaRemuxer.remux(input: multipleInput, output: multipleOutput)
        let multipleAsset = AVURLAsset(url: multipleOutput)
        let multipleAudio = try await multipleAsset.loadTracks(withMediaType: .audio)
        guard multipleAudio.count == 2,
              let audioGroup = try await multipleAsset.loadMediaSelectionGroup(for: .audible),
              audioGroup.options.count == 2,
              let english = audioGroup.options.first(where: { $0.extendedLanguageTag?.hasPrefix("en") == true }),
              let japanese = audioGroup.options.first(where: { $0.extendedLanguageTag?.hasPrefix("ja") == true }),
              audioGroup.defaultOption == english else { throw fail("Alternate audio group, languages or English default were not preserved") }
        let selectionItem = AVPlayerItem(asset: multipleAsset)
        selectionItem.select(japanese, in: audioGroup)
        guard selectionItem.selectedMediaOption(in: audioGroup) == japanese else { throw fail("Japanese alternate audio is not selectable") }
        for track in multipleAudio {
            let segments = try await track.load(.segments)
            guard let first = segments.first(where: { !$0.isEmpty }) else { throw fail("Alternate audio has no media edit") }
            let language = try await track.load(.extendedLanguageTag) ?? ""
            let expectedStart = language.hasPrefix("ja") ? 0.5 : 0.25
            guard abs(first.timeMapping.target.start.seconds - expectedStart) < 0.003 else { throw fail("Alternate audio start changed") }
            guard try await decodedAudioCount(multipleAsset, track: track, sampleRate: asbd.mSampleRate, channels: Int(asbd.mChannelsPerFrame)) > 0 else { throw fail("Alternate audio did not decode") }
        }
        guard multipleResult.subtitles.count == 2,
              let decodedASS = multipleResult.subtitles.first(where: { $0.name.contains("ASS Demo") }),
              let decodedUTF8 = multipleResult.subtitles.first(where: { $0.name.contains("UTF8 Demo") }),
              decodedASS.isDefault, !decodedASS.isForced, decodedUTF8.isForced, !decodedUTF8.isDefault,
              decodedASS.cues.count == 1, decodedUTF8.cues.count == 1,
              decodedASS.cues[0].text == "Example\nLine, with commas & more",
              decodedUTF8.cues[0].text == "Original caption",
              abs(decodedASS.cues[0].start - 0.5) < 0.001,
              abs(decodedASS.cues[0].end - 1.75) < 0.001,
              abs(decodedUTF8.cues[0].start - 0.65) < 0.001,
              decodedASS.renderingNote?.contains("advanced styling") == true else { throw fail("Text subtitle content, timing, flags or rendering limitation changed") }
        print("PASS: selectable English-default/Japanese-alternate audio, both tracks native-decoded with distinct offsets; ASS/UTF8 cue text/timing/default/forced metadata preserved")

        let disabledInput = temp.appendingPathComponent("disabled-tracks.mkv")
        let disabledOutput = temp.appendingPathComponent("disabled-tracks.mov")
        try makeMatroska(video: video, audio: audio, avcC: avcC, asc: asc,
            width: Int(dims.width), height: Int(dims.height), sampleRate: asbd.mSampleRate,
            channels: Int(asbd.mChannelsPerFrame), laceMode: 1,
            secondAudio: audio, textTracks: [assText, utf8Text],
            enabledMetadata: [3: false, 4: false, 5: false], includeDisabledUnknownTrack: true).write(to: disabledInput)
        let disabledResult = try await MatroskaRemuxer.remux(input: disabledInput, output: disabledOutput)
        let disabledAsset = AVURLAsset(url: disabledOutput)
        let disabledAudio = try await disabledAsset.loadTracks(withMediaType: .audio)
        let disabledVideo = try await disabledAsset.loadTracks(withMediaType: .video)
        let (disabledVideoStart, disabledAudioStart, disabledVideoCount, disabledAudioCount) = try await decodedOutput(disabledOutput)
        guard disabledAudio.count == 1, disabledVideo.count == 1, disabledResult.subtitles.isEmpty,
              disabledVideoCount == video.count, disabledAudioCount > 0,
              abs(disabledVideoStart) < 0.003,
              abs(disabledAudioStart - disabledVideoStart - 0.25) < 0.003 else { throw fail("Disabled alternate, text or unsupported track affected playable output") }
        if let group = try await disabledAsset.loadMediaSelectionGroup(for: .audible) {
            guard group.options.count == 1 else { throw fail("Disabled alternate audio remained selectable") }
        }
        print("PASS: late FlagEnabled0 ignores unknown codec/unsupported metadata/invalid packets, removes disabled alternate audio/text, preserves enabled native A/V timing")

        let noAudioInput = temp.appendingPathComponent("all-audio-disabled.mkv")
        let noAudioOutput = temp.appendingPathComponent("all-audio-disabled.mov")
        try makeMatroska(video: video, audio: audio, avcC: avcC, asc: asc,
            width: Int(dims.width), height: Int(dims.height), sampleRate: asbd.mSampleRate,
            channels: Int(asbd.mChannelsPerFrame), laceMode: 1, audioOffset: -0.5,
            secondAudio: audio, secondAudioOffset: -0.25,
            enabledMetadata: [2: false, 3: false], includeDisabledUnknownTrack: true).write(to: noAudioInput)
        let noAudioResult = try await MatroskaRemuxer.remux(input: noAudioInput, output: noAudioOutput)
        let noAudioAsset = AVURLAsset(url: noAudioOutput)
        guard try await noAudioAsset.loadTracks(withMediaType: .audio).isEmpty,
              let noAudioVideo = try await noAudioAsset.loadTracks(withMediaType: .video).first,
              !noAudioResult.codecSummary.contains("AAC"), abs(noAudioResult.duration - 12) < 0.06,
              try decodedVideoCount(noAudioAsset, track: noAudioVideo) == video.count else { throw fail("All-disabled audio was re-enabled or changed the enabled video timeline") }
        if let group = try await noAudioAsset.loadMediaSelectionGroup(for: .audible) {
            guard group.options.isEmpty else { throw fail("All-disabled audio remained selectable") }
        }
        print("PASS: all audio disabled produces native-decodable video-only output; discarded audio timestamps do not shift the enabled timeline")

        let invalidLanguageInput = temp.appendingPathComponent("invalid-language.mkv")
        let invalidLanguageOutput = temp.appendingPathComponent("invalid-language.mov")
        try makeMatroska(video: video, audio: audio, avcC: avcC, asc: asc,
            width: Int(dims.width), height: Int(dims.height), sampleRate: asbd.mSampleRate,
            channels: Int(asbd.mChannelsPerFrame), laceMode: 0,
            audioLanguage: "not an ISO code!", audioLanguageIETF: "../invalid language tag?!").write(to: invalidLanguageInput)
        _ = try await MatroskaRemuxer.remux(input: invalidLanguageInput, output: invalidLanguageOutput)
        let (_, _, invalidLanguageVideo, invalidLanguageAudio) = try await decodedOutput(invalidLanguageOutput)
        guard invalidLanguageVideo == video.count, invalidLanguageAudio > 0 else { throw fail("Invalid language metadata affected playable media") }
        print("PASS: invalid container language metadata cannot trigger Objective-C language-setter exceptions")

        let malformedText = FixtureTextTrack(number: 4, codec: "S_TEXT/ASS", name: "Invalid original text", language: "en", packets: [
            FixtureTextPacket(start: 0.75, duration: 1, bytes: Data([255]))
        ])
        let badTextInput = temp.appendingPathComponent("invalid-subtitle.mkv")
        let badTextOutput = temp.appendingPathComponent("invalid-subtitle.mov")
        try makeMatroska(video: video, audio: audio, avcC: avcC, asc: asc,
            width: Int(dims.width), height: Int(dims.height), sampleRate: asbd.mSampleRate,
            channels: Int(asbd.mChannelsPerFrame), laceMode: 0, textTracks: [malformedText]).write(to: badTextInput)
        do {
            _ = try await MatroskaRemuxer.remux(input: badTextInput, output: badTextOutput)
            throw fail("Malformed subtitle unexpectedly succeeded")
        } catch is MatroskaTextError { }
        guard !FileManager.default.fileExists(atPath: badTextOutput.path) else { throw fail("Subtitle failure after writer completion left output behind") }
        print("PASS: subtitle decoding failure after writer completion removes the owned output")

        let mp3Frame = Data([0xff, 0xfb, 0x94, 0]) + Data(repeating: 0, count: 380)
        let mp3Packets = (0..<80).map { FixtureSample(bytes: mp3Frame, pts: Double($0) * 1152 / 48000, duration: 1152 / 48000, keyframe: true) }
        let pcmPackets = (0..<100).map { FixtureSample(bytes: Data(repeating: 0, count: 480 * 2 * 2), pts: Double($0) / 100, duration: 0.01, keyframe: true) }
        for (name, codec, packets, bitDepth, expectedSamples) in [
            ("MP3", "A_MPEG/L3", mp3Packets, nil as Int?, 80 * 1152 + 12000),
            ("PCM16LE", "A_PCM/INT/LIT", pcmPackets, 16 as Int?, 48000 + 12000)
        ] {
            let input = temp.appendingPathComponent("generic-audio-\(name).mkv")
            let output = temp.appendingPathComponent("generic-audio-\(name).mov")
            try makeMatroska(video: video, audio: packets, avcC: avcC, asc: Data(),
                width: Int(dims.width), height: Int(dims.height), sampleRate: 48000,
                channels: 2, laceMode: 3, audioCodec: codec, audioBitDepth: bitDepth).write(to: input)
            let result = try await MatroskaRemuxer.remux(input: input, output: output)
            let (start, audioStart, count, samples) = try await decodedOutput(output, audioSampleRate: 48000, audioChannels: 2)
            guard result.codecSummary.contains(name == "MP3" ? "MP3" : "PCM"), count == video.count,
                  abs(audioStart - start - 0.25) < 0.003, abs(samples - expectedSamples) <= 2048 else { throw fail("\(name) Matroska packet splitting, lacing, decoded count or A/V synchronization failed: \(samples) vs \(expectedSamples)") }
            print("PASS: \(name) original packets → laced Matroska remux → native48k stereoPCM, \(samples) samples, A/V offset retained")
        }
        let negative = temp.appendingPathComponent("negative.mkv")
        try makeMatroska(video: video, audio: audio, avcC: avcC, asc: asc, width: Int(dims.width), height: Int(dims.height),
                         sampleRate: asbd.mSampleRate, channels: Int(asbd.mChannelsPerFrame), laceMode: 0, videoOffset: -0.1, audioOffset: 0.15).write(to: negative)
        let negativeOut = temp.appendingPathComponent("negative.mp4")
        _ = try await MatroskaRemuxer.remux(input: negative, output: negativeOut)
        let (v, a, _, _) = try await decodedOutput(negativeOut)
        precondition(abs(v) < 0.003 && abs(a - v - 0.25) < 0.003, "Negative PTS normalization changed synchronization")
        print("PASS: negative absolute timestamps shift both tracks together")
        let unsupported = temp.appendingPathComponent("unsupported.mkv")
        try makeMatroska(video: video, audio: audio, avcC: avcC, asc: asc, width: Int(dims.width), height: Int(dims.height),
                         sampleRate: asbd.mSampleRate, channels: Int(asbd.mChannelsPerFrame), laceMode: 0, audioCodec: "A_NOT_SUPPORTED").write(to: unsupported)
        let rejectedOut = temp.appendingPathComponent("rejected.mov")
        do { _ = try await MatroskaRemuxer.remux(input: unsupported, output: rejectedOut); throw fail("Unsupported audio was silently discarded") }
        catch let error as MatroskaRemuxError { precondition(error.localizedDescription.contains("A_NOT_SUPPORTED")) }
        precondition(!FileManager.default.fileExists(atPath: rejectedOut.path))
        let truncated = temp.appendingPathComponent("truncated.mkv")
        try Data([0x1a, 0x45, 0xdf]).write(to: truncated)
        do { _ = try await MatroskaRemuxer.remux(input: truncated, output: rejectedOut); throw fail("Truncated input was accepted") }
        catch is MatroskaRemuxError {}
        let cancelled = Task { try await MatroskaRemuxer.remux(input: negative, output: rejectedOut) }
        cancelled.cancel()
        do { _ = try await cancelled.value; throw fail("Cancellation was ignored") }
        catch is CancellationError {}
        precondition(!FileManager.default.fileExists(atPath: rejectedOut.path))
        print("PASS: unsupported-track rejection, truncated-input rejection, existing-output preservation, cancellation cleanup")
        if CommandLine.arguments.count > 2 {
            let retained = URL(fileURLWithPath: CommandLine.arguments[2])
            try makeMatroska(video: video, audio: audio, avcC: avcC, asc: asc, width: Int(dims.width), height: Int(dims.height),
                             sampleRate: asbd.mSampleRate, channels: Int(asbd.mChannelsPerFrame), laceMode: 1).write(to: retained)
            print("Saved original test Matroska fixture: \(retained.path)")
        }
    }

    private static func compressedSamples(asset: AVAsset, track: AVAssetTrack) throws -> ([FixtureSample], CMFormatDescription) {
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: nil)
        output.alwaysCopiesSampleData = false
        reader.add(output)
        guard reader.startReading() else { throw reader.error ?? fail("Cannot read compressed fixture") }
        var samples: [FixtureSample] = [], format: CMFormatDescription?
        while let sample = output.copyNextSampleBuffer() {
            if CMSampleBufferGetNumSamples(sample) == 0 { continue }
            guard let block = CMSampleBufferGetDataBuffer(sample), let description = CMSampleBufferGetFormatDescription(sample) else { throw fail("Missing compressed fixture data: samples=\(CMSampleBufferGetNumSamples(sample)), bytes=\(CMSampleBufferGetTotalSampleSize(sample)), format=\(String(describing: CMSampleBufferGetFormatDescription(sample)))") }
            let size = CMBlockBufferGetDataLength(block)
            var data = Data(count: size)
            let status = data.withUnsafeMutableBytes { CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: size, destination: $0.baseAddress!) }
            guard status == noErr else { throw fail("Cannot copy compressed fixture") }
            let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[String: Any]]
            let count = CMSampleBufferGetNumSamples(sample)
            var offset = 0
            for index in 0..<count {
                let packetSize = CMSampleBufferGetSampleSize(sample, at: index)
                guard packetSize > 0, packetSize <= data.count - offset else { throw fail("Invalid compressed fixture packet sizing") }
                var timing = CMSampleTimingInfo()
                guard CMSampleBufferGetSampleTimingInfo(sample, at: index, timingInfoOut: &timing) == noErr else { throw fail("Missing fixture packet timing") }
                let nonSync = attachments?[min(index, (attachments?.count ?? 1) - 1)][kCMSampleAttachmentKey_NotSync as String] as? Bool ?? false
                samples.append(FixtureSample(bytes: Data(data[offset..<offset + packetSize]), pts: timing.presentationTimeStamp.seconds,
                                             duration: timing.duration.seconds, keyframe: !nonSync,
                                             decodeTime: timing.decodeTimeStamp.isNumeric ? timing.decodeTimeStamp.seconds : nil))
                offset += packetSize
            }
            format = description
        }
        guard reader.status == .completed, let format, let first = samples.first else { throw reader.error ?? fail("Empty compressed fixture") }
        for index in samples.indices {
            samples[index].pts -= first.pts
            if let time = samples[index].decodeTime { samples[index].decodeTime = time - first.pts }
        }
        return (samples, format)
    }

    static func decodedOutput(_ url: URL, audioSampleRate: Double? = nil, audioChannels: Int? = nil) async throws -> (Double, Double, Int, Int) {
        let asset = AVURLAsset(url: url)
        let videoTrack = try await asset.loadTracks(withMediaType: .video).first!
        guard let audioTrack = try await asset.loadTracks(withMediaType: .audio).first else { throw fail("Output lost its audio track") }
        // AVAssetReader can vend padding/priming samples at media-local PTS.
        // The track edit mapping is the actual shared playback timeline.
        let audioSegments = try await audioTrack.load(.segments)
        guard let audioSegment = audioSegments.first(where: { !$0.isEmpty }) else { throw fail("No nonempty audio edit") }
        let audioTimelineStart = audioSegment.timeMapping.target.start.seconds
        let reader = try AVAssetReader(asset: asset)
        let video = AVAssetReaderTrackOutput(track: videoTrack, outputSettings: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA])
        var settings: [String: Any] = [AVFormatIDKey: kAudioFormatLinearPCM,
            AVLinearPCMBitDepthKey: 16, AVLinearPCMIsFloatKey: false, AVLinearPCMIsNonInterleaved: false]
        if let audioSampleRate { settings[AVSampleRateKey] = audioSampleRate }
        if let audioChannels { settings[AVNumberOfChannelsKey] = audioChannels }
        let audio = AVAssetReaderTrackOutput(track: audioTrack, outputSettings: settings)
        reader.add(video); reader.add(audio)
        guard reader.startReading() else { throw reader.error ?? fail("Native decode cannot start") }
        var videoPTS: Double?, audioPTS: Double?, videoCount = 0, audioCount = 0
        var videoDone = false, audioDone = false
        while !videoDone || !audioDone {
            if !videoDone {
                if let sample = video.copyNextSampleBuffer() {
                    if videoPTS == nil { videoPTS = CMSampleBufferGetPresentationTimeStamp(sample).seconds }
                    guard CMSampleBufferGetImageBuffer(sample) != nil else { throw fail("Native video decoder returned no pixels") }
                    videoCount += 1
                } else { videoDone = true }
            }
            if !audioDone {
                if let sample = audio.copyNextSampleBuffer() {
                    if let audioSampleRate, let audioChannels {
                        let description = CMSampleBufferGetFormatDescription(sample)!
                        let pcm = CMAudioFormatDescriptionGetStreamBasicDescription(description)!.pointee
                        guard pcm.mSampleRate == audioSampleRate, pcm.mChannelsPerFrame == UInt32(audioChannels) else { throw fail("Wrong rendered HE PCM format") }
                    }
                    if audioPTS == nil { audioPTS = CMSampleBufferGetPresentationTimeStamp(sample).seconds }
                    audioCount += CMSampleBufferGetNumSamples(sample)
                } else { audioDone = true }
            }
        }
        guard reader.status == .completed, let videoPTS, let audioPTS else { throw reader.error ?? fail("Native decode failed") }
        return (videoPTS, audioTimelineStart, videoCount, audioCount)
    }

    private static func decodedVideoCount(_ asset: AVAsset, track: AVAssetTrack) throws -> Int {
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA])
        reader.add(output)
        guard reader.startReading() else { throw reader.error ?? fail("Cannot decode video-only output") }
        var frames = 0
        while let sample = output.copyNextSampleBuffer() {
            guard CMSampleBufferGetImageBuffer(sample) != nil else { throw fail("Video-only decoder returned no pixels") }
            if frames == 0, abs(CMSampleBufferGetPresentationTimeStamp(sample).seconds) >= 0.003 { throw fail("Disabled audio shifted the enabled video timeline") }
            frames += 1
        }
        guard reader.status == .completed else { throw reader.error ?? fail("Video-only decode failed") }
        return frames
    }

    private static func decodedAudioCount(_ asset: AVAsset, track: AVAssetTrack, sampleRate: Double, channels: Int) async throws -> Int {
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: sampleRate, AVNumberOfChannelsKey: channels, AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false, AVLinearPCMIsNonInterleaved: false])
        reader.add(output)
        guard reader.startReading() else { throw reader.error ?? fail("Cannot decode alternate audio") }
        var frames = 0
        while let sample = output.copyNextSampleBuffer() { frames += CMSampleBufferGetNumSamples(sample) }
        guard reader.status == .completed else { throw reader.error ?? fail("Alternate audio decode failed") }
        return frames
    }

    private static func makeMatroska(video: [FixtureSample], audio originalAudio: [FixtureSample], avcC: Data, asc: Data,
                             width: Int, height: Int, sampleRate: Double, channels: Int, laceMode: Int,
                             videoOffset: Double = 0.25, audioOffset: Double = 0.5, audioCodec: String = "A_AAC",
                             outputSampleRate: Double? = nil,
                             videoCodec: String = "V_MPEG4/ISO/AVC", videoFrameDuration: Double = 1 / 30,
                             secondAudio: [FixtureSample]? = nil, secondAudioOffset: Double = 0.75,
                             audioLanguage: String = "eng", audioLanguageIETF: String? = "en",
                             audioBitDepth: Int? = nil, textTracks: [FixtureTextTrack] = [],
                             enabledMetadata: [UInt8: Bool] = [:], includeDisabledUnknownTrack: Bool = false) -> Data {
        func unsigned(_ value: UInt64) -> Data {
            var result = Data(), started = false
            for shift in stride(from: 56, through: 0, by: -8) {
                let byte = UInt8(truncatingIfNeeded: value >> shift)
                if byte != 0 || started || shift == 0 { result.append(byte); started = true }
            }
            return result
        }
        func vint(_ value: UInt64) -> Data {
            for width in 1...8 where value < (UInt64(1) << (7 * width)) - 1 {
                let encoded = value | (UInt64(1) << (7 * width))
                return Data((0..<width).reversed().map { UInt8(truncatingIfNeeded: encoded >> ($0 * 8)) })
            }
            fatalError("Fixture size overflow")
        }
        func element(_ id: UInt64, _ payload: Data) -> Data { unsigned(id) + vint(UInt64(payload.count)) + payload }
        func u(_ id: UInt64, _ value: UInt64) -> Data { element(id, unsigned(value)) }
        func text(_ id: UInt64, _ value: String) -> Data { element(id, Data(value.utf8)) }
        func float(_ id: UInt64, _ value: Double) -> Data {
            let raw = value.bitPattern
            return element(id, Data((0..<8).reversed().map { UInt8(truncatingIfNeeded: raw >> ($0 * 8)) }))
        }
        let header = element(0x1A45DFA3, u(0x4286, 1) + u(0x42F7, 1) + u(0x42F2, 4) + u(0x42F3, 8) + text(0x4282, "matroska") + u(0x4287, 4) + u(0x4285, 2))
        let info = element(0x1549A966, u(0x2AD7B1, 1_000_000) + text(0x4D80, "Luma original fixture") + text(0x5741, "Luma original fixture"))
        // Put FlagEnabled last: element order must not cause premature codec or
        // feature rejection for a track the container explicitly disables.
        func enabledFlag(_ number: UInt8) -> Data { enabledMetadata[number].map { u(0xB9, $0 ? 1 : 0) } ?? Data() }
        let videoEntry = element(0xAE, u(0xD7, 1) + u(0x73C5, 1) + u(0x83, 1) + text(0x86, videoCodec) + element(0x63A2, avcC) + u(0x23E383, UInt64((videoFrameDuration * 1_000_000_000).rounded())) + element(0xE0, u(0xB0, UInt64(width)) + u(0xBA, UInt64(height))) + enabledFlag(1))
        let outputFrequency = outputSampleRate.map { float(0x78B5, $0) } ?? Data()
        let bitDepth = audioBitDepth.map { u(0x6264, UInt64($0)) } ?? Data()
        func audioEntry(number: UInt64, language: String, languageIETF: String?, name: String, isDefault: Bool) -> Data {
            let ietf = languageIETF.map { text(0x22B59D, $0) } ?? Data()
            var payload = u(0xD7, number) + u(0x73C5, number) + u(0x83, 2)
            payload += u(0x88, isDefault ? 1 : 0)
            payload += text(0x22B59C, language) + ietf + text(0x536E, name)
            payload += text(0x86, audioCodec) + element(0x63A2, asc)
            let settings = outputFrequency + float(0xB5, sampleRate) + u(0x9F, UInt64(channels)) + bitDepth
            payload += element(0xE1, settings) + enabledFlag(UInt8(number))
            return element(0xAE, payload)
        }
        var trackEntries = videoEntry + audioEntry(number: 2, language: audioLanguage, languageIETF: audioLanguageIETF, name: "English", isDefault: true)
        if secondAudio != nil { trackEntries += audioEntry(number: 3, language: "jpn", languageIETF: "ja", name: "Japanese", isDefault: false) }
        for track in textTracks {
            trackEntries += element(0xAE, u(0xD7, UInt64(track.number)) + u(0x73C5, UInt64(track.number)) + u(0x83, 17)
                + u(0x88, track.isDefault ? 1 : 0) + u(0x55AA, track.isForced ? 1 : 0)
                + text(0x86, track.codec) + text(0x536E, track.name) + text(0x22B59D, track.language) + element(0x63A2, track.codecPrivate) + enabledFlag(track.number))
        }
        if includeDisabledUnknownTrack {
            trackEntries += element(0xAE, u(0xD7, 6) + u(0x73C5, 6) + u(0x83, 2)
                + text(0x86, "A_NOT_SUPPORTED") + element(0x63A2, Data([255]))
                + element(0x6D80, Data()) + u(0x537F, 1) + u(0x56AA, 1_000_000) + u(0xB9, 0))
        }
        var blocks: [(Double, Data)] = []
        let clusterTimestamp = 1000
        func blockHeader(track: UInt8, pts: Double, flags: UInt8) -> Data {
            let relative = Int16((pts * 1000).rounded() - Double(clusterTimestamp))
            let raw = UInt16(bitPattern: relative)
            return Data([0x80 | track, UInt8(raw >> 8), UInt8(raw & 255), flags])
        }
        for (index, sample) in video.enumerated() {
            let pts = sample.pts + videoOffset
            let payload = blockHeader(track: 1, pts: pts, flags: sample.keyframe ? 0x80 : 0) + sample.bytes
            // A BlockGroup keyframe has no ReferenceBlock; most frames use SimpleBlock.
            let encoded = index == 0 ? element(0xA0, element(0xA1, payload) + u(0x9B, UInt64((videoFrameDuration * 1000).rounded()))) : element(0xA3, payload)
            // Interleave by DTS so the original video coding order is retained.
            blocks.append(((sample.decodeTime ?? sample.pts) + videoOffset, encoded))
        }
        func appendAudio(_ original: [FixtureSample], number: UInt8, offset: Double) {
            var audio = original
            if laceMode == 2 {
                // Equal-sized valid AAC packets exercise fixed lacing independently.
                audio = original.indices.map { i in FixtureSample(bytes: original[0].bytes,
                    pts: Double(i) * 1024 / sampleRate, duration: 1024 / sampleRate, keyframe: true) }
            }
            var index = 0
            while index < audio.count {
            let count = laceMode == 0 ? 1 : min(3, audio.count - index)
            let effectiveMode = count == 1 ? 0 : laceMode
            let group = Array(audio[index..<index + count])
            let pts = group[0].pts + offset
            var payload = blockHeader(track: number, pts: pts, flags: 0x80 | UInt8(effectiveMode << 1))
            if effectiveMode != 0 {
                payload.append(UInt8(count - 1))
                if effectiveMode == 1 {
                    for sample in group.dropLast() {
                        var size = sample.bytes.count
                        while size >= 255 { payload.append(255); size -= 255 }
                        payload.append(UInt8(size))
                    }
                } else if effectiveMode == 3 {
                    payload += vint(UInt64(group[0].bytes.count))
                    for i in 1..<(count - 1) {
                        let delta = group[i].bytes.count - group[i - 1].bytes.count
                        // Pick a signed EBML width able to represent the size delta.
                        for width in 1...8 {
                            let bias = (Int64(1) << (7 * width - 1)) - 1
                            if Int64(delta) >= -bias && Int64(delta) <= bias {
                                let encoded = UInt64(Int64(delta) + bias) | (UInt64(1) << (7 * width))
                                payload += Data((0..<width).reversed().map { UInt8(truncatingIfNeeded: encoded >> ($0 * 8)) })
                                break
                            }
                        }
                    }
                }
            }
            for sample in group { payload += sample.bytes }
            blocks.append((pts, element(0xA3, payload)))
            index += count
            }
        }
        appendAudio(originalAudio, number: 2, offset: audioOffset)
        if let secondAudio { appendAudio(secondAudio, number: 3, offset: secondAudioOffset) }
        if includeDisabledUnknownTrack {
            // This would fail the visible-frame guard or audio parser if a
            // disabled track's packets reached normal media processing.
            let payload = blockHeader(track: 6, pts: -0.75, flags: 0x08) + Data([255, 0])
            blocks.append((-0.75, element(0xA3, payload)))
        }
        for track in textTracks {
            for packet in track.packets {
                let payload = blockHeader(track: track.number, pts: packet.start, flags: 0) + packet.bytes
                let group = element(0xA1, payload) + u(0x9B, UInt64((packet.duration * 1000).rounded()))
                blocks.append((packet.start, element(0xA0, group)))
            }
        }
        blocks.sort { $0.0 < $1.0 }
        let cluster = element(0x1F43B675, u(0xE7, UInt64(clusterTimestamp)) + blocks.reduce(Data()) { $0 + $1.1 })
        // An unknown-sized Segment is common in real streaming-produced MKV files.
        return header + unsigned(0x18538067) + Data([0x01, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff]) + info + element(0x1654AE6B, trackEntries) + cluster
    }
}
