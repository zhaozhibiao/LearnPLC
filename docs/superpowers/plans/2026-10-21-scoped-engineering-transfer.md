# Scoped Engineering Transfer Implementation Plan

**Goal:** Implement the approved engineering service, single compile/build action, contextual program/hardware upload and strict separate downloads.

**Architecture:** A user-level TLS engineering service on port 18445 proxies the existing local Runtime REST/Socket.IO and routes a bounded deployment API to the existing coordinator. Desktop uses a protected persisted pairing token and pinned device certificate. Source archives are checksum-bound package artifacts; independent hardware bundles bypass ST compilation. Existing SSH is retained for initial installation/legacy fallback only.

**Tech stack:** Existing Python/aiohttp or installed Runtime transport libraries, C#/.NET/Avalonia; audit any new dependency and prefer installed permissive dependencies.

- [x] Service: inspect installed libraries and existing TLS/keypair. Add failing isolated API/authorization/readonly/proxy tests, implement fixed user-level service with request limits, timeouts, Runtime proxy, deployment API, installation script. Do not expose shell or arbitrary paths, do not auto-retry mutations. Service survives PLC restart. Coordinate exact JSON with desktop.
- [x] Source/hardware transfer backend: add inventory-bound source archive with original project metadata and ST; verify active-program export and applied-hardware export. Add independent hardware bundle builder and hardware-stage route without ST compilation. Preserve all existing broker gates and hardware identity checks. Legacy missing source yields explicit error.
- [x] Desktop: merge compile/package, download auto-build; application download refuses mismatched physical/axis configuration and never applies it. Hardware download independent of ST; contextual upload/download labels and selection routing. Upload saves to reviewed destination and protects current draft. Add actual headless UI tests with fake transport.
- [x] Direct transport: paired HTTPS client, secure Windows credential storage, persistent session; runtime and debug proxy endpoints, eliminate SSH status on direct online, preserve program/symbol/layout identity and guarded writes. Pair using already authorized SSH only once; install to configured device and use read-only live validation.
- [x] Review scoped changes, full build/tests, Linux isolation tests, service deployment and read-only state/online/export checks. Do not download/activate programs or move motors during validation. Document current old-version source limits, new controls, endpoints and evidence.

Use TDD per component. Work in the user-requested existing folder and preserve previous dirty changes. Agents own distinct files; parent integrates desktop/transport and requests independent review. Accepted design: ../specs/2026-10-21-engineering-service-and-scoped-transfer-design.md.
