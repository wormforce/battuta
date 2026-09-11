import AVFAudio
import CoreGraphics
import Darwin
import Foundation

private enum HarnessFailure: Error, CustomStringConvertible {
    case assertion(String)

    var description: String {
        switch self {
        case let .assertion(message): message
        }
    }
}

private struct HarnessResults {
    private(set) var passed = 0

    mutating func check(
        _ condition: @autoclosure () -> Bool,
        _ message: String,
        file: StaticString = #filePath,
        line: UInt = #line
    ) throws {
        guard condition() else {
            throw HarnessFailure.assertion("\(file):\(line): \(message)")
        }
        passed += 1
    }

    mutating func expectError(
        _ message: String,
        _ operation: () throws -> Void,
        matches: (Error) -> Bool
    ) throws {
        do {
            try operation()
            throw HarnessFailure.assertion("Expected error: \(message)")
        } catch let failure as HarnessFailure {
            throw failure
        } catch {
            guard matches(error) else {
                throw HarnessFailure.assertion("Wrong error for \(message): \(error)")
            }
            passed += 1
        }
    }
}

private struct PCM16MonoWave {
    let sampleRate: Int
    let channels: Int
    let bitsPerSample: Int
    let samples: [Int16]

    init(contentsOf url: URL) throws {
        let bytes = [UInt8](try Data(contentsOf: url))
        guard bytes.count >= 12,
              Array(bytes[0..<4]) == Array("RIFF".utf8),
              Array(bytes[8..<12]) == Array("WAVE".utf8) else {
            throw HarnessFailure.assertion("invalid RIFF/WAVE header: \(url.path)")
        }

        func uint16(at offset: Int) -> UInt16 {
            UInt16(bytes[offset]) | (UInt16(bytes[offset + 1]) << 8)
        }

        func uint32(at offset: Int) -> UInt32 {
            UInt32(bytes[offset])
                | (UInt32(bytes[offset + 1]) << 8)
                | (UInt32(bytes[offset + 2]) << 16)
                | (UInt32(bytes[offset + 3]) << 24)
        }

        var format: (audioFormat: UInt16, channels: UInt16, sampleRate: UInt32, bits: UInt16)?
        var sampleBytes: ArraySlice<UInt8>?
        var offset = 12
        while offset <= bytes.count - 8 {
            let chunkID = String(bytes: bytes[offset..<(offset + 4)], encoding: .ascii)
            let chunkSize = Int(uint32(at: offset + 4))
            let payloadStart = offset + 8
            guard chunkSize <= bytes.count - payloadStart else {
                throw HarnessFailure.assertion("truncated WAV chunk in \(url.path)")
            }
            let payloadEnd = payloadStart + chunkSize

            switch chunkID {
            case "fmt ":
                guard chunkSize >= 16 else {
                    throw HarnessFailure.assertion("short WAV format chunk: \(url.path)")
                }
                format = (
                    audioFormat: uint16(at: payloadStart),
                    channels: uint16(at: payloadStart + 2),
                    sampleRate: uint32(at: payloadStart + 4),
                    bits: uint16(at: payloadStart + 14)
                )
            case "data":
                sampleBytes = bytes[payloadStart..<payloadEnd]
            default:
                break
            }

            offset = payloadEnd + (chunkSize & 1)
        }

        guard let format, let sampleBytes,
              format.audioFormat == 1,
              format.channels == 1,
              format.bits == 16,
              sampleBytes.count.isMultiple(of: 2) else {
            throw HarnessFailure.assertion("WAV must be mono 16-bit PCM: \(url.path)")
        }

        var decoded = [Int16]()
        decoded.reserveCapacity(sampleBytes.count / 2)
        var sampleOffset = sampleBytes.startIndex
        while sampleOffset < sampleBytes.endIndex {
            let bits = UInt16(sampleBytes[sampleOffset])
                | (UInt16(sampleBytes[sampleOffset + 1]) << 8)
            decoded.append(Int16(bitPattern: bits))
            sampleOffset += 2
        }
        guard !decoded.isEmpty else {
            throw HarnessFailure.assertion("WAV has no samples: \(url.path)")
        }

        sampleRate = Int(format.sampleRate)
        channels = Int(format.channels)
        bitsPerSample = Int(format.bits)
        samples = decoded
    }
}

private struct PointerSpectrumMetrics {
    let peakAmplitude: Double
    let rmsAmplitude: Double
    let tailRMSDBFS: Double
    let centroidHz: Double
    let energyAbove8KHz: Double

    init(wave: PCM16MonoWave) {
        let normalized = wave.samples.map { Double($0) / 32_768 }
        peakAmplitude = normalized.lazy.map(abs).max() ?? 0
        rmsAmplitude = sqrt(normalized.lazy.map { $0 * $0 }.reduce(0, +) / Double(normalized.count))

        let tailCount = min(max(wave.sampleRate / 1_000, 1), normalized.count)
        let tailEnergy = normalized.suffix(tailCount).lazy.map { $0 * $0 }.reduce(0, +)
            / Double(tailCount)
        let tailRMS = sqrt(tailEnergy)
        tailRMSDBFS = tailRMS > 0 ? 20 * log10(tailRMS) : -.infinity

        var fftSize = 1
        while fftSize < normalized.count {
            fftSize <<= 1
        }
        fftSize = max(fftSize, 2)

        var real = [Double](repeating: 0, count: fftSize)
        var imaginary = [Double](repeating: 0, count: fftSize)
        let windowDenominator = Double(max(normalized.count - 1, 1))
        for index in normalized.indices {
            let hann = 0.5 - 0.5 * cos(2 * .pi * Double(index) / windowDenominator)
            real[index] = normalized[index] * hann
        }
        Self.radix2FFT(real: &real, imaginary: &imaginary)

        let binWidth = Double(wave.sampleRate) / Double(fftSize)
        var totalEnergy = 0.0
        var weightedFrequency = 0.0
        var highFrequencyEnergy = 0.0
        for bin in 0...fftSize / 2 {
            let energy = real[bin] * real[bin] + imaginary[bin] * imaginary[bin]
            let frequency = Double(bin) * binWidth
            totalEnergy += energy
            weightedFrequency += frequency * energy
            if frequency >= 8_000 {
                highFrequencyEnergy += energy
            }
        }
        centroidHz = totalEnergy > 0 ? weightedFrequency / totalEnergy : 0
        energyAbove8KHz = totalEnergy > 0 ? highFrequencyEnergy / totalEnergy : 0
    }

    private static func radix2FFT(real: inout [Double], imaginary: inout [Double]) {
        precondition(real.count == imaginary.count && real.count.isPowerOfTwo)
        let count = real.count

        var reversed = 0
        if count > 1 {
            for index in 1..<count {
                var bit = count >> 1
                while reversed & bit != 0 {
                    reversed ^= bit
                    bit >>= 1
                }
                reversed ^= bit
                if index < reversed {
                    real.swapAt(index, reversed)
                    imaginary.swapAt(index, reversed)
                }
            }
        }

        var length = 2
        while length <= count {
            let angle = -2 * Double.pi / Double(length)
            let stepReal = cos(angle)
            let stepImaginary = sin(angle)
            let halfLength = length / 2
            var blockStart = 0
            while blockStart < count {
                var twiddleReal = 1.0
                var twiddleImaginary = 0.0
                for offset in 0..<halfLength {
                    let even = blockStart + offset
                    let odd = even + halfLength
                    let oddReal = real[odd] * twiddleReal - imaginary[odd] * twiddleImaginary
                    let oddImaginary = real[odd] * twiddleImaginary + imaginary[odd] * twiddleReal
                    let evenReal = real[even]
                    let evenImaginary = imaginary[even]
                    real[even] = evenReal + oddReal
                    imaginary[even] = evenImaginary + oddImaginary
                    real[odd] = evenReal - oddReal
                    imaginary[odd] = evenImaginary - oddImaginary

                    let nextTwiddleReal = twiddleReal * stepReal - twiddleImaginary * stepImaginary
                    twiddleImaginary = twiddleReal * stepImaginary + twiddleImaginary * stepReal
                    twiddleReal = nextTwiddleReal
                }
                blockStart += length
            }
            length <<= 1
        }
    }
}

private extension Int {
    var isPowerOfTwo: Bool {
        self > 0 && (self & (self - 1)) == 0
    }
}

private final class LockedClock: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Date

    init(_ value: Date) {
        self.value = value
    }

    func now() -> Date {
        lock.lock()
        defer { lock.unlock() }
        return value
    }

    func advance(_ interval: TimeInterval) {
        lock.lock()
        value = value.addingTimeInterval(interval)
        lock.unlock()
    }
}

private actor FetchRecorder {
    private(set) var etags: [String?] = []
    private var responses: [Result<GitHubReleaseFetchResult, Error>]

    init(_ responses: [Result<GitHubReleaseFetchResult, Error>]) {
        self.responses = responses
    }

    func fetch(etag: String?) throws -> GitHubReleaseFetchResult {
        etags.append(etag)
        guard !responses.isEmpty else {
            throw GitHubReleaseClientError.invalidResponse
        }
        return try responses.removeFirst().get()
    }

    var callCount: Int { etags.count }
}

private struct PackSnapshot: Equatable {
    let manifestData: Data
    let assetHashes: [String: String]
}

@main
private struct DIYCoreHarness {
    private static let bcpFixtureTitle = "【打字声音】Suit80｜BCP轴｜GMK Ursa 大熊 - Original.mp4"
    private static let bcpPackUUID = UUID(uuidString: "15d04652-5265-4ea7-a376-8a7e11ff6813")!
    private static let bcpSelectionID = "custom:15d04652-5265-4ea7-a376-8a7e11ff6813"
    private static let bcpBundledSelectionID = "bundled-pack:15d04652-5265-4ea7-a376-8a7e11ff6813"

    static func main() async {
        var results = HarnessResults()
        do {
            try testSemanticVersion(&results)
            try testKeyboardVisualLayout(&results)
            try testPointerEventMapping(&results)
            try testPerceptualKeyboardVolume(&results)
            try testPointerSettingsAndResources(&results)
            try testKeyRepeatSound(&results)
            try await testLocalBCPSoundPackInstaller(&results)
            try testLaunchAtLoginInstallPaths(&results)
            try testValidatorAndResolver(&results)
            try await testAudioLibraryAndArchive(&results)
            try await testAudioSplit(&results)
            try testEngineLoadFailureContract(&results)
            try testNormalKeyboardVolumeMessagingContract(&results)
            try await testUpdateCachingAndThrottling(&results)
            print("DIY core harness passed: \(results.passed) assertions")
        } catch {
            fputs("DIY core harness FAILED: \(error)\n", stderr)
            exit(1)
        }
    }

    private static func testPerceptualKeyboardVolume(
        _ results: inout HarnessResults
    ) throws {
        let fixtures: [(position: Double, expectedGain: Double)] = [
            (0, 0),
            (0.25, 0.015_625),
            (0.5, 0.125),
            (0.75, 0.421_875),
            (1, 1),
        ]

        for fixture in fixtures {
            try results.check(
                abs(
                    KeyboardVolumeCurve.playbackGain(for: fixture.position)
                        - fixture.expectedGain
                ) < 0.000_001,
                "keyboard slider position \(fixture.position) should use cubic gain"
            )
        }

        try results.check(
            KeyboardVolumeCurve.playbackGain(for: -1) == 0
                && KeyboardVolumeCurve.playbackGain(for: 2) == 1,
            "keyboard gain should clamp positions to the slider range"
        )

        for legacyGain in [0.0, 0.01, 0.42, 0.8, 1.0] {
            let migratedPosition = KeyboardVolumeCurve.sliderPosition(
                preservingLegacyGain: legacyGain
            )
            try results.check(
                abs(KeyboardVolumeCurve.playbackGain(for: migratedPosition) - legacyGain)
                    < 0.000_001,
                "legacy gain \(legacyGain) should survive perceptual-curve migration"
            )
        }
    }

    private static func testSemanticVersion(_ results: inout HarnessResults) throws {
        let ordered = [
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0",
        ].compactMap(SemanticVersion.init)
        try results.check(ordered.count == 8, "all SemVer precedence fixtures should parse")
        try results.check(zip(ordered, ordered.dropFirst()).allSatisfy(<), "SemVer precedence must match 2.0.0")
        try results.check(SemanticVersion("v0.4.0")?.description == "0.4.0", "release tag prefix should parse")
        try results.check(
            SemanticVersion("1.2.3+build.1") == SemanticVersion("1.2.3+build.2"),
            "build metadata must not affect precedence equality"
        )

        for invalid in ["1.2", "01.2.3", "1.02.3", "1.2.03", "1.2.3-01", "1.2.3-", "1.2.3+", "1.2.3+bad_idea"] {
            try results.check(SemanticVersion(invalid) == nil, "invalid SemVer accepted: \(invalid)")
        }
    }

