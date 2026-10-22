using System;
using System.Runtime.InteropServices;

namespace ShawMuOCR.Windows.Win32;

public static partial class Imm32
{
    public const uint IME_CMODE_ALPHANUMERIC = 0x0000;
    public const uint IME_CMODE_NATIVE = 0x0001;
    public const uint IME_CMODE_CHINESE = IME_CMODE_NATIVE;
    public const uint IME_CMODE_FULLSHAPE = 0x0008;

    [LibraryImport("imm32.dll")]
    public static partial IntPtr ImmGetContext(IntPtr hWnd);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ImmSetOpenStatus(IntPtr hIMC, [MarshalAs(UnmanagedType.Bool)] bool fOpen);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ImmGetOpenStatus(IntPtr hIMC);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ImmGetConversionStatus(IntPtr hIMC, out uint lpfdwConversion, out uint lpfdwSentence);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ImmSetConversionStatus(IntPtr hIMC, uint fdwConversion, uint fdwSentence);
}
