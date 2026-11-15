// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using S7G.mbpm.Interop;

namespace S7G.mbpm.Collection
{
    /// <summary>One instance's value from a wildcard counter.</summary>
    internal struct InstanceValue
    {
        public string Name;
        public double Value;
        public bool Valid;      // false for a rate counter's first sample, or an instance mid-exit
    }

    /// <summary>Thin wrapper over a PDH query. Not thread-safe; the caller serializes access.</summary>
    internal sealed class PdhQuery : IDisposable
    {
        private const uint Format = NativeMethods.PDH_FMT_DOUBLE | NativeMethods.PDH_FMT_NOCAP100;

        private IntPtr query;

        public PdhQuery()
        {
            Check(NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out query), "open query");
        }

        /// <summary>Adds a counter; throws if PDH rejects it.</summary>
        public IntPtr Add(string path)
        {
            IntPtr counter;
            Check(NativeMethods.PdhAddEnglishCounter(query, path, IntPtr.Zero, out counter), "add counter " + path);
            return counter;
        }

        /// <summary>Adds a counter; returns IntPtr.Zero if PDH rejects it (optional counters).</summary>
        public IntPtr TryAdd(string path)
        {
            IntPtr counter;
            uint status = NativeMethods.PdhAddEnglishCounter(query, path, IntPtr.Zero, out counter);
            return status == NativeMethods.ERROR_SUCCESS ? counter : IntPtr.Zero;
        }

        /// <summary>Takes one sample of every counter. Returns false when there is no data at all.</summary>
        public bool Collect()
        {
            uint status = NativeMethods.PdhCollectQueryData(query);
            if (status == NativeMethods.PDH_NO_DATA)
            {
                return false;
            }
            Check(status, "collect");
            return true;
        }

        /// <summary>
        /// All instances of a wildcard counter. Invalid items are kept (flagged), so arrays of counters
        /// on the same object stay aligned by position.
        /// </summary>
        public static List<InstanceValue> ReadArray(IntPtr counter)
        {
            var result = new List<InstanceValue>();
            if (counter == IntPtr.Zero)
            {
                return result;
            }

            int itemSize = Marshal.SizeOf(typeof(NativeMethods.PDH_FMT_COUNTERVALUE_ITEM));

            // Ask for the size, allocate, read. Retry if instances appeared in between.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint bufferSize = 0;
                uint itemCount;
                uint status = NativeMethods.PdhGetFormattedCounterArray(counter, Format, ref bufferSize, out itemCount, IntPtr.Zero);
                if (status != NativeMethods.PDH_MORE_DATA)
                {
                    return result;     // no instances right now (or no valid data yet)
                }

                IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
                try
                {
                    status = NativeMethods.PdhGetFormattedCounterArray(counter, Format, ref bufferSize, out itemCount, buffer);
                    if (status == NativeMethods.PDH_MORE_DATA)
                    {
                        continue;
                    }
                    if (status != NativeMethods.ERROR_SUCCESS)
                    {
                        return result;
                    }

                    for (int i = 0; i < itemCount; i++)
                    {
                        var item = (NativeMethods.PDH_FMT_COUNTERVALUE_ITEM)Marshal.PtrToStructure(
                            IntPtr.Add(buffer, i * itemSize), typeof(NativeMethods.PDH_FMT_COUNTERVALUE_ITEM));

                        result.Add(new InstanceValue
                        {
                            Name = Marshal.PtrToStringUni(item.Name),
                            Value = item.FmtValue.DoubleValue,
                            Valid = IsValid(item.FmtValue.CStatus),
                        });
                    }
                    return result;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return result;
        }

        /// <summary>The value of a single-instance counter, or null if unavailable.</summary>
        public static double? ReadValue(IntPtr counter)
        {
            if (counter == IntPtr.Zero)
            {
                return null;
            }

            uint type;
            NativeMethods.PDH_FMT_COUNTERVALUE value;
            uint status = NativeMethods.PdhGetFormattedCounterValue(counter, Format, out type, out value);
            if (status != NativeMethods.ERROR_SUCCESS || !IsValid(value.CStatus))
            {
                return null;
            }
            return value.DoubleValue;
        }

        public void Dispose()
        {
            if (query != IntPtr.Zero)
            {
                NativeMethods.PdhCloseQuery(query);   // also frees the counters
                query = IntPtr.Zero;
            }
        }

        private static bool IsValid(uint cstatus)
        {
            return cstatus == NativeMethods.PDH_CSTATUS_VALID_DATA || cstatus == NativeMethods.PDH_CSTATUS_NEW_DATA;
        }

        private static void Check(uint status, string what)
        {
            if (status != NativeMethods.ERROR_SUCCESS)
            {
                throw new PdhException(string.Format(Strings.PdhFailed, what, NativeMethods.DescribePdhStatus(status)), status);
            }
        }
    }

    internal sealed class PdhException : Exception
    {
        public PdhException(string message, uint status) : base(message)
        {
            Status = status;
        }

        public uint Status { get; private set; }
    }
}
