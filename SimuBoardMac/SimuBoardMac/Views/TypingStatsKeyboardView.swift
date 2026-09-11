import AppKit
import Foundation
import SwiftUI

private enum TypingKeyCountScope: String, CaseIterable, Identifiable {
    case today = "今日"
    case allTime = "累计"

    var id: Self { self }
}

private struct TypingStatsKeyboardPresentation: Equatable {
    static let layout = KeyboardLayoutCatalog.ansiTKL
    static let visualLayout = KeyboardVisualLayoutCatalog.magicKeyboardANSI
    static let extendedRows = KeyboardExtendedLayoutCatalog.rows
    private static let knownKeys = layout.keys + extendedRows.flatMap(\.keys)
    private static let knownKeysByCode = Dictionary(
        uniqueKeysWithValues: knownKeys.map { ($0.keyCode, $0) }
    )
    static let unplacedLayoutKeys = layout.keys.filter { !visualLayout.keyIDs.contains($0.id) }

    let counts: [UInt16: Int64]
    let totalPresses: Int64
    let heatScale: TypingHeatmapScale
    let otherKeys: [KeyboardKeyDescriptor]

    init(snapshot: TypingStatsSnapshot, scope: TypingKeyCountScope) {
        switch scope {
        case .today:
            counts = snapshot.todayKeyCounts
        case .allTime:
            counts = snapshot.allTimeKeyCounts
        }

        totalPresses = counts.values.reduce(0, +)
        heatScale = TypingHeatmapScale(values: counts.values.map(Double.init))
        otherKeys = counts.keys
            .filter { Self.knownKeysByCode[$0] == nil }
            .sorted()
            .map { keyCode in
                KeyboardKeyDescriptor(
                    id: KeyboardKeyID("stats.other.\(keyCode)"),
                    keyCode: keyCode,
                    label: L10n.format("键码 %@", "\(keyCode)"),
                    row: .r4
                )
            }
    }
}

@MainActor
struct TypingStatsKeyboardView: View {
    let snapshot: TypingStatsSnapshot
    @State private var scope: TypingKeyCountScope = .today
    @State private var showsExtendedKeys = false

    private var exposesEmptyKeysToAssistiveTech: Bool {
        NSWorkspace.shared.isVoiceOverEnabled || NSWorkspace.shared.isSwitchControlEnabled
    }

    private var presentation: TypingStatsKeyboardPresentation {
        TypingStatsKeyboardPresentation(snapshot: snapshot, scope: scope)
    }

    var body: some View {
        let presentation = presentation

        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                VStack(alignment: .leading, spacing: 16) {
                    HStack(alignment: .top, spacing: 20) {
                        BattutaSectionHeading(
                            "键盘热力图",
                            subtitle: "Apple 紧凑型 Mac 键盘 · US ANSI · 14.5U",
                            symbol: "square.grid.3x3.fill"
                        )

                        Spacer()

                        VStack(alignment: .trailing, spacing: 10) {
                            Picker("统计范围", selection: $scope) {
                                ForEach(TypingKeyCountScope.allCases) { scope in
                                    Text(L10n.tr(scope.rawValue)).tag(scope)
                                }
                            }
                            .labelsHidden()
                            .pickerStyle(.segmented)
                            .frame(width: 150)

                            heatLegend(scale: presentation.heatScale)
                        }
                    }

                    Divider()

                    if presentation.totalPresses == 0 {
                        Label(
                            scope == .today
                                ? "今天还没有按键记录；开始输入后键盘会逐键点亮。"
                                : "还没有累计按键记录；开始输入后键盘会逐键点亮。",
                            systemImage: "keyboard"
                        )
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }

                    TypingStatsKeyboardHeatmap(
                        counts: presentation.counts,
                        heatScale: presentation.heatScale,
                        exposesEmptyKeyMetadata: exposesEmptyKeysToAssistiveTech
                    )
                    .equatable()
                    .padding(.vertical, 4)
                    .accessibilityHidden(presentation.totalPresses == 0)

                    Divider()

                    DisclosureGroup(isExpanded: $showsExtendedKeys) {
                        TypingStatsKeyboardExtendedSection(
                            counts: presentation.counts,
                            heatScale: presentation.heatScale,
                            otherKeys: presentation.otherKeys,
                            exposesEmptyMetadata: exposesEmptyKeysToAssistiveTech
                        )
                        .equatable()
                            .padding(.top, 12)
                    } label: {
                        Text("外接键盘与扩展按键")
                            .font(.subheadline.weight(.medium))
                    }

                    HStack(alignment: .top, spacing: 8) {
                        Image(systemName: "info.circle")
                        Text(
                            "按下计数包含 Shift、Command、回车、退格和方向键；长按产生的系统自动重复不增加物理按下次数。"
                        )
                    }
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                }
                .padding(BattutaVisualStyle.cardPadding)
                .battutaPanel()
            }
            .padding(20)
            .frame(maxWidth: 1_080)
            .frame(maxWidth: .infinity)
        }
    }

    private func heatLegend(scale: TypingHeatmapScale) -> some View {
        let exactDescription = L10n.format(
            "连续颜色图例，从 %@ 到 %@",
            scale.hasValues
                ? L10n.format("低 %@ ", statsCount(Int64(scale.low.rounded())))
                    .trimmingCharacters(in: .whitespaces)
                : L10n.tr("低 0"),
            scale.hasValues
                ? L10n.format("高 ≥%@", statsCount(Int64(scale.high.rounded())))
                : L10n.tr("高 0")
        )

        return BattutaHeatmapLegend(
            leadingLabel: scale.hasValues
                ? L10n.format("低 %@ ", statsCompactKeyCount(Int64(scale.low.rounded())))
                    .trimmingCharacters(in: .whitespaces)
                : L10n.tr("低 0"),
            trailingLabel: scale.hasValues
                ? L10n.format("高 ≥%@", statsCompactKeyCount(Int64(scale.high.rounded())))
                : L10n.tr("高 0")
        )
        .help(exactDescription)
        .accessibilityLabel(exactDescription)
    }
}

