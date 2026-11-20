using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PulseIslandInstaller
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerForm());
        }
    }

    internal sealed class InstallerForm : Form
    {
        private readonly TextBox _pathBox;
        private readonly CheckBox _shortcutCheck;
        private readonly CheckBox _launchCheck;
        private readonly ProgressBar _progress;
        private readonly Label _statusLabel;
        private readonly Button _installBtn;
        private readonly Button _browseBtn;

        private const string DotNet8DesktopUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";

        public InstallerForm()
        {
            Text = "Установка PulseIsland";
            ClientSize = new Size(490, 295);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(14, 18, 28);
            ForeColor = Color.FromArgb(241, 245, 249);
            Font = new Font("Segoe UI", 9.5f);

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }

            var title = new Label
            {
                Text = "PulseIsland",
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(24, 18),
                AutoSize = true
            };

            var subtitle = new Label
            {
                Text = "Динамический островок: музыкальный визуализатор и экранный переводчик",
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(26, 52),
                Size = new Size(440, 22)
            };

            var dirLabel = new Label
            {
                Text = "Папка установки:",
                ForeColor = Color.FromArgb(203, 213, 225),
                Location = new Point(26, 88),
                AutoSize = true
            };

            string defaultDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PulseIsland");

            _pathBox = new TextBox
            {
                Text = defaultDir,
                Location = new Point(26, 112),
                Size = new Size(344, 26),
                BackColor = Color.FromArgb(24, 30, 44),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            _browseBtn = new Button
            {
                Text = "Обзор...",
                Location = new Point(378, 110),
                Size = new Size(86, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White
            };
            _browseBtn.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
            _browseBtn.Click += OnBrowseClick;

            _shortcutCheck = new CheckBox
            {
                Text = "Создать ярлык на Рабочем столе",
                Checked = true,
                Location = new Point(26, 150),
                AutoSize = true,
                ForeColor = Color.FromArgb(226, 232, 240)
            };

            _launchCheck = new CheckBox
            {
                Text = "Запустить PulseIsland сразу после установки",
                Checked = true,
                Location = new Point(26, 176),
                AutoSize = true,
                ForeColor = Color.FromArgb(226, 232, 240)
            };

            _progress = new ProgressBar
            {
                Location = new Point(26, 212),
                Size = new Size(438, 14),
                Style = ProgressBarStyle.Continuous
            };

            _statusLabel = new Label
            {
                Text = "Готово к установке.",
                ForeColor = Color.FromArgb(125, 211, 252),
                Location = new Point(26, 234),
                Size = new Size(310, 40)
            };

            _installBtn = new Button
            {
                Text = "Установить",
                Location = new Point(344, 242),
                Size = new Size(120, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(56, 189, 248),
                ForeColor = Color.FromArgb(10, 15, 26),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            _installBtn.FlatAppearance.BorderSize = 0;
            _installBtn.Click += OnInstallClick;

            Controls.AddRange(new Control[]
            {
                title, subtitle, dirLabel, _pathBox, _browseBtn,
                _shortcutCheck, _launchCheck, _progress, _statusLabel, _installBtn
            });
        }

        private void OnBrowseClick(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Выберите папку для установки PulseIsland";
                dlg.SelectedPath = Directory.Exists(_pathBox.Text)
                    ? _pathBox.Text
                    : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _pathBox.Text = Path.Combine(dlg.SelectedPath, "PulseIsland");
                }
            }
        }

        private async void OnInstallClick(object sender, EventArgs e)
        {
            string targetDir = (_pathBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(targetDir))
            {
                MessageBox.Show(this, "Укажите папку установки.", "PulseIsland", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _installBtn.Enabled = false;
            _browseBtn.Enabled = false;
            _pathBox.Enabled = false;

            try
            {
                await Task.Run(() => RunInstallation(targetDir));

                SetStatus("Установка успешно завершена!", 100);
                string exePath = Path.Combine(targetDir, "PulseIsland.exe");

                if (_launchCheck.Checked && File.Exists(exePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        WorkingDirectory = targetDir,
                        UseShellExecute = true
                    });
                }
                else
                {
                    MessageBox.Show(this, "PulseIsland успешно установлен в:\n" + targetDir, "PulseIsland", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                Close();
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка установки.", 0);
                MessageBox.Show(this, "Не удалось завершить установку:\n" + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _installBtn.Enabled = true;
                _browseBtn.Enabled = true;
                _pathBox.Enabled = true;
            }
        }

        private void RunInstallation(string targetDir)
        {
            SetStatus("Закрытие запущенных копий PulseIsland...", 10);
            foreach (var proc in Process.GetProcessesByName("PulseIsland"))
            {
                try { proc.Kill(); proc.WaitForExit(3000); } catch { }
            }

            if (!IsDotNet8DesktopInstalled())
            {
                SetStatus("Загрузка .NET 8 Desktop Runtime...", 25);
                string tempSetup = Path.Combine(Path.GetTempPath(), "PulseIsland-dotnet8-desktop.exe");
                using (var client = new WebClient())
                {
                    client.DownloadFile(DotNet8DesktopUrl, tempSetup);
                }

                SetStatus("Установка .NET 8 Desktop Runtime...", 50);
                var psi = new ProcessStartInfo
                {
                    FileName = tempSetup,
                    Arguments = "/install /quiet /norestart",
                    UseShellExecute = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit();
                }

                try { if (File.Exists(tempSetup)) File.Delete(tempSetup); } catch { }
            }

            SetStatus("Распаковка файлов PulseIsland...", 72);
            Directory.CreateDirectory(targetDir);

            string tempZip = Path.Combine(Path.GetTempPath(), "PulseIsland-payload.zip");
            try
            {
                using (Stream res = Assembly.GetExecutingAssembly().GetManifestResourceStream("PulseIsland.zip"))
                {
                    if (res == null)
                        throw new InvalidOperationException("Встроенный архив приложения не найден.");
                    using (var fs = File.Create(tempZip))
                    {
                        res.CopyTo(fs);
                    }
                }

                using (ZipArchive archive = ZipFile.OpenRead(tempZip))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name))
                            continue;
                        string destPath = Path.Combine(targetDir, entry.FullName);
                        string destFolder = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(destFolder))
                            Directory.CreateDirectory(destFolder);
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                }
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }

            if (_shortcutCheck.Checked)
            {
                SetStatus("Создание ярлыка на Рабочем столе...", 90);
                CreateDesktopShortcut(Path.Combine(targetDir, "PulseIsland.exe"), targetDir);
            }
        }

        private static bool IsDotNet8DesktopInstalled()
        {
            try
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string sharedDir = Path.Combine(programFiles, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
                if (Directory.Exists(sharedDir))
                {
                    foreach (string dir in Directory.GetDirectories(sharedDir, "8.*"))
                    {
                        if (File.Exists(Path.Combine(dir, "PresentationFramework.dll")))
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static void CreateDesktopShortcut(string exePath, string workDir)
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string lnkPath = Path.Combine(desktop, "PulseIsland.lnk");
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = workDir;
                shortcut.IconLocation = exePath + ",0";
                shortcut.Description = "PulseIsland";
                shortcut.Save();
            }
            catch { }
        }

        private void SetStatus(string text, int percent)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => SetStatus(text, percent)));
                return;
            }
            _statusLabel.Text = text;
            _progress.Value = Math.Max(0, Math.Min(100, percent));
        }
    }
}
