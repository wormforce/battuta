using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Battuta.TestSupport;
using Battuta.TestSupport.Threading;
using Battuta.Windows.Controls;
using Battuta.Windows.Views.Tray;

namespace Battuta.Windows.Tests.Ui;

public sealed class TraySliderTemplateTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(44, 44)]
    [InlineData(100, 100)]
    [InlineData(150, 75)]
    [InlineData(200, 100)]
    [Trait(TestCategories.TraitName, TestCategories.Ui)]
    public void KeyboardAndPointerVolumeThumbsStayCenteredVisibleAndUnclipped(
        double keyboardVolume,
        double pointerVolume)
    {
        StaTestHost.Run(() =>
        {
            var window = new TrayFlyoutWindow();
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            Arrange(content, 360, 760);

            var keyboardSlider = Assert.IsType<Slider>(window.FindName("KeyboardVolumeSlider"));
            var pointerSlider = Assert.IsType<Slider>(window.FindName("PointerVolumeSlider"));
            Assert.Equal(200, keyboardSlider.Maximum);
            Assert.Equal(100, pointerSlider.Maximum);
            AssertSliderGeometry(keyboardSlider, keyboardVolume);
            AssertSliderGeometry(pointerSlider, pointerVolume);
        });
    }

    private static void AssertSliderGeometry(Slider slider, double value)
    {
        slider.Value = value;
        _ = slider.ApplyTemplate();
        slider.UpdateLayout();

        var visualTrack = Assert.IsType<BattutaSliderTrack>(
            slider.Template.FindName("PART_VisualTrack", slider));
        var track = Assert.IsType<Track>(
            slider.Template.FindName("PART_Track", slider));
        var thumb = Assert.IsType<Thumb>(
            slider.Template.FindName("PART_Thumb", slider));
        Assert.Same(thumb, track.Thumb);
        Assert.InRange(track.ActualHeight, 13.5, 14.5);
        Assert.InRange(thumb.ActualWidth, 13.5, 14.5);
        Assert.InRange(thumb.ActualHeight, 13.5, 14.5);

        var thumbOrigin = thumb.TransformToAncestor(slider).Transform(new Point());
        var thumbCenter = new Point(
            thumbOrigin.X + thumb.ActualWidth / 2,
            thumbOrigin.Y + thumb.ActualHeight / 2);
        var visualCenter = visualTrack.TransformToAncestor(slider).Transform(new Point(
            visualTrack.ProgressEndX,
            visualTrack.TrackCenterY));

        Assert.InRange(Math.Abs(thumbCenter.X - visualCenter.X), 0, 0.5);
        Assert.InRange(Math.Abs(thumbCenter.Y - visualCenter.Y), 0, 0.5);
        Assert.InRange(thumbOrigin.X, -0.01, slider.ActualWidth);
        Assert.InRange(
            thumbOrigin.X + thumb.ActualWidth,
            0,
            slider.ActualWidth + 0.01);
        Assert.InRange(thumbOrigin.Y, -0.01, slider.ActualHeight);
        Assert.InRange(
            thumbOrigin.Y + thumb.ActualHeight,
            0,
            slider.ActualHeight + 0.01);

        var remaining = Assert.IsAssignableFrom<SolidColorBrush>(visualTrack.RemainingBrush);
        Assert.True(remaining.Color.A > 0);
        Assert.True(visualTrack.RailThickness > 0);
        Assert.True(visualTrack.TrackStartX >= visualTrack.ThumbRadius);
        Assert.True(visualTrack.TrackEndX <= visualTrack.ActualWidth - visualTrack.ThumbRadius);
    }

    private static void Arrange(FrameworkElement element, double width, double height)
    {
        var size = new Size(width, height);
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
    }
}
