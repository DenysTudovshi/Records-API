# Records API

A small .NET 8 microservice that stores and retrieves person records over a JSON REST API,
backed by PostgreSQL.

## Quickstart

No clone, no .NET SDK — just Docker:

```bash
curl -fsSL https://raw.githubusercontent.com/DenysTudovshi/Records-API/main/compose.yaml | docker compose -f - up
```

This pulls the image published by the last green build of `main`, starts PostgreSQL
alongside it, and serves the API on <http://localhost:8080> (Swagger UI at
<http://localhost:8080/swagger>).

**This exact command runs against the published image on every green build of `main`**,
in the `verify-published` CI job, with all registry credentials dropped first.

### Try it

```bash
curl -i -X POST http://localhost:8080/save \
  -H 'Content-Type: application/json' \
  -d '{"external_id":"3fa85f64-5717-4562-b3fc-2c963f66afa6","name":"some name","email":"email@email.com","date_of_birth":"2020-01-01T12:12:34+00:00"}'

curl http://localhost:8080/3fa85f64-5717-4562-b3fc-2c963f66afa6
```

## API

| Method | Route           | Result                                                    |
| ------ | --------------- | --------------------------------------------------------- |
| `POST` | `/save`         | `201` with `Location: /{external_id}`, or `200` if it already existed |
| `GET`  | `/{id}`         | `200` with the record, `404` if unknown                    |
| `GET`  | `/health/live`  | Process liveness                                           |
| `GET`  | `/health/ready` | Readiness, including database reachability                 |

Both endpoints speak this shape, in `snake_case`:

```json
{
  "external_id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "some name",
  "email": "email@email.com",
  "date_of_birth": "2020-01-01T12:12:34+00:00"
}
```

Errors are RFC 7807 `application/problem+json`. Validation failures return `400` with an
`errors` object keyed by the wire field name (`date_of_birth`, not `DateOfBirth`).

### Contract decisions

The brief leaves two points genuinely open. Both are decisions, not assumptions, and the
alternative reading is recorded so it can be argued with.

- **`{id}` is the `external_id`.** The response is specified as containing `external_id`,
  and that is the only identifier in the contract, so `GET /{id}` resolves against it. A
  surrogate key exists in the table but never reaches the wire: a client only ever holds
  the id it supplied itself.
  *Rejected:* treating `{id}` as a server-generated internal id. It is a defensible
  reading, but it forces a client to `POST` and parse the response before it can read
  anything back, and it would require a fifth response field the spec does not define.
- **`POST /save` is an upsert.** The verb is `save`, not `create`, so it is idempotent on
  `external_id` — `201` on create, `200` on replace. With a caller-supplied id that means
  a retry after a dropped connection converges instead of failing. Concurrent duplicates
  race the unique index; the loser catches PostgreSQL `23505` and converges on the update.
  *Rejected:* `409 Conflict` and strict create-only semantics. Equally defensible, and a
  small change, but it makes the endpoint non-idempotent — a retried request that already
  succeeded then reports failure.
- **`date_of_birth` round-trips its offset exactly.** Send `+02:00` and `GET` returns
  `+02:00`. See below — this is less obvious than it looks.
- A zero offset is always rendered `+00:00`, so an input of `...Z` returns as `...+00:00`.
  Both are RFC 3339 spellings of the same instant.

### Conventions

Three of the choices above are not house style. Whalebone publishes two OpenAPI documents that
anyone can fetch without credentials, and they settle what the brief leaves open:

- <https://api.whalebone.io/whalebone/2/doc/openapi> — OpenAPI 3.0.3, *Whalebone API*, ~110 KB of YAML
- <https://portal.whalebone.io/api/public/v1/doc/api-spec> — OpenAPI 3.0.0, *Policy Config API*, ~27 KB

Both answered `200` to an unauthenticated `GET` on 2026-09-02. Readable that day is not a promise
about tomorrow, hence the date.

**`snake_case` is theirs.** Across both documents, field and query-parameter names are snake_case:
65 distinct multi-word names against two camelCase (`createdAt` and `createdBy`, both in the portal
spec). Real examples from the main spec — `client_ip`, `device_id`, `subscription_id`, `error_code`,
`accepted_values`, `content_categories`; and from the portal spec — `allow_lists`, `deny_lists`,
`match_strategy`. The claim is about names *on the wire*: the portal document's own component keys
are PascalCase (`DomainListCreateRequest` and 44 others), which is a different namespace and one a
client never sees.

