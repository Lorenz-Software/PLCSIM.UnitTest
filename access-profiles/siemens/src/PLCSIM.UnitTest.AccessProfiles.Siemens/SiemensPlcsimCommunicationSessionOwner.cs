using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.Operations;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed class SiemensPlcsimCommunicationSessionOwner
{
    private readonly HashSet<SiemensPlcsimCommunicationAdapterKind> adapters = [];
    private readonly ILogger<SiemensPlcsimCommunicationSessionOwner> logger;
    private readonly object sync = new();
    private IPlcsimUnitTestCommunicationSession? session;
    private bool closeInProgress;
    private bool terminal;

    public SiemensPlcsimCommunicationSessionOwner(
        ILogger<SiemensPlcsimCommunicationSessionOwner>? logger = null)
    {
        this.logger = logger ?? NullLogger<SiemensPlcsimCommunicationSessionOwner>.Instance;
    }

    public void Attach(
        SiemensPlcsimCommunicationAdapterKind adapter,
        IPlcsimUnitTestCommunicationSession communicationSession)
    {
        ArgumentNullException.ThrowIfNull(communicationSession);

        lock (sync)
        {
            if (closeInProgress)
            {
                throw new InvalidOperationException(
                    "A Siemens PLCSIM UnitTest communication session is still closing.");
            }

            if (session is not null && !ReferenceEquals(session, communicationSession))
            {
                throw new InvalidOperationException(
                    "A different Siemens PLCSIM UnitTest communication session is already active.");
            }

            session ??= communicationSession;
            adapters.Add(adapter);
        }
    }

    public bool IsUsable(IPlcsimUnitTestCommunicationSession communicationSession)
    {
        ArgumentNullException.ThrowIfNull(communicationSession);

        lock (sync)
        {
            return ReferenceEquals(session, communicationSession) && !terminal;
        }
    }

    public void MarkTerminal(IPlcsimUnitTestCommunicationSession communicationSession)
    {
        ArgumentNullException.ThrowIfNull(communicationSession);

        lock (sync)
        {
            if (ReferenceEquals(session, communicationSession))
            {
                terminal = true;
            }
        }
    }

    public async Task<OperationResult> DetachAsync(
        SiemensPlcsimCommunicationAdapterKind adapter,
        IPlcsimUnitTestCommunicationSession communicationSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(communicationSession);

        IPlcsimUnitTestCommunicationSession? closingSession = null;
        lock (sync)
        {
            if (!ReferenceEquals(session, communicationSession))
            {
                return OperationResult.Success();
            }

            adapters.Remove(adapter);
            if (adapters.Count == 0)
            {
                closingSession = session;
                session = null;
                terminal = false;
                closeInProgress = true;
            }
        }

        if (closingSession is null)
        {
            return OperationResult.Success();
        }

        try
        {
            await closingSession.CloseAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Siemens PLCSIM UnitTest communication session closed by shared owner. CommunicationSessionId={CommunicationSessionId}",
                closingSession.Communication.SessionId);
            return OperationResult.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = $"Siemens PLCSIM UnitTest communication session close failed: {ex.GetBaseException().Message}";
            logger.LogError(
                ex,
                "Siemens PLCSIM UnitTest communication session close failed in shared owner. CommunicationSessionId={CommunicationSessionId}",
                closingSession.Communication.SessionId);
            return OperationResult.Failure(message);
        }
        finally
        {
            lock (sync)
            {
                closeInProgress = false;
            }
        }
    }
}

internal enum SiemensPlcsimCommunicationAdapterKind
{
    Lifecycle,
    EventStream
}
