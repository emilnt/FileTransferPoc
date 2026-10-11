# FileTransferPoc.Client

Console client for the ESAS [HenteDokumentViaHttps](https://github.com/domstolene/ESAS/blob/main/rfc/HenteDokumentViaHttps.md) spec.

```sh
dotnet run --project src/FileTransferPoc.Client -- \
  550e8400-e29b-41d4-a716-446655440000 \
  --base-url http://localhost:5000 \
  --checksum sha256:<hex from the message> \
  --out document.pdf
```

What it does:

- **Auth**: gets a token with the Maskinporten JWT-bearer grant (signed RS256 assertion, `iss`/`aud`/`scope`/`iat`/`exp`/`jti`)
  from `--token-endpoint` (defaults to `<base-url>/token`, the PoC's stub issuer). Tokens are cached until 30s before expiry,
  and a 401 triggers one refresh. Without `--key` a throwaway key signs the assertion, which only the stub accepts.
- **Resume**: bytes go to `<out>.part`. A dropped connection, or a new run of the same command, continues with
  `Range: bytes=<part length>-`. A 200 to a ranged request restarts from zero; a 416 with `bytes */N` when the part
  already holds N bytes counts as done. `--chunk-size` asks for fixed-size ranges instead.
- **Retries**: 500 and 503 (plus 502, 504, 429) and broken connections are retried with exponential backoff
  (1s, 2s, 4s… ±20% jitter, capped at 60s, `Retry-After` honoured), up to `--max-retries` consecutive failures.
  Other statuses fail at once with the spec's explanation.
- **Integrity**: `--checksum alg:value` (sha256 default; sha384, sha512, sha1, md5; hex or base64) is checked before
  the file is moved into place. A mismatch deletes the partial file.

Exit codes: 0 success, 1 download or token error, 2 bad arguments, 130 cancelled (rerun to resume).
