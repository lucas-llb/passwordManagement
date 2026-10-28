using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Vault;

/// <summary>
/// Measures how long <see cref="VaultSession.Unlock"/> takes with <see cref="KdfParameters.Default"/> (64 MiB, 3
/// passes) on the machine that runs it. It is an explicit test: normal runs never execute it. It runs one warm-up
/// unlock and then five measured ones, prints the machine, the KDF parameters and min, median and max in ms, and
/// fails only if the median is above 1500 ms (the upper bound in AGENTS.md §3.2). The numbers go in
/// <c>docs/architecture.md</c> §4.3. Every value here is fictitious.
/// </summary>
/// <remarks>
/// <para>Run from <c>windows/</c> (Release, so the timing is not inflated by Debug code):</para>
/// <code>
/// dotnet test --project tests/PasswordManager.Core.Tests/PasswordManager.Core.Tests.csproj -c Release --filter-class "PasswordManager.Core.Tests.Vault.UnlockTimingMeasurement" --explicit only --show-live-output on --output Detailed
/// </code>
/// <para>The output lines appear only with <c>--output Detailed</c> (<c>--show-live-output on</c> alone prints
/// nothing in <c>dotnet test</c>).</para>
/// <para>To measure another machine (for example a modest one), without the SDK:</para>
/// <list type="number">
/// <item>Run <c>dotnet build tests/PasswordManager.Core.Tests/PasswordManager.Core.Tests.csproj -c Release</c>.</item>
/// <item>Copy <c>tests/PasswordManager.Core.Tests/bin/Release/net10.0/</c> to that machine (it needs the .NET 10
/// runtime, not the SDK).</item>
/// <item>Run <c>PasswordManager.Core.Tests.exe -class "PasswordManager.Core.Tests.Vault.UnlockTimingMeasurement" -explicit only -showLiveOutput</c>
/// in that folder.</item>
/// </list>
/// </remarks>
public class UnlockTimingMeasurement
{
    private const string FictitiousPassword = "fictitious unlock timing passphrase";
    private const int MeasuredRuns = 5;
    private const double MedianLimitMs = 1500;

    [Fact(Explicit = true)]
    public void Measure()
    {
        var output = TestContext.Current.TestOutputHelper!;
        var parameters = KdfParameters.Default;
        var directory = Directory.CreateTempSubdirectory("pm-unlock-timing-");
        try
        {
            var path = Path.Combine(directory.FullName, $"{Guid.NewGuid()}.pmvault");
            var kitText = new char[RecoveryKit.TextLength];
            try
            {
                VaultFile.Write(path, VaultSession.Create(FictitiousPassword, kitText, parameters));
            }
            finally
            {
                Array.Clear(kitText);
            }

            // Warm-up: JIT, the Argon2id library load and the page cache are not part of the numbers.
            using (var warmUp = new VaultSession(path))
            {
                warmUp.Unlock(FictitiousPassword);
                warmUp.Lock();
            }

            var samplesMs = new double[MeasuredRuns];
            for (var i = 0; i < MeasuredRuns; i++)
            {
                using var session = new VaultSession(path);
                var start = Stopwatch.GetTimestamp();
                session.Unlock(FictitiousPassword);
                samplesMs[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

                Assert.True(session.IsUnlocked);
                session.Lock();
            }

            var sorted = samplesMs.Order().ToArray();
            var median = sorted[MeasuredRuns / 2];

            output.WriteLine($"Unlock timing, KdfParameters.Default: memory {parameters.MemoryKiB} KiB, iterations {parameters.Iterations}, parallelism {VaultFormat.DefaultKdfParallelism}.");
            output.WriteLine($"OS: {RuntimeInformation.OSDescription}");
            output.WriteLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
            output.WriteLine($"Logical processors: {Environment.ProcessorCount}");
            output.WriteLine($"CPU identifier: {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown"}");
            output.WriteLine($"Memory available to GC: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)} MiB");
            output.WriteLine($"Runs: {MeasuredRuns} after 1 warm-up. Min {Format(sorted[0])} ms, median {Format(median)} ms, max {Format(sorted[^1])} ms.");
            output.WriteLine($"Samples (ms): {string.Join(", ", samplesMs.Select(Format))}");

            Assert.True(
                median <= MedianLimitMs,
                $"Median unlock {Format(median)} ms is above the {MedianLimitMs} ms bound (AGENTS.md §3.2).");
        }
        finally
        {
            if (Directory.Exists(directory.FullName))
            {
                directory.Delete(recursive: true);
            }
        }
    }

    private static string Format(double milliseconds) =>
        milliseconds.ToString("F1", CultureInfo.InvariantCulture);
}
