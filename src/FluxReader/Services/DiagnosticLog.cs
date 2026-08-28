using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace FluxReader.Services;

internal static class DiagnosticLog
{
    private const int RetainedSessionCount = 20;
    private static readonly Lock Gate = new();
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };
    private static readonly string SessionId = Guid.NewGuid().ToString("N");
    private static StreamWriter? _writer;
    private static string? _activeSessionPath;
    private static bool _sessionCompleted;

    public static string? CurrentLogPath { get; private set; }

    public static void Initialize(string dataDirectory)
    {
        lock (Gate)
        {
            if (_writer is not null)
            {
                return;
            }

            try
            {
                var logDirectory = Path.Combine(dataDirectory, "Logs");
                Directory.CreateDirectory(logDirectory);
                PruneOldSessionLogs(logDirectory);

                var process = Process.GetCurrentProcess();
                var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss.fff'Z'");
                CurrentLogPath = Path.Combine(
                    logDirectory,
                    $"FluxReader-{timestamp}-{process.Id}-{SessionId[..8]}.jsonl");
                _writer = new StreamWriter(
                    new FileStream(CurrentLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };

                _activeSessionPath = Path.Combine(logDirectory, "active-session.json");
                JsonElement? previousSession = TryReadPreviousSession(_activeSessionPath);

                WriteCore(
                    "information",
                    "session.started",
                    new
                    {
                        version = GetApplicationVersion(),
                        processId = process.Id,
                        processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                        osDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                        logPath = CurrentLogPath
                    });

                if (previousSession is { } previous)
                {
                    WriteCore(
                        "warning",
                        "session.previous_exit_was_unclean",
                        new { previousSession = previous });
                }

                File.WriteAllText(
                    _activeSessionPath,
                    JsonSerializer.Serialize(
                        new
                        {
                            sessionId = SessionId,
                            startedAtUtc = DateTimeOffset.UtcNow,
                            processId = process.Id,
                            logPath = CurrentLogPath,
                            version = GetApplicationVersion()
                        },
                        SerializerOptions),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch
            {
                _writer?.Dispose();
                _writer = null;
                CurrentLogPath = null;
                _activeSessionPath = null;
            }
        }
    }

    public static void Information(string eventName, object? data = null) =>
        Write("information", eventName, data);

    public static void Warning(string eventName, object? data = null) =>
        Write("warning", eventName, data);

    public static void Error(string eventName, Exception exception, object? data = null) =>
        Write(
            "error",
            eventName,
            new
            {
                context = data,
                exceptionType = exception.GetType().FullName,
                exception.Message,
                hResult = $"0x{exception.HResult:X8}",
                exception = exception.ToString(),
                memory = CaptureMemory()
            });

    public static void MemorySnapshot(string eventName, object? data = null) =>
        Information(eventName, new { context = data, memory = CaptureMemory() });

    public static void CompleteSession(string reason)
    {
        lock (Gate)
        {
            if (_sessionCompleted)
            {
                return;
            }

            _sessionCompleted = true;
            try
            {
                WriteCore("information", "session.completed", new { reason, memory = CaptureMemory() });
                if (_activeSessionPath is not null && File.Exists(_activeSessionPath))
                {
                    File.Delete(_activeSessionPath);
                }
            }
            catch
            {
            }
            finally
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private static void Write(string level, string eventName, object? data)
    {
        lock (Gate)
        {
            try
            {
                WriteCore(level, eventName, data);
            }
            catch
            {
            }
        }
    }

    private static void WriteCore(string level, string eventName, object? data)
    {
        if (_writer is null)
        {
            return;
        }

        _writer.WriteLine(JsonSerializer.Serialize(
            new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                level,
                eventName,
                sessionId = SessionId,
                processId = Environment.ProcessId,
                threadId = Environment.CurrentManagedThreadId,
                data
            },
            SerializerOptions));
    }

    private static object CaptureMemory()
    {
        using var process = Process.GetCurrentProcess();
        return new
        {
            workingSetBytes = process.WorkingSet64,
            privateBytes = process.PrivateMemorySize64,
            managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
            gcGen0 = GC.CollectionCount(0),
            gcGen1 = GC.CollectionCount(1),
            gcGen2 = GC.CollectionCount(2),
            threadCount = process.Threads.Count
        };
    }

    private static JsonElement? TryReadPreviousSession(string activeSessionPath)
    {
        try
        {
            if (!File.Exists(activeSessionPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(activeSessionPath));
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string GetApplicationVersion() =>
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

    private static void PruneOldSessionLogs(string logDirectory)
    {
        try
        {
            foreach (var file in new DirectoryInfo(logDirectory)
                         .EnumerateFiles("FluxReader-*.jsonl")
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(RetainedSessionCount - 1))
            {
                file.Delete();
            }
        }
        catch
        {
        }
    }
}
