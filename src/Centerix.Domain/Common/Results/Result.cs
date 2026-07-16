namespace Centerix.Domain.Common.Results;

public static class Result
{
    public static Success Success => new();
    public static Created Created => new();
    public static Deleted Deleted => new();
    public static Updated Updated => new();
}

public sealed class Result<TValue> : IResult<TValue>
{
    private readonly TValue? _value;
    private readonly List<Error>? _errors;

    private Result(TValue value)
    {
        _value = value;
        _errors = null;
        IsSuccess = true;
    }

    private Result(Error error)
    {
        _value = default;
        _errors = [error];
        IsSuccess = false;
    }

    private Result(List<Error> errors)
    {
        _value = default;
        _errors = errors;
        IsSuccess = false;
    }

    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot access the value of a failed result.");

    public List<Error>? Errors => _errors;

    public bool IsSuccess { get; }

    public TOut Match<TOut>(Func<TValue, TOut> onSuccess, Func<List<Error>, TOut> onFailure) =>
        IsSuccess ? onSuccess(_value!) : onFailure(_errors!);

    public static implicit operator Result<TValue>(TValue value) => new(value);

    public static implicit operator Result<TValue>(Error error) => new(error);

    public static implicit operator Result<TValue>(List<Error> errors) => new(errors);
}

public readonly record struct Success;
public readonly record struct Created;
public readonly record struct Deleted;
public readonly record struct Updated;
