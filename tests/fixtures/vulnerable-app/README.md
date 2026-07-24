# vulnerable-app — INTENTIONALLY INSECURE TEST FIXTURE

> ⚠️ **This directory is deliberately broken.** It exists only to give EAAP's security
> adapters (Trivy, Semgrep, Gitleaks) something real to find in tests. Do **not** copy any
> of it into a real project and do **not** deploy it anywhere.

What is planted here, and which scanner is expected to catch it:

| File | Problem | Caught by |
| ---- | ------- | --------- |
| `package.json` | Depends on `lodash@4.17.11`, which has a known prototype-pollution CVE | Trivy (SCA) |
| `config/settings.py` | Contains an **AWS access key** matching the standard pattern | Gitleaks / Trivy (secret) |
| `app/db.py` | Classic SQL injection: user input concatenated into a query | Semgrep (SAST) |

**The credentials are fake.** `AKIAIOSFODNN7EXAMPLE` is the example access key published in
AWS's own documentation; it authenticates nothing. It is used here precisely because it is a
well-known non-secret that still matches secret-scanner patterns.
