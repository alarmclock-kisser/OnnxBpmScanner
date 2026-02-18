using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

namespace OnnxBpmScanner.Core
{
    public static class StaticLogger
    {
        public static readonly ConcurrentDictionary<DateTime, string> LogEntries = new();
        public static readonly BindingList<string> LogEntriesBindingList = new();

        public static event Action<string>? LogAdded;

        // UI synchronization context (set from the UI at startup)
        private static SynchronizationContext? UiContext;

        public static void SetUiContext(SynchronizationContext context)
        {
            UiContext = context;
        }


        public static void Log(string message)
        {
            DateTime timestamp = DateTime.Now;
            string logEntry = $"[{timestamp:HH:mm:ss}] {message}";
            LogEntries[timestamp] = logEntry;

            if (UiContext != null)
            {
                UiContext.Post(_ => LogEntriesBindingList.Add(logEntry), null);
            }
            else
            {
                // Fallback: add on current thread
                lock (LogEntriesBindingList)
                {
                    LogEntriesBindingList.Add(logEntry);
                }
            }

            LogAdded?.Invoke(logEntry);

            Console.WriteLine(logEntry);
        }

        public static void Log(Exception ex)
        {
            Log($"Exception: {ex.Message}\nStack Trace: {ex.StackTrace}");
        }

        public static async Task LogAsync(string message)
        {
            await Task.Run(() => Log(message));
        }

        public static async Task LogAsync(Exception ex)
        {
            await Task.Run(() => Log(ex));
        }



        public static void ClearLogs()
        {
            LogEntries.Clear();
            if (UiContext != null)
            {
                UiContext.Post(_ => LogEntriesBindingList.Clear(), null);
            }
            else
            {
                lock (LogEntriesBindingList)
                {
                    LogEntriesBindingList.Clear();
                }
            }
        }



    }
}