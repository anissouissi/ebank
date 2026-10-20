using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Ebank.Api.Data;
using Ebank.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Ebank.Api.Endpoints;

public static class KycEndpoints
{
    private const long MaximumDocumentSize = 5 * 1024 * 1024;

    public static RouteGroupBuilder MapKycEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/kyc")
            .RequireAuthorization()
            .WithTags("KYC");

        group.MapGet("/", async Task<Results<Ok<KycProfileResponse>, UnauthorizedHttpResult>> (
                ClaimsPrincipal user,
                IKycService kycService,
                CancellationToken cancellationToken) =>
            {
                var subject = user.FindFirstValue("sub");
                if (string.IsNullOrWhiteSpace(subject))
                {
                    return TypedResults.Unauthorized();
                }

                return TypedResults.Ok(await kycService.GetAsync(subject, cancellationToken));
            })
            .WithName("GetKycProfile")
            .WithSummary("Get the signed-in customer's KYC profile")
            .WithDescription("Returns the profile and upload status for the current Keycloak subject only.")
            .Produces<KycProfileResponse>()
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPut("/profile", async Task<Results<Ok<KycProfileResponse>, UnauthorizedHttpResult, ValidationProblem>> (
                UpdateKycProfileRequest request,
                ClaimsPrincipal user,
                IKycService kycService,
                CancellationToken cancellationToken) =>
            {
                var subject = user.FindFirstValue("sub");
                if (string.IsNullOrWhiteSpace(subject))
                {
                    return TypedResults.Unauthorized();
                }

                return TypedResults.Ok(await kycService.UpdateProfileAsync(
                    subject,
                    request,
                    cancellationToken));
            })
            .WithName("UpdateKycProfile")
            .WithSummary("Save the signed-in customer's contact and address details")
            .WithDescription("Updates only the current Keycloak subject's KYC details.")
            .Produces<KycProfileResponse>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPut("/documents/{documentType}", async Task<Results<Ok<KycProfileResponse>, UnauthorizedHttpResult, ValidationProblem>> (
                string documentType,
                IFormFile file,
                ClaimsPrincipal user,
                IKycService kycService,
                CancellationToken cancellationToken) =>
            {
                var subject = user.FindFirstValue("sub");
                if (string.IsNullOrWhiteSpace(subject))
                {
                    return TypedResults.Unauthorized();
                }

                var type = documentType.ToLowerInvariant() switch
                {
                    "identity" => KycDocumentType.IdentityDocument,
                    "address-proof" => KycDocumentType.AddressProof,
                    _ => (KycDocumentType?)null
                };

                if (type is null)
                {
                    return InvalidFile("documentType", "Use 'identity' or 'address-proof'.");
                }

                if (file.Length is <= 0 or > MaximumDocumentSize)
                {
                    return InvalidFile("file", "Choose a non-empty file no larger than 5 MB.");
                }

                var content = new byte[file.Length];
                await using (var stream = file.OpenReadStream())
                {
                    var offset = 0;
                    while (offset < content.Length)
                    {
                        var bytesRead = await stream.ReadAsync(
                            content.AsMemory(offset),
                            cancellationToken);
                        if (bytesRead == 0)
                        {
                            return InvalidFile("file", "The file could not be read completely.");
                        }

                        offset += bytesRead;
                    }
                }

                var mediaType = GetVerifiedMediaType(content);
                if (mediaType is null || !string.Equals(
                        file.ContentType,
                        mediaType,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return InvalidFile("file", "Upload a valid PDF, PNG, or JPEG file.");
                }

                return TypedResults.Ok(await kycService.UploadDocumentAsync(
                    subject,
                    type.Value,
                    mediaType,
                    content,
                    cancellationToken));
            })
            .WithName("UploadKycDocument")
            .WithSummary("Upload an identity document or address proof")
            .WithDescription(
                "Accepts one PDF, PNG, or JPEG file up to 5 MB. Document contents are encrypted before being stored in PostgreSQL.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<KycProfileResponse>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status401Unauthorized)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumDocumentSize + 128 * 1024));

        return group;
    }

    private static ValidationProblem InvalidFile(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            [field] = [message]
        });

    private static string? GetVerifiedMediaType(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith("%PDF-"u8))
        {
            return "application/pdf";
        }

        if (content.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }

        if (content.Length >= 3 &&
            content[0] == 0xFF &&
            content[1] == 0xD8 &&
            content[2] == 0xFF)
        {
            return "image/jpeg";
        }

        return null;
    }
}

/// <summary>Customer contact and address data submitted for KYC review.</summary>
public sealed record UpdateKycProfileRequest
{
    /// <summary>The customer's legal first name.</summary>
    [Required, StringLength(100, MinimumLength = 1)]
    public required string FirstName { get; init; }

    /// <summary>The customer's legal last name.</summary>
    [Required, StringLength(100, MinimumLength = 1)]
    public required string LastName { get; init; }

    /// <summary>A phone number where the customer can be contacted.</summary>
    [Required, Phone, StringLength(32, MinimumLength = 7)]
    public required string PhoneNumber { get; init; }

    /// <summary>The first line of the customer's residential address.</summary>
    [Required, StringLength(200, MinimumLength = 3)]
    public required string AddressLine1 { get; init; }

    /// <summary>The optional second line of the customer's residential address.</summary>
    [StringLength(200)]
    public string? AddressLine2 { get; init; }

    /// <summary>The city or locality of the customer's address.</summary>
    [Required, StringLength(100, MinimumLength = 1)]
    public required string City { get; init; }

    /// <summary>The postal or ZIP code for the customer's address.</summary>
    [Required, StringLength(32, MinimumLength = 1)]
    public required string PostalCode { get; init; }

    /// <summary>The ISO 3166-1 alpha-2 country code for the customer's address.</summary>
    [Required, StringLength(2, MinimumLength = 2)]
    [RegularExpression("^[A-Za-z]{2}$")]
    public required string Country { get; init; }
}

/// <summary>Customer KYC details and the status of the uploaded supporting documents.</summary>
public sealed record KycProfileResponse(
    string FirstName,
    string LastName,
    string PhoneNumber,
    string AddressLine1,
    string AddressLine2,
    string City,
    string PostalCode,
    string Country,
    KycStatus Status,
    KycDocumentStatusResponse? IdentityDocument,
    KycDocumentStatusResponse? AddressProof,
    DateTimeOffset? UpdatedAt);

/// <summary>Metadata for an uploaded KYC document; document contents are never returned.</summary>
public sealed record KycDocumentStatusResponse(
    string MediaType,
    DateTimeOffset UploadedAt);
