namespace Centerix.Infrastructure.Data.ValueGenerators;

using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

/// <summary>
/// Generates a random 8-byte array for rowversion properties when using EF InMemory.
/// SQL Server uses its native rowversion type which is auto-generated at the database level;
/// this generator ensures InMemory tests can create and save entities without the property being null.
/// </summary>
public class RowVersionValueGenerator : ValueGenerator<byte[]>
{
    public override bool GeneratesTemporaryValues => false;

    public override byte[] Next(EntityEntry entry)
    {
        return new byte[8];
    }
}
