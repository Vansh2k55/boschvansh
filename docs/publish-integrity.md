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

## Options for a real fix

Not yet implemented — this is a scope decision.

**A. Make the local source authoritative.** Swap `PackageReference` for
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

## Regression test

`chaos-scripts/test-publish-integrity.ps1` reproduces the defect on demand and
exits non-zero when any divergence is observed, so it can gate a build. It is
the before/after measurement for whichever option is taken.

Current measurement, 2026-10-10, before any effective fix:

| | Result |
| --- | --- |
| Writes attempted | 20 |
| Accepted (201) | 14 |
| **Divergent (500, row committed, no event)** | **6 (30%)** |
| Failed cleanly (nothing committed) | 0 |

It leaves `regress-*` customers in the sandbox database. Harmless, but they
accumulate.
