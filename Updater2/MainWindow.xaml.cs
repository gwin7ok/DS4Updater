/*
DS4Updater
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
    #pragma warning restore CS4014
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using DS4Updater.Dtos;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Shell;

namespace DS4Updater
{
    #pragma warning disable CS4014 // suppress warnings for intentional fire-and-forget Tasks
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string CUSTOM_EXE_CONFIG_FILENAME = "custom_exe_name.txt";
        //WebClient wc = new WebClient(), subwc = new WebClient();
        private HttpClient wc = new HttpClient();
        protected string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DS4Windows");
        // Paths: ds4UpdaterDir is where DS4Updater.exe resides; ds4WindowsDir is the DS4Windows program folder root.
        string ds4UpdaterDir = AppContext.BaseDirectory;
        string ds4WindowsDir = AppContext.BaseDirectory;
        string version = "0", newversion = "0";
        bool downloading = false;
        private int round = 1;
        public bool downloadLang = false;
        private bool backup;
        private string outputUpdatePath = "";
        private string updatesFolder = "";
        public bool autoLaunchDS4W = false;
        public bool forceLaunchDS4WUser = false;
        // PreferredLaunchMode can be "admin" or "user" when passed from DS4Windows.
        public string PreferredLaunchMode = null;
        internal string arch = Environment.Is64BitProcess ? "x64" : "x86";
        // Repo configuration (base URL and derived API URL)
        private RepoConfig repoConfig;
        private string custom_exe_name_path;
        public string CustomExeNamePath { get => custom_exe_name_path; }

        [DllImport("Shell32.dll")]
        private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken,
        out IntPtr ppszPath);

        public bool AdminNeeded()
        {
            string tmpName = $"ds4up_test_{Guid.NewGuid()}.tmp";
            try
            {
                // Test write to ds4UpdaterDir
                string upPath = Path.Combine(ds4UpdaterDir, tmpName);
                File.WriteAllText(upPath, "test");
                Thread.Sleep(20);
                File.Delete(upPath);

                // If ds4WindowsDir differs, also test write there
                if (!string.Equals(Path.GetFullPath(ds4UpdaterDir).TrimEnd(Path.DirectorySeparatorChar),
                                   Path.GetFullPath(ds4WindowsDir).TrimEnd(Path.DirectorySeparatorChar),
                                   StringComparison.OrdinalIgnoreCase))
                {
                    string winPath = Path.Combine(ds4WindowsDir, tmpName);
                    File.WriteAllText(winPath, "test");
                    Thread.Sleep(20);
                    File.Delete(winPath);
                }

                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            try
            {
                var hwndSource = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
                if (hwndSource != null)
                {
                    hwndSource.AddHook(WndProc);
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "MainWindow_SourceInitialized");
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_DPICHANGED = 0x02E0;
            if (msg == WM_DPICHANGED)
            {
                try
                {
                    // lParam is a RECT* with suggested new window position in device pixels
                    // We can resize the window appropriately; WPF will usually handle DPI, but ensure layout updates
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        this.InvalidateVisual();
                        this.UpdateLayout();
                    }));
                }
                catch (Exception ex)
                {
                    Logger.LogException(ex, "WndProc_DPICHANGED");
                }
            }
            return IntPtr.Zero;
        }


        public MainWindow()
        {
            InitializeComponent();
            this.SourceInitialized += MainWindow_SourceInitialized;

            Logger.Log($"Startup: args={string.Join(' ', Environment.GetCommandLineArgs())}");

            // Ensure retry button hidden at startup
            try { btnRetry.Visibility = Visibility.Collapsed; } catch { }

            wc.DefaultRequestHeaders.Add("User-Agent", "DS4Windows Updater");

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            // Determine repository configuration (may be overridden by command-line)
            repoConfig = RepoConfig.FromEnvironmentArgs();
            if (File.Exists(Path.Combine(ds4WindowsDir, "DS4Windows.exe")))
                version = FileVersionInfo.GetVersionInfo(Path.Combine(ds4WindowsDir, "DS4Windows.exe")).FileVersion;

            if (AdminNeeded())
            {
                // If running in CI/headless mode, do not prompt for elevation
                string[] cmdArgs = Environment.GetCommandLineArgs();
                bool isCi = cmdArgs.Any(a => string.Equals(a, "--ci", StringComparison.OrdinalIgnoreCase));

                Logger.Log($"AdminNeeded returned true; isCi={isCi}");

                if (isCi)
                {
                    label1.Text = "Please re-run with admin rights";
                    UpdaterResult.ExitCode = 3;
                    UpdaterResult.Message = "admin_required";
                    Logger.Log("Admin required (no elevation): exiting with admin_required");
                    Logger.Log("CI mode: aborting due to missing admin rights");
                }
                else
                {
                    var result = MessageBox.Show("Administrator rights are required to update. Restart with elevated privileges?", "Elevation Required", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    Logger.Log($"Elevation prompt shown, userChoice={result}");
                    if (result == MessageBoxResult.Yes)
                    {
                        try
                        {
                            // Re-launch self with same command-line args, requesting elevation
                            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                            string arguments = string.Empty;
                            if (cmdArgs.Length > 1)
                            {
                                arguments = string.Join(" ", cmdArgs.Skip(1).Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
                            }

                            var psi = new System.Diagnostics.ProcessStartInfo(exePath, arguments)
                            {
                                UseShellExecute = true,
                                Verb = "runas",
                                WorkingDirectory = AppContext.BaseDirectory
                            };

                            // Write a small restart log via Logger so all file logs go through the same function
                            Logger.Log($"REQUEST_ELEVATE\tExe:{exePath}\tArgs:{arguments}\tUser:{Environment.UserName}@{Environment.MachineName}\tPid:{System.Diagnostics.Process.GetCurrentProcess().Id}");

                            System.Diagnostics.Process.Start(psi);
                            Logger.Log("Started elevated process and shutting down current process");
                            Application.Current.Shutdown();
                            return;
                        }
                        catch (System.ComponentModel.Win32Exception ex)
                        {
                            // User refused elevation or an error occurred
                            label1.Text = "Elevation was cancelled or failed";
                            UpdaterResult.ExitCode = 3;
                            UpdaterResult.Message = "admin_required";
                            Logger.Log("Admin required (elevation cancelled or failed)");
                            Logger.LogException(ex, "ElevationFailed");
                            // fall through to allow user to close the UI
                        }
                    }
                    else
                    {
                        label1.Text = "Please re-run with admin rights";
                        UpdaterResult.ExitCode = 3;
                        UpdaterResult.Message = "admin_required";
                        Logger.Log("User declined elevation");
                    }
                }
            }
            else
            {
                custom_exe_name_path = Path.Combine(ds4UpdaterDir, CUSTOM_EXE_CONFIG_FILENAME);

                try
                {
                    string[] files = Directory.GetFiles(ds4WindowsDir);

                    for (int i = 0, arlen = files.Length; i < arlen; i++)
                    {
                        string tempFile = Path.GetFileName(files[i]);
                        if (new Regex(@"DS4Windows_[\w.]+_\w+.zip").IsMatch(tempFile))
                        {
                            File.Delete(files[i]);
                        }
                    }

                    if (Directory.Exists(Path.Combine(ds4WindowsDir, "Update Files")))
                        Directory.Delete(Path.Combine(ds4WindowsDir, "Update Files"), true);

                    if (!Directory.Exists(Path.Combine(ds4WindowsDir, "Updates")))
                        Directory.CreateDirectory(Path.Combine(ds4WindowsDir, "Updates"));

                    updatesFolder = Path.Combine(ds4WindowsDir, "Updates");
                }
                catch (IOException ex) { Logger.LogException(ex, "CannotSaveDownload"); label1.Text = "Cannot save download at this time"; UpdaterResult.ExitCode = 4; UpdaterResult.Message = "cannot_save_download"; return; }

                if (File.Exists(Path.Combine(ds4WindowsDir, "Profiles.xml")))
                    path = ds4WindowsDir;

                if (File.Exists(path + "\\version.txt"))
                {
                    newversion = File.ReadAllText(path + "\\version.txt");
                    newversion = newversion.Trim();
                }
                else if (File.Exists(Path.Combine(ds4WindowsDir, "version.txt")))
                {
                    newversion = File.ReadAllText(Path.Combine(ds4WindowsDir, "version.txt"));
                    newversion = newversion.Trim();
                }
                else
                {
                    StartVersionFileDownload();
                }

                if (!downloading && version.Replace(',', '.').CompareTo(newversion) != 0)
                {
                    Uri url = new Uri($"{repoConfig.DS4WindowsRepoUrl}/releases/download/v{newversion}/DS4Windows_{newversion}_{arch}.zip");
                    sw.Start();
                    outputUpdatePath = Path.Combine(updatesFolder, $"DS4Windows_{newversion}_{arch}.zip");
                    StartAppArchiveDownload(url, outputUpdatePath);
                }
                else if (!downloading)
                {
                    label1.Text = "DS4Windows is up to date";
                    UpdaterResult.ExitCode = 0;
                    UpdaterResult.Message = "up_to_date";
                    Logger.Log("No update required: up_to_date");
                    try
                    {
                        File.Delete(path + "\\version.txt");
                        File.Delete(Path.Combine(ds4WindowsDir, "version.txt"));
                    }
                    catch { }
                    btnOpenDS4.IsEnabled = true;
                }
            }
        }

        // Allow App to inject parsed repo configuration (DS4Updater / DS4Windows URLs)
        public void SetRepoConfig(RepoConfig config)
        {
            if (config != null)
            {
                repoConfig = config;
            }
        }

        // Allow external callers to set DS4Windows program folder and Updater folder.
        public void SetPaths(string ds4WindowsPath, string ds4UpdaterPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(ds4UpdaterPath)) ds4UpdaterDir = Path.GetFullPath(ds4UpdaterPath);
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(ds4WindowsPath)) ds4WindowsDir = Path.GetFullPath(ds4WindowsPath);
            }
            catch { }
        }

        private void StartAppArchiveDownload(Uri url, string outputUpdatePath)
        {
            Logger.Log($"StartAppArchiveDownload called. url={url} output={outputUpdatePath}");
            _ = Task.Run(async () =>
            {
                try
                {
                    bool success = false;
                    using (var downloadStream = new FileStream(outputUpdatePath, FileMode.Create))
                    {
                        using HttpResponseMessage response = await wc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                        long contentLen = response.Content.Headers.ContentLength ?? 0;
                        using (var contentStream = await response.Content.ReadAsStreamAsync())
                        {
                            byte[] buffer = new byte[16384];
                            int bytesRead = 0;
                            long totalBytesRead = 0;
                            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
                            {
                                await downloadStream.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                                totalBytesRead += bytesRead;
                                Application.Current.Dispatcher.BeginInvoke(() =>
                                {
                                    wc_DownloadProgressChanged(new CopyProgress(totalBytesRead, contentLen));
                                });
                            }
                                Logger.Log($"StartAppArchiveDownload called. url={url} output={outputUpdatePath}");
                                // hide retry button when (re)starting a download
                                try { Application.Current.Dispatcher.Invoke(() => { btnRetry.Visibility = Visibility.Collapsed; }); } catch { }
                            if (downloadStream.CanSeek) downloadStream.Position = 0;
                        }

                        success = response.IsSuccessStatusCode;
                        response.EnsureSuccessStatusCode();
                    }

                    if (success)
                    {
                        Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            wc_DownloadFileCompleted();
                        });
                    }
                    else
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            label1.Text = "Could not download update";
                            UpdaterResult.ExitCode = 2;
                            UpdaterResult.Message = "download_failed";
                            Logger.Log($"Download failed (StartAppArchiveDownload) for url={url}");
                            Logger.Log($"Download failed for url={url}");
                            try { btnRetry.Visibility = Visibility.Visible; } catch { }
                        });
                    }

                    //wc.DownloadFileAsync(url, outputUpdatePath);
                }
                catch (HttpRequestException ex)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        label1.Text = "Could not download update";
                        UpdaterResult.ExitCode = 2;
                        UpdaterResult.Message = "download_failed";
                        Logger.Log($"Download failed (StartAppArchiveDownload catch) for url={url}");
                        Logger.LogException(ex, "StartAppArchiveDownload_HttpRequestException");
                        try { btnRetry.Visibility = Visibility.Visible; } catch { }
                    });
                }
                catch (Exception e) { Logger.LogException(e, "StartAppArchiveDownload_General"); Application.Current.Dispatcher.Invoke(() => { label1.Text = e.Message; }); }
                //wc.DownloadFileCompleted += wc_DownloadFileCompleted;
                //wc.DownloadProgressChanged += wc_DownloadProgressChanged;
            });
        }

        private void StartVersionFileDownload()
        {
            if (string.IsNullOrEmpty(repoConfig?.DS4WindowsApiLatestUrl)) return;
            Uri urlv = new Uri(repoConfig.DS4WindowsApiLatestUrl);
            Logger.Log($"StartVersionFileDownload called. url={urlv}");
            //Sorry other devs, gonna have to find your own server
            downloading = true;

            label1.Text = "Getting Update info";
            _ = Task.Run(async () =>
            {
                try
                {
                    bool success = false;
                    using HttpResponseMessage response = await wc.GetAsync(urlv);
                    response.EnsureSuccessStatusCode();
                    success = response.IsSuccessStatusCode;

                    if (success)
                    {
                        var gitHubRelease = await response.Content.ReadFromJsonAsync<GitHubRelease>();
                        string verPath = Path.Combine(ds4WindowsDir, "version.txt");
                        using (StreamWriter sw = new(verPath, false))
                        {
                            sw.Write(gitHubRelease.tag_name.Substring(1));
                        }

                        subwc_DownloadFileCompleted();
                    }
                    else
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            label1.Text = "Could not download update";
                            try { btnRetry.Visibility = Visibility.Visible; } catch { }
                        });
                    }
                    //subwc.DownloadFileAsync(urlv, Path.Combine(ds4WindowsDir, "version.txt"));
                    //subwc.DownloadFileCompleted += subwc_DownloadFileCompleted;
                }
                catch (HttpRequestException e)
                {
                    Logger.LogException(e, "StartVersionFileDownload_HttpRequestException");
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        label1.Text = "Could not download update";
                        try { btnRetry.Visibility = Visibility.Visible; } catch { }
                    });
                }
            });
        }

        private void subwc_DownloadFileCompleted()
        {
            Logger.Log("Version file downloaded; reading version.txt");
            newversion = File.ReadAllText(Path.Combine(ds4WindowsDir, "version.txt"));
            newversion = newversion.Trim();
            File.Delete(Path.Combine(ds4WindowsDir, "version.txt"));
            if (version.Replace(',', '.').CompareTo(newversion) != 0)
            {
                Logger.Log($"New version available: {newversion} (current={version})");
                Uri url = new Uri($"{repoConfig.DS4WindowsRepoUrl}/releases/download/v{newversion}/DS4Windows_{newversion}_{arch}.zip");
                sw.Start();
                outputUpdatePath = Path.Combine(updatesFolder, $"DS4Windows_{newversion}_{arch}.zip");

                //wc.DownloadFileAsync(url, outputUpdatePath);
                //Task.Run(async () =>
                Func<Task> currentTask = async () =>
                {
                    try
                    {
                        bool success = false;
                        using (var downloadStream = new FileStream(outputUpdatePath, FileMode.Create))
                        {
                            using HttpResponseMessage response =
                                await wc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                            long contentLen = response.Content.Headers.ContentLength ?? 0;
                            using (var contentStream = await response.Content.ReadAsStreamAsync())
                            {
                                byte[] buffer = new byte[16384];
                                int bytesRead = 0;
                                long totalBytesRead = 0;
                                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)
                                           .ConfigureAwait(false)) != 0)
                                {
                                    await downloadStream.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                                    totalBytesRead += bytesRead;
                                    Application.Current.Dispatcher.BeginInvoke(() =>
                                    {
                                        wc_DownloadProgressChanged(
                                            new CopyProgress(totalBytesRead, contentLen));
                                    });
                                }

                                if (downloadStream.CanSeek) downloadStream.Position = 0;
                            }

                            success = response.IsSuccessStatusCode;
                            response.EnsureSuccessStatusCode();
                        }

                        if (success)
                        {
                            Application.Current.Dispatcher.BeginInvoke(() => { wc_DownloadFileCompleted(); });
                        }
                    }
                    catch (Exception ec)
                    {
                        Application.Current.Dispatcher.BeginInvoke(() => { label1.Text = ec.Message; });
                    }
                    //});
                };
                _ = currentTask?.Invoke();
            }
            else
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    label1.Text = "DS4Windows is up to date";
                });

                try
                {
                    File.Delete(Path.Combine(path, "version.txt"));
                    File.Delete(Path.Combine(ds4WindowsDir, "version.txt"));
                }
                catch { }

                // Do not auto-launch after check; enable Run button for user to start DS4Windows
                Application.Current.Dispatcher.Invoke(() =>
                {
                    label1.Text = "DS4Windows is up to date. Click 'Run DS4Windows' to start.";
                    btnOpenDS4.IsEnabled = true;
                });
            }
        }

        private bool IsAdministrator()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        Stopwatch sw = new Stopwatch();

        private void wc_DownloadProgressChanged(CopyProgress e)
        {
            label2.Opacity = 1;
            double speed = e.BytesTransferred / sw.Elapsed.TotalSeconds;
            double timeleft = (e.ExpectedBytes - e.BytesTransferred) / speed;
            if (timeleft > 3660)
                label2.Content = (int)timeleft / 3600 + "h left";
            else if (timeleft > 90)
                label2.Content = (int)timeleft / 60 + "m left";
            else
                label2.Content = (int)timeleft + "s left";

            UpdaterBar.Value = e.PercentComplete * 100.0;
            TaskbarItemInfo.ProgressValue = UpdaterBar.Value / 106d;
            string convertedrev, convertedtotal;
            if (e.BytesTransferred > 1024 * 1024 * 5) convertedrev = (int)(e.BytesTransferred / 1024d / 1024d) + "MB";
            else convertedrev = (int)(e.BytesTransferred / 1024d) + "kB";

            if (e.ExpectedBytes > 1024 * 1024 * 5) convertedtotal = (int)(e.ExpectedBytes / 1024d / 1024d) + "MB";
            else convertedtotal = (int)(e.ExpectedBytes / 1024d) + "kB";

            if (round == 1) label1.Text = "Downloading update: " + convertedrev + " / " + convertedtotal;
            else label1.Text = "Downloading Language Pack: " + convertedrev + " / " + convertedtotal;
        }

        //private void wc_DownloadFileCompleted(object sender, AsyncCompletedEventArgs e)
        private async void wc_DownloadFileCompleted()
        {
            Logger.Log($"wc_DownloadFileCompleted invoked; outputUpdatePath={outputUpdatePath}");
            sw.Reset();
            string lang = CultureInfo.CurrentCulture.ToString();

            if (new FileInfo(outputUpdatePath).Length > 0)
            {
                Process[] processes = Process.GetProcessesByName("DS4Windows");
                label1.Text = "Download Complete";
                Logger.Log($"Download completed; found DS4Windows processes count={processes.Length}");
                if (processes.Length > 0)
                {
                    string msgTitle = "DS4Windows is still running";
                    string msgBody = msgTitle + Environment.NewLine + Environment.NewLine + "It will be closed to continue this update.";
                    if (MessageBox.Show(msgBody, msgTitle, MessageBoxButton.OKCancel, MessageBoxImage.Exclamation) == MessageBoxResult.OK)
                    {
                        Logger.Log("User agreed to terminate DS4Windows");
                        label1.Text = "Terminating DS4Windows";
                        foreach (Process p in processes)
                        {
                            if (!p.HasExited)
                            {
                                try
                                {
                                    p.Kill();
                                    Logger.Log($"Killed process pid={p.Id}");
                                }
                                catch
                                {
                                    Logger.Log("Failed to kill a DS4Windows process");
                                    MessageBox.Show("Failed to close DS4Windows. Cannot continue update. Please terminate DS4Windows and run DS4Updater again.");
                                    this.Close();
                                    return;
                                }
                            }
                        }

                        System.Threading.Thread.Sleep(5000);
                    }
                    else
                    {
                        Logger.Log("User declined to terminate DS4Windows; aborting update");
                        this.Close();
                        return;
                    }
                }

                while (processes.Length > 0)
                {
                    label1.Text = "Waiting for DS4Windows to close";
                    processes = Process.GetProcessesByName("DS4Windows");
                    System.Threading.Thread.Sleep(200);
                }

                // Need to check for presense of HidGuardHelper
                processes = Process.GetProcessesByName("HidGuardHelper");
                if (processes.Length > 0)
                {
                    Logger.Log("HidGuardHelper found; waiting for it to close");
                    label1.Text = "Waiting for HidGuardHelper to close";
                    System.Threading.Thread.Sleep(5000);

                    processes = Process.GetProcessesByName("HidGuardHelper");
                    if (processes.Length > 0)
                    {
                        Logger.Log("HidGuardHelper did not close; aborting update");
                        MessageBox.Show("HidGuardHelper will not close. Cannot continue update. Please terminate HidGuardHelper and run DS4Updater again.");
                        this.Close();
                        return;
                    }
                }

                label2.Opacity = 0;
                label1.Text = "Deleting old files";
                Logger.Log("Deleting old files and preparing for install");
                UpdaterBar.Value = 102;
                TaskbarItemInfo.ProgressValue = UpdaterBar.Value / 106d;

                string libsPath = Path.Combine(ds4WindowsDir, "libs");
                string oldLibsPath = Path.Combine(ds4WindowsDir, "oldlibs");

                // Grab relative file paths to DLL files in the current install
                string[] oldDLLFiles = Directory.GetDirectories(ds4WindowsDir, "*.dll", SearchOption.AllDirectories);
                for (int i = oldDLLFiles.Length - 1; i >= 0; i--)
                {
                    oldDLLFiles[i] = oldDLLFiles[i].Replace($"{ds4WindowsDir}", "");
                }

                try
                {
                    Logger.Log("Attempting to move libs and delete old files");
                    // Temporarily move existing libs folder
                    if (Directory.Exists(libsPath))
                    {
                        Directory.Move(libsPath, oldLibsPath);
                    }

                    string[] checkFiles = new string[]
                    {
                        Path.Combine(ds4WindowsDir, "DS4Windows.exe"),
                        Path.Combine(ds4WindowsDir, "DS4Tool.exe"),
                        Path.Combine(ds4WindowsDir, "DS4Control.dll"),
                        Path.Combine(ds4WindowsDir, "DS4Library.dll"),
                        Path.Combine(ds4WindowsDir, "HidLibrary.dll"),
                    };

                    foreach (string checkFile in checkFiles)
                    {
                        if (File.Exists(checkFile))
                        {
                            try
                            {
                                File.Delete(checkFile);
                                Logger.Log($"Deleted file {checkFile}");
                            }
                            catch (Exception ex)
                            {
                                Logger.LogException(ex, "DeleteCheckFile");
                            }
                        }
                    }

                    string updateFilesDir = Path.Combine(ds4WindowsDir, "Update Files");
                    if (Directory.Exists(updateFilesDir))
                    {
                        Logger.Log("Deleting existing Update Files directory");
                        Directory.Delete(updateFilesDir);
                    }

                    string[] updatefiles = Directory.GetFiles(ds4WindowsDir);
                    for (int i = 0, arlen = updatefiles.Length; i < arlen; i++)
                    {
                        if (Path.GetExtension(updatefiles[i]) == ".ds4w" && File.Exists(updatefiles[i]))
                            File.Delete(updatefiles[i]);
                    }
                }
                catch (Exception ex) { Logger.LogException(ex, "DeleteOldFiles"); }

                label1.Text = "Installing new files";
                Logger.Log($"Extracting archive {outputUpdatePath}");
                UpdaterBar.Value = 104;
                TaskbarItemInfo.ProgressValue = UpdaterBar.Value / 106d;

                try
                {
                    Directory.CreateDirectory(Path.Combine(ds4WindowsDir, "Update Files"));
                    ZipFile.ExtractToDirectory(outputUpdatePath, Path.Combine(ds4WindowsDir, "Update Files"));
                    Logger.Log("Extraction complete");
                }
                catch (IOException ex) { Logger.LogException(ex, "ExtractToDirectory"); }

                try
                {
                    File.Delete(Path.Combine(ds4WindowsDir, "version.txt"));
                    File.Delete(path + "\\version.txt");
                }
                catch { }

                // Add small sleep timer here as a pre-caution
                Thread.Sleep(20);

                string[] directories = Directory.GetDirectories(Path.Combine(ds4WindowsDir, "Update Files", "DS4Windows"), "*", SearchOption.AllDirectories);
                for (int i = directories.Length - 1; i >= 0; i--)
                {
                    string relativePath = directories[i].Replace($"{ds4WindowsDir}\\Update Files\\DS4Windows\\", "");
                    string tempDestPath = Path.Combine(ds4WindowsDir, relativePath);
                    if (!Directory.Exists(tempDestPath))
                    {
                        Directory.CreateDirectory(tempDestPath);
                    }
                }

                // Grab relative file paths to DLL files in the newer install
                string[] newDLLFiles = Directory.GetFiles(Path.Combine(ds4WindowsDir, "Update Files", "DS4Windows"), "*.dll", SearchOption.AllDirectories);
                for (int i = newDLLFiles.Length - 1; i >= 0; i--)
                {
                    newDLLFiles[i] = newDLLFiles[i].Replace($"{ds4WindowsDir}\\Update Files\\DS4Windows\\", "");
                }

                string[] files = Directory.GetFiles(Path.Combine(ds4WindowsDir, "Update Files", "DS4Windows"), "*", SearchOption.AllDirectories);
                for (int i = files.Length - 1; i >= 0; i--)
                {
                    if (Path.GetFileNameWithoutExtension(files[i]) != "DS4Updater")
                    {
                        string relativePath = files[i].Replace($"{ds4WindowsDir}\\Update Files\\DS4Windows\\", "");
                        string tempDestPath = Path.Combine(ds4WindowsDir, relativePath);
                        //string tempDestPath = $"{exepath}\\{Path.GetFileName(files[i])}";
                        if (File.Exists(tempDestPath))
                        {
                            File.Delete(tempDestPath);
                        }

                        File.Move(files[i], tempDestPath);
                    }
                }

                // Delete old libs folder
                if (Directory.Exists(oldLibsPath))
                {
                    Directory.Delete(oldLibsPath, true);
                }

                // Remove unused DLLs (in main app folder) from previous install
                string[] excludedDLLs = oldDLLFiles.Except(newDLLFiles).ToArray();
                foreach (string dllFile in excludedDLLs)
                {
                    if (File.Exists(dllFile))
                    {
                        File.Delete(dllFile);
                    }
                }

                string ds4winversion = FileVersionInfo.GetVersionInfo(Path.Combine(ds4WindowsDir, "DS4Windows.exe")).FileVersion;
                if ((File.Exists(Path.Combine(ds4WindowsDir, "DS4Windows.exe")) || File.Exists(Path.Combine(ds4WindowsDir, "DS4Tool.exe"))) &&
                    ds4winversion == newversion.Trim())
                {
                    //File.Delete(exepath + $"\\DS4Windows_{newversion}_{arch}.zip");
                    //File.Delete(exepath + "\\" + lang + ".zip");
                    label1.Text = $"DS4Windows has been updated to v{newversion}";
                    UpdaterResult.ExitCode = 0;
                    UpdaterResult.Message = $"updated:{newversion}";
                    Logger.Log($"Update success: newversion={newversion}");
                }
                else if (File.Exists(Path.Combine(ds4WindowsDir, "DS4Windows.exe")) || File.Exists(Path.Combine(ds4WindowsDir, "DS4Tool.exe")))
                {
                    label1.Text = "Could not replace DS4Windows, please manually unzip";
                    UpdaterResult.ExitCode = 5;
                    UpdaterResult.Message = "replace_failed";
                    Logger.Log("Replace failed: DS4Windows present but version mismatch after install");
                }
                else
                {
                    label1.Text = "Could not unpack zip, please manually unzip";
                    UpdaterResult.ExitCode = 6;
                    UpdaterResult.Message = "unpack_failed";
                    Logger.Log("Unpack failed: DS4Windows executable not found after extraction");
                }

                // Check for custom exe name setting
                string custom_exe_name_path = Path.Combine(ds4UpdaterDir, CUSTOM_EXE_CONFIG_FILENAME);
                bool fakeExeFileExists = File.Exists(custom_exe_name_path);
                if (fakeExeFileExists)
                {
                    string fake_exe_name = File.ReadAllText(custom_exe_name_path).Trim();
                    bool valid = !string.IsNullOrEmpty(fake_exe_name) && !(fake_exe_name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0);
                    // Attempt to copy executable and assembly config file
                    if (valid)
                    {
                        string current_exe_location = Path.Combine(ds4WindowsDir, "DS4Windows.exe");
                        string current_conf_file_path = Path.Combine(ds4WindowsDir, "DS4Windows.runtimeconfig.json");
                        string current_deps_file_path = Path.Combine(ds4WindowsDir, "DS4Windows.deps.json");

                        string fake_exe_file = Path.Combine(ds4WindowsDir, $"{fake_exe_name}.exe");
                        string fake_conf_file = Path.Combine(ds4WindowsDir, $"{fake_exe_name}.runtimeconfig.json");
                        string fake_deps_file = Path.Combine(ds4WindowsDir, $"{fake_exe_name}.deps.json");

                        File.Copy(current_exe_location, fake_exe_file, true); // Copy exe file
                            Logger.Log($"Created fake exe name: {fake_exe_file}");

                        // Copy needed app config and deps files
                        File.Copy(current_conf_file_path, fake_conf_file, true);
                        File.Copy(current_deps_file_path, fake_deps_file, true);
                    }
                }

                UpdaterBar.Value = 106;
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;

                // After update install, do not auto-launch; enable Run button so user can start DS4Windows when ready
                label1.Text = "DS4Windows has been updated. Click 'Run DS4Windows' to start.";
                btnOpenDS4.IsEnabled = true;
            }
            else if (!backup)
            {
                Uri url = new Uri($"{repoConfig.DS4WindowsRepoUrl}/releases/download/v{newversion}/DS4Windows_{newversion}_{arch}.zip");

                sw.Start();
                outputUpdatePath = Path.Combine(updatesFolder, $"DS4Windows_{newversion}_{arch}.zip");
                try
                {
                    bool success = false;
                    using (var downloadStream = new FileStream(outputUpdatePath, FileMode.Create))
                    {
                        using HttpResponseMessage response = await wc.GetAsync(url);
                        response.EnsureSuccessStatusCode();
                        success = response.IsSuccessStatusCode;
                        if (success)
                        {
                            await response.Content.CopyToAsync(downloadStream);
                        }
                    }
                    //wc.DownloadFileAsync(url, outputUpdatePath);
                }
                catch (Exception ex) { Logger.LogException(ex, "BackupDownload"); Application.Current.Dispatcher.Invoke(() => { label1.Text = ex.Message; }); }
                backup = true;
            }
            else
            {
                label1.Text = "Could not download update";
                Logger.Log("Could not download update (final backup step)");
                try { btnRetry.Visibility = Visibility.Visible; } catch { }
                try
                {
                    File.Delete(Path.Combine(ds4WindowsDir, "version.txt"));
                    File.Delete(path + "\\version.txt");
                }
                catch { }
                btnOpenDS4.IsEnabled = true;
            }
        }

        private void BtnChangelog_Click(object sender, RoutedEventArgs e)
        {
            // Open repository CHANGELOG.md if available; otherwise fallback to repo URL
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string changelog = null;
                var dir = new DirectoryInfo(baseDir);
                for (int i = 0; i < 8 && dir != null; i++)
                {
                    var candidate = Path.Combine(dir.FullName, "CHANGELOG.md");
                    if (File.Exists(candidate)) { changelog = candidate; break; }
                    dir = dir.Parent;
                }

                // Always open the repository's latest release page when the user requests changelog
                string fallback = repoConfig?.DS4WindowsRepoUrl ?? "https://github.com/";
                string url = fallback.TrimEnd('/') + "/releases/latest";
                ProcessStartInfo startInfo = new ProcessStartInfo(url) { UseShellExecute = true };
                Logger.Log($"Opening repository latest release page: {url}");

                using (Process tempProc = Process.Start(startInfo)) { }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "BtnChangelog_Click");
            }
        }

        private void BtnRetry_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Log("User initiated retry of download");
                btnRetry.Visibility = Visibility.Collapsed;
                label1.Text = "Retrying download...";
                if (string.IsNullOrEmpty(newversion))
                {
                    Logger.Log("Retry requested but newversion is empty; starting version file download");
                    StartVersionFileDownload();
                    return;
                }

                Uri url = new Uri($"{repoConfig.DS4WindowsRepoUrl}/releases/download/v{newversion}/DS4Windows_{newversion}_{arch}.zip");
                outputUpdatePath = Path.Combine(updatesFolder, $"DS4Windows_{newversion}_{arch}.zip");
                StartAppArchiveDownload(url, outputUpdatePath);
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "BtnRetry_Click");
                try { btnRetry.Visibility = Visibility.Visible; } catch { }
            }
        }

        private void BtnOpenDS4_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string exe = Path.Combine(ds4WindowsDir, "DS4Windows.exe");
                bool runAsAdmin = false;
                if (!string.IsNullOrEmpty(PreferredLaunchMode)) runAsAdmin = string.Equals(PreferredLaunchMode, "admin", StringComparison.OrdinalIgnoreCase);

                if (File.Exists(exe))
                {
                    Util.StartProcessDetached(exe, runAsAdmin, ds4WindowsDir);
                }
                else
                {
                    // Open folder via shell (non-exe)
                    Util.StartProcessDetached(ds4WindowsDir, false, ds4WindowsDir);
                }

                App.openingDS4W = true;
                this.Close();
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "BtnOpenDS4_Click");
                try { Process.Start(Path.Combine(ds4WindowsDir, "DS4Windows.exe")); } catch { }
                App.openingDS4W = true;
                this.Close();
            }
        }

        // Auto-open helper removed; Run action now requires user to click the Run button.
    }
}

