using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BikeSync.App.Services;

/// <summary>A small append-only file logger for diagnostics; no activity payloads are logged.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private bool _disabled;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _disabled = true;
            System.Diagnostics.Debug.WriteLine("BikeSync file logging disabled: " + ex.Message);
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        lock (_gate)
        {
            if (_disabled)
            {
                return;
            }

            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (_writer is null || _writerDate != today)
                {
                    _writer?.Dispose();
                    _writerDate = today;
                    var path = Path.Combine(_directory, $"app-{today:yyyyMMdd}.log");
                    _writer = new StreamWriter(
                        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                    {
                        AutoFlush = true,
                    };
                }

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {category}: {message}";
                _writer.WriteLine(line);
                if (exception is not null)
                {
                    _writer.WriteLine(exception);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the application down. Fall back to the debugger
                // and stop trying to write the file for this process.
                _disabled = true;
                _writer?.Dispose();
                _writer = null;
                System.Diagnostics.Debug.WriteLine("BikeSync file logging disabled: " + ex.Message);
            }
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            _provider.Write(_category, logLevel, formatter(state, exception), exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}
