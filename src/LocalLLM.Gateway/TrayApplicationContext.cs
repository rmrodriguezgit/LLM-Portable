using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LocalLLM.Gateway;

/// <summary>
/// Interfaz en la bandeja del sistema: estado en vivo (incluye % de descarga de herramientas/modelo),
/// métricas, sincronización Git manual y "Iniciar con Windows" mediante un .lnk en la carpeta Startup
/// del usuario (%AppData%\Microsoft\Windows\Start Menu\Programs\Startup) — sin Admin, sin HKLM.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppServices _app;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem, _modelItem, _memoryItem, _startupItem, _syncItem;
    private readonly System.Windows.Forms.Timer _refresh;
    private readonly SynchronizationContext _ui;
    private EngineState _lastIconState = (EngineState)(-1);
    private bool _exiting;

    private static string StartupShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "LocalLLM Portable.lnk");

    public TrayApplicationContext(AppServices app)
    {
        _app = app;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _statusItem = new ToolStripMenuItem("Iniciando...") { Enabled = false };
        _modelItem = new ToolStripMenuItem("Modelo: —") { Enabled = false };
        _memoryItem = new ToolStripMenuItem("Memoria: —") { Enabled = false };
        _syncItem = new ToolStripMenuItem("Sincronizar desde Git ahora", null, async (_, _) => await SyncNowAsync());
        _startupItem = new ToolStripMenuItem("Iniciar con Windows", null, (_, _) => ToggleStartup()) { Checked = File.Exists(StartupShortcut) };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem, _modelItem, _memoryItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Abrir métricas (/v1/metrics)", null, (_, _) => OpenUrl($"http://127.0.0.1:{_app.Config.Gateway.Port}/v1/metrics")),
            new ToolStripMenuItem("Ver perfil de hardware", null, (_, _) => OpenUrl($"http://127.0.0.1:{_app.Config.Gateway.Port}/v1/hardware-profile")),
            new ToolStripMenuItem("Copiar URL de la API (OpenAI)", null, (_, _) => Clipboard.SetText($"http://127.0.0.1:{_app.Config.Gateway.Port}/v1")),
            _syncItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Abrir carpeta de datos", null, (_, _) => OpenFolder(PortablePaths.Data)),
            new ToolStripMenuItem("Abrir registros", null, (_, _) => OpenFolder(PortablePaths.Logs)),
            _startupItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Salir", null, async (_, _) => await ExitAsync())
        });

        _tray = new NotifyIcon
        {
            Text = "LocalLLM Portable",
            Icon = BuildIcon(Color.Gray),
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => OpenUrl($"http://127.0.0.1:{_app.Config.Gateway.Port}/v1/metrics");

        _app.StatusChanged += () => _ui.Post(_ => Refresh(), null);
        _app.ExitRequested += () => _ui.Post(async _ => await ExitAsync(), null);

        _refresh = new System.Windows.Forms.Timer { Interval = 2000 };
        _refresh.Tick += (_, _) => Refresh();
        _refresh.Start();

        _ = Task.Run(_app.StartAsync);
    }

    private void Refresh()
    {
        if (_exiting) return;
        var pct = _app.ProgressPercent is { } p ? $" ({p:F0}%)" : "";
        _statusItem.Text = Truncate(_app.StatusText + pct, 90);
        if (_app.Ollama is { } o)
        {
            var pr = o.Profile;
            _modelItem.Text = $"Modelo: {pr.Model} · ctx {pr.NumCtx} · x{pr.NumParallel} · {pr.Target.ToUpperInvariant()}";
        }
        if (_app.Guard is { } g)
        {
            var m = g.Current;
            var vram = m.FreeVramMb is { } fv ? $" · VRAM libre {fv / 1024.0:F1}/{(m.TotalVramMb ?? 0) / 1024.0:F1} GB" : "";
            _memoryItem.Text = $"RAM libre {m.FreeRamMb / 1024.0:F1}/{m.TotalRamMb / 1024.0:F1} GB{vram}{(m.CircuitOpen ? " · 429 ACTIVO" : "")}";
        }
        if (_app.Gateway is { } gw)
            _statusItem.ToolTipText = $"Activas: {gw.Gate.Active} · En cola: {gw.Gate.Queued}";

        // NotifyIcon.Text admite máx. 127 caracteres.
        _tray.Text = Truncate($"LocalLLM · {_app.StatusText}{pct}", 127);

        if (_app.State != _lastIconState)
        {
            _lastIconState = _app.State;
            var old = _tray.Icon;
            _tray.Icon = BuildIcon(_app.State switch
            {
                EngineState.Ready => Color.FromArgb(46, 160, 67),
                EngineState.Degraded => Color.FromArgb(218, 54, 51),
                EngineState.Provisioning or EngineState.PullingModel or EngineState.Updating => Color.FromArgb(210, 153, 34),
                _ => Color.Gray
            });
            old?.Dispose();
            if (_app.State == EngineState.Degraded)
                _tray.ShowBalloonTip(4000, "LocalLLM Portable", Truncate(_app.StatusText, 200), ToolTipIcon.Warning);
        }
    }

    private async Task SyncNowAsync()
    {
        if (_app.Updater is null) return;
        _syncItem.Enabled = false;
        try
        {
            var r = await Task.Run(() => _app.Updater.SyncAsync("tray", CancellationToken.None));
            _tray.ShowBalloonTip(5000, "Sincronización Git", Truncate(r.Message, 200), r.Success ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        finally { _syncItem.Enabled = true; }
    }

    // ------------------------------------------------------------------ Iniciar con Windows (sin Admin)

    private void ToggleStartup()
    {
        try
        {
            if (File.Exists(StartupShortcut))
            {
                File.Delete(StartupShortcut);
                _startupItem.Checked = false;
            }
            else
            {
                CreateShortcut(StartupShortcut, PortablePaths.GatewayExe, PortablePaths.Root);
                _startupItem.Checked = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("[tray] No se pudo cambiar el inicio automático", ex);
            MessageBox.Show($"No se pudo cambiar el inicio automático:\n{ex.Message}", "LocalLLM Portable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Crea un .lnk mediante WScript.Shell (COM disponible para cualquier usuario estándar).</summary>
    private static void CreateShortcut(string lnkPath, string target, string workingDir)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell no disponible.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(lnkPath);
            try
            {
                lnk.TargetPath = target;
                lnk.WorkingDirectory = workingDir;
                lnk.Description = "LocalLLM Portable (Zero-Admin)";
                lnk.IconLocation = target + ",0";
                lnk.Save();
            }
            finally { Marshal.FinalReleaseComObject(lnk); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    // ------------------------------------------------------------------ utilidades

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { Log.Warn($"[tray] {ex.Message}"); }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Icono generado en tiempo de ejecución (sin recursos externos): círculo de color de estado.</summary>
    private static Icon BuildIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(30, 30, 36));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var ring = new Pen(color, 4);
            g.DrawEllipse(ring, 4, 4, 24, 24);
            using var font = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.White);
            var size = g.MeasureString("AI", font);
            g.DrawString("AI", font, fg, (32 - size.Width) / 2, (32 - size.Height) / 2);
        }
        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _refresh.Stop();
        _tray.Text = "LocalLLM · Deteniendo...";
        try { await _app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (Exception ex) { Log.Warn($"[tray] Apagado con errores: {ex.Message}"); }
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }
}
