namespace DS4Updater.Dtos
{
    // Release API endpoint is determined by `RepoConfig` at runtime.
    // Default DS4Windows endpoint: see `RepoConfig.DefaultDS4WindowsRepo` (or pass --ds4windows-repo).
    public record GitHubRelease(string tag_name);
}
