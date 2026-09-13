using System;
using System.Windows.Forms;

namespace SlitherIn
{
    /// <summary>
    /// Entry point. `REV` is the single build-identity constant: shown in the tray
    /// menu, bumped on EVERY code change, and verified inside the built EXE by
    /// build.ps1 as the "the build actually ran" proof.
    /// </summary>
    internal static class Program
    {
        public const string REV = "rev-20260912-19";

        /// <summary>Ensures the REV literal is actually embedded in the binary
        /// (Roslyn omits unused const string literals, which would break the
        /// REV-verify step). The tray version item will display this.</summary>
        public static string BuildRevision => REV;

        [STAThread]
        private static void Main(string[] args)
        {
            // WinForms threading model: hooks + NotifyIcon need an STA thread with a
            // message pump, which Application.Run() below provides.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            CliArgs cli = CliArgs.Parse(args);

            if (!Startup.AcquireSingleInstance())
            {
                // A second copy was launched while one already runs: tell that
                // instance to surface a balloon, then exit quietly.
                Startup.NotifyExistingInstance();
                return;
            }

            try
            {
                using (App app = App.Boot(cli))
                {
                    // Blocking message loop. Exits when ExitThread is called
                    // (e.g. from Dispose of the tray / a shutdown command).
                    Application.Run();
                }
            }
            finally
            {
                Startup.ReleaseSingleInstance();
            }
        }
    }
}