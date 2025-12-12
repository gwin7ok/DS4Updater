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

namespace DS4Updater
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private string exedirpath = AppContext.BaseDirectory;
        public static bool openingDS4W;
        private string launchExeName;
        private string launchExePath;
        private MainWindow mwd;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            Logger.Log($"Application_Startup: args={string.Join(' ', e.Args)}");
            try { Logger.Log($"Application_Startup: CurrentDirectory={Environment.CurrentDirectory}"); } catch { }

            mwd = new MainWindow();
            // allow overriding paths via args
            string ds4windowsPath = null;
            string ds4updaterPath = null;
            launchExePath = Path.Combine(exedirpath, "DS4Windows.exe");
            for (int i=0, arlen = e.Args.Length; i < arlen; i++)
            {
                string temp = e.Args[i];
                if (temp.Contains("-skipLang"))
                {
                    mwd.downloadLang = false;
                }
                else if (temp.Equals("-autolaunch"))
                {
                    mwd.autoLaunchDS4W = true;
                }
                else if (temp.Equals("-user"))
                {
                    mwd.forceLaunchDS4WUser = true;
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
                            // Pass through to MainWindow for behavior when auto-launching DS4Windows
                            mwd.PreferredLaunchMode = mode;
                        }
                    }
                    catch { }
                }
            }

            // Inject parsed repo configuration (ds4updater and ds4windows repos)
            try
            {
                var cfg = RepoConfig.FromArgs(e.Args);
                mwd.SetRepoConfig(cfg);
            }
            catch { }

            // If updater path provided, make it the exe dir used for self-update tasks
            try { if (!string.IsNullOrEmpty(ds4updaterPath)) exedirpath = Path.GetFullPath(ds4updaterPath); } catch { }
            // If ds4windows path provided, prefer that for launching DS4Windows
            try { if (!string.IsNullOrEmpty(ds4windowsPath)) launchExePath = Path.Combine(Path.GetFullPath(ds4windowsPath), "DS4Windows.exe"); } catch { }

            // Pass paths into MainWindow so file operations target correct roots
            try { mwd.SetPaths(ds4windowsPath, ds4updaterPath); } catch { }

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
                string currentUpdaterPath = Path.Combine(exedirpath, "Update Files", "DS4Windows", "DS4Updater.exe");
                string tempNewUpdaterPath = Path.Combine(exedirpath, "DS4Updater NEW.exe");

                string fileName = $"{Assembly.GetExecutingAssembly().GetName().Name}.exe";
                string filePath = Path.Combine(AppContext.BaseDirectory, fileName);
                FileVersionInfo fileVersion = FileVersionInfo.GetVersionInfo(filePath);
                string version = fileVersion.ProductVersion;
                if (File.Exists(exedirpath + "\\Update Files\\DS4Windows\\DS4Updater.exe")
                    && FileVersionInfo.GetVersionInfo(exedirpath + "\\Update Files\\DS4Windows\\DS4Updater.exe").FileVersion.CompareTo(version) != 0)
                {
                    File.Move(currentUpdaterPath, tempNewUpdaterPath);
                    //Directory.Delete(exepath + "\\Update Files", true);

                    //string tempFilePath = Path.GetTempFileName();
                    string tempFilePath = Path.Combine(Path.GetTempPath(), "UpdateReplacer.bat");
                    using (StreamWriter w = new StreamWriter(new FileStream(tempFilePath,
                        FileMode.Create, FileAccess.Write)))
                    {
                        w.WriteLine("@echo off"); // Turn off echo
                        w.WriteLine("@echo Attempting to replace updater, please wait...");
                        w.WriteLine("@ping -n 4 127.0.0.1 > nul"); //Its silly but its the most compatible way to call for a timeout in a batch file, used to give the main updater time to cleanup and exit.
                        w.WriteLine("@del \"" + exedirpath + "\\DS4Updater.exe" + "\"");
                        w.WriteLine("@ren \"" + exedirpath + "\\DS4Updater NEW.exe" + "\" \"DS4Updater.exe\"");
                        w.Close();
                    }

                    Process.Start(tempFilePath);
                }
                else if (File.Exists(tempNewUpdaterPath))
                {
                    File.Delete(tempNewUpdaterPath);
                }

                if (Directory.Exists(exedirpath + "\\Update Files"))
                {
                    Directory.Delete(exedirpath + "\\Update Files", true);
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
    }
}
