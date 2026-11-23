// Native Per-User Zero-UAC Windows Installer & Uninstaller for SwiftBeam (compiled via .NET Framework csc.exe)
// Strictly C# 5 compatible so zero third-party installer toolkits are required.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SwiftBeamSetup
{
    internal static class Program
    {
        private const string AppName = "SwiftBeam";
        private const string AppPublisher = "Cathildros";
        private const string UninstallRegSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SwiftBeam";

        [STAThread]
        internal static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool silent = false;
            bool forceUninstall = false;
            string customDir = null;
#if !UNINSTALLER_ONLY
            bool launchAfterSilent = false;
#endif

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = (args[i] ?? string.Empty).Trim();
                    if (string.Equals(a, "/S", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "-s", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase))
                    {
                        silent = true;
                    }
#if !UNINSTALLER_ONLY
                    else if (string.Equals(a, "/LAUNCH", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(a, "--launch", StringComparison.OrdinalIgnoreCase))
                    {
                        launchAfterSilent = true;
                    }
#endif
                    else if (string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase))
                    {
                        forceUninstall = true;
                    }
                    else if (a.StartsWith("/DIR=", StringComparison.OrdinalIgnoreCase) && a.Length > 5)
                    {
                        customDir = a.Substring(5).Trim().Trim('"');
                    }
                    else if (a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase) && a.Length > 6)
                    {
                        customDir = a.Substring(6).Trim().Trim('"');
                    }
                }
            }

#if UNINSTALLER_ONLY
            forceUninstall = true;
#endif

            if (forceUninstall)
            {
                RunUninstall(silent);
                return;
            }

#if !UNINSTALLER_ONLY
            if (silent)
            {
                try
                {
                    string targetDir = !string.IsNullOrEmpty(customDir) ? customDir : GetDefaultInstallDir();
                    InstallCore(targetDir, true, true, launchAfterSilent, null);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Silent installation failed: " + ex.Message, "SwiftBeam Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Environment.ExitCode = 1;
                }
                return;
            }

            Application.Run(new InstallerForm(customDir));
