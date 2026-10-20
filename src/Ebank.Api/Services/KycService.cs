using Ebank.Api.Data;
using Ebank.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Ebank.Api.Services;

public sealed class KycService(
    EbankDbContext dbContext,
    DocumentEncryptionService documentEncryptionService) : IKycService
{
    public async Task<KycProfileResponse> GetAsync(
        string subject,
        CancellationToken cancellationToken)
    {
        var profile = await dbContext.KycProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Subject == subject, cancellationToken);

        return profile is null
            ? EmptyProfile()
            : await MapAsync(profile, cancellationToken);
    }

    public async Task<KycProfileResponse> UpdateProfileAsync(
        string subject,
        UpdateKycProfileRequest request,
        CancellationToken cancellationToken)
    {
        var profile = await GetOrCreateAsync(subject, cancellationToken);
        profile.FirstName = request.FirstName.Trim();
        profile.LastName = request.LastName.Trim();
        profile.PhoneNumber = request.PhoneNumber.Trim();
        profile.AddressLine1 = request.AddressLine1.Trim();
        profile.AddressLine2 = request.AddressLine2?.Trim() ?? string.Empty;
        profile.City = request.City.Trim();
        profile.PostalCode = request.PostalCode.Trim();
        profile.Country = request.Country.Trim().ToUpperInvariant();
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        var documentTypes = await GetDocumentTypesAsync(subject, cancellationToken);
        UpdateStatus(
            profile,
            documentTypes.Contains(KycDocumentType.IdentityDocument),
            documentTypes.Contains(KycDocumentType.AddressProof));

        await dbContext.SaveChangesAsync(cancellationToken);
        return await MapAsync(profile, cancellationToken);
    }

    public async Task<KycProfileResponse> UploadDocumentAsync(
        string subject,
        KycDocumentType type,
        string mediaType,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var profile = await GetOrCreateAsync(subject, cancellationToken);
        var encrypted = documentEncryptionService.Encrypt(content, subject, type.ToString());
        var existingId = await dbContext.KycDocuments
            .Where(document => document.Subject == subject && document.Type == type)
            .Select(document => (Guid?)document.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (existingId is null)
        {
            dbContext.KycDocuments.Add(new KycDocument
            {
                Id = Guid.NewGuid(),
                Subject = subject,
                Type = type,
                MediaType = mediaType,
                EncryptedContent = encrypted.Content,
                Nonce = encrypted.Nonce,
                AuthenticationTag = encrypted.AuthenticationTag,
                UploadedAt = DateTimeOffset.UtcNow,
                Profile = profile
            });
        }
        else
        {
            var existing = new KycDocument
            {
                Id = existingId.Value,
                Subject = subject,
                Type = type,
                MediaType = mediaType,
                EncryptedContent = encrypted.Content,
                Nonce = encrypted.Nonce,
                AuthenticationTag = encrypted.AuthenticationTag,
                UploadedAt = DateTimeOffset.UtcNow
            };
            dbContext.KycDocuments.Attach(existing);
            var entry = dbContext.Entry(existing);
            entry.Property(document => document.MediaType).IsModified = true;
            entry.Property(document => document.EncryptedContent).IsModified = true;
            entry.Property(document => document.Nonce).IsModified = true;
            entry.Property(document => document.AuthenticationTag).IsModified = true;
            entry.Property(document => document.UploadedAt).IsModified = true;
        }

        profile.UpdatedAt = DateTimeOffset.UtcNow;
        var documentTypes = await GetDocumentTypesAsync(subject, cancellationToken);
        UpdateStatus(
            profile,
            documentTypes.Contains(KycDocumentType.IdentityDocument) ||
                type == KycDocumentType.IdentityDocument,
            documentTypes.Contains(KycDocumentType.AddressProof) ||
                type == KycDocumentType.AddressProof);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await MapAsync(profile, cancellationToken);
    }

    private async Task<KycProfile> GetOrCreateAsync(
        string subject,
        CancellationToken cancellationToken)
    {
        var profile = await dbContext.KycProfiles
            .SingleOrDefaultAsync(candidate => candidate.Subject == subject, cancellationToken);

        if (profile is not null)
        {
            return profile;
        }

        profile = new KycProfile
        {
            Subject = subject,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        dbContext.KycProfiles.Add(profile);
        return profile;
    }

    private async Task<HashSet<KycDocumentType>> GetDocumentTypesAsync(
        string subject,
        CancellationToken cancellationToken) =>
        (await dbContext.KycDocuments
            .AsNoTracking()
            .Where(document => document.Subject == subject)
            .Select(document => document.Type)
            .ToListAsync(cancellationToken))
        .ToHashSet();

    private static void UpdateStatus(
        KycProfile profile,
        bool hasIdentityDocument,
        bool hasAddressProof)
    {
        var hasRequiredDetails =
            !string.IsNullOrWhiteSpace(profile.FirstName) &&
            !string.IsNullOrWhiteSpace(profile.LastName) &&
            !string.IsNullOrWhiteSpace(profile.PhoneNumber) &&
            !string.IsNullOrWhiteSpace(profile.AddressLine1) &&
            !string.IsNullOrWhiteSpace(profile.City) &&
            !string.IsNullOrWhiteSpace(profile.PostalCode) &&
            !string.IsNullOrWhiteSpace(profile.Country);

        profile.Status = hasIdentityDocument && hasAddressProof && hasRequiredDetails
            ? KycStatus.PendingReview
            : KycStatus.Incomplete;
    }

    private static KycProfileResponse EmptyProfile() => new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        KycStatus.Incomplete,
        null,
        null,
        null);

    private async Task<KycProfileResponse> MapAsync(
        KycProfile profile,
        CancellationToken cancellationToken)
    {
        var documents = await dbContext.KycDocuments
            .AsNoTracking()
            .Where(document => document.Subject == profile.Subject)
            .Select(document => new DocumentStatus(
                document.Type,
                document.MediaType,
                document.UploadedAt))
            .ToListAsync(cancellationToken);
        var identityDocument = documents.SingleOrDefault(
            document => document.Type == KycDocumentType.IdentityDocument);
        var addressProof = documents.SingleOrDefault(
            document => document.Type == KycDocumentType.AddressProof);

        return new KycProfileResponse(
            profile.FirstName,
            profile.LastName,
            profile.PhoneNumber,
            profile.AddressLine1,
            profile.AddressLine2,
            profile.City,
            profile.PostalCode,
            profile.Country,
            profile.Status,
            identityDocument is null
                ? null
                : new KycDocumentStatusResponse(identityDocument.MediaType, identityDocument.UploadedAt),
            addressProof is null
                ? null
                : new KycDocumentStatusResponse(addressProof.MediaType, addressProof.UploadedAt),
            profile.UpdatedAt);
    }

    private sealed record DocumentStatus(
        KycDocumentType Type,
        string MediaType,
        DateTimeOffset UploadedAt);
}
