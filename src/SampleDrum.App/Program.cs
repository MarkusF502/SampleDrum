using System.Runtime.InteropServices;
using SampleDrum.App.Audio;
using SampleDrum.App.Harness;

namespace SampleDrum.App;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    /// <summary>
    /// Command line:
    ///   (none)              WinForms UI
    ///   --simulation        WinForms UI already in the "Simulação" profile with the synthetic kit (no ASIO needed)
    ///   --harness &lt;pasta&gt; [...] scenario runner (see docs/harness.md / --harness --help); exit 0 = all expectations pass
    ///   --selftest          headless checks (converters, harness checks, real-time simulation host); exit 0 = pass
    ///   --list-drivers      prints the installed ASIO drivers
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        string first = args.Length > 0 ? args[0] : "";
        if (first is "--selftest" or "--list-drivers" or "--harness")
        {
            AttachConsole(-1); // WinExe: reuse the parent console when there is one (no-op if output is redirected)
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            switch (first)
            {
                case "--list-drivers":
                    var names = AsioDuplexHost.GetDriverNames();
                    Console.WriteLine(names.Length == 0
                        ? "Nenhum driver ASIO instalado (HKLM\\SOFTWARE\\ASIO vazio)."
                        : $"Drivers ASIO instalados ({names.Length}):\n  " + string.Join("\n  ", names));
                    return 0;
                case "--harness":
                    return HarnessCommand.Run(args[1..]);
                default:
                    return SelfTest.Run();
            }
        }

        if (args.Contains("--simulation")) StartupOptions.SimulationMode = true;
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
