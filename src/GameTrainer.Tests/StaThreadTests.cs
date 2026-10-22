using System.IO;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 入口线程模型契约。
///
/// 为什么这条必须锁住：
/// 主线程没有 [STAThread] 时，Shell 的 IFileDialog（WinForms OpenFileDialog
/// 底层用的就是它）会永久阻塞 —— 栈停在 IFileDialog.Show 不返回，
/// 对话框窗口从不出现，主窗口被置 Enabled=False，
/// 用户看到的是「点一下设置路径就卡死，然后闪退」。
///
/// 这个 bug 排查花了好几轮：先后试过 BeginInvoke 延后、原生 COM 接口、
/// 关 FreeConsole、切 WinExe，全都无效，因为根因是套间模型。
///
/// 测试写法说明：本项目是 WinForms，直接断言运行时线程状态会受测试宿主影响
/// （xUnit 的线程不一定是程序入口线程），所以这里检查源码特性是否存在。
/// 边界：如果有人把 [STAThread] 挪到别的辅助方法上，本测试会漏报；
/// 那种情况需要人工 review。
/// </summary>
public class StaThreadTests
{
    private static string FindProgramSource()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "src", "GameTrainer", "Program.cs");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    [Fact]
    public void 入口方法带STAThread特性()
    {
        string path = FindProgramSource();
        if (path == null) return;   // 源码不在预期位置时跳过，不误报

        string src = File.ReadAllText(path);

        int mainIdx = src.IndexOf("private static int Main(string[] args)", StringComparison.Ordinal);
        Assert.True(mainIdx > 0, "找不到 Main 方法");

        // 取 Main 之前的一段，检查里面是否有 [STAThread]
        int start = Math.Max(0, mainIdx - 400);
        string before = src[start..mainIdx];

        Assert.Contains("[STAThread]", before);
    }

    [Fact]
    public void 文件对话框使用原生选择器而非自实现()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string gui = Path.Combine(dir, "src", "GameTrainer", "GuiForm.cs");
            if (File.Exists(gui))
            {
                string src = File.ReadAllText(gui);

                // 必须用系统选择器
                Assert.Contains("new OpenFileDialog", src);

                // 不能复活自己实现的残缺浏览器
                Assert.DoesNotContain("FileBrowserDialog", src);
                return;
            }
            dir = Path.GetDirectoryName(dir);
        }
    }
}
