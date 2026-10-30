using System.Buffers.Binary;
using System.Text;
using MuRecorder.Interop;
using MuRecorder.Services;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Windows.ScreenCapture;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// 录屏端到端集成测试：C# FFI → Rust 捕获/编码/封装 → mp4 落盘。
/// 快档只覆盖不需要真实捕获的校验路径（配置校验、状态机、DLL 探测）；
/// 真实录制标注 Slow（含真实等待时长），只在发布/提交前的全量门禁跑。
/// </summary>
public class RecordingServiceIntegrationTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public RecordingServiceIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void WindowEnumerator_Enumerate_DoesNotThrow()
    {
        var windows = MuRecorder.Windows.WindowEnumerator.Enumerate();
        Assert.NotNull(windows);
        // Ensure TextInputHost is never included
        Assert.DoesNotContain(windows, w => string.Equals(w.ProcessName, "TextInputHost", StringComparison.OrdinalIgnoreCase));
    }

    private static RecordingService NewService() => new();

    [Fact]
    public void RecorderCore_IsAvailable_WhenDllPresent()
    {
        // DLL 由 scripts\build.ps1 从 Rust release 产物拷贝至 Libs\x64\；
        // 此断言失败说明漏了拷贝或架构不匹配（x86/x64）。
        Assert.True(RecordingService.IsAvailable, "murecorder_core.dll 缺失或无法加载");
    }

    [Fact]
    public async Task Create_RejectsRegionOutsideAnyOutput()
    {
        await using var service = NewService();
        var options = new RecordingOptions
        {
            // 远超任何显示器尺寸：Rust 侧 find_target 必须拒绝
            Region = new ScreenRect(-100_000, -100_000, 4_000_000, 4_000_000),
            Fps = 30,
            OutputPath = Path.Combine(Path.GetTempPath(), "shawmu_rec_invalid.mp4"),
        };

        var ex = await Assert.ThrowsAsync<RecordingException>(
            () => service.StartAsync(options));
        Assert.Equal(RecorderResult.Invalid, ex.Result);
    }

    [Fact]
    public async Task Create_RejectsZeroSizedRegion()
    {
        await using var service = NewService();
        var options = new RecordingOptions
        {
            Region = new ScreenRect(0, 0, 0, 0),
            Fps = 30,
            OutputPath = Path.Combine(Path.GetTempPath(), "shawmu_rec_zero.mp4"),
        };

        var ex = await Assert.ThrowsAsync<RecordingException>(
            () => service.StartAsync(options));
        Assert.Equal(RecorderResult.Invalid, ex.Result);
    }

    [Fact]
    public async Task Stop_WithoutStart_IsNoOp()
    {
        await using var service = NewService();
        // 幂等语义：未开始录制时停止不应抛异常
        await service.StopAsync();
        Assert.False(service.IsRecording);
    }

    [Fact]
    public async Task DoubleStart_RejectedWithStateError()
    {
        await using var service = NewService();
        var options = ValidOptions(Path.Combine(Path.GetTempPath(), "shawmu_rec_double.mp4"));

        // 第一次 start 若因环境失败（无桌面会话等）则跳过本断言，不做假阳性
        try
        {
            await service.StartAsync(options);
        }
        catch (RecordingException)
        {
            return;
        }

        try
        {
            var ex = await Assert.ThrowsAsync<RecordingException>(
                () => service.StartAsync(options));
            Assert.Equal(RecorderResult.State, ex.Result);
        }
        finally
        {
            // 无人为画面变化时可能 0 样本，Rust stop 会以 Io 透传；此处只关心清理，不断言
            try
            {
                await service.StopAsync();
            }
            catch (RecordingException)
            {
            }
        }
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task Record_ShortClip_ProducesFastStartMp4()
    {
        var capture = new WindowsScreenCaptureService();
        var bounds = capture.GetVirtualScreenBounds();

        // 区域锚定虚拟桌面原点，钳制在 320×240 内，保证完整落在单一输出内
        int w = Math.Min(320, bounds.Width);
        int h = Math.Min(240, bounds.Height);
        Assert.True(w >= 16 && h >= 16, "虚拟桌面尺寸过小，无法构造有效录制区域");

        var output = Path.Combine(Path.GetTempPath(), $"shawmu_rec_e2e_{Guid.NewGuid():N}.mp4");
        if (File.Exists(output))
        {
            File.Delete(output);
        }

        await using var service = NewService();
        var options = new RecordingOptions
        {
            Region = new ScreenRect(bounds.X, bounds.Y, w, h),
            Fps = 15,
            BitrateKbps = 2000,
            Encoder = EncoderPreference.Soft,
            CaptureCursor = false,
            OutputPath = output,
        };

        await service.StartAsync(options);
        Assert.True(service.IsRecording);

        // DXGI 桌面复制只在画面发生变化时才交付帧，桌面完全静止（无人操作、无动画）
        // 时连首帧都拿不到 —— 这是 API 语义，不是缺陷。因此两条路径各自断言：
        //   · 有帧 → 校验真实端到端产物（faststart mp4 + avc1 轨道）
        //   · 无帧 → 校验「0 样本必须判失败且删除残留文件」，防"假成功"回归
        // 「编码 → 封装」这一段由 Rust 侧确定性覆盖（合成帧驱动真实 encode/mux 线程：
        // recorder::tests::pipeline_encodes_synthetic_frames_into_valid_mp4）。
        await Task.Delay(1500);

        var midStats = service.PollStats();
        Assert.NotNull(midStats);
        Assert.Equal(4u, midStats.Value.EncoderType); // 4 = openh264 软件编码

        if (midStats.Value.FramesCaptured == 0)
        {
            var ex = await Assert.ThrowsAsync<RecordingException>(() => service.StopAsync());
            Assert.True(ex.Result is RecorderResult.Io or RecorderResult.NoEncoder);
            Assert.False(File.Exists(output), "0 样本无法生成 moov，残留文件必须被删除");
            return;
        }

        Assert.True(midStats.Value.FramesEncoded > 0, "已捕获到帧时应同步产出编码帧");
        Assert.Equal(0, midStats.Value.LastError);

        await service.StopAsync();
        Assert.False(service.IsRecording);

        // ---- 校验 mp4 结构：faststart（ftyp 在前、moov 紧随其后）----
        Assert.True(File.Exists(output), "停止后文件必须存在");
        var fileInfo = new FileInfo(output);
        Assert.True(fileInfo.Length > 1024, $"文件过小（{fileInfo.Length} 字节），封装可能不完整");

        var bytes = File.ReadAllBytes(output);

        // 1. 起始必须是 ftyp
        var firstBox = ReadBoxHeader(bytes, 0);
        Assert.Equal("ftyp", firstBox.Type);

        // 2. 布局必须是 ftyp → mdat → moov（moov 在末尾，**刻意不做 faststart**）。
        //    理由见 C:\Rust\MuRecorder\src\mux\remux.rs 顶部注释：分片态收敛成普通 MP4 时
        //    只「末尾追加 moov + 覆写 ftyp 后 16 字节的 mdat 头」，样本字节一个都不搬，
        //    成本只与样本数成正比（1 小时 60fps 约几十毫秒）。若把 moov 前置，就要搬移
        //    整个 mdat，几十 GB 的录屏会在停止瞬间卡数秒到数十秒，得不偿失。
        var secondBox = ReadBoxHeader(bytes, (int)firstBox.Size);
        Assert.Equal("mdat", secondBox.Type);

        // 3. moov 在文件末尾，需要扫顶层盒定位（不能在第二个盒上取）。
        //    注意 mdat 用的是 64 位 largesize（前 4 字节为 1，真实大小在随后的 8 字节），
        //    必须按 largesize 递推，否则偏移会错位、永远扫不到 moov。
        int scanAt = 0;
        int moovAt = -1;
        long moovSize = 0;
        while (scanAt + 8 <= bytes.Length)
        {
            var box = ReadBoxHeader(bytes, scanAt);
            long size = box.Size;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(new ReadOnlySpan<byte>(bytes, scanAt + 8, 8));
            }
            if (size < 8) break;
            if (box.Type == "moov")
            {
                moovAt = scanAt;
                moovSize = size;
                break;
            }
            scanAt += (int)size;
        }
        Assert.True(moovAt > 0, "文件里找不到 moov 盒（封装可能未完成）");

        var moov = new ReadOnlySpan<byte>(bytes, moovAt, (int)moovSize);
        Assert.True(moov.IndexOf("avc1"u8) >= 0, "moov 缺少 avc1 样本描述");
        Assert.True(moov.IndexOf("avcC"u8) >= 0, "moov 缺少 avcC 解码配置");

        int stszAt = moov.IndexOf("stsz"u8);
        Assert.True(stszAt > 0, "moov 缺少 stsz 样本尺寸表");
        // stsz 载荷：box start+12 = sample_size，+16 = sample_count
        uint sampleCount = BinaryPrimitives.ReadUInt32BigEndian(
            moov.Slice(stszAt + 12, 4));
        Assert.True(sampleCount > 0, $"stsz 样本数为 {sampleCount}，H.264 轨道没有写入任何样本");
        Assert.True(
            sampleCount >= midStats.Value.FramesEncoded / 2,
            $"stsz 样本数 {sampleCount} 与编码帧数 {midStats.Value.FramesEncoded} 明显不符");
    }

    private static RecordingOptions ValidOptions(string outputPath)
    {
        var capture = new WindowsScreenCaptureService();
        var bounds = capture.GetVirtualScreenBounds();
        int w = Math.Min(320, bounds.Width);
        int h = Math.Min(240, bounds.Height);
        return new RecordingOptions
        {
            Region = new ScreenRect(bounds.X, bounds.Y, w, h),
            Fps = 15,
            BitrateKbps = 2000,
            Encoder = EncoderPreference.Soft,
            OutputPath = outputPath,
        };
    }

    private static (uint Size, string Type) ReadBoxHeader(byte[] bytes, int offset)
    {
        uint size = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(bytes, offset, 4));
        string type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
        return (size, type);
    }

    [Fact]
    public void EncoderPreference_DefaultsToGpuH264()
    {
        var options = new RecordingOptions
        {
            Region = new ScreenRect(0, 0, 100, 100),
            OutputPath = "dummy.mp4",
        };
        Assert.Equal(EncoderPreference.GpuH264, options.Encoder);
    }

    [Theory]
    [InlineData(1, "GPU · H.265 (HEVC)")]
    [InlineData(2, "GPU · H.264 (AVC)")]
    [InlineData(4, "CPU · H.264 (软编)")]
    [InlineData(5, "GPU · AV1 (硬件加速)")]
    public void RecorderStats_EncoderName_MapsExpectedLabels(uint type, string expected)
    {
        var stats = new RecorderStats { EncoderType = type };
        Assert.Equal(expected, stats.EncoderName);
    }

    [Fact]
    public void AppSettings_RecordingDefaults_PriorityIsGpuHevc()
    {
        var settings = new ShawMuOCR.Core.Settings.AppSettings();
        Assert.Equal((int)EncoderPreference.GpuHevc, settings.RecordingEncoder);
        Assert.Equal(1, settings.RecordingEncoder);
        Assert.Equal(30u, settings.RecordingFps);
        Assert.Equal(8000u, settings.RecordingBitrateKbps);
        Assert.True(settings.RecordingShowRegionBorder);
        Assert.Equal(string.Empty, settings.RecordingOutputDir);
        Assert.Equal("MuRecorder", settings.RecordingFilePrefix);
        Assert.Equal("Alt+R", settings.RecordingHotkey);
    }

    [Fact]
    public void AppSettings_RecordingSettings_SerializesAndDeserializesRoundtrip()
    {
        var original = new ShawMuOCR.Core.Settings.AppSettings
        {
            RecordingEncoder = (int)EncoderPreference.GpuAv1,
            RecordingFps = 60,
            RecordingBitrateKbps = 12000,
            RecordingShowRegionBorder = false,
            RecordingOutputDir = @"C:\TestOutputDir",
            RecordingFilePrefix = "CustomPrefix",
            RecordingHotkey = "Alt+Shift+R",
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            original,
            ShawMuOCR.Core.Settings.AppSettingsJsonContext.Default.AppSettings);

        Assert.Contains("\"Encoder\": 5", json);
        Assert.Contains("\"Fps\": 60", json);
        Assert.Contains("\"BitrateKbps\": 12000", json);
        Assert.Contains("\"ShowRegionBorder\": false", json);
        Assert.Contains("\"OutputDir\": \"C:\\\\TestOutputDir\"", json);
        Assert.Contains("\"FilePrefix\": \"CustomPrefix\"", json);
        Assert.Contains("\"Recording\":", json);
        Assert.Contains("Alt\\u002BShift\\u002BR", json);

        var restored = System.Text.Json.JsonSerializer.Deserialize(
            json,
            ShawMuOCR.Core.Settings.AppSettingsJsonContext.Default.AppSettings);

        Assert.NotNull(restored);
        Assert.Equal(5, restored.RecordingEncoder);
        Assert.Equal(60u, restored.RecordingFps);
        Assert.Equal(12000u, restored.RecordingBitrateKbps);
        Assert.False(restored.RecordingShowRegionBorder);
        Assert.Equal(@"C:\TestOutputDir", restored.RecordingOutputDir);
        Assert.Equal("CustomPrefix", restored.RecordingFilePrefix);
        Assert.Equal("Alt+Shift+R", restored.RecordingHotkey);
    }

    [Theory]
    [InlineData(null, "MuRecorder")]
    [InlineData("", "MuRecorder")]
    [InlineData("   ", "MuRecorder")]
    [InlineData("MyVideo", "MyVideo")]
    [InlineData("Rec:1/2*?", "Rec_1_2__")]
    public void SanitizeFilePrefix_CleansInvalidCharsAndDefaults(string? input, string expected)
    {
        var result = MuRecorder.ViewModels.RecordingViewModel.SanitizeFilePrefix(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Create_RejectsDeviceAudioWithoutDeviceId()
    {
        await using var service = NewService();
        var options = new RecordingOptions
        {
            Region = new ScreenRect(0, 0, 1920, 1080),
            Fps = 30,
            OutputPath = Path.Combine(Path.GetTempPath(), "shawmu_rec_dev_no_id.mp4"),
            AudioSource = AudioSourceType.Device,
            AudioDeviceId = null,
        };

        var ex = await Assert.ThrowsAsync<RecordingException>(() => service.StartAsync(options));
        Assert.Equal(RecorderResult.Invalid, ex.Result);
    }

    [Fact]
    public async Task Create_RejectsWindowAudioWithoutHwnd()
    {
        await using var service = NewService();
        var options = new RecordingOptions
        {
            Region = new ScreenRect(0, 0, 1920, 1080),
            Fps = 30,
            OutputPath = Path.Combine(Path.GetTempPath(), "shawmu_rec_win_no_hwnd.mp4"),
            AudioSource = AudioSourceType.Window,
            AudioWindowHwnd = IntPtr.Zero,
        };

        var ex = await Assert.ThrowsAsync<RecordingException>(() => service.StartAsync(options));
        Assert.Equal(RecorderResult.Invalid, ex.Result);
    }

    [Fact]
    public async Task Create_RejectsUnsupportedAudioBitrate()
    {
        await using var service = NewService();
        var options = new RecordingOptions
        {
            Region = new ScreenRect(0, 0, 1920, 1080),
            Fps = 30,
            OutputPath = Path.Combine(Path.GetTempPath(), "shawmu_rec_invalid_bitrate.mp4"),
            AudioSource = AudioSourceType.None,
            AudioBitrateKbps = 12345,
        };

        var ex = await Assert.ThrowsAsync<RecordingException>(() => service.StartAsync(options));
        Assert.Equal(RecorderResult.Invalid, ex.Result);
    }

    [Fact]
    public async Task ExtractRange_RejectsNonExistentFile()
    {
        string nonExistent = Path.Combine(Path.GetTempPath(), "non_existent_" + Guid.NewGuid().ToString("N") + ".mp4");
        string dst = Path.Combine(Path.GetTempPath(), "out_" + Guid.NewGuid().ToString("N") + ".mp4");

        var ex = await Assert.ThrowsAsync<RecordingException>(() =>
            RecordingService.ExtractRangeAsync(nonExistent, dst, 0, 10_000_000, 30));
        Assert.True(ex.Result is RecorderResult.Io or RecorderResult.Invalid);
    }
}


