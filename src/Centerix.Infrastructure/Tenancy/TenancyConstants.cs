using System.Security.Cryptography;

namespace Centerix.Infrastructure.Tenancy;

public static class TenancyConstants
{
    public const string TenantIdName = "tenant";
    public const string FirstName = "Mahmoud";
    public const string LastName = "Ahmed";

    public static string GenerateTemporaryPassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789!@#$";
        var random = RandomNumberGenerator.Create();
        var bytes = new byte[16];
        random.GetBytes(bytes);
        var result = new char[16];
        for (int i = 0; i < 16; i++)
            result[i] = chars[bytes[i] % chars.Length];
        return new string(result);
    }

    public static class Root
    {
        public const string Id = "root";
        public const string Name = "Root";
        public const string Email = "admin.root@centerix.com";
    }
}