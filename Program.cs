using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using QuizApi.Models;

var builder = WebApplication.CreateBuilder(args);

DotNetEnv.Env.Load();

builder.Services.AddCors(options =>
{
    options.AddPolicy("QuizChatPolicy", policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://localhost:5174")
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddControllers();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "QuizApi", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Nhập Token theo dạng: Bearer {token}",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? builder.Configuration["JwtSettings:Issuer"],
            ValidAudience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? builder.Configuration["JwtSettings:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("JWT_SECRET_KEY") ?? builder.Configuration["JwtSettings:SecretKey"]!))
        };
    });

builder.Services.AddDbContext<QuizDbContext>(options =>
    options.UseSqlServer(Environment.GetEnvironmentVariable("DB_CONNECTION_STRING") ?? builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHttpClient();
builder.Services.AddScoped<QuizApi.Services.GroqService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors("QuizChatPolicy");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<QuizDbContext>();
    try {
        context.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[ChatMessages]') AND type in (N'U'))
            BEGIN
                CREATE TABLE [dbo].[ChatMessages] (
                    [MessageId] INT IDENTITY(1,1) NOT NULL,
                    [Username] NVARCHAR(MAX) NOT NULL,
                    [UserMessage] NVARCHAR(MAX) NOT NULL,
                    [AiResponse] NVARCHAR(MAX) NOT NULL,
                    [SentAt] DATETIME2 NOT NULL DEFAULT (GETDATE()),
                    [Metadata] NVARCHAR(MAX) NULL,
                    CONSTRAINT [PK_ChatMessages] PRIMARY KEY CLUSTERED ([MessageId] ASC)
                );
            END
        ");

        context.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Notifications]') AND type in (N'U'))
            BEGIN
                CREATE TABLE [dbo].[Notifications] (
                    [NotificationId] INT IDENTITY(1,1) NOT NULL,
                    [Title] NVARCHAR(MAX) NOT NULL,
                    [Message] NVARCHAR(MAX) NOT NULL,
                    [Type] NVARCHAR(50) NOT NULL DEFAULT ('Exam'),
                    [TargetId] INT NULL,
                    [IsRead] BIT NOT NULL DEFAULT (0),
                    [CreatedAt] DATETIME2 NOT NULL DEFAULT (GETDATE()),
                    [UserId] INT NULL,
                    CONSTRAINT [PK_Notifications] PRIMARY KEY CLUSTERED ([NotificationId] ASC)
                );
            END
        ");

        context.Database.ExecuteSqlRaw(@"
            IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Notifications]') AND type in (N'U'))
            BEGIN
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[Notifications]') AND name = 'UserId')
                BEGIN
                    ALTER TABLE [dbo].[Notifications] ADD [UserId] INT NULL;
                END
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[Notifications]') AND name = 'Type')
                BEGIN
                    ALTER TABLE [dbo].[Notifications] ADD [Type] NVARCHAR(50) NOT NULL DEFAULT ('Exam');
                END
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[Notifications]') AND name = 'TargetId')
                BEGIN
                    ALTER TABLE [dbo].[Notifications] ADD [TargetId] INT NULL;
                END
            END
        ");
    } catch (Exception ex) {
        Console.WriteLine($"[Database Init] Error creating or altering database tables: {ex.Message}");
    }
}

app.Run();
