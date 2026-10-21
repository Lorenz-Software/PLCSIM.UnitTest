namespace PLCSIM.UnitTest.Common.Identifiers;

/// <summary>
/// Identifies an application session.
/// </summary>
public readonly record struct ApplicationSessionId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
}

/// <summary>
/// Identifies one workflow run request.
/// </summary>
public readonly record struct WorkflowRunId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
}

/// <summary>
/// Identifies one workflow or service operation.
/// </summary>
public readonly record struct OperationId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
}

/// <summary>
/// Identifies one observed UnitTest run epoch.
/// </summary>
public readonly record struct RunEpochId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
}

/// <summary>
/// Identifies a node in the neutral UnitTest run hierarchy.
/// </summary>
public readonly record struct UnitTestNodeId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
}

/// <summary>
/// Identifies a prepared PLC project context.
/// </summary>
public readonly record struct ProjectContextId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
}

/// <summary>
/// Identifies a prepared PLC runtime context.
/// </summary>
public readonly record struct RuntimeContextId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
}

/// <summary>
/// Identifies a PLC project target.
/// </summary>
public readonly record struct ProjectTargetId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
}

/// <summary>
/// Identifies a PLC runtime target.
/// </summary>
public readonly record struct RuntimeTargetId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
}

/// <summary>
/// Identifies an event-stream observation context.
/// </summary>
public readonly record struct EventStreamContextId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
}

/// <summary>
/// Identifies a lifecycle observation.
/// </summary>
public readonly record struct LifecycleObservationId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
}

/// <summary>
/// Identifies an evidence snapshot.
/// </summary>
public readonly record struct EvidenceSnapshotId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
}

/// <summary>
/// Identifies one immutable evidence-analysis package.
/// </summary>
public readonly record struct EvidenceAnalysisPackageId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
}

/// <summary>
/// Identifies one deterministic evidence-analysis incident.
/// </summary>
public readonly record struct EvidenceIncidentId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
}

/// <summary>
/// Identifies an output request.
/// </summary>
public readonly record struct OutputRequestId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
}
