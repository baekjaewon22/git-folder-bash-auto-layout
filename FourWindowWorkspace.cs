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

        private static readonly Color PageColor = Color.FromArgb(243, 244, 246);
        private static readonly Color BorderColor = Color.FromArgb(218, 222, 228);
        private static readonly Color MutedColor = Color.FromArgb(100, 108, 120);
        private static readonly Color AccentColor = Color.FromArgb(37, 99, 235);
        private static readonly Color CheckedRowColor = Color.FromArgb(232, 240, 254);

        private readonly TextBox rootPath = new TextBox();
        private readonly FlowLayoutPanel list = new FlowLayoutPanel();
        private readonly List<FolderRow> rows = new List<FolderRow>();
        private readonly Label status = new Label();
        private readonly Label selectedCount = new Label();
        private readonly Button runButton;
        private readonly Dictionary<IntPtr, string> lockedTitles = new Dictionary<IntPtr, string>();
        private readonly System.Windows.Forms.Timer titleTimer = new System.Windows.Forms.Timer { Interval = 700 };
        private readonly float scale = 1f;
        private string loadedRoot;

        internal MainForm()
        {
            // 프로세스가 DPI 인식 상태라 글꼴(pt)만 배율대로 커진다. 픽셀 값은 S()로 직접 배율을 곱한다.
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;

            Text = "Git 폴더 Bash 자동배치";
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Malgun Gothic", 10.5F);
            BackColor = PageColor;
            Rectangle screen = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(Math.Min(S(920), screen.Width * 9 / 10), Math.Min(S(760), screen.Height * 9 / 10));
            MinimumSize = new Size(Math.Min(S(700), screen.Width), Math.Min(S(520), screen.Height));

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(S(20), S(16), S(20), S(16)) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // 제목
            var header = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, S(14)) };
            header.Controls.Add(new Label { AutoSize = true, Margin = new Padding(0), Text = "Git 폴더 Bash 자동배치", Font = new Font("Malgun Gothic", 16F, FontStyle.Bold) });
            header.Controls.Add(new Label {
                AutoSize = true, Margin = new Padding(0, S(4), 0, 0), ForeColor = MutedColor,
                Text = "실행할 폴더를 체크하고 폴더마다 AI(claude / codex)를 고르세요.\r\n선택한 폴더마다 Git Bash에서 AI를 실행하고 화면에 자동 배치합니다."
            });
            layout.Controls.Add(header, 0, 0);

            // Git 상위 폴더 경로
            var pathRow = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 0, 0, S(10)) };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.Controls.Add(new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, S(10), 0), Text = "Git 폴더", Font = new Font(Font, FontStyle.Bold) }, 0, 0);
            rootPath.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            rootPath.Margin = new Padding(0, 0, S(8), 0);
            rootPath.Text = LoadRootPath();
            rootPath.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                LoadFolders();
            };
            pathRow.Controls.Add(rootPath, 1, 0);
            pathRow.Controls.Add(MakeButton("찾아보기", BrowseFolder), 2, 0);
            pathRow.Controls.Add(MakeButton("새로고침", delegate { LoadFolders(); }), 3, 0);
            layout.Controls.Add(pathRow, 0, 1);

            // 목록 위 도구 모음
            var toolbar = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 0, 0, S(8)) };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.Controls.Add(MakeButton("전체 선택", delegate { SetAllChecked(true); }), 0, 0);
            toolbar.Controls.Add(MakeButton("전체 해제", delegate { SetAllChecked(false); }), 1, 0);
            selectedCount.AutoSize = true;
            selectedCount.Anchor = AnchorStyles.Left;
            selectedCount.ForeColor = MutedColor;
            selectedCount.Margin = new Padding(S(8), 0, 0, 0);
            toolbar.Controls.Add(selectedCount, 2, 0);
            toolbar.Controls.Add(MakeButton("기본저장", SaveDefaults), 3, 0);
            layout.Controls.Add(toolbar, 0, 2);

            // 폴더 목록 (1px 테두리 안에 스크롤 목록)
            var listBorder = new Panel { Dock = DockStyle.Fill, BackColor = BorderColor, Padding = new Padding(1), Margin = new Padding(0, 0, 0, S(12)) };
            list.Dock = DockStyle.Fill;
            list.FlowDirection = FlowDirection.TopDown;
            list.WrapContents = false;
            list.AutoScroll = true;
            list.BackColor = Color.White;
            list.ClientSizeChanged += delegate { FitRowWidths(); };
            listBorder.Controls.Add(list);
            layout.Controls.Add(listBorder, 0, 3);

            // 상태 표시 + 실행 버튼
            var footer = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status.AutoSize = false;
            status.AutoEllipsis = true;
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.ForeColor = MutedColor;
            status.Margin = new Padding(0, 0, S(12), 0);
            status.Text = "폴더를 읽는 중입니다...";
            footer.Controls.Add(status, 0, 0);
            footer.Controls.Add(MakeButton("바탕화면 아이콘 만들기", CreateDesktopShortcut), 1, 0);
            runButton = MakeButton("선택 폴더 실행 + 전체화면 자동배치", RunSelected, true);
            footer.Controls.Add(runButton, 2, 0);
            layout.Controls.Add(footer, 0, 4);

            Controls.Add(layout);
            AcceptButton = runButton;
            titleTimer.Tick += delegate { EnforceTitles(); };
            Shown += delegate { LoadFolders(); };
        }

        private int S(int value)
        {
            return (int)Math.Round(value * scale);
        }

        private Button MakeButton(string text, EventHandler handler, bool primary = false)
        {
            var button = new Button {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(S(12), S(5), S(12), S(5)),
                Margin = new Padding(S(6), 0, 0, 0),
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                BackColor = primary ? AccentColor : Color.White,
                ForeColor = primary ? Color.White : Color.FromArgb(31, 41, 55),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.None
            };
            if (primary) button.Font = new Font(Font, FontStyle.Bold);
            button.FlatAppearance.BorderColor = primary ? AccentColor : BorderColor;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(29, 78, 216) : Color.FromArgb(243, 246, 250);
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
        private string CheckedSettingsFile { get { return Path.Combine(SettingsFolder, "checked.txt"); } }

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

        // 기본저장: 폴더별 AI 선택과 체크 상태를 함께 저장해 다음 실행 때 그대로 불러온다.
        private void SaveDefaults(object sender, EventArgs e)
        {
            if (SaveAiChoices() && SaveCheckedFolders())
                status.Text = string.Format("기본값을 저장했습니다: 체크 {0}개, AI 선택 {1}개. 다음 실행 때 이 상태로 표시됩니다.",
                    rows.Count(r => r.Check.Checked), rows.Count);
            else
                MessageBox.Show("기본값을 저장하지 못했습니다.", "저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private HashSet<string> LoadCheckedFolders()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(CheckedSettingsFile)) return result;
                foreach (string line in File.ReadAllLines(CheckedSettingsFile, Encoding.UTF8))
                    if (line.Trim().Length > 0) result.Add(line.Trim());
            }
            catch { }
            return result;
        }

        private bool SaveCheckedFolders()
        {
            // 다른 Git 상위 폴더에서 저장한 체크는 유지하고, 지금 목록에 있는 폴더만 갱신한다.
            var saved = LoadCheckedFolders();
            foreach (FolderRow row in rows)
            {
                if (row.Check.Checked) saved.Add(row.Item.FullPath);
                else saved.Remove(row.Item.FullPath);
            }
            try
            {
                File.WriteAllLines(CheckedSettingsFile, saved.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(), new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }

        private bool SaveAiChoices()
        {
            var choices = LoadAiChoices();
            foreach (FolderRow row in rows) choices[row.Item.FullPath] = row.SelectedAi;
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
            // 같은 폴더를 새로고침하면 지금 체크 상태를 유지하고, 처음 열거나 상위 폴더가 바뀌면 저장된 체크를 불러온다.
            var wasChecked = string.Equals(loadedRoot, root, StringComparison.OrdinalIgnoreCase)
                ? new HashSet<string>(rows.Where(r => r.Check.Checked).Select(r => r.Item.FullPath), StringComparer.OrdinalIgnoreCase)
                : LoadCheckedFolders();
            loadedRoot = root;
            ClearRows();
            if (!Directory.Exists(root))
            {
                status.Text = "폴더를 찾을 수 없습니다: " + root;
                UpdateSelectedCount();
                return;
            }

            try
            {
                var directories = Directory.GetDirectories(root)
                    .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                var aiChoices = LoadAiChoices();
                list.SuspendLayout();
                foreach (string path in directories)
                {
                    string ai;
                    if (!aiChoices.TryGetValue(path, out ai)) ai = AiTools[0];
                    var row = CreateRow(new FolderItem(path), ai, wasChecked.Contains(path));
                    rows.Add(row);
                    list.Controls.Add(row.Panel);
                }
                list.ResumeLayout();
                FitRowWidths();
                SaveRootPath();
                status.Text = string.Format("{0}에서 하위 폴더 {1}개를 찾았습니다.", root, directories.Length);
            }
            catch (Exception ex)
            {
                list.ResumeLayout();
                status.Text = "폴더를 읽지 못했습니다: " + ex.Message;
            }
            UpdateSelectedCount();
        }

        private void ClearRows()
        {
            list.SuspendLayout();
            list.Controls.Clear();
            foreach (FolderRow row in rows) row.Panel.Dispose();
            rows.Clear();
            list.ResumeLayout();
        }

        // 한 줄: [체크] 폴더명 | 전체 경로 | AI 선택. 줄 아무 곳이나 클릭해도 체크가 바뀐다.
        private FolderRow CreateRow(FolderItem item, string ai, bool isChecked)
        {
            var row = new FolderRow { Item = item };
            var panel = new TableLayoutPanel {
                ColumnCount = 4, RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(S(12), 0, S(12), 1),
                BackColor = Color.White,
                Cursor = Cursors.Hand
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(140)));

            var check = new CheckBox { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, S(10), 0), Checked = isChecked };
            var name = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Text = item.Name, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0) };
            var path = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Text = item.FullPath, ForeColor = MutedColor, Margin = new Padding(S(8), 0, S(8), 0) };
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0), Cursor = Cursors.Default };
            combo.Items.AddRange(AiTools);
            combo.SelectedItem = ai;
            // 목록 스크롤 중 마우스 휠로 AI 값이 바뀌지 않게 하고, 대신 목록을 스크롤한다.
            combo.MouseWheel += delegate(object sender, MouseEventArgs e)
            {
                ((HandledMouseEventArgs)e).Handled = true;
                if (combo.DroppedDown) return;
                list.AutoScrollPosition = new Point(0, Math.Max(0, -list.AutoScrollPosition.Y - e.Delta));
            };

            EventHandler toggle = delegate { check.Checked = !check.Checked; };
            panel.Click += toggle;
            name.Click += toggle;
            path.Click += toggle;
            check.CheckedChanged += delegate
            {
                panel.BackColor = check.Checked ? CheckedRowColor : Color.White;
                UpdateSelectedCount();
            };
            panel.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(236, 238, 241)))
                    e.Graphics.DrawLine(pen, 0, panel.Height - 1, panel.Width, panel.Height - 1);
            };

            panel.Controls.Add(check, 0, 0);
            panel.Controls.Add(name, 1, 0);
            panel.Controls.Add(path, 2, 0);
            panel.Controls.Add(combo, 3, 0);
            panel.Height = Math.Max(S(44), combo.PreferredHeight + S(14));
            panel.BackColor = isChecked ? CheckedRowColor : Color.White;

            row.Panel = panel;
            row.Check = check;
            row.Ai = combo;
            return row;
        }

        private void FitRowWidths()
        {
            int width = list.ClientSize.Width;
            if (width <= 0) return;
            list.SuspendLayout();
            foreach (FolderRow row in rows) row.Panel.Width = width;
            list.ResumeLayout();
        }

        private void UpdateSelectedCount()
        {
            selectedCount.Text = string.Format("선택 {0} / {1}", rows.Count(r => r.Check.Checked), rows.Count);
        }

        private void SetAllChecked(bool value)
        {
            foreach (FolderRow row in rows) row.Check.Checked = value;
        }

        private void RunSelected(object sender, EventArgs e)
        {
            var selected = rows.Where(r => r.Check.Checked)
                .Select(r => new { Folder = r.Item, Ai = r.SelectedAi })
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
            // 1개: 전체, 2개: 좌우, 3~4개: 2열, 5~9개: 3열 (README 배치 예시와 동일)
            int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count)));
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
        }

        private sealed class FolderRow
        {
            internal FolderItem Item;
            internal TableLayoutPanel Panel;
            internal CheckBox Check;
            internal ComboBox Ai;
            internal string SelectedAi { get { return (string)Ai.SelectedItem ?? AiTools[0]; } }
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
