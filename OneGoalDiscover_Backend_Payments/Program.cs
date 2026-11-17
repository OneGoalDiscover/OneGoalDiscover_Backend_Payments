using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using OneGoalDiscover_Backend_Payments.Services;
using OneGoalDiscover_Helpers.Auth;
using OneGoalDiscover_Models.DTOs.Auth;
using System.Text;
using System.Threading.RateLimiting;

namespace OneGoalDiscover_Backend_Payments;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddApplicationServices(builder.Configuration);

        JwtTokenParameter jwtParams = new JwtToken().tokenValidationParameters();
        builder.Services.AddAuthentication(opt =>
        {
            opt.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            opt.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(opt =>
        {
            opt.RequireHttpsMetadata = true;
            opt.SaveToken = true;
            opt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtParams.issuer,
                ValidAudience = jwtParams.audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtParams.secretKey))
            };
        });

        builder.Services.AddAuthorization();

        builder.Services.AddControllers();

        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                if (builder.Environment.IsDevelopment())
                    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
                else
                {
                    string[] origins = builder.Configuration
                        .GetSection("AllowedOrigins").Get<string[]>() ?? [];
                    if (origins.Length > 0)
                        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
                }
            });
        });

        AddPaymentsRateLimiting(builder);

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "OneGoal Discover - API de Pagos",
                Version = "v1",
                Description = "Gestión de planes de licencia y procesamiento de pagos vía OpenPay."
            });

            options.AddSecurityDefinition(name: JwtBearerDefaults.AuthenticationScheme, securityScheme: new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Description = "Enter JWT Bearer token",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = JwtBearerDefaults.AuthenticationScheme,
                BearerFormat = "JWT"
            });
            options.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecuritySchemeReference(JwtBearerDefaults.AuthenticationScheme, doc),
                    new List<string>()
                }
            });
        });

        var app = builder.Build();

        app.UseErrorHandlingMiddleware();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();
        app.UseCors();
        app.UseAuthentication();
        app.UseRateLimiter();   // después de autenticar: el límite de cobros se aplica por usuario
        app.UseAuthorization();
        app.MapControllers();

        // Requerido para descifrar archivos .cfg de OneGoalDiscover_Utilities
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        app.Run();
    }

    // Límites por ventana deslizante de 1 minuto (configurables en la sección RateLimiting):
    //  charge  - cobros por usuario (frena pruebas de tarjetas y dobles envíos masivos)
    //  status  - consultas de estatus por usuario (el wizard consulta cada 10-60 s)
    //  webhook - llamadas por IP (frena el intento de adivinar las credenciales del webhook)
    private static void AddPaymentsRateLimiting(WebApplicationBuilder builder)
    {
        int chargePerMinute  = builder.Configuration.GetValue<int?>("RateLimiting:ChargePerMinute")  ?? 5;
        int statusPerMinute  = builder.Configuration.GetValue<int?>("RateLimiting:StatusPerMinute")  ?? 60;
        int webhookPerMinute = builder.Configuration.GetValue<int?>("RateLimiting:WebhookPerMinute") ?? 300;

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("charge",  context => SlidingWindowByUser(context, chargePerMinute));
            options.AddPolicy("status",  context => SlidingWindowByUser(context, statusPerMinute));
            options.AddPolicy("webhook", context => SlidingWindow($"ip|{context.Connection.RemoteIpAddress}", webhookPerMinute));

            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();

                context.HttpContext.Response.ContentType = "application/problem+json";
                await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title  = "Demasiadas solicitudes",
                    Detail = "Se hicieron demasiados intentos en poco tiempo. Espera un minuto antes de intentarlo de nuevo."
                }, cancellationToken);
            };
        });
    }

    private static RateLimitPartition<string> SlidingWindowByUser(HttpContext context, int permitLimit)
    {
        string? user = context.User.FindFirst("idUser")?.Value;
        string  cfg  = context.User.FindFirst("cfg")?.Value ?? string.Empty;
        string  key  = user is null ? $"ip|{context.Connection.RemoteIpAddress}" : $"user|{cfg}|{user}";
        return SlidingWindow(key, permitLimit);
    }

    private static RateLimitPartition<string> SlidingWindow(string key, int permitLimit) =>
        RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit       = permitLimit,
            Window            = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit        = 0,
            AutoReplenishment = true
        });
}
