namespace Centerix.Domain.Students.Branches;

using System.Text.RegularExpressions;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

public class Branch : SoftDeletableEntity<Guid>
{
    public string Name { get; private set; } = default!;
    public string? Address { get; private set; }
    public string? Phone { get; private set; }
    public Guid? ManagerId { get; private set; }
    public bool IsActive { get; private set; }

    private Branch() { }

    private Branch(Guid id, string name, string? address, string? phone, Guid? managerId, bool isActive)
        : base(id)
    {
        Name = name;
        Address = address;
        Phone = phone;
        ManagerId = managerId;
        IsActive = isActive;
    }

    public static Result<Branch> Create(
        Guid id,
        string name,
        string? address = null,
        string? phone = null,
        Guid? managerId = null,
        bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BranchErrors.NameRequired;

        if (!string.IsNullOrWhiteSpace(phone) && !Regex.IsMatch(phone.Trim(), @"^\+?\d{7,15}$"))
            return BranchErrors.InvalidPhone;

        return new Branch(
            id,
            name.Trim(),
            string.IsNullOrWhiteSpace(address) ? null : address.Trim(),
            string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
            managerId,
            isActive);
    }

    public Result<Updated> Update(string name, string? address, string? phone, Guid? managerId)
    {
        if (IsDeleted())
            return BranchErrors.AlreadyDeleted;

        if (string.IsNullOrWhiteSpace(name))
            return BranchErrors.NameRequired;

        if (!string.IsNullOrWhiteSpace(phone) && !Regex.IsMatch(phone.Trim(), @"^\+?\d{7,15}$"))
            return BranchErrors.InvalidPhone;

        Name = name.Trim();
        Address = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        ManagerId = managerId;

        return Result.Updated;
    }

    public Result<Updated> Activate()
    {
        if (IsDeleted())
            return BranchErrors.AlreadyDeleted;

        IsActive = true;
        return Result.Updated;
    }

    public Result<Updated> Deactivate()
    {
        if (IsDeleted())
            return BranchErrors.AlreadyDeleted;

        IsActive = false;
        return Result.Updated;
    }

    public Result<Updated> SoftDelete()
    {
        if (IsDeleted())
            return BranchErrors.AlreadyDeleted;

        DeletedAtUtc = DateTimeOffset.UtcNow;
        // DeletedBy is stamped by AuditableEntityInterceptor from the current user name.
        IsActive = false;

        return Result.Updated;
    }
}
