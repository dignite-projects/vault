using Volo.Abp.Modularity;

namespace Dignite.Vault.Index;

/// <summary>
/// Registers <see cref="AgentFramework.VaultIndexTextSearch"/>. Depend on it next to the Index application module
/// (Index in-process) or <c>VaultIndexHttpApiClientModule</c> (Index behind its REST API).
/// </summary>
[DependsOn(typeof(VaultIndexApplicationContractsModule))]
public class VaultIndexAgentFrameworkModule : AbpModule
{
}
