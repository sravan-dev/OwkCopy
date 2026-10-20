using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace OwkCopy;

/// <summary>
/// Command line:
///   OwkCopy.exe                          open the window
///   OwkCopy.exe paste "C:\target"        paste Explorer clipboard (copy or cut) into target
///   OwkCopy.exe copy|move src... dst     run a job immediately
///   OwkCopy.exe --agent                  tray agent that takes over Ctrl+V in Explorer
///   OwkCopy.exe --default-on|--default-off  make Owk Copy the Ctrl+V handler (starts at login)
///   OwkCopy.exe --register|--unregister  add/remove the Explorer right-click entries
/// </summary>
public partial class App : Application
{
    Mutex? _agentMutex;
    Forms.NotifyIcon? _tray;
    PasteAgent? _agent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        if (args.Length >= 1)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--register":
                    ShellIntegration.Register();
                    Shutdown();
                    return;
                case "--unregister":
                    ShellIntegration.Unregister();
                    Shutdown();
                    return;
                case "--default-on" or "--default-off":
                    PasteAgent.SetDefault(args[0].Equals("--default-on", StringComparison.OrdinalIgnoreCase));
                    Shutdown();
                    return;
                case "--agent":
                    RunAgent();
                    return;
            }
        }

        var window = new MainWindow();
        if (args.Length >= 2 && args[0].Equals("paste", StringComparison.OrdinalIgnoreCase))
        {
            if (!ReadClipboard(out var files, out var move))
            {
                MessageBox.Show("Clipboard has no files. Copy or cut files in Explorer first.", "Owk Copy",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            window.StartJob(files, CleanArg(args[1]), move, autoClose: true, clearClipboardOnSuccess: move);
        }
        else if (args.Length >= 3 && args[0].ToLowerInvariant() is "copy" or "move")
        {
            window.StartJob(args[1..^1].Select(CleanArg), CleanArg(args[^1]),
                args[0].Equals("move", StringComparison.OrdinalIgnoreCase), autoClose: true);
        }

        MainWindow = window;
        window.Show();
    }

    /// <summary>Undo the "E:\" -> E:" quoting accident and make bare drive letters absolute.</summary>
    static string CleanArg(string a)
    {
        a = a.Trim().Trim('"');
        return a.Length == 2 && a[1] == ':' ? a + "\\" : a;
    }

    void RunAgent()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _agentMutex = new Mutex(true, PasteAgent.MutexName, out bool first);
        if (!first) { Shutdown(); return; }

        _agent = new PasteAgent(Dispatcher);
        _agent.Start();

        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, PasteAgent.QuitEventName);
        ThreadPool.RegisterWaitForSingleObject(quit, (_, _) => Dispatcher.BeginInvoke(ExitAgent), null, -1, true);

        var exe = Environment.ProcessPath!;
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(exe),
            Text = "Owk Copy: handling Ctrl+V in Explorer",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _tray.ContextMenuStrip.Items.Add("Open Owk Copy", null, (_, _) => System.Diagnostics.Process.Start(exe));
        var toggle = new Forms.ToolStripMenuItem("Handle Ctrl+V in Explorer") { Checked = true, CheckOnClick = true };
        toggle.CheckedChanged += (_, _) =>
        {
            _agent.Enabled = toggle.Checked;
            _tray.Text = toggle.Checked ? "Owk Copy: handling Ctrl+V in Explorer" : "Owk Copy: paused";
        };
        _tray.ContextMenuStrip.Items.Add(toggle);
        _tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add("Stop being the default copy handler", null, (_, _) =>
        {
            PasteAgent.SetDefault(false);
            ExitAgent();
        });
        _tray.DoubleClick += (_, _) => System.Diagnostics.Process.Start(exe);
    }

    void ExitAgent()
    {
        _agent?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _agentMutex?.Dispose();
        Shutdown();
    }

    static bool ReadClipboard(out string[] files, out bool move)
    {
        files = [];
        move = false;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (!Clipboard.ContainsFileDropList()) return false;
                files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
                {
                    var effect = BitConverter.ToInt32(ms.ToArray(), 0);
                    move = (effect & 2) != 0; // DROPEFFECT_MOVE => files were cut
                }
                return files.Length > 0;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(100); // clipboard busy, retry
            }
        }
        return false;
    }
}
