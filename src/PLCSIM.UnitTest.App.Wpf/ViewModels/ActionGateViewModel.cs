using PLCSIM.UnitTest.Core.Workflow;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

public sealed record ActionGateViewModel(
    string Action,
    string Availability,
    string Reason)
{
    public static ActionGateViewModel FromGate(ActionGateState gate)
    {
        var reason = gate.Reasons.Count == 0
            ? string.Empty
            : string.Join("; ", gate.Reasons.Select(item => item.Message));

        return new ActionGateViewModel(
            gate.Action.ToString(),
            gate.Availability.ToString(),
            reason);
    }
}
