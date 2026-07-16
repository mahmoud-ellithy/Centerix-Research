namespace Centerix.Application.Common.Interfaces;

public interface ICurrentUser
{
    string UserId { get; }
    string UserName { get; }
    bool IsAuthenticated { get; }
    bool IsPlatformAdmin { get; }
    IEnumerable<string> Roles { get; }
}