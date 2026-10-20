using System.IO;
using System.Windows;

namespace TeyvatRoutine;
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        int imageTest = Array.IndexOf(e.Args, "--profile-vision");
        if (imageTest >= 0 && e.Args.Length > imageTest + 2)
        {
            try { File.WriteAllText(e.Args[imageTest + 2], Task.Run(() => RecognitionTests.TestUserTemplates(e.Args[imageTest + 1])).GetAwaiter().GetResult()); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(e.Args[imageTest + 2], ex.ToString()); Shutdown(1); }
            return;
        }
        int previousIndex = Array.IndexOf(e.Args, "--wait-for-process");
        if (previousIndex >= 0 && e.Args.Length > previousIndex + 1 && int.TryParse(e.Args[previousIndex + 1], out int previousId) && previousId != Environment.ProcessId)
        {
            // Let the previous instance release its global hotkeys before registering ours.
            try { using var previous = System.Diagnostics.Process.GetProcessById(previousId); previous.WaitForExit(5000); }
            catch (ArgumentException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        if (e.Args.Contains("--self-test"))
        {
            var index = Array.IndexOf(e.Args, "--self-test");
            var path = e.Args.Length > index + 1 ? e.Args[index + 1] : "self-test.txt";
            try { File.WriteAllText(path, Task.Run(SelfTests.Run).GetAwaiter().GetResult()); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(path, ex.ToString()); Shutdown(1); }
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(args.Exception.Message, "Teyvat Routine 오류");
            args.Handled = true;
        };
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
