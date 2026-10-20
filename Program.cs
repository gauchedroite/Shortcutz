using System.Threading;

namespace Shortcutz;

static class Program
{
    private const string MutexName = "Shortcutz_SingleInstance_b3c9a1d2";

    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        var createdNew = false;
        using var mutex = new Mutex(true, MutexName, out createdNew);
        if (!createdNew) return;

        DiagnosticLog.Start();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            DiagnosticLog.WriteException("UI thread", e.Exception);
            MessageBox.Show($"An error was recorded in {DiagnosticLog.FilePath}.\n\n{e.Exception.Message}",
                "Shortcutz error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown exception");
            DiagnosticLog.WriteException($"Unhandled process exception; terminating={e.IsTerminating}", exception);
        };

        try
        {
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteException("Application startup", ex);
            throw;
        }
        finally
        {
            DiagnosticLog.Write("STOP");
            DiagnosticLog.Stop();
        }
    }
}
