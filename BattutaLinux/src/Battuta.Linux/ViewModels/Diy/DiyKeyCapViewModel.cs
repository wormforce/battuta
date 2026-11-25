using CommunityToolkit.Mvvm.ComponentModel;

namespace Battuta.Linux.ViewModels.Diy;

/// <summary>One drawn key cap of the DIY editor keyboard workspace. Geometry
/// is precomputed in pixels (same unit size as the statistics heatmap) so the
/// view only binds; selection state is mirrored from the shared editor VM.</summary>
public sealed partial class DiyKeyCapViewModel : ObservableObject
{
    /// <summary>Pixels per keyboard unit (1U key pitch); matches the heatmap.</summary>
    public const double UnitPixels = 40;

    public required string Id { get; init; }

    public required string Label { get; init; }

    public required Core.Input.PhysicalKeyId KeyId { get; init; }

    public required int Row { get; init; }

    public required double XUnits { get; init; }

    public required double YUnits { get; init; }

    public required double WidthUnits { get; init; }

    public required double HeightUnits { get; init; }

    [ObservableProperty]
    private bool isSelected;

    public double X => XUnits * UnitPixels + 2;

    public double Y => YUnits * UnitPixels + 2;

    public double Width => WidthUnits * UnitPixels - 4;

    public double Height => HeightUnits * UnitPixels - 4;

    public bool IsRow0 => Row == 0;

    public bool IsRow1 => Row == 1;

    public bool IsRow2 => Row == 2;

    public bool IsRow3 => Row == 3;

    public bool IsRow4 => Row == 4;
}
