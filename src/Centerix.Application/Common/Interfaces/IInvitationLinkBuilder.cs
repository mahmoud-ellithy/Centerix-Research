namespace Centerix.Application.Common.Interfaces;

/// <summary>
/// Builds the absolute URL an invitee follows to accept an invitation. The application base URL is
/// environment-specific configuration — never hardcoded — so development, staging and production
/// can point at their own front ends.
/// </summary>
public interface IInvitationLinkBuilder
{
    /// <summary>
    /// Returns the absolute invitation acceptance link for the given raw token.
    /// </summary>
    Uri BuildAcceptLink(string token);
}
