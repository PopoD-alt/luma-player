import Foundation
import AudioToolbox
import AVFoundation
import CoreMedia

private struct SmokeFailure: Error, CustomStringConvertible { let description: String }
private func require(_ condition: @autoclosure () throws -> Bool, _ detail: String) throws {
    if try condition() == false { throw SmokeFailure(description: detail) }
}
private func rejects(_ operation: () throws -> Void) throws {
    do { try operation(); throw SmokeFailure(description: "Invalid audio input was accepted") }
    catch is MatroskaAudioFormatError { }
}
private func appendLE<T: FixedWidthInteger>(_ value: T, to data: inout Data) {
    var little = value.littleEndian
    withUnsafeBytes(of: &little) { data.append(contentsOf: $0) }
}
private func packed(_ fields: [(Int, Int)], paddedTo size: Int) -> Data {
    var bits = [UInt8]()
    for (value, width) in fields {
        for bit in stride(from: width - 1, through: 0, by: -1) { bits.append(UInt8((value >> bit) & 1)) }
    }
    var bytes = [UInt8](repeating: 0, count: size)
    for (index, bit) in bits.enumerated() { bytes[index / 8] |= bit << (7 - index % 8) }
    return Data(bytes)
}
private func ac3(rate: Int = 0, sizeCode: Int = 16, bsid: Int = 8, bytes: Int = 512) -> Data {
    // Header-only payload; not presented as a complete valid AC-3 bitstream.
    packed([(0x0b77,16),(0,16),(rate,2),(sizeCode,6),(bsid,5),(0,3),(2,3),(0,2),(0,1)], paddedTo: bytes)
}
private func eac3(blockCode: Int, streamType: Int = 0, substream: Int = 0) -> Data {
    // Header-only payload; not presented as a complete valid E-AC-3 bitstream.
    packed([(0x0b77,16),(streamType,2),(substream,3),(63,11),(0,2),(blockCode,2),(2,3),(0,1),(16,5)], paddedTo: 128)
}

