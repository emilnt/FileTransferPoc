# FileTransferPoc

Proof of concept for [ESAS RFC: Hente dokument via HTTPS](https://github.com/domstolene/ESAS/blob/main/rfc/HenteDokumentViaHttps.md),
built on .NET 10.

## Layout

| Path | What |
| :--- | :--- |
| `src/FileTransferPoc.Api` | ASP.NET Core Web API serving `GET /filer/v1/nedlasting/{guid}` |
| `tests/FileTransferPoc.Api.Tests` | Unit tests for range handling and integration tests for every status code |

## Run it

```sh
dotnet run --project src/FileTransferPoc.Api --launch-profile http
```

In Development the API seeds two sample files in service `TestDomain` for organization `123456789` and exposes a Maskinporten stand-in:

```sh
TOKEN=$(curl -s -d consumer_org=123456789 -d scope=esas:filer \
  http://localhost:5005/maskinporten-stub/token | jq -r .access_token)

# Whole file (200)
curl -H "Authorization: Bearer $TOKEN" \
  http://localhost:5005/filer/v1/nedlasting/550e8400-e29b-41d4-a716-446655440000

# 10 MiB file, first MiB (206)
curl -H "Authorization: Bearer $TOKEN" -H "Range: bytes=0-1048575" -o part.bin \
  http://localhost:5005/filer/v1/nedlasting/6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a5b
```

## Behaviour

| Status | When |
| :--- | :--- |
| 200 | No `Range`, a `Range` the server ignores (malformed or multi-part, per RFC 9110), or a range from byte 0 reaching past the end of the file (ESAS rule) |
| 206 | A single satisfiable range. `Content-Range: bytes start-end/total`. A range starting inside the file but ending past it is clamped to the end, so resumed downloads work |
| 400 | The identifier is not a GUID in `D` format (`00000000-0000-0000-0000-000000000000`) |
| 401 | Missing, expired or invalid bearer token |
| 403 | Token lacks the required scope (`Maskinporten:RequiredScope`), or the calling organization is not on the file's list |
| 404 | No record for the GUID, the bytes are gone, or the file is older than `Storage:RetentionDays` (default 15) |
| 405 | Any method other than GET (`Allow: GET`) |
| 416 | The range starts at or after the end of the file. `Content-Range: bytes */total` |
| 500 | Unexpected error, answered as `application/problem+json` |
| 503 | `Service:MaintenanceMode` is on, or more than `Service:MaxConcurrentDownloads` downloads are running. Sends `Retry-After` |

Responses are streamed from disk with `SendFileAsync`, so files up to 5 GB are never buffered in memory.
The GUID doubles as a strong `ETag`, since the bytes behind a GUID never change.

### Logging

Every request is logged with method, path, source IP, organization number (from Maskinporten's `consumer` claim),
`client_id`, requested range, status and duration, including requests rejected with 401/403/503.
Behind a reverse proxy, list it under `ForwardedHeaders:KnownProxies` so the logged IP is the caller's.

### Storage

`LocalFileStore` keeps bytes under `{Storage:RootPath}/files/` and one JSON record per GUID under
`{Storage:RootPath}/metadata/{guid}.json`:

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "serviceName": "TestDomain",
  "filePath": "550e8400-e29b-41d4-a716-446655440000",
  "allowedOrganizations": ["123456789"],
  "publishedAt": "2026-10-11T19:00:00+00:00"
}
```

### Authentication

The API validates Maskinporten-style JWTs: issuer, lifetime, signature, and the scope in `Maskinporten:RequiredScope`
(default `esas:filer`). The calling organization comes from `consumer.ID` (`0192:<orgno>`).

- **POC / tests:** `Maskinporten:SigningKey` is a shared HMAC key. With `Maskinporten:EnableStubIssuer` on (Development only),
  `POST /maskinporten-stub/token` with form fields `consumer_org` and `scope` returns a token shaped like Maskinporten's.
- **Real Maskinporten:** set `Maskinporten:Authority` (for example `https://test.maskinporten.no/`) and `Maskinporten:Issuer`;
  signing keys then come from Maskinporten's metadata.
