using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FactorioLibrary.Models;

namespace FactorioLibrary.Services.Orchestrators;

public interface IContainerOrchestrator
{
    string OrchestratorName { get; }
    
    Task<(bool IsHealthy, string Message)> CheckHealthAsync();
    
    bool IsContainerRunning(int instanceId);

    Task<bool> StartContainerAsync(ServerInstance instance, string hostBaseMountPath, string localDataPath, string savesDir, bool loadLatest, bool generateNewSave);
    
    Task StopContainerAsync(int instanceId, ServerInstance? instance);
    
    Task RemoveContainerAsync(int instanceId);
    
    Task<ServerStats> GetContainerStatsAsync(ServerInstance instance);
    
    Task<List<string>> GetOfflineLogsAsync(int instanceId, int tail = 200);
    
    IAsyncEnumerable<string> GetLiveLogsAsync(int instanceId, CancellationToken cancellationToken);
    
    Task<string?> GetContainerIpAddressAsync(int instanceId);
    
    Task<(bool Success, string Logs)> RunTemporarySyncContainerAsync(int instanceId, string saveName, string imageTag, string instanceHostPath);
}
