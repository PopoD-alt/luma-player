import Foundation
import AVFoundation
import CoreVideo
import CoreMedia
import CoreGraphics
import CryptoKit

private struct NativeFormatFailure: Error, CustomStringConvertible { let description: String }
private struct FormatCandidate {
    let id: String
    let name: String
    let codec: AVVideoCodecType
    let width: Int
    let height: Int
}
private struct DecodeProof {
    let frameHashes: [String]
    let firstPTS: Double
    let width: Int
    let height: Int
}

@main struct NativeFormatEvidence {
    static func main() async throws {
        setbuf(stdout, nil)
        let project = URL(fileURLWithPath: FileManager.default.currentDirectoryPath, isDirectory: true)
        // Generated encodes belong outside the reviewed fixture tree. Native
        // encoder metadata can vary between runs; verification must leave the
        // reviewed Git commit clean before release packaging begins.
        let fixtures = project.appendingPathComponent("build/tests/NativeFormats", isDirectory: true)
        let progress = project.appendingPathComponent("Progress", isDirectory: true)
        try FileManager.default.createDirectory(at: fixtures, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: progress, withIntermediateDirectories: true)
        let candidates = [
            FormatCandidate(id:"prores-422",name:"Apple ProRes 422",codec:.proRes422,width:320,height:180),
            FormatCandidate(id:"prores-422-hq",name:"Apple ProRes 422 HQ",codec:.proRes422HQ,width:320,height:180),
            FormatCandidate(id:"prores-422-lt",name:"Apple ProRes 422 LT",codec:.proRes422LT,width:320,height:180),
            FormatCandidate(id:"prores-422-proxy",name:"Apple ProRes 422 Proxy",codec:.proRes422Proxy,width:320,height:180),
            FormatCandidate(id:"prores-4444",name:"Apple ProRes 4444",codec:.proRes4444,width:320,height:180),
            FormatCandidate(id:"prores-4444-xq",name:"Apple ProRes 4444 XQ",codec:AVVideoCodecType(rawValue:"ap4x"),width:320,height:180),
            FormatCandidate(id:"h263",name:"H.263",codec:AVVideoCodecType(rawValue:"h263"),width:352,height:288)
        ]
        var verified = [[String:Any]](), notes = [String]()
        for candidate in candidates {
            let output = fixtures.appendingPathComponent("Luma-\(candidate.id).mov")
            do {
                try await encode(candidate, output:output)
                let asset = AVURLAsset(url:output)
                guard let track = try await asset.loadTracks(withMediaType:.video).first else {throw NativeFormatFailure(description:"No encoded video track")}
                let descriptions = try await track.load(.formatDescriptions)
                guard let description = descriptions.first else {throw NativeFormatFailure(description:"No actual codec description")}
                let actual = fourCC(CMFormatDescriptionGetMediaSubType(description))
                guard actual == candidate.codec.rawValue else {throw NativeFormatFailure(description:"Requested\(candidate.codec.rawValue), actual\(actual)")}
                let decoded = try await decode(asset:asset,track:track,startFrame:0,frames:15)
                guard decoded.frameHashes.count == 15, decoded.width == candidate.width, decoded.height == candidate.height,
                      decoded.frameHashes.first != decoded.frameHashes.last else {throw NativeFormatFailure(description:"Native decode lost frame count, dimensions or motion")}
                let seek = try await decode(asset:asset,track:track,startFrame:6,frames:9)
                guard seek.frameHashes.count == 9, abs(seek.firstPTS - 0.4) < 1e-7,
                      seek.frameHashes.first == decoded.frameHashes[6] else {throw NativeFormatFailure(description:"Native seek did not match uninterrupted pixels")}
                let duration = try await asset.load(.duration).seconds
                guard abs(duration-1)<1e-7 else {throw NativeFormatFailure(description:"Unexpected output duration\(duration)")}
                let bytes = try output.resourceValues(forKeys:[URLResourceKey.fileSizeKey]).fileSize ?? 0
                verified.append([
                    "id":candidate.id,"name":candidate.name,"codec_fourcc":actual,
                    "route":"native AVFoundation / VideoToolbox", "container":"MOV",
                    "verification":"actual native encode, full pixel decode and seek comparison passed",
                    "original_compressed_decoder":false,
                    "fixture":"build/tests/NativeFormats/\(output.lastPathComponent)",
                    "source":"original synthetic gradient and moving rectangle, generated with CoreGraphics",
                    "file_bytes":bytes,"width":decoded.width,"height":decoded.height,
                    "frames":decoded.frameHashes.count,"frames_per_second":15,"duration_seconds":duration,
                    "first_frame_sha256":decoded.frameHashes.first!,"last_frame_sha256":decoded.frameHashes.last!,
                    "seek":["target_seconds":0.4,"decoded_frames":seek.frameHashes.count,"first_frame_matches_uninterrupted":true],
                    "limits":["Only the generated 8-bit BGRA source was exercised; this does not verify 10/12-bit input or alpha preservation.",
                              "This verifies the native MOV playback route, not Matroska/WebM encapsulation or an original compressed decoder."]
                ])
                print("PASS:\(candidate.name) actual\(actual),15native decodedframes,\(candidate.width)×\(candidate.height),1s;0.4sseekmatchespixels,\(bytes)bytes")
            } catch {
                try? FileManager.default.removeItem(at:output)
                let note="\(candidate.name) was not verified: \(error)"
                notes.append(note); print("UNVERIFIED:\(note)")
            }
        }
        guard !verified.isEmpty else {throw NativeFormatFailure(description:"No additional native video format could be verified")}
        let evidence:[String:Any] = [
            "schema_version":1,"generated_at":ISO8601DateFormatter().string(from:Date()),
            "generator":"Tests/NativeFormatEvidence.swift","reproduce":"./test-native-formats.sh",
            "platform":ProcessInfo.processInfo.operatingSystemVersionString,
            "verification_scope":"Local native playback compatibility only. The verified_formats list contains only formats whose original synthetic fixture was actually encoded, decoded to pixels and seek-compared.",
            "verified_formats":verified,"probe_notes":notes,
            "not_verified":["VP9 and AV1: no local Apple encoder or independent valid synthetic payload was available; codec identifiers and hardware flags are not decode proof.",
                            "DV and JPEG2000 video: no local Apple video encoder was listed; no payload playback claim is made.",
                            "ProRes RAW and RAW HQ are not covered by the standard ProRes fixtures."],
            "primary_references":[
                "local SDK: CoreMedia.framework/Headers/CMFormatDescription.h, video codec constants",
                "local SDK: VideoToolbox.framework/Headers/VTVideoEncoderList.h, VTCopyVideoEncoderList",
                "https://developer.apple.com/documentation/avfoundation/avvideocodectype",
                "https://developer.apple.com/documentation/avfoundation/avassetreadertrackoutput"
            ]
        ]
        let json = try JSONSerialization.data(withJSONObject:evidence,options:[.prettyPrinted,.sortedKeys])
        let destination = progress.appendingPathComponent("native-format-evidence.json")
        try json.write(to:destination,options:.atomic)
        print("Saved verified native-format evidence:\(destination.path)")
    }

