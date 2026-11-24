using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace QuickCapture.Services;

/// Registers QuickCapture under the current user's Run key, so Windows starts it
/// in the tray (`--background`) at sign-in. Only HKCU is touched: no admin rights
/// are needed and other users of the PC are not affected.
public sealed class StartupService
{
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "QuickCapture";
    public string KeyPath { get; }
    public string Command { get; }
    public StartupService(string? keyPath = null, string? command = null)
    {
        KeyPath = keyPath ?? DefaultKeyPath;
        Command = command ?? BuildCommand(Environment.ProcessPath, Assembly.GetEntryAssembly()?.Location);
    }
    /// The exe itself, or — when started through the shared host
    /// (`dotnet QuickCapture.dll`) — the host plus the dll, always quoted.
    public static string BuildCommand(string? processPath, string? assemblyPath)
    {
        if (string.IsNullOrEmpty(processPath)) throw new InvalidOperationException("実行ファイルの場所を取得できません。");
        bool host = Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        if (host && !string.IsNullOrEmpty(assemblyPath)) return $"\"{processPath}\" \"{assemblyPath}\" --background";
        return $"\"{processPath}\" --background";
    }
    public string? RegisteredCommand
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(KeyPath); return key?.GetValue(ValueName) as string; }
    }
    public bool IsEnabled => RegisteredCommand != null;
    /// Writes the current path (so a new version in another folder takes over
    /// the entry) or removes the entry. Unchanged entries are left alone.
    public void Apply(bool enabled)
    {
        if (enabled)
        {
            if (string.Equals(RegisteredCommand, Command, StringComparison.OrdinalIgnoreCase)) return;
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, true);
            key?.DeleteValue(ValueName, false);
        }
    }
}
