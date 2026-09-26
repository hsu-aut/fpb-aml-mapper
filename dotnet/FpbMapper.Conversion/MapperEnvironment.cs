namespace FpbMapper.Conversion;

/// <summary>
/// The two things the mapper takes from outside: new GUIDs and the current time.
/// By default random and the clock; a comparison (e.g. the FPB.js port checked
/// against this mapper) sets fixed sources for its scope with <see cref="Use"/>,
/// so every ID and timestamp in the output can be compared exactly.
/// </summary>
public static class MapperEnvironment
{
    private static readonly AsyncLocal<Func<Guid>?> _newGuid = new();
    private static readonly AsyncLocal<Func<DateTime>?> _utcNow = new();

    /// <summary>A new GUID, random unless a scope set a source.</summary>
    public static Guid NewGuid() => _newGuid.Value?.Invoke() ?? Guid.NewGuid();

    /// <summary>The current UTC time, unless a scope set a source.</summary>
    public static DateTime UtcNow => _utcNow.Value?.Invoke() ?? DateTime.UtcNow;

    /// <summary>Use <paramref name="newGuid"/> and <paramref name="utcNow"/> until the result is disposed.</summary>
    public static IDisposable Use(Func<Guid> newGuid, Func<DateTime> utcNow)
    {
        var previous = (_newGuid.Value, _utcNow.Value);
        _newGuid.Value = newGuid;
        _utcNow.Value = utcNow;
        return new Scope(() =>
        {
            _newGuid.Value = previous.Item1;
            _utcNow.Value = previous.Item2;
        });
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        private Action? _restore = restore;

        public void Dispose()
        {
            _restore?.Invoke();
            _restore = null;
        }
    }
}
