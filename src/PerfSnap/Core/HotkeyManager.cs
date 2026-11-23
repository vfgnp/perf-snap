using PerfSnap.Platform;
using PerfSnap.Storage;

namespace PerfSnap.Core;

/// <summary>
/// グローバルホットキー（要件 F-04）。メッセージ専用の見えないウィンドウで WM_HOTKEY を受け取る。
/// UI スレッドで作ること。
/// </summary>
public sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int HotkeyId = 0x5053; // "PS"
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private bool _registered;

    public event Action? Pressed;

    public HotkeyManager()
    {
        CreateHandle(new CreateParams { Caption = "PerfSnap.Hotkey", Parent = HWND_MESSAGE });
    }

    /// <summary>登録する。すでに登録していれば付け替える。他のアプリが使っていれば false。</summary>
    public bool Register(HotkeySettings hk)
    {
        Unregister();
        if (!HotkeyParser.TryParse(hk, out var mods, out var vk)) return false;
        _registered = NativeMethods.RegisterHotKey(Handle, HotkeyId, mods | NativeMethods.MOD_NOREPEAT, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        NativeMethods.UnregisterHotKey(Handle, HotkeyId);
        _registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }
}