    private static func encode(_ format:FormatCandidate,output:URL) async throws {
        if FileManager.default.fileExists(atPath:output.path) {try FileManager.default.removeItem(at:output)}
        let writer = try AVAssetWriter(outputURL:output,fileType:.mov)
        writer.movieTimeScale = 15_000
        let settings:[String:Any] = [AVVideoCodecKey:format.codec,AVVideoWidthKey:format.width,AVVideoHeightKey:format.height]
        guard writer.canApply(outputSettings:settings,forMediaType:.video) else {throw NativeFormatFailure(description:"Native writer rejects codec settings")}
        let input = AVAssetWriterInput(mediaType:.video,outputSettings:settings)
        input.expectsMediaDataInRealTime = false
        input.mediaTimeScale = 15_000
        let attributes:[String:Any] = [kCVPixelBufferPixelFormatTypeKey as String:kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String:format.width,kCVPixelBufferHeightKey as String:format.height,
            kCVPixelBufferCGImageCompatibilityKey as String:true,kCVPixelBufferCGBitmapContextCompatibilityKey as String:true]
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput:input,sourcePixelBufferAttributes:attributes)
        guard writer.canAdd(input) else {throw NativeFormatFailure(description:"Native writer cannot add video input")}
        writer.add(input)
        guard writer.startWriting() else {throw writer.error ?? NativeFormatFailure(description:"Native encode cannot start")}
        var completed=false
        defer {if !completed {writer.cancelWriting()}}
        writer.startSession(atSourceTime:.zero)
        guard let pool=adaptor.pixelBufferPool else {throw NativeFormatFailure(description:"No native pixel buffer pool")}
        for index in 0..<15 {
            let deadline=Date().addingTimeInterval(10)
            while !input.isReadyForMoreMediaData {
                guard writer.status == .writing,Date()<deadline else {throw writer.error ?? NativeFormatFailure(description:"Native encoder stalled")}
                try await Task.sleep(nanoseconds:1_000_000)
            }
            let buffer=try pixels(pool:pool,width:format.width,height:format.height,frame:index)
            guard adaptor.append(buffer,withPresentationTime:CMTime(value:Int64(index),timescale:15)) else {throw writer.error ?? NativeFormatFailure(description:"Native frame append failed")}
        }
        input.markAsFinished();await writer.finishWriting()
        guard writer.status == .completed else {throw writer.error ?? NativeFormatFailure(description:"Native encode failed to complete")}
        completed=true
    }

    private static func pixels(pool:CVPixelBufferPool,width:Int,height:Int,frame:Int) throws -> CVPixelBuffer {
        var allocated:CVPixelBuffer?
        guard CVPixelBufferPoolCreatePixelBuffer(nil,pool,&allocated)==kCVReturnSuccess,let buffer=allocated else {throw NativeFormatFailure(description:"Pixel allocation")}
        CVPixelBufferLockBaseAddress(buffer,[])
        defer {CVPixelBufferUnlockBaseAddress(buffer,[])}
        guard let context=CGContext(data:CVPixelBufferGetBaseAddress(buffer),width:width,height:height,bitsPerComponent:8,
            bytesPerRow:CVPixelBufferGetBytesPerRow(buffer),space:CGColorSpaceCreateDeviceRGB(),
            bitmapInfo:CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) else {throw NativeFormatFailure(description:"Synthetic drawing context")}
        let phase=CGFloat(frame)/14
        let colors=[CGColor(red:0.1+0.2*phase,green:0.1,blue:0.3,alpha:1),CGColor(red:0.05,green:0.4,blue:0.5+0.2*phase,alpha:1)]
        let gradient=CGGradient(colorsSpace:CGColorSpaceCreateDeviceRGB(),colors:colors as CFArray,locations:[0,1])!
        context.drawLinearGradient(gradient,start:.zero,end:CGPoint(x:width,y:height),options:[])
        context.setFillColor(CGColor(red:0.9,green:0.7,blue:0.15,alpha:1))
        context.fill(CGRect(x:20+Int(phase*CGFloat(width-100)),y:height/3,width:64,height:height/3))
        return buffer
    }

    private static func decode(asset:AVAsset,track:AVAssetTrack,startFrame:Int,frames:Int) async throws -> DecodeProof {
        let reader=try AVAssetReader(asset:asset)
        let output=AVAssetReaderTrackOutput(track:track,outputSettings:[kCVPixelBufferPixelFormatTypeKey as String:kCVPixelFormatType_32BGRA])
        reader.add(output)
        if startFrame>0 {reader.timeRange=CMTimeRange(start:CMTime(value:Int64(startFrame),timescale:15),duration:CMTime(value:Int64(frames),timescale:15))}
        guard reader.startReading() else {throw reader.error ?? NativeFormatFailure(description:"Native decode cannot start")}
        var hashes=[String](),first:Double?,width=0,height=0
        while let sample=output.copyNextSampleBuffer() {
            guard let image=CMSampleBufferGetImageBuffer(sample) else {throw NativeFormatFailure(description:"Native decoder returned no pixels")}
            if first==nil {first=CMSampleBufferGetPresentationTimeStamp(sample).seconds}
            width=CVPixelBufferGetWidth(image);height=CVPixelBufferGetHeight(image)
            CVPixelBufferLockBaseAddress(image,.readOnly)
            guard let base=CVPixelBufferGetBaseAddress(image) else {CVPixelBufferUnlockBaseAddress(image,.readOnly);throw NativeFormatFailure(description:"Decoded pixel buffer unavailable")}
            let row=CVPixelBufferGetBytesPerRow(image)
            var active=Data()
            for y in 0..<height {active.append(base.advanced(by:y*row).assumingMemoryBound(to:UInt8.self),count:width*4)}
            CVPixelBufferUnlockBaseAddress(image,.readOnly)
            hashes.append(SHA256.hash(data:active).map{String(format:"%02x",$0)}.joined())
        }
        guard reader.status == .completed else {throw reader.error ?? NativeFormatFailure(description:"Native decode incomplete")}
        return DecodeProof(frameHashes:hashes,firstPTS:first ?? .nan,width:width,height:height)
    }

    private static func fourCC(_ value:UInt32)->String {
        String(bytes:[24,16,8,0].map {UInt8((value >> $0)&255)},encoding:.ascii) ?? "unknown"
    }
}
