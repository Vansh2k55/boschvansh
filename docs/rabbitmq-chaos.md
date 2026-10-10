# Experiment: RabbitMQ broker outage

`chaos-scripts/kill-rabbitmq.ps1`

Original experiment by Tigisha P. Pradhan. Reworked to meet the safety contract
in `AGENTS.md` and extended to measure the write path, because the read path
turns out not to break at all.

## Why this experiment

The dependency map puts four publishers and four consumers behind `rabbitmq`,
which makes it one of the two genuine single points of failure in the system
(the other is `sqlserver`). It is also the one where the damage is least
visible, which is exactly why it is worth injecting.

## What it does

1. Validates it is talking to a local sandbox Docker endpoint, and that exactly
   one broker container is running.
2. Captures a baseline: three rounds of probes across all three module pages,
   plus one customer write to prove the publish path works before anything is
   broken. **If the baseline fails, nothing is injected.**
3. Starts a watchdog job that restores the broker even if the script is killed.
4. `docker compose kill -s SIGKILL` then `stop` on `rabbitmq`.
5. Probes the module pages throughout, and attempts one customer write during
   the outage.
6. Rolls back on whichever comes first: the duration cap, a safety threshold
   breach, or the `STOP` file.
7. Waits for recovery, then checks whether the outage-time write left the
   database and the event stream disagreeing.
8. Writes `results/<run-id>/samples.csv` and `report.json`.

Injection requires `-Execute`. Without it the script prints what it would do and
exits — that is the operator-approval gate.

## Running it

```powershell
./chaos-scripts/kill-rabbitmq.ps1                          # dry run, changes nothing
./chaos-scripts/kill-rabbitmq.ps1 -DowntimeSeconds 25 -Execute
```

Manual kill switch, from a second terminal at the repo root:

```powershell
New-Item chaos-scripts/STOP -ItemType File -Force
```

Delete the file afterwards or the next run refuses to start. If PowerShell is
killed outright and the watchdog does not fire:

```bash
docker compose -f src/docker-compose.yml start rabbitmq
```

Duration is capped at 120s, tighter than `kill-service.ps1`, because every
event-driven service depends on this one container.

## Observed results

Four runs on 2026-10-10. Numbers below are from the runs recorded in
`chaos-scripts/results/`.

### The read path does not break

| Phase | Customer | Vehicle | Workshop |
| --- | --- | --- | --- |
| Baseline | 13–22ms, 200 | 11–25ms, 200 | 18–51ms, 200 |
| Broker down | 11–19ms, 200 | 9–15ms, 200 | 15–29ms, 200 |
| Recovered | 12–19ms, 200 | 14–17ms, 200 | 20–24ms, 200 |

All three module pages stayed healthy for the entire outage, with no measurable
latency change. Each API serves reads from its own database and never touches
the broker on that path. **A monitoring setup that only watches page health
would have reported this outage as a non-event.** That is the first finding, and
it is the reason the experiment also writes.

### The write path fails, and leaves the system inconsistent

| | Baseline | During outage | After recovery |
| --- | --- | --- | --- |
| `POST /api/customers` | 201 | **500** | 201 |

Reproduced in three runs. The interesting part is what the 500 hides:

```
divergence check: outage write accepted=False rowCommitted=True
```

The customer row **was committed to the database**, then publishing
`CustomerRegistered` failed, and the caller got a 500. So:

- The customer exists in Customer Management.
- No `CustomerRegistered` event was ever published.
- The workshop event handler, the invoice service and the notification service
  never learn that this customer exists, and there is no replay — the event was
  never written anywhere, so nothing drains when the broker returns.
- The caller saw a failure and will reasonably retry, which creates a second
  customer rather than repairing the first.

`report.json` records this as `DivergenceDetected: true`, derived from
`WriteDuringOutage.Accepted = false` and `OutageWriteRowCommitted = true`. It is
recorded as evidence, not as a verdict.

### Recovery

`RecoverySeconds: 19.28` from the restore command to the broker accepting
connections and the publish path working again.

The publisher reconnects on its own, but not instantly. In one run the next
experiment's baseline guard refused to inject roughly a minute after a previous
recovery because a write still returned 500; a probe shortly after succeeded on
the second attempt, 5.4s in. So there is a window after the broker is back where
writes still fail. Treat broker readiness and publish-path readiness as
different things.

## What this suggests

Recommendations, not conclusions — a human should decide.

1. **No transactional outbox.** The database write and the event publish are not
   atomic. An outbox table written in the same transaction, drained by a relay,
   would make the event survive a broker outage.
2. **Health checks do not cover the publish path.** `/hc` and the module pages
   stay green while the system cannot emit an event. A readiness probe that
   includes broker connectivity would make this visible.
3. **The 500 is not idempotent-safe.** A retry after the committed-but-500 write
   duplicates the customer. The write needs to be idempotent on `CustomerId`.

## Limitations

- The experiment proves the event was not published by showing the broker was
  down. It does not inspect consumer state to confirm non-delivery.
- Only the customer publish path is exercised. Vehicle, workshop and time
  publishers are not probed.
- The watchdog restore path is implemented but has not been tested by killing
  PowerShell mid-run.
- Threshold-breach rollback is implemented but not exercised on this script.
  The module pages stay healthy during a broker outage, so there is no natural
  way to trip it. The same code is verified in `kill-service.ps1`.
- Each run leaves `chaos-*` probe customers in the database. They are harmless
  sandbox data, but they accumulate.
