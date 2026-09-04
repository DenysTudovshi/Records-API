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
| `GET`  | `/metrics`      | Prometheus text exposition format — **port `9090`**, not `8080`        |
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

Errors use Whalebone's own envelope, as `application/json`:

```json
{
  "message": "Request validation failed",
  "errors": [
    { "error": "MISSING_PARAM_VALUE", "error_code": 22, "message": "'name' is required.", "parameter": "name" },
    { "error": "INVALID_PARAM_VALUE", "error_code": 21, "message": "'email' is not a valid email address.", "parameter": "email" }
  ],
  "request_id": "0c209a31-12e6-4e6c-923a-6c5bb27ae769"
}
```

Every response carries an `X-Request-Id` header; error bodies repeat it as `request_id`.

A `500` uses the other shape the vendor publishes — flat, not wrapped, because there is no
parameter to name and nothing to put in an array:

```json
{ "error": "UNEXPECTED_ERROR", "error_code": 10, "message": "Unexpected error occurred.", "request_id": "…" }
```

## Matching Whalebone's conventions

Whalebone publishes two OpenAPI documents that need no credentials, and they settle what the
brief leaves open. Both answered `200` to an unauthenticated `GET` on 2026-09-02:

- <https://api.whalebone.io/whalebone/2/doc/openapi> — *Whalebone API*, ~110 KB
- <https://portal.whalebone.io/api/public/v1/doc/api-spec> — *Policy Config API*, ~27 KB

**The wire format is `snake_case`.** Counting every key in a `properties` map plus every declared
parameter name, across both documents: 62 names carry an underscore, against exactly two camelCase
(`createdAt`, `createdBy`). So `snake_case` it is, in the response body and in the OpenAPI document
alike.

**Correlation travels as `X-Request-Id`.** The published API returns one, as a UUID, so a caller
sitting in front of both services can correlate across them without a translation table.

**The OpenAPI document is served in every environment.** Both documents above are public and need
no key. The deliverable here is a container someone else runs, and an API explorer that only exists
in Development helps nobody.

**Personal data is marked with `x-wb-encrypt`.** The one vendor extension in either document,
applied at contract level. This service's OpenAPI document carries it too — see
[Data protection](#data-protection).

**Errors use the published envelope.** `{message, errors: [...]}` as `application/json`, each entry
carrying `error`, `error_code`, `message` and `parameter`. `error_code` is a stable integer, used
consistently across the published examples — `22` for `MISSING_PARAM_VALUE`, `21` for
`INVALID_PARAM_VALUE`, `10` for `UNEXPECTED_ERROR` — so a client branches on a number rather than
string-matching prose. Neither document mentions RFC 7807 anywhere, and this service does not emit
it.

**There are two error shapes, and this service implements both.** All twelve published operations
pin `400` to the envelope and `500`/`503` to a bare `{error, error_code, message}`. The split is
principled rather than accidental: a `400` can fail on several parameters at once and needs the
array, a `500` is a single failure with no parameter to name. An envelope everywhere would have
been tidier and wrong.

**The published status set is `200, 400, 401, 429, 500, 503`** — no `404` anywhere, and the envelope
marks neither member required. So a `404` here carries `message` alone: nothing about the request
was wrong, and there is no parameter to name. The same goes for a body that could not be read, and
for `405` and `415`.

**One field is deliberately omitted.** The `error` schema describes `value` as required when
`error` is `INVALID_PARAM_VALUE` — the rejected input, echoed back. Here that input is a name, an
email address or a date of birth, and an error body is among the least controlled things a service
emits: it reaches the caller, then their logs, then frequently a screenshot in a ticket. The
schema's `required` list is `[error, error_code, message]`, so omitting `value` stays schema-valid;
what it departs from is the prose, and a test pins the omission. `accepted_values` is absent for a
duller reason — no field in this contract is an enum.

## Data protection

Three of the four fields are personal data. That is not incidental to this service, it is the
entire payload. Whalebone mark personal data at contract level with `x-wb-encrypt` — their only
vendor extension — and this service's OpenAPI document carries the same one on `name`, `email` and
`date_of_birth`. Theirs sits on query parameters and this on schema properties, because that is
where each contract's personal data lives; the extension and its meaning are identical. A test pins
which three fields carry it, and that `external_id` does not.

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

**Error bodies never echo the rejected value.** That is the one field omitted from Whalebone's
error contract, and the reasoning is above. Two tests hold it: one posts an invalid email and
asserts the `400` names `email` without containing it, another that no entry ever carries `value`.

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

Served by `prometheus-net.AspNetCore`, because the OpenTelemetry Prometheus exporter has never
shipped a stable release — all 34 published versions are prerelease.

**The scrape has its own listener, on `9090`.** It publishes `process_*` internals and this service
has no auth, so serving it beside the API would hand those to anyone who can reach the API.
`compose.yaml` publishes both ports, so it is still one command to reach; on a real deployment
`9090` goes to the monitoring network. A test asserts `/metrics` answers `404` on the API port.

## Architecture

```
src/
  Whalebone.Records.Api             Minimal API endpoints, error envelope, health, Swagger
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
dotnet test                                                      # 91 tests
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
| `ASPNETCORE_URLS`            | `http://+:8080;http://+:9090` | 8080 the API, 9090 the scrape |

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
