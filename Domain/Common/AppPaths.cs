using System;
using System.IO;

namespace CryptoSense.Domain.Common
{
    public static class AppPaths
    {
        public static string DataDirectory { get; }
        public static string DatabasePath => Path.Combine(DataDirectory, "cryptosense.db");
        public static string UserBackupFilePath => Path.Combine(DataDirectory, "users_backup.json");
        public static string SettingsFilePath => Path.Combine(DataDirectory, "user_preferences.json");
        public static string SignalMapFilePath => Path.Combine(DataDirectory, "signal_user_numbers.json");

        static AppPaths()
        {
            var volumeEnv = Environment.GetEnvironmentVariable("RAILWAY_VOLUME_MOUNT_PATH");
            if (!string.IsNullOrWhiteSpace(volumeEnv))
            {
                DataDirectory = volumeEnv.Trim();
            }
            else if (Directory.Exists("/data"))
            {
                DataDirectory = "/data";
            }
            else if (Directory.Exists("/app/data"))
            {
                DataDirectory = "/app/data";
            }
            else
            {
                DataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            }

            try
            {
                if (!Directory.Exists(DataDirectory))
                {
                    Directory.CreateDirectory(DataDirectory);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AppPaths] Warning: Could not create DataDirectory '{DataDirectory}': {ex.Message}");
            }

            EnsureDatabaseFile();
        }

        public static void EnsureDatabaseFile()
        {
            try
            {
                if (File.Exists(DatabasePath))
                {
                    return;
                }

                var candidates = new[]
                {
                    "/app/data/cryptosense.db",
                    "/data/cryptosense.db",
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cryptosense.db"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "cryptosense.db"),
                    Path.Combine(Directory.GetCurrentDirectory(), "cryptosense.db"),
                    "cryptosense.db"
                };

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate) && !string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(DatabasePath), StringComparison.OrdinalIgnoreCase))
                    {
                        var targetDir = Path.GetDirectoryName(DatabasePath);
                        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                        }

                        File.Copy(candidate, DatabasePath, overwrite: false);
                        Console.WriteLine($"[AppPaths] Migrated existing database: {candidate} -> {DatabasePath}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AppPaths] Error migrating database file: {ex.Message}");
            }
        }
    }
}
