using System.Collections.Concurrent;
using Centerix.Application.Common.Behaviours;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Invitations.Commands;
using Centerix.Application.Platform.Staff.Commands;
using MediatR;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Centerix.SecurityTests;

// REV-001: secret payloads (passwords, invitation tokens) must NEVER appear in logs.
// These tests cover: UnhandledExceptionBehaviour, PerformanceBehaviour and
// the ToString() override of the two ISensitiveRequest records.
public class Rev001SecretsInLogsTests
{
    private const string Secret = "SuperSecretPassword_DO_NOT_LEAK";
    private const string Token = "invitation-token-DO-NOTE-LEAK";

    [Fact]
    public async Task UnhandledExceptionBehaviour_DoesNotLogRequestPayload()
    {
        var sink = new CapturingLoggerProvider();
        var logger = sink.Get<RegisterFromInvitationCommand>();

        var behaviour = new UnhandledExceptionBehaviour<RegisterFromInvitationCommand, Unit>(
            logger);

        var request = new RegisterFromInvitationCommand(Token, Secret, "Alice", "Smith");

        // MediatR 12's RequestHandlerDelegate<TResponse>(CancellationToken) requires a 1-arg lambda.
        RequestHandlerDelegate<Unit> throwing = _ =>
            throw new InvalidOperationException("boom");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            behaviour.Handle(request, throwing, CancellationToken.None));

        Assert.Equal("boom", ex.Message);

        var messages = string.Join(Environment.NewLine, sink.Entries);
        Assert.Contains(nameof(RegisterFromInvitationCommand), messages);
        Assert.DoesNotContain(Secret, messages);
        Assert.DoesNotContain(Token, messages);
    }

    [Fact]
    public async Task PerformanceBehaviour_DoesNotLogRequestPayload_WhenSlow()
    {
        var sink = new CapturingLoggerProvider();
        var logger = sink.Get<CreatePlatformUserCommand>();

        var behaviour = new PerformanceBehaviour<CreatePlatformUserCommand, Unit>(logger);

        var request = new CreatePlatformUserCommand("alice@test.com", "Alice", Secret);

        // Force a delay > 500 ms without depending on wall-clock arithmetic.
        RequestHandlerDelegate<Unit> slow = async _ =>
        {
            await Task.Delay(550);
            return Unit.Value;
        };

        await behaviour.Handle(request, slow, CancellationToken.None);

        var messages = string.Join(Environment.NewLine, sink.Entries);
        Assert.Contains(nameof(CreatePlatformUserCommand), messages);
        Assert.DoesNotContain(Secret, messages);
    }

    [Fact]
    public void RegisterFromInvitationCommand_ToString_DoesNotLeakSecrets()
    {
        var cmd = new RegisterFromInvitationCommand(Token, Secret, "Alice", "Smith");

        var text = cmd.ToString();

        Assert.NotEqual(
            "Centerix.Application.Platform.Invitations.Commands.RegisterFromInvitationCommand",
            text);
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain("Alice", text);
        Assert.DoesNotContain("Smith", text);
    }

    [Fact]
    public void CreatePlatformUserCommand_ToString_DoesNotLeakSecrets()
    {
        var cmd = new CreatePlatformUserCommand("alice@test.com", "Alice", Secret);

        var text = cmd.ToString();

        Assert.NotEqual(
            "Centerix.Application.Platform.Staff.Commands.CreatePlatformUserCommand",
            text);
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain("alice@test.com", text);
        Assert.DoesNotContain("Alice", text);
    }

    // Minimal test logger provider — captures (level, message) into a thread-safe buffer.
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public IReadOnlyCollection<string> Entries => _entries;

        public ILogger<T> Get<T>()
            => new TypedCapturingLogger<T>(new CapturingLogger(typeof(T).FullName ?? typeof(T).Name, _entries));

        // Suppresses CS0114 / IDE0001: ILoggerProvider contract requires non-generic CreateLogger.
        public ILogger CreateLogger(string categoryName)
            => new CapturingLogger(categoryName, _entries);

        public void Dispose() { }

        // Wraps a non-generic ILogger so it satisfies ILogger<T> via contravariance on the
        // generic parameter; MediatR behaviours take ILogger<TRequest> in their primary ctor.
        private sealed class TypedCapturingLogger<T>(ILogger inner) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
                => inner.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => inner.Log(logLevel, eventId, state, exception, formatter);
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                sink.Enqueue($"[{logLevel}] {category}: {formatter(state, exception)}");
                if (exception is not null)
                {
                    sink.Enqueue(exception.ToString());
                }
            }
        }
    }
}
