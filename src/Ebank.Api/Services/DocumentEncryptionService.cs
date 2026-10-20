using System.Security.Cryptography;

namespace Ebank.Api.Services;

public sealed class DocumentEncryptionService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public DocumentEncryptionService(IConfiguration configuration)
    {
        var configuredKey = configuration["Kyc:DocumentEncryptionKey"]
            ?? throw new InvalidOperationException(
                "Kyc:DocumentEncryptionKey is missing. For local development, add it to .NET User Secrets: " +
                "dotnet user-secrets set \"Kyc:DocumentEncryptionKey\" \"<base64-32-byte-key>\" " +
                "--project src/Ebank.Api. You can generate a key with `openssl rand -base64 32`.");

        try
        {
            _key = Convert.FromBase64String(configuredKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "Kyc:DocumentEncryptionKey must be a Base64-encoded 32-byte key.",
                exception);
        }

        if (_key.Length != 32)
        {
            throw new InvalidOperationException(
                "Kyc:DocumentEncryptionKey must be a Base64-encoded 32-byte key.");
        }
    }

    public EncryptedDocument Encrypt(ReadOnlySpan<byte> content, string subject, string documentType)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[content.Length];
        var tag = new byte[TagSize];
        var associatedData = System.Text.Encoding.UTF8.GetBytes($"{subject}:{documentType}");

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, content, ciphertext, tag, associatedData);

        return new EncryptedDocument(ciphertext, nonce, tag);
    }
}

public sealed record EncryptedDocument(byte[] Content, byte[] Nonce, byte[] AuthenticationTag);
