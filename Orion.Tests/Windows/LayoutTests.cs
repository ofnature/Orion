using Orion.Windows;
using Xunit;

namespace Orion.Tests.Windows;

/// <summary>
/// The Settings sliders keep their label on screen. With a fixed 180 width a narrow window pushed the label
/// off its edge ("mount when farther than (yalr", 2026-11-21).
/// </summary>
public class LayoutTests
{
    [Fact]
    public void WithRoomTheSliderIsItsUsualWidth()
        => Assert.Equal(Layout.SliderWidthPreferred, Layout.SliderWidth(roomForSlider: 400f));

    [Fact]
    public void InANarrowWindowTheSliderGivesWayToItsLabel()
        => Assert.Equal(120f, Layout.SliderWidth(roomForSlider: 120f));

    [Fact]
    public void TheSliderNeverShrinksPastUsable()
        => Assert.Equal(Layout.SliderWidthMinimum, Layout.SliderWidth(roomForSlider: 10f));

    /// <summary>A label wider than the window leaves negative room.</summary>
    [Fact]
    public void NegativeRoomStillGivesTheMinimum()
        => Assert.Equal(Layout.SliderWidthMinimum, Layout.SliderWidth(roomForSlider: -50f));
}
