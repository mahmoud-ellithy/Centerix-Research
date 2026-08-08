using System.Security.Cryptography;

namespace Centerix.Infrastructure.Tenancy;

public static class TenancyConstants
{
    public const string TenantIdName = "tenant";
    public const string FirstName = "Mahmoud";
    public const string LastName = "Ahmed";

    public static string GenerateTemporaryPassword()
    {
        // Fixed dev password — change to random generation before production deployment.
        return "Admin@123";
    }

    public static class Root
    {
        public const string Id = "root";
        public const string Name = "Root";
        public const string Email = "admin.root@centerix.com";
    }
}