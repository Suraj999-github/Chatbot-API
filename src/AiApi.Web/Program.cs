using AiApi.Web.Configuration;
using AiApi.Web.Middleware;
using AiApi.Web.Services;
using AiApi.Web.Services.Chat;
using AiApi.Web.Services.Documents;
using AiApi.Web.Services.Embeddings;
using AiApi.Web.Services.Ingestion;
using AiApi.Web.Services.Jobs;
using AiApi.Web.Services.VectorStore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Qdrant.Client;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Serilog ────────────────────────────────────────────────
builder.Host.UseSerilog((ctx, lc) =>
    lc.ReadFrom.Configuration(ctx.Configuration)
      .WriteTo.Console()
      .WriteTo.File("logs/api-.log", rollingInterval: RollingInterval.Day));

// ── Options ────────────────────────────────────────────────
builder.Services.Configure<AiProviderOptions>(
    builder.Configuration.GetSection(AiProviderOptions.SectionName));

builder.Services.Configure<DocumentChatOptions>(
    builder.Configuration.GetSection(DocumentChatOptions.SectionName));

// ── HttpClient (Chat) ──────────────────────────────────────
builder.Services.AddHttpClient("OllamaClient", (sp, client) =>
{
    var opts = builder.Configuration
        .GetSection(AiProviderOptions.SectionName)
        .Get<AiProviderOptions>()!;

    client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + '/');
    client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
});

// ── HttpClient (Embeddings) ────────────────────────────────
builder.Services.AddHttpClient("OllamaEmbedClient", (sp, client) =>
{
    var opts = builder.Configuration
        .GetSection(AiProviderOptions.SectionName)
        .Get<AiProviderOptions>()!;

    // Embeddings endpoint uses /api/embeddings
    var baseUrl = opts.BaseUrl.TrimEnd('/').Replace("/v1", "");

    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// ── Qdrant ─────────────────────────────────────────────────
builder.Services.AddSingleton(_ =>
{
    var host = builder.Configuration["Qdrant:Host"] ?? "localhost";
    var port = int.Parse(builder.Configuration["Qdrant:Port"] ?? "6334");

    return new QdrantClient(host, port);
});

// ── AI Provider Selection ──────────────────────────────────
var provider = builder.Configuration["AiProvider:Provider"] ?? "Ollama";

if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
    provider.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase))
{
    // Future implementation
    // builder.Services.AddScoped<IAIChatService, OpenAIChatService>();

    throw new InvalidOperationException(
        $"Provider '{provider}' is configured but OpenAIChatService is not yet implemented.");
}
else
{
    builder.Services.AddScoped<IAIChatService, OllamaChatService>();
}

// ── Document Parsing ───────────────────────────────────────
builder.Services.AddSingleton<IDocumentParser, PdfDocumentParser>();
builder.Services.AddSingleton<IDocumentParser, WordDocumentParser>();
builder.Services.AddSingleton<DocumentParserFactory>();

// ── Embeddings + Vector Store ──────────────────────────────
builder.Services.AddScoped<IEmbeddingService, OllamaEmbeddingService>();

// Production vector DB
builder.Services.AddSingleton<IVectorStore, QdrantVectorStore>();

// Development/testing in-memory vector store
// Remove if using only Qdrant
builder.Services.AddSingleton<IVectorStore, InMemoryVectorStore>();

builder.Services.AddScoped<RagService>();

// ── Core AI Services ───────────────────────────────────────
builder.Services.AddScoped<IDocumentIndexer, DocumentIndexer>();
builder.Services.AddScoped<IDocumentChatService, DocumentChatService>();

// ── Background Jobs ────────────────────────────────────────
builder.Services.AddSingleton<IJobStore, InMemoryJobStore>();
builder.Services.AddSingleton<DocumentIndexingQueue>();
builder.Services.AddHostedService<DocumentIndexingWorker>();

// ── Semantic Kernel ────────────────────────────────────────
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<AiProviderOptions>>().Value;

    return Kernel.CreateBuilder()
        .AddOpenAIChatCompletion(
            modelId: opts.DefaultModel,
            apiKey: string.IsNullOrWhiteSpace(opts.ApiKey)
                ? "ollama"
                : opts.ApiKey,
            endpoint: new Uri(opts.BaseUrl))
        .Build();
});

// ── Controllers + OpenAPI + Scalar ─────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer(); 
builder.Services.AddSwaggerGen(c =>          
{
    c.SwaggerDoc("v1", new() { Title = "Document Chatbot API", Version = "v1" });
});

// OpenAPI document generation

// ── CORS ───────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("Default", policy =>
    {
        var origins = builder.Configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>();

        if (origins is { Length: > 0 })
        {
            policy
                .WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        }
        else
        {
            policy
                .AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    });
});

// ── Health Checks ──────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck("qdrant", () =>
    {
        return HealthCheckResult.Healthy("Qdrant configured");
    });

// ── Upload Limits ──────────────────────────────────────────
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 52_428_800; // 50 MB
});

// ── Kestrel ────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(10);
});

var app = builder.Build();

// ── Middleware Pipeline ────────────────────────────────────
app.UseSerilogRequestLogging();

app.UseMiddleware<GlobalExceptionMiddleware>();

app.UseCors("Default");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger(); // serves OpenAPI JSON at /swagger/v1/swagger.json

    app.MapScalarApiReference(options =>
    {
        options
            .WithTitle("Document Chatbot API")
            .WithTheme(ScalarTheme.BluePlanet)
            .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient)
            .WithOpenApiRoutePattern("/swagger/v1/swagger.json"); // point Scalar at Swashbuckle's output
    });
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.MapHealthChecks("/health");

app.Run();