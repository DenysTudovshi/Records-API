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

- **`{id}` is the `external_id`.** The response contract has no other identifier, and the
  client already knows `external_id` at `POST` time. A surrogate key exists in the table
  but never appears on the wire. The spec is ambiguous here; this is the reading taken.
- **`POST /save` is an upsert.** It is `save`, not `create`: idempotent on `external_id`,
  so a retry after a network blip converges rather than failing. Concurrent duplicates
  race the unique index; the loser catches PostgreSQL `23505` and converges on the update.
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
missing registration is a compile error rather than a missing route, and startup does no
assembly scanning. MediatR carries one pipeline behaviour — `ValidationBehavior` — which is
what earns it its place: validation is declared once beside each command and applies to
every use case without per-endpoint wiring.

## Development

Requires the .NET 8 SDK and a running Docker daemon (the tests start real containers).

```bash
dotnet test                                                    # 44 tests
docker compose -f compose.yaml -f compose.build.yaml up --build # run from source
```

Running the binary directly against your own PostgreSQL:

```bash
dotnet publish src/Whalebone.Records.Api -c Release -o ./out
Database__ConnectionString="Host=localhost;Port=5432;Database=whalebone;Username=whalebone;Password=whalebone" \
  ./out/Whalebone.Records.Api
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

Dependencies are pinned to their last open-source releases on purpose: **MediatR 12.5.0**
(13.0.0 moved to a commercial licence) and **FluentAssertions 7.2.2** (8.0.0 moved to an
Xceed commercial licence). Both pins are exact — `[12.5.0]`, `[7.2.2]` — so a restore can
never silently cross that boundary.
