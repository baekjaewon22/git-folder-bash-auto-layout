using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace GitFolderBashLauncher
{
    internal sealed class WindowInfo
    {
        public IntPtr Handle;
        public string ProcessName;
        public string Title;
    }

    internal static class NativeMethods
    {
        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        internal static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool SetWindowText(IntPtr hWnd, string text);

        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr hWnd);

        internal const int SW_RESTORE = 9;
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_SHOWWINDOW = 0x0040;
    }

    internal static class WindowCatalog
    {
        internal static List<WindowInfo> GetVisibleWindows()
        {
            var result = new List<WindowInfo>();
            NativeMethods.EnumWindows(delegate(IntPtr hWnd, IntPtr unused)
            {
                if (!NativeMethods.IsWindowVisible(hWnd)) return true;
                var titleBuffer = new StringBuilder(1024);
                NativeMethods.GetWindowText(hWnd, titleBuffer, titleBuffer.Capacity);
                uint pid;
                NativeMethods.GetWindowThreadProcessId(hWnd, out pid);
                try
                {
                    using (var process = Process.GetProcessById((int)pid))
                    {
                        result.Add(new WindowInfo { Handle = hWnd, ProcessName = process.ProcessName, Title = titleBuffer.ToString() });
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return result;
        }
    }

    internal sealed class MainForm : Form
    {
        private static readonly string DefaultGitFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "git");
        private const string DefaultGitBash = @"C:\Program Files\Git\git-bash.exe";
        private const string Mintty = @"C:\Program Files\Git\usr\bin\mintty.exe";
        private static readonly string[] AiTools = { "claude", "codex" };

        private readonly TextBox rootPath = new TextBox();
        private readonly DataGridView folders = new DataGridView();
        private readonly Label status = new Label();
        private readonly Button runButton = new Button();
        private readonly Dictionary<IntPtr, string> lockedTitles = new Dictionary<IntPtr, string>();
        private readonly System.Windows.Forms.Timer titleTimer = new System.Windows.Forms.Timer { Interval = 700 };

        internal MainForm()
        {
            Text = "Git 폴더 Bash 자동배치";
            StartPosition = FormStartPosition.CenterScreen;
            Width = 760;
            Height = 620;
            MinimumSize = new Size(620, 460);
            Font = new Font("Malgun Gothic", 9F);

            var title = new Label {
                Dock = DockStyle.Top,
                Height = 72,
                Padding = new Padding(14, 12, 14, 0),
                Font = new Font("Malgun Gothic", 11F, FontStyle.Bold),
                Text = "실행할 Git 작업 폴더와 AI(claude / codex)를 선택하세요.\r\n선택한 폴더마다 Git Bash에서 AI를 실행하고 화면 전체에 자동 배치합니다."
            };

            var pathPanel = new TableLayoutPanel { Dock = DockStyle.Top, Height = 48, ColumnCount = 4, Padding = new Padding(10, 7, 10, 5) };
            pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            pathPanel.Controls.Add(new Label { Text = "Git 폴더", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
            rootPath.Dock = DockStyle.Fill;
            rootPath.Text = LoadRootPath();
            pathPanel.Controls.Add(rootPath, 1, 0);
            pathPanel.Controls.Add(MakeButton("찾아보기", BrowseFolder), 2, 0);
            pathPanel.Controls.Add(MakeButton("새로고침", delegate { LoadFolders(); }), 3, 0);

            folders.Dock = DockStyle.Fill;
            folders.BorderStyle = BorderStyle.FixedSingle;
            folders.BackgroundColor = SystemColors.Window;
            folders.AllowUserToAddRows = false;
            folders.AllowUserToDeleteRows = false;
            folders.AllowUserToResizeRows = false;
            folders.RowHeadersVisible = false;
            folders.SelectionMode = DataGridViewSelectionMode.CellSelect;
            folders.EditMode = DataGridViewEditMode.EditOnEnter;
            folders.RowTemplate.Height = 28;
            folders.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Run", HeaderText = "실행", Width = 48 });
            folders.Columns.Add(new DataGridViewTextBoxColumn { Name = "Folder", HeaderText = "폴더", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            var aiColumn = new DataGridViewComboBoxColumn { Name = "Ai", HeaderText = "AI", Width = 110, FlatStyle = FlatStyle.Flat };
            aiColumn.Items.AddRange(AiTools);
            folders.Columns.Add(aiColumn);
            folders.CurrentCellDirtyStateChanged += delegate
            {
                if (folders.IsCurrentCellDirty) folders.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            titleTimer.Tick += delegate { EnforceTitles(); };

            var selectPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(9, 5, 9, 3) };
            selectPanel.Controls.Add(MakeButton("전체 선택", delegate { SetAllChecked(true); }, 100));
            selectPanel.Controls.Add(MakeButton("전체 해제", delegate { SetAllChecked(false); }, 100));
            var saveButton = MakeButton("기본저장", SaveAiDefaults, 110);
            saveButton.Dock = DockStyle.Right;
            var saveHolder = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(0, 5, 13, 3) };
            saveHolder.Controls.Add(selectPanel);
            saveHolder.Controls.Add(saveButton);
            selectPanel.Dock = DockStyle.Fill;

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(10, 10, 10, 8), FlowDirection = FlowDirection.LeftToRight };
            runButton.Text = "선택 폴더 실행 + 전체화면 자동배치";
            runButton.Width = 285;
            runButton.Height = 36;
            runButton.Font = new Font("Malgun Gothic", 9F, FontStyle.Bold);
            runButton.Click += RunSelected;
            bottom.Controls.Add(runButton);
            bottom.Controls.Add(MakeButton("바탕화면 아이콘 만들기", CreateDesktopShortcut, 190, 36));

            status.Dock = DockStyle.Bottom;
            status.Height = 38;
            status.Padding = new Padding(13, 8, 13, 0);
            status.Text = "폴더를 읽는 중입니다...";

            var listContainer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 6) };
            listContainer.Controls.Add(folders);

            Controls.Add(listContainer);
            Controls.Add(saveHolder);
            Controls.Add(pathPanel);
            Controls.Add(title);
            Controls.Add(status);
            Controls.Add(bottom);
            Shown += delegate { LoadFolders(); };
        }

        private Button MakeButton(string text, EventHandler handler, int width = 80, int height = 28)
        {
            var button = new Button { Text = text, Width = width, Height = height, Margin = new Padding(4, 0, 4, 0) };
            button.Click += handler;
            return button;
        }

        private string SettingsFolder
        {
            get
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitFolderBashLauncher");
                Directory.CreateDirectory(folder);
                return folder;
            }
        }

        private string SettingsFile { get { return Path.Combine(SettingsFolder, "root.txt"); } }
        private string AiSettingsFile { get { return Path.Combine(SettingsFolder, "ai.txt"); } }

        private Dictionary<string, string> LoadAiChoices()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(AiSettingsFile)) return result;
                foreach (string line in File.ReadAllLines(AiSettingsFile, Encoding.UTF8))
                {
                    int separator = line.LastIndexOf('=');
                    if (separator <= 0) continue;
                    string tool = line.Substring(separator + 1).Trim();
                    if (AiTools.Contains(tool)) result[line.Substring(0, separator)] = tool;
                }
            }
            catch { }
            return result;
        }

        private void SaveAiDefaults(object sender, EventArgs e)
        {
            folders.EndEdit();
            if (SaveAiChoices())
                status.Text = string.Format("AI 선택값 {0}개를 저장했습니다. 다음 실행 때 이 값으로 표시됩니다.", folders.Rows.Count);
            else
                MessageBox.Show("AI 선택값을 저장하지 못했습니다.", "저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private bool SaveAiChoices()
        {
            var choices = LoadAiChoices();
            foreach (DataGridViewRow row in folders.Rows)
            {
                var item = (FolderItem)row.Tag;
                choices[item.FullPath] = (string)row.Cells["Ai"].Value;
            }
            try
            {
                File.WriteAllLines(AiSettingsFile, choices.Select(p => p.Key + "=" + p.Value).ToArray(), new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }

        private string LoadRootPath()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string saved = File.ReadAllText(SettingsFile, Encoding.UTF8).Trim();
                    if (Directory.Exists(saved)) return saved;
                }
            }
            catch { }
            return DefaultGitFolder;
        }

        private void SaveRootPath()
        {
            try { File.WriteAllText(SettingsFile, rootPath.Text.Trim(), new UTF8Encoding(false)); } catch { }
        }

        private void BrowseFolder(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "작업 폴더들이 들어 있는 Git 상위 폴더를 선택하세요.";
                dialog.SelectedPath = Directory.Exists(rootPath.Text.Trim()) ? rootPath.Text.Trim() : DefaultGitFolder;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    rootPath.Text = dialog.SelectedPath;
                    LoadFolders();
                }
            }
        }

        private void LoadFolders()
        {
            string root = rootPath.Text.Trim();
            folders.Rows.Clear();
            if (!Directory.Exists(root))
            {
                status.Text = "폴더를 찾을 수 없습니다: " + root;
                return;
            }

            try
            {
                var directories = Directory.GetDirectories(root)
                    .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                var aiChoices = LoadAiChoices();
                foreach (string path in directories)
                {
                    var item = new FolderItem(path);
                    string ai;
                    if (!aiChoices.TryGetValue(path, out ai)) ai = AiTools[0];
                    int index = folders.Rows.Add(false, item.ToString(), ai);
                    folders.Rows[index].Tag = item;
                }
                SaveRootPath();
                status.Text = string.Format("{0}에서 하위 폴더 {1}개를 찾았습니다.", root, directories.Length);
            }
            catch (Exception ex)
            {
                status.Text = "폴더를 읽지 못했습니다: " + ex.Message;
            }
        }

        private void SetAllChecked(bool value)
        {
            folders.EndEdit();
            foreach (DataGridViewRow row in folders.Rows) row.Cells["Run"].Value = value;
        }

        private void RunSelected(object sender, EventArgs e)
        {
            folders.EndEdit();
            var selected = folders.Rows.Cast<DataGridViewRow>()
                .Where(r => r.Cells["Run"].Value is bool && (bool)r.Cells["Run"].Value)
                .Select(r => new { Folder = (FolderItem)r.Tag, Ai = (string)r.Cells["Ai"].Value })
                .ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("실행할 폴더를 한 개 이상 체크해 주세요.", "선택 필요", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!File.Exists(Mintty))
            {
                MessageBox.Show("Git Bash 터미널(mintty)을 찾지 못했습니다.\r\n" + Mintty, "Git Bash 없음", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            runButton.Enabled = false;
            var launchedWindows = new List<IntPtr>();
            try
            {
                for (int i = 0; i < selected.Count; i++)
                {
                    string title = selected[i].Folder.Name + "  |  " + selected[i].Ai;
                    status.Text = string.Format("Git Bash + {0} 실행 중 ({1}/{2}): {3}", selected[i].Ai, i + 1, selected.Count, selected[i].Folder.Name);
                    Application.DoEvents();
                    IntPtr window = LaunchBashAndFindWindow(selected[i].Folder.FullPath, selected[i].Ai, title);
                    if (window == IntPtr.Zero) continue;
                    launchedWindows.Add(window);
                    lockedTitles[window] = title;
                }
                EnforceTitles();
                titleTimer.Enabled = lockedTitles.Count > 0;
                ArrangeInGrid(launchedWindows);
                status.Text = string.Format("완료: 선택 {0}개 중 {1}개 창을 화면에 자동 배치했습니다.", selected.Count, launchedWindows.Count);
                if (launchedWindows.Count < selected.Count)
                {
                    MessageBox.Show("일부 Git Bash 창을 확인하지 못했습니다. 열린 창은 자동 배치했습니다.", "일부 실행 확인 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                runButton.Enabled = true;
            }
        }

        private IntPtr LaunchBashAndFindWindow(string workingDirectory, string aiTool, string title)
        {
            var before = new HashSet<IntPtr>(WindowCatalog.GetVisibleWindows().Select(w => w.Handle));
            try
            {
                // git-bash.exe 대신 mintty를 직접 실행해야 창 제목과 시작 명령을 지정할 수 있다.
                // AI 종료 후에도 창이 닫히지 않도록 일반 Bash로 이어서 실행한다.
                string command = aiTool + "; exec bash --login -i";
                var startInfo = new ProcessStartInfo(Mintty) {
                    Arguments = string.Join(" ", new[] {
                        "-i", Quote(DefaultGitBash),
                        "-o", "AppID=GitForWindows.Bash",
                        "-t", Quote(title),
                        "/usr/bin/bash", "--login", "-i", "-c", Quote(command)
                    }),
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false
                };
                startInfo.EnvironmentVariables["MSYSTEM"] = "MINGW64";
                startInfo.EnvironmentVariables["CHERE_INVOKING"] = "1";
                startInfo.EnvironmentVariables["CLAUDE_CODE_DISABLE_TERMINAL_TITLE"] = "1";
                Process.Start(startInfo);
            }
            catch { return IntPtr.Zero; }

            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
                Application.DoEvents();
                var added = WindowCatalog.GetVisibleWindows()
                    .FirstOrDefault(w => !before.Contains(w.Handle) &&
                        (w.ProcessName.Equals("mintty", StringComparison.OrdinalIgnoreCase) ||
                         w.ProcessName.Equals("git-bash", StringComparison.OrdinalIgnoreCase)));
                if (added != null) return added.Handle;
            }
            return IntPtr.Zero;
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        // Bash 프롬프트나 AI가 창 제목을 바꿔도 "폴더 | AI" 제목으로 되돌린다 (이 프로그램이 열려 있는 동안).
        private void EnforceTitles()
        {
            foreach (var pair in lockedTitles.ToList())
            {
                if (!NativeMethods.IsWindow(pair.Key))
                {
                    lockedTitles.Remove(pair.Key);
                    continue;
                }
                var current = new StringBuilder(1024);
                NativeMethods.GetWindowText(pair.Key, current, current.Capacity);
                if (current.ToString() != pair.Value) NativeMethods.SetWindowText(pair.Key, pair.Value);
            }
            if (lockedTitles.Count == 0) titleTimer.Enabled = false;
        }

        private void ArrangeInGrid(List<IntPtr> windows)
        {
            int count = windows.Count;
            if (count == 0) return;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            double aspect = (double)area.Width / Math.Max(1, area.Height);
            int columns = Math.Max(1, Math.Min(count, (int)Math.Round(Math.Sqrt(count * aspect))));
            int rows = (int)Math.Ceiling((double)count / columns);

            for (int i = 0; i < count; i++)
            {
                int row = i / columns;
                int column = i % columns;
                int itemsInRow = Math.Min(columns, count - row * columns);
                int cellWidth = area.Width / itemsInRow;
                int left = area.Left + column * cellWidth;
                int right = column == itemsInRow - 1 ? area.Right : left + cellWidth;
                int top = area.Top + row * (area.Height / rows);
                int bottom = row == rows - 1 ? area.Bottom : area.Top + (row + 1) * (area.Height / rows);

                NativeMethods.ShowWindow(windows[i], NativeMethods.SW_RESTORE);
                NativeMethods.SetWindowPos(windows[i], IntPtr.Zero, left, top, right - left, bottom - top,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
            }
        }

        private void CreateDesktopShortcut(object sender, EventArgs e)
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string shortcutPath = Path.Combine(desktop, "Git 폴더 Bash 자동배치.lnk");
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = Application.ExecutablePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
                shortcut.Description = "Git 작업 폴더를 선택하고 Git Bash 창을 자동 배치합니다.";
                shortcut.IconLocation = DefaultGitBash + ",0";
                shortcut.Save();
                MessageBox.Show("바탕화면에 ‘Git 폴더 Bash 자동배치’ 아이콘을 만들었습니다.", "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
                status.Text = "바탕화면 아이콘을 만들었습니다.";
            }
            catch (Exception ex)
            {
                MessageBox.Show("바로가기를 만들지 못했습니다.\r\n" + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private sealed class FolderItem
        {
            internal readonly string FullPath;
            internal string Name { get { return Path.GetFileName(FullPath); } }
            internal FolderItem(string path) { FullPath = path; }
            public override string ToString() { return Name + "    (" + FullPath + ")"; }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try { NativeMethods.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
