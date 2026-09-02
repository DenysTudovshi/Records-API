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
dotnet test                                                    # 44 tests
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
| Second pipeline behaviour | Logging is already covered by the framework's request logging.            |
| Separate migration job  | Startup migration under an advisory lock is correct up to a few replicas. Past that, split it out. |
