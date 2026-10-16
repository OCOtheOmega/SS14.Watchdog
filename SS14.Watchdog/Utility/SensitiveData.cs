using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SS14.Watchdog.Utility;

public static partial class SensitiveData
{
    private static readonly ConcurrentDictionary<string, byte> Secrets = new(StringComparer.Ordinal);

    public static void Register(string? secret)
    {
        if (!string.IsNullOrEmpty(secret))
            Secrets.TryAdd(secret, 0);
    }

    public static string Redact(string text)
    {
        foreach (var secret in Secrets.Keys)
            text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        text = UrlPattern().Replace(text, "[redacted URL]");
        text = AuthorizationPattern().Replace(text, "$1[redacted]");
        return CredentialPattern().Replace(text, "$1[redacted]");
    }

    // Do not retain the original exception or its inner exceptions in logging/notification objects.
    public static Exception SafeException(Exception exception) => new RedactedException(exception);

    private sealed class RedactedException : Exception
    {
        private readonly string? _stack;
        public RedactedException(Exception original) : base($"{original.GetType().Name}: {Redact(original.Message)}",
            original.InnerException == null ? null : SafeException(original.InnerException))
        {
            _stack = original.StackTrace == null ? null : Redact(original.StackTrace);
        }
        public override string? StackTrace => _stack;
    }

    [GeneratedRegex(@"https?://[^\s\""<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"((?:token|password|secret|authorization|api[_-]?key)\s*[=:]\s*)[^\s,;]+", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialPattern();

    [GeneratedRegex(@"(authorization\s*[:=]\s*)(?:Basic|Bearer)\s+[^\s,;]+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationPattern();
}
