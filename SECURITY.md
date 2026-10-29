# Security

Do not report credentials or private repository contents in public issues.
Use GitHub private vulnerability reporting for sensitive reports when enabled.
Otherwise contact the repository owner privately before disclosing details.

Consumers must pin actions to a reviewed commit, serialize comment sessions, and
keep privileged workflows on trusted code. Never execute or import code from an
untrusted PR with write credentials. Agent JSON is an assertion, not proof that
the agent actually inspected code. Limit who can supply trusted assessments.
