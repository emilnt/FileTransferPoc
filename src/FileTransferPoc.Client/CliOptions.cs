using FileTransferPoc.Client.Download;

namespace FileTransferPoc.Client;

/// <summary>Command line options. Each one can also come from an <c>ESAS_*</c> environment variable.</summary>
public sealed class CliOptions
{
    public const string Usage = """
        Usage: FileTransferPoc.Client <file-uri | guid> [options]

        Downloads one document from an ESAS HenteDokumentViaHttps endpoint, resuming
        from <out>.part if a previous run was interrupted.

        Options (environment variable in brackets):
          --out <path>             Where to save the file. Default: <guid> in the current folder.
          --base-url <url>         API base used when a bare GUID is given      [ESAS_BASE_URL]
                                   (default http://localhost:5005). The file URI becomes
                                   <base-url>/filer/v1/nedlasting/<guid>.
          --checksum <alg:value>   Expected checksum from the message, e.g. sha256:<hex>.
                                   Algorithms: sha256, sha384, sha512, sha1, md5. Hex or base64.
          --scope <scope>          Scope to request (default esas:filer)         [ESAS_SCOPE]
          --max-retries <n>        Retries for 500/503 and broken connections (default 6).
          --chunk-size <bytes>     Ask for this many bytes per request instead of the rest of the file.
          -h, --help               Show this help.

        Tokens, without --key (the API's Development stub):
          --consumer-org <orgno>   Organization to get a token for (default 123456789) [ESAS_CONSUMER_ORG]
          --token-endpoint <url>   Default <base-url>/maskinporten-stub/token     [ESAS_TOKEN_ENDPOINT]

        Tokens, with --key (real Maskinporten, JWT-bearer grant):
          --key <pem-file>         RSA private key that signs the assertion      [ESAS_KEY]
          --client-id <id>         Maskinporten client id (required)             [ESAS_CLIENT_ID]
          --kid <key-id>           Key id for the JWT header                     [ESAS_KID]
          --token-endpoint <url>   Default https://test.maskinporten.no/token    [ESAS_TOKEN_ENDPOINT]
          --audience <aud>         Assertion audience (default: token endpoint origin) [ESAS_AUDIENCE]
        """;

    public bool ShowHelp { get; private init; }
    public Uri FileUri { get; private init; } = null!;
    public string OutputPath { get; private init; } = null!;
    public Checksum? Checksum { get; private init; }
    public Uri TokenEndpoint { get; private init; } = null!;
    public string? Audience { get; private init; }
    public string? ClientId { get; private init; }
    public string ConsumerOrganization { get; private init; } = null!;
    public string Scope { get; private init; } = null!;
    public string? KeyPath { get; private init; }
    public string? KeyId { get; private init; }
    public int MaxRetries { get; private init; }
    public long? ChunkSize { get; private init; }

    public static CliOptions Parse(string[] args, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? target = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                return new CliOptions { ShowHelp = true };
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    throw new FormatException($"Missing value for {arg}.");
                }

                values[arg[2..]] = args[++i];
            }
            else if (target is null)
            {
                target = arg;
            }
            else
            {
                throw new FormatException($"Unexpected argument '{arg}'.");
            }
        }

        if (target is null)
        {
            throw new FormatException("Give a file URI or a GUID.");
        }

        string? Get(string name, string env) => values.GetValueOrDefault(name) ?? environment(env);

        var baseUrl = new Uri((Get("base-url", "ESAS_BASE_URL") ?? "http://localhost:5005").TrimEnd('/') + "/");
        Uri fileUri;
        Guid id;
        if (Guid.TryParse(target, out id))
        {
            fileUri = new Uri(baseUrl, $"filer/v1/nedlasting/{id}");
        }
        else if (Uri.TryCreate(target, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
        {
            // The spec requires the URI to end with the document's GUID.
            if (!Guid.TryParse(parsed.Segments[^1].TrimEnd('/'), out id))
            {
                throw new FormatException($"'{target}' does not end with a GUID.");
            }

            fileUri = parsed;
        }
        else
        {
            throw new FormatException($"'{target}' is neither a GUID nor an http(s) URI.");
        }

        var keyPath = Get("key", "ESAS_KEY");
        var clientId = Get("client-id", "ESAS_CLIENT_ID");
        if (keyPath is not null && clientId is null)
        {
            throw new FormatException("--client-id is required with --key.");
        }

        var tokenEndpoint = Get("token-endpoint", "ESAS_TOKEN_ENDPOINT") is { } te ? new Uri(te)
            : keyPath is not null ? new Uri("https://test.maskinporten.no/token")
            : new Uri(baseUrl, "maskinporten-stub/token");
        var consumerOrganization = Get("consumer-org", "ESAS_CONSUMER_ORG") ?? "123456789";
        if (consumerOrganization.Length != 9 || !consumerOrganization.All(char.IsAsciiDigit))
        {
            throw new FormatException("--consumer-org must be a 9 digit organization number.");
        }

        return new CliOptions
        {
            FileUri = fileUri,
            OutputPath = values.GetValueOrDefault("out") ?? id.ToString(),
            Checksum = values.TryGetValue("checksum", out var c) ? Checksum.Parse(c) : null,
            TokenEndpoint = tokenEndpoint,
            Audience = Get("audience", "ESAS_AUDIENCE"),
            ClientId = clientId,
            ConsumerOrganization = consumerOrganization,
            Scope = Get("scope", "ESAS_SCOPE") ?? "esas:filer",
            KeyPath = keyPath,
            KeyId = Get("kid", "ESAS_KID"),
            MaxRetries = ParseNumber(values.GetValueOrDefault("max-retries"), "max-retries") is { } n ? (int)n : 6,
            ChunkSize = ParseNumber(values.GetValueOrDefault("chunk-size"), "chunk-size"),
        };
    }

    private static long? ParseNumber(string? value, string name) =>
        value is null ? null
        : long.TryParse(value, out var n) && n > 0 ? n
        : throw new FormatException($"--{name} must be a positive number.");
}
