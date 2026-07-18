using System.Threading;

namespace DropFolders;

static class Program
{
    private const string MutexName = "DropFolders_SingleInstance_b3c9a1d2";

    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        var createdNew = false;
        using var mutex = new Mutex(true, MutexName, out createdNew);
        if (!createdNew) return;

        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
    }
}