@main struct MatroskaAudioFormatSmoke {
    static func main() async throws {
        let mp3 = try MatroskaAudioFormat.make(codecID: "A_MPEG/L3", sampleRate: 48000, channels: 2)
        // An original silent MPEG-1 Layer III frame. Both granules have zero
        // part2_3_length/big_values and an empty reservoir; no decoder code is
        // used to generate it. Native playback below validates the bitstream.
        let silentMP3 = Data([0xff,0xfb,0x94,0]) + Data(repeating: 0, count: 380)
        let packet = try mp3.packetInfo(silentMP3)
        try require(packet.sampleFrames == 1152 && packet.encodedByteCount == 384, "MPEG-1 packet dimensions")
        try require(abs(packet.duration - 0.024) < 1e-12, "MPEG-1 duration")
        let concatenated = try mp3.packetInfo(silentMP3 + silentMP3)
        try require(concatenated.encodedByteCount == 384, "Compressed packet extent must identify the first frame")
        for (header, rate) in [([UInt8(0xff),0xf3,0x84,0xc0],24000),([UInt8(0xff),0xe3,0x44,0xc0],12000)] {
            let format = try MatroskaAudioFormat.make(codecID: "A_MPEG/L3", sampleRate: Double(rate), channels: 1)
            let frame = Data(header) + Data(repeating: 0, count: 380)
            let info = try format.packetInfo(frame)
            try require(info.sampleFrames == 576, "Lower-rate MP3 uses 576 frames")
        }
        try rejects { _ = try mp3.packetInfo(silentMP3.prefix(383)) }
        try rejects { _ = try mp3.packetInfo(Data([0xff,0xfb,0x04,0]) + Data(repeating:0,count:380)) }
        try rejects { _ = try mp3.packetInfo(Data([0xff,0xfb,0x94,2]) + Data(repeating:0,count:380)) }
        let wrongChannels = try MatroskaAudioFormat.make(codecID:"A_MPEG/L3",sampleRate:48000,channels:1)
        try rejects { _ = try wrongChannels.packetInfo(silentMP3) }

        let dolby = try MatroskaAudioFormat.make(codecID:"A_AC3",sampleRate:48000,channels:2)
        let dolbyInfo = try dolby.packetInfo(ac3())
        try require(dolbyInfo.sampleFrames == 1536 && dolbyInfo.encodedByteCount == 512 && abs(dolbyInfo.duration - 0.032) < 1e-12, "AC-3 packet timing")
        let cdRate = try MatroskaAudioFormat.make(codecID:"A_AC3",sampleRate:44100,channels:2)
        try require(try cdRate.packetInfo(ac3(rate:1,sizeCode:0,bytes:138)).encodedByteCount == 138, "AC-3 44.1kHz even frame size")
        try require(try cdRate.packetInfo(ac3(rate:1,sizeCode:1,bytes:140)).encodedByteCount == 140, "AC-3 44.1kHz odd frame size")
        try rejects { _ = try dolby.packetInfo(ac3(sizeCode:38)) }
        for count in 0..<7 { try rejects { _ = try dolby.packetInfo(ac3().prefix(count)) } }
        let enhanced = try MatroskaAudioFormat.make(codecID:"A_EAC3",sampleRate:48000,channels:2)
        try require(enhanced.asbd.mFramesPerPacket == 0, "E-AC-3 blocks can vary per packet")
        for (code, frames) in [256,512,768,1536].enumerated() {
            let info = try enhanced.packetInfo(eac3(blockCode:code))
            try require(info.sampleFrames == frames && info.encodedByteCount == 128, "E-AC-3 block count")
        }
        try rejects { _ = try enhanced.packetInfo(eac3(blockCode:3,streamType:1)) }
        try rejects { _ = try enhanced.packetInfo(eac3(blockCode:3,substream:1)) }
        try rejects { _ = try enhanced.packetInfo(eac3(blockCode:3,streamType:3)) }

        let pcm = try MatroskaAudioFormat.make(codecID:"A_PCM/INT/LIT",sampleRate:48000,channels:2,bitDepth:16)
        let unsigned = try MatroskaAudioFormat.make(codecID:"A_PCM/INT/LIT",sampleRate:48000,channels:1,bitDepth:8)
        try require(unsigned.asbd.mFormatFlags & kAudioFormatFlagIsSignedInteger == 0, "Matroska 8-bit PCM is unsigned")
        let float = try MatroskaAudioFormat.make(codecID:"A_PCM/FLOAT/IEEE",sampleRate:48000,channels:6,bitDepth:32)
        try require(float.asbd.mChannelsPerFrame == 6 && float.asbd.mBytesPerFrame == 24, "Multichannel float PCM must not be narrowed to stereo")
        try rejects { _ = try pcm.packetInfo(Data([0,1,2])) }
        try rejects { _ = try MatroskaAudioFormat.make(codecID:"A_PCM/INT/LIT",sampleRate:48000,channels:2) }
        try rejects { _ = try MatroskaAudioFormat.make(codecID:"A_OPUS",sampleRate:48000,channels:2) }
        try rejects { _ = try MatroskaAudioFormat.make(codecID:"A_MPEG/L3",codecPrivate:Data([1]),sampleRate:48000,channels:2) }
        print("PASS: MPEG-1/2/2.5 headers, AC-3 sizes, E-AC-3 variable blocks, truncation/metadata checks, PCM widths/channel handling")

        let root = FileManager.default.temporaryDirectory.appendingPathComponent("Luma-Audio-Format-\(UUID().uuidString)",isDirectory:true)
        try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
        defer { try? FileManager.default.removeItem(at:root) }
        let mp3Output = root.appendingPathComponent("original-silence.mov")
        try await writeMovie(format:mp3,packets:Array(repeating:silentMP3,count:80),to:mp3Output)
        let mp3Frames = try await decodedFrames(mp3Output,channels:2)
        try require(mp3Frames > 90_000 && mp3Frames <= 92_160,"Native MP3 decode frame count \(mp3Frames)")
        var tone = Data()
        for sample in 0..<48_000 {
            let left = Int16(sin(Double(sample) * 2 * .pi * 440 / 48_000) * 4096)
            let right = Int16(sin(Double(sample) * 2 * .pi * 660 / 48_000) * 4096)
            appendLE(left,to:&tone); appendLE(right,to:&tone)
        }
        let pcmOutput = root.appendingPathComponent("original-pcm.mov")
        try await writeMovie(format:pcm,packets:[tone],to:pcmOutput)
        let pcmFrames = try await decodedFrames(pcmOutput,channels:2)
        try require(pcmFrames == 48_000,"Native PCM decode count \(pcmFrames)")
        print("PASS: original silent MP3 -> MOV passthrough -> native PCM \(mp3Frames) frames; original stereo PCM -> MOV -> native PCM \(pcmFrames) frames")
        let flacURL = root.appendingPathComponent("original-tone.flac")
        let encoder = Process()
        encoder.executableURL = URL(fileURLWithPath:"/usr/bin/afconvert")
        encoder.arguments = ["-f","flac","-d","flac","Tests/Fixtures/Luma-Tone.wav",flacURL.path]
        try encoder.run(); encoder.waitUntilExit()
        try require(encoder.terminationStatus == 0,"Apple native FLAC fixture encoding failed")
        let (flacPrivate,flacPackets) = try readFLAC(flacURL)
        let flac = try MatroskaAudioFormat.make(codecID:"A_FLAC",codecPrivate:flacPrivate,sampleRate:48000,channels:1,bitDepth:16)
        let flacInfos = try flacPackets.map { try flac.packetInfo($0) }
        let flacSamples = flacInfos.reduce(0) { $0 + $1.sampleFrames }
        try require(flacSamples == 144_000,"FLAC elementary frame sample count \(flacSamples)")
        try require(flacInfos.last!.sampleFrames < flacInfos.first!.sampleFrames,"Fixture must exercise a short final FLAC frame")
        var corrupt = flacPackets[0]
        corrupt[corrupt.count-1] ^= 1
        try rejects { _ = try flac.packetInfo(corrupt) }
        try rejects { _ = try MatroskaAudioFormat.make(codecID:"A_FLAC",codecPrivate:flacPrivate,sampleRate:44100,channels:1) }
        let flacOutput = root.appendingPathComponent("original-flac.mov")
        try await writeMovie(format:flac,packets:flacPackets,to:flacOutput)
        let flacDecoded = try await decodedFrames(flacOutput,channels:1)
        try require(flacDecoded == flacSamples,"FLAC MOV native PCM count \(flacDecoded), expected \(flacSamples)")
        print("PASS: FLAC STREAMINFO -> native dfLa cookie, all \(flacPackets.count) original frames CRC8/CRC16 checked, short final frame preserved; MOV native PCM \(flacDecoded) frames")
        print("NOT VERIFIED: complete AC-3/E-AC-3 payload decode; those tests validate headers only, and native decoder construction was probed separately.")
    }

