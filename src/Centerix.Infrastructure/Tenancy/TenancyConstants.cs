using System.Security.Cryptography;

namespace Centerix.Infrastructure.Tenancy;

public static class TenancyConstants
{
    public const string TenantIdName = "tenant";
    public const string FirstName = "Mahmoud";
    public const string LastName = "Ahmed";

    /// <summary>
    /// NEW-1 bootstrap hardening: generates a fresh cryptographically random temporary
    /// password on every call. There is deliberately NO static/default password anywhere
    /// in the codebase. The value satisfies Identity password rules (upper, lower, digit,
    /// symbol, length 16, distinct chars) and must be rotated via
    /// POST /api/auth/change-password (which clears password.change_required).
    /// </summary>
    public static string GenerateTemporaryPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*-_=+";
        const string all = upper + lower + digits + symbols;

        var chars = new char[16];
        chars[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        chars[3] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];
        for (var i = 4; i < chars.Length; i++)
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];

        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }

    public static class Root
    {
        public const string Id = "root";
        public static readonly Guid GuidId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        public const string Name = "Root";
        public const string Email = "admin.root@centerix.com";
    }
}