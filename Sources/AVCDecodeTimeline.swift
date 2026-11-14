import Foundation

/// Constructs a bounded constant-frame-rate decode schedule when a Matroska AVC/HEVC
/// track supplies PTS in coding order but carries no DTS. This does not recover
/// the encoder's original HRD clock. Matroska coding order and unchanged PTS remain
/// authoritative; no compressed packets are sorted or decoded here.
///
/// Reference: RFC 9559 §§10–11. Callers must separately establish a supported AVC
/// configuration and a usable random-access starting point. VFR, missing/duplicate
/// presentation positions, and more than 16 frames of decode-rank lag are rejected.
enum AVCDecodeTimeline {
    static func reconstruct(presentationTimes: [Double], frameDuration: Double,
                            timestampTolerance: Double) throws -> [Double] {
        guard presentationTimes.count >= 2, presentationTimes.count <= 2_000_000 else {
            throw AVCDecodeTimelineError.unsupported("Video DTS reconstruction requires 2–2,000,000 frames")
        }
        guard frameDuration.isFinite, frameDuration > 0,
              timestampTolerance.isFinite, timestampTolerance >= 0,
              timestampTolerance * 2 < frameDuration else {
            throw AVCDecodeTimelineError.unsupported("Video DTS reconstruction needs a positive DefaultDuration and finer-than-half-frame timestamp precision")
        }
        let timeLimit = Double(Int64.max) / 1_000_000_000 - 1
        guard presentationTimes.allSatisfy({ $0.isFinite && abs($0) < timeLimit }) else {
            throw AVCDecodeTimelineError.unsupported("Video presentation timestamps are non-finite or out of range")
        }
        // Sorting a metadata copy establishes presentation ranks; the returned
        // array always follows the caller's original coded-packet order.
        let presentationOrder = presentationTimes.enumerated().sorted { a, b in
            a.element == b.element ? a.offset < b.offset : a.element < b.element
        }
        let firstPTS = presentationOrder[0].element
        guard presentationOrder[0].offset == 0 else {
            throw AVCDecodeTimelineError.unsupported("Video reordered stream does not begin at its earliest presentation point; open-GOP/preroll reconstruction is not implemented")
        }
        let epsilon = max(1e-9, abs(firstPTS) * Double.ulpOfOne * 8)
        var maximumLag = 0
        for (rank, entry) in presentationOrder.enumerated() {
            if rank > 0, entry.element <= presentationOrder[rank - 1].element {
                throw AVCDecodeTimelineError.unsupported("Video has duplicate presentation timestamps")
            }
            let expected = firstPTS + Double(rank) * frameDuration
            guard expected.isFinite, abs(entry.element - expected) <= timestampTolerance + epsilon else {
                throw AVCDecodeTimelineError.unsupported("Video presentation timestamps are not a complete constant-frame-rate grid within the Matroska timestamp precision")
            }
            maximumLag = max(maximumLag, entry.offset - rank)
        }
        guard maximumLag <= 16 else {
            throw AVCDecodeTimelineError.unsupported("Video decode-rank lag exceeds the supported 16-frame bound")
        }
        let latestOrigin = presentationTimes.enumerated().map { $0.element - Double($0.offset) * frameDuration }.min()!
        let nominalOrigin = firstPTS - Double(maximumLag) * frameDuration
        let origin = min(nominalOrigin, latestOrigin) - epsilon
        var result: [Double] = []
        result.reserveCapacity(presentationTimes.count)
        for index in presentationTimes.indices {
            let dts = origin + Double(index) * frameDuration
            guard dts.isFinite, abs(dts) < timeLimit,
                  dts <= presentationTimes[index] + epsilon,
                  result.last.map({ dts > $0 }) ?? true else {
                throw AVCDecodeTimelineError.unsupported("Video decode schedule cannot be represented monotonically before presentation")
            }
            result.append(dts)
        }
        return result
    }
}

enum AVCDecodeTimelineError: Error, LocalizedError {
    case unsupported(String)
    var errorDescription: String? {
        switch self { case .unsupported(let detail): return detail }
    }
}
