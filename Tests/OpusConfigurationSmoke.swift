import Foundation
import AudioToolbox
import AVFoundation
import CoreMedia

private struct OpusSmokeFailure: Error, CustomStringConvertible { let description: String }
private func require(_ condition: @autoclosure () throws -> Bool, _ message: String) throws {
    if try condition() == false { throw OpusSmokeFailure(description: message) }
}
private func rejects(_ operation: () throws -> Void) throws {
    do { try operation(); throw OpusSmokeFailure(description:"Malformed/unsupported Opus was accepted") }
    catch is OpusConfigurationError { }
}
private func little(_ value:UInt32,_ count:Int)->Data {
    Data((0..<count).map { UInt8((value >> ($0*8)) & 255) })
}

@main struct OpusConfigurationSmoke {
    static func main() async throws {
        setbuf(stdout,nil)
        let head = Data("OpusHead".utf8) + Data([1,2]) + little(312,2) + little(48000,4) + Data([0,0,0])
        let config = try OpusConfiguration.parse(head)
        try config.validateMatroska(sampleRate:48000,channels:2,codecDelayNanoseconds:6_500_000)
        for prefix in 0..<19 { try rejects { _ = try OpusConfiguration.parse(head.prefix(prefix)) } }
        var invalid=head; invalid[18]=1
        try rejects { _ = try OpusConfiguration.parse(invalid) }
        try rejects { try config.validateMatroska(sampleRate:48000,channels:2,codecDelayNanoseconds:0) }
        let timingCases: [(Data,Int)] = [(Data([0x80,0]),120),(Data([0x60,0]),480),(Data([0xf8,0]),960),
                                (Data([0x18,0]),2880),(Data([0x19,0,0]),5760),
                                (Data([0xfa,1,0,0]),1920),(Data([0xfb,0x82,1,0,0]),1920),
                                (Data([0xfb,0x41,1,0,0]),960),(Data([0x83,48])+Data(repeating:0,count:48),5760)]
        for (packet,frames) in timingCases {
            try require(try config.packetSampleFrames(packet)==frames,"RFC packet duration/framing example")
        }
        for packet in [Data(),Data([0xf9,0]),Data([0xfb,0]),Data([0xfb,7]),Data([0xfb,0x41,255]),
                       Data([0xfb,0x82,252]),Data([0xf8])+Data(repeating:0,count:1276)] {
            try rejects { _ = try config.packetSampleFrames(packet) }
        }
        let description=try config.makeFormatDescription()
        let asbd=CMAudioFormatDescriptionGetStreamBasicDescription(description)!.pointee
        try require(asbd.mFormatID==kAudioFormatOpus && asbd.mSampleRate==48000 && asbd.mChannelsPerFrame==2 && asbd.mFramesPerPacket==0,"Native bridge Opus ASBD")
        var cookieSize=0
        guard let cookie=CMAudioFormatDescriptionGetMagicCookie(description,sizeOut:&cookieSize) else{throw OpusSmokeFailure(description:"Native ISO bridge produced no cookie")}
        try require(Data(bytes:cookie,count:cookieSize)==head,"CoreMedia should derive OpusHead, not require an invented native cookie")
        print("PASS: OpusHead family0, actual ISO/dOps native bridge, packet modes0–3, 2.5–120ms timing, malformed lengths/padding")

        let root=FileManager.default.temporaryDirectory.appendingPathComponent("Luma-Opus-\(UUID().uuidString)",isDirectory:true)
        try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
        defer{try? FileManager.default.removeItem(at:root)}
        let caf=root.appendingPathComponent("original.caf")
        let encoder=Process();encoder.executableURL=URL(fileURLWithPath:"/usr/bin/afconvert")
        encoder.arguments=["-f","caff","-d","opus@48000","-c","2","-b","64000","Tests/Fixtures/Luma-Tone.wav",caf.path]
        try encoder.run();encoder.waitUntilExit()
        try require(encoder.terminationStatus==0,"Native Opus fixture encoding")
        let (packets,table)=try readPackets(caf)
        try require(table.mPrimingFrames==312 && table.mNumberValidFrames==144000 && table.mRemainderFrames==648,"Native original Opus priming/valid/remainder metadata changed")
        let total=try packets.reduce(0){try $0 + config.packetSampleFrames($1)}
        try require(total==144960,"Native Opus packet timing \(total)")
        let movie=root.appendingPathComponent("original-opus.mov")
        try await writeMovie(config:config,packets:packets,trimAtEnd:Int(table.mRemainderFrames),output:movie)
        let (frames,start,rms)=try await decode(movie)
        print("Opus native decoded frames \(frames), firstPTS \(start), RMS \(rms)")
        try require(frames==144000,"Opus native decoded count \(frames), expected144000")
        try require(abs(start)<1.0/48000 && rms>0.01,"Opus trim/tone signal was not preserved")
        print("PASS: original native-encoded Opus -> original OpusHead/dOps config -> perpacket MOV passthrough -> native48kHz stereo PCM exactly144000frames, pre-skip312 and tail648 trimmed")
        var croppedHead=head
        croppedHead[10]=UInt8(2232 & 255);croppedHead[11]=UInt8(2232 >> 8)
        let croppedConfig=try OpusConfiguration.parse(croppedHead)
        let croppedMovie=root.appendingPathComponent("cropped-opus.mov")
        try await writeMovie(config:croppedConfig,packets:packets,trimAtEnd:648,output:croppedMovie)
        let (croppedFrames,croppedStart,_)=try await decode(croppedMovie)
        try require(croppedFrames==142080 && abs(croppedStart)<1.0/48000,"Multi-packet Opus priming: \(croppedFrames), \(croppedStart)")
        print("PASS:2232-frame pre-skip spans two whole packets plus312frames; nativePCM exactly142080frames, firstPTS0")
        var gainHead=head;gainHead[16]=0;gainHead[17]=6
        let gainConfig=try OpusConfiguration.parse(gainHead)
        try require(gainConfig.outputGain==1536,"Opus Q7.8 gain preservation")
        try rejects { _ = try gainConfig.makeFormatDescription() }
        print("PASS:nonzero OpusHead gain rejected explicitly; this native MOV path was observed to ignore the gain")
    }

