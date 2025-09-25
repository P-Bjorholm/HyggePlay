using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HyggePlay.Services
{
    public static class LogService
    {
        private static readonly SemaphoreSlim _semaphore = new(1, 1);
        private static readonly string _logFilePath;

        static LogService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolder = Path.Combine(appData, "HyggePlay");
            Directory.CreateDirectory(appFolder);
            _logFilePath = Path.Combine(appFolder, "hyggeplay.log");
        }

        public static async Task LogInfoAsync(string message, IDictionary<string, string>? metadata = null)
        {
            await WriteAsync("INFO", message, metadata, null);
        }

        public static async Task LogErrorAsync(string message, Exception exception, IDictionary<string, string>? metadata = null)
        {
            await WriteAsync("ERROR", message, metadata, exception);
        }

        private static async Task WriteAsync(string level, string message, IDictionary<string, string>? metadata, Exception? exception)
        {
            StringBuilder builder = new();
            builder.Append('[')
                   .Append(DateTimeOffset.UtcNow.ToString("o"))
                   .Append("] ")
                   .Append(level)
                   .Append(" - ")
                   .Append(message);

            if (metadata != null && metadata.Count > 0)
            {
                builder.Append(" | ");
                foreach ((string key, string value) in metadata)
                {
                    builder.Append(key)
                           .Append('=')
                           .Append(value)
                           .Append(';');
                }
            }

            if (exception != null)
            {
                builder.AppendLine()
                       .AppendLine(exception.ToString());
            }

            builder.AppendLine();

            await _semaphore.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(_logFilePath, builder.ToString());
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public static string GetLogFilePath() => _logFilePath;
    }
}
