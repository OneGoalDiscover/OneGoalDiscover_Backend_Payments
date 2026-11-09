using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using OneGoalDiscover_Backend_Payments.Middlewares;
using OneGoalDiscover_Backend_Payments.Services;
using OneGoalDiscover_Helpers.Auth;
using OneGoalDiscover_Models.DTOs.Auth;
using System.Text;

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
        app.UseAuthorization();
        app.MapControllers();

        // Requerido para descifrar archivos .cfg de OneGoalDiscover_Utilities
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        app.Run();
    }
}
