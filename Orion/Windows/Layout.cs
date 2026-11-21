namespace Orion.Windows;

internal static class Layout
{
    public const float HeroButtonHeight = 52f;
    public const float StatTileHeight = 58f;

    /// <summary>A labelled slider's usual width, and the least it shrinks to in a narrow window.</summary>
    public const float SliderWidthPreferred = 180f;
    public const float SliderWidthMinimum = 60f;

    /// <summary>
    /// How wide to draw a slider given the room left for it once its label is accounted for: the usual width
    /// when it fits, less when it does not, never unusably small.
    /// </summary>
    public static float SliderWidth(float roomForSlider)
        => System.Math.Clamp(roomForSlider, SliderWidthMinimum, SliderWidthPreferred);
}
