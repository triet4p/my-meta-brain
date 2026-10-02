using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

internal sealed class InMemoryGrantStore : IGrantStore
{
    private readonly object _gate = new();
    private GrantPolicyState _state = GrantPolicyState.Empty;

    public GrantPolicyState Load()
    {
        lock (_gate)
        {
            return Copy(_state);
        }
    }

    public void Save(GrantPolicyState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            _state = Copy(state);
        }
    }

    private static GrantPolicyState Copy(GrantPolicyState state) =>
        state with { Grants = (AccessGrant[])state.Grants.Clone() };
}
