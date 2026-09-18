using System.Runtime.InteropServices;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Windows.Win32;

namespace ShawMuOCR.Windows.Clipboard;

public sealed class WindowsClipboardService : IClipboardService
{
    public async Task<bool> SetTextAsync(string text)
    {
        if (text == null) text = string.Empty;

        // 剪贴板可能被其他进程瞬时占用，重试 5 次
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (TrySetTextInternal(text))
            {
                return true;
            }
            await Task.Delay(50);
        }

        return false;
    }

    public async Task<bool> SetImageAsync(OcrImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (TrySetImageInternal(image))
            {
                return true;
            }
            await Task.Delay(50);
        }

        return false;
    }

    private static bool TrySetImageInternal(OcrImage image)
    {
        int width = image.Width;
        int height = image.Height;
        if (width <= 0 || height <= 0) return false;

        int stride = width * 4;
        int headerSize = Marshal.SizeOf<Gdi32.BITMAPINFOHEADER>();
        int totalSize = headerSize + stride * height;

        if (!User32.OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        try
        {
            User32.EmptyClipboard();

            IntPtr hGlobal = Kernel32.GlobalAlloc(Kernel32.GMEM_MOVEABLE, (nuint)totalSize);
            if (hGlobal == IntPtr.Zero)
            {
                return false;
            }

            IntPtr targetPtr = Kernel32.GlobalLock(hGlobal);
            if (targetPtr == IntPtr.Zero)
            {
                Kernel32.GlobalFree(hGlobal);
                return false;
            }

            try
            {
                var bmiHeader = new Gdi32.BITMAPINFOHEADER
                {
                    biSize = (uint)headerSize,
                    biWidth = width,
                    biHeight = height, // 正数：自底向上 DIB
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Gdi32.BI_RGB,
                    biSizeImage = (uint)(stride * height),
                    biXPelsPerMeter = 0,
                    biYPelsPerMeter = 0,
                    biClrUsed = 0,
                    biClrImportant = 0
                };
                Marshal.StructureToPtr(bmiHeader, targetPtr, false);

                unsafe
                {
                    byte* src = (byte*)image.Bitmap.GetPixels();
                    byte* dst = (byte*)targetPtr + headerSize;
                    int srcRowBytes = image.Bitmap.RowBytes;

                    for (int y = 0; y < height; y++)
                    {
                        byte* srcRow = src + y * srcRowBytes;
                        byte* dstRow = dst + (height - 1 - y) * stride;

                        for (int x = 0; x < width; x++)
                        {
                            int px = x * 4;
                            dstRow[px + 0] = srcRow[px + 0]; // B
                            dstRow[px + 1] = srcRow[px + 1]; // G
                            dstRow[px + 2] = srcRow[px + 2]; // R
                            dstRow[px + 3] = 0xFF;           // A: 确保不透明
                        }
                    }
                }
            }
            finally
            {
                Kernel32.GlobalUnlock(hGlobal);
            }

            return User32.SetClipboardData(User32.CF_DIB, hGlobal) != IntPtr.Zero;
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    public Task<string?> GetTextAsync()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (TryGetTextInternal(out string? result))
            {
                return Task.FromResult(result);
            }
            Thread.Sleep(30);
        }

        return Task.FromResult<string?>(null);
    }

    private static bool TrySetTextInternal(string text)
    {
        if (!User32.OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        try
        {
            User32.EmptyClipboard();

            int bytesNeeded = (text.Length + 1) * 2;
            IntPtr hGlobal = Kernel32.GlobalAlloc(Kernel32.GMEM_MOVEABLE, (nuint)bytesNeeded);
            if (hGlobal == IntPtr.Zero)
            {
                return false;
            }

            IntPtr targetPtr = Kernel32.GlobalLock(hGlobal);
            if (targetPtr == IntPtr.Zero)
            {
                Kernel32.GlobalFree(hGlobal);
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, targetPtr, text.Length);
                Marshal.WriteInt16(targetPtr, text.Length * 2, 0); // 终止空字符
            }
            finally
            {
                Kernel32.GlobalUnlock(hGlobal);
            }

            return User32.SetClipboardData(User32.CF_UNICODETEXT, hGlobal) != IntPtr.Zero;
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    private static bool TryGetTextInternal(out string? text)
    {
        text = null;
        if (!User32.IsClipboardFormatAvailable(User32.CF_UNICODETEXT))
        {
            return false;
        }

        if (!User32.OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        try
        {
            IntPtr handle = User32.GetClipboardData(User32.CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            IntPtr pointer = Kernel32.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                text = Marshal.PtrToStringUni(pointer);
                return true;
            }
            finally
            {
                Kernel32.GlobalUnlock(handle);
            }
        }
        finally
        {
            User32.CloseClipboard();
        }
    }
}
