using System.Security.Cryptography;
using System.Text;

namespace LocalLLM.Gateway;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Modo aplicador de actualización (Nivel 2): sin UI, sin mutex.
        if (args.Contains("--apply-update"))
            return UpdateApplier.RunAsync(args).GetAwaiter().GetResult();

        // Modo consola para diagnóstico: LocalLLM-Gateway.exe --profile  → imprime hardware + perfil en data\hardware.json
        if (args.Contains("--profile"))
        {
            var cfg = GatewayConfig.Load();
            var hw = HardwareProfiler.DetectAsync().GetAwaiter().GetResult();
            var p = HardwareProfiler.SelectProfile(hw, cfg);
            HardwareProfiler.UpdateSnapshot(hw, p);
            return 0;
        }

        // Una instancia por carpeta portable (dos copias en carpetas distintas no colisionan en el mutex,
        // aunque sí en el puerto 11434: la segunda mostrará el error).
        var rootHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PortablePaths.Root.ToLowerInvariant())))[..16];
        using var mutex = new Mutex(initiallyOwned: false, $"Local\\LocalLLM-Gateway-{rootHash}");
        var waitFor = args.Contains("--post-update") ? TimeSpan.FromSeconds(30) : TimeSpan.Zero;
        bool owned;
        try { owned = mutex.WaitOne(waitFor); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            MessageBox.Show("LocalLLM Portable ya se está ejecutando desde esta carpeta (ver bandeja del sistema).",
                "LocalLLM Portable", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.Error("[ui] Excepción no controlada", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("[app] Excepción fatal", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("[app] Tarea no observada", e.Exception); e.SetObserved(); };

        AppServices services;
        try
        {
            services = new AppServices();
        }
        catch (Exception ex)
        {
            Log.Error("[app] Configuración inválida", ex);
            MessageBox.Show($"No se pudo cargar profiles.json:\n{ex.Message}", "LocalLLM Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        Log.Info($"[app] LocalLLM Portable iniciado desde {PortablePaths.Root} (PID {Environment.ProcessId}).");
        Application.Run(new TrayApplicationContext(services));
        try { mutex.ReleaseMutex(); } catch { }
        return 0;
    }
}
