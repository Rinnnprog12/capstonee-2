using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using TsuOrg.Application.Common;

namespace TsuOrg.Infrastructure.Services;

public sealed class BlobService : IBlobService
{
    // Well-known Azurite / Storage Emulator account key
    private const string AzuriteAccountName = "devstoreaccount1";
    private const string AzuriteAccountKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly BlobServiceClient _client;
    private readonly string _container;
    private readonly StorageSharedKeyCredential _credential;

    public BlobService(IConfiguration config)
    {
        var connStr = config["AzureBlob:ConnectionString"] ?? "UseDevelopmentStorage=true";
        _container  = config["AzureBlob:Container"] ?? "documents";
        _client     = new BlobServiceClient(connStr);
        _credential = ResolveCredential(connStr);
    }

    public async Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct);
        var blobName  = BuildBlobName(fileName);
        var blob      = container.GetBlobClient(blobName);

        await blob.UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        }, ct);

        return blobName;
    }

    public async Task<string> GetReadUrlAsync(string blobPath, TimeSpan expiry, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct);
        var blob      = container.GetBlobClient(blobPath);

        // Always mint a SAS — Azurite supports SharedKey SAS with the well-known key.
        // PublicAccessType.None containers require a SAS for ML httpx downloads.
        var sasBuilder = new BlobSasBuilder(BlobSasPermissions.Read, DateTimeOffset.UtcNow.Add(expiry))
        {
            BlobContainerName = _container,
            BlobName          = blobPath,
            Resource          = "b",
        };

        var sas = sasBuilder.ToSasQueryParameters(_credential).ToString();
        return $"{blob.Uri}?{sas}";
    }

    public async Task DeleteAsync(string blobPath, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct);
        await container.GetBlobClient(blobPath).DeleteIfExistsAsync(cancellationToken: ct);
    }

    public async Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct);
        var blob      = container.GetBlobClient(blobPath);
        var response  = await blob.DownloadStreamingAsync(cancellationToken: ct);
        return response.Value.Content;
    }

    private async Task<BlobContainerClient> GetContainerAsync(CancellationToken ct)
    {
        var container = _client.GetBlobContainerClient(_container);
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
        return container;
    }

    private static string BuildBlobName(string fileName)
    {
        var now  = DateTime.UtcNow;
        var safe = Path.GetFileName(fileName)
            .Replace(" ", "_")
            .Replace("..", "");
        return $"{now.Year}/{now.Month:D2}/{Guid.NewGuid()}/{safe}";
    }

    private StorageSharedKeyCredential ResolveCredential(string connStr)
    {
        if (IsLocalEmulator(connStr))
            return new StorageSharedKeyCredential(AzuriteAccountName, AzuriteAccountKey);

        // Parse AccountName / AccountKey from connection string
        string? name = null, key = null;
        foreach (var part in connStr.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0].Equals("AccountName", StringComparison.OrdinalIgnoreCase)) name = kv[1];
            if (kv[0].Equals("AccountKey",  StringComparison.OrdinalIgnoreCase)) key  = kv[1];
        }

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(key))
            throw new InvalidOperationException(
                "AzureBlob:ConnectionString must include AccountName and AccountKey for SAS generation.");

        return new StorageSharedKeyCredential(name, key);
    }

    private static bool IsLocalEmulator(string connStr) =>
        connStr.Contains("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase)
        || connStr.Contains("devstoreaccount1", StringComparison.OrdinalIgnoreCase)
        || connStr.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);
}
