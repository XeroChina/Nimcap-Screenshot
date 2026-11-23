using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NimCap.Screenshot.Launcher;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);

    private static int Main(string[] args)
    {
        var root = AppContext.BaseDirectory;
        var programDirectory = Path.Combine(root, "Program");
        var executable = Path.Combine(programDirectory, "NimCap.Screenshot.exe");
        if (!File.Exists(executable))
        {
            MessageBox(IntPtr.Zero, "Missing Program\\NimCap.Screenshot.exe", "NimCap Screenshot", 0x10);
            return 1;
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = programDirectory,
        };
        foreach (var argument in args)
            start.ArgumentList.Add(File.Exists(argument) ? Path.GetFullPath(argument) : argument);
        try
        {
            using var process = Process.Start(start);
            return process is null ? 1 : 0;
        }
        catch (Exception error)
        {
            MessageBox(IntPtr.Zero, error.Message, "NimCap Screenshot", 0x10);
            return 1;
        }
    }
}
