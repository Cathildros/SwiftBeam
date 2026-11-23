// Native Per-User Zero-UAC Windows Installer & Uninstaller for SwiftBeam (compiled via .NET Framework csc.exe)
// Strictly C# 5 compatible so zero third-party installer toolkits are required.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
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
            return "1.0.1";
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
    internal static class RoundedGraphics
    {
        internal static GraphicsPath CreateRoundRect(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0)
                return path;

            int d = Math.Min(Math.Max(2, radius * 2), Math.Min(rect.Width, rect.Height));
            Rectangle arc = new Rectangle(rect.Location, new Size(d, d));

            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - d;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - d;
            path.AddArc(arc, 0, 90);
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal static void FillAndStrokeRoundRect(Graphics g, Rectangle rect, int radius, Color fillColor, Color borderColor, float borderWidth)
        {
            if (rect.Width <= 2 || rect.Height <= 2) return;
            using (GraphicsPath path = CreateRoundRect(rect, radius))
            {
                using (SolidBrush fillBrush = new SolidBrush(fillColor))
                {
                    g.FillPath(fillBrush, path);
                }
                if (borderWidth > 0 && borderColor.A > 0)
                {
                    using (Pen pen = new Pen(borderColor, borderWidth))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }
        }
    }

    internal sealed class HeaderBannerPanel : Panel
    {
        private readonly Image _iconImage;
        private readonly string _title;
        private readonly string _versionText;
        private readonly string _subtitle;
        private readonly float _dpi;

        internal HeaderBannerPanel(Image iconImage, string title, string versionText, string subtitle, float dpi)
        {
            _iconImage = iconImage;
            _title = title;
            _versionText = versionText;
            _subtitle = subtitle;
            _dpi = dpi;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        private int S(int px) { return (int)Math.Round(px * _dpi); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            Rectangle full = new Rectangle(0, 0, Width, Height);
            using (LinearGradientBrush grad = new LinearGradientBrush(full, Color.FromArgb(34, 16, 72), Color.FromArgb(59, 21, 120), 15f))
            {
                g.FillRectangle(grad, full);
            }

            using (SolidBrush accent = new SolidBrush(Color.FromArgb(139, 92, 246)))
            {
                g.FillRectangle(accent, 0, Height - S(2), Width, S(2));
            }

            int iconSize = S(42);
            int iconX = S(20);
            int iconY = (Height - iconSize) / 2;
            if (_iconImage != null)
            {
                g.DrawImage(_iconImage, new Rectangle(iconX, iconY, iconSize, iconSize));
            }

            int textX = iconX + iconSize + S(14);
            using (Font titleFont = new Font("Segoe UI", 14.5F, FontStyle.Bold))
            using (Font verFont = new Font("Segoe UI", 8.5F, FontStyle.Bold))
            using (Font subFont = new Font("Segoe UI", 9F, FontStyle.Regular))
            {
                SizeF titleSz = g.MeasureString(_title, titleFont);
                int titleY = S(16);
                using (SolidBrush whiteBrush = new SolidBrush(Color.White))
                {
                    g.DrawString(_title, titleFont, whiteBrush, textX, titleY);
                }

                SizeF verSz = g.MeasureString(_versionText, verFont);
                int badgeW = (int)Math.Ceiling(verSz.Width) + S(14);
                int badgeH = S(22);
                int badgeX = textX + (int)Math.Ceiling(titleSz.Width) + S(8);
                int badgeY = titleY + S(3);
                Rectangle badgeRect = new Rectangle(badgeX, badgeY, badgeW, badgeH);
                RoundedGraphics.FillAndStrokeRoundRect(g, badgeRect, S(7), Color.FromArgb(124, 58, 237), Color.FromArgb(167, 139, 250), 1f);

                using (StringFormat centerFmt = new StringFormat())
                using (SolidBrush badgeTextBrush = new SolidBrush(Color.FromArgb(245, 243, 255)))
                {
                    centerFmt.Alignment = StringAlignment.Center;
                    centerFmt.LineAlignment = StringAlignment.Center;
                    g.DrawString(_versionText, verFont, badgeTextBrush, badgeRect, centerFmt);
                }

                using (SolidBrush subBrush = new SolidBrush(Color.FromArgb(221, 214, 254)))
                {
                    g.DrawString(_subtitle, subFont, subBrush, textX + S(1), S(47));
                }
            }
        }
    }

    internal sealed class RoundedPanel : Panel
    {
        internal int CornerRadius { get; set; }
        internal Color FillColor { get; set; }
        internal Color BorderColor { get; set; }
        internal float BorderThickness { get; set; }

        internal RoundedPanel()
        {
            CornerRadius = 12;
            FillColor = Color.FromArgb(17, 24, 39);
            BorderColor = Color.FromArgb(31, 41, 55);
            BorderThickness = 1f;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            RoundedGraphics.FillAndStrokeRoundRect(e.Graphics, rect, CornerRadius, FillColor, BorderColor, BorderThickness);
        }
    }

    internal sealed class RoundedButton : Control
    {
        private bool _hover;
        private bool _pressed;

        internal int CornerRadius { get; set; }
        internal Color NormalBackColor { get; set; }
        internal Color HoverBackColor { get; set; }
        internal Color PressedBackColor { get; set; }
        internal Color NormalBorderColor { get; set; }
        internal Color DisabledBackColor { get; set; }

        internal RoundedButton()
        {
            CornerRadius = 10;
            NormalBackColor = Color.FromArgb(30, 41, 59);
            HoverBackColor = Color.FromArgb(51, 65, 85);
            PressedBackColor = Color.FromArgb(30, 41, 59);
            NormalBorderColor = Color.FromArgb(51, 65, 85);
            DisabledBackColor = Color.FromArgb(24, 32, 46);
            ForeColor = Color.FromArgb(226, 232, 240);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && Enabled)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
        }

        internal void PerformClick()
        {
            if (Enabled) OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Color bg = !Enabled ? DisabledBackColor : (_pressed ? PressedBackColor : (_hover ? HoverBackColor : NormalBackColor));
            Color fg = Enabled ? ForeColor : Color.FromArgb(100, 116, 139);
            Color bd = !Enabled ? Color.FromArgb(30, 41, 59) : (Focused ? Color.FromArgb(196, 181, 253) : NormalBorderColor);

            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            RoundedGraphics.FillAndStrokeRoundRect(g, rect, CornerRadius, bg, bd, Focused ? 1.5f : 1f);

            using (StringFormat fmt = new StringFormat())
            using (SolidBrush textBrush = new SolidBrush(fg))
            {
                fmt.Alignment = StringAlignment.Center;
                fmt.LineAlignment = StringAlignment.Center;
                g.DrawString(Text, Font, textBrush, rect, fmt);
            }
        }
    }

    internal sealed class RoundedCheckBox : Control
    {
        private bool _checked;
        private bool _hover;
        private readonly float _dpi;

        internal bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked != value)
                {
                    _checked = value;
                    Invalidate();
                }
            }
        }

        internal RoundedCheckBox(float dpi)
        {
            _dpi = dpi;
            ForeColor = Color.FromArgb(226, 232, 240);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
        }

        private int S(int px) { return (int)Math.Round(px * _dpi); }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnClick(EventArgs e)
        {
            if (!Enabled) return;
            Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space && Enabled)
            {
                Checked = !Checked;
                e.Handled = true;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            int boxSz = S(18);
            int boxY = (Height - boxSz) / 2;
            Rectangle boxRect = new Rectangle(S(1), boxY, boxSz, boxSz);

            Color fill = _checked
                ? (_hover ? Color.FromArgb(167, 139, 250) : Color.FromArgb(139, 92, 246))
                : Color.FromArgb(11, 17, 32);
            Color border = _checked
                ? Color.FromArgb(167, 139, 250)
                : (_hover || Focused ? Color.FromArgb(139, 92, 246) : Color.FromArgb(51, 65, 85));

            RoundedGraphics.FillAndStrokeRoundRect(g, boxRect, S(5), fill, border, 1.3f);

            if (_checked)
            {
                using (Pen checkPen = new Pen(Color.White, Math.Max(1.8f, 2.0f * _dpi)))
                {
                    checkPen.StartCap = LineCap.Round;
                    checkPen.EndCap = LineCap.Round;
                    checkPen.LineJoin = LineJoin.Round;
                    Point[] pts = new Point[]
                    {
                        new Point(boxRect.Left + S(4), boxRect.Top + S(9)),
                        new Point(boxRect.Left + S(8), boxRect.Top + S(13)),
                        new Point(boxRect.Left + S(14), boxRect.Top + S(5))
                    };
                    g.DrawLines(checkPen, pts);
                }
            }

            Rectangle textRect = new Rectangle(boxRect.Right + S(10), 0, Width - boxRect.Right - S(12), Height);
            Color textColor = Enabled ? ForeColor : Color.FromArgb(100, 116, 139);
            using (StringFormat fmt = new StringFormat())
            using (SolidBrush tb = new SolidBrush(textColor))
            {
                fmt.Alignment = StringAlignment.Near;
                fmt.LineAlignment = StringAlignment.Center;
                fmt.HotkeyPrefix = HotkeyPrefix.None;
                g.DrawString(Text, Font, tb, textRect, fmt);
            }
        }
    }

    internal sealed class RoundedProgressBar : Control
    {
        private readonly System.Windows.Forms.Timer _animTimer;
        private int _marqueeOffset;
        private bool _marquee;
        private int _value;

        internal bool Marquee
        {
            get { return _marquee; }
            set
            {
                _marquee = value;
                if (_marquee) _animTimer.Start();
                else _animTimer.Stop();
                Invalidate();
            }
        }

        internal int Value
        {
            get { return _value; }
            set
            {
                int clamped = Math.Max(0, Math.Min(100, value));
                if (_value != clamped)
                {
                    _value = clamped;
                    Invalidate();
                }
            }
        }

        internal RoundedProgressBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            _animTimer = new System.Windows.Forms.Timer();
            _animTimer.Interval = 24;
            _animTimer.Tick += delegate(object s, EventArgs e)
            {
                _marqueeOffset = (_marqueeOffset + 8) % Math.Max(1, Width + 140);
                Invalidate();
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _animTimer != null)
            {
                _animTimer.Stop();
                _animTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int radius = Math.Max(2, Height / 2);
            Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
            RoundedGraphics.FillAndStrokeRoundRect(g, track, radius, Color.FromArgb(30, 41, 59), Color.FromArgb(51, 65, 85), 1f);

            if (_marquee)
            {
                int pillW = Math.Max(60, Width / 3);
                int pillX = _marqueeOffset - pillW;
                Rectangle pillRect = new Rectangle(Math.Max(1, pillX), 1, Math.Min(pillW, Width - 2 - Math.Max(1, pillX)), Math.Max(2, Height - 3));
                if (pillRect.Width > 4)
                {
                    RoundedGraphics.FillAndStrokeRoundRect(g, pillRect, radius, Color.FromArgb(139, 92, 246), Color.Empty, 0f);
                }
            }
            else if (_value > 0)
            {
                int fillW = (int)Math.Round((Width - 2) * (_value / 100.0));
                if (fillW > 3)
                {
                    Rectangle fillRect = new Rectangle(1, 1, fillW, Math.Max(2, Height - 3));
                    Color fillColor = _value >= 100 ? Color.FromArgb(16, 185, 129) : Color.FromArgb(139, 92, 246);
                    RoundedGraphics.FillAndStrokeRoundRect(g, fillRect, radius, fillColor, Color.Empty, 0f);
                }
            }
        }
    }

    internal sealed class InstallerForm : Form
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private readonly float _dpi;
        private readonly RoundedPanel _pathCapsule;
        private readonly TextBox _pathBox;
        private readonly RoundedButton _browseBtn;
        private readonly RoundedCheckBox _chkDesktop;
        private readonly RoundedCheckBox _chkStartMenu;
        private readonly RoundedCheckBox _chkLaunch;
        private readonly Label _statusLabel;
        private readonly RoundedProgressBar _progressBar;
        private readonly RoundedButton _installBtn;
        private readonly RoundedButton _cancelBtn;

        private int S(int px) { return (int)Math.Round(px * _dpi); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int dark = 1;
                if (DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref dark, sizeof(int));
                }
            }
            catch { }
        }

        internal InstallerForm(string customDir)
        {
            using (Graphics g = CreateGraphics())
            {
                _dpi = Math.Max(1.0f, g.DpiX / 96.0f);
            }

            string ver = Program.GetAppVersion();
            Text = "SwiftBeam Setup";
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(S(520), S(430));
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(11, 17, 32);
            ForeColor = Color.FromArgb(241, 245, 249);
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);
            DoubleBuffered = true;

            Bitmap iconBmp = null;
            try
            {
                Icon exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (exeIcon != null)
                {
                    Icon = exeIcon;
                    iconBmp = exeIcon.ToBitmap();
                }
            }
            catch { }

            HeaderBannerPanel header = new HeaderBannerPanel(
                iconBmp,
                "Install SwiftBeam",
                "v" + ver,
                "Fast cross-platform parallel file transfer over Wi-Fi, LAN & direct cable",
                _dpi);
            header.SetBounds(0, 0, S(520), S(82));
            Controls.Add(header);

            // Card 1: Destination Folder
            RoundedPanel destCard = new RoundedPanel();
            destCard.CornerRadius = S(12);
            destCard.FillColor = Color.FromArgb(17, 24, 39);
            destCard.BorderColor = Color.FromArgb(31, 41, 55);
            destCard.SetBounds(S(18), S(96), S(484), S(94));
            Controls.Add(destCard);

            Label destLbl = new Label();
            destLbl.Text = "DESTINATION FOLDER  (PER-USER • NO ADMIN REQUIRED)";
            destLbl.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
            destLbl.ForeColor = Color.FromArgb(148, 163, 184);
            destLbl.UseMnemonic = false;
            destLbl.SetBounds(S(14), S(12), S(450), S(18));
            destCard.Controls.Add(destLbl);

            _pathCapsule = new RoundedPanel();
            _pathCapsule.CornerRadius = S(9);
            _pathCapsule.FillColor = Color.FromArgb(11, 17, 32);
            _pathCapsule.BorderColor = Color.FromArgb(30, 41, 59);
            _pathCapsule.SetBounds(S(14), S(38), S(356), S(38));
            destCard.Controls.Add(_pathCapsule);

            _pathBox = new TextBox();
            _pathBox.Text = !string.IsNullOrEmpty(customDir) ? customDir : Program.GetDefaultInstallDir();
            _pathBox.BackColor = Color.FromArgb(11, 17, 32);
            _pathBox.ForeColor = Color.FromArgb(241, 245, 249);
            _pathBox.BorderStyle = BorderStyle.None;
            _pathBox.Font = new Font("Segoe UI", 9.5F);
            int boxH = _pathBox.PreferredHeight;
            int boxY = Math.Max(2, (_pathCapsule.Height - boxH) / 2);
            _pathBox.SetBounds(S(12), boxY, _pathCapsule.Width - S(24), boxH);
            _pathBox.GotFocus += delegate(object s, EventArgs e)
            {
                _pathCapsule.BorderColor = Color.FromArgb(139, 92, 246);
                _pathCapsule.Invalidate();
            };
            _pathBox.LostFocus += delegate(object s, EventArgs e)
            {
                _pathCapsule.BorderColor = Color.FromArgb(30, 41, 59);
                _pathCapsule.Invalidate();
            };
            _pathCapsule.Controls.Add(_pathBox);

            _browseBtn = CreateSubtleButton("Browse...");
            _browseBtn.SetBounds(S(378), S(38), S(92), S(38));
            _browseBtn.CornerRadius = S(9);
            _browseBtn.Click += OnBrowseClick;
            destCard.Controls.Add(_browseBtn);

            // Card 2: Options
            RoundedPanel optCard = new RoundedPanel();
            optCard.CornerRadius = S(12);
            optCard.FillColor = Color.FromArgb(17, 24, 39);
            optCard.BorderColor = Color.FromArgb(31, 41, 55);
            optCard.SetBounds(S(18), S(202), S(484), S(112));
            Controls.Add(optCard);

            _chkDesktop = CreateOptionCheck("Create Desktop shortcut", true, S(14));
            _chkStartMenu = CreateOptionCheck("Create Start Menu shortcut (enables Windows Search)", true, S(45));
            _chkLaunch = CreateOptionCheck("Launch SwiftBeam when finished", true, S(76));
            optCard.Controls.Add(_chkDesktop);
            optCard.Controls.Add(_chkStartMenu);
            optCard.Controls.Add(_chkLaunch);

            // Status & Rounded Progress Bar
            _statusLabel = new Label();
            _statusLabel.Text = "Ready to install with silent GitHub auto-updates.";
            _statusLabel.Font = new Font("Segoe UI", 8.75F, FontStyle.Regular);
            _statusLabel.ForeColor = Color.FromArgb(148, 163, 184);
            _statusLabel.UseMnemonic = false;
            _statusLabel.SetBounds(S(20), S(326), S(480), S(20));
            Controls.Add(_statusLabel);

            _progressBar = new RoundedProgressBar();
            _progressBar.SetBounds(S(18), S(350), S(484), S(10));
            Controls.Add(_progressBar);

            // Bottom Action Bar
            Panel footerLine = new Panel();
            footerLine.SetBounds(0, S(372), S(520), 1);
            footerLine.BackColor = Color.FromArgb(30, 41, 59);
            Controls.Add(footerLine);

            _cancelBtn = CreateSubtleButton("Cancel");
            _cancelBtn.SetBounds(S(284), S(383), S(96), S(36));
            _cancelBtn.Click += delegate(object sender, EventArgs e) { Close(); };
            Controls.Add(_cancelBtn);

            _installBtn = CreatePrimaryButton("Install");
            _installBtn.SetBounds(S(390), S(383), S(112), S(36));
            _installBtn.Click += OnInstallClick;
            Controls.Add(_installBtn);

            Shown += delegate(object sender, EventArgs e)
            {
                _pathBox.SelectionLength = 0;
                _pathBox.SelectionStart = _pathBox.Text.Length;
                ActiveControl = _installBtn;
            };
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Enter && _installBtn.Enabled)
            {
                _installBtn.PerformClick();
                return true;
            }
            if (keyData == Keys.Escape && _cancelBtn.Enabled)
            {
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private RoundedCheckBox CreateOptionCheck(string text, bool checkedByDefault, int y)
        {
            RoundedCheckBox chk = new RoundedCheckBox(_dpi);
            chk.Text = text;
            chk.Checked = checkedByDefault;
            chk.Font = new Font("Segoe UI", 9.5F);
            chk.SetBounds(S(14), y, S(456), S(24));
            return chk;
        }

        private RoundedButton CreatePrimaryButton(string text)
        {
            RoundedButton btn = new RoundedButton();
            btn.Text = text;
            btn.CornerRadius = S(10);
            btn.NormalBackColor = Color.FromArgb(139, 92, 246);
            btn.HoverBackColor = Color.FromArgb(167, 139, 250);
            btn.PressedBackColor = Color.FromArgb(124, 58, 237);
            btn.NormalBorderColor = Color.FromArgb(167, 139, 250);
            btn.ForeColor = Color.White;
            btn.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            return btn;
        }

        private RoundedButton CreateSubtleButton(string text)
        {
            RoundedButton btn = new RoundedButton();
            btn.Text = text;
            btn.CornerRadius = S(10);
            btn.NormalBackColor = Color.FromArgb(30, 41, 59);
            btn.HoverBackColor = Color.FromArgb(51, 65, 85);
            btn.PressedBackColor = Color.FromArgb(30, 41, 59);
            btn.NormalBorderColor = Color.FromArgb(51, 65, 85);
            btn.ForeColor = Color.FromArgb(226, 232, 240);
            btn.Font = new Font("Segoe UI", 9.25F, FontStyle.Regular);
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
                    _pathBox.SelectionLength = 0;
                    _pathBox.SelectionStart = _pathBox.Text.Length;
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
            _progressBar.Marquee = true;

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
                        _progressBar.Marquee = false;
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
                        _progressBar.Marquee = false;
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
