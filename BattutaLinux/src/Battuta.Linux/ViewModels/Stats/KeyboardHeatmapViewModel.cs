using Battuta.Core.Input;
using Battuta.Windows.Stats.Visualization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Battuta.Linux.ViewModels.Stats;

/// <summary>One drawn key cell of the Linux heatmap. Geometry is precomputed
/// in pixels (unit size fixed in the VM) so the view only binds; the shading
/// and count are observable so the view updates live as the refresh loop
/// repaints.</summary>
public sealed partial class KeyboardHeatmapCellViewModel : ObservableObject
{
    /// <summary>Pixels per keyboard unit (1U key pitch) used by this view.</summary>
    public const double UnitPixels = 40;

    public required string Id { get; init; }

    public required string Label { get; init; }

    public PhysicalKeyId? KeyId { get; init; }

    public required double XUnits { get; init; }

    public required double YUnits { get; init; }

    public required double WidthUnits { get; init; }

    public required double HeightUnits { get; init; }

    [ObservableProperty]
    private long count;

    /// <summary>Adaptively normalized 0..1 intensity; 0 when uncounted.</summary>
    public double Intensity { get; private set; }

    [ObservableProperty]
    private double shadeOpacity;

    [ObservableProperty]
    private string countText = string.Empty;

    public bool IsKey => KeyId.HasValue;

    public double X => XUnits * UnitPixels;

    public double Y => YUnits * UnitPixels;

    public double Width => WidthUnits * UnitPixels - 4;

    public double Height => HeightUnits * UnitPixels - 4;

    public void Update(long count, double intensity)
    {
        Count = count;
        Intensity = intensity;
        ShadeOpacity = count > 0
            ? 0.10 + 0.90 * intensity
            : 0.06;
        CountText = count > 0 ? count.ToString() : string.Empty;
    }
}

/// <summary>
/// Binds the shared compact ANSI layout (Battuta.Core) to per-key statistics
/// counts. Intensities use <see cref="AdaptiveHeatScale"/>, the same mapping
/// the Windows heatmap uses, so identical data produces identical shading.
/// Decorative placements (no key id) stay dim and never receive counts.
/// </summary>
public sealed class KeyboardHeatmapViewModel
{
    public KeyboardHeatmapViewModel(KeyboardVisualLayout layout)
    {
        LayoutId = layout.Id;
        Cells = layout.Placements
            .Select(placement => new KeyboardHeatmapCellViewModel
            {
                Id = placement.Id,
                Label = placement.Label,
                KeyId = placement.KeyId,
                XUnits = placement.XUnits,
                YUnits = placement.Row + (placement.VerticalSlot == KeyboardVisualVerticalSlot.LowerHalf ? 0.5d : 0d),
                WidthUnits = placement.WidthUnits,
                HeightUnits = placement.VerticalSlot == KeyboardVisualVerticalSlot.Full
                    ? 1d
                    : 0.5d,
            })
            .ToArray();
    }

    public string LayoutId { get; }

    public IReadOnlyList<KeyboardHeatmapCellViewModel> Cells { get; }

    /// <summary>Maximum observed count in the last update, for the legend.</summary>
    public long MaxCount { get; private set; }

    /// <summary>Recomputes every cell from the given key counts. Count
    /// entries that no layout cell displays do not affect the legend.</summary>
    public void UpdateCounts(IReadOnlyDictionary<PhysicalKeyId, long> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        var heat = AdaptiveHeatScale.FromNonZero(counts.Values);
        foreach (var cell in Cells)
        {
            var count = cell.KeyId is { } keyId && counts.TryGetValue(keyId, out var value)
                ? value
                : 0L;
            cell.Update(count, heat.Normalize(count));
        }

        MaxCount = Cells
            .Where(cell => cell.IsKey)
            .Select(cell => cell.Count)
            .DefaultIfEmpty(0)
            .Max();
    }
}
