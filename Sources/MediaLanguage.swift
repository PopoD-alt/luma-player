import Foundation

/// Converts permissive container tags to the conservative language subset used
/// by AVAssetWriterInput's exception-throwing metadata setters.
enum MediaLanguage {
    private static let knownCodes = Set(Locale.LanguageCode.isoLanguageCodes.compactMap { $0.identifier(.alpha3) } + ["und", "mul", "zxx"])
    static func iso639(_ source: String) -> String? {
        let raw = source.lowercased()
        guard raw.range(of: "^[a-z]{2,3}$", options: .regularExpression) != nil,
              let code = Locale.LanguageCode(raw).identifier(.alpha3), knownCodes.contains(code) else { return nil }
        return code
    }
    static func bcp47(_ source: String?) -> String? {
        guard let source, source.count <= 35 else { return nil }
        let tag = source.replacingOccurrences(of: "_", with: "-")
        guard tag.range(of: "^[A-Za-z]{2,3}(?:-[A-Za-z]{4})?(?:-(?:[A-Za-z]{2}|[0-9]{3}))?$", options: .regularExpression) != nil,
              let base = tag.split(separator: "-").first, iso639(String(base)) != nil else { return nil }
        return Locale.canonicalLanguageIdentifier(from: tag)
    }
}