@MainActor
private struct TypingStatsKeyboardHeatmap: View, Equatable {
    let counts: [UInt16: Int64]
    let heatScale: TypingHeatmapScale
    let exposesEmptyKeyMetadata: Bool

    var body: some View {
        FittedTypingStatsKeyboard(
            counts: counts,
            heatScale: heatScale,
            exposesEmptyKeyMetadata: exposesEmptyKeyMetadata
        )
    }
}

private struct TypingStatsKeyboardCanvasEntry: Identifiable, Hashable {
    let renderedKey: MacKeyboardRenderedKey
    let frame: CGRect

    var id: String { renderedKey.id }
}

private enum TypingStatsKeyboardCanvasLayout {
    static let baseMetrics = MacKeyboardLayoutMetrics.typingStats
    static let baseSize = baseMetrics.canvasSize(for: TypingStatsKeyboardPresentation.visualLayout)
    static let entries: [TypingStatsKeyboardCanvasEntry] = {
        let descriptorsByID = Dictionary(
            uniqueKeysWithValues: TypingStatsKeyboardPresentation.layout.keys.map { ($0.id, $0) }
        )
        return TypingStatsKeyboardPresentation.visualLayout.placements.map { placement in
            let descriptor = placement.content.keyID.flatMap { descriptorsByID[$0] }
            return TypingStatsKeyboardCanvasEntry(
                renderedKey: MacKeyboardRenderedKey(
                    placement: placement,
                    descriptor: descriptor
                ),
                frame: baseMetrics.frame(for: placement)
            )
        }
    }()
}

@MainActor
private struct TypingStatsKeyboardExtendedSection: View, Equatable {
    let counts: [UInt16: Int64]
    let heatScale: TypingHeatmapScale
    let otherKeys: [KeyboardKeyDescriptor]
    let exposesEmptyMetadata: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if !TypingStatsKeyboardPresentation.unplacedLayoutKeys.isEmpty {
                Text("额外修饰键")
                    .font(.caption.weight(.medium))
                    .foregroundStyle(.secondary)
                HStack(spacing: 4) {
                    ForEach(TypingStatsKeyboardPresentation.unplacedLayoutKeys) { key in
                        TypingStatsKeycap(
                            key: key,
                            count: counts[key.keyCode, default: 0],
                            heatScale: heatScale,
                            exposesEmptyMetadata: exposesEmptyMetadata
                        )
                    }
                }
            }

            Text("导航、功能、数字键盘与国际键")
                .font(.caption.weight(.medium))
                .foregroundStyle(.secondary)
                .padding(.top, 4)