    private static func testKeyboardVisualLayout(_ results: inout HarnessResults) throws {
        let keyboard = KeyboardLayoutCatalog.ansiTKL
        let visual = KeyboardVisualLayoutCatalog.magicKeyboardANSI
        let tolerance = 0.000_1

        try results.check(
            keyboard.id == "mac-ansi-tkl-v1",
            "the persisted sound-pack layout identifier must remain compatible"
        )
        try results.check(visual.widthUnits == 14.5, "compact Magic Keyboard must span 14.5U")
        try results.check(visual.rowCount == 6, "compact Magic Keyboard must contain six rows")
        try results.check(visual.placements.count == 78, "visual layout should contain 77 keys plus lock")
        try results.check(
            Set(visual.placements.map(\.id)).count == visual.placements.count,
            "visual placements must have stable unique identifiers"
        )

        let descriptorsByID = Dictionary(uniqueKeysWithValues: keyboard.keys.map { ($0.id, $0) })
        try results.check(
            visual.keyIDs.allSatisfy { descriptorsByID[$0] != nil },
            "every tracked visual key must resolve to the existing logical keyboard"
        )
        let unplacedIDs = Set(keyboard.keys.map(\.id)).subtracting(visual.keyIDs)
        try results.check(
            unplacedIDs == [KeyboardKeyID("rightControl")],
            "only the extended-keyboard right Control should sit outside the compact layout"
        )

        let extendedKeys = KeyboardExtendedLayoutCatalog.keys
        try results.check(
            extendedKeys.count == 41,
            "the DIY extended keyboard should expose navigation, F13-F20, keypad, international and media keys"
        )
        try results.check(
            Set(extendedKeys.map(\.id)).count == extendedKeys.count,
            "extended keyboard keys must have unique persistent identifiers"
        )
        try results.check(
            Set(extendedKeys.map(\.keyCode)).count == extendedKeys.count,
            "extended keyboard keys must have unique macOS virtual key codes"
        )
        try results.check(
            extendedKeys.allSatisfy { KeyboardLayoutCatalog.key(for: $0.keyCode)?.id == $0.id },
            "the runtime resolver must recognize every DIY extended key"
        )

        for row in 0..<visual.rowCount {
            let placements = visual.placements(inRow: row)
            let groupedByX = Dictionary(grouping: placements, by: \.xUnits)
            var cursor = 0.0
            for x in groupedByX.keys.sorted() {
                guard let column = groupedByX[x] else { continue }
                let widths = Set(column.map(\.widthUnits))
                try results.check(widths.count == 1, "stacked keys must share a column width")
                try results.check(abs(x - cursor) < tolerance, "row \(row) contains a gap or overlap")
                cursor = x + (widths.first ?? 0)
            }
            try results.check(
                abs(cursor - visual.widthUnits) < tolerance,
                "row \(row) must end at the common 14.5U edge"
            )
        }

        let up = visual.placements.first { $0.content.keyID == KeyboardKeyID("upArrow") }
        let down = visual.placements.first { $0.content.keyID == KeyboardKeyID("downArrow") }
        try results.check(up?.verticalSlot == .upperHalf, "up arrow must occupy the upper half")
        try results.check(down?.verticalSlot == .lowerHalf, "down arrow must occupy the lower half")
        try results.check(
            up?.row == down?.row && up?.xUnits == down?.xUnits && up?.widthUnits == down?.widthUnits,
            "up and down arrows must form one shared inverted-T column"
        )
        try results.check(
            visual.placements.contains {
                if case let .decoration(id, _, systemImage) = $0.content {
                    return id == "lock" && systemImage == "lock.fill"
                }
                return false
            },
            "top row must include the untracked Lock or Touch ID position"
        )
    }

    @MainActor
    private static func testPointerEventMapping(_ results: inout HarnessResults) throws {
        let observedTypes = KeyboardMonitor.observedEventTypes
        try results.check(observedTypes.count == 9, "input tap should observe the expected event types exactly once")
        try results.check(Set(observedTypes.map(\.rawValue)).count == observedTypes.count, "input event mask must not contain duplicates")
        for eventType in observedTypes {
            let bit = CGEventMask(1) << eventType.rawValue
            try results.check(
                KeyboardMonitor.observedEventMask & bit != 0,
                "input event mask should contain \(eventType.rawValue)"
            )
        }
        for ignoredType in [CGEventType.mouseMoved, .leftMouseDragged, .rightMouseDragged, .scrollWheel] {
            let bit = CGEventMask(1) << ignoredType.rawValue
            try results.check(
                KeyboardMonitor.observedEventMask & bit == 0,
                "movement, drag, and scroll events must remain outside the tap mask"
            )
        }

        let keyboardPressOnly = KeyboardMonitor.EventInterest(
            keyboardPresses: true,
            keyboardReleases: false,
            pointerPresses: false,
            pointerReleases: false
        )
        try results.check(
            keyboardPressOnly.eventTypes == [.keyDown, .flagsChanged],
            "press-only keyboard monitoring should omit key-up and every pointer event"
        )

        let pointerPressOnly = KeyboardMonitor.EventInterest(
            keyboardPresses: false,
            keyboardReleases: false,
            pointerPresses: true,
            pointerReleases: false
        )
        try results.check(
            pointerPressOnly.eventTypes == [.leftMouseDown, .rightMouseDown, .otherMouseDown],
            "press-only pointer monitoring should omit releases and keyboard events"
        )

        let disabled = KeyboardMonitor.EventInterest(
            keyboardPresses: false,
            keyboardReleases: false,
            pointerPresses: false,
            pointerReleases: false
        )
        try results.check(
            disabled.isEmpty && disabled.eventMask == 0,
            "disabled input features should produce an empty event tap mask"
        )

        guard let keyboardEvent = CGEvent(
            keyboardEventSource: nil,
            virtualKey: 12,
            keyDown: true
        ) else {
            throw HarnessFailure.assertion("could not create keyboard CGEvent fixture")
        }
        keyboardEvent.setIntegerValueField(.keyboardEventAutorepeat, value: 1)
        guard case let .keyboard(decodedKeyboard)? = KeyboardMonitor.decodedInputEvent(
            type: .keyDown,
            event: keyboardEvent
        ) else {
            throw HarnessFailure.assertion("key-down CGEvent should decode as keyboard input")
        }
        try results.check(
            decodedKeyboard == KeyboardEvent(kind: .keyDown, keyCode: 12, isRepeat: true),
            "keyboard decoding should retain key code and repeat state"
        )

        keyboardEvent.flags = [.maskCommand]
        guard case let .keyboard(decodedShortcut)? = KeyboardMonitor.decodedInputEvent(
            type: .keyDown,
            event: keyboardEvent
        ) else {
            throw HarnessFailure.assertion("shortcut key-down should decode as keyboard input")
        }
        try results.check(
            decodedShortcut == KeyboardEvent(
                kind: .keyDown,
                keyCode: 12,
                isRepeat: true,
                isShortcutModified: true
            ),
            "keyboard decoding should retain Command/Control shortcut state"
        )

        let pointerFixtures: [(CGEventType, CGMouseButton, Int64?, PointerEvent)] = [
            (.leftMouseDown, .left, nil, PointerEvent(phase: .press, button: .primary)),
            (.leftMouseUp, .left, nil, PointerEvent(phase: .release, button: .primary)),
            (.rightMouseDown, .right, nil, PointerEvent(phase: .press, button: .secondary)),
            (.rightMouseUp, .right, nil, PointerEvent(phase: .release, button: .secondary)),
            (.otherMouseDown, .center, nil, PointerEvent(phase: .press, button: .middle)),
            (.otherMouseUp, .center, nil, PointerEvent(phase: .release, button: .middle)),
            (.otherMouseDown, .center, 4, PointerEvent(phase: .press, button: .auxiliary(4))),
            (.otherMouseUp, .center, 5, PointerEvent(phase: .release, button: .auxiliary(5))),
        ]
        for (eventType, mouseButton, buttonNumber, expected) in pointerFixtures {
            guard let event = CGEvent(
                mouseEventSource: nil,
                mouseType: eventType,
                mouseCursorPosition: .zero,
                mouseButton: mouseButton
            ) else {
                throw HarnessFailure.assertion("could not create pointer CGEvent fixture")
            }
            if let buttonNumber {
                event.setIntegerValueField(.mouseEventButtonNumber, value: buttonNumber)
            }
            guard case let .pointer(decoded)? = KeyboardMonitor.decodedInputEvent(type: eventType, event: event) else {
                throw HarnessFailure.assertion("pointer CGEvent should decode as pointer input")
            }
            try results.check(decoded == expected, "pointer phase/button mapping should match the CGEvent type")
        }

        try results.check(PointerButton(mouseButtonNumber: 0) == .primary, "button 0 should be primary")
        try results.check(PointerButton(mouseButtonNumber: 1) == .secondary, "button 1 should be secondary")
        try results.check(PointerButton(mouseButtonNumber: 2) == .middle, "button 2 should be middle")
        try results.check(PointerButton(mouseButtonNumber: 8) == .auxiliary(8), "higher button numbers should remain identifiable")
        try results.check(PointerButton.secondary.sample == .secondary, "secondary button should request its semantic sample")
        try results.check(PointerButton.middle.sample == .middle, "middle button should request its semantic sample")

        guard let ignoredEvent = CGEvent(
            mouseEventSource: nil,
            mouseType: .mouseMoved,
            mouseCursorPosition: .zero,
            mouseButton: .left
        ) else {
            throw HarnessFailure.assertion("could not create ignored CGEvent fixture")
        }
        try results.check(
            KeyboardMonitor.decodedInputEvent(type: .mouseMoved, event: ignoredEvent) == nil,
            "unobserved mouse movement should not decode into input audio"
        )
    }