    private static func readFLAC(_ url:URL) throws -> (Data,[Data]) {
        let contents = try Data(contentsOf:url)
        var cursor = 4
        while true {
            try require(cursor+4 <= contents.count,"Truncated native FLAC metadata")
            let last = contents[cursor] & 0x80 != 0
            let size = Int(contents[cursor+1]) << 16 | Int(contents[cursor+2]) << 8 | Int(contents[cursor+3])
            cursor += 4+size
            try require(cursor <= contents.count,"Truncated native FLAC metadata payload")
            if last {break}
        }
        var file:AudioFileID?
        let opened = AudioFileOpenURL(url as CFURL,.readPermission,0,&file)
        guard opened == noErr,let file else {throw SmokeFailure(description:"FLAC AudioFile open \(opened)")}
        defer {AudioFileClose(file)}
        var upper:UInt32=0,packetCount:UInt64=0
        var propertySize=UInt32(MemoryLayout.size(ofValue:upper))
        try require(AudioFileGetProperty(file,kAudioFilePropertyPacketSizeUpperBound,&propertySize,&upper)==noErr,"FLAC max packet size")
        propertySize=UInt32(MemoryLayout.size(ofValue:packetCount))
        try require(AudioFileGetProperty(file,kAudioFilePropertyAudioDataPacketCount,&propertySize,&packetCount)==noErr,"FLAC packet count")
        try require(upper>0 && upper<1_000_000 && packetCount<1000,"FLAC fixture resource bounds")
        var result=[Data]()
        for index in 0..<packetCount {
            var data=Data(count:Int(upper))
            var byteCount=upper,count:UInt32=1,description=AudioStreamPacketDescription()
            let status=data.withUnsafeMutableBytes{AudioFileReadPacketData(file,false,&byteCount,&description,Int64(index),&count,$0.baseAddress!)}
            try require(status==noErr && count==1,"FLAC native frame extraction")
            result.append(Data(data.prefix(Int(byteCount))))
        }
        return (Data(contents.prefix(cursor)),result)
    }

