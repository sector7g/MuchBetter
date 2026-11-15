// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Collections.Generic;
using System.Management;
using System.Text.RegularExpressions;
using S7G.mbpm.Core;

namespace S7G.mbpm.Collection
{
    /// <summary>
    /// Maps worker-process PIDs to IIS app pool names using each process's command line
    /// (w3wp.exe ... -ap "PoolName" ...), read through WMI. Only a fallback for workers that IIS's
    /// W3SVC_W3WP counters don't cover yet; called from the poll timer (and once at startup for the
    /// machine's memory size).
    /// </summary>
    internal sealed class PoolMapper : IPoolNameSource
    {
        // -ap "Pool Name"  or  -ap PoolName
        private static readonly Regex AppPoolArgument = new Regex(
            "-ap\\s+(?:\"(?<name>[^\"]+)\"|(?<name>\\S+))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly ManagementScope scope;
        private readonly ObjectQuery processQuery;

        public PoolMapper(string processName)
        {
            scope = new ManagementScope(@"root\cimv2");
            processQuery = new ObjectQuery(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '" + processName + ".exe'");
        }

        /// <summary>Worker processes whose command line could not be read (exiting, or this console isn't elevated).</summary>
        public int HiddenCount { get; private set; }

        /// <summary>Returns a new PID -> pool name map on every call (never mutated afterwards).</summary>
        public Dictionary<int, string> GetPidToPool()
        {
            EnsureConnected();

            var map = new Dictionary<int, string>();
            int hidden = 0;

            using (var searcher = new ManagementObjectSearcher(scope, processQuery))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementObject process in results)
                {
                    using (process)
                    {
                        int pid = Convert.ToInt32(process["ProcessId"]);
                        string commandLine = process["CommandLine"] as string;
                        if (commandLine == null)
                        {
                            hidden++;
                            continue;
                        }

                        Match match = AppPoolArgument.Match(commandLine);
                        map[pid] = match.Success ? match.Groups["name"].Value : string.Format(Strings.UnnamedPool, pid);
                    }
                }
            }

            HiddenCount = hidden;
            return map;
        }

        public string GetUser(int pid)
        {
            return ProcessOwner.Get(pid);
        }

        /// <summary>Total physical memory of the target in MB, or null if it can't be read.</summary>
        public double? GetTotalPhysicalMemoryMB()
        {
            try
            {
                EnsureConnected();
                var query = new ObjectQuery("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                using (var searcher = new ManagementObjectSearcher(scope, query))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject system in results)
                    {
                        using (system)
                        {
                            return Convert.ToDouble(system["TotalPhysicalMemory"]) / (1024.0 * 1024.0);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Optional: the memory meter then shows available memory only.
            }
            return null;
        }

        private void EnsureConnected()
        {
            if (!scope.IsConnected)
            {
                scope.Connect();
            }
        }
    }
}
