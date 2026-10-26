using System.Reflection;

namespace MetaBrain.OwnerUi.Setup;

internal static class SetupIdentity
{
    internal static string ProductVersion => typeof(SetupIdentity).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException("The setup product version is missing.");
}
