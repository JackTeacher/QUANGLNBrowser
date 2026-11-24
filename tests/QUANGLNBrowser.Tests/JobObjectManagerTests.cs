using System.Diagnostics;
using QUANGLNBrowser.Network;
using Xunit;

namespace QUANGLNBrowser.Tests;

public sealed class JobObjectManagerTests
{
    [Fact]
    public void Dispose_KillsAssignedChildProcess_ViaKillOnJobClose()
    {
        var job = new JobObjectManager();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 30 127.0.0.1 >nul",
            CreateNoWindow = true,
            UseShellExecute = false
        });

        Assert.NotNull(process);
        try
        {
            job.AssignProcess(process!);
            Assert.False(process!.HasExited);

            job.Dispose();

            bool exited = process.WaitForExit(5000);
            Assert.True(exited);
        }
        finally
        {
            job.Dispose();
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
