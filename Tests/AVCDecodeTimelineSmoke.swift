import Foundation

@main struct AVCDecodeTimelineSmoke {
    static func main() throws {
        let d = 1001.0 / 24000
        func check(_ pts: [Double], tolerance: Double = 0.001) throws -> [Double] {
            let original = pts
            let dts = try AVCDecodeTimeline.reconstruct(presentationTimes: pts, frameDuration: d, timestampTolerance: tolerance)
            precondition(pts == original && dts.count == pts.count)
            for index in dts.indices {
                precondition(dts[index] <= pts[index] + 1e-8)
                if index > 0 { precondition(abs((dts[index] - dts[index - 1]) - d) < 1e-8) }
            }
            return dts
        }
        func rejects(_ label: String, pts: [Double], duration: Double = d, tolerance: Double = 0.001) {
            do { _ = try AVCDecodeTimeline.reconstruct(presentationTimes: pts, frameDuration: duration, timestampTolerance: tolerance) }
            catch is AVCDecodeTimelineError { return }
            catch { fatalError("Unexpected error for \(label): \(error)") }
            fatalError("Accepted unsupported AVC timing: \(label)")
        }
        let ipbb = [0,3,1,2,6,4,5].map { Double($0) * d }
        let timeline = try check(ipbb)
        precondition(timeline[0] < 0 && timeline[0] > -2 * d)
        let hierarchy = [0,8,4,1,2,3,5,6,7,16,12,9,10,11,13,14,15].map { Double($0) * d }
        _ = try check(hierarchy)
        let rounded = hierarchy.map { ($0 * 1000).rounded() / 1000 }
        _ = try check(rounded)
        _ = try check(ipbb.map { $0 - 0.1 })
        rejects("variable frame rate", pts: [0,d,2.2*d])
        rejects("missing presentation position", pts: [0,2*d,3*d])
        rejects("duplicate presentation position", pts: [0,d,d])
        rejects("non-finite timestamp", pts: [0,.nan,d])
        rejects("open GOP/preroll", pts: [d,0,2*d])
        rejects("no DefaultDuration", pts: ipbb, duration: 0)
        rejects("invalid duration", pts: ipbb, duration: .infinity)
        rejects("coarse timestamp precision", pts: ipbb, tolerance: d)
        let tooDeep = ([0] + Array(18...34) + Array(1...17)).map { Double($0) * d }
        rejects("more than 16-frame lag", pts: tooDeep)
        print("PASS: reordered IPBB/hierarchical CFR, quantized/negative PTS, DTS monotonicity and PTS preservation; VFR/gaps/duplicates/preroll/depth/precision/invalid-value rejection")
    }
}
