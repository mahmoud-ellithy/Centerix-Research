namespace Centerix.Infrastructure.Data.Interceptors;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

public class AuditableEntityInterceptor(TimeProvider dateTime, ICurrentUser currentUser) : SaveChangesInterceptor
{
    private readonly TimeProvider dateTime = dateTime;
    private readonly ICurrentUser currentUser = currentUser;

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        this.UpdateEntities(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        this.UpdateEntities(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void UpdateEntities(DbContext? context)
    {
        if (context == null)
        {
            return;
        }

        var user = currentUser.IsAuthenticated ? currentUser.UserName : "System";
        var now = this.dateTime.GetUtcNow();

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is AuditableEntity auditableEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    auditableEntity.CreatedAtUtc = now;
                    auditableEntity.CreatedBy = user;
                }

                if (entry.State == EntityState.Added || entry.State == EntityState.Modified || this.HasChangedOwnedEntities(entry))
                {
                    auditableEntity.LastModifiedUtc = now;
                    auditableEntity.LastModifiedBy = user;
                }

                // When the entity is being soft-deleted (DeletedAtUtc transitions from null to a value)
                // EF marks the entry as Modified. Stamp DeletedBy so audit captures the actor.
                if (entry.State == EntityState.Modified
                    && auditableEntity.DeletedAtUtc.HasValue
                    && entry.Property(nameof(AuditableEntity.DeletedBy)).IsModified)
                {
                    auditableEntity.DeletedBy = user;
                }
            }
        }
    }

    private bool HasChangedOwnedEntities(EntityEntry entry) =>
        entry.References.Any(r =>
            r.TargetEntry != null &&
            r.TargetEntry.Metadata.IsOwned() &&
            (r.TargetEntry.State == EntityState.Added || r.TargetEntry.State == EntityState.Modified));
}
