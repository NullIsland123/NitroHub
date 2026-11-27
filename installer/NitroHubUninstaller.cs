using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class NitroHubUninstallerProgram
{
    private const string InnoAppIdKey = "{B9F2E4A1-6C7D-4E8B-9F1A-3D5E7A2C8B4F}_is1";

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    [STAThread]
    private static void Main()
    {
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new UninstallerForm());
    }

    private sealed class UninstallerForm : Form
    {
        private readonly CheckBox removeDataCheck;
        private readonly Button uninstallBtn;
        private readonly Button cancelBtn;
        private readonly Label statusLabel;

        public UninstallerForm()
        {
            bool dark = IsWindowsDark();
            Color bg = dark ? Color.FromArgb(24, 30, 38) : Color.FromArgb(244, 247, 251);
            Color fg = dark ? Color.FromArgb(236, 241, 248) : Color.FromArgb(28, 40, 52);
            Color muted = dark ? Color.FromArgb(164, 178, 194) : Color.FromArgb(90, 106, 122);
            Color btnBg = dark ? Color.FromArgb(44, 54, 66) : Color.White;
            Color accent = Color.FromArgb(210, 48, 66);

            Text = "NitroHub Uninstaller";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            ClientSize = new Size(440, 248);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = bg;
            ForeColor = fg;
            Font = new Font("Segoe UI", 9F);

            HandleCreated += delegate
            {
                try
                {
                    int useDark = dark ? 1 : 0;
                    if (DwmSetWindowAttribute(Handle, 20, ref useDark, sizeof(int)) != 0)
                        DwmSetWindowAttribute(Handle, 19, ref useDark, sizeof(int));
                }
                catch { }
            };

            Label title = new Label
            {
                Text = "Uninstall NitroHub",
                Font = new Font("Segoe UI Semibold", 13F),
                ForeColor = fg,
                Location = new Point(24, 20),
                Size = new Size(392, 28)
            };
            Controls.Add(title);

            Label desc = new Label
            {
                Text = "This will stop any running NitroHub instance, remove the installed application (if present), shortcuts, and the Windows startup scheduled task.",
                ForeColor = muted,
                Location = new Point(24, 54),
                Size = new Size(392, 46)
            };
            Controls.Add(desc);

            removeDataCheck = new CheckBox
            {
                Text = "Also remove settings, caches, and logs (%LocalAppData%\\NitroHub)",
                ForeColor = fg,
                BackColor = Color.Transparent,
                Location = new Point(24, 112),
                Size = new Size(392, 24),
                Checked = false
            };
            Controls.Add(removeDataCheck);

            statusLabel = new Label
            {
                Text = String.Empty,
                ForeColor = muted,
                Location = new Point(24, 148),
                Size = new Size(392, 24)
            };
            Controls.Add(statusLabel);

            uninstallBtn = new Button
            {
                Text = "Uninstall",
                Location = new Point(206, 188),
                Size = new Size(104, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = accent,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9F),
                Cursor = Cursors.Hand
            };
            uninstallBtn.FlatAppearance.BorderSize = 0;
            uninstallBtn.Click += OnUninstallClick;
            Controls.Add(uninstallBtn);

            cancelBtn = new Button
            {
                Text = "Cancel",
                Location = new Point(318, 188),
                Size = new Size(98, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = btnBg,
                ForeColor = fg,
                Cursor = Cursors.Hand
            };
            cancelBtn.FlatAppearance.BorderColor = dark ? Color.FromArgb(80, 94, 110) : Color.FromArgb(196, 208, 220);
            cancelBtn.Click += delegate { Close(); };
            Controls.Add(cancelBtn);
        }

        private void OnUninstallClick(object sender, EventArgs e)
        {
            bool removeData = removeDataCheck.Checked;
            uninstallBtn.Enabled = false;
            cancelBtn.Enabled = false;
            removeDataCheck.Enabled = false;
            statusLabel.Text = "Uninstalling NitroHub...";

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> actions = new List<string>();
                List<string> warnings = new List<string>();
                try
                {
                    PerformUninstall(removeData, actions, warnings);
                }
                catch (Exception ex)
                {
                    warnings.Add(ex.Message);
                }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        StringBuilder sb = new StringBuilder();
                        if (actions.Count > 0)
                        {
                            sb.AppendLine("NitroHub has been uninstalled:");
                            foreach (string a in actions) sb.AppendLine("• " + a);
                        }
                        else
                        {
                            sb.AppendLine("No installed NitroHub files or tasks were found.");
                        }
                        if (warnings.Count > 0)
                        {
                            sb.AppendLine();
                            sb.AppendLine("Notes:");
                            foreach (string w in warnings) sb.AppendLine("• " + w);
                        }
                        MessageBox.Show(this, sb.ToString().Trim(), "NitroHub Uninstaller",
                            MessageBoxButtons.OK, warnings.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                        Close();
                    });
                }
                catch { }
            });
        }

        private static void PerformUninstall(bool removeData, List<string> actions, List<string> warnings)
        {
            // 1. Stop any running NitroHub instances.
            int stopped = 0;
            foreach (Process p in Process.GetProcessesByName("NitroHub"))
            {
                using (p)
                {
                    try
                    {
                        p.CloseMainWindow();
                        if (!p.WaitForExit(1500))
                        {
                            p.Kill();
                            p.WaitForExit(3000);
                        }
                        stopped++;
                    }
                    catch { }
                }
            }
            if (stopped > 0) actions.Add("Stopped running NitroHub process.");

            // 2. Run Inno Setup's own uninstaller if NitroHub was installed via Setup.
            string innoUninstaller = FindInnoUninstaller();
            if (!String.IsNullOrEmpty(innoUninstaller) && File.Exists(innoUninstaller))
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = innoUninstaller,
                        Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (Process p = Process.Start(psi))
                    {
                        if (p != null) p.WaitForExit(30000);
                    }
                    actions.Add("Removed installed NitroHub application and registry entry.");
                }
                catch (Exception ex)
                {
                    warnings.Add("Could not run Setup uninstaller: " + ex.Message);
                }
            }

            // 3. Remove Windows scheduled startup tasks (both current and legacy names).
            bool removedTask = DeleteScheduledTask("NitroHub Startup");
            bool removedLegacy = DeleteScheduledTask("NitroHub Charge Limit");
            if (removedTask || removedLegacy)
                actions.Add("Removed Windows startup scheduled task.");

            // 4. Clean up any leftover Program Files folder or shortcuts.
            string[] leftoverDirs = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NitroHub"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "NitroHub"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NitroHub")
            };
            foreach (string dir in leftoverDirs)
            {
                if (Directory.Exists(dir))
                {
                    try
                    {
                        Directory.Delete(dir, true);
                        actions.Add("Removed folder: " + dir);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("Could not remove " + dir + ": " + ex.Message);
                    }
                }
            }

            string[] leftoverShortcuts = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "NitroHub.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NitroHub.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "NitroHub.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NitroHub.lnk")
            };
            foreach (string lnk in leftoverShortcuts)
            {
                if (File.Exists(lnk))
                {
                    try { File.Delete(lnk); } catch { }
                }
            }

            // 5. Optionally remove local user settings, caches, and logs.
            if (removeData)
            {
                string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NitroHub");
                if (Directory.Exists(dataDir))
                {
                    try
                    {
                        Directory.Delete(dataDir, true);
                        actions.Add("Removed settings, caches, and logs (" + dataDir + ").");
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("Could not remove user data folder: " + ex.Message);
                    }
                }
            }
        }

        private static string FindInnoUninstaller()
        {
            string[] regPaths = new string[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + InnoAppIdKey,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + InnoAppIdKey
            };
            foreach (string subKey in regPaths)
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(subKey))
                    {
                        if (key == null) continue;
                        string raw = Convert.ToString(key.GetValue("UninstallString"));
                        if (String.IsNullOrWhiteSpace(raw)) continue;
                        raw = raw.Trim();
                        if (raw.StartsWith("\""))
                        {
                            int end = raw.IndexOf('"', 1);
                            if (end > 1) return raw.Substring(1, end - 1);
                        }
                        return raw;
                    }
                }
                catch { }
            }
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NitroHub", "unins000.exe");
            return File.Exists(fallback) ? fallback : null;
        }

        private static bool DeleteScheduledTask(string taskName)
        {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                    Arguments = "/Delete /F /TN \"" + taskName + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process p = Process.Start(start))
                {
                    if (p == null) return false;
                    p.WaitForExit(5000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private static bool IsWindowsDark()
        {
            try
            {
                object val = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                return val != null && Convert.ToInt32(val) == 0;
            }
            catch { return true; }
        }
    }
}
