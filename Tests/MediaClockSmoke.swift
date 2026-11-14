import Foundation

@main struct MediaClockSmoke {
    static func main() throws {
        let empty = try MediaClock.movieTimeScale(audioSampleRates: [])
        let opus = try MediaClock.movieTimeScale(audioSampleRates: [48000])
        precondition(empty == 1000 && opus == 48000)
        let clock = try MediaClock.movieTimeScale(audioSampleRates: [44100, 48000, 96000])
        precondition(clock == 14_112_000)
        for rate in [44100, 48000, 96000] { precondition(Int(clock) % rate == 0) }
        for rates in [[Double.nan], [0], [48000.5], [48001, 44100]] {
            do { _ = try MediaClock.movieTimeScale(audioSampleRates: rates); fatalError("Invalid clock accepted") }
            catch is MediaClockError {}
        }
        print("PASS: sample-aligned movie clocks, mixed audio rates and invalid/overflow rejection")
    }
}
