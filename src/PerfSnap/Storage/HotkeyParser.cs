using PerfSnap.Platform;

namespace PerfSnap.Storage;

/// <summary>設定のホットキー表記を RegisterHotKey の引数に変換する。</summary>
public static class HotkeyParser
{
    public static bool TryParse(HotkeySettings? hk, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (hk?.Modifiers is null || string.IsNullOrWhiteSpace(hk.Key)) return false;

        foreach (var m in hk.Modifiers)
        {
            switch (m.Trim().ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": modifiers |= NativeMethods.MOD_CONTROL; break;
                case "ALT": modifiers |= NativeMethods.MOD_ALT; break;
                case "SHIFT": modifiers |= NativeMethods.MOD_SHIFT; break;
                case "WIN": modifiers |= NativeMethods.MOD_WIN; break;
                default: return false;
            }
        }

        // 修飾キーなしのホットキーは、通常の入力を奪うので許さない
        if (modifiers == 0) return false;

        if (!Enum.TryParse<Keys>(hk.Key.Trim(), ignoreCase: true, out var key)) return false;
        if (key is Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return false;

        virtualKey = (uint)key;
        return true;
    }
}
