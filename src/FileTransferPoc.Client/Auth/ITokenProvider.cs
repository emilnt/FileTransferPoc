namespace FileTransferPoc.Client.Auth;

/// <summary>Supplies bearer access tokens for calls to the document API.</summary>
public interface ITokenProvider
{
    /// <summary>Returns a valid access token, using a cached one when it has not expired.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Discards any cached token so the next call fetches a fresh one (used after a 401).</summary>
    void Invalidate();
}