    private static func readPackets(_ url:URL)throws->([Data],AudioFilePacketTableInfo){
        var opened:AudioFileID?
        let status=AudioFileOpenURL(url as CFURL,.readPermission,0,&opened)
        guard status==noErr,let file=opened else{throw OpusSmokeFailure(description:"Native CAF open \(status)")}
        defer{AudioFileClose(file)}
        var table=AudioFilePacketTableInfo(),size=UInt32(MemoryLayout<AudioFilePacketTableInfo>.size)
        try require(AudioFileGetProperty(file,kAudioFilePropertyPacketTableInfo,&size,&table)==noErr,"CAF packet table")
        var upper:UInt32=0,count:UInt64=0
        size=4;try require(AudioFileGetProperty(file,kAudioFilePropertyPacketSizeUpperBound,&size,&upper)==noErr,"CAF packet upper")
        size=8;try require(AudioFileGetProperty(file,kAudioFilePropertyAudioDataPacketCount,&size,&count)==noErr,"CAF packet count")
        try require(upper>0 && upper<100000 && count<1000,"Original fixture bounds")
        var result=[Data]()
        for index in 0..<count {
            var bytes=Data(count:Int(upper)),byteCount=upper,packetCount:UInt32=1,description=AudioStreamPacketDescription()
            let read=bytes.withUnsafeMutableBytes{AudioFileReadPacketData(file,false,&byteCount,&description,Int64(index),&packetCount,$0.baseAddress!)}
            try require(read==noErr && packetCount==1,"CAF packet extraction")
            result.append(Data(bytes.prefix(Int(byteCount))))
        }
        return(result,table)
    }

