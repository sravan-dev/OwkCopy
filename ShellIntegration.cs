using Microsoft.Win32;

namespace OwkCopy;

/// <summary>Per-user Explorer context menu entries (HKCU, no admin needed).</summary>
static class ShellIntegration
{
    const string Key = "OwkCopyPaste";

    static readonly (string Root, string Arg, string Label)[] Targets =
    [
        (@"Software\Classes\Directory\Background\shell", "%V", "Owk Paste here"),
        (@"Software\Classes\Directory\shell", "%1", "Owk Paste into this folder"),
        (@"Software\Classes\Drive\shell", "%1", "Owk Paste into this drive"),
    ];

    static string ExePath => Environment.ProcessPath!;

    public static bool IsRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{Targets[0].Root}\{Key}\command");
        return k?.GetValue(null) is string cmd && cmd.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void Register()
    {
        foreach (var (root, arg, label) in Targets)
        {
            using var k = Registry.CurrentUser.CreateSubKey($@"{root}\{Key}");
            k.SetValue(null, label);
            k.SetValue("Icon", $"\"{ExePath}\",0");
            using var c = k.CreateSubKey("command");
            c.SetValue(null, $"\"{ExePath}\" paste \"{arg}\"");
        }
    }

    public static void Unregister()
    {
        foreach (var (root, _, _) in Targets)
            Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{Key}", false);
    }
}
