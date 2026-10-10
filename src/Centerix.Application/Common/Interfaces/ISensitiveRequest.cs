namespace Centerix.Application.Common.Interfaces;

// REV-001: marker interface for requests whose payloads MUST NEVER be logged.
// Any future command/query carrying credentials, tokens, or other secrets
// should implement this so logging pipelines can redact defensively.
public interface ISensitiveRequest
{
}
