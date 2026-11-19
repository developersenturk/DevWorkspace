using System.Reflection;

var cli = Assembly.Load("DevWorkspace").GetType("DevWorkspace.GitHubCli", true)!;
var parse = cli.GetMethod("RepositoryNameFromUrl", BindingFlags.NonPublic | BindingFlags.Static)!;
int checks = 0;
foreach (string url in new[]
{
    "https://github.com/owner/repo", "https://github.com/owner/repo.git",
    " https://github.com/owner/repo/ ", "github.com/owner/repo",
    "git@github.com:owner/repo.git", "https://GITHUB.com/owner/repo"
})
{
    if ((string?)parse.Invoke(null, [url]) != "owner/repo")
        throw new Exception("Unexpected repository: " + url);
    checks++;
}
foreach (string url in new[]
{
    "", "owner/repo", "http://github.com/owner/repo", "https://example.com/owner/repo",
    "https://github.com.evil.test/owner/repo", "https://user:password@github.com/owner/repo",
    "https://github.com/owner/repo/tree/main", "https://github.com/owner/repo?token=secret",
    "https://github.com/owner/repo#readme", "https://github.com/owner/../repo",
    "https://github.com/owner/%2e%2e", "https://github.com:8443/owner/repo",
    "git@github.com:owner/../../repo.git", "--upload-pack=bad", "https://github.com/owner",
    "https://github.com/owner/.git", "file:///owner/repo", "https://github.com/owner/repo;command"
})
{
    try
    {
        parse.Invoke(null, [url]);
        throw new Exception("Invalid URL accepted: " + url);
    }
    catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException) { checks++; }
}
Console.WriteLine($"{checks} GitHub link checks passed.");
