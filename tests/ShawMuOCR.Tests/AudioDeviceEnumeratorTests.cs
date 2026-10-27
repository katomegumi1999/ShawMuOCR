using System;
using System.Linq;
using MuRecorder.Audio;
using Xunit;
using Xunit.Abstractions;

namespace ShawMuOCR.Tests;

public class AudioDeviceEnumeratorTests
{
    private readonly ITestOutputHelper _output;

    public AudioDeviceEnumeratorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TestEnumerateDevices()
    {
        var render = AudioDeviceEnumerator.EnumerateRenderDevices();
        var capture = AudioDeviceEnumerator.EnumerateCaptureDevices();
        var all = AudioDeviceEnumerator.EnumerateAllDevices();

        _output.WriteLine($"Render count: {render.Count}, Capture count: {capture.Count}, All count: {all.Count}");
        foreach (var r in render)
        {
            _output.WriteLine($"Render: {r.Name} ({r.Id}), Default={r.IsDefault}");
        }
        foreach (var c in capture)
        {
            _output.WriteLine($"Capture: {c.Name} ({c.Id}), Default={c.IsDefault}");
        }

        Assert.NotNull(render);
        Assert.NotNull(capture);
        Assert.NotNull(all);
        Assert.Equal(render.Count + capture.Count, all.Count);
    }

    [Fact]
    public void TestSelectableAudioDevice_PropertiesAndEvents()
    {
        var info = new AudioDeviceInfo("dev-1", "Test Speaker", AudioDeviceFlow.Render, true);
        var item = new SelectableAudioDevice(info, isSelected: false);

        Assert.Equal("dev-1", item.Id);
        Assert.Equal("Test Speaker", item.Name);
        Assert.Equal("🔊", item.FlowIcon);
        Assert.Equal("系统声音", item.FlowLabel);
        Assert.Equal("Test Speaker (系统默认)", item.DisplayName);
        Assert.False(item.IsSelected);

        bool eventFired = false;
        item.SelectionChanged += () => eventFired = true;

        item.IsSelected = true;
        Assert.True(eventFired);
        Assert.True(item.IsSelected);
    }

    [Fact]
    public void TestAudioPreset_Behavior()
    {
        var dev1 = new SelectableAudioDevice(new AudioDeviceInfo("id1", "Speaker", AudioDeviceFlow.Render, true));
        var dev2 = new SelectableAudioDevice(new AudioDeviceInfo("id2", "Headset", AudioDeviceFlow.Render, false));
        var mic1 = new SelectableAudioDevice(new AudioDeviceInfo("id3", "Microphone", AudioDeviceFlow.Capture, true));

        var list = new[] { dev1, dev2, mic1 };

        // Test None
        foreach (var d in list) d.IsSelected = false;
        Assert.True(list.All(d => !d.IsSelected));

        // Test System: should select default render only
        dev1.IsSelected = dev1.Flow == AudioDeviceFlow.Render && dev1.IsDefault;
        dev2.IsSelected = dev2.Flow == AudioDeviceFlow.Render && dev2.IsDefault;
        mic1.IsSelected = mic1.Flow == AudioDeviceFlow.Render && mic1.IsDefault;
        Assert.True(dev1.IsSelected);
        Assert.False(dev2.IsSelected);
        Assert.False(mic1.IsSelected);

        // Test Microphone: should select default capture only
        dev1.IsSelected = dev1.Flow == AudioDeviceFlow.Capture && dev1.IsDefault;
        dev2.IsSelected = dev2.Flow == AudioDeviceFlow.Capture && dev2.IsDefault;
        mic1.IsSelected = mic1.Flow == AudioDeviceFlow.Capture && mic1.IsDefault;
        Assert.False(dev1.IsSelected);
        Assert.False(dev2.IsSelected);
        Assert.True(mic1.IsSelected);

        // Test SystemAndMic: should select default render and default capture
        dev1.IsSelected = dev1.IsDefault;
        dev2.IsSelected = false;
        mic1.IsSelected = mic1.IsDefault;
        Assert.True(dev1.IsSelected);
        Assert.False(dev2.IsSelected);
        Assert.True(mic1.IsSelected);
    }

    [Fact]
    public void TestEstimate_StaticTextPageWithWhitespace()
    {
        // 800x600 image with white background and some black text lines
        using var bmp1 = new SkiaSharp.SKBitmap(800, 600, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        using var canvas1 = new SkiaSharp.SKCanvas(bmp1);
        canvas1.Clear(SkiaSharp.SKColors.White);

        using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, StrokeWidth = 2 };
        // Draw some text lines (horizontal lines simulating text)
        for (int y = 50; y < 350; y += 30)
        {
            canvas1.DrawLine(50, y, 750, y, paint);
        }
        canvas1.Flush();

        // bmp2 is identical to bmp1
        using var bmp2 = bmp1.Copy();

        var est = ShawMuOCR.Core.Capture.VerticalShiftEstimator.Estimate(bmp1, bmp2);
        _output.WriteLine($"Static page: ShiftPixels={est.ShiftPixels}, Confident={est.IsConfident}, Reason={est.Reason}, BestScore={est.BestScore}, RunnerUp={est.RunnerUpScore}");

        Assert.Equal(0, est.ShiftPixels);
        Assert.True(est.IsConfident);
    }
}