    private static func writeMovie(config:OpusConfiguration,packets:[Data],trimAtEnd:Int,output:URL)async throws{
        let format=try config.makeFormatDescription()
        let writer=try AVAssetWriter(outputURL:output,fileType:.mov)
        let input=AVAssetWriterInput(mediaType:.audio,outputSettings:nil,sourceFormatHint:format)
        input.expectsMediaDataInRealTime=false
        guard writer.canAdd(input) else{throw OpusSmokeFailure(description:"MOV rejected native bridged Opus")}
        writer.add(input)
        guard writer.startWriting() else{throw writer.error ?? OpusSmokeFailure(description:"MOV writer start")}
        writer.startSession(atSourceTime:.zero)
        var cumulative=0
        for (index,data) in packets.enumerated(){
            let frames=try config.packetSampleFrames(data)
            var block:CMBlockBuffer?
            var status=CMBlockBufferCreateWithMemoryBlock(allocator:nil,memoryBlock:nil,blockLength:data.count,blockAllocator:nil,customBlockSource:nil,offsetToData:0,dataLength:data.count,flags:0,blockBufferOut:&block)
            guard status==noErr,let block else{throw OpusSmokeFailure(description:"Opus block allocation")}
            status=data.withUnsafeBytes{CMBlockBufferReplaceDataBytes(with:$0.baseAddress!,blockBuffer:block,offsetIntoDestination:0,dataLength:data.count)}
            try require(status==noErr,"Opus block copy")
            var description=AudioStreamPacketDescription(mStartOffset:0,mVariableFramesInPacket:UInt32(frames),mDataByteSize:UInt32(data.count))
            var sample:CMSampleBuffer?
            status=CMAudioSampleBufferCreateReadyWithPacketDescriptions(allocator:nil,dataBuffer:block,formatDescription:format,sampleCount:1,
                presentationTimeStamp:CMTime(value:Int64(cumulative-config.preSkip),timescale:48000),packetDescriptions:&description,sampleBufferOut:&sample)
            guard status==noErr,let sample else{throw OpusSmokeFailure(description:"Opus sample creation \(status)")}
            let trimStart=max(0,min(frames,config.preSkip-cumulative))
            if trimStart>0 {CMSetAttachment(sample,key:kCMSampleBufferAttachmentKey_TrimDurationAtStart,value:CMTimeCopyAsDictionary(CMTime(value:Int64(trimStart),timescale:48000),allocator:nil)!,attachmentMode:kCMAttachmentMode_ShouldPropagate)}
            if index==packets.count-1 && trimAtEnd>0 {CMSetAttachment(sample,key:kCMSampleBufferAttachmentKey_TrimDurationAtEnd,value:CMTimeCopyAsDictionary(CMTime(value:Int64(trimAtEnd),timescale:48000),allocator:nil)!,attachmentMode:kCMAttachmentMode_ShouldPropagate)}
            let deadline=Date().addingTimeInterval(10)
            while !input.isReadyForMoreMediaData {
                guard writer.status == .writing,Date()<deadline else{throw writer.error ?? OpusSmokeFailure(description:"Opus writer stalled")}
                try await Task.sleep(nanoseconds:1_000_000)
            }
            guard input.append(sample) else{throw writer.error ?? OpusSmokeFailure(description:"Opus append failed")}
            cumulative += frames
        }
        input.markAsFinished();await writer.finishWriting()
        guard writer.status == .completed else{throw writer.error ?? OpusSmokeFailure(description:"Opus MOV did not complete")}
    }

    private static func decode(_ url:URL)async throws->(Int,Double,Double){
        let asset=AVURLAsset(url:url)
        guard let track=try await asset.loadTracks(withMediaType:.audio).first else{throw OpusSmokeFailure(description:"Missing Opus MOV audio")}
        let reader=try AVAssetReader(asset:asset)
        let output=AVAssetReaderTrackOutput(track:track,outputSettings:[AVFormatIDKey:kAudioFormatLinearPCM,AVSampleRateKey:48000,AVNumberOfChannelsKey:2,AVLinearPCMIsFloatKey:true,AVLinearPCMBitDepthKey:32])
        reader.add(output)
        guard reader.startReading() else{throw reader.error ?? OpusSmokeFailure(description:"Opus native decoder start")}
        var frames=0,energy=0.0,first:Double?
        while let sample=output.copyNextSampleBuffer(){
            if first==nil{first=CMSampleBufferGetPresentationTimeStamp(sample).seconds}
            frames += CMSampleBufferGetNumSamples(sample)
            if let block=CMSampleBufferGetDataBuffer(sample){
                var bytes=Data(count:CMBlockBufferGetDataLength(block))
                let byteCount=bytes.count
                let status=bytes.withUnsafeMutableBytes{CMBlockBufferCopyDataBytes(block,atOffset:0,dataLength:byteCount,destination:$0.baseAddress!)}
                try require(status==noErr,"Copy native Opus PCM")
                bytes.withUnsafeBytes{raw in for offset in stride(from:0,to:raw.count,by:4){let value=raw.loadUnaligned(fromByteOffset:offset,as:Float.self);energy += Double(value)*Double(value)}}
            }
        }
        guard reader.status == .completed else{throw reader.error ?? OpusSmokeFailure(description:"Opus native decoding incomplete")}
        return(frames,first ?? .nan,sqrt(energy/Double(max(1,frames*2))))
    }
}
