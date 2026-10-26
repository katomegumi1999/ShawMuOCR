using System;
using ShawMuOCR.Core.Capture;

namespace ShawMuOCR.Views;

public sealed class LongScreenshotOverlayController
{
    private readonly LongScreenshotMaskWindow _maskWindow;
    private readonly LongScreenshotFloatingWindow _floatingWindow;

    public static LongScreenshotOverlayController Show(
        ScreenRect region,
        ScreenRect virtualBounds,
        Action onStop,
        Action onCancel)
    {
        var mask = new LongScreenshotMaskWindow(region, virtualBounds);
        mask.Show();

        var floating = new LongScreenshotFloatingWindow(region, virtualBounds, onStop, onCancel);
        floating.Show();
        floating.Activate();

        return new LongScreenshotOverlayController(mask, floating);
    }

    public LongScreenshotOverlayController(LongScreenshotMaskWindow mask, LongScreenshotFloatingWindow floating)
    {
        _maskWindow = mask;
        _floatingWindow = floating;
    }

    public void UpdateStatus(string message)
    {
        _floatingWindow.UpdateStatus(message);
    }

    public void Close()
    {
        try { _floatingWindow.Close(); } catch { }
        try { _maskWindow.Close(); } catch { }
    }
}
