using System.Runtime.InteropServices;
using SampleDrum.App.Audio;

namespace SampleDrum.App;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    /// <summary>
    /// Command line:
    ///   (none)            WinForms UI
    ///   --selftest [s]    headless: simulation host + TriggerEngine for s seconds (default 3), prints counts, exit 0 on success
    ///   --list-drivers    prints the installed ASIO drivers
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "--selftest" || args[0] == "--list-drivers"))
        {
            AttachConsole(-1); // WinExe: reuse the parent console when there is one (no-op if output is redirected)
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            if (args[0] == "--list-drivers")
            {
                var names = AsioDuplexHost.GetDriverNames();
                Console.WriteLine(names.Length == 0
                    ? "Nenhum driver ASIO instalado (HKLM\\SOFTWARE\\ASIO vazio)."
                    : $"Drivers ASIO instalados ({names.Length}):\n  " + string.Join("\n  ", names));
                return 0;
            }
            double seconds = args.Length > 1 && double.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 3.0;
            return SelfTest.Run(seconds);
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
