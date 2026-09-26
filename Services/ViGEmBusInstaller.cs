using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;

namespace MobileWheel.Win.Services;

/// <summary>
/// 内置 ViGEmBus 驱动安装包的管理：先检测驱动是否可用，缺失时把随程序打包的官方安装包
/// （ViGEmBus 1.22.0，BSD-3-Clause）释放到临时目录，再以管理员身份静默安装。
/// 驱动是内核组件，安装必须提权，所以一定会弹一次 UAC；用户点“否”时
/// <see cref="Process.Start(ProcessStartInfo)"/> 抛 <see cref="Win32Exception"/>，
/// 错误码为 <see cref="ErrorCancelled"/>。
/// </summary>
public static class ViGEmBusInstaller
{
    /// <summary>嵌入资源名，与 csproj 里的 LogicalName 一致</summary>
    private const string SetupResourceName = "MobileWheel.Win.ViGEmBusSetup.exe";

    /// <summary>Advanced Installer 的静默参数：不出界面、装完不自动重启</summary>
    private const string SetupArguments = "/exenoui /qn /norestart";

    /// <summary>用户取消 UAC 提权时 ShellExecute 返回的 Win32 错误码</summary>
    public const int ErrorCancelled = 1223;

    /// <summary>驱动是否可用：能开出 ViGEm 客户端就说明虚拟总线设备在</summary>
    public static bool IsDriverAvailable()
    {
        try
        {
            using var client = new ViGEmClient();
            return true;
        }
        catch (VigemBusNotFoundException)
        {
            return false;
        }
        catch
        {
            // 服务被停用、设备异常等一并不当作“可用”
            return false;
        }
    }

    /// <summary>
    /// 释放内置安装包并提权静默安装，返回安装程序的退出码。
    /// 用户取消 UAC 会抛 <see cref="Win32Exception"/>（NativeErrorCode = <see cref="ErrorCancelled"/>）。
    /// </summary>
    public static async Task<int> InstallAsync(CancellationToken cancellationToken = default)
    {
        var setupPath = ExtractSetup();

        var startInfo = new ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = SetupArguments,
            // 提权只能走 ShellExecute，UseShellExecute=false 时 Verb 会被忽略
            UseShellExecute = true,
            Verb = "runas",
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("安装程序未能启动");

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        finally
        {
            TryDelete(setupPath);
        }
    }

    /// <summary>把嵌入的安装包写到临时目录，返回路径</summary>
    private static string ExtractSetup()
    {
        var path = Path.Combine(Path.GetTempPath(), "mobileWheel_ViGEmBus_1.22.0_setup.exe");

        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(SetupResourceName)
            ?? throw new InvalidOperationException($"程序里没有内置安装包（{SetupResourceName}），请重新构建");

        using var file = File.Create(path);
        resource.CopyTo(file);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 安装程序可能还留着句柄，临时文件交给系统清理
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
