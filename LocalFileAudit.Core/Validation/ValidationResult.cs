namespace LocalFileAudit.Core.Validation;

/// <summary>
/// Result of a validation check.
/// </summary>
public sealed class ValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; } = new();

    public static ValidationResult Success() => new();

    public static ValidationResult Failure(string error)
    {
        var result = new ValidationResult();
        result.Errors.Add(error);
        return result;
    }

    public static ValidationResult Failure(IEnumerable<string> errors)
    {
        var result = new ValidationResult();
        result.Errors.AddRange(errors);
        return result;
    }
}
