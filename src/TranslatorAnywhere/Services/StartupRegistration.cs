using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace TranslatorAnywhere.Services;

public sealed record StartupRegistrationStatus(bool ReadSucceeded, bool HasRegistration,
    bool IsEnabledForCurrentExecutable, bool PointsToDifferentExecutable, string RegisteredExecutable, string Message)
{
    public bool RequiresMigration { get; init; }
}

public sealed record StartupRegistrationResult(bool Success, string Message);

internal sealed record StartupShortcutDescription(string TargetPath, string Arguments, string WorkingDirectory);

internal enum StartupApprovalState { Missing, Enabled, Disabled, Unknown }

/// <summary>
/// Uses the current user's Startup folder. Status reads never execute shortcuts or change Windows approval records.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TranslatorAnywhere";
    private const string ShortcutFileName = "TranslatorAnywhere.lnk";
    private const string ApprovedRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedStartupFolderKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private const string BackgroundArgument = "--background";

    internal static StartupShortcutDescription BuildShortcutDescription(string executablePath)
    {
        string executable = NormalizeExecutablePath(executablePath);
        return new StartupShortcutDescription(executable, BackgroundArgument,
            Path.GetDirectoryName(executable) ?? throw new ArgumentException("程序目录无效。", nameof(executablePath)));
    }

    internal static StartupApprovalState EvaluateStartupApproval(object? raw)
    {
        if (raw is null) return StartupApprovalState.Missing;
        if (raw is not byte[] bytes || bytes.Length < 4) return StartupApprovalState.Unknown;
        uint state = (uint)bytes[0] | (uint)bytes[1] << 8 | (uint)bytes[2] << 16 | (uint)bytes[3] << 24;
        return state switch
        {
            2 or 6 => StartupApprovalState.Enabled,
            3 or 7 or 9 => StartupApprovalState.Disabled,
            _ => StartupApprovalState.Unknown
        };
    }

    internal static StartupRegistrationStatus EvaluateShortcut(StartupShortcutDescription? shortcut,
        string currentExecutable, object? approvalRaw = null)
    {
        var expected = BuildShortcutDescription(currentExecutable);
        if (shortcut is null)
            return new StartupRegistrationStatus(true, false, false, false, "", "开机启动未启用。");
        string registered = TryNormalizeExecutablePath(shortcut.TargetPath);
        bool different = registered.Length > 0 && !string.Equals(registered, expected.TargetPath, StringComparison.OrdinalIgnoreCase);
        if (different)
            return new StartupRegistrationStatus(true, true, false, true, registered,
                "启动快捷方式仍指向旧目录或另一份程序。请在当前路径重新启用开机启动。");
        if (registered.Length == 0 || !string.Equals(registered, expected.TargetPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(shortcut.Arguments?.Trim(), expected.Arguments, StringComparison.OrdinalIgnoreCase)
            || !DirectoryMatches(shortcut.WorkingDirectory, expected.WorkingDirectory))
            return new StartupRegistrationStatus(true, true, false, false, registered,
                "启动快捷方式的路径、后台参数或工作目录不匹配，请重新启用开机启动。");
        var approval = EvaluateStartupApproval(approvalRaw);
        if (!ApprovalAllowsStartup(approval))
            return new StartupRegistrationStatus(true, true, false, false, registered, ApprovalMessage(approval));
        return new StartupRegistrationStatus(true, true, true, false, registered,
            "已登记启动快捷方式，登录 Windows 后将在托盘后台运行。");
    }

    internal static StartupRegistrationStatus EvaluateLegacyRegistration(object? rawCommand, string currentExecutable,
        object? runApprovalRaw = null, object? startupFolderApprovalRaw = null)
    {
        // The old Run command's 260-character limit must not constrain a new shortcut or an absent Run entry.
        if (rawCommand is null)
            return new StartupRegistrationStatus(true, false, false, false, "", "开机启动未启用。");
        StartupRegistrationStatus legacy;
        try { legacy = EvaluateCommand(rawCommand, currentExecutable); }
        catch (ArgumentException)
        {
            return new StartupRegistrationStatus(true, true, false, false, "",
                "现有启动登记无法用于当前路径，请重新启用以创建启动快捷方式。");
        }
        var runApproval = EvaluateStartupApproval(runApprovalRaw);
        var folderApproval = EvaluateStartupApproval(startupFolderApprovalRaw);
        if (runApproval == StartupApprovalState.Disabled || folderApproval == StartupApprovalState.Disabled)
            return legacy with { IsEnabledForCurrentExecutable = false, Message = ApprovalMessage(StartupApprovalState.Disabled) };
        if (!ApprovalAllowsStartup(runApproval) || !ApprovalAllowsStartup(folderApproval))
            return legacy with { IsEnabledForCurrentExecutable = false, Message = ApprovalMessage(StartupApprovalState.Unknown) };
        if (!legacy.IsEnabledForCurrentExecutable) return legacy;
        return legacy with
        {
            IsEnabledForCurrentExecutable = false, RequiresMigration = true,
            Message = "旧启动登记需要迁移到 Windows 启动文件夹，请重新启用开机启动。"
        };
    }

    /// <summary>Creates/saves only a .lnk description; neither the shortcut nor its target is ever executed.</summary>
    internal static void WriteShortcut(string shortcutPath, StartupShortcutDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        string path = NormalizeShortcutPath(shortcutPath);
        var expected = BuildShortcutDescription(description.TargetPath);
        if (!string.Equals(description.Arguments?.Trim(), expected.Arguments, StringComparison.OrdinalIgnoreCase)
            || !DirectoryMatches(description.WorkingDirectory, expected.WorkingDirectory))
            throw new ArgumentException("启动快捷方式参数或工作目录无效。", nameof(description));
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = CreateShell();
            shortcut = Invoke(shell, "CreateShortcut", BindingFlags.InvokeMethod, new object[] { path })
                ?? throw new InvalidOperationException("无法创建启动快捷方式。");
            Invoke(shortcut, "TargetPath", BindingFlags.SetProperty, new object[] { expected.TargetPath });
            Invoke(shortcut, "Arguments", BindingFlags.SetProperty, new object[] { expected.Arguments });
            Invoke(shortcut, "WorkingDirectory", BindingFlags.SetProperty, new object[] { expected.WorkingDirectory });
            Invoke(shortcut, "Save", BindingFlags.InvokeMethod, Array.Empty<object>());
        }
        finally { ReleaseCom(shortcut); ReleaseCom(shell); }
    }

    internal static StartupShortcutDescription ReadShortcut(string shortcutPath)
    {
        string path = NormalizeShortcutPath(shortcutPath);
        if (!File.Exists(path)) throw new FileNotFoundException("启动快捷方式不存在。");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = CreateShell();
            shortcut = Invoke(shell, "CreateShortcut", BindingFlags.InvokeMethod, new object[] { path })
                ?? throw new InvalidOperationException("无法读取启动快捷方式。");
            return new StartupShortcutDescription(ReadComString(shortcut, "TargetPath"),
                ReadComString(shortcut, "Arguments"), ReadComString(shortcut, "WorkingDirectory"));
        }
        finally { ReleaseCom(shortcut); ReleaseCom(shell); }
    }

    private static bool ApprovalAllowsStartup(StartupApprovalState state)
        => state is StartupApprovalState.Missing or StartupApprovalState.Enabled;

    private static string ApprovalMessage(StartupApprovalState state) => state == StartupApprovalState.Disabled
        ? "Windows 已停用此启动项，请到 Windows 设置的“应用 > 启动”或任务管理器中启用。"
        : "Windows 启动项状态无法确认，请在系统设置的“应用 > 启动”中检查。";

    private static string TryNormalizeExecutablePath(string path)
    {
        try { return NormalizeExecutablePath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return ""; }
    }

    private static bool DirectoryMatches(string path, string expected)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0) return false;
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }

    private static string NormalizeShortcutPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)
            || path.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
            throw new ArgumentException("启动快捷方式需要有效的 .lnk 完整路径。", nameof(path));
        string result = Path.GetFullPath(path);
        if (File.Exists(result) && (File.GetAttributes(result) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("无法使用重定向的启动快捷方式文件。");
        return result;
    }

    private static object CreateShell()
    {
        var type = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)
            ?? throw new InvalidOperationException("Windows 快捷方式服务不可用。");
        return Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows 快捷方式服务不可用。");
    }

    private static object? Invoke(object instance, string name, BindingFlags flags, object[] arguments)
        => instance.GetType().InvokeMember(name, flags, null, instance, arguments, CultureInfo.InvariantCulture);

    private static string ReadComString(object instance, string property)
        => Invoke(instance, property, BindingFlags.GetProperty, Array.Empty<object>()) as string ?? "";

    private static void ReleaseCom(object? instance)
    {
        if (instance is null || !Marshal.IsComObject(instance)) return;
        try { Marshal.FinalReleaseComObject(instance); }
        catch (Exception error) when (error is ArgumentException or InvalidComObjectException) { }
    }

    public static bool IsEnabledForCurrentExecutable() => GetStatus().IsEnabledForCurrentExecutable;

    public static StartupRegistrationStatus GetStatus()
    {
        try
        {
            string executable = GetCurrentExecutable();
            string shortcutPath = GetStartupShortcutPath();
            if (File.Exists(shortcutPath))
                return EvaluateShortcut(ReadShortcut(shortcutPath), executable,
                    ReadRegistryValue(ApprovedStartupFolderKey, ShortcutFileName));
            object? command = ReadRegistryValue(RunKey, ValueName);
            if (command is null) return EvaluateLegacyRegistration(null, executable);
            return EvaluateLegacyRegistration(command, executable,
                ReadRegistryValue(ApprovedRunKey, ValueName),
                ReadRegistryValue(ApprovedStartupFolderKey, ShortcutFileName));
        }
        catch (Exception error) when (IsExpectedError(error))
        {
            DiagnosticLog.Write("Startup registration status could not be read.", error);
            return new StartupRegistrationStatus(false, false, false, false, "", DescribeError(error, reading: true));
        }
    }

    public static StartupRegistrationResult SetEnabled(bool enabled)
    {
        string? temporaryShortcut = null;
        try
        {
            string shortcutPath = GetStartupShortcutPath();
            if (enabled)
            {
                string executable = GetCurrentExecutable();
                if (!File.Exists(executable)) return new StartupRegistrationResult(false, "程序路径不存在，未修改开机启动。请从实际的 EXE 文件重新运行。");
                var folderApproval = EvaluateStartupApproval(ReadRegistryValue(ApprovedStartupFolderKey, ShortcutFileName));
                if (!ApprovalAllowsStartup(folderApproval)) return new StartupRegistrationResult(false, ApprovalMessage(folderApproval));
                if (ReadRegistryValue(RunKey, ValueName) is not null)
                {
                    var runApproval = EvaluateStartupApproval(ReadRegistryValue(ApprovedRunKey, ValueName));
                    if (!ApprovalAllowsStartup(runApproval)) return new StartupRegistrationResult(false, ApprovalMessage(runApproval));
                }

                var description = BuildShortcutDescription(executable);
                Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
                // Stage outside Startup so Windows cannot enumerate a half-written temporary startup item.
                temporaryShortcut = Path.Combine(Path.GetTempPath(), "TranslatorAnywhere-startup-" + Guid.NewGuid().ToString("N") + ".lnk");
                WriteShortcut(temporaryShortcut, description);
                if (!EvaluateShortcut(ReadShortcut(temporaryShortcut), executable).IsEnabledForCurrentExecutable)
                    return new StartupRegistrationResult(false, "启动快捷方式写入后验证失败，旧启动登记已保留。");
                File.Move(temporaryShortcut, shortcutPath, overwrite: true);
                temporaryShortcut = null;
                var actual = EvaluateShortcut(ReadShortcut(shortcutPath), executable,
                    ReadRegistryValue(ApprovedStartupFolderKey, ShortcutFileName));
                if (!actual.IsEnabledForCurrentExecutable) return new StartupRegistrationResult(false, actual.Message);

                // Retain the legacy entry until the installed shortcut has been read back and approved.
                RemoveLegacyRun();
                return new StartupRegistrationResult(true, "已登记启动快捷方式，登录 Windows 后将在托盘后台运行。");
            }
            File.Delete(shortcutPath);
            if (File.Exists(shortcutPath)) return new StartupRegistrationResult(false, "启动快捷方式未能移除，请重试。");
            RemoveLegacyRun();
            return new StartupRegistrationResult(true, "开机启动已关闭。");
        }
        catch (Exception error) when (IsExpectedError(error))
        {
            DiagnosticLog.Write("Startup registration preference could not be applied.", error);
            return new StartupRegistrationResult(false, DescribeError(error, reading: false));
        }
        finally
        {
            if (temporaryShortcut is not null)
            {
                try { File.Delete(temporaryShortcut); }
                catch (Exception error) when (IsExpectedError(error))
                { DiagnosticLog.Write("Temporary startup shortcut cleanup failed.", error); }
            }
        }
    }

    private static string GetStartupShortcutPath()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            throw new InvalidOperationException("当前用户的 Windows 启动文件夹不可用。");
        return NormalizeShortcutPath(Path.Combine(folder, ShortcutFileName));
    }

    private static object? ReadRegistryValue(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    private static void RemoveLegacyRun()
    {
        if (ReadRegistryValue(RunKey, ValueName) is null) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        if (key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not null)
            throw new IOException("旧启动登记未能移除。");
    }

    /// <summary>Builds a direct Run command, with the EXE quoted independently of its arguments. No shell is invoked.</summary>
    public static string BuildCommand(string executablePath)
    {
        string executable = NormalizeExecutablePath(executablePath);
        string command = "\"" + executable + "\" " + BackgroundArgument;
        // Microsoft's Run key documentation limits command lines to 260 characters.
        if (command.Length > 260) throw new ArgumentException("程序路径过长，无法登记开机启动。请将便携目录移到较短的路径。", nameof(executablePath));
        return command;
    }

    internal static StartupRegistrationStatus EvaluateCommand(object? rawCommand, string currentExecutable)
    {
        string expected = BuildCommand(currentExecutable);
        if (rawCommand is null)
            return new StartupRegistrationStatus(true, false, false, false, "", "开机启动未启用。");
        if (rawCommand is not string command)
            return new StartupRegistrationStatus(true, true, false, false, "", "现有开机启动项格式无法识别，请在当前路径重新启用并保存。");
        command = command.Trim();
        if (string.Equals(command, expected, StringComparison.OrdinalIgnoreCase))
            return new StartupRegistrationStatus(true, true, true, false, NormalizeExecutablePath(currentExecutable), "已登记当前程序，登录后将在托盘后台运行。");

        string registeredPath = ReadQuotedExecutable(command);
        bool different = registeredPath.Length > 0 && !string.Equals(registeredPath, NormalizeExecutablePath(currentExecutable), StringComparison.OrdinalIgnoreCase);
        string message = different
            ? "开机启动项仍指向旧目录或另一份程序。请在当前路径重新启用并保存，以更新启动路径。"
            : "现有开机启动项的路径或后台参数不匹配，请重新启用并保存。";
        return new StartupRegistrationStatus(true, true, false, different, registeredPath, message);
    }

    private static string GetCurrentExecutable()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请直接运行发布版 EXE 后设置开机启动。");
        return NormalizeExecutablePath(path);
    }

    private static string NormalizeExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || executablePath.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0 ||
            !Path.IsPathFullyQualified(executablePath) || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("开机启动需要有效的 EXE 完整路径。", nameof(executablePath));
        return Path.GetFullPath(executablePath);
    }

    private static string ReadQuotedExecutable(string command)
    {
        if (command.Length < 3 || command[0] != '"') return "";
        int end = command.IndexOf('"', 1);
        if (end <= 1) return "";
        try { return NormalizeExecutablePath(command[1..end]); }
        catch (ArgumentException) { return ""; }
        catch (NotSupportedException) { return ""; }
    }

    private static bool IsExpectedError(Exception error)
        => error is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException or ArgumentException
            or NotSupportedException or COMException or TargetInvocationException or MissingMemberException;

    private static string DescribeError(Exception error, bool reading)
    {
        if (error is UnauthorizedAccessException or SecurityException)
            return reading ? "无法读取当前用户的开机启动设置，请检查 Windows 账户权限。" : "无法修改当前用户的开机启动设置，请检查 Windows 账户权限。";
        if (error is COMException or TargetInvocationException or MissingMemberException)
            return reading ? "启动快捷方式无法读取，请重新启用开机启动。" : "Windows 快捷方式服务未能保存启动项，请稍后重试。";
        if (error is ArgumentException or InvalidOperationException)
            return "当前程序路径或 Windows 启动文件夹不可用，请直接运行发布版 EXE 后重试。";
        return reading ? "开机启动状态读取失败，请稍后重试。" : "开机启动设置未能应用，请稍后重新保存。";
    }
}