**Serving the OpenAPI document in every environment matches their practice.** Both documents above
are the vendor's own and need no key. This service serves its own document unconditionally for the
same reason: the deliverable is a container someone else runs, and an API explorer that only exists
in Development helps nobody.

**RFC 7807 is a considered divergence.** Their error envelope is `{message, errors: [...]}`, each
item carrying `error`, `error_code`, `message`, `parameter`, `value` and `accepted_values`. In one
respect that shape is better than problem+json: `error_code` is a stable integer — `21` for
`INVALID_PARAM_VALUE`, `22` for `MISSING_PARAM_VALUE` — so a client branches on a number instead of
string-matching prose. Neither document mentions `problem+json` or RFC 7807 anywhere in 137 KB.

This service diverges anyway. `application/problem+json` is the registered media type, so a generic
client can tell the body is an error before it parses it; and ASP.NET Core produces
`ValidationProblemDetails` — an `errors` object keyed by field — out of the framework, so the
per-field detail arrives without a hand-rolled envelope that would need its own tests to stay
correct. Matching their envelope exactly would mean writing and testing that envelope, in a service
whose contract is four fields, to gain nothing a caller of *this* API can use. The divergence costs
one media type and one JSON shape, both at the edge, and both trivially replaceable if this ever
had to sit behind the same gateway.

*Rejected:* mirroring `{message, errors[]}` for familiarity. It is the friendlier choice for a
reviewer who lives in their ecosystem and the wrong one for anybody else, and it trades a documented
standard for a bespoke shape on the strength of a guess about who calls this.

