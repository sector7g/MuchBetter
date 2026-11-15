// (c) 2017-2026 Sector 7G Consulting LLC
using System.Collections.Generic;

namespace S7G.mbpm
{
    /// <summary>Product name, version and copyright, in one place.</summary>
    internal static class Brand
    {
        public const string Short = "MBPM";
        public const string Name = "A Much Better appPool Monitor";
        public const string Title = Short + "  ·  " + Name;     // splash, help, title bar, -h
        public const string Copyright = "© 2017-2026 Sector 7G Consulting LLC";

        public static string Version
        {
            get { return "v" + typeof(Brand).Assembly.GetName().Version.ToString(2); }
        }

        /// <summary>Title and copyright lines for plain text output (-h).</summary>
        public static IEnumerable<string> HeaderLines()
        {
            yield return Title + "  " + Version;
            yield return Copyright;
        }

        /// <summary>ASCII stand-ins for consoles without the symbols (--ascii).</summary>
        public static string Plain(string text)
        {
            return text.Replace("™", "(TM)").Replace("©", "(c)").Replace("“", "\"").Replace("”", "\"").Replace("·", "-");
        }
    }
}
