namespace LeagueClanker.Core;

/// <summary>
/// A minimal log. Core code reports failures it recovers from (op.gg down, the client refusing a page); the app points
/// <see cref="Sink"/> at a file so problems in real games can be traced afterwards.
/// </summary>
public static class Log
{
    public static Action<string>? Sink { get; set; }

    public static void Write(string message) => Sink?.Invoke(message);

    public static void Error(string what, Exception ex) => Sink?.Invoke($"{what}: {ex.GetType().Name}: {ex.Message}");
}
