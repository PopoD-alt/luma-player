import Foundation

enum MediaClockError: Error, LocalizedError {
    case unsupported
    var errorDescription: String? { "The audio sample-rate combination cannot be represented by this movie clock." }
}

enum MediaClock {
    /// Align both the millisecond container grid and each audio sample grid.
    /// A nanosecond movie clock can round a precise trim upward by one sample.
    static func movieTimeScale(audioSampleRates: [Double]) throws -> Int32 {
        func gcd(_ left: Int64, _ right: Int64) -> Int64 {
            var a = left, b = right
            while b != 0 { let remainder = a % b; a = b; b = remainder }
            return a
        }
        var clock: Int64 = 1000
        for value in audioSampleRates {
            guard value.isFinite, value.rounded() == value, (1...768_000).contains(value) else { throw MediaClockError.unsupported }
            let rate = Int64(value), multiplier = rate / gcd(clock, rate)
            guard clock <= Int64(Int32.max) / multiplier else { throw MediaClockError.unsupported }
            clock *= multiplier
        }
        return Int32(clock)
    }
}
