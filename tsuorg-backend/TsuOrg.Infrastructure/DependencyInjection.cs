using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Auth.Commands;
using TsuOrg.Application.Features.Documents.Commands;
using TsuOrg.Application.Features.Documents.Queries;
using TsuOrg.Application.Features.Documents.Services;
using TsuOrg.Application.Features.Tracking.Queries;
using TsuOrg.Application.Features.Workflow.Commands;
using TsuOrg.Application.Features.Workflow.Queries;
using TsuOrg.Infrastructure.Calsv;
using TsuOrg.Infrastructure.Persistence;
using TsuOrg.Infrastructure.Services;

namespace TsuOrg.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("DefaultConnection")
            ?? "Server=127.0.0.1;Port=3306;Database=TsuOrgDb;User=root;Password=;";

        // XAMPP ships MariaDB 10.4.x — pin server version so migrations don't require a live DB at design time.
        var serverVersion = new MariaDbServerVersion(new Version(10, 4, 32));

        services.AddDbContext<TsuOrgDbContext>(options =>
            options.UseMySql(connectionString, serverVersion));

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<TsuOrgDbContext>());

        var calsvBase = config["Calsv:BaseUrl"] ?? "http://localhost:8000";
        services.AddHttpClient<ICalsvClient, CalsvHttpClient>(client =>
        {
            client.BaseAddress = new Uri(calsvBase);
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        // Infrastructure services
        services.AddSingleton<EmailService>();
        services.AddScoped<IBlobService, BlobService>();
        services.AddScoped<IDocumentNumberService, DocumentNumberService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<ISignatureService, SignatureService>();
        services.AddScoped<IPasswordHasher, PasswordHasherService>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<ITrackingRecorder, TrackingRecorder>();

        // CALSV background queue (singleton channel + hosted worker)
        services.AddSingleton<ICalsvJobQueue, ChannelCalsvJobQueue>();
        services.AddScoped<ICalsvOrchestrator, CalsvOrchestrator>();
        services.AddHostedService<CalsvBackgroundService>();

        // Auth handlers
        services.AddScoped<LoginHandler>();
        services.AddScoped<RefreshTokenHandler>();
        services.AddScoped<LogoutHandler>();
        services.AddScoped<ChangePasswordHandler>();
        services.AddScoped<GetProfileHandler>();
        services.AddScoped<UpdateProfileHandler>();
        services.AddScoped<UploadAvatarHandler>();

        // DSM
        services.AddScoped<CreateDocumentHandler>();
        services.AddScoped<UploadPrimaryFileHandler>();
        services.AddScoped<AddAttachmentHandler>();
        services.AddScoped<SubmitDocumentHandler>();
        services.AddScoped<ConfirmSubmissionHandler>();
        services.AddScoped<GetDocumentHandler>();
        services.AddScoped<ListDocumentsHandler>();
        services.AddScoped<GetDocumentDownloadHandler>();
        services.AddScoped<GetValidationHandler>();
        services.AddScoped<RerunValidationHandler>();

        // WM
        services.AddScoped<WorkflowDecisionHandler>();
        services.AddScoped<GetReviewQueueHandler>();
        services.AddScoped<GetWorkflowHistoryHandler>();

        // DTM + Dashboard
        services.AddScoped<GetTrackerListHandler>();
        services.AddScoped<GetTrackingTimelineHandler>();
        services.AddScoped<GetAnalyticsSummaryHandler>();
        services.AddScoped<GetDashboardHandler>();

        return services;
    }
}
