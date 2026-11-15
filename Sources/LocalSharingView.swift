import SwiftUI

struct LocalSharingView: View {
    @ObservedObject var player: PlayerCoordinator
    @ObservedObject var server: LocalSharingServer
    @Environment(\.dismiss) private var dismiss
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack { Image(systemName: "wifi").font(.title2); Text("Watch on your phone").font(.title2.weight(.semibold)); Spacer() }
            Text("Open Luma on Android on the same Wi-Fi, select this Mac, and enter the pairing code. Your playlist updates on the phone while sharing is on.").foregroundStyle(.secondary)
            Text("Use a trusted home network. This preview streams over local HTTP. The Mac must stay awake with Luma open; video is buffered on the phone, without a saved download.").font(.callout).foregroundStyle(.secondary)
            Divider()
            Text(server.status).font(.headline)
            if server.isSharing {
                TimelineView(.periodic(from: .now, by: 1)) { context in
                    VStack(alignment: .leading, spacing: 8) {
                        Text(context.date < server.pairingExpires ? server.pairingCode : "Code expired").font(.system(size: 34, weight: .medium, design: .monospaced)).textSelection(.enabled)
                        Text("\(server.pairedDevices) paired device\(server.pairedDevices == 1 ? "" : "s") · \(server.activeStreams) active stream\(server.activeStreams == 1 ? "" : "s")").font(.caption).foregroundStyle(.secondary)
                        Text("Pairing codes expire after 10 minutes. Luma prevents idle system sleep while sending video; closing the lid or choosing Sleep still stops sharing.").font(.caption).foregroundStyle(.secondary)
                    }
                }
                ForEach(server.addresses, id: \.self) { Text($0).font(.system(.body, design: .monospaced)).textSelection(.enabled) }
                Text("If automatic discovery is unavailable, enter an address above in Android. A 127.0.0.1 address means no usable local IPv4 address was found.").font(.caption).foregroundStyle(.secondary)
                HStack {
                    Button("New pairing code") { do { try server.renewPairingCode() } catch { player.tell("Could not generate a pairing code") } }
                    Button("Stop sharing and disconnect", role: .destructive) { server.stop() }
                }
            } else {
                Button("Start sharing this playlist") { player.startSharing() }.buttonStyle(.borderedProminent)
            }
            if let error = server.error { Text(error).foregroundStyle(.red).textSelection(.enabled) }
            HStack { Spacer(); Button("Done") { dismiss() }.keyboardShortcut(.defaultAction) }
        }.padding(28).frame(width: 550)
    }
}