#endif
        }

        internal static string GetAppVersion()
        {
            try
            {
                object[] attrs = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                if (attrs.Length > 0)
                {
                    AssemblyInformationalVersionAttribute info = attrs[0] as AssemblyInformationalVersionAttribute;
                    if (info != null && !string.IsNullOrEmpty(info.InformationalVersion))
                    {
                        string v = info.InformationalVersion.Trim();
                        int plusIdx = v.IndexOf('+');
                        if (plusIdx > 0) v = v.Substring(0, plusIdx);
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
                }
            }
            catch { }
            return "1.0.0";
        }

        internal static string GetDefaultInstallDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SwiftBeam");
        }

        internal static string GetDesktopShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SwiftBeam.lnk");
        }

        internal static string GetStartMenuShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SwiftBeam.lnk");
        }

        internal static void StopRunningSwiftBeam()
        {
            int selfPid = Process.GetCurrentProcess().Id;
            string[] names = new string[] { "SwiftBeam", "swiftbeam-cli" };
            for (int n = 0; n < names.Length; n++)
            {
                Process[] procs = Process.GetProcessesByName(names[n]);
                for (int i = 0; i < procs.Length; i++)
                {
                    Process p = procs[i];
                    try
                    {
                        if (p.Id == selfPid) continue;

                        // Wait briefly first in case the app initiated self-update shutdown
                        if (!p.WaitForExit(1500))
                        {
                            try
                            {
                                if (p.CloseMainWindow())
                                    p.WaitForExit(1500);
                            }
                            catch { }
                        }

                        if (!p.HasExited)
                        {
                            p.Kill();
                            p.WaitForExit(3000);
                        }
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            Thread.Sleep(250);
        }

#if !UNINSTALLER_ONLY
        internal static void InstallCore(
            string installDir,
            bool createDesktopShortcut,
            bool createStartMenuShortcut,
            bool launchWhenFinished,
            Action<string> reportProgress)
        {
            Action<string> progress = delegate(string msg)
            {
                if (reportProgress != null) reportProgress(msg);
            };

            progress("Stopping any running SwiftBeam instances...");
            StopRunningSwiftBeam();

            progress("Creating installation folder...");
            Directory.CreateDirectory(installDir);

            progress("Extracting SwiftBeam binaries...");
            long totalBytes = ExtractEmbeddedPayload(installDir);

            string mainExe = Path.Combine(installDir, "SwiftBeam.exe");
            string uninstallExe = Path.Combine(installDir, "Uninstall.exe");

            if (createStartMenuShortcut)
            {
                progress("Adding Start Menu shortcut (enables Windows Search)...");
                string startLink = GetStartMenuShortcutPath();
                Directory.CreateDirectory(Path.GetDirectoryName(startLink));
                CreateShortcut(startLink, mainExe, installDir, "Fast cross-platform parallel file transfer");
            }

            if (createDesktopShortcut)
            {
                progress("Adding Desktop shortcut...");
                string desktopLink = GetDesktopShortcutPath();
                Directory.CreateDirectory(Path.GetDirectoryName(desktopLink));
                CreateShortcut(desktopLink, mainExe, installDir, "Fast cross-platform parallel file transfer");
            }

            progress("Updating user PATH and Apps & Features registry...");
            AddToUserPath(installDir);
            WriteUninstallRegistryEntry(installDir, mainExe, uninstallExe, totalBytes);

            if (launchWhenFinished && File.Exists(mainExe))
            {
                progress("Launching SwiftBeam...");
                LaunchApp(mainExe, installDir);
            }
        }

        private static long ExtractEmbeddedPayload(string installDir)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            Stream resStream = asm.GetManifestResourceStream("SwiftBeamPayload.zip");
            if (resStream == null)
                throw new InvalidOperationException("Embedded SwiftBeamPayload.zip resource was not found inside setup.");

            long totalBytes = 0;
            using (resStream)
            {
                using (ZipArchive archive = new ZipArchive(resStream, ZipArchiveMode.Read))
                {
                    for (int i = 0; i < archive.Entries.Count; i++)
                    {
                        ZipArchiveEntry entry = archive.Entries[i];
                        if (string.IsNullOrEmpty(entry.Name))
                            continue;

                        string destPath = Path.Combine(installDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                        string destDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(destDir))
                            Directory.CreateDirectory(destDir);

                        using (Stream entryStream = entry.Open())
                        {
                            using (FileStream outStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                entryStream.CopyTo(outStream);
                                totalBytes += outStream.Length;
                            }
                        }
                    }
                }
            }
            return totalBytes;
        }

        private static void CreateShortcut(string shortcutPath, string targetPath, string workingDir, string description)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;

            object shell = null;
            object shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                Type scType = shortcut.GetType();
                scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
                scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
                scType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
                scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath + ",0" });
                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }

        private static void WriteUninstallRegistryEntry(string installDir, string exePath, string uninstallExePath, long totalBytes)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallRegSubKey))
            {
                if (key == null) return;
                key.SetValue("DisplayName", AppName, RegistryValueKind.String);
                key.SetValue("DisplayVersion", GetAppVersion(), RegistryValueKind.String);
                key.SetValue("Publisher", AppPublisher, RegistryValueKind.String);
                key.SetValue("DisplayIcon", exePath + ",0", RegistryValueKind.String);
                key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
                key.SetValue("UninstallString", "\"" + uninstallExePath + "\"", RegistryValueKind.String);
                key.SetValue("QuietUninstallString", "\"" + uninstallExePath + "\" /S", RegistryValueKind.String);
                int estimatedKb = (int)Math.Max(1L, totalBytes / 1024L);
                key.SetValue("EstimatedSize", estimatedKb, RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        private static void AddToUserPath(string installDir)
        {
            try
            {
                string cleanDir = installDir.TrimEnd('\\');
                string current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
                string[] parts = current.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (string.Equals(parts[i].Trim().TrimEnd('\\'), cleanDir, StringComparison.OrdinalIgnoreCase))
                        return;
                }
                string updated = string.IsNullOrEmpty(current) ? cleanDir : current.TrimEnd(';') + ";" + cleanDir;
                Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);
            }
            catch { }
        }

        private static void LaunchApp(string exePath, string installDir)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exePath;
                psi.WorkingDirectory = installDir;
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }
#endif

        private static void RemoveFromUserPath(string installDir)
        {
            try
            {
                string cleanDir = installDir.TrimEnd('\\');
                string current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
                string[] parts = current.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                List<string> kept = new List<string>();
                for (int i = 0; i < parts.Length; i++)
                {
                    string item = parts[i].Trim();
                    if (item.Length == 0) continue;
                    if (!string.Equals(item.TrimEnd('\\'), cleanDir, StringComparison.OrdinalIgnoreCase))
                        kept.Add(item);
                }
                Environment.SetEnvironmentVariable("Path", string.Join(";", kept.ToArray()), EnvironmentVariableTarget.User);
            }
            catch { }
        }

        private static void RunUninstall(bool silent)
        {
            if (!silent)
            {
                DialogResult res = MessageBox.Show(
                    "Uninstall SwiftBeam from this computer?",
                    "SwiftBeam Uninstall",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (res != DialogResult.Yes)
                    return;
            }

            try
            {
                StopRunningSwiftBeam();

                string installDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallRegSubKey, false))
                    {
                        if (key != null)
                        {
                            object loc = key.GetValue("InstallLocation");
                            if (loc is string && !string.IsNullOrEmpty((string)loc))
                                installDir = ((string)loc).TrimEnd('\\');
                        }
                    }
                }
                catch { }

                try { File.Delete(GetDesktopShortcutPath()); } catch { }
                try { File.Delete(GetStartMenuShortcutPath()); } catch { }
                RemoveFromUserPath(installDir);
                try { Registry.CurrentUser.DeleteSubKeyTree(UninstallRegSubKey, false); } catch { }

                try { File.Delete(Path.Combine(installDir, "SwiftBeam.exe")); } catch { }
                try { File.Delete(Path.Combine(installDir, "swiftbeam-cli.exe")); } catch { }

                // Schedule self-deletion of Uninstall.exe and installDir after this process exits
                ProcessStartInfo cleanupPsi = new ProcessStartInfo();
                cleanupPsi.FileName = "cmd.exe";
                cleanupPsi.Arguments = string.Format("/c ping 127.0.0.1 -n 3 > nul & rd /s /q \"{0}\"", installDir);
                cleanupPsi.CreateNoWindow = true;
                cleanupPsi.UseShellExecute = false;
                cleanupPsi.WindowStyle = ProcessWindowStyle.Hidden;
                try { Process.Start(cleanupPsi); } catch { }

                if (!silent)
                {
                    MessageBox.Show("SwiftBeam has been uninstalled.", "SwiftBeam Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    MessageBox.Show("Uninstall encountered an error: " + ex.Message, "SwiftBeam Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                Environment.ExitCode = 1;
            }
        }
    }

