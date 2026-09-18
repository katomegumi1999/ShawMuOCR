using ShawMuOCR.Core.Hotkey;
using Xunit;

namespace ShawMuOCR.Tests;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Shift+C", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x43)]
    [InlineData("Alt+F4", HotkeyModifiers.Alt, 0x73)]
    [InlineData("Ctrl+Alt+A", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x41)]
    public void TryParse_ValidString_ParsesCorrectly(string text, HotkeyModifiers expectedModifiers, int expectedVk)
    {
        bool success = HotkeyGesture.TryParse(text, out var gesture);

        Assert.True(success);
        Assert.Equal(expectedModifiers, gesture.Modifiers);
        Assert.Equal(expectedVk, gesture.VirtualKey);
    }

    [Fact]
    public void ToString_Roundtrip_Works()
    {
        string original = "Ctrl+Shift+C";
        Assert.True(HotkeyGesture.TryParse(original, out var gesture));
        Assert.Equal(original, gesture.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+Shift")]
    [InlineData("InvalidKeyName")]
    public void TryParse_InvalidString_ReturnsFalse(string text)
    {
        bool success = HotkeyGesture.TryParse(text, out _);
        Assert.False(success);
    }

    [Fact]
    public void WindowsGlobalHotkeyService_Registration_Test()
    {
        using var service = new ShawMuOCR.Windows.Hotkey.WindowsGlobalHotkeyService();
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+F12", out var gesture));
        bool registered = service.Register(gesture, () => { });
        int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        Assert.True(registered, $"Register failed with Win32 error code: {err}");
        Assert.True(service.IsRegistered);
    }

    [Fact]
    public void WindowsGlobalHotkeyService_AltZ_And_MultiRegistration_Test()
    {
        using var service = new ShawMuOCR.Windows.Hotkey.WindowsGlobalHotkeyService();
        Assert.True(HotkeyGesture.TryParse("Alt+Z", out var altZGesture));
        Assert.True(HotkeyGesture.TryParse("Ctrl+Shift+X", out var copyGesture));

        bool regOcr = service.Register(1, altZGesture, () => { });
        bool regCopy = service.Register(2, copyGesture, () => { });

        Assert.True(regOcr);
        Assert.True(regCopy);
        Assert.True(service.IsRegisteredId(1));
        Assert.True(service.IsRegisteredId(2));
        Assert.Equal(altZGesture, service.GetGesture(1));
        Assert.Equal(copyGesture, service.GetGesture(2));

        service.Unregister(1);
        Assert.False(service.IsRegisteredId(1));
        Assert.True(service.IsRegisteredId(2));

        service.Unregister(2);
        Assert.False(service.IsRegisteredId(2));
    }
}
