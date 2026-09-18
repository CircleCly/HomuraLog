namespace HomuraLog.Domain;

public sealed record LargeWindowBounds(float X, float Y, float Width, float Height);

public static class LargeWindowGeometry
{
    public const float Margin = 24f;
    // Preset captured on the user's 2560x1440 (16:9) display. With the game's
    // 1.333 UI scale Godot exposes that as a 1920x1080 logical viewport, where
    // the approved window rect is 24, 166.5, 1163.25, 578. Geometry is always
    // calculated in those logical viewport coordinates.
    public const float XRatio = 0.0125f;
    public const float YRatio = 0.15416665f;
    public const float WidthRatio = 0.6058594f;
    public const float HeightRatio = 0.53518516f;
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
        float x = Math.Clamp(viewportWidth * XRatio, Margin,
            Math.Max(Margin, viewportWidth - width - Margin));
        float y = Math.Clamp(viewportHeight * YRatio, Margin,
            Math.Max(Margin, viewportHeight - height - Margin));
        return new LargeWindowBounds(x, y, width, height);
    }

    public static bool IsReasonableSavedLayout(LargeWindowBounds bounds,
        float viewportWidth, float viewportHeight) =>
        bounds.Width <= viewportWidth * 0.9f && bounds.Height <= viewportHeight * 0.8f;

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
