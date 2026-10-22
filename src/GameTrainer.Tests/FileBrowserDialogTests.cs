using System.IO;
using System.Reflection;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 应用内文件浏览器的行为契约。
///
/// 背景：上一版用「打开资源管理器让用户自己找」当作选择入口，
/// 但资源管理器里双击 exe 会直接启动游戏 —— 按钮说「选择」，
/// 实际行为是「启动」，是欺骗性设计。
///
/// 这一组用例锁住三条不可回退的规则：
///   1. 浏览器的目录列表只包含目录与 .exe，不包含其它类型
///   2. 双击 exe 只设置选中项，不调用任何启动 API
///   3. 取消时不返回任何路径
///
/// 前两条靠源码检查（本项目是 WinForms，UI 类不便实例化到测试环境）。
/// </summary>
public class FileBrowserDialogTests
{
    private static string SourcePath()
    {
        // 从测试程序集位置往上找源码目录
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "src", "GameTrainer", "UI", "FileBrowserDialog.cs");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    [Fact]
    public void 源码存在()
    {
        Assert.NotNull(SourcePath());
    }

    [Fact]
    public void 双击exe不会启动进程()
    {
        string path = SourcePath();
        if (path == null) return;

        string src = File.ReadAllText(path);

        // 取 ActivateCurrentItem 方法体
        int idx = src.IndexOf("private void ActivateCurrentItem()", StringComparison.Ordinal);
        Assert.True(idx > 0, "找不到 ActivateCurrentItem");

        int end = src.IndexOf("private sealed record Entry", idx, StringComparison.Ordinal);
        Assert.True(end > idx, "找不到方法结束标记");

        string body = src[idx..end];

        // 这是本用例的核心：激活 exe 的分支里绝不能出现进程启动
        Assert.DoesNotContain("Process.Start", body);
        Assert.DoesNotContain("ShellExecute", body);
        Assert.DoesNotContain("UseShellExecute", body);

        // 必须只是记录选中路径
        Assert.Contains("_selectedFile = e.FullPath", body);
    }

    [Fact]
    public void 列表只收目录和exe()
    {
        string path = SourcePath();
        if (path == null) return;

        string src = File.ReadAllText(path);

        // 只枚举 "*.exe"
        Assert.Contains("GetFiles(_currentDir, \"*.exe\")", src);

        // 不应枚举所有文件
        Assert.DoesNotContain("GetFiles(_currentDir)", src);
    }

    [Fact]
    public void 取消时返回null()
    {
        string path = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && path != null; i++)
        {
            string candidate = Path.Combine(path, "src", "GameTrainer", "GuiForm.cs");
            if (File.Exists(candidate))
            {
                string src = File.ReadAllText(candidate);

                // 调用方必须是"OK 才取路径，否则 null"
                Assert.Contains("dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null", src);
                return;
            }
            path = Path.GetDirectoryName(path);
        }
    }
}
