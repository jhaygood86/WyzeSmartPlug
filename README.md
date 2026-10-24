# WyzeSmartPlug

A small ASP.NET Core API to read the state of a Wyze Plug and turn it on/off, plus a watchdog
that uses it to power-cycle a crashed server.

> Unofficial. Not affiliated with or endorsed by Wyze Labs. It uses Wyze's cloud API, which can change at any time.

## API

| Method | Path | Description |
| --- | --- | --- |
| GET | `/plugs` | List plugs (`mac`, `model`, `nickname`, `isOnline`) |
| GET | `/plugs/{mac}` | Status including `isOn` |
| POST | `/plugs/{mac}/on` | Turn on |
| POST | `/plugs/{mac}/off` | Turn off |
| POST | `/plugs/{mac}/toggle` | Toggle |
| GET | `/healthz` | Liveness (always unauthenticated) |

If `Api__Key` is set, every request (except `/healthz`) must send a matching `X-Api-Key` header; if unset the API is open, so restrict access at the network layer.

## Configuration

Create a Wyze developer Key ID + API Key at <https://developer-api-console.wyze.com/>. The Wyze login also needs your account email and a Wyze password (if you sign in with Google, use "Forgot password" to set one). Accounts with 2FA are not supported.

| Setting (env var) | Description |
| --- | --- |
| `Wyze__Email`, `Wyze__Password` | Wyze account |
| `Wyze__KeyId`, `Wyze__ApiKey` | Wyze developer API credentials |
| `Wyze__Proxy` | Optional proxy, e.g. `socks5://127.0.0.1:40000` |
| `Api__Key` | Optional API key for this service |

Never commit credentials. For local development:

```bash
cd src/WyzePlugApi
dotnet user-secrets set "Wyze:Email" "you@example.com"
dotnet user-secrets set "Wyze:Password" "..."
dotnet user-secrets set "Wyze:KeyId" "..."
dotnet user-secrets set "Wyze:ApiKey" "..."
dotnet run
curl http://localhost:5000/plugs
```

## IPv6-only hosts

`api.wyzecam.com` and `auth-prod.api.wyze.com` have no IPv6 records. On an IPv6-only host, run Cloudflare WARP in proxy mode
(`warp-cli mode proxy`, SOCKS5 on `127.0.0.1:40000`) and set `Wyze__Proxy`; proxy mode leaves the host's own routing untouched.

## Watchdog (`deploy/`)

`deploy/watchdog/watchdog.sh` runs from a systemd timer every 5 minutes. It checks that Cockpit answers over HTTPS
(TLS not validated) and that it can SSH in and run `remote-health.sh`. **Only if both Cockpit and SSH fail in the same run**
does it turn the plug off, wait, and on again. Failed health checks on a reachable host are logged, not acted on.
A cooldown (default 30 min) prevents reboot loops and `DRY_RUN=1` (the default) only logs what it would do.
See the `*.example` env files and `deploy/systemd/`.

## License

MIT
