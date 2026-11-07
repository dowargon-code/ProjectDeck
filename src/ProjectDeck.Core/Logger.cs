using System.Text;

namespace ProjectDeck.Core;

/// <summary>日別の動作ログ（logs\yyyyMMdd.log）。書き込みに失敗しても処理は止めない</summary>
public static class Logger
{
    private static readonly object Gate = new();
    private static string? _directory;
    private static string _source = "app";

    /// <summary>ログの置き場所と、どのプログラムが書いたか（app / deckctl）を設定する</summary>
    public static void Init(string directory, string source)
    {
        _directory = directory;
        _source = source;
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        if (_directory == null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(_directory);
                var file = Path.Combine(_directory, $"{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {_source}: {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
