namespace Kabelaar.Core;

/// <summary>Generous drawing limits prevent finite input values overflowing display geometry and text bounds.</summary>
public static class PresentationLimits
{
    public const double MaximumDisplayScale = 1000;
    public const double MaximumFontSizeMm = 1000;
    public const double MaximumLineWidthMm = 1000;
    public const double MaximumTwistAmplitudeMm = 1000;
    public const double MaximumTwistDisplayPitchMm = 100000;

    public static bool ValidDisplayScale(double value) => Positive(value, MaximumDisplayScale);
    public static bool ValidFontSize(double value) => Positive(value, MaximumFontSizeMm);
    public static bool ValidStyle(DrawingStyle style) =>
        Positive(style.WireLineWidthMm, MaximumLineWidthMm) &&
        ValidFontSize(style.WireLabelFontSizeMm) &&
        Positive(style.ConnectorLineWidthMm, MaximumLineWidthMm) &&
        NonNegative(style.TwistAmplitudeMm, MaximumTwistAmplitudeMm) &&
        NonNegative(style.TwistDisplayPitchMm, MaximumTwistDisplayPitchMm) &&
        Enum.IsDefined(style.TwistDisplayMode);

    private static bool Positive(double value, double maximum) => double.IsFinite(value) && value > 0 && value <= maximum;
    private static bool NonNegative(double value, double maximum) => double.IsFinite(value) && value >= 0 && value <= maximum;
}
