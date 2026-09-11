using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace EPATA.BusinessLedger.Services;

internal sealed class WindowsTrayApplication : IDisposable
{
    private readonly string _appUrl;
    private readonly Action _stopApplication;
    private readonly ManualResetEventSlim _started = new(false);
    private readonly ManualResetEventSlim _stopRequested = new(false);
    private readonly Thread _thread;
    private Exception? _startupError;
    private int _disposed;

    private WindowsTrayApplication(string appUrl, Action stopApplication)
    {
        _appUrl = appUrl;
        _stopApplication = stopApplication;
        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "EPATA notification area icon"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_started.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The EPATA notification area icon did not start in time.");
        }

        if (_startupError is not null)
        {
            throw new InvalidOperationException("The EPATA notification area icon could not start.", _startupError);
        }
    }

    public static WindowsTrayApplication Start(string appUrl, Action stopApplication) =>
        new(appUrl, stopApplication);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopRequested.Set();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(3));
        }

        _started.Dispose();
        _stopRequested.Dispose();
    }

    private void RunMessageLoop()
    {
        NotifyIcon? notifyIcon = null;
        ContextMenuStrip? menu = null;
        System.Windows.Forms.Timer? shutdownTimer = null;
        Icon? icon = null;

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

            icon = GetApplicationIcon();
            ToolStripMenuItem openItem = new("Open EPATA Business Ledger");
            ToolStripMenuItem exitItem = new("Exit EPATA Business Ledger");
            menu = new ContextMenuStrip();
            menu.Items.Add(openItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            notifyIcon = new NotifyIcon
            {
                ContextMenuStrip = menu,
                Icon = icon,
                Text = "EPATA Business Ledger — double-click to open",
                Visible = true
            };

            openItem.Click += (_, _) => OpenBrowser();
            notifyIcon.DoubleClick += (_, _) => OpenBrowser();
            exitItem.Click += (_, _) =>
            {
                _stopRequested.Set();
                _stopApplication();
            };

            shutdownTimer = new System.Windows.Forms.Timer { Interval = 250 };
            shutdownTimer.Tick += (_, _) =>
            {
                if (!_stopRequested.IsSet)
                {
                    return;
                }

                notifyIcon.Visible = false;
                Application.ExitThread();
            };
            shutdownTimer.Start();

            _started.Set();
            Application.Run();
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _started.Set();
        }
        finally
        {
            if (notifyIcon is not null)
            {
                notifyIcon.Visible = false;
            }

            shutdownTimer?.Dispose();
            notifyIcon?.Dispose();
            menu?.Dispose();
            icon?.Dispose();
        }
    }

    private void OpenBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_appUrl) { UseShellExecute = true });
        }
        catch
        {
            // The tray menu remains available if the default browser cannot be opened.
        }
    }

    private static Icon GetApplicationIcon()
    {
        if (Environment.ProcessPath is { Length: > 0 } processPath)
        {
            Icon? extractedIcon = Icon.ExtractAssociatedIcon(processPath);
            if (extractedIcon is not null)
            {
                return extractedIcon;
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
