using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumos
{
    public enum StatusKind { On, Off, Emergency, NoData }

    public sealed class Interval
    {
        public int Start; // хвилини від півночі (може бути > 1440, тоді це завтра)
        public int End;
        public bool Off;
    }

    public sealed class Status
    {
        public StatusKind Kind = StatusKind.NoData;
        /// <summary>Коли зміниться стан (хвилини від сьогоднішньої півночі), якщо відомо.</summary>
        public int? NextChange;
        /// <summary>Найближче майбутнє відключення, коли світло зараз є.</summary>
        public Interval? NextOutage;
    }

    internal static class StatusEngine
    {
        public static bool IsOff(int value) => value != 1;

        public static List<Interval> Intervals(int[] slots)
        {
            var list = new List<Interval>();
            int start = 0;
            for (int i = 1; i <= 48; i++)
            {
                if (i == 48 || IsOff(slots[i]) != IsOff(slots[start]))
                {
                    list.Add(new Interval { Start = start * 30, End = i * 30, Off = IsOff(slots[start]) });
                    start = i;
                }
            }
            return list;
        }

        public static Status Compute(int[]? today, int[]? tomorrow, bool emergency, int nowMinutes)
        {
            var status = new Status();
            if (today == null)
            {
                status.Kind = emergency ? StatusKind.Emergency : StatusKind.NoData;
                return status;
            }

            int nowIndex = Math.Clamp(nowMinutes / 30, 0, 47);
            bool off = IsOff(today[nowIndex]);
            status.Kind = emergency ? StatusKind.Emergency : off ? StatusKind.Off : StatusKind.On;

            int[] all = tomorrow != null ? today.Concat(tomorrow).ToArray() : today;

            int j = nowIndex + 1;
            while (j < all.Length && IsOff(all[j]) == off) j++;
            if (j < all.Length) status.NextChange = j * 30;

            if (!off)
            {
                int a = j;
                while (a < all.Length && !IsOff(all[a])) a++;
                if (a < all.Length)
                {
                    int b = a;
                    while (b < all.Length && IsOff(all[b])) b++;
                    status.NextOutage = new Interval { Start = a * 30, End = b * 30, Off = true };
                }
            }
            return status;
        }

        public static string Hm(int minutes)
        {
            bool tomorrow = minutes >= 1440;
            int m = minutes % 1440;
            if (minutes == 1440) return "24:00";
            string t = (m / 60).ToString("00") + ":" + (m % 60).ToString("00");
            return tomorrow ? "завтра " + t : t;
        }

        public static string Duration(int minutes)
        {
            int h = minutes / 60, m = minutes % 60;
            if (h == 0) return m + " хв";
            return m == 0 ? h + " год" : h + " год " + m + " хв";
        }

        public static string Clock(int minutes) => (minutes / 60) + ":" + (minutes % 60).ToString("00");

        /// <summary>Поточний час у Києві, щоб графік був правильний навіть з іншим часовим поясом на ПК.</summary>
        public static DateTime KyivNow()
        {
            try
            {
                TimeZoneInfo tz = TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            }
            catch
            {
                return DateTime.Now;
            }
        }
    }
}
