using Battuta.Core.Input;
using Battuta.Linux.ViewModels.Stats;
using Xunit;

namespace Battuta.Linux.Tests.ViewModels;

public sealed class KeyboardHeatmapViewModelTests
{
    private static KeyboardHeatmapViewModel Create() =>
        new(WindowsAnsiVisualLayoutCatalog.CompactAnsi);

    [Fact]
    public void CellsMirrorLayoutGeometry()
    {
        var layout = WindowsAnsiVisualLayoutCatalog.CompactAnsi;
        var heatmap = new KeyboardHeatmapViewModel(layout);

        Assert.Equal(layout.Placements.Count, heatmap.Cells.Count);
        foreach (var (cell, placement) in heatmap.Cells.Zip(layout.Placements))
        {
            Assert.Equal(placement.Id, cell.Id);
            Assert.Equal(placement.KeyId, cell.KeyId);
            Assert.Equal(placement.XUnits * KeyboardHeatmapCellViewModel.UnitPixels, cell.X, 6);
            Assert.Equal(
                placement.VerticalSlot == KeyboardVisualVerticalSlot.Full ? 1d : 0.5d,
                cell.HeightUnits);
        }
    }

    [Fact]
    public void UpdateCountsNormalizesAdaptively()
    {
        var heatmap = Create();
        var keyA = PhysicalKeys.KeyA;
        var keyB = PhysicalKeys.KeyB;

        heatmap.UpdateCounts(new Dictionary<PhysicalKeyId, long>
        {
            [keyA] = 10,
            [keyB] = 1000,
        });

        var cellA = heatmap.Cells.Single(cell => cell.KeyId == keyA);
        var cellB = heatmap.Cells.Single(cell => cell.KeyId == keyB);
        var keyC = PhysicalKeys.KeyC;
        var cellC = heatmap.Cells.Single(cell => cell.KeyId == keyC);

        // Adaptive small-sample semantics (Windows parity): the scale spans
        // the minimum visible non-zero value (intensity 0) to the maximum
        // (intensity 1); the 10 still gets a visible fill and count text.
        Assert.Equal(0d, cellA.Intensity);
        Assert.Equal(1d, cellB.Intensity);
        Assert.Equal(0d, cellC.Intensity);
        Assert.Equal(0.10, cellA.ShadeOpacity, 6);
        Assert.Equal(0.06, cellC.ShadeOpacity, 6);
        Assert.Equal(1000, heatmap.MaxCount);
        Assert.Equal("10", cellA.CountText);
        Assert.Equal(string.Empty, cellC.CountText);
    }

    [Fact]
    public void IntensityMonotonicallyFollowsCount()
    {
        var heatmap = Create();
        var keys = new[]
        {
            PhysicalKeys.KeyA,
            PhysicalKeys.KeyB,
            PhysicalKeys.KeyC,
            PhysicalKeys.KeyD,
        };

        heatmap.UpdateCounts(keys
            .Select((key, index) => (key, count: (long)((index + 1) * 10)))
            .ToDictionary(pair => pair.key, pair => pair.count));

        var intensities = keys
            .Select(key => heatmap.Cells.Single(cell => cell.KeyId == key).Intensity)
            .ToArray();
        Assert.True(intensities.SequenceEqual(intensities.OrderBy(value => value)));
        Assert.All(intensities, value => Assert.InRange(value, 0, 1));
    }

    [Fact]
    public void EveryKeyCellShowsCountTextWhenCounted()
    {
        var heatmap = Create();

        heatmap.UpdateCounts(new Dictionary<PhysicalKeyId, long>
        {
            [PhysicalKeys.KeyA] = 1,
            [PhysicalKeys.KeyZ] = 500,
        });

        Assert.Equal(500, heatmap.MaxCount);
        var cellA = heatmap.Cells.Single(cell => cell.KeyId == PhysicalKeys.KeyA);
        var cellZ = heatmap.Cells.Single(cell => cell.KeyId == PhysicalKeys.KeyZ);
        Assert.Equal("1", cellA.CountText);
        Assert.Equal(1d, cellZ.Intensity);
        Assert.Equal(0d, cellA.Intensity);
    }

    [Fact]
    public void EmptyCountsClearsEverything()
    {
        var heatmap = Create();
        heatmap.UpdateCounts(new Dictionary<PhysicalKeyId, long>
        {
            [PhysicalKeys.KeyA] = 5,
        });

        heatmap.UpdateCounts(new Dictionary<PhysicalKeyId, long>());

        Assert.Equal(0, heatmap.MaxCount);
        Assert.All(heatmap.Cells, cell => Assert.Equal(0, cell.Count));
    }
}
