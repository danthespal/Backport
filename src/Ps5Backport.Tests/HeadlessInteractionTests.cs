using System.Diagnostics;

namespace Ps5Backport.Tests;

public class HeadlessInteractionTests
{
    [Theory]
    [InlineData("busy-cancel")]
    [InlineData("close-busy")]
    [InlineData("search-selection")]
    [InlineData("stale-analysis")]
    [InlineData("close-save-failure")]
    public async Task WindowBindingsAndInput_RunOnAnIsolatedHeadlessDispatcher(string scenario)
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-headless-" + Guid.NewGuid().ToString("N"))).FullName;
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        string worker = Path.Combine(Repo.Root, "verification", "UiProfile", "bin", configuration, "net10.0", "UiProfile.dll");
        var info = new ProcessStartInfo("dotnet") { WorkingDirectory = Repo.Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { worker, "--check", scenario, root }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, $"{scenario}: {await stdout}\n{await stderr}");
            Assert.Contains("Passed: " + scenario, await stdout);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
            Directory.Delete(root, recursive: true);
        }
    }
}
