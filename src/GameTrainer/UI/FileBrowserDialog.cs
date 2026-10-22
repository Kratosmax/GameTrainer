using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace GameTrainer.UI;

/// <summary>
/// 应用内文件浏览器。
///
/// 为什么不用系统文件对话框：
/// 实测该环境下 IFileDialog.Show 会永久阻塞（栈停在 Show 不返回，
/// 主窗口 Enabled=False 但对话框窗口从未出现），连 PowerShell 自己的
/// OpenFileDialog 也一样卡死。所以必须自己实现。
///
/// 为什么不用「打开资源管理器让用户自己找」：
/// 那个做法是骗人的 —— 用户在资源管理器里双击 exe 会直接启动游戏，
/// 而不是把路径带回本程序。按钮写着「选择」却做不到选择，是误导。
///
/// 本实现的行为约定：
///   - 双击目录 = 进入该目录（不会启动任何程序）
///   - 双击 exe = 选中它（不会启动它）
///   - 只有点「确定」才把路径返回给调用方
/// </summary>
internal sealed class FileBrowserDialog : Form
{
    private readonly TextBox _pathBox;
    private readonly ListBox _list;
    private readonly Label _hint;
    private string _currentDir;
    private string _selectedFile;

    /// <summary>用户确认的文件完整路径。取消时为 null。</summary>
    public string SelectedPath => _selectedFile;

    public FileBrowserDialog(string title, string startDir, string expectedFileName)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(680, 480);
        BackColor = Tokens.PageBack;
        Font = Tokens.FontBody;

        // ---- 路径栏 ----
        _pathBox = new TextBox
        {
            Left = 16,
            Top = 16,
            Width = 560,
            Height = 26,
            Font = Tokens.FontBody,
            BorderStyle = BorderStyle.FixedSingle,
            ReadOnly = true
        };

        var up = new Button
        {
            Text = "上一级",
            Left = 584,
            Top = 14,
            Width = 80,
            Height = 30
        };
        up.Click += (_, _) => Navigate(Path.GetDirectoryName(_currentDir));

        // ---- 文件列表 ----
        _list = new ListBox
        {
            Left = 16,
            Top = 52,
            Width = 648,
            Height = 340,
            Font = Tokens.FontBody,
            IntegralHeight = false
        };

        // 双击：目录进入、exe 选中。都不启动程序。
        _list.DoubleClick += (_, _) => ActivateCurrentItem();

        // 单击 exe 也直接记下选中，方便用户只点一下再按确定
        _list.SelectedIndexChanged += (_, _) =>
        {
            string full = CurrentFullPath();
            _selectedFile = full != null && full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        };

        // ---- 提示 ----
        _hint = new Label
        {
            Left = 16,
            Top = 398,
            Width = 648,
            Height = 40,
            ForeColor = Tokens.TextSecondary,
            Font = Tokens.FontCaption,
            Text = $"双击文件夹进入；双击或单击 {expectedFileName} 选中它（不会启动游戏）。"
        };

        // ---- 按钮 ----
        var ok = new Button
        {
            Text = "确定",
            Left = 500,
            Top = 438,
            Width = 80,
            Height = 32,
            DialogResult = DialogResult.OK
        };
        ok.Click += (_, _) =>
        {
            // 确定时若还没选中文件，但列表里有高亮的 exe，就用它
            if (string.IsNullOrWhiteSpace(_selectedFile))
            {
                string full = CurrentFullPath();
                if (full != null && full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    _selectedFile = full;
            }

            if (string.IsNullOrWhiteSpace(_selectedFile))
            {
                MessageBox.Show(this, "请先选中一个 .exe 文件。", "GameTrainer",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.None;
            }
        };

        var cancel = new Button
        {
            Text = "取消",
            Left = 588,
            Top = 438,
            Width = 80,
            Height = 32,
            DialogResult = DialogResult.Cancel
        };

        Controls.AddRange(new Control[] { _pathBox, up, _list, _hint, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;

        Navigate(string.IsNullOrWhiteSpace(startDir) ? DefaultStartDir() : startDir);
    }

    private static string DefaultStartDir()
    {
        // 从程序所在盘开始，比从 C:\ 开始更容易找到游戏
        string baseDir = AppContext.BaseDirectory;
        try
        {
            var root = Path.GetPathRoot(baseDir);
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root)) return root;
        }
        catch { }

        return @"C:\";
    }

    private void Navigate(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;

        try
        {
            if (!Directory.Exists(dir)) return;
            _currentDir = dir;
        }
        catch
        {
            return;
        }

        _pathBox.Text = _currentDir;
        _selectedFile = null;
        _list.BeginUpdate();
        _list.Items.Clear();

        try
        {
            // 目录在前
            var dirs = Directory.GetDirectories(_currentDir);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (string d in dirs)
            {
                string name = Path.GetFileName(d);
                // 跳过隐藏与系统目录，避免一屏全是 $Recycle.Bin 之类
                try
                {
                    var attr = File.GetAttributes(d);
                    if ((attr & FileAttributes.Hidden) != 0) continue;
                }
                catch { continue; }

                _list.Items.Add(new Entry(name + "\\", d, IsDir: true));
            }

            // 然后 exe
            var files = Directory.GetFiles(_currentDir, "*.exe");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
                _list.Items.Add(new Entry(Path.GetFileName(f), f, IsDir: false));
        }
        catch (UnauthorizedAccessException)
        {
            _hint.Text = "这个目录没有访问权限，换一个试试。";
        }
        catch (Exception ex)
        {
            _hint.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            _list.EndUpdate();
        }
    }

    private string CurrentFullPath()
    {
        return _list.SelectedItem is Entry e ? e.FullPath : null;
    }

    private void ActivateCurrentItem()
    {
        if (_list.SelectedItem is not Entry e) return;

        if (e.IsDir)
        {
            Navigate(e.FullPath);
        }
        else
        {
            // 双击 exe 只选中，不启动
            _selectedFile = e.FullPath;
            _pathBox.Text = e.FullPath;
        }
    }

    private sealed record Entry(string Display, string FullPath, bool IsDir)
    {
        public override string ToString() => Display;
    }
}
