using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TsuOrg.Frontend.Models;

namespace TsuOrg.Frontend.Services;

public sealed class ApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public ApiClient(HttpClient http) => _http = http;

    public Task<HttpResponseMessage> HealthAsync(CancellationToken ct = default) =>
        _http.GetAsync("/health", ct);

    // ── Auth ─────────────────────────────────────────────────────────────

    public async Task<LoginResponse> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, password),
            ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new AuthException(await ReadProblemAsync(response, ct) ?? "Invalid email or password.");

        if (!response.IsSuccessStatusCode)
            throw new AuthException(await ReadProblemAsync(response, ct) ?? "Sign in failed. Please try again.");

        var result = await response.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions, ct)
                     ?? throw new AuthException("Sign in failed. Empty response from server.");
        return result;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try
        {
            await _http.PostAsync("/api/v1/auth/logout", null, ct);
        }
        catch
        {
            // Local sign-out still proceeds if the API call fails.
        }
    }

    public async Task<UserProfile?> GetProfileAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/v1/auth/me", ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<UserProfile>(JsonOptions, ct);
    }

    public async Task<UserProfile> UpdateProfileAsync(string fullName, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync(
            "/api/v1/auth/me",
            new UpdateProfileRequest(fullName),
            ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<UserProfile>(JsonOptions, ct))!;
    }

    public async Task<UserProfile> UploadAvatarAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        using var form = BuildFileContent(content, fileName, contentType);
        var response = await _http.PostAsync("/api/v1/auth/me/avatar", form, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<UserProfile>(JsonOptions, ct))!;
    }

    /// <summary>Loads the caller's avatar via authenticated API (Bearer) as a data URL for &lt;img&gt;.</summary>
    public async Task<string?> GetMyAvatarDataUrlAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/v1/auth/me/avatar", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) return null;

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
    }

    public async Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            "/api/v1/auth/change-password",
            new ChangePasswordRequest(currentPassword, newPassword),
            ct);
        await EnsureSuccessAsync(response, ct);
    }

    // ── Catalog ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DocumentTypeDto>> GetDocumentTypesAsync(CancellationToken ct = default)
    {
        var items = await _http.GetFromJsonAsync<List<DocumentTypeDto>>("/api/v1/document-types", JsonOptions, ct);
        return items ?? [];
    }

    public async Task<IReadOnlyList<OrganizationDto>> GetMyOrganizationsAsync(CancellationToken ct = default)
    {
        var items = await _http.GetFromJsonAsync<List<OrganizationDto>>("/api/v1/organizations/my", JsonOptions, ct);
        return items ?? [];
    }

    public async Task<IReadOnlyList<OrganizationDto>> GetOrganizationsAsync(CancellationToken ct = default)
    {
        var items = await _http.GetFromJsonAsync<List<OrganizationDto>>("/api/v1/organizations", JsonOptions, ct);
        return items ?? [];
    }

    public async Task<AcademicYearDto?> GetCurrentAcademicYearAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/v1/academic-years/current", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<AcademicYearDto>(JsonOptions, ct);
    }

    // ── Documents ────────────────────────────────────────────────────────

    public async Task<CreateDocumentResult> CreateDocumentAsync(CreateDocumentRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/v1/documents", request, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<CreateDocumentResult>(JsonOptions, ct))!;
    }

    public async Task<UploadFileResult> UploadPrimaryFileAsync(
        Guid documentId,
        Stream content,
        string fileName,
        string contentType,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        using var form = BuildFileContent(content, fileName, contentType, progress);
        var response = await _http.PostAsync($"/api/v1/documents/{documentId}/files", form, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<UploadFileResult>(JsonOptions, ct))!;
    }

    public async Task<UploadFileResult> UploadAttachmentAsync(
        Guid documentId,
        string attachmentType,
        Stream content,
        string fileName,
        string contentType,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        using var form = BuildFileContent(content, fileName, contentType, progress);
        var response = await _http.PostAsync(
            $"/api/v1/documents/{documentId}/attachments?attachmentType={Uri.EscapeDataString(attachmentType)}",
            form, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<UploadFileResult>(JsonOptions, ct))!;
    }

    public async Task<SubmitDocumentResult> SubmitDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/v1/documents/{documentId}/submit", null, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<SubmitDocumentResult>(JsonOptions, ct))!;
    }

    public async Task<ConfirmSubmissionResult> ConfirmSubmissionAsync(Guid documentId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/v1/documents/{documentId}/confirm", null, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ConfirmSubmissionResult>(JsonOptions, ct))!;
    }

    public async Task<DocumentDetailDto?> GetDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/v1/documents/{documentId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<DocumentDetailDto>(JsonOptions, ct);
    }

    public async Task<PagedDocumentsDto> GetDocumentsAsync(int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/v1/documents?page={page}&pageSize={pageSize}", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PagedDocumentsDto>(JsonOptions, ct))
               ?? new PagedDocumentsDto([], 0, page, pageSize);
    }

    public async Task<ValidationDetailDto?> GetValidationAsync(Guid documentId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/v1/documents/{documentId}/validation", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<ValidationDetailDto>(JsonOptions, ct);
    }

    public async Task<TrackerListResult> GetTrackerAsync(
        string tab = "All",
        int page = 1,
        int pageSize = 50,
        string? search = null,
        string? college = null,
        Guid? organizationId = null,
        CancellationToken ct = default)
    {
        var url = $"/api/v1/tracking?tab={Uri.EscapeDataString(tab)}&page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(search)) url += $"&search={Uri.EscapeDataString(search)}";
        if (!string.IsNullOrWhiteSpace(college)) url += $"&college={Uri.EscapeDataString(college)}";
        if (organizationId.HasValue) url += $"&organizationId={organizationId}";

        var response = await _http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TrackerListResult>(JsonOptions, ct))
               ?? new TrackerListResult([], 0, page, pageSize, []);
    }

    public async Task<SouTrackerResult> GetSouTrackerAsync(
        string tab = "All",
        int page = 1,
        int pageSize = 100,
        string? search = null,
        string? college = null,
        Guid? organizationId = null,
        string complianceBand = "all",
        CancellationToken ct = default)
    {
        var url = $"/api/v1/tracking/sou?tab={Uri.EscapeDataString(tab)}&page={page}&pageSize={pageSize}&complianceBand={Uri.EscapeDataString(complianceBand)}";
        if (!string.IsNullOrWhiteSpace(search)) url += $"&search={Uri.EscapeDataString(search)}";
        if (!string.IsNullOrWhiteSpace(college)) url += $"&college={Uri.EscapeDataString(college)}";
        if (organizationId.HasValue) url += $"&organizationId={organizationId}";

        var response = await _http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<SouTrackerResult>(JsonOptions, ct))
               ?? new SouTrackerResult([], 0, page, pageSize, [], [], []);
    }

    public async Task<WorkflowMonitorResult> GetWorkflowMonitorAsync(string tab = "AllActive", int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/v1/workflow/monitor?tab={tab}&page={page}&pageSize={pageSize}", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<WorkflowMonitorResult>(JsonOptions, ct))
               ?? new WorkflowMonitorResult([], 0, page, pageSize);
    }

    // ─── SOU Admin management (Figures 26–29) ────────────────────────────

    public async Task<OrganizationsAdminResult> GetOrganizationsAdminAsync(string tab = "Active", CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/v1/organizations/admin-list?tab={tab}", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<OrganizationsAdminResult>(JsonOptions, ct))
               ?? new OrganizationsAdminResult([], 0, 0);
    }

    public async Task<SystemReportsDto?> GetSystemReportsAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/v1/reports/summary", ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<SystemReportsDto>(JsonOptions, ct);
    }

    public async Task<(string FileName, string ContentType, byte[] Bytes)> DownloadReportAsync(string type, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/v1/reports/export/{Uri.EscapeDataString(type)}", ct);
        await EnsureSuccessAsync(response, ct);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? $"{type}.csv";
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "text/csv";
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return (fileName, contentType, bytes);
    }

    public async Task<SystemAuditResult> GetSystemAuditAsync(int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/v1/workflow/audit?page={page}&pageSize={pageSize}", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<SystemAuditResult>(JsonOptions, ct))
               ?? new SystemAuditResult([], 0, page, pageSize);
    }

    public async Task<ScopedAuditResult> GetScopedAuditAsync(int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/v1/workflow/audit/scoped?page={page}&pageSize={pageSize}", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ScopedAuditResult>(JsonOptions, ct))
               ?? new ScopedAuditResult([], 0, page, pageSize);
    }

    public async Task<SystemSettingsDto?> GetSystemSettingsAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/v1/settings", ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<SystemSettingsDto>(JsonOptions, ct);
    }

    public async Task<SystemSettingsDto?> UpdateSystemSettingsAsync(SystemSettingsDto settings, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync("/api/v1/settings", settings, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<SystemSettingsDto>(JsonOptions, ct);
    }

    public async Task<TrackingTimelineDto?> GetTrackingAsync(Guid documentId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/v1/tracking/{documentId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<TrackingTimelineDto>(JsonOptions, ct);
    }

    // ── Dashboard ────────────────────────────────────────────────────────

    public async Task<DashboardDto> GetDashboardAsync(Guid? orgId = null, CancellationToken ct = default)
    {
        var url = orgId.HasValue
            ? $"/api/v1/dashboard?orgId={orgId}"
            : "/api/v1/dashboard";
        var response = await _http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<DashboardDto>(JsonOptions, ct))
               ?? new DashboardDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], [], [], 0, 0, []);
    }

    public async Task<SouDashboardDto> GetSouDashboardAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/v1/dashboard/sou", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<SouDashboardDto>(JsonOptions, ct))
               ?? new SouDashboardDto(0, 0, 0, 0, [], new SouStatusBreakdownDto(0, 0, 0, 0, 0, 0), [], []);
    }

    // ── Archive (DMA) ────────────────────────────────────────────────────

    public async Task<ArchiveListResult> GetArchiveAsync(
        Guid? organizationId = null,
        string? documentTypeCode = null,
        string? eventKeyword = null,
        Guid? academicYearId = null,
        string? status = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var qs = new List<string>
        {
            $"page={page}",
            $"pageSize={pageSize}",
        };
        if (organizationId.HasValue) qs.Add($"organizationId={organizationId}");
        if (!string.IsNullOrWhiteSpace(documentTypeCode))
            qs.Add($"documentTypeCode={Uri.EscapeDataString(documentTypeCode)}");
        if (!string.IsNullOrWhiteSpace(eventKeyword))
            qs.Add($"eventKeyword={Uri.EscapeDataString(eventKeyword)}");
        if (academicYearId.HasValue) qs.Add($"academicYearId={academicYearId}");
        if (!string.IsNullOrWhiteSpace(status))
            qs.Add($"status={Uri.EscapeDataString(status)}");

        var response = await _http.GetAsync($"/api/v1/archive?{string.Join('&', qs)}", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ArchiveListResult>(JsonOptions, ct))
               ?? new ArchiveListResult([], 0, page, pageSize);
    }

    public async Task<ArchiveCategoryCountsDto> GetArchiveCategoryCountsAsync(
        Guid? organizationId = null,
        Guid? academicYearId = null,
        CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (organizationId.HasValue) qs.Add($"organizationId={organizationId}");
        if (academicYearId.HasValue) qs.Add($"academicYearId={academicYearId}");
        var suffix = qs.Count > 0 ? "?" + string.Join('&', qs) : string.Empty;

        var response = await _http.GetAsync($"/api/v1/archive/category-counts{suffix}", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ArchiveCategoryCountsDto>(JsonOptions, ct))
               ?? new ArchiveCategoryCountsDto([], []);
    }

    public async Task<IReadOnlyList<AcademicYearDto>> GetAcademicYearsAsync(CancellationToken ct = default)
    {
        var items = await _http.GetFromJsonAsync<List<AcademicYearDto>>("/api/v1/academic-years", JsonOptions, ct);
        return items ?? [];
    }

    public async Task<IReadOnlyList<DocumentVersionDto>> GetDocumentVersionsAsync(Guid documentId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/v1/documents/{documentId}/versions", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<List<DocumentVersionDto>>(JsonOptions, ct)) ?? [];
    }

    public async Task<DocumentDownloadDto?> GetDocumentDownloadAsync(Guid documentId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/v1/documents/{documentId}/download", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<DocumentDownloadDto>(JsonOptions, ct);
    }

    public async Task<DocumentDownloadDto?> GetAttachmentDownloadAsync(
        Guid documentId, Guid attachmentId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(
            $"/api/v1/documents/{documentId}/attachments/{attachmentId}/download", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<DocumentDownloadDto>(JsonOptions, ct);
    }

    // ── Workflow (WM) ────────────────────────────────────────────────────

    public async Task<ReviewQueueResult> GetReviewQueueAsync(string tab = "All", int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/v1/workflow/queue?tab={tab}&page={page}&pageSize={pageSize}", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ReviewQueueResult>(JsonOptions, ct))
               ?? new ReviewQueueResult([], 0, page, pageSize);
    }

    public async Task<ReviewHistoryResult> GetMyReviewHistoryAsync(string tab = "All", int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/v1/workflow/my-history?tab={tab}&page={page}&pageSize={pageSize}", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ReviewHistoryResult>(JsonOptions, ct))
               ?? new ReviewHistoryResult([], 0, page, pageSize);
    }

    public async Task<WorkflowHistoryDto?> GetWorkflowHistoryAsync(Guid documentId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/v1/workflow/{documentId}/history", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<WorkflowHistoryDto>(JsonOptions, ct);
    }

    public async Task<WorkflowDecisionResult> ApproveAsync(
        Guid documentId, string? comments, Stream signature, string signatureFileName, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        if (!string.IsNullOrWhiteSpace(comments))
            form.Add(new StringContent(comments), "comments");
        var sigContent = new StreamContent(signature);
        sigContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(sigContent, "signature", signatureFileName);

        var response = await _http.PostAsync($"/api/v1/workflow/{documentId}/approve", form, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<WorkflowDecisionResult>(JsonOptions, ct))!;
    }

    public async Task<WorkflowDecisionResult> RejectAsync(Guid documentId, string comments, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(comments), "comments");
        var response = await _http.PostAsync($"/api/v1/workflow/{documentId}/reject", form, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<WorkflowDecisionResult>(JsonOptions, ct))!;
    }

    public async Task<WorkflowDecisionResult> ReturnAsync(Guid documentId, string comments, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(comments), "comments");
        var response = await _http.PostAsync($"/api/v1/workflow/{documentId}/return", form, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<WorkflowDecisionResult>(JsonOptions, ct))!;
    }

    // ── Notifications ────────────────────────────────────────────────────

    public async Task<NotificationListDto> GetNotificationsAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/v1/notifications", ct);
        if (!response.IsSuccessStatusCode)
            return new NotificationListDto(0, []);
        return (await response.Content.ReadFromJsonAsync<NotificationListDto>(JsonOptions, ct))
               ?? new NotificationListDto(0, []);
    }

    public async Task MarkNotificationReadAsync(Guid id, CancellationToken ct = default)
    {
        await _http.PostAsync($"/api/v1/notifications/{id}/read", null, ct);
    }

    public async Task MarkAllNotificationsReadAsync(CancellationToken ct = default)
    {
        await _http.PostAsync("/api/v1/notifications/read-all", null, ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static MultipartFormDataContent BuildFileContent(
        Stream content,
        string fileName,
        string contentType,
        IProgress<int>? progress = null)
    {
        var form = new MultipartFormDataContent();
        var length = content.CanSeek ? content.Length : -1;
        Stream uploadStream = content;
        if (progress is not null && length > 0)
            uploadStream = new ProgressStream(content, length, progress);

        var streamContent = new StreamContent(uploadStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        form.Add(streamContent, "file", fileName);
        return form;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var problem = await ReadProblemAsync(response, ct) ?? $"Request failed ({(int)response.StatusCode}).";
        throw new ApiException(problem, response.StatusCode);
    }

    private static async Task<string?> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                return detail.GetString();
            if (doc.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                return title.GetString();
        }
        catch
        {
            // ignore parse errors
        }

        return null;
    }
}

public sealed class AuthException : Exception
{
    public AuthException(string message) : base(message) { }
}

public sealed class ApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public ApiException(string message, HttpStatusCode statusCode) : base(message)
    {
        StatusCode = statusCode;
    }
}
