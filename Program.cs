// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using S7G.mbpm.Collection;
using S7G.mbpm.Interop;
using S7G.mbpm.Logging;
using S7G.mbpm.UI;

namespace S7G.mbpm
{
    internal static class Program
    {
        private static readonly ManualResetEvent ShutdownDone = new ManualResetEvent(false);

        // Kept in a static field so the GC can't collect the delegate while Windows still holds a pointer to it.
        private static NativeMethods.ConsoleCtrlHandler ctrlHandler;
        private static App app;
        private static volatile bool quitRequested;

        private static readonly TimeSpan MinimumSplash = TimeSpan.FromSeconds(2);

        private static int Main(string[] args)
        {
            // © and other non-ASCII text in the usage text and messages.
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (System.IO.IOException)
            {
            }

            Options options;
            try
            {
                options = Options.Parse(args);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.WriteLine();
                Console.Error.WriteLine(Options.UsageText);
                return 2;
            }

            if (options.ShowHelp)
            {
                Console.WriteLine(Options.UsageText);
                return 0;
            }

            if (options.ExportFolder != null)
            {
                return RunExport(options.ExportFolder, options.Machine, options.ExportFrom, options.ExportTo) ? 0 : 1;
            }

            // Ctrl-C, Ctrl-Break and closing the window all come through here.
            ctrlHandler = OnConsoleCtrl;
            NativeMethods.SetConsoleCtrlHandler(ctrlHandler, true);

            if (!Terminal.Enter(options.AlternateScreen))
            {
                Console.Error.WriteLine(Strings.MsgVtUnsupported);
                ShutdownDone.Set();
                return 1;
            }
            Theme.Apply(options.ThemeName != null ? Theme.Find(options.ThemeName) : Theme.Default);

            // The splash stays up while MBPM connects (WMI and PDH can take a moment).
            Stopwatch splashTime = Stopwatch.StartNew();
            if (options.ShowSplash)
            {
                Splash.Draw(string.Format(Strings.SplashConnecting, options.Machine), options.Ascii);
            }

            try
            {
                app = new App(options);
            }
            catch (Exception e)
            {
                Terminal.Leave();
                Console.Error.WriteLine(string.Format(Strings.MsgStartupFailed, FirstLine(e.Message)));
                ShutdownDone.Set();
                return 1;
            }

            if (options.ShowSplash && !quitRequested)
            {
                Splash.Draw(Strings.SplashConnected, options.Ascii);
                WaitForKeyOr(MinimumSplash - splashTime.Elapsed);
            }

            if (quitRequested)
            {
                // Ctrl-C while connecting: nothing was sampled, so FinishSession removes the session folder.
                Terminal.Leave();
                app.Dispose();
                ShutdownDone.Set();
                FinishSession(app, options);
                return 0;
            }

            int exitCode = 0;
            try
            {
                app.Run();
            }
            catch (Exception e)
            {
                Terminal.Leave();
                Console.Error.WriteLine(string.Format(Strings.MsgStoppedError, e));
                exitCode = 1;
            }
            finally
            {
                Terminal.Leave();
                try
                {
                    app.Dispose();
                    Console.WriteLine(Strings.MsgStopped, app.SessionFolder);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(Strings.MsgCleanupFailed, e.Message);
                    exitCode = 1;
                }
                ShutdownDone.Set();

                // After ShutdownDone: a window close may end the process here, but the logs are already closed.
                FinishSession(app, options);
            }
            return exitCode;
        }

        // On exit: the export was written live, so there's nothing to build or delete. A session that
        // collected no samples is removed entirely.
        private static void FinishSession(App finished, Options options)
        {
            string folder = finished.SessionFolder;
            if (!options.ExportOnExit)
            {
                Console.WriteLine(Strings.MsgExportSkipped, folder);
                return;
            }

            Exporter.Result result = finished.LiveResult;
            if (result == null)
            {
                // The live export stopped on an error; the raw logs took over from there.
                Console.Error.WriteLine(Strings.MsgLiveExportStopped, finished.LiveError);
                Console.Error.WriteLine(Strings.MsgExportRetry, folder);
                return;
            }

            if (result.Samples == 0)
            {
                try
                {
                    System.IO.Directory.Delete(folder, true);
                    Console.WriteLine(Strings.MsgNoSamplesRemoved);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(Strings.MsgEmptyFolderRemoveFailed, e.Message);
                }
                return;
            }

            PrintExport(result);
            if (options.KeepRaw)
            {
                Console.WriteLine(Strings.MsgRawKept);
            }
        }

        private static void PrintExport(Exporter.Result result)
        {
            Console.WriteLine(Strings.MsgExported, result.Samples, result.Pools);
            Console.WriteLine("  " + result.PerfMonPath + "   " + Strings.MsgExportPerfmonHint);
            Console.WriteLine("  " + result.CpuPath);
            Console.WriteLine("  " + result.InfoPath);
        }

        private static bool RunExport(string folder, string machine, string from, string to)
        {
            try
            {
                Exporter.Result result = Exporter.Export(folder, machine, from, to);
                PrintExport(result);
                return true;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(Strings.MsgExportFailed, e.Message);
                return false;
            }
        }

        // Returns when a key is pressed, Ctrl-C is pressed, or the time is up.
        private static void WaitForKeyOr(TimeSpan time)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.Elapsed < time && !quitRequested)
            {
                try
                {
                    if (Console.KeyAvailable)
                    {
                        Console.ReadKey(true);
                        return;
                    }
                }
                catch (InvalidOperationException)
                {
                    return;
                }
                Thread.Sleep(50);
            }
        }

        private static string FirstLine(string message)
        {
            message = message ?? string.Empty;
            int newline = message.IndexOfAny(new[] { '\r', '\n' });
            return newline >= 0 ? message.Substring(0, newline) : message;
        }

        private static bool OnConsoleCtrl(uint ctrlType)
        {
            quitRequested = true;
            App current = app;
            if (current != null)
            {
                current.RequestQuit();
            }

            // For a window close, Windows ends the process about 5 s after this returns or times out,
            // so wait here for Main to restore the console and close the logs.
            ShutdownDone.WaitOne(TimeSpan.FromSeconds(4.5));
            return true;
        }
    }
}
