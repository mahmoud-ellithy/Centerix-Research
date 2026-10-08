namespace Centerix.Infrastructure.Tenancy;

public static class TenancyConstants
{
    public const string TenantIdName = "tenant";
    public const string FirstName = "Mahmoud";
    public const string LastName = "Ahmed";

    // Bootstrap administrators:
    //  - Development/sample-data path (BootstrapAdmin:TemporaryPassword +
    //    DatabaseInitialization:SeedDevelopmentData): unchanged, never used in production.
    //  - Production first-PlatformAdmin path (PlatformAdminBootstrap:Enabled/Email/
    //    TemporaryPassword): independent of SeedDevelopmentData; see
    //    PlatformAdminBootstrapOptions and docs/PLATFORM-ADMIN-BOOTSTRAP.md.
    // There is deliberately NO bootstrap/default password generator anywhere in the
    // codebase — no static password, no generated password, no fallback.

    public static class Root
    {
        public const string Id = "root";
        public static readonly Guid GuidId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        public const string Name = "Root";
        public const string Email = "admin.root@centerix.com";
    }
}
