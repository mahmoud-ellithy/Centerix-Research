namespace Centerix.Domain.Common;

using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations.Schema;

public abstract class Entity
{
    private readonly List<DomainEvent> domainEvents = [];

    [NotMapped]
    public IReadOnlyCollection<DomainEvent> DomainEvents => domainEvents.AsReadOnly();

    protected Entity()
    { }

    public void AddDomainEvent(DomainEvent domainEvent)
    {
        this.domainEvents.Add(domainEvent);
    }

    public void RemoveDomainEvent(DomainEvent domainEvent)
    {
        this.domainEvents.Remove(domainEvent);
    }

    public void ClearDomainEvents()
    {
        this.domainEvents.Clear();
    }
}
