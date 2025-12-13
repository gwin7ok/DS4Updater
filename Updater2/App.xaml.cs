/*
DS4Updater
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Net.Http;
using System.Net.Http.Json;
using DS4Updater.Dtos;
using System.Linq;

namespace DS4Updater
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private string exedirpath = AppContext.BaseDirectory;
        public static bool openingDS4W;
        // When true, do not delete Update Files on Exit because a replacer batch will use them
        private bool skipUpdateFilesCleanupOnExit = false;
        private string launchExeName;
        private string launchExePath;
        private MainWindow mwd;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            Logger.Log($"Application_Startup: args={string.Join(' ', e.Args)}");
            try { Logger.Log($"Application_Startup: CurrentDirectory={Environment.CurrentDirectory}"); } catch { }

            // Parse args first into locals so we can perform self-update before creating MainWindow
            // allow overriding paths via args
            string ds4windowsPath = null;
            string ds4updaterPath = null;
            string preferredLaunchMode = null;
            bool parsedAutoLaunch = false;
            bool parsedForceLaunchUser = false;
            bool parsedDownloadLang = true;
            launchExePath = Path.Combine(exedirpath, "DS4Windows.exe");
            for (int i=0, arlen = e.Args.Length; i < arlen; i++)
            {
                string temp = e.Args[i];
                if (temp.Contains("-skipLang"))
                {
                    parsedDownloadLang = false;
                }
                else if (temp.Equals("-autolaunch"))
                {
                    parsedAutoLaunch = true;
                }
                else if (temp.Equals("-user"))
                {
                    parsedForceLaunchUser = true;
                }
                else if (temp.Equals("--launchExe"))
                {
                    if ((i+1) < arlen)
                    {
                        i++;
                        temp = e.Args[i];
                        string tempPath = Path.Combine(exedirpath, temp);
                        if (File.Exists(tempPath))
                        {
                            launchExeName = temp;
                            launchExePath = tempPath;
                        }
                    }
                }
                else if (temp.StartsWith("--ds4windows-path=", StringComparison.OrdinalIgnoreCase))
                {
                    ds4windowsPath = temp.Substring("--ds4windows-path=".Length).Trim();
                }
                else if (temp.Equals("--ds4windows-path", StringComparison.OrdinalIgnoreCase) && i + 1 < arlen)
                {
                    ds4windowsPath = e.Args[++i];
                }
                else if (temp.StartsWith("--ds4updater-path=", StringComparison.OrdinalIgnoreCase))
                {
                    ds4updaterPath = temp.Substring("--ds4updater-path=".Length).Trim();
                }
                else if (temp.Equals("--ds4updater-path", StringComparison.OrdinalIgnoreCase) && i + 1 < arlen)
                {
                    ds4updaterPath = e.Args[++i];
                }
                else if (temp.Equals("--ci", StringComparison.OrdinalIgnoreCase))
                {
                    UpdaterResult.IsCiMode = true;
                }
                else if (temp.StartsWith("--launch-mode=", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var mode = temp.Substring("--launch-mode=".Length).Trim();
                        if (!string.IsNullOrEmpty(mode))
                        {
                            preferredLaunchMode = mode;
                        }
                    }
                    catch { }
                }
            }

            // If updater path provided, make it the exe dir used for self-update tasks
            try { if (!string.IsNullOrEmpty(ds4updaterPath)) exedirpath = Path.GetFullPath(ds4updaterPath); } catch { }

            // Inject parsed repo configuration (ds4updater and ds4windows repos)
            RepoConfig cfg = null;
            try
            {
                cfg = RepoConfig.FromArgs(e.Args);
            }
            catch { }

            // If a newer DS4Updater release exists in the configured repo, download and replace self before continuing.
            try
            {
                if (cfg != null)
                {
                    TrySelfUpdateAndRestartIfNeeded(cfg, e.Args);
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "TrySelfUpdateAndRestartIfNeeded");
            }

            // If ds4windows path provided, prefer that for launching DS4Windows
            try { if (!string.IsNullOrEmpty(ds4windowsPath)) launchExePath = Path.Combine(Path.GetFullPath(ds4windowsPath), "DS4Windows.exe"); } catch { }

            // Now create MainWindow and apply parsed options (only after self-update check completed)
            mwd = new MainWindow();
            mwd.downloadLang = parsedDownloadLang;
            mwd.autoLaunchDS4W = parsedAutoLaunch;
            mwd.forceLaunchDS4WUser = parsedForceLaunchUser;
            if (!string.IsNullOrEmpty(preferredLaunchMode)) mwd.PreferredLaunchMode = preferredLaunchMode;

            // Pass repo config and paths into MainWindow so file operations target correct roots
            try { if (cfg != null) mwd.SetRepoConfig(cfg); } catch { }
            try { mwd.SetPaths(ds4windowsPath, ds4updaterPath); } catch { }

            // Ensure MainWindow starts initial checks only after repo/path were injected
            try { mwd.StartInitialChecks(); } catch (Exception ex) { Logger.LogException(ex, "StartInitialChecks"); }

            mwd.Show();
            // If CI mode requested, don't show UI (but we still used MainWindow for logic)
            if (UpdaterResult.IsCiMode)
            {
                // In CI mode we will run headless and close the app when finished.
                // MainWindow will set UpdaterResult.ExitCode/Message during processing.
                // Ensure result is written on exit (handled in App.Exit event below).
            }
        }

        public App()
        {
            // Ensure process is per-monitor DPI aware (v2) so WPF handles scaling across monitors correctly
            try
            {
                DpiHelper.EnablePerMonitorDpiAwareness();
                Logger.Log("Set process DPI awareness to Per-Monitor v2");
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "EnablePerMonitorDpiAwareness");
            }
            //Debug.WriteLine(CultureInfo.CurrentCulture);
            this.Exit += (s, e) =>
            {
                try
                {
                    if (!skipUpdateFilesCleanupOnExit)
                    {
                        if (Directory.Exists(Path.Combine(exedirpath, "Update Files")))
                        {
                            Directory.Delete(Path.Combine(exedirpath, "Update Files"), true);
                        }
                    }
                    else
                    {
                        Logger.Log("Exit: skipping Update Files cleanup due to pending self-update replacer");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogException(ex, "Exit_CleanupUpdateFiles");
                }

                // Write CI result and set process exit code if requested
                Logger.Log("Application exiting: cleaning up and writing result");
                UpdaterResult.WriteAndApply();
            };

            this.Exit += (s, e) =>
            {
                if (openingDS4W)
                {
                    AutoOpenDS4();
                }
            };
        }

        private void AutoOpenDS4()
        {
            string finalLaunchExePath = Path.Combine(exedirpath, "DS4Windows.exe");
            if (File.Exists(launchExePath))
                finalLaunchExePath = launchExePath;
            try
            {
                // Determine the intended working directory for the launched DS4Windows process
                string launchWorkingDir = Path.GetDirectoryName(finalLaunchExePath) ?? exedirpath;

                // If a preferred launch mode was provided by the launching DS4Windows, respect it
                if (!string.IsNullOrEmpty(mwd.PreferredLaunchMode))
                {
                    bool runAsAdmin = string.Equals(mwd.PreferredLaunchMode, "admin", StringComparison.OrdinalIgnoreCase);
                    Logger.Log($"AutoOpenDS4: launching with PreferredLaunchMode mode={mwd.PreferredLaunchMode}, path={finalLaunchExePath}, workingDir={launchWorkingDir}, runAsAdmin={runAsAdmin}");
                    Util.StartProcessDetached(finalLaunchExePath, runAsAdmin, launchWorkingDir);
                    Logger.Log($"Auto-launch DS4Windows (preferred mode) initiated: {finalLaunchExePath} mode={mwd.PreferredLaunchMode}");
                }
                else if (mwd.forceLaunchDS4WUser)
                {
                    // Attempt to launch program as a normal user via shell token
                    Logger.Log($"AutoOpenDS4: launching via Explorer token: path={finalLaunchExePath}, workingDir={launchWorkingDir}");
                    Util.StartProcessInExplorer(finalLaunchExePath, launchWorkingDir);
                    Logger.Log($"Auto-launch DS4Windows via explorer initiated: {finalLaunchExePath}");
                }
                else
                {
                    ProcessStartInfo startInfo = new ProcessStartInfo(finalLaunchExePath);
                    startInfo.WorkingDirectory = launchWorkingDir;
                    using (Process tempProc = Process.Start(startInfo))
                    {
                        Logger.Log($"Auto-launch DS4Windows via Process.Start: FileName={startInfo.FileName}, WorkingDirectory={startInfo.WorkingDirectory}, UseShellExecute={startInfo.UseShellExecute}, Arguments={startInfo.Arguments}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "AutoOpenDS4");
                try { Process.Start(finalLaunchExePath); } catch { }
            }
        }

        private void TrySelfUpdateAndRestartIfNeeded(RepoConfig cfg, string[] originalArgs)
        {
            try
            {
                if (cfg == null || string.IsNullOrEmpty(cfg.DS4UpdaterApiLatestUrl)) return;

                using HttpClient client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "DS4Windows Updater SelfUpdate");
                Logger.Log($"SelfUpdate: checking latest at {cfg.DS4UpdaterApiLatestUrl}");

                var resp = client.GetAsync(cfg.DS4UpdaterApiLatestUrl).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) { Logger.Log($"SelfUpdate: latest query failed status={resp.StatusCode}"); return; }

                var release = resp.Content.ReadFromJsonAsync<GitHubRelease>().GetAwaiter().GetResult();
                if (release == null || string.IsNullOrEmpty(release.tag_name)) { Logger.Log("SelfUpdate: no release info"); return; }

                string latestTag = release.tag_name.TrimStart('v');
                Logger.Log($"SelfUpdate: latestTag={latestTag}");
                // determine current exe/dll version
                string fileName = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name + ".exe";
                string exeFilePath = Path.Combine(AppContext.BaseDirectory, fileName);
                string currentVersion = "0";
                try
                {
                    if (File.Exists(exeFilePath))
                    {
                        currentVersion = FileVersionInfo.GetVersionInfo(exeFilePath).ProductVersion;
                    }
                    else
                    {
                        // fallback: try assembly location (could be a .dll when run with `dotnet`) and assembly version
                        var asmPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        if (!string.IsNullOrEmpty(asmPath) && File.Exists(asmPath))
                        {
                            try { currentVersion = FileVersionInfo.GetVersionInfo(asmPath).ProductVersion; }
                            catch { currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0"; }
                        }
                        else
                        {
                            currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0";
                        }
                    }
                }
                catch { }

                Logger.Log($"SelfUpdate: exeFilePath={exeFilePath} exists={File.Exists(exeFilePath)}");
                try { Logger.Log($"SelfUpdate: detected currentVersion raw='{currentVersion}'"); } catch { }

                // Normalize version strings to first three numeric components for reliable comparison
                static string NormalizeVersion(string v)
                {
                    if (string.IsNullOrEmpty(v)) return "0";
                    // Keep digits and dots and trim leading non-digit
                    var m = System.Text.RegularExpressions.Regex.Match(v, @"(\d+(?:\.\d+){0,3})");
                    if (m.Success) return m.Groups[1].Value;
                    return v;
                }

                string normLatest = NormalizeVersion(latestTag);
                string normCurrent = NormalizeVersion(currentVersion);
                Logger.Log($"SelfUpdate: normalized latest={normLatest} current={normCurrent}");

                bool needUpdate = false;
                bool parsedLatest = Version.TryParse(normLatest, out var vLatest);
                bool parsedCurrent = Version.TryParse(normCurrent, out var vCurrent);
                if (parsedLatest && parsedCurrent)
                {
                    Logger.Log($"SelfUpdate: parsed vLatest={vLatest} vCurrent={vCurrent}");
                    needUpdate = vLatest > vCurrent;
                }
                else
                {
                    Logger.Log($"SelfUpdate: version parse failed parsedLatest={parsedLatest} parsedCurrent={parsedCurrent} normLatest={normLatest} normCurrent={normCurrent}");
                    needUpdate = !string.Equals(normLatest, normCurrent, StringComparison.OrdinalIgnoreCase);
                }

                Logger.Log($"SelfUpdate: needUpdate={needUpdate}");

                // If the executable or dll in the exe dir already matches or exceeds the latest, skip update
                try
                {
                    string dllPath = Path.Combine(AppContext.BaseDirectory, System.Reflection.Assembly.GetExecutingAssembly().GetName().Name + ".dll");
                    string observedVersion = null;
                    if (File.Exists(exeFilePath)) observedVersion = FileVersionInfo.GetVersionInfo(exeFilePath).ProductVersion;
                    else if (File.Exists(dllPath)) observedVersion = FileVersionInfo.GetVersionInfo(dllPath).ProductVersion;
                    if (!string.IsNullOrEmpty(observedVersion))
                    {
                        var normObserved = NormalizeVersion(observedVersion);
                        Logger.Log($"SelfUpdate: observed on-disk version={normObserved}");
                        if (Version.TryParse(normLatest, out var vL) && Version.TryParse(normObserved, out var vObs))
                        {
                            if (vObs >= vL)
                            {
                                Logger.Log($"SelfUpdate: on-disk version ({normObserved}) >= latest ({normLatest}); skipping self-update");
                                return;
                            }
                        }
                        else if (string.Equals(normObserved, normLatest, StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.Log($"SelfUpdate: on-disk version equals latest; skipping self-update");
                            return;
                        }
                    }
                }
                catch (Exception ex) { Logger.LogException(ex, "SelfUpdate_ObservedVersionCheck"); }

                if (!needUpdate)
                {
                    Logger.Log($"SelfUpdate: no update required (current={currentVersion}, latest={latestTag})");
                    return;
                }

                // Find a suitable asset: only .zip assets are supported for releases
                string downloadUrl = null;
                string assetName = null;
                bool assetIsZip = true;
                if (release.assets != null && release.assets.Length > 0)
                {
                    var zipAsset = Array.Find(release.assets, a => !string.IsNullOrEmpty(a.name) && a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                    if (zipAsset != null)
                    {
                        downloadUrl = zipAsset.browser_download_url;
                        assetName = zipAsset.name;
                        assetIsZip = true;
                    }
                }

                if (string.IsNullOrEmpty(downloadUrl))
                {
                    Logger.Log("SelfUpdate: no zip asset found in latest release (only zip supported)");
                    return;
                }

                // Prepare update files folder and paths
                string exedirpath = AppContext.BaseDirectory;
                string updateFilesDir = Path.Combine(exedirpath, "Update Files", "DS4Windows");
                Directory.CreateDirectory(updateFilesDir);
                string newUpdaterPath = Path.Combine(updateFilesDir, "DS4Updater.exe");

                Logger.Log($"SelfUpdate: downloading new updater asset {assetName} from {downloadUrl}");
                using (var httpResp = client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    httpResp.EnsureSuccessStatusCode();
                    if (!assetIsZip)
                    {
                        using (var fs = new FileStream(newUpdaterPath, FileMode.Create, FileAccess.Write))
                        {
                            httpResp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
                        }
                    }
                    else
                    {
                        // Save zip to temp and extract the DS4Updater.exe entry
                        string tmpZip = Path.Combine(Path.GetTempPath(), $"ds4updater_asset_{Guid.NewGuid()}.zip");
                        using (var fs = new FileStream(tmpZip, FileMode.Create, FileAccess.Write))
                        {
                            httpResp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
                        }

                        try
                        {
                            // Extract entire zip to a temporary directory, then move contents to updateFilesDir
                            string tmpExtractDir = Path.Combine(Path.GetTempPath(), $"ds4updater_extract_{Guid.NewGuid()}");
                            Directory.CreateDirectory(tmpExtractDir);
                            System.IO.Compression.ZipFile.ExtractToDirectory(tmpZip, tmpExtractDir);

                            // If archive contains a top-level "DS4Windows" folder, use its contents.
                            string insideDs4Windows = Path.Combine(tmpExtractDir, "DS4Windows");
                            string sourceDir = Directory.Exists(insideDs4Windows) ? insideDs4Windows : tmpExtractDir;

                            // Ensure destination exists
                            Directory.CreateDirectory(updateFilesDir);

                            // Move directories
                            foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.TopDirectoryOnly))
                            {
                                var name = Path.GetFileName(dirPath);
                                var dest = Path.Combine(updateFilesDir, name);
                                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                                Directory.Move(dirPath, dest);
                            }

                            // Move files
                            foreach (var filePath in Directory.GetFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
                            {
                                var name = Path.GetFileName(filePath);
                                var dest = Path.Combine(updateFilesDir, name);
                                try { File.Copy(filePath, dest, true); File.Delete(filePath); } catch (Exception ex) { Logger.LogException(ex, "SelfUpdate_MoveFile"); }
                            }

                            // Clean up temp extract dir
                            try { Directory.Delete(tmpExtractDir, true); } catch { }
                        }
                        finally
                        {
                            try { File.Delete(tmpZip); } catch { }
                        }
                    }
                }

                // Create batch to replace current exe after this process exits, then restart with same args
                string tempBat = Path.Combine(Path.GetTempPath(), $"UpdateReplacer_{Guid.NewGuid()}.bat");
                string currentExe = Path.Combine(exedirpath, fileName);
                string quotedCurrent = "\"" + currentExe + "\"";
                string quotedNew = "\"" + newUpdaterPath + "\"";
                string quotedExeDir = "\"" + exedirpath + "\"";

                // Rebuild args string preserving original quoting
                string argline = string.Empty;
                if (originalArgs != null && originalArgs.Length > 0)
                {
                    argline = string.Join(" ", originalArgs.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
                }

                string newDllPath = Path.Combine(updateFilesDir, System.Reflection.Assembly.GetExecutingAssembly().GetName().Name + ".dll");

                using (var w = new StreamWriter(new FileStream(tempBat, FileMode.Create, FileAccess.Write)))
                {
                    w.WriteLine("@echo off");
                    w.WriteLine("REM Wait a moment for the updater to exit");
                    w.WriteLine("ping -n 3 127.0.0.1 > nul");
                    w.WriteLine(":waitloop");
                    w.WriteLine($"tasklist /fi \"imagename eq {fileName}\" | find /i \"{fileName}\" > nul");
                    w.WriteLine("if %errorlevel%==0 ( timeout /t 1 /nobreak > nul & goto waitloop )");

                    // If the new exe exists in Update Files, swap it in atomically
                    w.WriteLine($"if exist {quotedNew} (");
                    w.WriteLine($"    del /f /q {quotedCurrent} > nul 2>&1");
                    w.WriteLine($"    copy /y {quotedNew} {quotedCurrent} > nul 2>&1");
                    w.WriteLine($"    del /f /q {quotedNew} > nul 2>&1");
                    w.WriteLine($"    start \"\" {quotedCurrent} {argline}");
                    w.WriteLine(") else (");
                    // If the new DLL exists, copy and start with dotnet
                    string quotedNewDll = "\"" + newDllPath + "\"";
                    string destDll = Path.Combine(exedirpath, System.Reflection.Assembly.GetExecutingAssembly().GetName().Name + ".dll");
                    string quotedDestDll = "\"" + destDll + "\"";
                    w.WriteLine($"    if exist {quotedNewDll} (");
                    w.WriteLine($"        copy /y {quotedNewDll} {quotedDestDll} > nul 2>&1");
                    w.WriteLine($"        start \"\" dotnet {quotedDestDll} {argline}");
                    w.WriteLine("    ) else (");
                    // Fallback: try to start existing exe or dll if present
                    string quotedExistingDll = "\"" + Path.Combine(exedirpath, System.Reflection.Assembly.GetExecutingAssembly().GetName().Name + ".dll") + "\"";
                    w.WriteLine($"        if exist {quotedCurrent} ( start \"\" {quotedCurrent} {argline} ) else if exist {quotedExistingDll} ( start \"\" dotnet {quotedExistingDll} {argline} )");
                    w.WriteLine("    )");
                    w.WriteLine(")");

                    w.WriteLine($"del /f /q %~f0 > nul 2>&1");
                }

                Logger.Log($"SelfUpdate: starting replacer batch {tempBat} and exiting");
                var psi = new ProcessStartInfo(tempBat) { UseShellExecute = true, WorkingDirectory = exedirpath };
                // Prevent Exit handler from removing Update Files so batch can access the downloaded assets
                try { skipUpdateFilesCleanupOnExit = true; } catch { }
                Process.Start(psi);

                // Exit current process so the replacer can swap files and restart new updater
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "TrySelfUpdateAndRestartIfNeeded");
            }
        }
    }
}
