using System;

namespace HMoeWebCrawler;

internal static class ConsoleLogger
{
    private const string Reset = "\u001b[0m";
    private const string Cyan = "\u001b[36m";
    private const string Green = "\u001b[32m";
    private const string Yellow = "\u001b[33m";
    private const string Red = "\u001b[31m";
    private const string Gray = "\u001b[90m";

    private static readonly object SyncRoot = new();
    private static readonly bool UseColors = !Console.IsOutputRedirected;

    public static void Header(string title)
    {
        lock (SyncRoot)
        {
            var rule = new string('=', Math.Clamp(title.Length + 12, 32, 72));
            Console.WriteLine();
            Console.WriteLine(rule);
            Console.WriteLine($"  {title}");
            Console.WriteLine(rule);
        }
    }

    public static void Info(string message) => Write("INFO ", Cyan, message);

    public static void Success(string message) => Write(" OK  ", Green, message);

    public static void Warning(string message) => Write("WARN ", Yellow, message);

    public static void Error(string message) => Write("ERROR", Red, message);

    public static void Skip(string message) => Write("SKIP ", Gray, message);

    public static void Exception(Exception exception, string? context = null)
    {
        var prefix = string.IsNullOrWhiteSpace(context) ? string.Empty : context + ": ";
        Error($"{prefix}{exception.GetType().Name}: {exception.Message}");
    }

    public static void Prompt(string message) => Write("INPUT", Yellow, message, newline: false);

    private static void Write(string level, string color, string message, bool newline = true)
    {
        lock (SyncRoot)
        {
            var prefix = $"[{DateTime.Now:HH:mm:ss}] [{level}]";
            var formatted = $"{prefix} {message}";
            if (UseColors)
            {
                var coloredLevel = $"{prefix[..^(level.Length + 2)]}{color}[{level}]{Reset}";
                formatted = $"{coloredLevel} {message}";
            }

            if (newline)
                Console.WriteLine(formatted);
            else
                Console.Write(formatted);
        }
    }
}
