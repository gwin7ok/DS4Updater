using System;
using System.Text.RegularExpressions;

namespace DS4Updater
{
    public class RepoConfig
    {
        private const string DefaultBase = "https://github.com/gwin7ok/DS4Updater";
        public string BaseRepoUrl { get; private set; }
        public string ApiLatestUrl { get; private set; }

        public RepoConfig(string baseRepoUrl)
        {
            BaseRepoUrl = (baseRepoUrl ?? DefaultBase).TrimEnd('/');
            var m = Regex.Match(BaseRepoUrl, @"github\.com/([^/]+)/([^/]+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var owner = m.Groups[1].Value;
                var repo = m.Groups[2].Value;
                ApiLatestUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            }
            else
            {
                ApiLatestUrl = null;
            }
        }

        // Use Environment.GetCommandLineArgs() (skips program path)
        public static RepoConfig FromEnvironmentArgs()
        {
            var args = Environment.GetCommandLineArgs();
            // Environment.GetCommandLineArgs() includes exe path at index 0
            if (args == null || args.Length <= 1) return new RepoConfig(DefaultBase);
            // build array excluding index 0
            var trimmed = new string[args.Length - 1];
            Array.Copy(args, 1, trimmed, 0, trimmed.Length);
            return FromArgs(trimmed);
        }

        // Use with App.xaml.cs e.Args (doesn't include exe path)
        public static RepoConfig FromArgs(string[] args)
        {
            string baseRepo = DefaultBase;
            if (args == null || args.Length == 0) return new RepoConfig(baseRepo);

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--base-url=", StringComparison.OrdinalIgnoreCase))
                {
                    baseRepo = a.Substring("--base-url=".Length).Trim();
                }
                else if (a.Equals("--base-url", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    baseRepo = args[++i].Trim();
                }
                else if (i == 0 && Uri.IsWellFormedUriString(a, UriKind.Absolute) && a.Contains("github.com"))
                {
                    baseRepo = a.Trim();
                }
            }

            return new RepoConfig(baseRepo);
        }
    }
}
