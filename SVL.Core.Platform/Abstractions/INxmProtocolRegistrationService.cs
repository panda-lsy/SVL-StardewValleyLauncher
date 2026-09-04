namespace SVL.Core.Platform.Abstractions;

public interface INxmProtocolRegistrationService
{
    NxmProtocolRegistrationResult GetStatus();

    NxmProtocolRegistrationResult TryRegister(string launcherExecutablePath);
}

public sealed class NxmProtocolRegistrationResult
{
    public bool IsSuccess { get; init; }

    public bool IsSupported { get; init; }

    public bool IsRegistered { get; init; }

    public string Message { get; init; } = string.Empty;
}