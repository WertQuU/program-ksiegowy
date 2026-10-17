namespace PsaKsiegi.Domain.Common;

/// <summary>Naruszenie reguły biznesowej / księgowej. Komunikat jest przeznaczony dla użytkownika (po polsku).</summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
    public DomainException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Brak uprawnień do wykonania operacji.</summary>
public sealed class AccessDeniedException : DomainException
{
    public AccessDeniedException(string message) : base(message) { }
}

/// <summary>Wynik walidacji z listą błędów i ostrzeżeń.</summary>
public sealed class ValidationResult
{
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();

    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<string> Warnings => _warnings;
    public bool IsValid => _errors.Count == 0;

    public ValidationResult Error(string message) { _errors.Add(message); return this; }
    public ValidationResult Warning(string message) { _warnings.Add(message); return this; }

    public void Merge(ValidationResult other)
    {
        _errors.AddRange(other._errors);
        _warnings.AddRange(other._warnings);
    }

    public void ThrowIfInvalid()
    {
        if (!IsValid) throw new DomainException(string.Join(Environment.NewLine, _errors));
    }

    public override string ToString() =>
        string.Join(Environment.NewLine, _errors.Select(e => "BŁĄD: " + e).Concat(_warnings.Select(w => "OSTRZEŻENIE: " + w)));
}
