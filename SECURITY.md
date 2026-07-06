# Security Policy

## Supported Versions

| Version | Supported |
|---|---|
| 10.1.x | Yes |
| 10.0.x | Critical fixes only |
| < 10.0 | No |

## Reporting a Vulnerability

**Do not report security vulnerabilities through public GitHub issues.**

Please email **mgotfryd@brasfieldgorrie.com** with:

- A description of the vulnerability
- Steps to reproduce
- Potential impact
- Any suggested mitigations (optional)

You will receive an acknowledgement within 48 hours and a resolution timeline within 7 days.

Once the vulnerability is confirmed and patched, a new release will be published and the issue disclosed in the CHANGELOG.

## Scope

This library serializes SQLite writes via an in-process Channel-based queue. Relevant security concerns include:

- **Connection string injection** — `GetOptimizedConnectionString` normalizes and validates connection strings. Report any bypass that allows injection of unintended PRAGMA values or connection parameters.
- **File path traversal** — the write queue is keyed by normalized connection string. Report any path that allows one database's write queue to be shared with another.
- **Dependency vulnerabilities** — if a transitive dependency (Microsoft.Data.Sqlite, EF Core) has a CVE, please also report it upstream to Microsoft.
