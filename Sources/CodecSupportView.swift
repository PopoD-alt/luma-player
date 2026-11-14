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
                    Text("Experimental coverage · codec, profile and container all matter")
                        .font(.system(size: 12)).foregroundStyle(.secondary)
                }
                Spacer()
                Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
            }
            Text("Luma does not yet match every VLC format. Luma decoders are independently written; macOS decoders use the system media framework.")
                .font(.system(size: 12)).foregroundStyle(.secondary)
            TextField("Search formats or decoder type", text: $model.query).textFieldStyle(.roundedBorder)
            List(model.filtered) { entry in
                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text(entry.name).font(.system(size: 13, weight: .semibold))
                        Spacer()
                        Text(entry.label).font(.system(size: 10, weight: .medium)).foregroundStyle(entry.color)
                    }
                    Text(entry.coverage).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }.padding(.vertical, 5)
            }.listStyle(.inset)
        }.padding(24).frame(width: 660, height: 650)
    }
}
