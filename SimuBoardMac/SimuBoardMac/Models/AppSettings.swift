import Combine
import Foundation

enum AppAppearancePreference: String, CaseIterable, Identifiable, Sendable {
    case system
    case light
    case dark

    var id: Self { self }

    var displayNameKey: String {
        switch self {
        case .system: "跟随系统"
        case .light: "浅色"
        case .dark: "深色"
        }
    }
}

enum KeyboardVolumeCurve {
    static let currentVersion = 1
    static let legacyDefaultGain = 0.42

    static func playbackGain(for sliderPosition: Double) -> Double {
        let position = clampedUnitValue(sliderPosition)
        return position * position * position
    }

    static func sliderPosition(preservingLegacyGain legacyGain: Double) -> Double {
        Foundation.pow(clampedUnitValue(legacyGain), 1.0 / 3.0)
    }

    static func normalizedSliderPosition(_ sliderPosition: Double) -> Double {
        clampedUnitValue(sliderPosition)
    }

    private static func clampedUnitValue(_ value: Double) -> Double {
        guard value.isFinite else { return 0 }
        return min(max(value, 0), 1)
    }
}

@MainActor
final class AppSettings: ObservableObject {
    private enum Key {
        static let enabled = "enabled"
        static let selectedProfile = "selectedProfile"
        static let volume = "volume"
        static let keyboardVolumeCurveVersion = "keyboardVolumeCurveVersion"
        static let releaseSound = "releaseSound"
        static let keyRepeatSound = "keyRepeatSound"
        static let pitchVariation = "pitchVariation"
        static let pointerSoundEnabled = "pointerSoundEnabled"
        static let selectedPointerProfile = "selectedPointerProfile"
        static let pointerVolume = "pointerVolume"
        static let pointerReleaseSound = "pointerReleaseSound"
        static let typingStatsEnabled = "typingStatsEnabled"
        static let launchAtLoginEnabled = "launchAtLoginEnabled"
        static let languagePreference = "languagePreference"
        static let appearancePreference = "appearancePreference"
    }

    private let defaults: UserDefaults

    @Published var isEnabled: Bool {
        didSet { defaults.set(isEnabled, forKey: Key.enabled) }
    }

    @Published var selectedProfileID: String {
        didSet { defaults.set(selectedProfileID, forKey: Key.selectedProfile) }
    }

    @Published var volume: Double {
        didSet {
            defaults.set(volume, forKey: Key.volume)
            defaults.set(KeyboardVolumeCurve.currentVersion, forKey: Key.keyboardVolumeCurveVersion)
        }
    }

    @Published var playsReleaseSound: Bool {
        didSet { defaults.set(playsReleaseSound, forKey: Key.releaseSound) }
    }

    @Published var playsKeyRepeatSound: Bool {
        didSet { defaults.set(playsKeyRepeatSound, forKey: Key.keyRepeatSound) }
    }

    @Published var usesPitchVariation: Bool {
        didSet { defaults.set(usesPitchVariation, forKey: Key.pitchVariation) }
    }

    @Published var isPointerSoundEnabled: Bool {
        didSet { defaults.set(isPointerSoundEnabled, forKey: Key.pointerSoundEnabled) }
    }

    @Published var selectedPointerProfileID: String {
        didSet { defaults.set(selectedPointerProfileID, forKey: Key.selectedPointerProfile) }
    }

    @Published var pointerVolume: Double {
        didSet { defaults.set(pointerVolume, forKey: Key.pointerVolume) }
    }

    @Published var playsPointerReleaseSound: Bool {
        didSet { defaults.set(playsPointerReleaseSound, forKey: Key.pointerReleaseSound) }
    }

    /// Opt-in because this persists aggregate key/app activity rather than only playing sound.
    @Published var isTypingStatsEnabled: Bool {
        didSet { defaults.set(isTypingStatsEnabled, forKey: Key.typingStatsEnabled) }
    }

    @Published var isLaunchAtLoginEnabled: Bool {
        didSet { defaults.set(isLaunchAtLoginEnabled, forKey: Key.launchAtLoginEnabled) }
    }

    @Published var languagePreference: AppLanguagePreference {
        willSet { L10n.configure(for: newValue) }
        didSet { defaults.set(languagePreference.rawValue, forKey: Key.languagePreference) }
    }

    @Published var appearancePreference: AppAppearancePreference {
        didSet { defaults.set(appearancePreference.rawValue, forKey: Key.appearancePreference) }
    }

    var selectedProfile: SwitchProfile {
        get { SwitchProfile(rawValue: selectedProfileID) ?? .holyPanda }
        set { selectedProfileID = newValue.rawValue }
    }

