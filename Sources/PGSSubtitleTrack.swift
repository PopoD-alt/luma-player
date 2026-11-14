import Foundation

struct PGSSubtitleTrack: Sendable {
    let id: Int32
    let name: String
    let language: String
    let events: [PGSDisplayEvent]
    let isDefault: Bool
    let isForced: Bool
}
