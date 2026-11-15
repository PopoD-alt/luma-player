import SwiftUI
import Combine

struct CodecSupportEntry: Decodable, Identifiable {
    let id: String
    let name: String
    let category: String
    let status: String
    let coverage: String
    var label: String {
        switch status {
        case "independent": return "Luma decoder"
        case "experimental": return "Limited support"
        case "system": return "macOS decoder"
        case "unverified": return "Not verified"
        default: return "Not implemented"
        }
    }
    var color: Color {
        switch status {
        case "independent": return .green
        case "experimental": return .orange
        case "system": return .blue
        default: return .secondary
        }
    }
}

@MainActor private final class CodecSupportModel: ObservableObject {
    @Published var query = ""
    let entries: [CodecSupportEntry]
    init() {
        struct Catalog: Decodable { let entries: [CodecSupportEntry] }
        if let url = Bundle.main.url(forResource: "CodecSupport", withExtension: "json"),
           let data = try? Data(contentsOf: url), let catalog = try? JSONDecoder().decode(Catalog.self, from: data) {
            entries = catalog.entries
        } else { entries = [] }
    }
    var filtered: [CodecSupportEntry] {
        let text = query.trimmingCharacters(in: .whitespacesAndNewlines)
        return entries.filter { text.isEmpty || $0.name.localizedCaseInsensitiveContains(text) || $0.coverage.localizedCaseInsensitiveContains(text) || $0.label.localizedCaseInsensitiveContains(text) }
    }
}

struct CodecSupportView: View {
    @Environment(\.dismiss) private var dismiss
    @StateObject private var model = CodecSupportModel()
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Format Support").font(.system(size: 23, weight: .semibold))
                    Text("VideoLAN libVLC playback · codec, profile and container all matter")
                        .font(.system(size: 12)).foregroundStyle(.secondary)
                }
                Spacer()
                Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
            }
            Text("Luma now uses libVLC for normal playback. Available formats follow the runtime and codec modules installed with it; a filename extension alone is not a playback guarantee.")
                .font(.system(size: 12)).foregroundStyle(.secondary)
            GroupBox("Verified in this Mac review") {
                Text("Synthetic H.264 MP4, HEVC Main10 with AAC 5.1, and AVC/AAC Matroska decoded and displayed through libVLC. These tests do not cover every VLC format, DRM-protected media or physical surround-sound output.").frame(maxWidth: .infinity, alignment: .leading).padding(10)
            }
            GroupBox("Watching over Wi-Fi") {
                Text("The desktop serves the original file; Android decodes it. Local sharing does not transcode unsupported media. Use the same trusted network and keep the desktop awake with sharing enabled.").frame(maxWidth: .infinity, alignment: .leading).padding(10)
            }
            Button("Third-party licenses") {
                if let url = Bundle.main.resourceURL?.appendingPathComponent("Licenses") { NSWorkspace.shared.open(url) }
            }
        }.padding(24).frame(width: 660)
    }
}