    @MainActor
    private static func testKeyRepeatSound(_ results: inout HarnessResults) throws {
        let suiteName = "SimuBoard.DIYCoreHarness.KeyRepeat.\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suiteName) else {
            throw HarnessFailure.assertion("could not create key-repeat UserDefaults")
        }
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let settings = AppSettings(defaults: defaults)
        let press = KeyboardEvent(kind: .keyDown, keyCode: 51, isRepeat: false)
        let repeated = KeyboardEvent(kind: .keyDown, keyCode: 51, isRepeat: true)
        let release = KeyboardEvent(kind: .keyUp, keyCode: 51, isRepeat: false)
        try results.check(!settings.playsKeyRepeatSound, "key-repeat sound must remain opt-in")
        try results.check(settings.shouldPlaySound(for: press), "physical presses should play")
        try results.check(!settings.shouldPlaySound(for: repeated), "legacy settings must suppress repeats")
        settings.playsKeyRepeatSound = true
        try results.check(AppSettings(defaults: defaults).playsKeyRepeatSound, "repeat opt-in must persist")
        try results.check(settings.shouldPlaySound(for: repeated), "enabled repeats should play press sounds")
        settings.playsReleaseSound = false
        try results.check(settings.shouldPlaySound(for: repeated), "repeat sounds must not depend on release sounds")
        try results.check(!settings.shouldPlaySound(for: release), "repeats must not bypass the release preference")
        settings.playsReleaseSound = true
        try results.check(settings.shouldPlaySound(for: release), "a physical release should still play once")
        settings.isEnabled = false
        for event in [press, repeated, release] {
            try results.check(!settings.shouldPlaySound(for: event), "pause must suppress every keyboard sound")
        }
        settings.playsKeyRepeatSound = false
        try results.check(!AppSettings(defaults: defaults).playsKeyRepeatSound, "disabling repeats must persist")
    }

    @MainActor
    private static func testPointerSettingsAndResources(
        _ results: inout HarnessResults
    ) throws {
        let suiteName = "SimuBoard.DIYCoreHarness.PointerSettings.\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suiteName) else {
            throw HarnessFailure.assertion("could not create pointer-settings UserDefaults")
        }
        defer { defaults.removePersistentDomain(forName: suiteName) }

        let initial = AppSettings(defaults: defaults)
        try results.check(!initial.isPointerSoundEnabled, "pointer sounds should be opt-in")
        try results.check(!initial.isTypingStatsEnabled, "persistent input statistics should be opt-in")
        try results.check(initial.isLaunchAtLoginEnabled, "launch at login should default to enabled")
        try results.check(
            initial.languagePreference == .system,
            "a new install should follow the system language"
        )
        try results.check(
            initial.appearancePreference == .system,
            "a new install should follow the system appearance"
        )
        try results.check(initial.selectedPointerProfile == .classic, "classic should be the default pointer profile")
        try results.check(initial.playsPointerReleaseSound, "pointer release sound should default to enabled")
        try results.check(
            abs(initial.keyboardPlaybackGain - 0.42) < 0.000_001,
            "a new install should preserve the legacy default audible keyboard gain"
        )
        try results.check(
            abs(
                initial.volume
                    - KeyboardVolumeCurve.sliderPosition(preservingLegacyGain: 0.42)
            ) < 0.000_001,
            "a new install should expose the legacy default through the perceptual slider"
        )
        try results.check(
            defaults.integer(forKey: "keyboardVolumeCurveVersion")
                == KeyboardVolumeCurve.currentVersion,
            "a new install should persist the current keyboard volume representation"
        )
        defaults.removeObject(forKey: "keyboardVolumeCurveVersion")
        initial.volume = 0.6
        let versionRepaired = AppSettings(defaults: defaults)
        try results.check(
            defaults.integer(forKey: "keyboardVolumeCurveVersion")
                == KeyboardVolumeCurve.currentVersion
                && abs(versionRepaired.volume - 0.6) < 0.000_001
                && abs(versionRepaired.keyboardPlaybackGain - 0.216) < 0.000_001,
            "changing keyboard volume should preserve the curve-version invariant"
        )
        try results.check(
            abs(initial.pointerVolume - (0.42 * 0.65)) < 0.000_001,
            "a new pointer volume should start at 65% of the keyboard volume"
        )
        try results.check(
            abs(defaults.double(forKey: "pointerVolume") - initial.pointerVolume) < 0.000_001,
            "the migrated pointer volume should be persisted immediately"
        )

        initial.isPointerSoundEnabled = true
        initial.selectedPointerProfile = .glass
        initial.playsPointerReleaseSound = false
        initial.volume = 0.78
        initial.pointerVolume = 0.24
        initial.isTypingStatsEnabled = true
        initial.isLaunchAtLoginEnabled = false
        initial.languagePreference = .english
        initial.appearancePreference = .light
        let reloaded = AppSettings(defaults: defaults)
        try results.check(reloaded.isPointerSoundEnabled, "pointer enabled state should persist")
        try results.check(reloaded.selectedPointerProfile == .glass, "pointer profile should persist")
        try results.check(!reloaded.playsPointerReleaseSound, "pointer release preference should persist")
        try results.check(abs(reloaded.volume - 0.78) < 0.000_001, "keyboard volume should persist independently")
        try results.check(abs(reloaded.pointerVolume - 0.24) < 0.000_001, "pointer volume should persist independently")
        try results.check(reloaded.isTypingStatsEnabled, "typing statistics opt-in should persist")
        try results.check(!reloaded.isLaunchAtLoginEnabled, "launch-at-login preference should persist")
        try results.check(
            reloaded.languagePreference == .english,
            "the in-app language override should persist"
        )
        try results.check(
            reloaded.appearancePreference == .light,
            "the in-app appearance override should persist"
        )

        defaults.set("missing-future-profile", forKey: "selectedPointerProfile")
        defaults.set("missing-language", forKey: "languagePreference")
        defaults.set("missing-appearance", forKey: "appearancePreference")
        let repaired = AppSettings(defaults: defaults)
        try results.check(
            repaired.selectedPointerProfileID == PointerSoundProfile.classic.rawValue,
            "an invalid persisted pointer profile should normalize to classic"
        )
        try results.check(
            defaults.string(forKey: "selectedPointerProfile") == PointerSoundProfile.classic.rawValue,
            "pointer profile normalization should repair persisted storage"
        )
        try results.check(
            repaired.languagePreference == .system
                && defaults.string(forKey: "languagePreference") == AppLanguagePreference.system.rawValue,
            "an invalid language preference should repair to follow-system"
        )
        try results.check(
            repaired.appearancePreference == .system
                && defaults.string(forKey: "appearancePreference") == AppAppearancePreference.system.rawValue,
            "an invalid appearance preference should repair to follow-system"
        )

        let migrationSuiteName = "SimuBoard.DIYCoreHarness.PointerVolumeMigration.\(UUID().uuidString)"
        guard let migrationDefaults = UserDefaults(suiteName: migrationSuiteName) else {
            throw HarnessFailure.assertion("could not create pointer-volume migration UserDefaults")
        }
        defer { migrationDefaults.removePersistentDomain(forName: migrationSuiteName) }
        migrationDefaults.set(0.8, forKey: "volume")
        let migrated = AppSettings(defaults: migrationDefaults)
        let migratedPosition = KeyboardVolumeCurve.sliderPosition(preservingLegacyGain: 0.8)
        try results.check(
            abs(migrated.volume - migratedPosition) < 0.000_001
                && abs(migrated.keyboardPlaybackGain - 0.8) < 0.000_001,
            "a legacy keyboard volume should migrate to the cubic slider without changing gain"
        )
        try results.check(
            abs(migrationDefaults.double(forKey: "volume") - migratedPosition) < 0.000_001
                && migrationDefaults.integer(forKey: "keyboardVolumeCurveVersion")
                    == KeyboardVolumeCurve.currentVersion,
            "keyboard volume migration should persist its position and version"
        )
        try results.check(
            abs(migrated.pointerVolume - 0.52) < 0.000_001,
            "an existing keyboard volume should seed pointer volume at 65% exactly once"
        )
        migrated.volume = 0.4
        let migratedReloaded = AppSettings(defaults: migrationDefaults)
        try results.check(
            abs(migratedReloaded.volume - 0.4) < 0.000_001
                && abs(migratedReloaded.keyboardPlaybackGain - 0.064) < 0.000_001
                && abs(migratedReloaded.pointerVolume - 0.52) < 0.000_001,
            "the curve must migrate once while pointer volume remains independent"
        )

        let projectRoot = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        let pointerRoot = projectRoot.appendingPathComponent(
            "shared/audio/builtin/pointer",
            isDirectory: true
        )
        let spectralLimits: [PointerSoundProfile: (centroidHz: Double, energyAbove8KHz: Double)] = [
            .heavy: (4_250, 0.005),
            .silent: (4_800, 0.010),
            .classic: (6_200, 0.090),
            .crisp: (6_750, 0.130),
            .glass: (7_200, 0.200),
        ]
        var expectedPaths = Set<String>()
        var spectra: [PointerSoundProfile: [PointerSpectrumMetrics]] = [:]
        for profile in PointerSoundProfile.allCases {
            for phase in PointerSoundPhase.allCases {
                let relativePath = "\(profile.rawValue)/\(phase.rawValue)/PRIMARY.wav"
                expectedPaths.insert(relativePath)
                let sampleURL = pointerRoot.appendingPathComponent(relativePath)
                let info = try AudioImportService.validateNormalizedAudio(at: sampleURL)
                try results.check(
                    (0.02...0.15).contains(info.durationSeconds),
                    "pointer sample duration should remain click-like: \(relativePath)"
                )

                let wave = try PCM16MonoWave(contentsOf: sampleURL)
                try results.check(
                    wave.sampleRate == 48_000 && wave.channels == 1 && wave.bitsPerSample == 16,
                    "pointer WAV must remain 48 kHz mono signed 16-bit PCM: \(relativePath)"
                )
                let spectrum = PointerSpectrumMetrics(wave: wave)
                try results.check(
                    spectrum.peakAmplitude > 0.001 && spectrum.rmsAmplitude > 0.000_1,
                    "pointer WAV must not become silent: \(relativePath)"
                )
                try results.check(
                    spectrum.peakAmplitude < 0.95,
                    "pointer WAV peak must retain clipping headroom: \(relativePath)"
                )
                try results.check(
                    wave.samples.last == 0,
                    "pointer WAV must end at an exact zero crossing: \(relativePath)"
                )
                try results.check(
                    spectrum.tailRMSDBFS < -60,
                    "pointer WAV final 1 ms must stay below -60 dBFS: \(relativePath)"
                )
                try results.check(
                    spectrum.centroidHz.isFinite && spectrum.centroidHz > 0
                        && spectrum.energyAbove8KHz.isFinite
                        && (0...1).contains(spectrum.energyAbove8KHz),
                    "pointer WAV spectrum must be finite and non-empty: \(relativePath)"
                )
                spectra[profile, default: []].append(spectrum)
            }
        }

        let bundledPaths = try FileManager.default.subpathsOfDirectory(atPath: pointerRoot.path)
            .filter { $0.hasSuffix(".wav") }
        try results.check(
            Set(bundledPaths) == expectedPaths,
            "pointer resource tree should contain exactly one press/release pair per profile"
        )

        var centroidByProfile: [PointerSoundProfile: Double] = [:]
        var highFrequencyEnergyByProfile: [PointerSoundProfile: Double] = [:]
        for profile in PointerSoundProfile.allCases {
            guard let profileSpectra = spectra[profile], profileSpectra.count == PointerSoundPhase.allCases.count,
                  let limits = spectralLimits[profile] else {
                throw HarnessFailure.assertion("missing pointer spectrum fixtures for \(profile.rawValue)")
            }
            let meanCentroid = profileSpectra.map(\.centroidHz).reduce(0, +) / Double(profileSpectra.count)
            let meanHighFrequencyEnergy = profileSpectra.map(\.energyAbove8KHz).reduce(0, +)
                / Double(profileSpectra.count)
            centroidByProfile[profile] = meanCentroid
            highFrequencyEnergyByProfile[profile] = meanHighFrequencyEnergy
            try results.check(
                meanCentroid < limits.centroidHz,
                "\(profile.rawValue) mean spectral centroid is too sharp: "
                    + String(format: "%.0f Hz (limit %.0f Hz)", meanCentroid, limits.centroidHz)
            )
            try results.check(
                meanHighFrequencyEnergy < limits.energyAbove8KHz,
                "\(profile.rawValue) mean energy above 8 kHz is too high: "
                    + String(
                        format: "%.2f%% (limit %.2f%%)",
                        meanHighFrequencyEnergy * 100,
                        limits.energyAbove8KHz * 100
                    )
            )
        }

        let toneOrder: [PointerSoundProfile] = [.heavy, .silent, .classic, .crisp, .glass]
        let orderedCentroids = toneOrder.compactMap { centroidByProfile[$0] }
        let orderedHighFrequencyEnergy = toneOrder.compactMap { highFrequencyEnergyByProfile[$0] }
        try results.check(
            zip(orderedCentroids, orderedCentroids.dropFirst()).allSatisfy(<),
            "pointer profiles should retain their low-to-bright spectral-centroid order"
        )
        try results.check(
            zip(orderedHighFrequencyEnergy, orderedHighFrequencyEnergy.dropFirst()).allSatisfy(<),
            "pointer profiles should retain their low-to-bright high-frequency energy order"
        )
        try results.check(
            orderedCentroids.sorted()[orderedCentroids.count / 2] < 6_000,
            "the median pointer profile must remain comfortably below a 6 kHz spectral centroid"
        )
    }

    private static func testLocalBCPSoundPackInstaller(
        _ results: inout HarnessResults
    ) async throws {
        let fileManager = FileManager.default
        let projectRoot = URL(fileURLWithPath: fileManager.currentDirectoryPath)
        let scriptURL = projectRoot.appendingPathComponent(
            "SimuBoardMac/scripts/install-local-bcp-sound-pack.sh"
        )
        try results.check(
            fileManager.isExecutableFile(atPath: scriptURL.path),
            "local BCP installer script must exist and be executable"
        )

        let bundledSourcePackURL = projectRoot.appendingPathComponent(
            "shared/soundpacks/bundled/"
                + "15d04652-5265-4ea7-a376-8a7e11ff6813.simuboardpack",
            isDirectory: true
        )
        let bundledSourceManifest = try SoundPackPackageValidator.validatePackage(
            at: bundledSourcePackURL
        )
        try results.check(
            bundledSourceManifest.id == bcpPackUUID
                && bundledSourceManifest.assets.count == 28,
            "source tree must contain the complete authorized BCP package"
        )
        try results.check(
            fileManager.fileExists(
                atPath: bundledSourcePackURL.appendingPathComponent(
                    "licenses/BCP-Suit80-PERMISSION.txt"
                ).path
            ),
            "bundled BCP package must carry its permission notice"
        )

        let root = fileManager.temporaryDirectory.appendingPathComponent(
            "SimuBoard-LocalBCPInstaller-\(UUID().uuidString)",
            isDirectory: true
        )
        let assetRoot = root.appendingPathComponent("assets", isDirectory: true)
        try fileManager.createDirectory(at: assetRoot, withIntermediateDirectories: true)
        defer { try? fileManager.removeItem(at: root) }

        try makeBCPInstallerFixtureAssets(at: assetRoot, variantOffset: 0)

        let homeRoot = root.appendingPathComponent("home", isDirectory: true)
        try fileManager.createDirectory(
            at: homeRoot.appendingPathComponent("Library/Preferences", isDirectory: true),
            withIntermediateDirectories: true
        )
        let defaultLibraryRoot = defaultBCPLibraryRoot(forHome: homeRoot)
        let migrationDomain = "com.simuboard.bcp-installer.migration.\(UUID().uuidString)"
        try writeSelectedProfile(
            "bcp",
            domain: migrationDomain,
            homeDirectory: homeRoot
        )
        let firstRun = try runProcess(
            executableURL: scriptURL,
            arguments: [assetRoot.path],
            currentDirectoryURL: projectRoot.appendingPathComponent("SimuBoardMac", isDirectory: true),
            environmentOverrides: [
                "HOME": homeRoot.path,
                "SIMUBOARD_DEFAULTS_DOMAIN": migrationDomain,
                "SIMUBOARD_INSTALLER_NOW": "2026-08-24T08:00:00Z",
            ]
        )
        try results.check(
            firstRun.terminationStatus == 0,
            "local BCP installer must succeed: \(firstRun.stderr)"
        )
        let migratedSelectedProfile = try readSelectedProfile(
            domain: migrationDomain,
            homeDirectory: homeRoot
        )
        try results.check(
            migratedSelectedProfile == bcpSelectionID,
            "installing into the default local library root must migrate the legacy bundled BCP selection"
        )

        let explicitDefaultDomain = "com.simuboard.bcp-installer.explicit-default.\(UUID().uuidString)"
        let explicitDefaultHomeRoot = root.appendingPathComponent(
            "explicit-default-home",
            isDirectory: true
        )
        try fileManager.createDirectory(
            at: explicitDefaultHomeRoot.appendingPathComponent(
                "Library/Preferences",
                isDirectory: true
            ),
            withIntermediateDirectories: true
        )
        let explicitDefaultLibraryRoot = defaultBCPLibraryRoot(forHome: explicitDefaultHomeRoot)
        try writeSelectedProfile(
            "bcp",
            domain: explicitDefaultDomain,
            homeDirectory: explicitDefaultHomeRoot
        )
        let explicitDefaultRun = try runProcess(
            executableURL: scriptURL,
            arguments: [assetRoot.path, explicitDefaultLibraryRoot.path],
            currentDirectoryURL: projectRoot.appendingPathComponent("SimuBoardMac", isDirectory: true),
            environmentOverrides: [
                "HOME": explicitDefaultHomeRoot.path,
                "SIMUBOARD_DEFAULTS_DOMAIN": explicitDefaultDomain,
                "SIMUBOARD_INSTALLER_NOW": "2026-08-24T08:00:00Z",
            ]
        )
        try results.check(
            explicitDefaultRun.terminationStatus == 0,
            "explicit default library-root install must still succeed: \(explicitDefaultRun.stderr)"
        )
        let explicitDefaultSelectedProfile = try readSelectedProfile(
            domain: explicitDefaultDomain,
            homeDirectory: explicitDefaultHomeRoot
        )
        try results.check(
            explicitDefaultSelectedProfile == bcpSelectionID,
            "explicit default library-root installs must still migrate the legacy bundled BCP selection"
        )

        let installedPackURLs = try fileManager.contentsOfDirectory(
            at: defaultLibraryRoot,
            includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles]
        ).filter { $0.pathExtension.lowercased() == "simuboardpack" }
        try results.check(
            installedPackURLs.count == 1,
            "installer must publish exactly one BCP custom pack"
        )
        guard let installedPackURL = installedPackURLs.first else { return }

        let manifest = try SoundPackPackageValidator.validatePackage(at: installedPackURL)
        try results.check(
            installedPackURL.lastPathComponent == "\(manifest.id.uuidString.lowercased()).simuboardpack",
            "installer directory name must match manifest UUID"
        )
        try results.check(manifest.name == "BCP (Suit80)", "installed pack name must match the picker label")
        try results.check(manifest.family == "线性", "installed pack family must remain linear")
        try results.check(manifest.tone == "厚实、木感", "installed pack tone must remain thick and woody")
        try results.check(
            manifest.layoutID == KeyboardLayoutCatalog.defaultLayoutID,
            "installed pack must target the persisted ANSI TKL layout"
        )
        try results.check(
            manifest.baseProfileID == SwitchProfile.holyPanda.rawValue,
            "installed pack must fall back to Holy Panda"
        )
        try results.check(manifest.press.generic == nil, "press mapping must not use a generic fallback asset")
        try results.check(manifest.release.generic == nil, "release mapping must not use a generic fallback asset")
        try results.check(
            manifest.press.rows.keys.sorted() == KeyboardRowID.allCases.map(\.rawValue).sorted(),
            "press mapping must cover every keyboard row"
        )
        try results.check(
            manifest.release.rows.keys.sorted() == KeyboardRowID.allCases.map(\.rawValue).sorted(),
            "release mapping must cover every keyboard row"
        )
        try results.check(
            manifest.press.specials.keys.sorted() == KeyboardSpecialKeyID.allCases.map(\.rawValue).sorted(),
            "press mapping must cover every supported special key"
        )
        try results.check(
            manifest.release.specials.keys.sorted() == KeyboardSpecialKeyID.allCases.map(\.rawValue).sorted(),
            "release mapping must cover every supported special key"
        )
        let expectedOverrideKeys = Set([
            "digit2", "digit4", "digit6", "digit8", "digit0", "equal",
            "w", "r", "y", "i", "p", "rightBracket",
            "s", "f", "h", "k", "semicolon",
            "x", "v", "n", "comma", "slash",
            "f2", "f4", "f6", "f8", "f10", "f12", "upArrow", "rightArrow",
            "leftShift", "rightShift",
        ])
        try results.check(
            Set(manifest.press.keyOverrides.keys) == expectedOverrideKeys
                && Set(manifest.release.keyOverrides.keys) == expectedOverrideKeys,
            "installer must map all alternate row samples and both Shift keys"
        )
        try results.check(
            manifest.press.override(for: KeyboardKeyID("leftShift"))
                == manifest.press.override(for: KeyboardKeyID("rightShift"))
                && manifest.release.override(for: KeyboardKeyID("leftShift"))
                    == manifest.release.override(for: KeyboardKeyID("rightShift")),
            "left and right Shift must share the dedicated Shift sample in each phase"
        )
        try results.check(manifest.assets.count == 28, "installer must preserve all 28 rendered assets")
        try results.check(manifest.attributions.count == 1, "installer must emit one attribution entry")
        if let attribution = manifest.attributions.first {
            try results.check(
                attribution.title == bcpFixtureTitle,
                "attribution title must preserve the recording filename"
            )
            try results.check(
                attribution.author == "J_Eason001",
                "attribution author must preserve the visible uploader"
            )
            try results.check(attribution.sourceURL == nil, "attribution must not invent a source URL")
            try results.check(
                attribution.licenseName == "Used with permission",
                "attribution must record the redistribution permission status"
            )
            try results.check(
                attribution.notice
                    == "Redistribution authorized; the permission record is retained by the Battuta maintainer.",
                "attribution notice must preserve the authorized shipping status"
            )
        }

        let firstPressR0Asset = manifest.press.asset(for: .r0)
        let firstReleaseSpaceAsset = manifest.release.asset(for: .space)
        try results.check(firstPressR0Asset != nil, "press R0 assignment must exist")
        try results.check(firstReleaseSpaceAsset != nil, "release Space assignment must exist")

        let library = SoundPackLibrary(rootURL: defaultLibraryRoot, builtInDescriptors: [])
        let descriptors = try await library.descriptors()
        try results.check(descriptors.count == 1, "installed pack should appear in the custom library")
        try results.check(
            descriptors.first?.customPackID == manifest.id,
            "installed pack descriptor must point to the installed UUID"
        )

        let bundledRoot = root.appendingPathComponent("BundledSoundPacks", isDirectory: true)
        try fileManager.createDirectory(at: bundledRoot, withIntermediateDirectories: true)
        let bundledPackURL = bundledRoot.appendingPathComponent(
            installedPackURL.lastPathComponent,
            isDirectory: true
        )
        try fileManager.copyItem(at: installedPackURL, to: bundledPackURL)
        let bundledLibrary = SoundPackLibrary(
            rootURL: defaultLibraryRoot,
            builtInDescriptors: [],
            bundledPackRootURL: bundledRoot
        )
        let bundledDescriptors = try await bundledLibrary.descriptors()
        try results.check(
            bundledDescriptors.count == 1,
            "a bundled pack must replace, not duplicate, the same local pack UUID"
        )
        guard let bundledDescriptor = bundledDescriptors.first else { return }
        try results.check(
            bundledDescriptor.id == bcpBundledSelectionID,
            "bundled package must have a stable read-only selection ID"
        )
        try results.check(
            bundledDescriptor.bundledPackID == bcpPackUUID
                && bundledDescriptor.customPackID == nil
                && bundledDescriptor.isReadOnly,
            "bundled package descriptor must remain distinct from editable DIY packs"
        )
        let bundledDocument = try await bundledLibrary.loadPack(for: bundledDescriptor)
        try results.check(
            bundledDocument.manifest.assets.count == 28,
            "bundled BCP package must load all 28 authorized assets"
        )
        let bundledPressR0 = try bundledDocument.manifest.press.asset(for: .r0)
            .map { try bundledDocument.assetURL(for: $0) }
        try results.check(
            bundledPressR0.map { fileManager.fileExists(atPath: $0.path) } == true,
            "bundled BCP package must resolve audio inside the app resource tree"
        )

        let explicitDomain = "com.simuboard.bcp-installer.explicit.\(UUID().uuidString)"
        let explicitHomeRoot = root.appendingPathComponent("explicit-home", isDirectory: true)
        try fileManager.createDirectory(
            at: explicitHomeRoot.appendingPathComponent("Library/Preferences", isDirectory: true),
            withIntermediateDirectories: true
        )
        let explicitLibraryRoot = root.appendingPathComponent("explicit-library", isDirectory: true)
        try writeSelectedProfile(
            "bcp",
            domain: explicitDomain,
            homeDirectory: explicitHomeRoot
        )
        let explicitRun = try runProcess(
            executableURL: scriptURL,
            arguments: [assetRoot.path, explicitLibraryRoot.path],
            currentDirectoryURL: projectRoot.appendingPathComponent("SimuBoardMac", isDirectory: true),
            environmentOverrides: [
                "HOME": explicitHomeRoot.path,
                "SIMUBOARD_DEFAULTS_DOMAIN": explicitDomain,
                "SIMUBOARD_INSTALLER_NOW": "2026-08-24T08:00:00Z",
            ]
        )
        try results.check(
            explicitRun.terminationStatus == 0,
            "explicit library-root install must still succeed: \(explicitRun.stderr)"
        )
        let explicitSelectedProfile = try readSelectedProfile(
            domain: explicitDomain,
            homeDirectory: explicitHomeRoot
        )
        try results.check(
            explicitSelectedProfile == "bcp",
            "explicit library-root installs must not rewrite the user's selected profile"
        )

        let timestampLibraryRoot = root.appendingPathComponent("timestamp-library", isDirectory: true)
        let timestampInstall1 = try runProcess(
            executableURL: scriptURL,
            arguments: [assetRoot.path, timestampLibraryRoot.path],
            currentDirectoryURL: projectRoot.appendingPathComponent("SimuBoardMac", isDirectory: true),
            environmentOverrides: [
                "SIMUBOARD_INSTALLER_NOW": "2026-08-24T08:00:00Z",
            ]
        )
        try results.check(
            timestampInstall1.terminationStatus == 0,
            "first timestamp fixture install must succeed: \(timestampInstall1.stderr)"
        )
        let timestampPackURL = bcpPackURL(in: timestampLibraryRoot)
        let timestampManifest1 = try SoundPackPackageValidator.validatePackage(at: timestampPackURL)
        let timestampInstall2 = try runProcess(
            executableURL: scriptURL,
            arguments: [assetRoot.path, timestampLibraryRoot.path],
            currentDirectoryURL: projectRoot.appendingPathComponent("SimuBoardMac", isDirectory: true),
            environmentOverrides: [
                "SIMUBOARD_INSTALLER_NOW": "2026-08-24T09:00:00Z",
            ]
        )
        try results.check(
            timestampInstall2.terminationStatus == 0,
            "same-input reinstall must succeed: \(timestampInstall2.stderr)"
        )
        let timestampManifest2 = try SoundPackPackageValidator.validatePackage(at: timestampPackURL)
        try results.check(
            timestampManifest2.createdAt == timestampManifest1.createdAt,
            "same-input reinstall must preserve createdAt"
        )
        try results.check(
            timestampManifest2.modifiedAt == timestampManifest1.modifiedAt,
            "same-input reinstall must preserve modifiedAt for byte-stable manifests"
        )

        try makeBCPInstallerFixtureAssets(at: assetRoot, variantOffset: 1)
        let timestampInstall3 = try runProcess(
            executableURL: scriptURL,
            arguments: [assetRoot.path, timestampLibraryRoot.path],
            currentDirectoryURL: projectRoot.appendingPathComponent("SimuBoardMac", isDirectory: true),
            environmentOverrides: [
                "SIMUBOARD_INSTALLER_NOW": "2026-08-24T10:00:00Z",
            ]
        )
        try results.check(
            timestampInstall3.terminationStatus == 0,
            "changed-input reinstall must succeed: \(timestampInstall3.stderr)"
        )
        let timestampManifest3 = try SoundPackPackageValidator.validatePackage(at: timestampPackURL)
        try results.check(
            timestampManifest3.createdAt == timestampManifest1.createdAt,
            "changed-input reinstall must preserve the original createdAt"
        )
        try results.check(
            timestampManifest3.modifiedAt == iso8601Date("2026-08-24T10:00:00Z"),
            "changed-input reinstall must update modifiedAt to the new install time"
        )

        try makeBCPInstallerFixtureAssets(at: assetRoot, variantOffset: 0)

        let corruptTimestampLibraryRoot = root.appendingPathComponent(
            "corrupt-timestamp-library",
            isDirectory: true
        )
        let corruptTimestampPackURL = try installBCPFixturePack(
            scriptURL: scriptURL,
            assetRoot: assetRoot,
            libraryRoot: corruptTimestampLibraryRoot,
            projectRoot: projectRoot,
            installTime: "2026-08-24T10:30:00Z"
        )
        try rewriteManifestJSONObject(at: corruptTimestampPackURL) { manifest in
            manifest["createdAt"] = "2026-08-24T10:30:00+08:00"
        }
        let corruptTimestampSnapshot = try snapshot(packAt: corruptTimestampPackURL)
        let corruptTimestampRun = try runProcess(
            executableURL: scriptURL,
            arguments: [assetRoot.path, corruptTimestampLibraryRoot.path],
            currentDirectoryURL: projectRoot.appendingPathComponent("SimuBoardMac", isDirectory: true),
            environmentOverrides: [
                "SIMUBOARD_INSTALLER_NOW": "2026-08-24T10:31:00Z",
            ]
        )
        try results.check(
            corruptTimestampRun.terminationStatus != 0,
            "installer must reject an existing fixed BCP pack with non-UTC timestamps"
        )
        let corruptTimestampSnapshotAfterRun = try snapshot(packAt: corruptTimestampPackURL)
        try results.check(
            corruptTimestampSnapshotAfterRun == corruptTimestampSnapshot,
            "rejecting corrupt timestamps must leave the old fixed BCP pack untouched"
        )

        let corruptAssetLibraryRoot = root.appendingPathComponent(
            "corrupt-asset-library",
            isDirectory: true
        )
        let corruptAssetPackURL = try installBCPFixturePack(
            scriptURL: scriptURL,
            assetRoot: assetRoot,
            libraryRoot: corruptAssetLibraryRoot,
            projectRoot: projectRoot,
            installTime: "2026-08-24T10:40:00Z"
        )
        try corruptOneBCPAsset(at: corruptAssetPackURL)
        let corruptAssetSnapshot = try snapshot(packAt: corruptAssetPackURL)
        let corruptAssetRun = try runProcess(
            executableURL: scriptURL,
            arguments: [assetRoot.path, corruptAssetLibraryRoot.path],
            currentDirectoryURL: projectRoot.appendingPathComponent("SimuBoardMac", isDirectory: true),
            environmentOverrides: [
                "SIMUBOARD_INSTALLER_NOW": "2026-08-24T10:41:00Z",
            ]
        )
        try results.check(
            corruptAssetRun.terminationStatus != 0,
            "installer must reject an existing fixed BCP pack whose asset bytes no longer match the manifest"
        )
        let corruptAssetSnapshotAfterRun = try snapshot(packAt: corruptAssetPackURL)
        try results.check(
            corruptAssetSnapshotAfterRun == corruptAssetSnapshot,
            "rejecting corrupt asset bytes must leave the old fixed BCP pack untouched"
        )

        let malformedMappingLibraryRoot = root.appendingPathComponent(
            "malformed-mapping-library",
            isDirectory: true
        )
        let malformedMappingPackURL = try installBCPFixturePack(
            scriptURL: scriptURL,
            assetRoot: assetRoot,
            libraryRoot: malformedMappingLibraryRoot,
            projectRoot: projectRoot,
            installTime: "2026-08-24T10:50:00Z"
        )
        try rewriteManifestJSONObject(at: malformedMappingPackURL) { manifest in
            guard var press = manifest["press"] as? [String: Any] else {
                throw HarnessFailure.assertion("fixture press assignments must exist")
            }
            press["generic"] = String(repeating: "a", count: 64)
            manifest["press"] = press
        }
        let malformedMappingSnapshot = try snapshot(packAt: malformedMappingPackURL)
        let malformedMappingRun = try runProcess(
            executableURL: scriptURL,
            arguments: [assetRoot.path, malformedMappingLibraryRoot.path],
            currentDirectoryURL: projectRoot.appendingPathComponent("SimuBoardMac", isDirectory: true),
            environmentOverrides: [
                "SIMUBOARD_INSTALLER_NOW": "2026-08-24T10:51:00Z",
            ]
        )
        try results.check(
            malformedMappingRun.terminationStatus != 0,
            "installer must reject an existing fixed BCP pack with malformed fixed-row assignments"
        )
        let malformedMappingSnapshotAfterRun = try snapshot(packAt: malformedMappingPackURL)
        try results.check(
            malformedMappingSnapshotAfterRun == malformedMappingSnapshot,
            "rejecting malformed mappings must leave the old fixed BCP pack untouched"
        )

        try makeBCPInstallerFixtureAssets(at: assetRoot, variantOffset: 0)
        let sentinelID = UUID()
        let rollbackLibraryRoot = root.appendingPathComponent("rollback-library", isDirectory: true)
        let rollbackInitialAssetRoot = root.appendingPathComponent(
            "rollback-initial-assets",
            isDirectory: true
        )
        try makeBCPInstallerFixtureAssets(at: rollbackInitialAssetRoot, variantOffset: 9)
        let rollbackLegacyPackURL = try installBCPFixturePack(
            scriptURL: scriptURL,
            assetRoot: rollbackInitialAssetRoot,
            libraryRoot: rollbackLibraryRoot,
            projectRoot: projectRoot,
            installTime: "2026-08-20T08:00:00Z"
        )
        try setManifestTimestamps(
            at: rollbackLegacyPackURL,
            createdAt: iso8601Date("2026-08-20T08:00:00Z"),
            modifiedAt: iso8601Date("2026-08-21T09:30:00Z")
        )
        let rollbackLegacySnapshot = try snapshot(packAt: rollbackLegacyPackURL)
        let rollbackLibrary = SoundPackLibrary(rootURL: rollbackLibraryRoot, builtInDescriptors: [])
        _ = try await rollbackLibrary.save(
            manifest: SoundPackManifest(
                id: sentinelID,
                name: "Sentinel",
                baseProfileID: SwitchProfile.holyPanda.rawValue
            )
        )
        let sentinelPackURL = await rollbackLibrary.packURL(id: sentinelID)
        let sentinelSnapshot = try snapshot(packAt: sentinelPackURL)
        try results.check(
            fileManager.fileExists(atPath: sentinelPackURL.path),
            "fixture sentinel pack must exist before reinstall"
        )

        let failAfterBackup = try runProcess(
            executableURL: scriptURL,
            arguments: [assetRoot.path, rollbackLibraryRoot.path],
            currentDirectoryURL: projectRoot.appendingPathComponent("SimuBoardMac", isDirectory: true),
            environmentOverrides: [
                "SIMUBOARD_INSTALLER_FAIL_AT": "after-backup",
                "SIMUBOARD_INSTALLER_NOW": "2026-08-24T11:00:00Z",
            ]
        )
        try results.check(
            failAfterBackup.terminationStatus != 0,
            "after-backup failure injection must fail the installer"
        )
        let rollbackSnapshotAfterBackup = try snapshot(packAt: rollbackLegacyPackURL)
        try results.check(
            rollbackSnapshotAfterBackup == rollbackLegacySnapshot,
            "after-backup rollback must restore the original BCP pack exactly"
        )
        let sentinelSnapshotAfterBackup = try snapshot(packAt: sentinelPackURL)
        try results.check(
            sentinelSnapshotAfterBackup == sentinelSnapshot,
            "after-backup rollback must leave unrelated packs untouched"
        )

        let failAfterInstallBeforeCommit = try runProcess(
            executableURL: scriptURL,
            arguments: [assetRoot.path, rollbackLibraryRoot.path],
            currentDirectoryURL: projectRoot.appendingPathComponent("SimuBoardMac", isDirectory: true),
            environmentOverrides: [
                "SIMUBOARD_INSTALLER_FAIL_AT": "after-install-before-commit",
                "SIMUBOARD_INSTALLER_NOW": "2026-08-24T12:00:00Z",
            ]
        )
        try results.check(
            failAfterInstallBeforeCommit.terminationStatus != 0,
            "after-install-before-commit failure injection must fail the installer"
        )
        let rollbackSnapshotAfterInstallFailure = try snapshot(packAt: rollbackLegacyPackURL)
        try results.check(
            rollbackSnapshotAfterInstallFailure == rollbackLegacySnapshot,
            "after-install-before-commit rollback must restore the original BCP pack exactly"
        )
        let sentinelSnapshotAfterInstallFailure = try snapshot(packAt: sentinelPackURL)
        try results.check(
            sentinelSnapshotAfterInstallFailure == sentinelSnapshot,
            "after-install-before-commit rollback must leave unrelated packs untouched"
        )

        let noExistingLibraryRoot = root.appendingPathComponent("no-existing-library", isDirectory: true)
        let failWithoutExisting = try runProcess(
            executableURL: scriptURL,
            arguments: [assetRoot.path, noExistingLibraryRoot.path],
            currentDirectoryURL: projectRoot.appendingPathComponent("SimuBoardMac", isDirectory: true),
            environmentOverrides: [
                "SIMUBOARD_INSTALLER_FAIL_AT": "after-install-before-commit",
                "SIMUBOARD_INSTALLER_NOW": "2026-08-24T12:30:00Z",
            ]
        )
        try results.check(
            failWithoutExisting.terminationStatus != 0,
            "after-install-before-commit must fail even when there was no prior pack"
        )
        try results.check(
            !fileManager.fileExists(atPath: bcpPackURL(in: noExistingLibraryRoot).path),
            "failed first-time install must not leave a partial BCP pack behind"
        )

        let invalidLibraryRoot = root.appendingPathComponent("invalid-existing-library", isDirectory: true)
        let invalidPackURL = bcpPackURL(in: invalidLibraryRoot)
        try fileManager.createDirectory(
            at: invalidPackURL.appendingPathComponent("assets", isDirectory: true),
            withIntermediateDirectories: true
        )
        try Data("not-json".utf8).write(
            to: invalidPackURL.appendingPathComponent("manifest.json"),
            options: .atomic
        )
        let invalidRun = try runProcess(
            executableURL: scriptURL,
            arguments: [assetRoot.path, invalidLibraryRoot.path],
            currentDirectoryURL: projectRoot.appendingPathComponent("SimuBoardMac", isDirectory: true),
            environmentOverrides: [
                "SIMUBOARD_INSTALLER_NOW": "2026-08-24T13:00:00Z",
            ]
        )
        try results.check(
            invalidRun.terminationStatus != 0,
            "installer must reject an invalid existing fixed-pack manifest instead of overwriting it"
        )
        try results.check(
            fileManager.fileExists(atPath: invalidPackURL.path)
                && !fileManager.fileExists(
                    atPath: invalidPackURL.appendingPathComponent("assets").appendingPathComponent("03598ddc778f4bcc03a28f59ada841dd663750846bc6449ff0fb3d9169fce057.wav").path
                ),
            "rejecting an invalid existing fixed-pack manifest must leave the original directory untouched"
        )
    }

    private static func testLaunchAtLoginInstallPaths(
        _ results: inout HarnessResults
    ) throws {
        let homeDirectory = URL(fileURLWithPath: "/Users/battuta-test", isDirectory: true)
        try results.check(
            LaunchAtLoginController.isInstalledApplication(
                at: URL(fileURLWithPath: "/Applications/Battuta.app", isDirectory: true),
                homeDirectory: homeDirectory
            ),
            "an app in the system Applications directory should be eligible for login launch"
        )
        try results.check(
            LaunchAtLoginController.isInstalledApplication(
                at: URL(
                    fileURLWithPath: "/Users/battuta-test/Applications/Battuta.app",
                    isDirectory: true
                ),
                homeDirectory: homeDirectory
            ),
            "an app in the user's Applications directory should be eligible for login launch"
        )
        try results.check(
            !LaunchAtLoginController.isInstalledApplication(
                at: URL(fileURLWithPath: "/Volumes/Battuta/Battuta.app", isDirectory: true),
                homeDirectory: homeDirectory
            ),
            "an app running from its DMG must not register as a login item"
        )
        try results.check(
            !LaunchAtLoginController.isInstalledApplication(
                at: URL(fileURLWithPath: "/private/tmp/Battuta.app", isDirectory: true),
                homeDirectory: homeDirectory
            ),
            "a development or temporary app must not register as a login item"
        )
    }

    private static func testValidatorAndResolver(_ results: inout HarnessResults) throws {
        let overrideID = soundAssetID("a")
        let specialID = soundAssetID("b")
        let rowID = soundAssetID("c")
        let genericID = soundAssetID("d")
        let assets = Dictionary(uniqueKeysWithValues: [overrideID, specialID, rowID, genericID].map {
            ($0.rawValue, fakeAsset(id: $0))
        })

        var press = SoundPackPhaseAssignments(generic: genericID)
        press.setAsset(rowID, for: .r2)
        press.setAsset(specialID, for: .space)
        press.setOverride(.asset(overrideID), for: KeyboardKeyID("a"))
        var manifest = SoundPackManifest(
            name: "Resolver fixture",
            baseProfileID: SwitchProfile.mxBlue.rawValue,
            press: press,
            assets: assets
        )
        try SoundPackValidator.validate(manifest)
        var resolver = SoundPackResolver(manifest: manifest)

        try results.check(
            resolver.resolution(for: 0, phase: .press) == .asset(overrideID, source: .keyOverride(KeyboardKeyID("a"))),
            "per-key override must beat row and generic"
        )
        try results.check(
            resolver.resolution(for: 49, phase: .press) == .asset(specialID, source: .special(.space)),
            "special assignment must beat row and generic"
        )
        try results.check(
            resolver.resolution(for: 1, phase: .press) == .asset(rowID, source: .row(.r2)),
            "row assignment must beat generic"
        )
        try results.check(
            resolver.resolution(for: 12, phase: .press) == .asset(genericID, source: .generic),
            "generic assignment should be the final custom fallback"
        )

        manifest.press.setOverride(.inherit, for: KeyboardKeyID("a"))
        resolver = SoundPackResolver(manifest: manifest)
        try results.check(
            resolver.resolution(for: 0, phase: .press) == .asset(rowID, source: .row(.r2)),
            "inherit must continue resolving through row"
        )

        manifest.press.setOverride(.silent, for: KeyboardKeyID("a"))
        resolver = SoundPackResolver(manifest: manifest)
        try results.check(
            resolver.resolution(for: 0, phase: .press) == .silent(source: .keyOverride(KeyboardKeyID("a"))),
            "explicit silence must prevent lower-level fallback"
        )
        try results.check(
            resolver.resolution(for: 63, phase: .press) == .asset(genericID, source: .generic),
            "Fn/Globe should participate in DIY mapping when delivered as flagsChanged"
        )

        let fallbackManifest = SoundPackManifest(
            name: "Built-in fallback fixture",
            baseProfileID: SwitchProfile.mxClear.rawValue
        )
        try results.check(
            SoundPackResolver(manifest: fallbackManifest).resolution(for: 0, phase: .press)
                == .missing(source: .missingAssignment),
            "unassigned custom slots must remain missing so the engine can reach the built-in fallback"
        )
        try results.check(
            KeySoundMapper.sample(for: 0, phase: .release, profile: .mxClear) == .genericR2,
            "built-in fallback should retain the base profile's row-specific release mapping"
        )

        var broken = manifest
        broken.assets.removeValue(forKey: overrideID.rawValue)
        broken.press.setOverride(.asset(overrideID), for: KeyboardKeyID("a"))
        try results.check(
            SoundPackResolver(manifest: broken).resolution(for: 0, phase: .press)
                == .missing(source: .brokenAssetReference(overrideID)),
            "resolver must not return an absent asset"
        )

        var missingAsset = manifest
        missingAsset.assets.removeValue(forKey: overrideID.rawValue)
        missingAsset.press.setOverride(.asset(overrideID), for: KeyboardKeyID("a"))
        try results.expectError("validator rejects missing asset", {
            try SoundPackValidator.validate(missingAsset)
        }, matches: {
            if case SoundPackError.missingAsset = $0 { return true }
            return false
        })

        var unknownRow = manifest
        unknownRow.press.rows["R99"] = genericID
        try results.expectError("validator rejects unknown row", {
            try SoundPackValidator.validate(unknownRow)
        }, matches: {
            if case SoundPackError.invalidManifest = $0 { return true }
            return false
        })

        var unsafePath = manifest
        unsafePath.assets[genericID.rawValue]?.relativePath = "../escape.wav"
        try results.expectError("validator rejects traversal path", {
            try SoundPackValidator.validate(unsafePath)
        }, matches: {
            if case SoundPackError.unsafePath = $0 { return true }
            return false
        })

        var unknownBase = manifest
        unknownBase.baseProfileID = "unknown-profile"
        try results.expectError("validator rejects unknown built-in fallback", {
            try SoundPackValidator.validate(unknownBase)
        }, matches: {
            if case SoundPackError.invalidManifest = $0 { return true }
            return false
        })

        var unknownLayout = manifest
        unknownLayout.layoutID = "future-iso-layout"
        try results.expectError("validator rejects unsupported layout", {
            try SoundPackValidator.validate(unknownLayout)
        }, matches: {
            if case SoundPackError.invalidManifest = $0 { return true }
            return false
        })

        var excessiveDuration = SoundPackManifest(name: "Excessive duration")
        for index in 1...37 {
            let rawID = String(format: "%064x", index)
            let id = SoundPackAssetID(rawID)
            excessiveDuration.assets[rawID] = SoundPackAudioAsset(
                id: id,
                relativePath: "assets/\(rawID).wav",
                sha256: rawID,
                durationSeconds: 5,
                byteCount: 480_044
            )
        }
        try results.expectError("validator rejects excessive total audio duration", {
            try SoundPackValidator.validate(excessiveDuration)
        }, matches: {
            if case SoundPackError.sizeLimitExceeded = $0 { return true }
            return false
        })
    }

    private static func testAudioLibraryAndArchive(_ results: inout HarnessResults) async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("SimuBoard-DIYCoreHarness-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }

        let boundedPCMBytes = try AudioImportService.checkedDecodedPCMByteCount(
            sampleRate: 48_000,
            channelCount: 2,
            frameLength: 48_000,
            bytesPerSample: 4
        )
        try results.check(boundedPCMBytes == 384_000, "decoded PCM byte count should include every channel")

        let framesAtMemoryLimit = AudioImportService.maximumDecodedPCMBytes / (8 * 8)
        let exactMemoryLimit = try AudioImportService.checkedDecodedPCMByteCount(
            sampleRate: 384_000,
            channelCount: 8,
            frameLength: framesAtMemoryLimit,
            bytesPerSample: 8
        )
        try results.check(
            exactMemoryLimit == AudioImportService.maximumDecodedPCMBytes,
            "decoded PCM allocation should allow the exact 64 MiB boundary"
        )
        try results.expectError("decoded PCM allocation rejects memory amplification", {
            _ = try AudioImportService.checkedDecodedPCMByteCount(
                sampleRate: 384_000,
                channelCount: 8,
                frameLength: framesAtMemoryLimit + 1,
                bytesPerSample: 8
            )
        }, matches: {
            if case SoundPackError.sizeLimitExceeded = $0 { return true }
            return false
        })
        try results.expectError("decoded PCM allocation rejects excessive channels", {
            _ = try AudioImportService.checkedDecodedPCMByteCount(
                sampleRate: 48_000,
                channelCount: 9,
                frameLength: 48_000,
                bytesPerSample: 4
            )
        }, matches: {
            if case SoundPackError.invalidAudio = $0 { return true }
            return false
        })
        try results.expectError("decoded PCM allocation rejects excessive sample rates", {
            _ = try AudioImportService.checkedDecodedPCMByteCount(
                sampleRate: 768_000,
                channelCount: 1,
                frameLength: 48_000,
                bytesPerSample: 4
            )
        }, matches: {
            if case SoundPackError.invalidAudio = $0 { return true }
            return false
        })
        try results.expectError("decoded PCM arithmetic cannot overflow", {
            _ = try AudioImportService.checkedDecodedPCMByteCount(
                sampleRate: 48_000,
                channelCount: 8,
                frameLength: Int64(AVAudioFrameCount.max),
                bytesPerSample: Int.max
            )
        }, matches: {
            if case SoundPackError.sizeLimitExceeded = $0 { return true }
            return false
        })

        let sourceURL = root.appendingPathComponent("source-44k-stereo.wav")
        try makeStereoFixture(at: sourceURL, sampleRate: 44_100, duration: 0.12)

        let importsRoot = root.appendingPathComponent("imports", isDirectory: true)
        let importer = AudioImportService(workingDirectory: importsRoot)
        let prepared = try await importer.prepareImport(from: sourceURL)
        let normalizedInfo = try AudioImportService.validateNormalizedAudio(at: prepared.normalizedFileURL)
        try results.check(normalizedInfo.sampleRate == 48_000, "import must normalize to 48 kHz")
        try results.check(normalizedInfo.channelCount == 1, "import must normalize to mono")
        try results.check(abs(normalizedInfo.durationSeconds - 0.12) < 0.003, "normalization should preserve duration")
        try results.check(prepared.metadata.sha256 == prepared.id.rawValue, "asset ID must be content hash")

        let duplicatePreparation = try await importer.prepareImport(from: sourceURL)
        try results.check(duplicatePreparation.id == prepared.id, "same normalized content should deduplicate")
        try results.check(
            duplicatePreparation.normalizedFileURL == prepared.normalizedFileURL,
            "deduplicated preparations should reuse the normalized file"
        )

        let invalidSourceURL = root.appendingPathComponent("invalid-compressed-audio.mp3")
        try Data("not an audio file".utf8).write(to: invalidSourceURL)

        let timeoutPIDURL = root.appendingPathComponent("timeout-ffmpeg.pid")
        let timeoutExecutableURL = root.appendingPathComponent("timeout-ffmpeg")
        try makeSleepingExecutable(at: timeoutExecutableURL, pidFileURL: timeoutPIDURL)
        let timeoutImportsRoot = root.appendingPathComponent("timeout-imports", isDirectory: true)
        let timeoutImporter = AudioImportService(
            workingDirectory: timeoutImportsRoot,
            ffmpegExecutableOverride: timeoutExecutableURL,
            ffmpegTimeoutSeconds: 0.5
        )
        let timeoutStartedAt = ProcessInfo.processInfo.systemUptime
        do {
            _ = try await timeoutImporter.prepareImport(from: invalidSourceURL)
            throw HarnessFailure.assertion("ffmpeg timeout fixture unexpectedly imported")
        } catch let error as SoundPackError {
            guard case let .invalidAudio(message) = error, message.contains("超时") else {
                throw HarnessFailure.assertion("wrong ffmpeg timeout error: \(error)")
            }
            try results.check(true, "ffmpeg fallback should report a bounded timeout")
        }
        let timeoutElapsed = ProcessInfo.processInfo.systemUptime - timeoutStartedAt
        try results.check(timeoutElapsed < 3, "ffmpeg timeout must return promptly")
        let timeoutPID = try readProcessIdentifier(at: timeoutPIDURL)
        try results.check(!processExists(timeoutPID), "timed-out ffmpeg process must be terminated")
        let timeoutLeftovers = try FileManager.default.contentsOfDirectory(
            at: timeoutImportsRoot,
            includingPropertiesForKeys: nil
        )
        try results.check(timeoutLeftovers.isEmpty, "timed-out ffmpeg output must be removed")

        try FileManager.default.removeItem(at: timeoutPIDURL)
        let playlistURL = root.appendingPathComponent("network-playlist.m3u")
        try Data("https://example.invalid/secret.mp3\n".utf8).write(to: playlistURL)
        do {
            _ = try await timeoutImporter.prepareImport(from: playlistURL)
            throw HarnessFailure.assertion("playlist unexpectedly reached fallback conversion")
        } catch let error as SoundPackError {
            guard case .invalidAudio = error else {
                throw HarnessFailure.assertion("wrong playlist rejection error: \(error)")
            }
            try results.check(true, "ffmpeg fallback should reject playlist containers")
        }
        try results.check(
            !FileManager.default.fileExists(atPath: timeoutPIDURL.path),
            "rejected playlists must not start the external converter"
        )

        let cancellationPIDURL = root.appendingPathComponent("cancelled-ffmpeg.pid")
        let cancellationExecutableURL = root.appendingPathComponent("cancelled-ffmpeg")
        try makeSleepingExecutable(at: cancellationExecutableURL, pidFileURL: cancellationPIDURL)
        let cancellationImportsRoot = root.appendingPathComponent("cancelled-imports", isDirectory: true)
        let cancellationImporter = AudioImportService(
            workingDirectory: cancellationImportsRoot,
            ffmpegExecutableOverride: cancellationExecutableURL,
            ffmpegTimeoutSeconds: 20
        )
        let cancellationTask = Task<PreparedSoundPackAudio, Error> {
            try await cancellationImporter.prepareImport(from: invalidSourceURL)
        }
        let fallbackStarted = await waitForFile(at: cancellationPIDURL, timeoutSeconds: 2)
        guard fallbackStarted else {
            cancellationTask.cancel()
            _ = try? await cancellationTask.value
            throw HarnessFailure.assertion("ffmpeg cancellation fixture did not start")
        }
        let cancellationPID = try readProcessIdentifier(at: cancellationPIDURL)
        let cancellationStartedAt = ProcessInfo.processInfo.systemUptime
        cancellationTask.cancel()
        do {
            _ = try await cancellationTask.value
            throw HarnessFailure.assertion("cancelled ffmpeg import unexpectedly succeeded")
        } catch is CancellationError {
            try results.check(true, "ffmpeg fallback should preserve task cancellation")
        }
        let cancellationElapsed = ProcessInfo.processInfo.systemUptime - cancellationStartedAt
        try results.check(cancellationElapsed < 3, "cancelled ffmpeg import must return promptly")
        try results.check(!processExists(cancellationPID), "cancelled ffmpeg process must be terminated")
        let cancellationLeftovers = try FileManager.default.contentsOfDirectory(
            at: cancellationImportsRoot,
            includingPropertiesForKeys: nil
        )
        try results.check(cancellationLeftovers.isEmpty, "cancelled ffmpeg output must be removed")

        var assignments = SoundPackPhaseAssignments(generic: prepared.id)
        assignments.setAsset(prepared.id, for: .space)
        let packID = UUID()
        let manifest = SoundPackManifest(
            id: packID,
            name: "Round trip",
            baseProfileID: SwitchProfile.holyPanda.rawValue,
            press: assignments,
            release: SoundPackPhaseAssignments(generic: prepared.id),
            assets: [prepared.id.rawValue: prepared.metadata]
        )

        let libraryRoot = root.appendingPathComponent("library", isDirectory: true)
        let library = SoundPackLibrary(rootURL: libraryRoot, builtInDescriptors: [])
        let descriptor = try await library.save(
            manifest: manifest,
            assetFiles: [prepared.id: prepared.normalizedFileURL]
        )
        try results.check(descriptor.customPackID == packID, "save should preserve pack UUID")
        let loaded = try await library.loadCustomPack(id: packID)
        try results.check(loaded.manifest.name == "Round trip", "saved manifest should load")
        let loadedAssetURL = try loaded.assetURL(for: prepared.id)
        let loadedAssetHash = try SoundPackFileUtilities.sha256(of: loadedAssetURL)
        try results.check(
            loadedAssetHash == prepared.id.rawValue,
            "saved audio hash should survive library round trip"
        )
        let initialDescriptors = try await library.descriptors()
        try results.check(initialDescriptors.count == 1, "library should enumerate saved custom pack")

        var renamed = loaded.manifest
        renamed.name = "Round trip renamed"
        _ = try await library.save(manifest: renamed)
        let reloadedAfterRename = try await library.loadCustomPack(id: packID)
        try results.check(
            reloadedAfterRename.manifest.name == "Round trip renamed",
            "updating metadata should retain existing asset files"
        )

        let archive = SoundPackArchiveService()
        let exportURL = root.appendingPathComponent("export.simuboardpack", isDirectory: true)
        _ = try await archive.export(customPackID: packID, from: library, to: exportURL)
        _ = try await archive.validate(at: exportURL)
        try results.check(FileManager.default.fileExists(atPath: exportURL.path), "export should create package")

        // Exporting over an existing package exercises the atomic replacement path.
        _ = try await archive.export(customPackID: packID, from: library, to: exportURL)
        _ = try await archive.validate(at: exportURL)
        try results.check(true, "re-export over an existing package should remain valid")

        let importedLibrary = SoundPackLibrary(
            rootURL: root.appendingPathComponent("imported-library", isDirectory: true),
            builtInDescriptors: []
        )
        let imported = try await archive.importPack(at: exportURL, into: importedLibrary)
        try results.check(imported.customPackID == packID, "first import should retain package UUID")

        do {
            _ = try await archive.importPack(
                at: exportURL,
                into: importedLibrary,
                collisionPolicy: .reject
            )
            throw HarnessFailure.assertion("reject collision policy accepted a duplicate UUID")
        } catch SoundPackError.packAlreadyExists(let rejectedID) {
            try results.check(rejectedID == packID, "reject collision should report colliding UUID")
        }

        let duplicated = try await archive.importPack(
            at: exportURL,
            into: importedLibrary,
            collisionPolicy: .duplicate
        )
        try results.check(duplicated.customPackID != packID, "duplicate collision should mint a new UUID")
        let duplicatedDescriptors = try await importedLibrary.descriptors()
        try results.check(duplicatedDescriptors.count == 2, "duplicate import should preserve both packs")

        let exportedManifestURL = exportURL.appendingPathComponent("manifest.json")
        var replacementManifest = try SoundPackCoding.decode(Data(contentsOf: exportedManifestURL))
        replacementManifest.name = "Collision replacement"
        try SoundPackCoding.encode(replacementManifest).write(to: exportedManifestURL, options: .atomic)
        _ = try await archive.validate(at: exportURL)
        _ = try await archive.importPack(
            at: exportURL,
            into: importedLibrary,
            collisionPolicy: .replace
        )
        let replacedDescriptors = try await importedLibrary.descriptors()
        try results.check(replacedDescriptors.count == 2, "replace collision should not add a third pack")
        let replacedDocument = try await importedLibrary.loadCustomPack(id: packID)
        try results.check(
            replacedDocument.manifest.name == "Collision replacement",
            "replace collision should update the existing package contents"
        )
    }

    private static func testAudioSplit(_ results: inout HarnessResults) async throws {
        let fileManager = FileManager.default
        let root = fileManager.temporaryDirectory.appendingPathComponent(
            "SimuBoardSplitHarness-\(UUID().uuidString)",
            isDirectory: true
        )
        try fileManager.createDirectory(at: root, withIntermediateDirectories: false)
        defer { try? fileManager.removeItem(at: root) }

        let source = root.appendingPathComponent("complete-keystroke.wav")
        try makeStereoFixture(at: source, sampleRate: 44_100, duration: 0.16)

        let splitter = AudioSplitService()
        let analysis = try await splitter.analyze(sourceURL: source)
        try results.check(analysis.sampleRate == 48_000, "split analysis should normalize to 48 kHz")
        try results.check(analysis.frameCount > 0, "split analysis should produce samples")
        try results.check(
            analysis.suggestion.splitTime > 0 && analysis.suggestion.splitTime < analysis.duration,
            "split suggestion should stay inside the recording"
        )

        let press = root.appendingPathComponent("press.wav")
        let release = root.appendingPathComponent("release.wav")
        let splitTime = analysis.duration / 2
        let exported = try await splitter.exportSplit(
            sourceURL: source,
            splitTime: splitTime,
            releaseEndTime: analysis.duration,
            pressDestination: press,
            releaseDestination: release
        )
        try results.check(exported.pressFrameCount > 0, "press export should not be empty")
        try results.check(exported.releaseFrameCount > 0, "release export should not be empty")
        let pressInfo = try AudioImportService.validateNormalizedAudio(at: press)
        let releaseInfo = try AudioImportService.validateNormalizedAudio(at: release)
        try results.check(pressInfo.sampleRate == 48_000, "press export should be normalized")
        try results.check(releaseInfo.sampleRate == 48_000, "release export should be normalized")

        _ = try await splitter.exportSplit(
            sourceURL: source,
            splitTime: splitTime,
            releaseEndTime: analysis.duration,
            pressDestination: press,
            releaseDestination: release,
            overwriteExisting: true
        )
        try results.check(
            fileManager.fileExists(atPath: press.path) && fileManager.fileExists(atPath: release.path),
            "pair overwrite should leave both outputs installed"
        )

        var constrainedConfiguration = AudioSplitConfiguration()
        constrainedConfiguration.maximumDecodedBytes = 1_024
        let constrained = AudioSplitService(configuration: constrainedConfiguration)
        do {
            _ = try await constrained.analyze(sourceURL: source)
            throw HarnessFailure.assertion("oversized decoded split input should be rejected")
        } catch let error as AudioSplitError {
            guard case .decodedAudioIsTooLarge = error else {
                throw HarnessFailure.assertion("wrong split memory-limit error: \(error)")
            }
            try results.check(true, "split decoded PCM limit should reject oversized input")
        }
    }

    private static func testEngineLoadFailureContract(_ results: inout HarnessResults) throws {
        let projectRoot = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        let engineURL = projectRoot.appendingPathComponent(
            "SimuBoardMac/SimuBoardMac/Services/KeyboardAudioEngine.swift"
        )
        let appModelURL = projectRoot.appendingPathComponent(
            "SimuBoardMac/SimuBoardMac/Services/AppModel.swift"
        )
        let engineSource = try String(contentsOf: engineURL, encoding: .utf8)
        let appModelSource = try String(contentsOf: appModelURL, encoding: .utf8)

        try results.check(
            engineSource.contains("func load(document: SoundPackDocument) -> Bool"),
            "custom engine loading must report success/failure"
        )
        guard let loadStart = engineSource.range(of: "func load(document: SoundPackDocument) -> Bool")?.lowerBound,
              let playStart = engineSource.range(of: "    func play(", range: loadStart..<engineSource.endIndex)?.lowerBound else {
            throw HarnessFailure.assertion("could not isolate custom load implementation")
        }
        let customLoadSource = engineSource[loadStart..<playStart]
        let failurePosition = customLoadSource.range(of: "return false")?.lowerBound
        let commitPosition = customLoadSource.range(of: "loadedSelectionID = document.id")?.lowerBound
        try results.check(
            failurePosition != nil && commitPosition != nil && failurePosition! < commitPosition!,
            "custom load should validate resources before committing selection state"
        )
        try results.check(
            appModelSource.contains("if audioEngine.load(document: document)")
                && appModelSource.contains("audioEngine.load(profile: fallback)"),
            "AppModel must detect custom-load failure and perform an explicit fallback"
        )
        try results.check(
            !appModelSource.contains("Timer.publish(every: 1")
                && appModelSource.contains("startPermissionPollingIfNeeded()"),
            "permission checks should poll only while authorization is still pending"
        )
        try results.check(
            appModelSource.contains("音色载入失败，已回退到"),
            "fallback should remain visible to the user instead of silently playing the prior pack"
        )
        try results.check(
            appModelSource.contains("guard audioEngine.load(pointerProfile: profile) else")
                && appModelSource.contains("rollBackPointerSelection(to: fallback)"),
            "pointer profile loading must roll back the UI selection after resource failure"
        )
        guard let keyboardHandlerStart = appModelSource.range(
            of: "private func handle(_ event: KeyboardEvent)"
        )?.lowerBound,
        let pointerHandlerStart = appModelSource.range(
            of: "private func handle(_ event: PointerEvent)"
        )?.lowerBound,
        let pointerHandlerEnd = appModelSource.range(
            of: "private func loadPointerSoundProfile",
            range: pointerHandlerStart..<appModelSource.endIndex
        )?.lowerBound else {
            throw HarnessFailure.assertion("could not isolate keyboard and pointer audio routing")
        }
        let keyboardHandlerSource = appModelSource[keyboardHandlerStart..<pointerHandlerStart]
        let pointerHandlerSource = appModelSource[pointerHandlerStart..<pointerHandlerEnd]
        try results.check(
            keyboardHandlerSource.contains("volume: settings.keyboardPlaybackGain")
                && !keyboardHandlerSource.contains("settings.pointerVolume"),
            "keyboard events must use the perceptual keyboard gain"
        )
        try results.check(
            pointerHandlerSource.contains("volume: settings.pointerVolume")
                && !pointerHandlerSource.contains("volume: settings.volume"),
            "pointer events must use only the pointer volume"
        )

        guard let previewStart = appModelSource.range(of: "    func preview()")?.lowerBound,
              let monitorStart = appModelSource.range(
                  of: "    private func startKeyboardMonitor()",
                  range: previewStart..<appModelSource.endIndex
              )?.lowerBound else {
            throw HarnessFailure.assertion("could not isolate keyboard preview routing")
        }
        let previewSource = appModelSource[previewStart..<monitorStart]
        try results.check(
            previewSource.contains("volume: settings.keyboardPlaybackGain")
                && !previewSource.contains("volume: settings.volume"),
            "keyboard previews must use the perceptual keyboard gain"
        )
    }

    private static func testNormalKeyboardVolumeMessagingContract(
        _ results: inout HarnessResults
    ) throws {
        let projectRoot = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        let menuBarSource = try String(
            contentsOf: projectRoot.appendingPathComponent(
                "SimuBoardMac/SimuBoardMac/Views/MenuBarView.swift"
            ),
            encoding: .utf8
        )
        let readmeSource = try String(
            contentsOf: projectRoot.appendingPathComponent("SimuBoardMac/README.md"),
            encoding: .utf8
        )

        try results.check(
            menuBarSource.contains("Text(\"键盘音量\")"),
            "keyboard volume row must expose the ordinary volume title"
        )
        try results.check(
            menuBarSource.contains("Int(settings.volume * 100)")
                && !menuBarSource.contains("Int(settings.keyboardPlaybackGain * 100)"),
            "keyboard volume percentage must describe perceptual slider position"
        )
        try results.check(
            menuBarSource.contains(".accessibilityLabel(\"键盘音量\")"),
            "keyboard volume slider must expose the ordinary volume accessibility label"
        )
        try results.check(
            !menuBarSource.contains("键盘绝对音量")
                && !menuBarSource.contains("系统过低时会自动收敛补偿避免失真"),
            "keyboard volume controls must not promise system-volume compensation"
        )
        try results.check(
            !readmeSource.contains("键盘绝对音量")
                && !readmeSource.contains("反向补偿系统主音量"),
            "README must not document the removed absolute-volume feature"
        )
    }

    @MainActor
    private static func testUpdateCachingAndThrottling(_ results: inout HarnessResults) async throws {
        let projectRoot = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        let releaseClientSource = try String(
            contentsOf: projectRoot.appendingPathComponent(
                "SimuBoardMac/SimuBoardMac/Services/GitHubReleaseClient.swift"
            ),
            encoding: .utf8
        )
        try results.check(
            releaseClientSource.contains("https://api.github.com/repos/wormforce/battuta/releases/latest"),
            "update checks must use the renamed Battuta repository"
        )
        try results.check(
            ReleaseSummary.isAllowedReleaseURL(
                URL(string: "https://github.com/wormforce/battuta/releases/tag/v0.6.2")!
            ),
            "release validation must accept the Battuta repository"
        )
        try results.check(
            !ReleaseSummary.isAllowedReleaseURL(
                URL(string: "https://github.com/7b7b7b/simuboard/releases/tag/v0.6.1")!
            ),
            "release validation must reject the retired SimuBoard repository path"
        )
        let appSource = try String(
            contentsOf: projectRoot.appendingPathComponent(
                "SimuBoardMac/SimuBoardMac/SimuBoardApp.swift"
            ),
            encoding: .utf8
        )
        try results.check(
            appSource.contains("model.updates.scheduleAutomaticCheck(after: 0)"),
            "opening the menu must schedule an automatic update check"
        )

        let release = try ReleaseSummary(
            tagName: "v0.4.1",
            releaseURL: URL(string: "https://github.com/wormforce/battuta/releases/tag/v0.4.1")!,
            publishedAt: nil
        )
        let rateLimit = GitHubRateLimit(remaining: 59, resetAt: nil)
        let modified = GitHubReleaseFetchResult.modified(
            release: release,
            etag: "etag-041",
            rateLimit: rateLimit
        )
        let notModified = GitHubReleaseFetchResult.notModified(
            etag: "etag-041",
            rateLimit: rateLimit
        )
        let recorder = FetchRecorder([.success(modified), .success(notModified), .success(notModified)])
        let client = GitHubReleaseClient { etag in try await recorder.fetch(etag: etag) }
        let suiteName = "SimuBoard.DIYCoreHarness.Updates.\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suiteName) else {
            throw HarnessFailure.assertion("could not create isolated UserDefaults")
        }
        defer { defaults.removePersistentDomain(forName: suiteName) }

        let clock = LockedClock(Date(timeIntervalSince1970: 1_800_000_000))
        let installed = SemanticVersion(major: 0, minor: 4, patch: 0)
        let controller = UpdateController(
            client: client,
            installedVersion: installed,
            defaults: defaults,
            now: clock.now
        )
        await controller.check(trigger: .automatic)
        let disabledAutomaticCallCount = await recorder.callCount
        try results.check(
            disabledAutomaticCallCount == 0,
            "menu-open automatic checks must remain disabled until the user opts in"
        )
        controller.enableAutomaticChecks(checkImmediately: false)
        await controller.check(trigger: .manual)
        let firstManualCallCount = await recorder.callCount
        try results.check(firstManualCallCount == 1, "first manual check should fetch")
        try results.check(controller.availableRelease?.version == SemanticVersion("0.4.1"), "newer release should surface")

        clock.advance(5)
        await controller.check(trigger: .manual)
        let spacedManualCallCount = await recorder.callCount
        try results.check(spacedManualCallCount == 1, "manual checks within 65 seconds should be suppressed")
        guard case let .failed(.requestedTooSoon(manualRetryAt), cached) = controller.state else {
            throw HarnessFailure.assertion("a throttled manual check should explain when retry is allowed")
        }
        try results.check(
            manualRetryAt == clock.now().addingTimeInterval(60)
                && cached?.result == .updateAvailable(release),
            "manual spacing feedback should retain the cached update and expose the exact retry time"
        )

        clock.advance(61)
        await controller.check(trigger: .manual)
        let resumedManualCallCount = await recorder.callCount
        try results.check(resumedManualCallCount == 2, "manual check should resume after spacing window")
        let firstControllerETags = await recorder.etags
        try results.check(firstControllerETags.last! == "etag-041", "subsequent fetch should send cached ETag")

        let relaunchedRecorder = FetchRecorder([.success(notModified)])
        let relaunchedClient = GitHubReleaseClient { etag in try await relaunchedRecorder.fetch(etag: etag) }
        let relaunched = UpdateController(
            client: relaunchedClient,
            installedVersion: installed,
            defaults: defaults,
            now: clock.now
        )
        try results.check(relaunched.availableRelease?.version == SemanticVersion("0.4.1"), "cached result should survive relaunch")
        await relaunched.check(trigger: .automatic)
        let cachedAutomaticCallCount = await relaunchedRecorder.callCount
        try results.check(
            cachedAutomaticCallCount == 0,
            "reopening the menu within five minutes should reuse the cached result"
        )

        clock.advance(5 * 60 + 1)
        await relaunched.check(trigger: .automatic)
        let expiredAutomaticCallCount = await relaunchedRecorder.callCount
        try results.check(
            expiredAutomaticCallCount == 1,
            "reopening the menu should refresh after the five-minute spacing window"
        )
        let relaunchedETags = await relaunchedRecorder.etags
        try results.check(relaunchedETags == ["etag-041"], "relaunch should retain ETag cache")

        let failureSuite = "SimuBoard.DIYCoreHarness.UpdateFailures.\(UUID().uuidString)"
        guard let failureDefaults = UserDefaults(suiteName: failureSuite) else {
            throw HarnessFailure.assertion("could not create failure UserDefaults")
        }
        defer { failureDefaults.removePersistentDomain(forName: failureSuite) }
        let failureClock = LockedClock(Date(timeIntervalSince1970: 1_900_000_000))
        let failureRecorder = FetchRecorder([
            .failure(URLError(.notConnectedToInternet)),
            .failure(URLError(.notConnectedToInternet)),
        ])
        let failureClient = GitHubReleaseClient { etag in try await failureRecorder.fetch(etag: etag) }
        let failures = UpdateController(
            client: failureClient,
            installedVersion: installed,
            defaults: failureDefaults,
            now: failureClock.now
        )
        failures.enableAutomaticChecks(checkImmediately: false)
        await failures.check(trigger: .automatic)
        let firstFailureCallCount = await failureRecorder.callCount
        try results.check(firstFailureCallCount == 1, "first automatic failure should fetch once")
        failureClock.advance(4 * 60)
        await failures.check(trigger: .automatic)
        let cooldownCallCount = await failureRecorder.callCount
        try results.check(
            cooldownCallCount == 1,
            "failed menu-open checks should still respect the five-minute spacing window"
        )
        failureClock.advance(61)
        await failures.check(trigger: .automatic)
        let expiredCooldownCallCount = await failureRecorder.callCount
        try results.check(
            expiredCooldownCallCount == 2,
            "a failed menu-open check should be retryable after five minutes"
        )

        let limitSuite = "SimuBoard.DIYCoreHarness.UpdateRateLimit.\(UUID().uuidString)"
        guard let limitDefaults = UserDefaults(suiteName: limitSuite) else {
            throw HarnessFailure.assertion("could not create rate-limit UserDefaults")
        }
        defer { limitDefaults.removePersistentDomain(forName: limitSuite) }
        let limitClock = LockedClock(Date(timeIntervalSince1970: 2_000_000_000))
        let retryAt = limitClock.now().addingTimeInterval(120)
        let limitRecorder = FetchRecorder([
            .failure(GitHubReleaseClientError.rateLimited(retryAt: retryAt)),
            .success(modified),
        ])
        let limitClient = GitHubReleaseClient { etag in try await limitRecorder.fetch(etag: etag) }
        let limited = UpdateController(
            client: limitClient,
            installedVersion: installed,
            defaults: limitDefaults,
            now: limitClock.now
        )
        await limited.check(trigger: .manual)
        limitClock.advance(60)
        await limited.check(trigger: .manual)
        let blockedRateLimitCalls = await limitRecorder.callCount
        try results.check(blockedRateLimitCalls == 1, "server retry deadline should block both manual and automatic requests")
        limitClock.advance(61)
        await limited.check(trigger: .manual)
        let resumedRateLimitCalls = await limitRecorder.callCount
        try results.check(resumedRateLimitCalls == 2, "requesting should resume after server retry deadline")

        let exhaustedSuite = "SimuBoard.DIYCoreHarness.UpdateExhausted.\(UUID().uuidString)"
        guard let exhaustedDefaults = UserDefaults(suiteName: exhaustedSuite) else {
            throw HarnessFailure.assertion("could not create exhausted-rate UserDefaults")
        }
        defer { exhaustedDefaults.removePersistentDomain(forName: exhaustedSuite) }
        let exhaustedClock = LockedClock(Date(timeIntervalSince1970: 2_100_000_000))
        let exhaustedReset = exhaustedClock.now().addingTimeInterval(120)
        let exhaustedRecorder = FetchRecorder([
            .success(
                .modified(
                    release: release,
                    etag: "etag-exhausted",
                    rateLimit: GitHubRateLimit(remaining: 0, resetAt: exhaustedReset)
                )
            ),
            .success(notModified),
        ])
        let exhaustedClient = GitHubReleaseClient {
            etag in try await exhaustedRecorder.fetch(etag: etag)
        }
        let exhausted = UpdateController(
            client: exhaustedClient,
            installedVersion: installed,
            defaults: exhaustedDefaults,
            now: exhaustedClock.now
        )
        await exhausted.check(trigger: .manual)
        exhaustedClock.advance(66)
        await exhausted.check(trigger: .manual)
        let proactivelyBlockedCalls = await exhaustedRecorder.callCount
        try results.check(
            proactivelyBlockedCalls == 1,
            "a successful response with zero remaining requests should honor its reset time"
        )
        exhaustedClock.advance(55)
        await exhausted.check(trigger: .manual)
        let proactivelyResumedCalls = await exhaustedRecorder.callCount
        try results.check(
            proactivelyResumedCalls == 2,
            "manual requests should resume after an exhausted successful response resets"
        )
    }

    private static func soundAssetID(_ character: Character) -> SoundPackAssetID {
        SoundPackAssetID(String(repeating: String(character), count: 64))
    }

    private static func runProcess(
        executableURL: URL,
        arguments: [String],
        currentDirectoryURL: URL,
        environmentOverrides: [String: String] = [:]
    ) throws -> (terminationStatus: Int32, stdout: String, stderr: String) {
        let process = Process()
        process.executableURL = executableURL
        process.arguments = arguments
        process.currentDirectoryURL = currentDirectoryURL
        if !environmentOverrides.isEmpty {
            process.environment = ProcessInfo.processInfo.environment.merging(environmentOverrides) { _, new in new }
        }

        let stdoutPipe = Pipe()
        let stderrPipe = Pipe()
        process.standardOutput = stdoutPipe
        process.standardError = stderrPipe

        try process.run()
        process.waitUntilExit()

        let stdoutData = stdoutPipe.fileHandleForReading.readDataToEndOfFile()
        let stderrData = stderrPipe.fileHandleForReading.readDataToEndOfFile()
        return (
            process.terminationStatus,
            String(decoding: stdoutData, as: UTF8.self),
            String(decoding: stderrData, as: UTF8.self)
        )
    }

    private static func defaultBCPLibraryRoot(forHome homeDirectory: URL) -> URL {
        homeDirectory
            .appendingPathComponent("Library/Application Support/SimuBoard", isDirectory: true)
            .appendingPathComponent("SoundPacks", isDirectory: true)
    }

    private static func bcpPackURL(in libraryRoot: URL) -> URL {
        libraryRoot.appendingPathComponent(
            "\(bcpPackUUID.uuidString.lowercased()).simuboardpack",
            isDirectory: true
        )
    }

    private static func writeSelectedProfile(
        _ value: String,
        domain: String,
        homeDirectory: URL
    ) throws {
        let result = try runProcess(
            executableURL: URL(fileURLWithPath: "/usr/bin/defaults"),
            arguments: ["write", domain, "selectedProfile", "-string", value],
            currentDirectoryURL: homeDirectory,
            environmentOverrides: ["HOME": homeDirectory.path]
        )
        guard result.terminationStatus == 0 else {
            throw HarnessFailure.assertion("defaults write failed: \(result.stderr)")
        }
    }

    private static func readSelectedProfile(
        domain: String,
        homeDirectory: URL
    ) throws -> String? {
        let result = try runProcess(
            executableURL: URL(fileURLWithPath: "/usr/bin/defaults"),
            arguments: ["read", domain, "selectedProfile"],
            currentDirectoryURL: homeDirectory,
            environmentOverrides: ["HOME": homeDirectory.path]
        )
        guard result.terminationStatus == 0 else { return nil }
        return result.stdout.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func iso8601Date(_ value: String) -> Date {
        let formatter = ISO8601DateFormatter()
        guard let date = formatter.date(from: value) else {
            preconditionFailure("invalid ISO-8601 fixture: \(value)")
        }
        return date
    }

    private static func installBCPFixturePack(
        scriptURL: URL,
        assetRoot: URL,
        libraryRoot: URL,
        projectRoot: URL,
        installTime: String
    ) throws -> URL {
        let result = try runProcess(
            executableURL: scriptURL,
            arguments: [assetRoot.path, libraryRoot.path],
            currentDirectoryURL: projectRoot.appendingPathComponent("SimuBoardMac", isDirectory: true),
            environmentOverrides: [
                "SIMUBOARD_INSTALLER_NOW": installTime,
            ]
        )
        guard result.terminationStatus == 0 else {
            throw HarnessFailure.assertion(
                "fixture BCP install failed at \(installTime): \(result.stderr)"
            )
        }
        return bcpPackURL(in: libraryRoot)
    }

    private static func setManifestTimestamps(
        at packURL: URL,
        createdAt: Date,
        modifiedAt: Date
    ) throws {
        var manifest = try SoundPackPackageValidator.validatePackage(at: packURL)
        manifest.createdAt = createdAt
        manifest.modifiedAt = modifiedAt
        try SoundPackCoding.encode(manifest).write(
            to: packURL.appendingPathComponent("manifest.json"),
            options: .atomic
        )
    }

    private static func rewriteManifestJSONObject(
        at packURL: URL,
        mutate: (inout [String: Any]) throws -> Void
    ) throws {
        let manifestURL = packURL.appendingPathComponent("manifest.json")
        let data = try Data(contentsOf: manifestURL, options: [.mappedIfSafe])
        guard var manifest = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw HarnessFailure.assertion("manifest fixture must decode as a JSON object")
        }
        try mutate(&manifest)
        let rewritten = try JSONSerialization.data(
            withJSONObject: manifest,
            options: [.prettyPrinted, .sortedKeys]
        )
        try rewritten.write(to: manifestURL, options: .atomic)
    }

    private static func installFixturePack(
        in libraryRoot: URL,
        packID: UUID,
        name: String,
        assetVariant: Int,
        createdAt: Date,
        modifiedAt: Date,
        workingRoot: URL
    ) async throws -> URL {
        let assetURL = workingRoot.appendingPathComponent(
            "fixture-\(packID.uuidString)-\(assetVariant).wav"
        )
        try makePCM16MonoFixture(
            at: assetURL,
            sampleRate: 48_000,
            duration: 0.072,
            frequency: 160 + Double(assetVariant * 17),
            peakAmplitude: 0.28
        )
        let info = try AudioImportService.validateNormalizedAudio(at: assetURL)
        let sha256 = try SoundPackFileUtilities.sha256(of: assetURL)
        let assetID = SoundPackAssetID(sha256)
        let manifest = SoundPackManifest(
            id: packID,
            name: name,
            family: "Legacy",
            tone: "Fixture",
            baseProfileID: SwitchProfile.holyPanda.rawValue,
            press: SoundPackPhaseAssignments(generic: assetID),
            release: SoundPackPhaseAssignments(generic: assetID),
            assets: [
                assetID.rawValue: SoundPackAudioAsset(
                    id: assetID,
                    relativePath: "assets/\(assetID.rawValue).wav",
                    sha256: assetID.rawValue,
                    originalFilename: "fixture.wav",
                    durationSeconds: info.durationSeconds,
                    sampleRate: info.sampleRate,
                    channelCount: info.channelCount,
                    byteCount: info.byteCount
                )
            ]
        )
        let library = SoundPackLibrary(rootURL: libraryRoot, builtInDescriptors: [])
        _ = try await library.save(
            manifest: manifest,
            assetFiles: [assetID: assetURL]
        )
        let packURL = await library.packURL(id: packID)
        var installedManifest = try SoundPackPackageValidator.validatePackage(at: packURL)
        installedManifest.createdAt = createdAt
        installedManifest.modifiedAt = modifiedAt
        let manifestData = try SoundPackCoding.encode(installedManifest)
        try manifestData.write(
            to: packURL.appendingPathComponent("manifest.json"),
            options: .atomic
        )
        return packURL
    }

    private static func corruptOneBCPAsset(at packURL: URL) throws {
        let manifest = try SoundPackPackageValidator.validatePackage(at: packURL)
        guard let asset = manifest.assets.values.sorted(by: { $0.relativePath < $1.relativePath }).first else {
            throw HarnessFailure.assertion("fixture BCP pack must contain at least one asset")
        }
        let assetURL = packURL.appendingPathComponent(asset.relativePath)
        try makePCM16MonoFixture(
            at: assetURL,
            sampleRate: 48_000,
            duration: min(asset.durationSeconds + 0.008, 0.16),
            frequency: 913,
            peakAmplitude: 0.22
        )
    }

    private static func snapshot(packAt packURL: URL) throws -> PackSnapshot {
        let manifestData = try Data(
            contentsOf: packURL.appendingPathComponent("manifest.json"),
            options: [.mappedIfSafe]
        )
        let assetURLs = try FileManager.default.contentsOfDirectory(
            at: packURL.appendingPathComponent("assets", isDirectory: true),
            includingPropertiesForKeys: nil,
            options: [.skipsHiddenFiles]
        ).filter { $0.pathExtension.lowercased() == "wav" }
        let assetHashes = try Dictionary(
            uniqueKeysWithValues: assetURLs.map { url in
                (url.lastPathComponent, try SoundPackFileUtilities.sha256(of: url))
            }
        )
        return PackSnapshot(manifestData: manifestData, assetHashes: assetHashes)
    }

    private static func fakeAsset(id: SoundPackAssetID) -> SoundPackAudioAsset {
        SoundPackAudioAsset(
            id: id,
            relativePath: "assets/\(id.rawValue).wav",
            sha256: id.rawValue,
            durationSeconds: 0.1,
            byteCount: 9_644
        )
    }

    private static func makeBCPInstallerFixtureAssets(
        at root: URL,
        variantOffset: Int
    ) throws {
        let sampleNames = [
            "GENERIC_R0",
            "GENERIC_R0_ALT",
            "GENERIC_R1",
            "GENERIC_R1_ALT",
            "GENERIC_R2",
            "GENERIC_R2_ALT",
            "GENERIC_R3",
            "GENERIC_R3_ALT",
            "GENERIC_R4",
            "GENERIC_R4_ALT",
            "SHIFT",
            "BACKSPACE",
            "ENTER",
            "SPACE",
        ]
        let phases = KeySoundPhase.allCases
        var sampleIndex = 0
        for phase in phases {
            for sampleName in sampleNames {
                let fileURL = root
                    .appendingPathComponent(phase.rawValue, isDirectory: true)
                    .appendingPathComponent("\(sampleName).wav")
                let frequency = 220.0 + Double(sampleIndex * 29 + variantOffset * 11)
                let duration = 0.040 + Double((sampleIndex + variantOffset) % 5) * 0.008
                try makePCM16MonoFixture(
                    at: fileURL,
                    sampleRate: 48_000,
                    duration: duration,
                    frequency: frequency,
                    peakAmplitude: 0.35
                )
                sampleIndex += 1
            }
        }
    }

    private static func makePCM16MonoFixture(
        at url: URL,
        sampleRate: Int,
        duration: Double,
        frequency: Double,
        peakAmplitude: Double
    ) throws {
        let frameCount = max(Int((Double(sampleRate) * duration).rounded()), 32)
        var samples = [Int16](repeating: 0, count: frameCount)
        let denominator = Double(max(frameCount - 1, 1))
        for index in 0..<frameCount {
            let envelope = sin(Double.pi * Double(index) / denominator)
            let sample = sin(2 * Double.pi * frequency * Double(index) / Double(sampleRate))
                * envelope
                * peakAmplitude
            let clipped = max(-1.0, min(1.0, sample))
            samples[index] = Int16((clipped * Double(Int16.max)).rounded())
        }
        samples[0] = 0
        samples[frameCount - 1] = 0

        let dataByteCount = samples.count * MemoryLayout<Int16>.size
        var data = Data()
        data.reserveCapacity(44 + dataByteCount)
        data.append(contentsOf: "RIFF".utf8)
        data.appendUInt32LE(UInt32(36 + dataByteCount))
        data.append(contentsOf: "WAVE".utf8)
        data.append(contentsOf: "fmt ".utf8)
        data.appendUInt32LE(16)
        data.appendUInt16LE(1)
        data.appendUInt16LE(1)
        data.appendUInt32LE(UInt32(sampleRate))
        data.appendUInt32LE(UInt32(sampleRate * MemoryLayout<Int16>.size))
        data.appendUInt16LE(UInt16(MemoryLayout<Int16>.size))
        data.appendUInt16LE(16)
        data.append(contentsOf: "data".utf8)
        data.appendUInt32LE(UInt32(dataByteCount))
        for sample in samples {
            data.appendUInt16LE(UInt16(bitPattern: sample))
        }

        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try data.write(to: url, options: .atomic)
    }

    private static func makeStereoFixture(
        at url: URL,
        sampleRate: Double,
        duration: Double
    ) throws {
        guard let format = AVAudioFormat(
            standardFormatWithSampleRate: sampleRate,
            channels: 2
        ) else {
            throw HarnessFailure.assertion("could not make source audio format")
        }
        let frameCount = AVAudioFrameCount((sampleRate * duration).rounded())
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frameCount),
              let channels = buffer.floatChannelData else {
            throw HarnessFailure.assertion("could not make source audio buffer")
        }
        buffer.frameLength = frameCount
        for frame in 0..<Int(frameCount) {
            let value = Float(sin(2 * Double.pi * 440 * Double(frame) / sampleRate) * 0.25)
            channels[0][frame] = value
            channels[1][frame] = -value * 0.75
        }
        var outputSettings = format.settings
        outputSettings[AVLinearPCMIsNonInterleaved] = false
        let file = try AVAudioFile(
            forWriting: url,
            settings: outputSettings,
            commonFormat: .pcmFormatFloat32,
            interleaved: false
        )
        try file.write(from: buffer)
    }

    private static func makeSleepingExecutable(at url: URL, pidFileURL: URL) throws {
        let quotedPIDPath = pidFileURL.path.replacingOccurrences(of: "'", with: "'\\''")
        let script = """
        #!/bin/sh
        printf '%s' "$$" > '\(quotedPIDPath)'
        exec /bin/sleep 30
        """
        try script.write(to: url, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
    }

    private static func readProcessIdentifier(at url: URL) throws -> pid_t {
        let contents = try String(contentsOf: url, encoding: .utf8)
        guard let identifier = pid_t(contents.trimmingCharacters(in: .whitespacesAndNewlines)),
              identifier > 0 else {
            throw HarnessFailure.assertion("invalid process identifier fixture")
        }
        return identifier
    }

    private static func processExists(_ identifier: pid_t) -> Bool {
        Darwin.kill(identifier, 0) == 0
    }

    private static func waitForFile(at url: URL, timeoutSeconds: TimeInterval) async -> Bool {
        let deadline = ProcessInfo.processInfo.systemUptime + timeoutSeconds
        while ProcessInfo.processInfo.systemUptime < deadline {
            if FileManager.default.fileExists(atPath: url.path) { return true }
            try? await Task.sleep(for: .milliseconds(10))
        }
        return FileManager.default.fileExists(atPath: url.path)
    }
}

private extension Data {
    mutating func appendUInt16LE(_ value: UInt16) {
        var littleEndian = value.littleEndian
        Swift.withUnsafeBytes(of: &littleEndian) { append(contentsOf: $0) }
    }

    mutating func appendUInt32LE(_ value: UInt32) {
        var littleEndian = value.littleEndian
        Swift.withUnsafeBytes(of: &littleEndian) { append(contentsOf: $0) }
    }
}
