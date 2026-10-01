using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace OmronUpsd;

/// <summary>%ProgramData%\omronupsd\logs への日付別ログ出力。書き込みは専用スレッドで実施。</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetainDays = 30;
    private readonly string _dir = Path.Combine(AppInfo.DataDirectory, "logs");
    private readonly BlockingCollection<string> _queue = new(4096);
    private readonly Thread _writer;

    public FileLoggerProvider()
    {
        Directory.CreateDirectory(_dir);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "FileLogger" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

    // キュー満杯時は破棄 (ロガーで呼び出し元を止めない)
    private void Enqueue(string line) => _queue.TryAdd(line);

    private void WriteLoop()
    {
        string? currentDate = null;
        StreamWriter? w = null;
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            var date = line[..10];
            if (date != currentDate)
            {
                w?.Dispose();
                currentDate = date;
                w = new StreamWriter(new FileStream(Path.Combine(_dir, $"omronupsd-{date}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
                Purge();
            }
            w!.WriteLine(line);
            if (_queue.Count == 0)
                w.Flush();
        }
        w?.Dispose();
    }

    private void Purge()
    {
        var limit = DateTime.Now.AddDays(-RetainDays);
        foreach (var f in Directory.EnumerateFiles(_dir, "omronupsd-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(f) < limit)
                    File.Delete(f);
            }
            catch (IOException)
            {
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(3));
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{Level(logLevel)}] {category}: {formatter(state, exception)}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            provider.Enqueue(line);
        }

        private static string Level(LogLevel l) => l switch
        {
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => l.ToString(),
        };
    }
}
