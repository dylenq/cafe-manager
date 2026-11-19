using System.ComponentModel.DataAnnotations;

namespace Cafe.Server.Stations;

/// <summary>Why an operation failed, so callers never have to inspect message text.</summary>
public enum FailureKind
{
    None,

    /// <summary>The input or a business rule was rejected; the record still exists and the user can correct it.</summary>
    Validation,

    /// <summary>The record being edited or deleted no longer exists.</summary>
    NotFound,
}

/// <summary>Outcome of a CRUD operation: success, or a friendly error plus optional per-field messages.</summary>
public class ServiceResult
{
    private static readonly IReadOnlyDictionary<string, string> NoFieldErrors = new Dictionary<string, string>();

    public bool Success { get; protected init; }

    public FailureKind Kind { get; protected init; }

    public string? Error { get; protected init; }

    public IReadOnlyDictionary<string, string> FieldErrors { get; protected init; } = NoFieldErrors;

    public static ServiceResult Ok() => new() { Success = true };

    public static ServiceResult Fail(string error, IReadOnlyDictionary<string, string>? fieldErrors = null) =>
        new() { Kind = FailureKind.Validation, Error = error, FieldErrors = fieldErrors ?? NoFieldErrors };

    /// <summary>The target record is gone (deleted by someone else).</summary>
    public static ServiceResult NotFound(string error) =>
        new() { Kind = FailureKind.NotFound, Error = error };
}

public sealed class ServiceResult<T> : ServiceResult
{
    public T? Value { get; private init; }

    public static ServiceResult<T> Ok(T value) => new() { Success = true, Value = value };

    public static new ServiceResult<T> Fail(string error, IReadOnlyDictionary<string, string>? fieldErrors = null) =>
        new() { Kind = FailureKind.Validation, Error = error, FieldErrors = fieldErrors ?? new Dictionary<string, string>() };

    public static new ServiceResult<T> NotFound(string error) =>
        new() { Kind = FailureKind.NotFound, Error = error };
}

internal static class InputValidation
{
    /// <summary>Runs DataAnnotations on every property; returns null when valid, else member name to message.</summary>
    public static Dictionary<string, string>? Validate(object input)
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(input, new ValidationContext(input), results, validateAllProperties: true))
        {
            return null;
        }

        var errors = new Dictionary<string, string>();
        foreach (var result in results)
        {
            var key = result.MemberNames.FirstOrDefault() ?? string.Empty;
            errors.TryAdd(key, result.ErrorMessage ?? "Invalid value.");
        }

        return errors;
    }

    public static string Summarise(Dictionary<string, string> errors) => string.Join(" ", errors.Values);

    /// <summary>Trims; whitespace-only or empty becomes null.</summary>
    public static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public static bool IsUniqueViolation(Microsoft.EntityFrameworkCore.DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 } sql
        && sql.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);
}
