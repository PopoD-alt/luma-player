import Foundation
import AVFoundation

@main struct MediaLanguageSmoke {
    static func main() {
        precondition(MediaLanguage.iso639("en") == "eng")
        precondition(MediaLanguage.iso639("jpn") == "jpn")
        precondition(MediaLanguage.iso639("not-a-language") == nil)
        precondition(MediaLanguage.bcp47("en_US") == "en-US")
        precondition(MediaLanguage.bcp47("ja") == "ja")
        precondition(MediaLanguage.bcp47("invalid--tag") == nil)
        precondition(MediaLanguage.bcp47("en;bad") == nil)
        let input = AVAssetWriterInput(mediaType: .audio, outputSettings: nil)
        for code in ["en", "eng", "jpn", "fre", "ger", "und", "invalid--tag", "zz"] {
            input.languageCode = MediaLanguage.iso639(code)
            input.extendedLanguageTag = MediaLanguage.bcp47(code)
        }
        print("PASS: normalized language metadata and invalid tags do not reach exception-throwing native setters")
    }
}
