# FileTransferPoc.Client

Console client for the ESAS [HenteDokumentViaHttps](https://github.com/domstolene/ESAS/blob/main/rfc/HenteDokumentViaHttps.md) spec.

With the API running in Development (`dotnet run --project src/FileTransferPoc.Api --launch-profile http`):

```sh
dotnet run --project src/FileTransferPoc.Client -- \
  6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a5b \
  --checksum sha256:38396e6dcf327c8bb3bdfe4c78cee671ac155ccad87016bbebf7d32ecb659f04 \
  --out large.bin
```

That fetches the API's seeded 10 MiB sample from `http://localhost:5005` as organization `123456789`.
Run `--help` for every option.

What it does:

- **Auth**: without `--key` it asks the API's Development stub (`POST /maskinporten-stub/token` with `consumer_org` and `scope`).
  With `--key` and `--client-id` it uses real Maskinporten's JWT-bearer grant: a signed RS256 assertion with
  `iss`/`aud`/`scope`/`iat`/`exp`/`jti`, sent to `--token-endpoint` (default `https://test.maskinporten.no/token`).
  Either way the token is cached until 30s before expiry, and a 401 triggers one refresh. Scope defaults to `esas:filer`.
- **Resume**: bytes go to `<out>.part`. A dropped connection, or a new run of the same command, continues with
  `Range: bytes=<part length>-`. A 200 to a ranged request restarts from zero; a 416 with `bytes */N` when the part
  already holds N bytes counts as done. `--chunk-size` asks for fixed-size ranges instead.
- **Retries**: 500 and 503 (plus 502, 504, 429) and broken connections are retried with exponential backoff
  (1s, 2s, 4s… ±20% jitter, capped at 60s, `Retry-After` honoured), up to `--max-retries` consecutive failures.
  Other statuses fail at once with the spec's explanation.
- **Integrity**: `--checksum alg:value` (sha256 default; sha384, sha512, sha1, md5; hex or base64) is checked before
  the file is moved into place. A mismatch deletes the partial file.

Exit codes: 0 success, 1 download or token error, 2 bad arguments, 130 cancelled (rerun to resume).
