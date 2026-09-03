# Records API

[![CI](https://github.com/DenysTudovshi/Records-API/actions/workflows/ci.yml/badge.svg)](https://github.com/DenysTudovshi/Records-API/actions/workflows/ci.yml)

A .NET 8 service that stores and retrieves person records over a JSON REST API, backed by
PostgreSQL.

## Quickstart

No clone, no .NET SDK — just Docker:

```bash
curl -fsSL https://raw.githubusercontent.com/DenysTudovshi/Records-API/main/compose.yaml | docker compose -f - up
```

This pulls the image from the last green build of `main`, starts PostgreSQL alongside it, and
serves the API on <http://localhost:8080> (Swagger UI at `/swagger`). CI runs this exact command
against the published image on every push to `main`, with all registry credentials dropped
first — so a broken quickstart is a red build, not a surprise for the reader.

```bash
curl -i -X POST http://localhost:8080/save \
  -H 'Content-Type: application/json' \
  -d '{"external_id":"3fa85f64-5717-4562-b3fc-2c963f66afa6","name":"some name","email":"email@email.com","date_of_birth":"2020-01-01T12:12:34+00:00"}'

curl http://localhost:8080/3fa85f64-5717-4562-b3fc-2c963f66afa6
```

## API

| Method | Route           | Result                                                               |
| ------ | --------------- | -------------------------------------------------------------------- |
| `POST` | `/save`         | `201` with `Location: /{external_id}`, or `200` if it already existed |
| `GET`  | `/{id}`         | `200` with the record, `404` if unknown                               |
| `GET`  | `/metrics`      | Prometheus text exposition format                                     |
| `GET`  | `/health/live`  | Process liveness                                                      |
| `GET`  | `/health/ready` | Readiness, including database reachability                            |

Both record endpoints speak this shape, in `snake_case`:

```json
{
  "external_id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "some name",
  "email": "email@email.com",
  "date_of_birth": "2020-01-01T12:12:34+00:00"
}
```

Errors are RFC 7807 `application/problem+json`. Validation failures return `400` with an `errors`
object keyed by the wire field name (`date_of_birth`, not `DateOfBirth`) — a missing field and a
malformed one alike. Every response carries an `X-Request-Id` header, repeated in the problem body
as `request_id`.

## Matching Whalebone's conventions

Whalebone publishes two OpenAPI documents that need no credentials, and they settle what the
brief leaves open. Both answered `200` to an unauthenticated `GET` on 2026-09-02:

- <https://api.whalebone.io/whalebone/2/doc/openapi> — *Whalebone API*, ~110 KB
- <https://portal.whalebone.io/api/public/v1/doc/api-spec> — *Policy Config API*, ~27 KB

**`snake_case` is theirs.** Counting every key in a `properties` map plus every declared
parameter name, across both documents: 62 names carry an underscore, against exactly two
camelCase (`createdAt`, `createdBy`). So the wire format here is `snake_case`, in the response body
and in the OpenAPI document alike.

**`X-Request-Id` is theirs.** Their API returns one, as a UUID, so a caller sitting in front of
both can correlate across them without a translation table.

**Serving the OpenAPI document in every environment is theirs.** Both documents above are public
and need no key. The deliverable here is a container someone else runs, and an API explorer that
only exists in Development helps nobody.

**RFC 7807 is a considered divergence.** Their error envelope is `{message, errors: [...]}`, each
item carrying `error`, `error_code`, `message`, `parameter`, `value` and `accepted_values`. In one
respect that shape is better than problem+json: `error_code` is a stable integer — `21` for
`INVALID_PARAM_VALUE`, `22` for `MISSING_PARAM_VALUE` — so a client branches on a number instead
of string-matching prose. Neither document mentions RFC 7807 anywhere.

This service diverges anyway. `application/problem+json` is the registered media type, so a
generic client can tell the body is an error before parsing it, and ASP.NET Core produces
per-field `ValidationProblemDetails` out of the framework. Matching their envelope exactly would
mean hand-rolling and testing a bespoke shape, in a service whose contract is four fields, to gain
nothing a caller of *this* API can use. Both the media type and the JSON shape sit at the edge and
are trivially replaceable if this ever had to sit behind the same gateway.

**One divergence inside the divergence.** Their `errors[]` items echo the rejected input back as
`value`. This service names the field and stops there — see below.

## Data protection

Three of the four fields are personal data. That is not incidental to this service, it is the
entire payload, and Whalebone marks personal data at contract level in their own API with an
`x-wb-encrypt` extension, so the handling is stated rather than left to be inferred.

| Field | Where it appears | Where it never appears |
| ----- | ---------------- | ---------------------- |
| `name` | Request, response, the `name` column | Logs, metric labels, error bodies |
| `email` | Request, response, the `email` column | Logs, metric labels, error bodies |
| `date_of_birth` | Request, response, two columns | Logs, metric labels, error bodies |
| `external_id` | Request, response, URL path, the framework's `RequestPath` scope | Metric labels |

**Proved by test, not by assertion.** `PersonalDataTests` captures every log line the host writes
and asserts that none carries the name, the email or the date of birth — in the message, in a
structured value, or in an enclosing scope. Guards run first so the assertion can never pass by
measuring an empty list, which is the failure worth fearing.

The test was itself verified the only way this kind of test can be: by adding a
`LogInformation("Saved {Name} {Email}", ...)` to the save handler, watching it fail and name the
offending line, then removing it. A separate test asserts EF Core's `EnableSensitiveDataLogging` is
off — it is off by default, and the default is one line away from changing.

**Error bodies never echo the rejected value.** The rejected value here is somebody's name, email
or date of birth, and an error body is among the least controlled things a service emits: it
reaches the caller, then their logs, then frequently a screenshot in a ticket. A test posts an
invalid email and asserts the `400` names `email` without containing it.

**Retention.** A row lives until something deletes it. No TTL, no soft delete, and no second copy
anywhere — no cache, no outbox, no event stream, no log line, no metric series. An erasure request
is one `DELETE FROM person_records WHERE external_id = ...` plus whatever backup retention adds,
rather than an archaeology exercise across systems that each kept their own copy. There is no
`DELETE` endpoint deliberately — see [omissions](#deliberate-omissions).

## Observability

**Correlation id.** Every response carries `X-Request-Id` — error responses included — and every
log line written while handling that request carries it under `CorrelationId`. A caller who
supplies their own gets it echoed, so a trace that began upstream continues here instead of
restarting. An inbound value is honoured only if it could plausibly be a request id — exactly one
header, at most 64 characters, drawn from `[A-Za-z0-9._:-]` — because that value reaches a response
header *and* every log line, and echoing arbitrary caller-supplied bytes into both is how a
correlation id turns into a log-injection vector.

Console logging is JSON with scopes included: this ships as a container, and unstructured console
text costs a field-by-field parse before anything can query it.

**Metrics.** `GET /metrics` serves the Prometheus text exposition format. It publishes ASP.NET
Core's own instruments rather than hand-rolled counters — the framework already records
`http.server.request.duration` and `http.server.active_requests` for every route, including the
ones nobody remembered to instrument, and a parallel set of counters would only be a second and
staler copy.

**No series is ever labelled with `external_id`** — that would be unbounded cardinality and
personal data in a single move. The route label is the route *template* (`/{id:guid}`, never
`/3fa85f64-...`), and a request that matched no route carries no route label at all, so a caller
cannot mint series by hammering random paths. A test asserts the scrape contains no UUID-shaped
token and no label named `external_id`, `name`, `email` or `date_of_birth`.

`prometheus-net.AspNetCore` was chosen over the OpenTelemetry Prometheus exporter, which has never
shipped a stable release — all 34 published versions are prerelease. Stated plainly, the cost is a
package with no commits since January 2024, targeting `net6.0`, whose bridged metric names diverge
from the OpenTelemetry convention (hence `microsoft_aspnetcore_hosting_...`). Swapping touches two
files.

**`/metrics` is on the main port, and in production it usually should not be.** A scrape endpoint
normally gets its own listener or a network policy; here it also publishes `process_*` internals to
anyone who can reach the API. It is on 8080 because the deliverable is a one-line quickstart, and
an endpoint a reviewer cannot reach is one they have to take on trust. The production position is
the second port.

## Architecture

```
src/
  Whalebone.Records.Api             Minimal API endpoints, RFC 7807 handler, health, Swagger
  Whalebone.Records.Application     Commands, queries, validators, domain — no ASP.NET, no EF
  Whalebone.Records.Infrastructure  EF Core 8 + Npgsql, migrations, repository
tests/
  Whalebone.Records.UnitTests        Domain and validation rules
  Whalebone.Records.IntegrationTests Endpoints against real PostgreSQL, plus end-to-end
```

`Application` references neither ASP.NET Core nor EF Core, which is the point of the split: it
declares `IRecordRepository` and `Infrastructure` implements it, so the dependency arrow points
inward. Endpoints are registered through an `IEndpoint` interface with a `static abstract Map`, so
a missing registration is a compile error rather than a missing route.

**Why MediatR, and what it costs.** Between an HTTP route and a `SELECT` sits a reflection-based
dispatcher: one indirection hop, one package, and assembly scanning at startup. What buys it is a
single open pipeline behaviour — `ValidationBehavior` gives both endpoints identical validation
with zero per-endpoint wiring, with the rules declared beside the command they guard, in a project
that references neither ASP.NET Core nor EF Core. The alternative is a few lines per endpoint that
must not be forgotten, and forgetting them fails silently, by accepting bad input.

At one behaviour and two endpoints this is close to break-even, and an endpoint filter reaches the
same place with no package. If a second behaviour never arrives, direct handler injection behind a
filter is the cheaper shape — and `Application` barely changes either way, since the commands and
their validators are already the contract.

**Dependency pins.** Two packages are pinned to their last open-source release, *exactly* rather
than as a floor, so a restore cannot silently cross a licence boundary:

| Package            | Pin        | What the next version changed                            |
| ------------------ | ---------- | -------------------------------------------------------- |
| `MediatR`          | `[12.5.0]` | 13.0.0 relicensed from Apache-2.0 to a commercial licence |
| `FluentAssertions` | `[7.2.2]`  | 8.0.0 moved to an Xceed commercial licence                |

The brackets are the point: `12.5.0` in NuGet means *12.5.0 or newer*, which is exactly how a
transitive bump walks a build across a licence change with nobody reading a diff.

## Development

Requires the .NET 8 SDK and a running Docker daemon (the tests start real containers).

```bash
dotnet test                                                      # 78 tests
docker compose -f compose.yaml -f compose.build.yaml up --build   # run from source
```

Running the binary directly against your own PostgreSQL:

```bash
dotnet publish src/Whalebone.Records.Api -c Release -o ./out
Database__ConnectionString="Host=localhost;Database=your-db;Username=your-user;Password=your-password" \
  ./out/Whalebone.Records.Api
```

For repeated local runs, prefer user secrets — stored outside the repository:

```bash
dotnet user-secrets --project src/Whalebone.Records.Api \
  set "Database:ConnectionString" "Host=localhost;Database=your-db;Username=your-user;Password=your-password"
```

### Configuration

| Variable                     | Default         | Notes                                      |
| ---------------------------- | --------------- | ------------------------------------------ |
| `Database__ConnectionString` | —               | Required; validated at startup, fails fast |
| `Database__MigrateOnStartup` | `true`          | Migrations run under an advisory lock      |
| `Database__MaxRetryCount`    | `8`             | Transient-failure retries                  |
| `ASPNETCORE_URLS`            | `http://+:8080` |                                            |

`compose.yaml` additionally reads `POSTGRES_DB`, `POSTGRES_USER` and `POSTGRES_PASSWORD`, all
defaulting to `whalebone`. Those defaults exist so the quickstart is one command; they belong to a
database the compose file creates itself, which publishes no host port and is reachable only from
the api container on a private network — throwaway values, not credentials.

```bash
POSTGRES_PASSWORD=something-else docker compose up
```

No connection string or password literal appears anywhere in `src/` — not even in the
`Database__ConnectionString` validation message, which names the setting but gives no example.
That message reaches stderr and the log sink, and a placeholder in either is one edit away from
being real.

### Tests

Two layers, because they answer different questions:

- **Endpoint tests** use `WebApplicationFactory` against a Testcontainers PostgreSQL. Only the
  connection string is overridden — not the registrations — so the real Npgsql provider, retry
  policy and options validation are all exercised. One container for the suite, Respawn between
  tests.
- **End-to-end tests** run the **production image**: real entrypoint, real environment-variable
  parsing, real PostgreSQL over a real Docker network, requests over a real TCP socket.
  `WebApplicationFactory` never binds a socket, so this is what actually proves the whole program
  works. CI points these at the image it just built, so the same code that proves the requirement
  also gates the publish.

## CI/CD

Every push to `main` runs build → tests → publish → verify. The image is published to GHCR for
`linux/amd64` and `linux/arm64`, tagged `latest` and `sha-<commit>`, then pulled back
**anonymously** and exercised through the quickstart command above.

> **One manual step:** the GHCR package must be set to **Public** once, by hand, under Packages →
> `records-api` → Package settings. Until then `verify-published` fails on purpose, rather than
> letting a broken quickstart ship quietly.

## Deliberate omissions

| Not included                | Why                                                                             |
| --------------------------- | ------------------------------------------------------------------------------- |
| A separate `Domain` project | One entity. Dependency inversion is already shown by `IRecordRepository`.        |
| AutoMapper                  | Two mappings, both one line.                                                     |
| Generic repository          | One aggregate, three operations. The interface is smaller than the abstraction.  |
| API versioning              | Two unversioned endpoints fixed by the brief.                                    |
| Authentication              | Not in scope; the service is intended to sit behind an ingress.                  |
| Rate limiting               | Belongs at the ingress that terminates TLS. Whalebone's own API returns `X-RateLimit-*` headers, so the budget is an edge concern, not a per-service one. |
| A `DELETE` endpoint         | Expands a two-endpoint brief. Erasure is served by the operator against the database; the trigger to add one is the first request that has to be actioned by somebody without a database session. |
| Second pipeline behaviour   | Logging is already covered by the framework's request logging.                   |
| Separate migration job      | Startup migration under an advisory lock is correct up to a few replicas. Past that, split it out. |
