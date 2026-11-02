using System;
using System.IO;
using System.Windows;

namespace Apage.Spikes.SingleFile
{
    public partial class App : Application
    {
        public static string ResultDirectory { get; private set; } = Path.Combine(Path.GetTempPath(), "ApageSingleFileSpike");

        protected override void OnStartup(StartupEventArgs e)
        {
            for (var i = 0; i < e.Args.Length - 1; i++)
            {
                if (string.Equals(e.Args[i], "--results", StringComparison.OrdinalIgnoreCase))
                    ResultDirectory = Path.GetFullPath(e.Args[i + 1]);
            }

            Directory.CreateDirectory(ResultDirectory);
            try
            {
                NativeBootstrap.Initialize();
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(ResultDirectory, "error.txt"), ex.ToString()); }
                catch { }
                Shutdown(1);
                return;
            }

            base.OnStartup(e);
        }
    }
}
