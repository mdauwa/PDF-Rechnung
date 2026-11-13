using Microsoft.Extensions.Options;

namespace PdfRechnung.Settings;

/// <summary>Feste Optionen für Testaufrufe und Tests.</summary>
public sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
