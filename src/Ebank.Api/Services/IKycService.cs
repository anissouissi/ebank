using Ebank.Api.Data;
using Ebank.Api.Endpoints;

namespace Ebank.Api.Services;

public interface IKycService
{
    Task<KycProfileResponse> GetAsync(string subject, CancellationToken cancellationToken);
    Task<KycProfileResponse> UpdateProfileAsync(
        string subject,
        UpdateKycProfileRequest request,
        CancellationToken cancellationToken);
    Task<KycProfileResponse> UploadDocumentAsync(
        string subject,
        KycDocumentType type,
        string mediaType,
        byte[] content,
        CancellationToken cancellationToken);
}