            ForEach(TypingStatsKeyboardPresentation.extendedRows) { row in
                HStack(spacing: 4) {
                    ForEach(row.keys) { key in
                        TypingStatsKeycap(
                            key: key,
                            count: counts[key.keyCode, default: 0],
                            heatScale: heatScale,
                            exposesEmptyMetadata: exposesEmptyMetadata
                        )
                    }
                }
            }

            if !otherKeys.isEmpty {
                Text("其他已识别键")
                    .font(.caption.weight(.medium))
                    .foregroundStyle(.secondary)
                    .padding(.top, 4)
                HStack(spacing: 4) {
                    ForEach(otherKeys) { key in
                        TypingStatsKeycap(
                            key: key,
                            count: counts[key.keyCode, default: 0],
                            heatScale: heatScale,
                            exposesEmptyMetadata: exposesEmptyMetadata
                        )
                    }
                }
            }
        }
    }
}

@MainActor
private struct FittedTypingStatsKeyboard: View {
    let counts: [UInt16: Int64]
    let heatScale: TypingHeatmapScale
    let exposesEmptyKeyMetadata: Bool

    var body: some View {
        let baseSize = TypingStatsKeyboardCanvasLayout.baseSize

        GeometryReader { proxy in
            let scale = max(0.1, proxy.size.width / baseSize.width)
            let scaledSize = CGSize(
                width: baseSize.width * scale,
                height: baseSize.height * scale
            )
            let interactiveEntries = TypingStatsKeyboardCanvasLayout.entries.filter {
                keyCount(for: $0) > 0 || exposesEmptyKeyMetadata
            }

            ZStack(alignment: .topLeading) {
                Canvas(opaque: false, colorMode: .nonLinear, rendersAsynchronously: true) {
                    context,
                    _ in
                    context.scaleBy(x: scale, y: scale)
                    for entry in TypingStatsKeyboardCanvasLayout.entries {
                        draw(entry, in: &context)
                    }
                }
                .frame(width: scaledSize.width, height: scaledSize.height)
                .accessibilityHidden(true)

                ForEach(interactiveEntries) { entry in
                    interactionHitTarget(for: entry, scale: scale)
                }
            }
            .frame(
                width: scaledSize.width,
                height: scaledSize.height,
                alignment: .topLeading
            )
        }
        .aspectRatio(baseSize.width / baseSize.height, contentMode: .fit)
    }

    private func keyCount(for entry: TypingStatsKeyboardCanvasEntry) -> Int64 {
        entry.renderedKey.descriptor.map { counts[$0.keyCode, default: 0] } ?? 0
    }

    @ViewBuilder
    private func interactionHitTarget(
        for entry: TypingStatsKeyboardCanvasEntry,
        scale: CGFloat
    ) -> some View {
        let count = keyCount(for: entry)
        let frame = entry.frame
        let scaledFrame = CGRect(
            x: frame.minX * scale,
            y: frame.minY * scale,
            width: frame.width * scale,
            height: frame.height * scale
        )

        let content = Color.clear
            .frame(width: scaledFrame.width, height: scaledFrame.height)
            .contentShape(Rectangle())
            .position(x: scaledFrame.midX, y: scaledFrame.midY)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(accessibilityLabel(for: entry))
            .accessibilityValue(accessibilityValue(for: entry, count: count))

        if count > 0 {
            content
                .help(helpText(for: entry, count: count))
        } else {
            content
        }
    }

    private func accessibilityLabel(for entry: TypingStatsKeyboardCanvasEntry) -> String {
        if let key = entry.renderedKey.descriptor {
            return key.label
        }
        return L10n.tr("锁定或 Touch ID 键")
    }

    private func accessibilityValue(
        for entry: TypingStatsKeyboardCanvasEntry,
        count: Int64
    ) -> String {
        if entry.renderedKey.descriptor != nil {
            return L10n.format("%@ 次", statsCount(count))
        }
        return L10n.tr("系统不提供普通按键计数")
    }

    private func helpText(
        for entry: TypingStatsKeyboardCanvasEntry,
        count: Int64
    ) -> String {
        if let key = entry.renderedKey.descriptor {
            return L10n.format("%@：%@ 次", key.label, statsCount(count))
        }
        return L10n.tr("锁定或 Touch ID 键：系统不提供普通按键事件")
    }

