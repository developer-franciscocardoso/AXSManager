using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("AXS Manager")]
[assembly: AssemblyDescription("ARTSoft XML Windows service manager")]
[assembly: AssemblyVersion("1.0.5.0")]
[assembly: AssemblyFileVersion("1.0.5.0")]

namespace AxsManager
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool created;
            using (var mutex = new Mutex(false, @"Local\AxsManager.UI.v1", out created))
            using (var activate = new EventWaitHandle(false,
                EventResetMode.AutoReset, @"Local\AxsManager.Activate.v1"))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(args.Contains("--wait") ? 10000 : 0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) { activate.Set(); return; }
                try
                {
                    var store = new StateStore();
                    var form = new MainForm(store, store.Load(), args.Contains("--tray"),
                        args.Contains("--setup"), args.Contains("--xml-users"),
                        args.FirstOrDefault(x => x.StartsWith("--start=", StringComparison.Ordinal)));
                    var registration = ThreadPool.RegisterWaitForSingleObject(activate,
                        delegate
                        {
                            if (form.IsHandleCreated && !form.IsDisposed)
                            {
                                form.BeginInvoke(new Action(form.ShowManager));
                            }
                        }, null, -1, false);
                    try { Application.Run(form); }
                    finally { registration.Unregister(null); }
                }
                catch (Exception error)
                {
                    MessageBox.Show("AXS Manager could not start.\r\n\r\n" + error.Message,
                        "AXS Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        public static void Elevate(string resumeArgument = "")
        {
            var arguments = "--wait" + (resumeArgument == "" ? "" :
                " " + CommandLine.Quote(resumeArgument));
            Process.Start(new ProcessStartInfo(Application.ExecutablePath, arguments)
            {
                UseShellExecute = true, Verb = "runas",
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            });
        }
    }
}
