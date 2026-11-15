// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Globalization;

namespace S7G.mbpm.Core
{
    /// <summary>
    /// Timestamps in MBPM's plain CSV files: two columns with the same moment, so no one has to
    /// convert or guess a time zone or UTC offset:
    ///   utc_timestamp     2026-11-14T23:33:00.123Z          (UTC, ISO 8601)
    ///   local_timestamp   2026-11-14T18:33:00.123-05:00     (local time with its UTC offset, ISO 8601)
    /// The offset is the one in effect at that moment, so the repeated hour when daylight saving
    /// time ends is unambiguous. The PerfMon export keeps PerfMon's own convention instead: local
    /// time, with the time zone and its bias in the header.
    /// </summary>
    internal static class Timestamps
    {
        public const string Header = "utc_timestamp,local_timestamp";
        public const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        public const string LocalFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";

        /// <summary>"utc_timestamp,local_timestamp" values for one moment.</summary>
        public static string Fields(DateTimeOffset time)
        {
            return Utc(time) + "," + Local(time);
        }

        public static string Utc(DateTimeOffset time)
        {
            return time.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture);
        }

        public static string Local(DateTimeOffset time)
        {
            return time.ToString(LocalFormat, CultureInfo.InvariantCulture);
        }

        public static bool TryParseLocal(string text, out DateTimeOffset time)
        {
            return DateTimeOffset.TryParseExact(text, LocalFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
        }

        /// <summary>A local clock reading with the offset in effect for it (for times captured without one).</summary>
        public static DateTimeOffset FromLocal(DateTime local)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(local));
        }
    }
}
