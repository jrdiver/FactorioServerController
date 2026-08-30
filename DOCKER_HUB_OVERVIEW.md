# Factorio Server Controller

A lightweight, powerful, and elegant web-based control panel for managing containerized Factorio dedicated servers. 

This controller sits on your Docker host and acts as a central command center. It automatically spawns, manages, and destroys isolated Factorio servers in their own containers behind the scenes, while providing you with an intuitive web UI to manage them.

## Key Features

* **Multi-Server Management:** Spin up and manage multiple Factorio servers instantly.
* **Full Server Backups:** Create point-in-time `.zip` backups of your servers and restore them with a single click.
* **Built-in Mod Management:** Download and update mods directly from the Factorio Mod Portal. Automatically resolve missing dependencies and sync mods to match your uploaded saves.
* **Import & Export:** Export your entire server (mods, saves, settings) as a single `.zip` file, and seamlessly import them to spin up clones or migrate servers.
* **Live RCON Console:** View real-time server logs and interact with the game via RCON.
* **Role-Based Access:** Support for Global Administrators, Viewers with Download Access, and strict Read-Only Viewers.
* **JSON Config Editor:** Edit `server-settings.json` with a visual editor to prevent syntax errors from bringing down your server.

## Quick Start (Docker Compose)

Factorio Server Controller is designed to run cleanly by utilizing a single unified data directory for all of its databases, settings, and the child Factorio servers it spawns.

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

### Setup Instructions
1. Save the above configuration as `docker-compose.yml` in an empty folder.
2. Run `docker compose up -d` in that folder.
3. Navigate to `http://localhost:8080` in your browser.
4. Register a new account. **The first registered user is automatically granted the Global Administrator role.**

## Architecture Notes

* **Docker Socket:** This container requires access to `/var/run/docker.sock` because it actively uses the Docker API to spin up the actual Factorio game servers (using the `factoriotools/factorio` image) as sibling containers on your host.
* **Data Storage:** Inside your mapped `HOST_DATA_PATH`, the controller will automatically generate four folders to keep everything cleanly organized:
  * `app-data/`: Contains the SQLite database, global settings, and encryption keys.
  * `instances/`: Contains the individual saves, mods, and config folders for each Factorio server you create.
  * `backups/`: Stores all the full-server `.zip` backup archives.
  * `global_mods/`: Acts as a centralized cache for mods downloaded from the Mod Portal, preventing duplicate downloads across your different servers.

## Support & Source Code

For bug reports, feature requests, or to view the source code, please visit the [GitHub Repository](https://github.com/jrdiver/FactorioServerController).
