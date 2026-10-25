# Conflict facts after a synthetic transfer failure

The existing failure-only CLI observer performs one additional public conflict
list query with `--no-start` and the original five-second request timeout. It
retains at most 64 records from the bounded public response and records whether
more results exist. It does not follow a cursor or resolve, resume or refresh work.

Only valid conflict/instance IDs, closed reason/source/status names and the
relationship between the two supplied revisions are retained. Paths, revisions,
messages and cursor text are discarded. Invalid fields remain unclassified or
unavailable. This cannot prove that any conflict caused the failed transfer or
later write; it supplies evidence for the next diagnosis.

Original transfer success checks, complete-byte verification, timeouts and cleanup
remain unchanged. This observation does not constitute a fix or S06 acceptance.
