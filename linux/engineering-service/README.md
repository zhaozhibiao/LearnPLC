# Independent engineering HTTPS service

The user service explicitly keeps `PrivateTmp=no` and `PrivateUsers=no`.
User-service mount sandboxing otherwise creates a user namespace containing only
UID 1000; the host root hardware broker then appears as overflow UID 65534 to
`SO_PEERCRED` and hardware application is correctly rejected. Keep
`NoNewPrivileges=yes`, owner-only files and the broker's UID-0 peer check. Do not
work around this by accepting the overflow UID as root. Only this engineering
service needs restarting when updating its unit; PLC and motion units are not
restarted by that update.

This user `a` service listens at HTTPS port **18445**, independently of the PLC,
Runtime and motion units. It never starts/restarts those units itself. The existing
fixed deployment coordinator remains responsible for all deployment safety gates.
Its code path is fixed at `/home/a/st-workbench-deployment/coordinator.py`; the
Runtime prefix's `deployment/` directory stores state and versions, not executable
coordinator code. Deploy the matching `scoped_transfer.py` alongside coordinator
before installing this service; the installer verifies both code files exist.
Restarting Runtime can interrupt debug sockets and Runtime calls, but authenticated
`GET /engineering/health` remains available. Identity reports coordinator disk state
and its explicit `runtime_confirmed` flag; a stopped PLC is not an authentication failure.

## Wire protocol

Every request requires `X-Engineering-Token: <pairing token>`. Engineering endpoints
also accept `Authorization: Bearer <pairing token>`. Pairing secrets are never accepted
in URLs, cookies or request bodies. The proxy preserves the separate Runtime
`Authorization: Bearer <Runtime JWT>` header. Socket.IO uses the same pairing header
and preserves the existing Runtime socket authentication payload/query.

| Method and path | Body/result |
| --- | --- |
| GET `/engineering/health` | `{ "success": true, "service": "engineering" }` independent health |
| GET `/engineering/identity?request_id=ID` | Normal coordinator `status` result; ID optional, generated if absent |
| POST `/engineering/session` | No body; private local Runtime login returns only `{ "access_token": "JWT" }` with `Cache-Control: no-store` |
| POST `/engineering/deploy` | Coordinator JSON, including required safe `request_id` and allowlisted `action`; normal coordinator response |
| Any `/api/*` | Local Runtime REST proxy; preserve Runtime JWT header; no redirects or retries |
| Any `/socket.io/` | Engine.IO polling or WebSocket proxy; preserve query/payload; pairing header required on each request |

Actions: `status`, `realtime-status`, `stage`, `compile`, `activate`, `recover`,
`start`, `stop`, `configuration-plan`, `configuration-apply`, `hardware-validate`,
`hardware-apply`, `hardware-stage`, `program-export`, `hardware-export`, `program-debug-map`.
Fields: `action`, `request_id`, `build_hash`, `package_base64`, `package_sha256`,
`expected_active_build_hash`, `expected_hardware_hash`, `configuration_identity`.
Arbitrary package paths, commands, service names and filesystem operations are rejected.
The existing baseline-adoption actions are intentionally unavailable over HTTPS.

`request_id` must match `[A-Za-z0-9_-]{1,80}`. Hashes must be SHA-256 hex strings
(nullable expected identities supported). Request/response limits are 48/64 MiB;
archives remain limited by coordinator to 32 MiB. Socket messages are capped at
1 MiB, connections at 16 total and 8 simultaneous WebSockets per paired service.
Coordinator calls run once with a 600-second deadline; HTTP upstream calls have a
25-second deadline; debug sockets reconnect at the client after Runtime restart.
Duplicate in-flight deployment IDs return 409. No mutation is retried automatically.
After timeout/disconnect, query identity before any explicit user-authorized retry.

The service returns 401 for missing/invalid pairing, 400 for invalid deployment
schemas, 413 for oversized requests, 429 for capacity and sanitized 502 for backend
exceptions. Coordinator operational failure is its normal JSON (`success:false`)
with a sanitized message. Coordinator request IDs must match. Expired Runtime JWTs
remain Runtime 401 responses: obtain a new session but never replay a write.

## Install and pair

Copy this folder to a private staging folder on the reviewed device. As user `a`,
run `bash install-user.sh 192.168.1.31`. It checks port occupancy, creates an
independent venv, installs pinned aiohttp, records resolved dependency versions and
license metadata, installs a private random pairing token and TLS key/certificate,
then enables only `stworkbench-engineering.service`. It needs neither root nor
firewall changes. It never edits the Runtime venv. User lingering must already be
enabled to survive logout/reboot; inspect `loginctl show-user a -p Linger` and have
the administrator enable it explicitly if needed.

If Debian's optional ensurepip/venv package is absent, the installer creates a
stdlib `venv --without-pip` and invokes `bootstrap-pip.py VENV` only when that venv
has no pip. The helper fetches stable pip release metadata from official PyPI over
verified HTTPS, permits only the universal wheel on `files.pythonhosted.org`,
checks its SHA-256 and bounded size, and installs that exact resolved version
offline from the private wheel into this venv. Its version/checksum/MIT license
are recorded in `venv/pip-bootstrap.json` and the dependency inventory. No sudo,
system package installation, Runtime environment changes or downloaded shell
scripts are used. The helper can also bootstrap a disposable Linux test venv.

The certificate is copied from Runtime only if its IP SAN matches the supplied IP;
otherwise a separate certificate includes localhost, 127.0.0.1 and the device IP.
Existing engineering certificate/token persist on reinstall. Backend TLS always
validates `https://localhost:8443` using the existing local Runtime certificate.
The desktop pins the engineering leaf DER SHA-256 returned by authenticated pairing.
Re-pair explicitly after certificate replacement. No certificate validation bypass.

Invoke `/home/a/.local/share/st-workbench/runtime-v4.3.0/engineering/venv/bin/python
/home/a/.local/share/st-workbench/runtime-v4.3.0/engineering/pair-client.py 192.168.1.31`
through the authenticated SSH bootstrap into a protected consumer. Its stdout is
**secret JSON**, exact shape: `{ "endpoint": "https://192.168.1.31:18445", "token":
"...", "certificate_sha256": "64 lower-case hex characters" }`. Never send stdout
to chat, journal or shared logs. Store it in Windows Credential Manager. The installer
does not print the token. Server token/key files are owner-only mode 600; service
access/error output is disabled in the unit to avoid auth/query leaks.

## Dependency and verification

aiohttp **3.14.5** supports Python 3.13 and is licensed Apache-2.0 AND MIT. See
[PyPI release metadata](https://pypi.org/project/aiohttp/3.14.5/) and
[official documentation](https://docs.aiohttp.org/en/stable/). Dependencies are
resolved inside this service's venv; inspect `dependency-licenses.json` and
`installed-dependencies.txt` before redistribution. This adds no GPL dependency.

Run `python -m unittest discover -s linux/engineering-service -v`. Tests use isolated
HTTP/WS backends and disposable subprocess scripts: they do not contact any PLC,
download a candidate, change hardware or enable motion. Run the same suite with the
installed Linux venv before enabling the service. Live acceptance is read-only:
health, identity, session, debug map and export; never use activate/start/hardware
apply for service validation.