    private func draw(
        _ entry: TypingStatsKeyboardCanvasEntry,
        in context: inout GraphicsContext
    ) {
        let count = keyCount(for: entry)
        let frame = entry.frame
        let keyPath = Path(roundedRect: frame, cornerRadius: 7, style: .continuous)

        if count > 0 {
            let shadowPath = Path(
                roundedRect: frame.offsetBy(dx: 0, dy: 1),
                cornerRadius: 7,
                style: .continuous
            )
            context.fill(shadowPath, with: .color(BattutaVisualStyle.keyboardShadow))
        }

        context.fill(keyPath, with: .color(BattutaVisualStyle.surface))
        context.fill(keyPath, with: .color(keycapTint(for: count)))
        context.stroke(keyPath, with: .color(strokeColor(for: count)), lineWidth: 1)

        if let key = entry.renderedKey.descriptor {
            drawKeyText(key: key, count: count, in: frame, context: &context)
        } else {
            drawDecoration(entry.renderedKey, in: frame, context: &context)
        }
    }

    private func drawKeyText(
        key: KeyboardKeyDescriptor,
        count: Int64,
        in frame: CGRect,
        context: inout GraphicsContext
    ) {
        let foreground = keycapForeground(for: count)
        let countColor = count > 0 ? foreground : Color.secondary.opacity(0.48)

        if frame.height < 30 {
            drawResolvedText(
                Text(key.label).font(key.widthUnits > 1.2 ? .caption2 : .caption),
                color: foreground,
                at: CGPoint(x: frame.minX + 3, y: frame.midY),
                anchor: .leading,
                in: &context
            )
            drawResolvedText(
                Text(statsCompactKeyCount(count))
                    .font(.caption2.weight(count > 0 ? .semibold : .regular))
                    .monospacedDigit(),
                color: countColor,
                at: CGPoint(x: frame.maxX - 3, y: frame.midY),
                anchor: .trailing,
                in: &context
            )
        } else {
            drawResolvedText(
                Text(key.label).font(key.widthUnits > 1.2 ? .caption2 : .caption),
                color: foreground,
                at: CGPoint(x: frame.midX, y: frame.midY - 7),
                anchor: .center,
                in: &context
            )
            drawResolvedText(
                Text(statsCompactKeyCount(count))
                    .font(.caption2.weight(count > 0 ? .semibold : .regular))
                    .monospacedDigit(),
                color: countColor,
                at: CGPoint(x: frame.midX, y: frame.midY + 8),
                anchor: .center,
                in: &context
            )
        }
    }

    private func drawDecoration(
        _ renderedKey: MacKeyboardRenderedKey,
        in frame: CGRect,
        context: inout GraphicsContext
    ) {
        if let systemImage = renderedKey.systemImage {
            drawResolvedText(
                Text(Image(systemName: systemImage)).font(.caption),
                color: .primary,
                at: CGPoint(x: frame.midX, y: frame.midY - 6),
                anchor: .center,
                in: &context
            )
        } else {
            drawResolvedText(
                Text(renderedKey.label).font(.caption2),
                color: .primary,
                at: CGPoint(x: frame.midX, y: frame.midY - 6),
                anchor: .center,
                in: &context
            )
        }

        drawResolvedText(
            Text("—").font(.caption2),
            color: Color.secondary.opacity(0.55),
            at: CGPoint(x: frame.midX, y: frame.midY + 8),
            anchor: .center,
            in: &context
        )
    }

    private func drawResolvedText(
        _ text: Text,
        color: Color,
        at point: CGPoint,
        anchor: UnitPoint,
        in context: inout GraphicsContext
    ) {
        var resolved = context.resolve(text)
        resolved.shading = .color(color)
        context.draw(resolved, at: point, anchor: anchor)
    }

    private func keycapTint(for count: Int64) -> Color {
        guard count > 0 else { return .clear }
        return BattutaHeatmapPalette.keyboardFillColor(
            at: keycapIntensity(for: count)
        )
    }

    private func strokeColor(for count: Int64) -> Color {
        guard count > 0 else { return BattutaVisualStyle.separator.opacity(0.55) }
        return BattutaHeatmapPalette.keyboardStrokeColor(
            at: keycapIntensity(for: count)
        )
    }