    var selectedPointerProfile: PointerSoundProfile {
        get { PointerSoundProfile(rawValue: selectedPointerProfileID) ?? .classic }
        set { selectedPointerProfileID = newValue.rawValue }
    }

    var keyboardPlaybackGain: Double {
        KeyboardVolumeCurve.playbackGain(for: volume)
    }

    func shouldPlaySound(for event: KeyboardEvent) -> Bool {
        guard isEnabled else { return false }
        switch event.kind {
        case .keyDown: return !event.isRepeat || playsKeyRepeatSound
        case .keyUp: return playsReleaseSound
        }
    }

    func refreshSystemLanguageIfNeeded() {
        guard languagePreference == .system else { return }
        if L10n.refreshSystemLocalizationIfNeeded() {
            objectWillChange.send()
        }
    }

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        isEnabled = defaults.object(forKey: Key.enabled) as? Bool ?? true
        selectedProfileID = defaults.string(forKey: Key.selectedProfile) ?? SwitchProfile.holyPanda.rawValue
        let storedKeyboardVolume = defaults.object(forKey: Key.volume) as? Double
        let storedKeyboardVolumeCurveVersion = defaults.integer(forKey: Key.keyboardVolumeCurveVersion)
        let resolvedKeyboardVolume: Double
        if let storedKeyboardVolume {
            resolvedKeyboardVolume = storedKeyboardVolumeCurveVersion < KeyboardVolumeCurve.currentVersion
                ? KeyboardVolumeCurve.sliderPosition(preservingLegacyGain: storedKeyboardVolume)
                : KeyboardVolumeCurve.normalizedSliderPosition(storedKeyboardVolume)
        } else {
            resolvedKeyboardVolume = KeyboardVolumeCurve.sliderPosition(
                preservingLegacyGain: KeyboardVolumeCurve.legacyDefaultGain
            )
        }
        volume = resolvedKeyboardVolume
        playsReleaseSound = defaults.object(forKey: Key.releaseSound) as? Bool ?? true
        playsKeyRepeatSound = defaults.object(forKey: Key.keyRepeatSound) as? Bool ?? false
        usesPitchVariation = defaults.object(forKey: Key.pitchVariation) as? Bool ?? true
        isPointerSoundEnabled = defaults.object(forKey: Key.pointerSoundEnabled) as? Bool ?? false
        let storedPointerProfileID = defaults.string(forKey: Key.selectedPointerProfile)
            ?? PointerSoundProfile.classic.rawValue
        selectedPointerProfileID = PointerSoundProfile(rawValue: storedPointerProfileID) == nil
            ? PointerSoundProfile.classic.rawValue
            : storedPointerProfileID
        let storedPointerVolume = defaults.object(forKey: Key.pointerVolume) as? Double
        let resolvedPointerVolume = storedPointerVolume
            ?? KeyboardVolumeCurve.playbackGain(for: resolvedKeyboardVolume) * 0.65
        pointerVolume = min(max(resolvedPointerVolume, 0), 1)
        playsPointerReleaseSound = defaults.object(forKey: Key.pointerReleaseSound) as? Bool ?? true
        isTypingStatsEnabled = defaults.object(forKey: Key.typingStatsEnabled) as? Bool ?? false
        isLaunchAtLoginEnabled = defaults.object(forKey: Key.launchAtLoginEnabled) as? Bool ?? true
        let storedLanguagePreference = defaults.string(forKey: Key.languagePreference)
        languagePreference = AppLanguagePreference(rawValue: storedLanguagePreference ?? "") ?? .system
        let storedAppearancePreference = defaults.string(forKey: Key.appearancePreference)
        appearancePreference = AppAppearancePreference(rawValue: storedAppearancePreference ?? "") ?? .system
        L10n.configure(for: languagePreference)
        if selectedPointerProfileID != storedPointerProfileID {
            defaults.set(selectedPointerProfileID, forKey: Key.selectedPointerProfile)
        }
        if storedKeyboardVolume == nil || storedKeyboardVolume != resolvedKeyboardVolume {
            defaults.set(resolvedKeyboardVolume, forKey: Key.volume)
        }
        if storedKeyboardVolumeCurveVersion < KeyboardVolumeCurve.currentVersion {
            defaults.set(KeyboardVolumeCurve.currentVersion, forKey: Key.keyboardVolumeCurveVersion)
        }
        if storedPointerVolume == nil || storedPointerVolume != pointerVolume {
            defaults.set(pointerVolume, forKey: Key.pointerVolume)
        }
        if storedLanguagePreference != languagePreference.rawValue {
            defaults.set(languagePreference.rawValue, forKey: Key.languagePreference)
        }
        if storedAppearancePreference != appearancePreference.rawValue {
            defaults.set(appearancePreference.rawValue, forKey: Key.appearancePreference)
        }
    }
}
