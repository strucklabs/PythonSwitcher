using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PythonSwitcher.Core;

/// <summary>Lesen und Schreiben der dauerhaft gespeicherten PATH-Werte.</summary>
public interface IPathStore
{
    string Read(PathScope scope);

    /// <summary>Schreibt den Wert. Für den System-PATH sind Adminrechte nötig.</summary>
    void Write(PathScope scope, string value);
}

/// <summary>PATH in der Registry: HKCU\Environment bzw. HKLM\...\Session Manager\Environment.</summary>
public sealed class RegistryPathStore : IPathStore
{
    private const string SystemKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const string UserKey = "Environment";

    public string Read(PathScope scope)
    {
        using var key = Open(scope, writable: false);
        return key?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
    }

    public void Write(PathScope scope, string value)
    {
        if (value.Contains('\n') || value.Contains('\r') || value.Contains('\0'))
            throw new ArgumentException("Der PATH-Wert enthält unzulässige Zeichen.");

        using var key = Open(scope, writable: true)
            ?? throw new InvalidOperationException("Der Registry-Schlüssel für den PATH fehlt.");

        // Art des Werts beibehalten (REG_SZ oder REG_EXPAND_SZ); neu angelegt wird REG_EXPAND_SZ.
        var kind = key.GetValueNames().Contains("Path", StringComparer.OrdinalIgnoreCase)
            ? key.GetValueKind("Path")
            : RegistryValueKind.ExpandString;
        if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString)
            kind = RegistryValueKind.ExpandString;
        if (kind == RegistryValueKind.String && value.Contains('%'))
            kind = RegistryValueKind.ExpandString;

        key.SetValue("Path", value, kind);
        EnvironmentBroadcast.Send();
    }

    private static RegistryKey? Open(PathScope scope, bool writable) => scope == PathScope.System
        ? Registry.LocalMachine.OpenSubKey(SystemKey, writable)
        : Registry.CurrentUser.OpenSubKey(UserKey, writable) ?? (writable ? Registry.CurrentUser.CreateSubKey(UserKey) : null);
}

/// <summary>Teilt laufenden Programmen (Explorer usw.) mit, dass sich Umgebungsvariablen geändert haben.</summary>
public static partial class EnvironmentBroadcast
{
    private static readonly IntPtr HwndBroadcast = new(0xffff);
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
        uint flags, uint timeout, out UIntPtr result);

    public static void Send()
    {
        SendMessageTimeout(HwndBroadcast, WmSettingChange, UIntPtr.Zero, "Environment", SmtoAbortIfHung, 3000, out _);
    }
}
