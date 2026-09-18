namespace ShawMuOCR.Core.Hotkey;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8
}

public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");

        parts.Add(VirtualKeyToString(VirtualKey));
        return string.Join("+", parts);
    }

    public static bool TryParse(string text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return false;

        var modifiers = HotkeyModifiers.None;
        int vk = 0;

        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (string.Equals(token, "Ctrl", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Ctrl;
            }
            else if (string.Equals(token, "Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Shift;
            }
            else if (string.Equals(token, "Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Alt;
            }
            else if (string.Equals(token, "Win", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Win;
            }
            else
            {
                // 最后的主键
                vk = StringToVirtualKey(token);
            }
        }

        if (vk == 0) return false;
        gesture = new HotkeyGesture(modifiers, vk);
        return true;
    }

    private static string VirtualKeyToString(int vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) // A-Z
        {
            return ((char)vk).ToString();
        }
        if (vk >= 0x30 && vk <= 0x39) // 0-9
        {
            return ((char)vk).ToString();
        }
        if (vk >= 0x70 && vk <= 0x87) // F1-F24
        {
            return $"F{vk - 0x70 + 1}";
        }
        return vk switch
        {
            0x20 => "Space",
            0x0D => "Enter",
            0x09 => "Tab",
            0x1B => "Esc",
            0x2D => "Insert",
            0x2E => "Delete",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0xBC => ",",
            0xBE => ".",
            0xBF => "/",
            0xBA => ";",
            0xDE => "'",
            0xDB => "[",
            0xDD => "]",
            0xC0 => "`",
            _ => $"0x{vk:X2}"
        };
    }

    private static int StringToVirtualKey(string keyName)
    {
        if (keyName.Length == 1)
        {
            char c = char.ToUpperInvariant(keyName[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                return c;
            }
        }
        if (keyName.StartsWith("F", StringComparison.OrdinalIgnoreCase) && int.TryParse(keyName[1..], out int fIndex) && fIndex is >= 1 and <= 24)
        {
            return 0x70 + fIndex - 1;
        }

        return keyName.ToUpperInvariant() switch
        {
            "SPACE" => 0x20,
            "ENTER" => 0x0D,
            "TAB" => 0x09,
            "ESC" => 0x1B,
            "ESCAPE" => 0x1B,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            _ => 0
        };
    }
}