#if !UNINSTALLER_ONLY
    internal sealed class InstallerForm : Form
    {
        private readonly TextBox _pathBox;
        private readonly Button _browseBtn;
        private readonly CheckBox _chkDesktop;
        private readonly CheckBox _chkStartMenu;
        private readonly CheckBox _chkLaunch;
        private readonly Label _statusLabel;
        private readonly ProgressBar _progressBar;
        private readonly Button _installBtn;
        private readonly Button _cancelBtn;

        internal InstallerForm(string customDir)
        {
            string ver = Program.GetAppVersion();
            Text = "SwiftBeam v" + ver + " Setup";
            ClientSize = new Size(510, 390);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.FromArgb(11, 17, 32);
            ForeColor = Color.FromArgb(241, 245, 249);
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);

            try
            {
                Icon exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (exeIcon != null) Icon = exeIcon;
            }
            catch { }

            Panel header = new Panel();
            header.SetBounds(0, 0, 510, 74);
            header.BackColor = Color.FromArgb(46, 16, 101);
            Controls.Add(header);

            Panel accentLine = new Panel();
            accentLine.SetBounds(0, 72, 510, 2);
            accentLine.BackColor = Color.FromArgb(139, 92, 246);
            header.Controls.Add(accentLine);

            Label titleLbl = new Label();
            titleLbl.Text = "Install SwiftBeam v" + ver;
            titleLbl.Font = new Font("Segoe UI", 15F, FontStyle.Bold);
            titleLbl.ForeColor = Color.White;
            titleLbl.SetBounds(22, 13, 460, 28);
            header.Controls.Add(titleLbl);

            Label subLbl = new Label();
            subLbl.Text = "Fast cross-platform parallel file transfer over Wi-Fi, LAN & direct cable";
            subLbl.Font = new Font("Segoe UI", 9F, FontStyle.Regular);
            subLbl.ForeColor = Color.FromArgb(221, 214, 254);
            subLbl.SetBounds(24, 44, 460, 20);
            header.Controls.Add(subLbl);

            Label destLbl = new Label();
            destLbl.Text = "Destination Folder (Per-User, No Admin Required)";
            destLbl.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            destLbl.ForeColor = Color.FromArgb(226, 232, 240);
            destLbl.SetBounds(24, 92, 460, 20);
            Controls.Add(destLbl);

            _pathBox = new TextBox();
            _pathBox.Text = !string.IsNullOrEmpty(customDir) ? customDir : Program.GetDefaultInstallDir();
            _pathBox.BackColor = Color.FromArgb(15, 23, 42);
            _pathBox.ForeColor = Color.FromArgb(241, 245, 249);
            _pathBox.BorderStyle = BorderStyle.FixedSingle;
            _pathBox.Font = new Font("Segoe UI", 9.5F);
            _pathBox.SetBounds(24, 118, 370, 26);
            Controls.Add(_pathBox);

            _browseBtn = CreateStyledButton("Browse...", Color.FromArgb(30, 41, 59), Color.FromArgb(226, 232, 240));
            _browseBtn.SetBounds(402, 116, 84, 28);
            _browseBtn.Click += OnBrowseClick;
            Controls.Add(_browseBtn);

            _chkDesktop = CreateOptionCheck("Create Desktop shortcut", true, 162);
            _chkStartMenu = CreateOptionCheck("Create Start Menu shortcut (enables Windows Search)", true, 190);
            _chkLaunch = CreateOptionCheck("Launch SwiftBeam when finished", true, 218);
            Controls.Add(_chkDesktop);
            Controls.Add(_chkStartMenu);
            Controls.Add(_chkLaunch);

            _statusLabel = new Label();
            _statusLabel.Text = "Ready to install with instant silent auto-updates.";
            _statusLabel.ForeColor = Color.FromArgb(148, 163, 184);
            _statusLabel.SetBounds(24, 260, 462, 20);
            Controls.Add(_statusLabel);

            _progressBar = new ProgressBar();
            _progressBar.SetBounds(24, 286, 462, 14);
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Minimum = 0;
            _progressBar.Maximum = 100;
            _progressBar.Value = 0;
            Controls.Add(_progressBar);

            Panel footerLine = new Panel();
            footerLine.SetBounds(0, 320, 510, 1);
            footerLine.BackColor = Color.FromArgb(30, 41, 59);
            Controls.Add(footerLine);

            _cancelBtn = CreateStyledButton("Cancel", Color.FromArgb(30, 41, 59), Color.FromArgb(226, 232, 240));
            _cancelBtn.SetBounds(268, 336, 94, 34);
            _cancelBtn.Click += delegate(object sender, EventArgs e) { Close(); };
            Controls.Add(_cancelBtn);

            _installBtn = CreateStyledButton("Install", Color.FromArgb(139, 92, 246), Color.White);
            _installBtn.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            _installBtn.SetBounds(372, 336, 114, 34);
            _installBtn.Click += OnInstallClick;
            Controls.Add(_installBtn);
            AcceptButton = _installBtn;
        }

        private CheckBox CreateOptionCheck(string text, bool checkedByDefault, int y)
        {
            CheckBox chk = new CheckBox();
            chk.Text = text;
            chk.Checked = checkedByDefault;
            chk.ForeColor = Color.FromArgb(226, 232, 240);
            chk.SetBounds(24, y, 462, 22);
            return chk;
        }

        private Button CreateStyledButton(string text, Color backColor, Color foreColor)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.BackColor = backColor;
            btn.ForeColor = foreColor;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 0;
            btn.Cursor = Cursors.Hand;
            return btn;
        }

        private void OnBrowseClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "Choose where to install SwiftBeam:";
                fbd.SelectedPath = Directory.Exists(_pathBox.Text) ? _pathBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (fbd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
                {
                    string selected = fbd.SelectedPath.TrimEnd('\\');
                    string leaf = Path.GetFileName(selected);
                    _pathBox.Text = string.Equals(leaf, "SwiftBeam", StringComparison.OrdinalIgnoreCase)
                        ? selected
                        : Path.Combine(selected, "SwiftBeam");
                }
            }
        }

        private void OnInstallClick(object sender, EventArgs e)
        {
            string targetDir = (_pathBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(targetDir))
            {
                MessageBox.Show(this, "Please choose a valid destination folder.", "SwiftBeam Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _pathBox.Enabled = false;
            _browseBtn.Enabled = false;
            _chkDesktop.Enabled = false;
            _chkStartMenu.Enabled = false;
            _chkLaunch.Enabled = false;
            _installBtn.Enabled = false;
            _cancelBtn.Enabled = false;
            _progressBar.Style = ProgressBarStyle.Marquee;

            bool desktop = _chkDesktop.Checked;
            bool startMenu = _chkStartMenu.Checked;
            bool launch = _chkLaunch.Checked;

            Thread worker = new Thread(delegate()
            {
                try
                {
                    Program.InstallCore(targetDir, desktop, startMenu, launch, delegate(string msg)
                    {
                        BeginInvoke((MethodInvoker)delegate() { _statusLabel.Text = msg; });
                    });

                    BeginInvoke((MethodInvoker)delegate()
                    {
                        _progressBar.Style = ProgressBarStyle.Continuous;
                        _progressBar.Value = 100;
                        _statusLabel.Text = "SwiftBeam installed successfully!";
                        _statusLabel.ForeColor = Color.FromArgb(52, 211, 153);
                        _installBtn.Text = "Finish";
                        _installBtn.Enabled = true;
                        _installBtn.Click -= OnInstallClick;
                        _installBtn.Click += delegate(object s2, EventArgs e2) { Close(); };
                        if (launch)
                        {
                            Close();
                        }
                    });
                }
                catch (Exception ex)
                {
                    BeginInvoke((MethodInvoker)delegate()
                    {
                        _progressBar.Style = ProgressBarStyle.Continuous;
                        _progressBar.Value = 0;
                        _statusLabel.Text = "Installation failed: " + ex.Message;
                        _statusLabel.ForeColor = Color.FromArgb(248, 113, 113);
                        _pathBox.Enabled = true;
                        _browseBtn.Enabled = true;
                        _chkDesktop.Enabled = true;
                        _chkStartMenu.Enabled = true;
                        _chkLaunch.Enabled = true;
                        _installBtn.Enabled = true;
                        _cancelBtn.Enabled = true;
                        MessageBox.Show(this, "Installation failed:\n\n" + ex.Message, "SwiftBeam Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }
    }
#endif
}
