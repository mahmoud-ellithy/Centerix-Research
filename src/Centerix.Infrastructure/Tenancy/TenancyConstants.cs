namespace Centerix.Infrastructure.Tenancy;

public static class TenancyConstants
{
    public const string TenantIdName = "tenant";
    public const string FirstName = "Mahmoud";
    public const string LastName = "Ahmed";

    // NEW-1 correction: there is deliberately NO bootstrap/default password generator
    // anywhere in the codebase — no static password, no generated password, no fallback.
    // A bootstrap admin is created ONLY when development seed data is explicitly enabled
    // AND an explicit BootstrapAdmin:TemporaryPassword is supplied via configuration /
    // secret store (see ApplicationDbContextInitialiser); startup fails clearly otherwise.

    public static class Root
    {
        public const string Id = "root";
        public static readonly Guid GuidId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        public const string Name = "Root";
        public const string Email = "admin.root@centerix.com";
    }
}
