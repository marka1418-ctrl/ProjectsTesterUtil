using System.IO;

namespace ProjectTesterMonitor
{
    /// <summary>
    /// Single working folder for everything the monitor creates at runtime:
    /// the extracted testing utility, settings presets, logs, downloaded
    /// projects and results. Keeps the exe from littering the folder it was
    /// started from.
    /// </summary>
    internal static class AppPaths
    {
        public const string DataFolderName = "ProjectTesterData";

        /// <summary>
        /// Working folder, created next to the exe on first access.
        /// </summary>
        public static string Root { get; }

        static AppPaths()
        {
            Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DataFolderName);
            Directory.CreateDirectory(Root);

            // ProjectTestingUtility.exe is extracted into Root and writes its own
            // files (logs, projects, results) next to itself, so everything ends up
            // here as well. The current directory follows for the code that uses it.
            Directory.SetCurrentDirectory(Root);
        }

        public static string Combine(string name) => Path.Combine(Root, name);
    }
}
