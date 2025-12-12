using System;
using System.Text.RegularExpressions;

namespace DS4Updater
{
    public class RepoConfig
    {
        private const string DefaultDS4UpdaterRepo = "https://github.com/gwin7ok/DS4Updater";
        private const string DefaultDS4WindowsRepo = "https://github.com/gwin7ok/DS4Windows-Vader4Pro";

        public string DS4UpdaterRepoUrl { get; private set; }
        public string DS4UpdaterApiLatestUrl { get; private set; }
        public string DS4WindowsRepoUrl { get; private set; }
        public string DS4WindowsApiLatestUrl { get; private set; }

        public RepoConfig(string ds4UpdaterRepoUrl, string ds4WindowsRepoUrl)
        {
            DS4UpdaterRepoUrl = (ds4UpdaterRepoUrl ?? DefaultDS4UpdaterRepo).TrimEnd('/');
            DS4WindowsRepoUrl = (ds4WindowsRepoUrl ?? DefaultDS4WindowsRepo).TrimEnd('/');

            var mu = Regex.Match(DS4UpdaterRepoUrl, @"github\.com/([^/]+)/([^/]+)", RegexOptions.IgnoreCase);
            if (mu.Success)
            {
                var owner = mu.Groups[1].Value;
                var repo = mu.Groups[2].Value;
                DS4UpdaterApiLatestUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            }
            else
            {
                DS4UpdaterApiLatestUrl = null;
            }

            var mt = Regex.Match(DS4WindowsRepoUrl, @"github\.com/([^/]+)/([^/]+)", RegexOptions.IgnoreCase);
            if (mt.Success)
            {
                var owner = mt.Groups[1].Value;
                var repo = mt.Groups[2].Value;
                DS4WindowsApiLatestUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            }
            else
            {
                DS4WindowsApiLatestUrl = null;
            }
        }

        // Use Environment.GetCommandLineArgs() (includes exe path at index 0)
        public static RepoConfig FromEnvironmentArgs()
        {
            var args = Environment.GetCommandLineArgs();
            if (args == null || args.Length <= 1) return new RepoConfig(null, null);
            var trimmed = new string[args.Length - 1];
            Array.Copy(args, 1, trimmed, 0, trimmed.Length);
            return FromArgs(trimmed);
        }

        // Parse args: support --ds4updater-repo, --ds4windows-repo, and legacy --base-url (treated as ds4windows)
        public static RepoConfig FromArgs(string[] args)
        {
            string updater = null;
            string target = null;
            if (args == null || args.Length == 0) return new RepoConfig(updater, target);

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--ds4updater-repo=", StringComparison.OrdinalIgnoreCase))
                {
                    updater = a.Substring("--ds4updater-repo=".Length).Trim();
                }
                else if (a.Equals("--ds4updater-repo", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    updater = args[++i].Trim();
                }
                else if (a.StartsWith("--ds4windows-repo=", StringComparison.OrdinalIgnoreCase))
                {
                    target = a.Substring("--ds4windows-repo=".Length).Trim();
                }
                else if (a.Equals("--ds4windows-repo", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    target = args[++i].Trim();
                }
                else if (a.StartsWith("--base-url=", StringComparison.OrdinalIgnoreCase))
                {
                    // legacy: treat as ds4windows
                    target = a.Substring("--base-url=".Length).Trim();
                }
                else if (a.Equals("--base-url", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    target = args[++i].Trim();
                }
                else if (i == 0 && Uri.IsWellFormedUriString(a, UriKind.Absolute) && a.Contains("github.com"))
                {
                    // positional first arg -> ds4windows
                    target = a.Trim();
                }
            }

            return new RepoConfig(updater, target);
        }
    }
}