    private func keycapForeground(for count: Int64) -> Color {
        guard count > 0 else { return .primary }
        return BattutaVisualStyle.activeKeyForeground
    }

    private func keycapIntensity(for count: Int64) -> Double {
        heatScale.normalized(Double(count))
    }
}

@MainActor
private struct TypingStatsKeycap: View {
    let key: KeyboardKeyDescriptor
    let count: Int64
    let heatScale: TypingHeatmapScale
    let size: CGSize?
    let exposesEmptyMetadata: Bool

    init(
        key: KeyboardKeyDescriptor,
        count: Int64,
        heatScale: TypingHeatmapScale,
        size: CGSize? = nil,
        exposesEmptyMetadata: Bool = false
    ) {
        self.key = key
        self.count = count
        self.heatScale = heatScale
        self.size = size
        self.exposesEmptyMetadata = exposesEmptyMetadata
    }

    private var width: CGFloat {
        max(38, CGFloat(key.widthUnits) * 38 + CGFloat(max(0, key.widthUnits - 1)) * 4)
    }

    private var resolvedSize: CGSize {
        size ?? CGSize(width: width, height: 50)
    }

    private var intensity: Double {
        heatScale.normalized(Double(count))
    }

    var body: some View {
        let content = Group {
            if resolvedSize.height < 30 {
                HStack(spacing: 3) {
                    keyLabel
                    keyCount
                }
                .padding(.horizontal, 3)
            } else {
                VStack(spacing: 3) {
                    keyLabel
                    keyCount
                }
                .padding(.horizontal, 4)
            }
        }
        .frame(
            width: resolvedSize.width,
            height: resolvedSize.height
        )
        .background {
            ZStack {
                RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .fill(BattutaVisualStyle.surface)
                RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .fill(keycapTint)
            }
        }
        .overlay(
            RoundedRectangle(cornerRadius: 7, style: .continuous)
                .stroke(
                    count > 0
                        ? BattutaHeatmapPalette.keyboardStrokeColor(at: intensity)
                        : BattutaVisualStyle.separator.opacity(0.55)
                )
        )
        .shadow(
            color: count > 0 ? BattutaVisualStyle.keyboardShadow : .clear,
            radius: count > 0 ? 1 : 0,
            y: count > 0 ? 1 : 0
        )

        return Group {
            if count > 0 {
                content
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel(key.label)
                    .accessibilityValue(L10n.format("%@ 次", statsCount(count)))
                    .help(L10n.format("%@：%@ 次", key.label, statsCount(count)))
            } else if exposesEmptyMetadata {
                content
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel(key.label)
                    .accessibilityValue(L10n.tr("0 次"))
            } else {
                content.accessibilityHidden(true)
            }
        }
    }

    private var keyLabel: some View {
        Text(key.label)
            .font(key.widthUnits > 1.2 ? .caption2 : .caption)
            .foregroundStyle(keycapForeground)
            .lineLimit(1)
            .minimumScaleFactor(0.6)
    }

    private var keyCount: some View {
        Text(statsCompactKeyCount(count))
            .font(.caption2.weight(count > 0 ? .semibold : .regular))
            .monospacedDigit()
            .foregroundStyle(count > 0 ? keycapForeground : Color.secondary.opacity(0.48))
            .lineLimit(1)
            .minimumScaleFactor(0.55)
    }

    private var keycapTint: Color {
        guard count > 0 else { return .clear }
        return BattutaHeatmapPalette.keyboardFillColor(at: intensity)
    }

    private var keycapForeground: Color {
        guard count > 0 else { return .primary }
        return BattutaVisualStyle.activeKeyForeground
    }
}

private func statsCompactKeyCount(_ count: Int64) -> String {
    guard count > 10_000 else { return statsCount(count) }

    let suffixes = ["K", "M", "B", "T", "Q", "E"]
    var scaled = Double(count) / 1_000
    var unitIndex = 0
    // Promote values that round to 1,000 so labels never show 1,000K or 1,000M.
    while scaled >= 999.5 && unitIndex < suffixes.count - 1 {
        scaled /= 1_000
        unitIndex += 1
    }

    return scaled.formatted(
        .number
            .grouping(.never)
            .precision(.significantDigits(1...3))
            .rounded(rule: .toNearestOrAwayFromZero)
            .locale(L10n.locale)
    ) + suffixes[unitIndex]
}
