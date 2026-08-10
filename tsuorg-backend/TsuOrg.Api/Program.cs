using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using TsuOrg.Application;
using TsuOrg.Application.Common;
using TsuOrg.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "TSU-ORGDOCX API",
        Version = "v1",
        Description = "Document validation & workflow API for Tarlac State University SOU"
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Bearer token",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer"
    });
});

var jwtKey = builder.Configuration["Jwt:Key"] ?? "DEV_ONLY_CHANGE_ME_TSUORG_SUPER_SECRET_KEY_32+";
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "tsuorg",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "tsuorg",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
            NameClaimType = System.Security.Claims.ClaimTypes.Name,
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CanSubmitDocuments", p => p.RequireRole("OrgOfficer"));
    options.AddPolicy("CanReviewAsAdviser", p => p.RequireRole("Adviser", "SystemAdmin"));
    options.AddPolicy("CanReviewAsDean", p => p.RequireRole("Dean", "SystemAdmin"));
    options.AddPolicy("CanReviewAsSou", p => p.RequireRole("SouStaff", "SystemAdmin"));
    options.AddPolicy("CanManageOrganizations", p => p.RequireRole("SouStaff", "SystemAdmin"));
    options.AddPolicy("CanViewGlobalTracker", p => p.RequireRole("SouStaff", "SystemAdmin"));
    options.AddPolicy("CanManageSystem", p => p.RequireRole("SystemAdmin"));
});

builder.Services.AddCors(o => o.AddPolicy("Frontend", p =>
    p.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["https://localhost:7001"])
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHttpsRedirection();
}

app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "tsuorg-backend" }));

// Apply EF migrations and seed reference/auth data (roles + demo users) in Development.
using (var scope = app.Services.CreateScope())
{
    var db     = scope.ServiceProvider.GetRequiredService<TsuOrg.Infrastructure.Persistence.TsuOrgDbContext>();
    var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
    await db.Database.MigrateAsync();
    if (app.Environment.IsDevelopment())
        await TsuOrg.Infrastructure.Persistence.DbSeeder.SeedAsync(db, hasher);
}

app.Run();

public partial class Program;