**One divergence inside the divergence.** Their `errors[]` items echo the rejected input straight
back — `parameter: action`, `value: foo`. This service never does, and that is deliberate: here the
rejected value is somebody's name, email address or date of birth. See
[Data protection](#data-protection).

## Why `date_of_birth` is stored as two columns

Npgsql maps `DateTimeOffset` onto `timestamptz` and **rejects any non-zero offset**:

```
Cannot write DateTimeOffset with Offset=02:00:00 to PostgreSQL type
'timestamp with time zone', only offset 0 (UTC) is supported.
```

So the obvious mapping throws on the primary happy path. The obvious fix —
`ToUniversalTime()` — stops the throw but makes the service answer `+00:00` to a request
that said `+02:00`: the right instant, the wrong document.

This service stores the UTC instant in `date_of_birth_utc` and the caller's offset in
`date_of_birth_offset_minutes`, and reconstructs on read. The column stays sortable and
comparable, and the API echoes back exactly what was sent. Pinned by tests at `+02:00`,
`-05:30`, `+14:00` and `-12:00`.

## Observability

### Correlation id

Every response carries an `X-Request-Id` header, and every log line written while handling that
request carries the same value under `CorrelationId`. A caller who supplies their own
`X-Request-Id` gets it echoed, so a trace that began upstream continues here instead of
restarting. The header name is the one Whalebone's own API already returns.

An inbound value is honoured only if it could plausibly be a request id: exactly one header, at
most 64 characters, drawn from `[A-Za-z0-9._:-]`. Anything else is replaced with a fresh UUID
rather than reflected. That value reaches a response header *and* every log line for the
request, and echoing arbitrary caller-supplied bytes into both is how a correlation id turns
into a log-injection vector.

Two details, because both are easy to get wrong and neither is visible from the outside:

- **The header is written from an `OnStarting` callback, never eagerly.** `UseExceptionHandler`
  calls `Response.Clear()` — which clears every header — before invoking the exception handler,
  so a header set before the throw is missing from the 500 that follows. `OnStarting`
  registrations live on the response feature, which `Clear()` leaves alone. The test that asserts
  a `400` still carries the header is what stops anyone simplifying this back to the eager form.
- **The middleware sits outside `UseExceptionHandler`, not inside.** The header behaves the same
  either way; the log scope does not. Registered inside, the scope is disposed as the exception
  unwinds, so the handler's own error line — the single line that most needs a correlation id —
  is the one without it.

The id appears in every problem body as `request_id`. It is spelled `snake_case` at the source
because `System.Text.Json` writes extension members verbatim, so the global naming policy never
sees them; a test pins that rather than trusting it.

Console logging is JSON, with scopes included. The service ships as a container, and
unstructured console text costs a field-by-field parse before anything can query it.

### Metrics

`GET /metrics` serves the Prometheus text exposition format
(`text/plain; version=0.0.4`). It publishes ASP.NET Core's own instruments rather than
hand-rolled counters: the framework already records `http.server.request.duration` and
`http.server.active_requests` for every route, including the ones nobody remembered to
instrument, and a parallel set of counters would only be a second and staler copy.

```
microsoft_aspnetcore_hosting_http_server_request_duration_count{
  http_request_method="POST", http_response_status_code="201",
  http_route="/save", network_protocol_version="1.1", url_scheme="http"} 1
```

`_count` is the request count and `_sum`/`_bucket` the duration — .NET 8 emits no separate
counter, so one histogram answers both. The exporter prefixes bridged names with the meter, which
is why they read `microsoft_aspnetcore_hosting_...` rather than the OpenTelemetry spelling.

**No series is ever labelled with `external_id`.** That would be unbounded cardinality and
personal data in a single move, and a metrics store is exactly the wrong place to discover
either. The route label is the route *template* — `/{id:guid}`, never `/3fa85f64-...` — and a
request that matched no route carries no route label at all, so a caller cannot mint series by
hammering random paths. A test asserts the scrape contains no UUID-shaped token anywhere and no
label named `external_id`, `name`, `email` or `date_of_birth`.

Latency buckets are set explicitly to eleven web-shaped boundaries from 5 ms to 10 s. The
bridge's own default is 25 exponential buckets from 10 ms, whose top boundary lands near 46
hours — 26 series per label combination, to describe a request that would have been abandoned
before the tenth of them.

**Why `prometheus-net.AspNetCore` and not the OpenTelemetry exporter.** The OpenTelemetry
Prometheus exporter has never shipped a stable release: all 34 published versions are
prerelease, the newest is `1.18.0-beta.1`, and its own README warns of breaking changes before
stable. `prometheus-net.AspNetCore` is MIT and stable, and it is one direct package against
three. What it costs, stated plainly: the project has had no commits since January 2024, it
targets `net6.0` (which `net8.0` consumes without a warning), and its bridged metric names
diverge from the OpenTelemetry convention. If any of that starts to bite — or the exporter
reaches 1.0 — swapping is a one-file change, because nothing in the service references it.

**`/metrics` is on the main port, and in production it usually should not be.** A scrape
endpoint normally gets its own listener or a network policy, so it is reachable by the scraper
and by nothing else; here it also publishes `process_*` and `dotnet_*` internals to anyone who
can reach the API. It is on port 8080 because the deliverable is a one-line quickstart, and an
endpoint a reviewer cannot reach is an endpoint they have to take on trust. The production
position is the second port.

One implementation note that is not obvious from the code: the meter bridge starts behind an
`Interlocked` guard, because the Prometheus registry is process-global while a host is not.
`WebApplicationFactory` runs the entry point once per host it builds, and a second adapter over
the same registry doubles every measurement — a test suite would read exactly twice the truth
and look entirely plausible doing it.

## Data protection

Three of the four fields are personal data. That is not incidental to this service — it is the
entire payload — and Whalebone marks personal data at contract level in their own API with an
`x-wb-encrypt` vendor extension, so the handling is stated here rather than left to be inferred
from the absence of a leak.

| Field | What it is | Where it appears | Where it never appears |
| ----- | ---------- | ---------------- | ---------------------- |
| `name` | Personal data | Request body, response body, the `name` column | Logs, metric labels, error bodies |
| `email` | Personal data, and an account identifier almost everywhere else | Request body, response body, the `email` column | Logs, metric labels, error bodies |
| `date_of_birth` | Personal data, and a common answer to a knowledge-based authentication question | Request body, response body, two columns | Logs, metric labels, error bodies |
| `external_id` | A caller-supplied opaque identifier, not personal data by itself | Request body, response body, the URL path, the framework's `RequestPath` log scope | Metric labels |

**Retention.** A row lives until something deletes it. There is no TTL, no soft delete, and no
second copy anywhere: no cache, no outbox, no event stream, no analytics sink, and — by the row
above — no log line and no metric series. That is what the table is for. It means an erasure
request is one `DELETE FROM person_records WHERE external_id = ...` plus whatever the operator's
backup retention adds, rather than an archaeology exercise across systems that each kept their
own copy for their own good reasons.

**There is no `DELETE` endpoint, deliberately.** The brief specifies two endpoints, and quietly
growing a third is the most common way this exercise gets failed. Erasure is served today by the
operator, against the database. *The trigger to add one:* the first time an erasure request has
to be actioned by somebody without a database session — at which point the endpoint is a
half-hour's work precisely because nothing else holds a copy.

**Nothing is provable by assertion, so it is proved by test.** `PersonalDataTests` registers a
capturing `ILoggerProvider` in the test host, POSTs a record whose name and email are
deliberately distinctive, and asserts neither string appears in any captured line — in the
rendered message, in a structured state value, or in an enclosing scope. Three ascending guards
run first (the capture is non-empty; the EF Core command channel is among the captured
categories; the `INSERT` itself was captured), because the failure worth fearing is not the
assertion breaking but the assertion quietly measuring an empty list.

The capture is filtered at `Trace` for that provider only. The service pins
`Microsoft.EntityFrameworkCore.Database.Command` to `Warning`, which is correct in production and
would have made the test vacuous: a capture inheriting those pins records nothing at all.

That test was verified the only way this kind of test can be: by adding
`LogInformation("Saved {Name} {Email} {DateOfBirth}", ...)` to the save handler, watching it
fail and name the offending line, and removing it again.

A separate test asserts EF Core's `EnableSensitiveDataLogging` is off. It is off by default; the
point is that turning it on writes every parameter value verbatim on the `Information` channel,
which for this service is the whole payload, and the default is one line away from changing.

**One deliberate divergence.** Whalebone's error contract echoes the rejected input back as
`value` — `parameter: action`, `value: foo`. This service names the field and stops there,
because the rejected value here is a name, an email address or a date of birth, and an error body
is among the least controlled things a service emits: it reaches the caller, then their logs,
then frequently a screenshot in a ticket. A test posts an invalid email and asserts the `400`
names `email` without containing it.

## Layout

```
src/
  Whalebone.Records.Api             Minimal API endpoints, RFC 7807 handler, health, Swagger
  Whalebone.Records.Application     Commands, queries, validators, domain — no ASP.NET, no EF
  Whalebone.Records.Infrastructure  EF Core 8 + Npgsql, migrations, repository
tests/
  Whalebone.Records.UnitTests        Domain and validation rules
  Whalebone.Records.IntegrationTests Endpoints against real PostgreSQL, plus end-to-end
```

`Application` references neither ASP.NET Core nor EF Core, which is the point of the split:
it declares `IRecordRepository` and `Infrastructure` implements it, so the dependency arrow
points inward.

Endpoints are registered through an `IEndpoint` interface with a `static abstract Map`, so a
missing registration is a compile error rather than a missing route, and endpoint registration
does no assembly scanning.

### Why MediatR, and what it costs

Between an HTTP route and a `SELECT` sits a reflection-based dispatcher. That is a real cost,
and worth stating rather than glossing:

- one indirection hop between an endpoint and its handler;
- one package, held to an exact licence pin (below);
- assembly scanning at startup to build the handler map — the one place this service scans.

What buys it is a single open pipeline behaviour. `ValidationBehavior` is the only one, and it
gives both endpoints identical validation with zero per-endpoint wiring: the rules are declared
beside the command they guard, in a project that references neither ASP.NET Core nor EF Core,
and a new use case inherits them by existing. The alternative — calling the validator by hand
at the top of each handler — is a few lines per endpoint that must not be forgotten, and
forgetting them fails silently, by accepting bad input.

**What would retire it.** At one behaviour and two endpoints this is close to break-even. An
endpoint filter (`AddEndpointFilter`) reaches the same place with no package and no reflection.
If a second behaviour never arrives — if authorisation, caching and transaction scoping all
stay out of scope — then the dispatcher is carrying one passenger, and direct handler
injection behind a filter is the cheaper shape. The `Application` project barely changes either
way: the commands and their validators are already the contract.

### Dependency pins

Two packages are pinned to their **last open-source release**, exactly rather than as a floor,
so a restore cannot silently cross a licence boundary:

| Package              | Pin        | What the next version changed                              |
| -------------------- | ---------- | ---------------------------------------------------------- |
| `MediatR`            | `[12.5.0]` | 13.0.0 relicensed from Apache-2.0 to a commercial licence   |
| `FluentAssertions`   | `[7.2.2]`  | 8.0.0 moved to an Xceed commercial licence                  |

The brackets are the point. `12.5.0` in NuGet means *12.5.0 or newer*, which is exactly how a
transitive bump walks a build across a licence change with nobody reading a diff. `[12.5.0]`
means that version and no other, and a restore that wants otherwise fails loudly.

### The line that stops the docs drifting

Swashbuckle's schema generator reads `Mvc.JsonOptions`, not `ConfigureHttpJsonOptions` — even
for minimal APIs. Configuring only the latter leaves the server speaking `external_id` while
the OpenAPI document advertises `externalId`: the wire format and the first thing a reader
opens, quietly disagreeing. `Program.cs` sets the snake_case policy on both, and an endpoint
test pins the wire half by asserting the exact four field names come back.

## Development

Requires the .NET 8 SDK and a running Docker daemon (the tests start real containers).

```bash
dotnet test                                                    # 65 tests
docker compose -f compose.yaml -f compose.build.yaml up --build # run from source
```

Running the binary directly against your own PostgreSQL:

```bash
dotnet publish src/Whalebone.Records.Api -c Release -o ./out
Database__ConnectionString="Host=localhost;Database=your-db;Username=your-user;Password=your-password" \
  ./out/Whalebone.Records.Api
```

For repeated local runs, put it in user secrets rather than an environment variable -
it is stored outside the repository:

```bash
dotnet user-secrets --project src/Whalebone.Records.Api \
  set "Database:ConnectionString" "Host=localhost;Database=your-db;Username=your-user;Password=your-password"
```

### Configuration

Read by the service:

| Variable                        | Default         | Notes                                      |
| ------------------------------- | --------------- | ------------------------------------------ |
| `Database__ConnectionString`    | —               | Required; validated at startup, fails fast |
| `Database__MigrateOnStartup`    | `true`          | Migrations run under an advisory lock      |
| `Database__MaxRetryCount`       | `8`             | Transient-failure retries                  |
| `ASPNETCORE_URLS`               | `http://+:8080` |                                            |

Read by `compose.yaml`, which wires the two services together:

| Variable            | Default     |
| ------------------- | ----------- |
| `POSTGRES_DB`       | `whalebone` |
| `POSTGRES_USER`     | `whalebone` |
| `POSTGRES_PASSWORD` | `whalebone` |

```bash
POSTGRES_PASSWORD=something-else docker compose up
```

The defaults exist so the quickstart is one command. They belong to a database this
compose file creates itself, which publishes no host port and is reachable only from the
api container on a private network — so they are throwaway values, not credentials.

No connection string or password literal appears anywhere in `src/` — `appsettings.Development.json`
carries logging levels only, and a developer's own connection string belongs in user secrets. The
`Database__ConnectionString` validation message names the setting but carries no example
value: validation messages reach stderr and the log sink, and a credential-shaped literal
does not belong in either — least of all one that a later edit might quietly turn real.

### Tests

Two layers, because they answer different questions:

- **Endpoint tests** use `WebApplicationFactory` against a Testcontainers PostgreSQL. Only
  the connection string is overridden — not the registrations — so the real Npgsql provider,
  retry policy and options validation are all exercised. One container for the suite, with
  Respawn between tests.
- **End-to-end tests** (`EndToEnd/ContainerizedAppTests`) run the **production image**: real
  entrypoint, real environment-variable parsing, real PostgreSQL over a real Docker network,
  requests over a real TCP socket. `WebApplicationFactory` never binds a socket, so this is
  what actually proves the whole program works. CI points these at the image it just built,
  so the same code that proves the requirement also gates the publish.

## CI/CD

Every push to `main` runs build → tests → publish → verify. The image is published to GHCR
for `linux/amd64` and `linux/arm64`, tagged `latest` and `sha-<commit>`, and then pulled
back **anonymously** and exercised through the quickstart command above.

> **One manual step:** the GHCR package must be set to **Public** once, by hand, under
> Packages → `records-api` → Package settings. Until then `verify-published`
> fails on purpose, rather than letting a broken quickstart ship quietly.

## Deliberate omissions

| Not included            | Why                                                                       |
| ----------------------- | ------------------------------------------------------------------------- |
| A separate `Domain` project | One entity. Dependency inversion is already demonstrated by `IRecordRepository`. |
| AutoMapper              | Two mappings, both one line. A mapping library would cost more than it saves. |
| Generic repository      | One aggregate, three operations. The interface is smaller than the abstraction. |
| API versioning          | Two unversioned endpoints fixed by the spec.                               |
| Authentication          | Not in scope; the service is intended to sit behind an ingress.            |
| A `DELETE` endpoint     | Expands a two-endpoint brief. The erasure position, and the trigger to add one, are in [Data protection](#data-protection). |
| Second pipeline behaviour | Logging is already covered by the framework's request logging.            |
| Separate migration job  | Startup migration under an advisory lock is correct up to a few replicas. Past that, split it out. |
