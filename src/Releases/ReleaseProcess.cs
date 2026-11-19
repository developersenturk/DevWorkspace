using System.Diagnostics;
using System.IO;
using System.Text;

namespace DevWorkspace;

internal interface IReleaseProcess
{
    Task<string> RunAsync(string tool, string[] arguments, string directory, Action<string>? log, CancellationToken token);
}

internal sealed class ReleaseProcess(Func<string, string>? resolve = null) : IReleaseProcess
{
    public async Task<string> RunAsync(string tool, string[] arguments, string directory, Action<string>? log, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(resolve?.Invoke(tool) ?? tool)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in arguments) info.ArgumentList.Add(arg);
        info.Environment["GH_PROMPT_DISABLED"] = "1";
        info.Environment["GH_HOST"] = "github.com";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        info.Environment["NO_COLOR"] = "1";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        using var process = Process.Start(info) ?? throw new IOException(tool);
        process.StandardInput.Close();
        using var cancellation = timeout.Token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        async Task<string> ReadAsync(StreamReader reader)
        {
            var text = new StringBuilder();
            while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                if (text.Length < 8 * 1024 * 1024) text.AppendLine(line);
                log?.Invoke(line);
            }
            return text.ToString();
        }
        var output = ReadAsync(process.StandardOutput);
        var error = ReadAsync(process.StandardError);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new IOException($"{tool} ({process.ExitCode}): " + (await error + await output).Trim()[..Math.Min(2500, (await error + await output).Trim().Length)]);
            return await output;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException(ReleaseText.T("Timeout")); }
    }
}
