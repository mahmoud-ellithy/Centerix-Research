namespace Centerix.Domain.Platform.Staff;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

/// <summary>
/// Platform staff users (SuperAdmin/Sales/Support) — completely separate
/// from tenant Users for security isolation.
/// </summary>
public class PlatformUser : Entity
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = default!;
    public string FullName { get; private set; } = default!;
    public string PasswordHash { get; private set; } = default!;
    public bool Is2FAEnabled { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private readonly List<PlatformUserRole> _userRoles = [];
    public IReadOnlyList<PlatformUserRole> UserRoles => _userRoles.AsReadOnly();

    private PlatformUser() { }

    private PlatformUser(
        Guid id,
        string email,
        string fullName,
        string passwordHash)
    {
        Id = id;
        Email = email;
        FullName = fullName;
        PasswordHash = passwordHash;
        Is2FAEnabled = false;
        IsActive = true;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public static Result<PlatformUser> Create(
        Guid id,
        string email,
        string fullName,
        string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(email))
            return PlatformUserErrors.EmailRequired;

        if (string.IsNullOrWhiteSpace(fullName))
            return PlatformUserErrors.FullNameRequired;

        if (string.IsNullOrWhiteSpace(passwordHash))
            return PlatformUserErrors.PasswordHashRequired;

        return new PlatformUser(id, email.Trim().ToLowerInvariant(), fullName.Trim(), passwordHash);
    }

    public Result<Updated> UpdateFullName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return PlatformUserErrors.FullNameRequired;

        FullName = fullName.Trim();
        return Result.Updated;
    }

    public Result<Updated> Deactivate()
    {
        if (!IsActive)
            return PlatformUserErrors.AlreadyDeactivated;

        IsActive = false;
        return Result.Updated;
    }

    public Result<Updated> Reactivate()
    {
        if (IsActive)
            return PlatformUserErrors.AlreadyActive;

        IsActive = true;
        return Result.Updated;
    }

    public void Enable2FA() => Is2FAEnabled = true;

    public void Disable2FA() => Is2FAEnabled = false;
}
