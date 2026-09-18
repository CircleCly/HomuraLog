namespace HomuraLog.Domain;

public sealed record LargeWindowBounds(float X, float Y, float Width, float Height);

public static class LargeWindowGeometry
{
    public const float Margin = 24f;
    public const float WidthRatio = 0.80f;
    public const float HeightRatio = 0.75f;
    public const float MinimumWidth = 760f;
    public const float MinimumHeight = 480f;
    public const float MaximumWidth = 2200f;
    public const float MaximumHeight = 1300f;

    public static LargeWindowBounds Default(float viewportWidth, float viewportHeight)
    {
        float availableWidth = Math.Max(1, viewportWidth - Margin * 2);
        float availableHeight = Math.Max(1, viewportHeight - Margin * 2);
        float minimumWidth = Math.Min(MinimumWidth, availableWidth);
        float minimumHeight = Math.Min(MinimumHeight, availableHeight);
        float width = Math.Clamp(viewportWidth * WidthRatio, minimumWidth,
            Math.Min(MaximumWidth, availableWidth));
        float height = Math.Clamp(viewportHeight * HeightRatio, minimumHeight,
            Math.Min(MaximumHeight, availableHeight));
        return new LargeWindowBounds((viewportWidth - width) / 2,
            (viewportHeight - height) / 2, width, height);
    }

    public static LargeWindowBounds Clamp(float viewportWidth, float viewportHeight,
        float x, float y, float width, float height)
    {
        float availableWidth = Math.Max(1, viewportWidth - Margin * 2);
        float availableHeight = Math.Max(1, viewportHeight - Margin * 2);
        width = Math.Min(Math.Max(1, width), availableWidth);
        height = Math.Min(Math.Max(1, height), availableHeight);
        float maximumX = Math.Max(Margin, viewportWidth - width - Margin);
        float maximumY = Math.Max(Margin, viewportHeight - height - Margin);
        return new LargeWindowBounds(Math.Clamp(x, Margin, maximumX),
            Math.Clamp(y, Margin, maximumY), width, height);
    }
}
