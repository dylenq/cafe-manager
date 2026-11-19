# Cafe Manager

Local management software for a small gaming cafe: station types and stations, timed sessions, PC lock and TV power control. Runs on one server in the cafe and keeps working without internet.

## Run

```bash
export PATH="/usr/local/share/dotnet:$PATH"   # macOS: put the dotnet CLI on PATH
dotnet run --project src/Cafe.Server
```

The server listens on `http://0.0.0.0:5080`, so tablets and phones on the LAN can reach it at `http://<server-ip>:5080`.

## First run

On an empty database every page redirects to `/setup`, where the owner chooses the shared admin password (minimum 8 characters, entered twice). Only a hash is stored. Setup cannot be re-run afterwards to reset the password. Until setup is finished, anyone on the LAN can claim the admin password, so do it right after installing.

## Configuration

| Key | Default | Environment variable |
|-----|---------|----------------------|
| `Kestrel:Endpoints:Http:Url` | `http://0.0.0.0:5080` | `Kestrel__Endpoints__Http__Url` |
| `ConnectionStrings:Cafe` | `Data Source=cafe.db` | `ConnectionStrings__Cafe` |

`ASPNETCORE_URLS` and the `--urls` argument also override the listen address. The schema is created and updated automatically at startup through EF Core migrations.

## Security note

v1 serves plain HTTP on the LAN with no TLS. The admin password and the auth cookie travel in clear text on the cafe network, so keep the cafe network private. The login is throttled in memory: after 5 failed attempts from one address within 5 minutes, that address is refused for 5 minutes (a server restart clears the counters).

## Tests

```bash
dotnet test Cafe.sln
```
