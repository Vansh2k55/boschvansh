# Write/event divergence on the publish path

Confirmed from the source and reproducible on demand. This is the most serious
finding the project has produced, and it is worse than the chaos experiment
first suggested.

## What the experiment found

`kill-rabbitmq.ps1` showed that during a broker outage `POST /api/customers`
returns 500 while the row is committed. The reasonable reading was "no
transactional outbox" — true, but not the whole story.

## What is actually happening

It does not need a broker outage. Against a completely healthy system:

```
./chaos-scripts/test-publish-integrity.ps1 -Writes 20
→ accepted=14  divergent=6  failed-cleanly=0  of 20
→ FAIL: 6 of 20 writes (30%) left the database and the event stream disagreeing.
```

**Roughly 30% of all customer registrations silently lose their event**, with
the broker up and healthy the whole time.

### The chain

`src/CustomerManagementAPI/Controllers/CustomersController.cs`

```csharp
_dbContext.Customers.Add(customer);
await _dbContext.SaveChangesAsync();        // row is committed here

CustomerRegistered e = command.MapToCustomerRegistered();
await _messagePublisher.PublishMessageAsync(...);   // throws
```

The `catch` only handles `DbUpdateException`, so a publish failure escapes as an
unhandled exception and ASP.NET returns 500 — after the commit.

The publish throws because of two defects that compound:

1. **`Infrastructure.Messaging/Configuration/Configuration.cs` registers the
   publisher with `AddTransient`.** A brand-new `RabbitMQMessagePublisher` — and
   a brand-new TCP connection to RabbitMQ — is constructed for *every request*,
   and the old ones are left to the finalizer.

2. **`RabbitMQMessagePublisher.Connect()` is `async void` by accident.** It
   passes an async lambda to `Policy.Execute(Action)`, which binds the `Action`
   overload. `Connect()` returns before `_channel` is assigned, so the
   constructor completes with a null channel. Connection failures are swallowed
   rather than retried, for the same reason.

Together: every request builds a publisher whose connection has only just
started being established, then immediately publishes. When the publish wins the
race, `_channel` is null and it throws:

```
System.NullReferenceException: Object reference not set to an instance of an object.
   at Pitstop.Infrastructure.Messaging.RabbitMQMessagePublisher.<<PublishMessageAsync>b__0>d.MoveNext()
   at ...CustomersController.RegisterAsync(RegisterCustomer command) in /app/Controllers/CustomersController.cs:line 47
```

This is the same class of defect as the one in `RabbitMQMessageHandler`.

## Consequences, each verified

- **A row can exist with no event.** Confirmed: 6 of 20 writes, broker healthy.
- **Downstream services diverge.** The workshop handler, invoice service and
  notification service each subscribe to `CustomerRegistered`. They never see
  these customers, and nothing replays — the event was never recorded anywhere.
- **No retry or reconciliation exists.** There is no outbox, no dead-letter, no
  background relay. The event is simply lost.
- **A retry with the same id cannot repair it.** `CustomerId` is the primary key
  (`builder.Entity<Customer>().HasKey(m => m.CustomerId)`), so a retry throws
  `DbUpdateException` → another 500. Verified.
- **A retry with a new id duplicates the person.** Verified: both rows exist,
  and only the second one publishes.

## Why the obvious fix does not work here

`src/Infrastructure.Messaging` is **not** compiled into the services. Every API
consumes it as a **NuGet package**:

```xml
<PackageReference Include="Pitstop.Infrastructure.Messaging" />
<PackageVersion Include="Pitstop.Infrastructure.Messaging" Version="5.5.0" />
```

There is no `nuget.config`, so this restores the published 5.5.0 from nuget.org.
Each service's Dockerfile build context is its own folder and never copies the
messaging source.

**Editing `src/Infrastructure.Messaging/` therefore has no effect on the running
system.** The source-level corrections committed there are correct for the
source and inert at runtime. The same is true of the earlier
`RabbitMQMessageHandler` correction: the stack came up healthy because of the
compose, healthcheck, curl and event-store fixes, not that one.

This was verified, not assumed: the fixes were made, the three API images
rebuilt, the containers force-recreated, and the identical `NullReferenceException`
still appeared in the logs.

## The fix that was applied: option A

**Status: implemented and verified.** The local source is now authoritative.

**A. Make the local source authoritative.** *(chosen)* Swap `PackageReference` for
`ProjectReference` in the services, and widen each Dockerfile build context to
include `Infrastructure.Messaging`. Fixes the root cause properly and is
arguably what a fork should do. Touches ~8 services, their Dockerfiles and the
compose build contexts.

**B. Decorate the publisher in each API.** Register a local `IMessagePublisher`
that wraps the package's publisher with a readiness check and a bounded retry.
Small and contained, no build changes. Masks the race rather than removing it,
and does nothing for a genuine broker outage.

**C. Transactional outbox.** Write the event to an outbox table in the same
transaction as the row, drained by a relay. The only option that survives a real
broker outage, and the largest: a table, a migration and a background service
per publishing service.

A and C are complementary: A removes the spurious 30%, C removes the rest.

### What option A changed

- All nine services swapped `PackageReference` for a `ProjectReference` to
  `..\Infrastructure.Messaging\Infrastructure.Messaging.csproj`.
- Every Dockerfile now builds from the `src/` context and copies
  `Directory.Packages.props`, `global.json` and the messaging source, so the
  project reference resolves. The compose `build:` stanzas were widened to
  match.
- With the source finally compiled in, the two corrections take effect:
  `Connect()` is awaited properly instead of being accidentally `async void`,
  and the publisher is registered `AddSingleton` rather than `AddTransient`, so
  one connection is shared instead of one being built per request.

### What option A did not fix, by design

A genuine broker outage still diverges. Re-running `kill-rabbitmq.ps1` after the
fix:

```
fault write  customerId=chaos-9a97e278 accepted=False status=500
divergence check: outage write accepted=False rowCommitted=True
```

That is the outbox problem, option C. The write still commits before the publish
is attempted, so if the broker is genuinely unreachable the event is still lost.
Option A removed the spurious failures that had nothing to do with an outage; it
did not make publishing durable.

## Regression test

`chaos-scripts/test-publish-integrity.ps1` reproduces the defect on demand and
exits non-zero when any divergence is observed, so it can gate a build. It is
the before/after measurement for whichever option is taken.

Measured 2026-10-10, same test, same healthy system:

| | Before (package build) | After (option A) |
| --- | --- | --- |
| Writes attempted | 20 | 30 |
| Accepted (201) | 14 | **30** |
| **Divergent (500, row committed, no event)** | **6 (30%)** | **0 (0%)** |
| Failed cleanly | 0 | 0 |
| Exit code | 1 (FAIL) | **0 (PASS)** |

Under a deliberate broker outage, divergence still reproduces. That is option C
and remains open.

It leaves `regress-*` customers in the sandbox database. Harmless, but they
accumulate.
