# Factorio Server Controller

A lightweight, powerful, and elegant web-based control panel for managing containerized Factorio dedicated servers. Built with ASP.NET Core Blazor, this tool allows you to easily manage multiple Factorio instances, mods, saves, configurations, and users through an easy to use Web Interface.

## 🚀 Features

* **Instance Management:** Spin up and manage multiple Factorio servers, each running in their own isolated Docker container.
* **Save Management:** 
  * Auto-loads the newest save or lets you manually set an "Active Save" to lock it in.
* **Mod Management:**
  * Upload mods or sync your server's mods to match any uploaded save file.
  * Easily toggle mods on/off via the `mod-list.json`.
  * Download the entire server modpack as a single `.zip` file for players to use.
* **Live RCON Console:** View real-time server logs and interact with the game via RCON. Includes quick-presets for common commands (Time, Players, Save).
* **Rich Configuration:** Edit your `server-settings.json` (server name, description, passwords) using a built-in interactive JSON editor, eliminating syntax errors.
* **Granular Permissions:** 
  * **Global Administrators:** Full access to all settings and servers.
  * **Instance Admins:** Full read/write/start/stop access, but restricted only to the servers they are assigned.
  * **Viewers:** Read-only access to view logs, see players, and download saves/mods, with zero ability to modify or control the server.
* **Developer API:** Generate an API Key for your account to programmatically list instances, download saves/mods, or send start/stop signals.

## 🛠️ Tech Stack

* **Backend:** C# 10, ASP.NET Core (.NET 10), Entity Framework Core (SQLite)
* **Frontend:** Blazor Server, Bootstrap 5, Bootstrap Icons
* **Infrastructure:** Docker (via Docker.DotNet)

## 📦 Prerequisites

* Docker running on the host machine.
* (Windows users) Docker Desktop must be configured to expose the Docker API, or the application must be run as Administrator if interacting via named pipes.

## ⚙️ Installation & Setup

Factorio Server Controller is designed to be run as a Docker container.

### Using Docker Compose (Recommended)

1. Create a `docker-compose.yml` file:
   ```yaml
   services:
     factorio-controller:
       image: jrdiver/factorio-server-controller:latest
       container_name: factorio-server-controller
       restart: unless-stopped
       ports:
         - "8080:8080"
       environment:
         # IMPORTANT: This must match the left side (host path) of the /factorio volume bind below!
         # It tells the controller where on the host the Factorio child containers should store their data.
         - HOST_BASE_MOUNT_PATH=/mnt/user/appdata/factorio_manager/servers
       volumes:
         # Persistent storage for the controller's SQLite database, keys, and settings
         - ./data:/app/data
         
         # Maps the host's Factorio server data path so the controller can write saves/mods/configs.
         # The left side here MUST match the HOST_BASE_MOUNT_PATH environment variable above.
         - /mnt/user/appdata/factorio_manager/servers:/factorio
         
         # Allows the controller app to communicate with the Docker daemon to spawn Factorio servers
         - /var/run/docker.sock:/var/run/docker.sock
   ```

2. Start the container:
   ```bash
   docker-compose up -d
   ```

### Using Docker Run

```bash
docker run -d \
  --name factorio-server-controller \
  -p 8080:8080 \
  -e HOST_BASE_MOUNT_PATH=/mnt/user/appdata/factorio_manager/servers \
  -v ./data:/app/data \
  -v /mnt/user/appdata/factorio_manager/servers:/factorio \
  -v /var/run/docker.sock:/var/run/docker.sock \
  jrdiver/factorio-server-controller:latest
```

### Configuration Parameters

You can customize the container's behavior by passing the following environment variables and volumes. 
All data is stored inside `/app/data` by default, so simply mapping a volume to `/app/data` is sufficient for full persistence.

**Volumes:**
- `-v /var/run/docker.sock:/var/run/docker.sock`: **(Required)** Mounts the Docker socket so the controller can spawn Factorio server containers.
- `-v ./data:/app/data`: **(Required)** Persists the SQLite database, data protection keys, and global settings.

**Environment Variables:**
- `-e HOST_BASE_MOUNT_PATH`: The absolute path on the **host machine** where Factorio server instances (saves, mods, configs) will be stored. *Defaults to `/mnt/user/appdata/factorio_manager/servers` on Linux or `C:\FactorioServers` on Windows.*

### Initial Setup

1. **Initial Login:**
   * Navigate to `http://localhost:8080` (or whatever port you mapped).
   * Register a new account. **The first registered user is automatically granted the Global Administrator role.**

2. **Configure Global Settings:**
   * Navigate to the **Global Settings** page in the UI.
   * Define the `Host Base Mount Path`. This is the absolute path on your host machine where you want all server instance files (saves, mods, configs) to be permanently stored. This must be an absolute path that Docker can mount.
   * *Example:* `/opt/factorio_servers` or `C:\FactorioServers`.



## 📡 API Usage

You can interact with your servers programmatically by generating an API Key from your User Profile page.
Include the key in your requests using the `Authorization: ApiKey YOUR_KEY` header.

**Available Endpoints:**
- `GET /api/instances` - List all accessible instances (includes your Access Level).
- `GET /api/instances/{id}/saves` - List all saves for an instance.
- `GET /api/instances/{id}/saves/{filename}` - Download a save file.
- `GET /api/instances/{id}/mods/{filename}` - Download a mod file.
- `GET /api/instances/{id}/mods/downloadAll` - Download all mods as a zipped modpack.
- `POST /api/instances/{id}/start` - Start the instance (Requires Admin access).
- `POST /api/instances/{id}/stop` - Stop the instance (Requires Admin access).
- `PUT /api/instances/{id}/saves/active` - Set the active save file (Requires Admin access).

## 🤝 Contributing

Contributions, issues, and feature requests are welcome!
Feel free to check out the [issues page](../../issues).

## 📝 License

This project is open-source and available under the [MIT License](LICENSE).

---

## 🔨 Developer Build Instructions

If you wish to fork the repository or compile from source, you will need the following prerequisites:
* [.NET 10.0 SDK](https://dotnet.microsoft.com/download)

**Clone & Build:**
```bash
git clone https://github.com/yourusername/FactorioServerController.git
cd FactorioServerController
dotnet build
```

**Run Development Server:**
```bash
cd FactorioServerController
dotnet run
```