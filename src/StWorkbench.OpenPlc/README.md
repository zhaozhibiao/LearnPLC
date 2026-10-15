# OpenPLC communication boundary

`OpenPlcClient` accepts a HTTPS origin (for example `https://plc.example:8443/`). It rejects credentials in the address, paths, queries and fragments. The default HTTP transport uses normal certificate validation and disables redirects. Install a trusted device certificate rather than disabling TLS validation. An injected `HttpClient` remains caller-owned and must preserve these security settings; it must not log credentials or tokens.

`LoginAsync(username, password)` sends JSON to `/api/login`; the in-memory `access_token` becomes a per-request Bearer header. Relogin clears the preceding token, including when login fails. `ResetAuthentication` forgets the local token and prevents a pending login from restoring it. It does not claim to revoke a server token. No passwords or tokens are persisted to projects.

Authenticated reads: `GetStatusAsync`, `GetCompilationStatusAsync`, `GetLogsAsync(minimumId, level)`. Unauthenticated reads: `GetVersionAsync`, `GetCapabilitiesAsync`. They return detached `JsonElement` responses, preserving actual fields without inventing version-specific meanings. `OpenPlcException` reports sanitized failure categories and optional HTTP status, without response bodies or underlying exception text. Timeouts cover headers and response body; requests are never automatically retried.

Every JSON response is capped at two MiB, including responses without a declared Content-Length. Oversized status/log responses fail explicitly instead of growing memory without a bound; request smaller log ranges when supported by the target.

`UploadAsync`, `StartAsync` and `StopAsync` currently throw `NotSupportedException` before any HTTP request. No independently verified Runtime/compiler revision, package ABI, isolated staging, activation or recovery implementation is available. Advertised capability JSON is information, not proof of safe deployment. Compilation status alone does not prove activation or successful running. Stop is not an emergency stop.

`ReadOnlyMonitorSession` is a cache/display model, not a device connection. Only an independently verified read-only debug integration may supply actual samples. Exact source hash, symbol hash and running-session identity must match at binding and observation. Samples retain instance path, type, sequence, device timestamp, received timestamp and quality. Missing, expired, disconnected and unsupported values are null. No zero filling, forcing or simulated device values are implemented.

Trend histories are per variable, ordered by increasing sample sequence and bounded by configured capacity. Missing sequences, expiry and disconnection produce gaps. The watched-variable cache is also bounded. Sequence reset requires a new verified session binding. `GetTrend` returns detached snapshots. This module deliberately does not implement an unverified Socket.IO/debug wire format.

REST paths were checked against upstream [Editor Integration](https://github.com/Autonomy-Logic/openplc-runtime/blob/main/docs/EDITOR_INTEGRATION.md) on 2026-10-13. This moving documentation is not a pinned or certified target profile. Real Runtime/device integration remains unverified.

Run local, dependency-free transport/model tests with `dotnet run --project tests/StWorkbench.OpenPlc.Tests`. The handler is an HTTP boundary fixture, not a PLC simulator.
