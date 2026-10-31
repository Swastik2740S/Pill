namespace DynamicIsland.Interop;

/// <summary>Parses shortcut text like "Ctrl+Alt+Space" into RegisterHotKey modifiers and a virtual key.</summary>
internal static class Hotkey
{
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    public static bool TryParse(string? text, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string part = raw.ToUpperInvariant();
            switch (part)
            {
                case "CTRL" or "CONTROL":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    continue;
                case "ALT":
                    modifiers |= NativeMethods.MOD_ALT;
                    continue;
                case "SHIFT":
                    modifiers |= MOD_SHIFT;
                    continue;
                case "WIN" or "WINDOWS":
                    modifiers |= MOD_WIN;
                    continue;
            }

            if (key != 0)
            {
                return false; // two non-modifier keys
            }
            key = part switch
            {
                "SPACE" => 0x20,
                "ENTER" or "RETURN" => 0x0D,
                "TAB" => 0x09,
                "ESC" or "ESCAPE" => 0x1B,
                "`" or "BACKQUOTE" => 0xC0,
                "." or "PERIOD" => 0xBE,
                "," or "COMMA" => 0xBC,
                "/" or "SLASH" => 0xBF,
                ";" or "SEMICOLON" => 0xBA,
                _ when part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]) => part[0],
                _ when part.Length is 2 or 3 && part[0] == 'F' && int.TryParse(part[1..], out int f) && f is >= 1 and <= 24
                    => (uint)(0x70 + f - 1),
                _ => 0,
            };
            if (key == 0)
            {
                return false;
            }
        }

        // A bare key would swallow normal typing system wide.
        return key != 0 && modifiers != 0;
    }
}
