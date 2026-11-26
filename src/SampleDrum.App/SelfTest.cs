using System.Diagnostics;
using NAudio.Wave.Asio;
using SampleDrum.App.Audio;
using SampleDrum.App.Harness;
using SampleDrum.Core.Simulation;

namespace SampleDrum.App;

/// <summary>
/// Headless smoke test (no ASIO, no X32): native ASIO sample converters, the shared harness checks (same code as
/// tests/SampleDrum.Core.Tests/HarnessTests.cs) and the real-time simulation-host checks. Exit 0 = everything passed.
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine("SampleDrum --selftest (sem ASIO, sem mesa)");
        var results = new List<CheckResult>();
        results.Add(HarnessChecks.Safe("Conversão float <-> formatos nativos ASIO (Int32/Float32/Int16/Int24 LSB, com saturação)", CheckConverters));
        results.AddRange(HarnessChecks.RunAll(Path.GetTempPath()));
        results.AddRange(RealtimeChecks.RunAll(speed: 4.0));

        foreach (var r in results)
        {
            Console.WriteLine($"  [{(r.Passed ? "OK" : "FALHA")}] {r.Name}");
            Console.WriteLine($"         {r.Detail}");
        }
        int failed = results.Count(r => !r.Passed);
        Console.WriteLine($"{results.Count - failed}/{results.Count} verificações OK em {sw.Elapsed.TotalSeconds:0.0} s");
        Console.WriteLine(failed == 0 ? "RESULTADO: OK" : "RESULTADO: FALHOU");
        return failed == 0 ? 0 : 1;
    }

    private static (bool, string) CheckConverters()
    {
        float[] src = [0f, 0.5f, -0.5f, 0.999f, -1f, 1.5f, -1.5f, 0.123456f];
        var bad = new List<string>();
        foreach (var type in new[] { AsioSampleType.Int32LSB, AsioSampleType.Float32LSB, AsioSampleType.Int16LSB, AsioSampleType.Int24LSB })
        {
            var bytes = new byte[src.Length * NativeSampleConverter.BytesPerSample(type)];
            var back = new float[src.Length];
            NativeSampleConverter.FromFloat(src, type, bytes);
            NativeSampleConverter.ToFloat(bytes, type, back);
            float tol = type == AsioSampleType.Int16LSB ? 1e-4f : 1e-6f;
            for (int i = 0; i < src.Length; i++)
                if (MathF.Abs(back[i] - Math.Clamp(src[i], -1f, 1f)) > tol) { bad.Add(type.ToString()); break; }
        }
        return (bad.Count == 0, bad.Count == 0 ? "4 formatos ida e volta" : "falhou: " + string.Join(", ", bad));
    }
}
