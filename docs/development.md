# Development

## Building from source

```bash
git clone https://github.com/MorganKryze/jellyflare.git
cd jellyflare
dotnet build --configuration Release
```

Output DLL: `Jellyfin.Plugin.JellyFlare/bin/Release/net9.0/Jellyfin.Plugin.JellyFlare.dll`

## Local testing with Docker

A Docker Compose file and `Makefile` provide a full dev loop using the LinuxServer.io Jellyfin image.

### Quick start

```bash
make deploy   # setup → build → start (run once)
```

Open `http://localhost:8096` and complete the setup wizard.

### Dev loop

```bash
make update   # build → copy DLL → restart container → tail logs
```

### Available targets

| Target    | Description                                                   |
| --------- | ------------------------------------------------------------- |
| `deploy`  | First-time bootstrap: download deps, build, start container   |
| `setup`   | Download JS Injector + File Transformation, write `meta.json` |
| `build`   | Build DLL and copy it to the plugin directory                 |
| `update`  | Build, copy DLL, restart container, tail logs                 |
| `up`      | `docker compose up -d` + tail logs                            |
| `down`    | `docker compose down`                                         |
| `restart` | Restart the Jellyfin container                                |
| `logs`    | Tail Jellyfin container logs                                  |
| `clean`   | Remove `bin/`, `obj/`, and `docker/config/`                   |
| `bump`    | Bump version locally: `make bump V=1.2.0`                     |

## Releasing a new version

1. On GitHub: **Releases → Draft a new release** → create tag `v1.2.0` targeting `main`.
   Write release notes in the body; they become the `changelog` field in `manifest.json`.
   Then publish.
2. CI automatically:
   - patches `AssemblyVersion` and `FileVersion` in the csproj from the tag
   - builds and zips the DLL + icon
   - prepends a new entry to `manifest.json` (with MD5 checksum and your release notes)
   - pushes a `chore: update manifest for vX.X.X` commit back to `main`
   - attaches the ZIP to the GitHub release
3. Run `git pull` before your next local change. The CI pushes a manifest commit back to `main`.

> `make bump V=1.2.0` is still available if you need to bump the csproj locally (e.g. to verify a build), but it is no longer required before releasing.
