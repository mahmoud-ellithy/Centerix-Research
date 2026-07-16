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

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is AuditableEntity auditableEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    auditableEntity.CreatedAtUtc = this.dateTime.GetUtcNow();
                    auditableEntity.CreatedBy = currentUser.IsAuthenticated ? currentUser.UserName : "System";
                }

                if (entry.State == EntityState.Added || entry.State == EntityState.Modified || this.HasChangedOwnedEntities(entry))
                {
                    auditableEntity.LastModifiedUtc = this.dateTime.GetUtcNow();
                    auditableEntity.LastModifiedBy = currentUser.IsAuthenticated ? currentUser.UserName : "System";
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