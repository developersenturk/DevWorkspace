using System.Diagnostics;
using System.IO;
using System.Text;

namespace DevWorkspace;

internal static class SshAskPass
{
    private const string PromptMode = "DEVWORKSPACE_SSH_ASKPASS";

    internal static void Configure(ProcessStartInfo startInfo)
    {
        // OpenSSH launches this executable itself and passes the prompt as one argument.
        if (Environment.ProcessPath is not { } executable ||
            !Path.GetFileName(executable).Equals("DevWorkspace.exe", StringComparison.OrdinalIgnoreCase))
            return;

        startInfo.Environment["SSH_ASKPASS"] = executable.Replace('\\', '/');
        startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        startInfo.Environment.Remove("SSH_ASKPASS_PROMPT");
        startInfo.Environment[PromptMode] = "1";
    }

    internal static int? TryRun(string[] args)
    {
        if (Environment.GetEnvironmentVariable(PromptMode) != "1")
            return null;
        if (args.Length != 1 || !Console.IsOutputRedirected)
            return 1;

        string prompt = args[0].Trim();
        string? hint = Environment.GetEnvironmentVariable("SSH_ASKPASS_PROMPT");
        bool hostKey = prompt.Contains("Are you sure you want to continue connecting", StringComparison.OrdinalIgnoreCase);
        bool confirmation = hostKey || hint == "confirm";
        var window = new SshPromptWindow(prompt, hostKey, confirmation, hint == "none");
        if (window.ShowDialog() != true)
            return 1;

        // Stdout is OpenSSH's response pipe. Never echo the response to status or logs.
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        output.Write(confirmation ? "yes" : window.Response);
        output.Write('\n');
        return 0;
    }
}
