// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using S7G.mbpm.UI;

namespace S7G.mbpm
{
    /// <summary>Command-line settings. MBPM monitors the machine it runs on, nothing else.</summary>
    internal sealed class Options
    {
        public string Machine = Environment.MachineName;
        public int Cores;                                   // 0 = from the per-core counters
        public uint SampleIntervalSeconds = 1;
        public int PoolPollSeconds = 5;
        public string ProcessName = "w3wp";
        public string LogRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sector 7G", "mbpm");
        public bool Ascii;
        public bool AlternateScreen = true;
        public bool ShowSplash = true;
        public bool ExportOnExit = true;
        public bool KeepRaw;        // keep samples.csv / machine.csv after the export on exit
        public bool DebugLogs;      // also write pid_map.csv
        public bool ShowHelp;
        public string ThemeName;    // --theme: starting theme (F3 cycles)

        // --mock [cores=<n>][,pools=<n>]: a simulated IIS server instead of PDH/WMI. Startup only;
        // never mixed with a real session.
        public bool Mock;
        public int MockCores = MaxMockCores;
        public int MockPools = MaxMockPools;
        public const int MaxMockCores = 64;
        public const int MaxMockPools = 100;

        // "mbpm export <session folder>": build the exports from an existing session and exit.
        public string ExportFolder;
        public string ExportFrom;
        public string ExportTo;

        public const string MockMachine = "MOCK";

        /// <summary>The product header followed by the usage text.</summary>
        public static string UsageText
        {
            get { return string.Join(Environment.NewLine, Brand.HeaderLines()) + Environment.NewLine + Environment.NewLine + Usage; }
        }

        /// <summary>Command-line help (Strings.resx: Usage).</summary>
        public static string Usage
        {
            get { return Strings.Usage; }
        }

        public static Options Parse(string[] args)
        {
            var options = new Options();

            int first = 0;
            if (args.Length > 0 && args[0].Equals("export", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2 || args[1].StartsWith("-", StringComparison.Ordinal))
                {
                    throw new ArgumentException(Strings.ErrExportNeedsFolder);
                }
                options.ExportFolder = args[1];
                options.Machine = null;     // taken from the session folder
                first = 2;
            }

            for (int i = first; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg.ToLowerInvariant())
                {
                    case "-c":
                    case "--cores":
                        options.Cores = ParsePositive(NextValue(args, ref i), arg);
                        break;
                    case "-i":
                    case "--interval":
                        options.SampleIntervalSeconds = (uint)ParsePositive(NextValue(args, ref i), arg);
                        break;
                    case "-p":
                    case "--poll":
                        options.PoolPollSeconds = ParsePositive(NextValue(args, ref i), arg);
                        break;
                    case "-l":
                    case "--logdir":
                        options.LogRoot = NextValue(args, ref i);
                        break;
                    case "--ascii":
                        options.Ascii = true;
                        break;
                    case "--no-alt":
                        options.AlternateScreen = false;
                        break;
                    case "--no-splash":
                        options.ShowSplash = false;
                        break;
                    case "--no-export":
                        options.ExportOnExit = false;
                        break;
                    case "--keep-raw":
                        options.KeepRaw = true;
                        break;
                    case "--debug-logs":
                        options.DebugLogs = true;
                        break;
                    case "--from":
                        options.ExportFrom = NextValue(args, ref i);
                        break;
                    case "--to":
                        options.ExportTo = NextValue(args, ref i);
                        break;
                    case "--theme":
                        options.ThemeName = NextValue(args, ref i);
                        if (Theme.Find(options.ThemeName) == null)
                        {
                            throw new ArgumentException(string.Format(Strings.ErrUnknownTheme, options.ThemeName,
                                string.Join(", ", Theme.All.Select(t => t.Name))));
                        }
                        break;
                    case "--mock":
                    case "-mock":
                        options.Mock = true;
                        if (i + 1 < args.Length && args[i + 1].Contains("="))
                        {
                            ParseMockSettings(options, args[++i]);
                        }
                        break;
                    case "-h":
                    case "-?":
                    case "/?":
                    case "--help":
                        options.ShowHelp = true;
                        break;
                    default:
                        throw new ArgumentException(string.Format(Strings.ErrUnknownOption, arg));
                }
            }

            if (options.ExportFolder == null && (options.ExportFrom != null || options.ExportTo != null))
            {
                throw new ArgumentException(Strings.ErrFromToOnlyExport);
            }
            if (options.ExportFolder != null)
            {
                return options;
            }

            if (options.Mock)
            {
                // Mock sessions are always labeled MOCK, in the log folder and the export.
                options.Machine = MockMachine;
            }
            return options;
        }

        // "cores=8,pools=20": either or both, in any order.
        private static void ParseMockSettings(Options options, string value)
        {
            foreach (string part in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = part.Split(new[] { '=' }, 2);
                string name = pair[0].Trim().ToLowerInvariant();
                string number = pair.Length > 1 ? pair[1].Trim() : string.Empty;
                switch (name)
                {
                    case "cores":
                        options.MockCores = ParseRange(number, name, MaxMockCores);
                        break;
                    case "pools":
                        options.MockPools = ParseRange(number, name, MaxMockPools);
                        break;
                    default:
                        throw new ArgumentException(string.Format(Strings.ErrMockSetting, part.Trim(), MaxMockCores, MaxMockPools));
                }
            }
        }

        private static int ParseRange(string value, string name, int max)
        {
            int result;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || result < 1 || result > max)
            {
                throw new ArgumentException(string.Format(Strings.ErrMockRange, name, max, value));
            }
            return result;
        }

        private static string NextValue(string[] args, ref int i)
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException(string.Format(Strings.ErrOptionNeedsValue, args[i]));
            }
            i++;
            return args[i];
        }

        private static int ParsePositive(string value, string option)
        {
            int result;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || result <= 0)
            {
                throw new ArgumentException(string.Format(Strings.ErrOptionNeedsNumber, option, value));
            }
            return result;
        }
    }
}
