using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Lumos
{
    public sealed class Settings
    {
        public string? RegionId { get; set; }
        public string? Queue { get; set; }
        public int WarnMinutes { get; set; } = 30;
        public bool Autostart { get; set; } = true;
        public bool Sound { get; set; } = true;
        public bool Notify { get; set; } = true;
        public bool NotifyNewSchedule { get; set; } = true;
        /// <summary>TopCenter, TopLeft, TopRight або TrayOnly.</summary>
        public string Position { get; set; } = "TopCenter";
        public bool HideWhenOn { get; set; }
        /// <summary>Dark (сливова) або Light (морозна).</summary>
        public string Theme { get; set; } = "Dark";

        public static string DataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumos");

        private static string FilePath => Path.Combine(DataDir, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch (Exception ex) { Log.Write("Settings load: " + ex.Message); }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Log.Write("Settings save: " + ex.Message); }
        }
    }

    internal static class AutostartHelper
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Lumos";

        public static void Apply(bool enabled)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (enabled && Environment.ProcessPath != null)
                    key.SetValue(ValueName, "\"" + Environment.ProcessPath + "\"");
                else
                    key.DeleteValue(ValueName, false);
            }
            catch (Exception ex) { Log.Write("Autostart: " + ex.Message); }
        }
    }
}
