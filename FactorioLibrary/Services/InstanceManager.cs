using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FactorioLibrary.Models;
using Microsoft.Extensions.Configuration;

namespace FactorioLibrary.Services;

public class InstanceManager
{
    private readonly DockerClient _dockerClient;
    private readonly string _hostBaseMountPath;
    
    // Maps instance ID to Docker Container ID
    private readonly ConcurrentDictionary<int, string> _runningContainers = new();

    public InstanceManager(IConfiguration configuration)
    {
        // Use named pipes on Windows, unix socket on Linux/Unraid
        var dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new Uri("npipe://./pipe/docker_engine")
            : new Uri("unix:///var/run/docker.sock");
            
        _dockerClient = new DockerClientConfiguration(dockerUri).CreateClient();
        
        // This is the path ON THE HOST OS where we store all Factorio data
        _hostBaseMountPath = configuration.GetValue<string>("HOST_BASE_MOUNT_PATH") 
            ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "C:\\FactorioServers" : "/mnt/user/appdata/factorio_manager/servers");
    }

    public async Task<bool> StartInstanceAsync(ServerInstance instance)
    {
        if (_runningContainers.ContainsKey(instance.Id)) 
            return false; // Already tracked as running

        string containerName = $"factorio_server_{instance.Id}";
        string imageTag = string.IsNullOrWhiteSpace(instance.AssignedVersion) ? "latest" : instance.AssignedVersion;
        string image = $"factoriotools/factorio:{imageTag}";

        try
        {
            // 1. Ensure the image is pulled
            await _dockerClient.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = image }, 
                null, 
                new Progress<JSONMessage>());

            // 2. Remove existing container with the same name if it exists (but is stopped)
            var existingContainers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters { All = true });
                
            foreach (var c in existingContainers)
            {
                if (c.Names.Contains($"/{containerName}"))
                {
                    if (c.State != "running")
                    {
                        await _dockerClient.Containers.RemoveContainerAsync(c.ID, new ContainerRemoveParameters { Force = true });
                    }
                    else
                    {
                        // Already actually running in Docker
                        _runningContainers.TryAdd(instance.Id, c.ID);
                        return true;
                    }
                }
            }

            // 3. Create host directory path for this specific instance
            string instanceHostPath = $"{_hostBaseMountPath.TrimEnd('/', '\\')}/{instance.Id}";
            
            // Note: Docker will automatically create the host directory if it doesn't exist when the volume is mounted

            // 4. Create the container
            var response = await _dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = image,
                Name = containerName,
                HostConfig = new HostConfig
                {
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        { "34197/udp", new List<PortBinding> { new PortBinding { HostPort = instance.Port.ToString() } } },
                        { "27015/tcp", new List<PortBinding> { new PortBinding { HostPort = instance.RconPort.ToString() } } }
                    },
                    Binds = new List<string>
                    {
                        $"{instanceHostPath}:/factorio"
                    }
                },
                Env = new List<string>
                {
                    $"RCON_PASSWORD={instance.RconPassword}"
                }
            });

            // 5. Start the container
            bool started = await _dockerClient.Containers.StartContainerAsync(response.ID, null);
            
            if (started)
            {
                _runningContainers.TryAdd(instance.Id, response.ID);
                return true;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting container: {ex.Message}");
            return false;
        }
    }

    public async Task StopInstanceAsync(int instanceId)
    {
        if (_runningContainers.TryGetValue(instanceId, out string? containerId))
        {
            try
            {
                await _dockerClient.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 10 });
            }
            catch
            {
                // Ignore errors if container already stopped or removed
            }
            finally
            {
                _runningContainers.TryRemove(instanceId, out _);
            }
        }
    }

    public bool IsRunning(int instanceId)
    {
        // Simple in-memory check for UI responsiveness. 
        // A more robust implementation would poll the Docker API, but this is fine for now.
        return _runningContainers.ContainsKey(instanceId);
    }
}
