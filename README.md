# Factorio Server Controller

A lightweight, powerful, and elegant web-based control panel for managing containerized Factorio dedicated servers. Built with ASP.NET Core Blazor, this tool allows you to easily manage multiple Factorio instances, mods, saves, configurations, and users through a seamless Web Interface.

## ✨ Features

* **Instance Management:** Spin up and manage multiple Factorio servers, each running in their own isolated Docker container.
* **Full Server Backups:** Create point-in-time `.zip` backups of your entire server. Automatically prune old backups based on retention policies, and instantly restore an entire server with a single click.
* **Import & Export:** Easily export your server (including mods, saves, and settings) as a single `.zip` file, and seamlessly import them back through the web UI.
* **Advanced Mod Management:**
  * Download and update mods directly from the official Factorio Mod Portal.
  * Automatically resolve missing dependencies to prevent broken servers.
  * Sync your server's mods to perfectly match any uploaded save file.
  * Toggle mods on/off effortlessly via the visual interface.
  * Download the entire server modpack as a single `.zip` file for players to use.
* **Save Management:** Auto-loads the newest save, or lets you manually set an "Active Save" to lock it in.
* **Live RCON Console:** View real-time server logs and interact with the game via RCON. Includes quick-presets for common commands (Time, Players, Save).
* **Rich Configuration:** Edit your `server-settings.json` (server name, description, passwords) using a built-in interactive JSON editor.
* **Granular Permissions:** 
  * **Global Administrators:** Full access to all settings and servers.
  * **Viewers with Download:** Read-only access to the UI, but granted permission to download saves, modpacks, and server backups.
  * **Viewers:** Strictly read-only access. Can view logs and players, with zero ability to modify or download server files.
* **Developer API:** Generate an API Key for your account to programmatically list instances, download saves/mods, or send start/stop signals.

## 🛠 Tech Stack

* **Backend:** C# 12, ASP.NET Core (.NET 10), Entity Framework Core (SQLite)
* **Frontend:** Blazor Server, Bootstrap 5, Bootstrap Icons
* **Infrastructure:** Docker (via Docker.DotNet)

## 🐳 Installation & Setup (Docker)

Factorio Server Controller is designed to run as a Docker container. 

The application utilizes a single, unified data directory for all of its databases, settings, and child Factorio servers.

### Using Docker Compose (Recommended)

1. Create a `docker-compose.yml` file:
   ```yaml
   services:
     factorio-controller:
       image: jrdiver/factorioservercontroller:latest
       container_name: factorio-server-controller
       restart: unless-stopped
       ports:
         - "8080:8080"
       environment:
         - HOST_DATA_PATH=${PWD}/data
       volumes:
         - ./data:/data
         - /var/run/docker.sock:/var/run/docker.sock
   ```

2. Start the container:
   ```bash
   docker compose up -d
   ```

### Initial Setup

1. **Initial Login:**
   * Navigate to `http://localhost:8080` (or whatever port you mapped).
   * Register a new account. **The first registered user is automatically granted the Global Administrator role.**

2. **Configure Global Settings:**
   * Navigate to the **Global Settings** page in the UI to input your Factorio Mod Portal credentials (necessary for downloading mods).
   * Adjust your Graceful Shutdown Timeouts and maximum server backup limits.

## 📡 API Usage

You can interact with your servers programmatically by generating an API Key from your User Profile page.
Include the key in your requests using the `Authorization: ApiKey YOUR_KEY` header.

**Available Endpoints:**
- `GET /api/instances` - List all accessible instances (includes your Access Level).
- `GET /api/instances/{id}/saves` - List all saves for an instance.
- `GET /api/instances/{id}/saves/{filename}` - Download a save file.
- `GET /api/instances/{id}/mods/{filename}` - Download a mod file.
- `GET /api/instances/{id}/mods/downloadAll` - Download all mods as a zipped modpack.
- `GET /api/instances/{id}/backups/{filename}` - Download a full server backup.
- `POST /api/instances/{id}/start` - Start the instance (Requires Admin access).
- `POST /api/instances/{id}/stop` - Stop the instance (Requires Admin access).
- `PUT /api/instances/{id}/saves/active` - Set the active save file (Requires Admin access).

## 🤝 Contributing

Contributions, issues, and feature requests are welcome!

## 📄 License

This project is open-source and available under the [MIT License](LICENSE).