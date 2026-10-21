namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;

// Managed test boundaries. Only the official interop types perform COM calls.
internal interface IXaeSystemTransfer
{
    void ActivateConfiguration();
    object LookupTreeItem(string path);
    void StartRestartTwinCAT();
    void SetTargetNetId(string netId);
    string GetTargetNetId();
}
internal interface IXaeProjectRoot { object NestedProject { get; } }
internal interface IXaePlcOnline
{
    void Login(int flags);
    void Logoff();
    bool IsLoggedIn { get; }
}
