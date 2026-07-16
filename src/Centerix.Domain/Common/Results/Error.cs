namespace Centerix.Domain.Common.Results;

public readonly record struct Error
{
    private Error(string code, string description, ErrorKind type)
    {
        Code = code;
        Description = description;
        Type = type;
    }

    public string Code { get; }
    public string Description { get; }
    public ErrorKind Type { get; }

    public static Error Failure(string code, string description) =>
        new(code, description, ErrorKind.Failure);

    public static Error Unexpected(string code, string description) =>
        new(code, description, ErrorKind.Unexpected);

    public static Error Validation(string code, string description) =>
        new(code, description, ErrorKind.Validation);

    public static Error Conflict(string code, string description) =>
        new(code, description, ErrorKind.Conflict);

    public static Error NotFound(string code, string description) =>
        new(code, description, ErrorKind.NotFound);

    public static Error Unauthorized(string code, string description) =>
        new(code, description, ErrorKind.Unauthorized);

    public static Error Forbidden(string code, string description) =>
        new(code, description, ErrorKind.Forbidden);
}
