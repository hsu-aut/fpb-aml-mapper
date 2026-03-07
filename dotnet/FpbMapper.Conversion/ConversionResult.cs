namespace FpbMapper.Conversion;

/// <summary>
/// Carries the conversion output together with any non-fatal warnings.
/// </summary>
public class ConversionResult<T>
{
    public T Value { get; }
    public List<string> Warnings { get; } = new();

    public ConversionResult(T value) => Value = value;
    public ConversionResult(T value, List<string> warnings)
    {
        Value = value;
        Warnings = warnings;
    }

    public void Warn(string message) => Warnings.Add(message);
}
