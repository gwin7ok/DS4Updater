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

using System.Diagnostics;
using System.IO;
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.ComponentModel;

namespace DS4Updater
{
    class Util
    {
        public static void StartProcessInExplorer(string path, string workingDirectory = null)
        {
            // Deprecated wrapper: try to start via shell token to ensure non-elevated interactive user context
            StartProcessAsShellUser(path, null, workingDirectory);
        }

        public static void StartProcessDetached(string path, bool runAsAdmin = false, string workingDirectory = null)
        {
            try
            {
                if (runAsAdmin)
                {
                    var psi = new ProcessStartInfo(path)
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(path)
                    };
                        Logger.Log($"StartProcessDetached: starting elevated process. FileName={psi.FileName}, WorkingDirectory={psi.WorkingDirectory}, UseShellExecute={psi.UseShellExecute}, Verb={psi.Verb}, Arguments={psi.Arguments}");
                    using (Process p = Process.Start(psi)) { }
                    return;
                }

                // Non-elevated: try to start as the interactive shell user and explicitly set working directory
                if (!StartProcessAsShellUser(path, null, workingDirectory ?? Path.GetDirectoryName(path)))
                {
                    // Fallback: try explorer, then Process.Start
                    try
                    {
                        var psi2 = new ProcessStartInfo("explorer.exe") { Arguments = path, UseShellExecute = true };
                            Logger.Log($"StartProcessDetached: fallback to explorer. FileName={psi2.FileName}, Arguments={psi2.Arguments}, UseShellExecute={psi2.UseShellExecute}");
                        using (Process p2 = Process.Start(psi2)) { }
                    }
                    catch
                    {
                        try { Process.Start(path); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "StartProcessDetached");
                try { Process.Start(path); } catch { }
            }
        }

        // Start a process using the interactive shell (explorer.exe) token so it runs as the logged-in user.
        // Returns true on success.
        public static bool StartProcessAsShellUser(string applicationPath, string arguments = null, string workingDirectory = null)
        {
            IntPtr hShellProcess = IntPtr.Zero;
            IntPtr hShellToken = IntPtr.Zero;
            IntPtr hDupedToken = IntPtr.Zero;
            IntPtr envBlock = IntPtr.Zero;
            try
            {
                var procs = Process.GetProcessesByName("explorer");
                Process shell = null;
                foreach (var p in procs)
                {
                    if (p.MainWindowHandle != IntPtr.Zero) { shell = p; break; }
                }
                if (shell == null && procs.Length > 0) shell = procs[0];
                if (shell == null) return false;

                const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
                hShellProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, shell.Id);
                if (hShellProcess == IntPtr.Zero) return false;

                const uint TOKEN_DUPLICATE = 0x0002;
                const uint TOKEN_QUERY = 0x0008;
                const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
                uint desiredAccess = TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY;

                if (!OpenProcessToken(hShellProcess, desiredAccess, out hShellToken)) return false;

                const int SecurityImpersonation = 2;
                const int TokenPrimary = 1;
                const uint MAXIMUM_ALLOWED = 0x02000000;
                if (!DuplicateTokenEx(hShellToken, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out hDupedToken)) return false;

                // Create environment block for the new user
                if (!CreateEnvironmentBlock(out envBlock, hDupedToken, false)) envBlock = IntPtr.Zero;

                STARTUPINFO si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(si);
                si.lpDesktop = "winsta0\\default";

                PROCESS_INFORMATION pi = new PROCESS_INFORMATION();

                StringBuilder cmd = new StringBuilder();
                cmd.Append('"').Append(applicationPath).Append('"');
                if (!string.IsNullOrEmpty(arguments)) { cmd.Append(' ').Append(arguments); }
                    Logger.Log($"StartProcessAsShellUser: CreateProcessAsUser cmd={cmd.ToString()}, workingDirectory={workingDirectory}");

                uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
                uint CREATE_NEW_CONSOLE = 0x00000010;
                uint creationFlags = CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE;

                bool ok = CreateProcessAsUser(hDupedToken, null, cmd.ToString(), IntPtr.Zero, IntPtr.Zero, false, creationFlags, envBlock, workingDirectory, ref si, out pi);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    Logger.Log($"StartProcessAsShellUser: CreateProcessAsUser failed GLE={err}");
                    return false;
                }

                // Close handles from PROCESS_INFORMATION
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "StartProcessAsShellUser");
                return false;
            }
            finally
            {
                if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
                if (hDupedToken != IntPtr.Zero) CloseHandle(hDupedToken);
                if (hShellToken != IntPtr.Zero) CloseHandle(hShellToken);
                if (hShellProcess != IntPtr.Zero) CloseHandle(hShellProcess);
            }
        }

        #region PInvoke
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out IntPtr phNewToken);

        [DllImport("userenv.dll", SetLastError = true)]
        static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

        [DllImport("userenv.dll", SetLastError = true)]
        static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessAsUser(IntPtr hToken, string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);
        #endregion
    }
}