    private static func buffer(format:MatroskaAudioFormat,data:Data,pts:Double) throws -> CMSampleBuffer {
        let info = try format.packetInfo(data)
        try require(info.encodedByteCount == data.count,"Test packet contains more than one compressed frame")
        var block:CMBlockBuffer?
        var status=CMBlockBufferCreateWithMemoryBlock(allocator:nil,memoryBlock:nil,blockLength:data.count,
            blockAllocator:nil,customBlockSource:nil,offsetToData:0,dataLength:data.count,flags:0,blockBufferOut:&block)
        guard status==noErr,let block else {throw SmokeFailure(description:"Block allocation \(status)")}
        status=data.withUnsafeBytes{CMBlockBufferReplaceDataBytes(with:$0.baseAddress!,blockBuffer:block,offsetIntoDestination:0,dataLength:data.count)}
        guard status==noErr else {throw SmokeFailure(description:"Block copy \(status)")}
        var sample:CMSampleBuffer?
        if format.isPCM {
            var timing=CMSampleTimingInfo(duration:CMTime(value:1,timescale:Int32(format.asbd.mSampleRate)),presentationTimeStamp:CMTime(seconds:pts,preferredTimescale:1_000_000_000),decodeTimeStamp:.invalid)
            var size=Int(format.asbd.mBytesPerFrame)
            status=CMSampleBufferCreateReady(allocator:nil,dataBuffer:block,formatDescription:format.formatDescription,
                sampleCount:info.sampleFrames,sampleTimingEntryCount:1,sampleTimingArray:&timing,sampleSizeEntryCount:1,sampleSizeArray:&size,sampleBufferOut:&sample)
        } else {
            var packet=AudioStreamPacketDescription(mStartOffset:0,mVariableFramesInPacket:UInt32(info.sampleFrames),mDataByteSize:UInt32(data.count))
            status=CMAudioSampleBufferCreateReadyWithPacketDescriptions(allocator:nil,dataBuffer:block,formatDescription:format.formatDescription,
                sampleCount:1,presentationTimeStamp:CMTime(seconds:pts,preferredTimescale:1_000_000_000),packetDescriptions:&packet,sampleBufferOut:&sample)
        }
        guard status==noErr,let sample else {throw SmokeFailure(description:"Audio sample creation \(status)")}
        return sample
    }

    private static func writeMovie(format:MatroskaAudioFormat,packets:[Data],to url:URL) async throws {
        let writer=try AVAssetWriter(outputURL:url,fileType:.mov)
        let input=AVAssetWriterInput(mediaType:.audio,outputSettings:nil,sourceFormatHint:format.formatDescription)
        input.expectsMediaDataInRealTime=false
        guard writer.canAdd(input) else {throw SmokeFailure(description:"MOV rejected \(format.profileName)")}
        writer.add(input)
        guard writer.startWriting() else {throw writer.error ?? SmokeFailure(description:"Writer failed")}
        writer.startSession(atSourceTime:.zero)
        var pts=0.0
        for data in packets {
            let deadline=Date().addingTimeInterval(10)
            while !input.isReadyForMoreMediaData {
                guard writer.status == .writing,Date()<deadline else {throw writer.error ?? SmokeFailure(description:"Writer stalled")}
                try await Task.sleep(nanoseconds:1_000_000)
            }
            let sample=try buffer(format:format,data:data,pts:pts)
            guard input.append(sample) else {throw writer.error ?? SmokeFailure(description:"Audio append failed")}
            pts += try format.packetInfo(data).duration
        }
        input.markAsFinished()
        await writer.finishWriting()
        guard writer.status == .completed else {throw writer.error ?? SmokeFailure(description:"Writer did not complete")}
    }

    private static func decodedFrames(_ url:URL,channels:Int) async throws -> Int {
        let asset=AVURLAsset(url:url)
        guard let track=try await asset.loadTracks(withMediaType:.audio).first else {throw SmokeFailure(description:"Missing output audio")}
        let reader=try AVAssetReader(asset:asset)
        let output=AVAssetReaderTrackOutput(track:track,outputSettings:[AVFormatIDKey:kAudioFormatLinearPCM,
            AVSampleRateKey:48000,AVNumberOfChannelsKey:channels,AVLinearPCMBitDepthKey:32,AVLinearPCMIsFloatKey:true])
        reader.add(output)
        guard reader.startReading() else {throw reader.error ?? SmokeFailure(description:"Native decoder failed")}
        var frames=0
        while let buffer=output.copyNextSampleBuffer() {frames += CMSampleBufferGetNumSamples(buffer)}
        guard reader.status == .completed else {throw reader.error ?? SmokeFailure(description:"Native decode incomplete")}
        return frames
    }
}
