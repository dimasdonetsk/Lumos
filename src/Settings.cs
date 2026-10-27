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
        /// <summary>Dark (темна) або Light (світла).</summary>
        public string Theme { get; set; } = "Dark";
        /// <summary>Коли людина позначила «у мене немає світла» (null — не позначено).</summary>
        public DateTime? ManualOffSince { get; set; }
        /// <summary>Коли людина позначила «Увімкнули» під час планового відключення (світло дали раніше).</summary>
        public DateTime? ManualOnSince { get; set; }
        /// <summary>Коли людина натиснула «Світло повернулося» — звідси рахується, скільки світло вже є.</summary>
        public DateTime? LightBackAt { get; set; }

        // EcoFlow. Режим: null — не підключено, "Keys" — ключі розробника, "Login" — логін застосунку.
        public string? EcoMode { get; set; }
        public string? EcoHost { get; set; }
        public string? EcoAccessKey { get; set; }
        /// <summary>Secret Key і пароль зберігаються зашифрованими (DPAPI, лише для цього користувача Windows).</summary>
        public string? EcoSecretEnc { get; set; }
        public string? EcoEmail { get; set; }
        public string? EcoPasswordEnc { get; set; }
        public string? EcoSn { get; set; }
        public string? EcoName { get; set; }
        /// <summary>Нагадувати поставити станцію на зарядку, коли світло є, а вона не заряджається.</summary>
        public bool EcoRemind { get; set; } = true;

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
        // Сюди Windows пише, чи автозапуск не вимкнули в Диспетчері завдань → «Автозавантаження».
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ValueName = "Lumos";

        /// <summary>Шлях до exe. Інсталятор кладе програму в …\Lumos\current\Lumos.exe — він не змінюється між оновленнями.</summary>
        private static string? ExePath()
        {
            string? path = Environment.ProcessPath;
            if (path == null) return null;
            // Якщо раптом запущено з тимчасової папки оновлення, беремо постійну копію.
            string? dir = Path.GetDirectoryName(path);
            string? root = dir != null ? Path.GetDirectoryName(dir) : null;
            if (root != null && !string.Equals(Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase))
            {
                string current = Path.Combine(root, "current", Path.GetFileName(path));
                if (File.Exists(current) && File.Exists(Path.Combine(root, "Update.exe"))) return current;
            }
            return path;
        }

        /// <summary>
        /// Записати або прибрати автозапуск. userAction — людина сама ввімкнула пункт у меню:
        /// тоді знімаємо й заборону з «Автозавантаження» Windows, якщо вона там стоїть.
        /// </summary>
        public static void Apply(bool enabled, bool userAction = false)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
                string? exe = ExePath();
                if (enabled && exe != null)
                {
                    string value = "\"" + exe + "\" --autostart";
                    if (!string.Equals(key.GetValue(ValueName) as string, value, StringComparison.Ordinal))
                        key.SetValue(ValueName, value);
                    if (userAction)
                        using (RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                            approved?.DeleteValue(ValueName, false);
                }
                else
                    key.DeleteValue(ValueName, false);
            }
            catch (Exception ex) { Log.Write("Autostart: " + ex.Message); }
        }

        /// <summary>Автозапуск вимкнули в Диспетчері завдань (Windows пропускає наш запис).</summary>
        public static bool DisabledByWindows()
        {
            try
            {
                using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                return approved?.GetValue(ValueName) is byte[] b && b.Length > 0 && (b[0] & 1) == 1;
            }
            catch { return false; }
        }
    }
}
