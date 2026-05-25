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

// ── HttpClient (no Polly — Ollama is slow by design) ───────
builder.Services.AddHttpClient("OllamaClient", (sp, client) =>
{
    var opts = builder.Configuration
        .GetSection(AiProviderOptions.SectionName)
        .Get<AiProviderOptions>()!;

    client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + '/');
    client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
});

// Program.cs — add alongside the existing OllamaClient registration

builder.Services.AddHttpClient("OllamaEmbedClient", (sp, client) =>
{
    var opts = builder.Configuration
        .GetSection(AiProviderOptions.SectionName)
        .Get<AiProviderOptions>()!;

    // Base URL without /v1 — embeddings use /api/embeddings
    var baseUrl = opts.BaseUrl.TrimEnd('/').Replace("/v1", "");
    client.BaseAddress = new Uri(baseUrl);

    // Per-call timeout for a single embedding (nomic-embed-text is fast, ~1-3s)
    client.Timeout = TimeSpan.FromSeconds(30);
});

// ── Qdrant ───────────────────────────────────────────────────────────────── 
builder.Services.AddSingleton(_ =>
{
    var host = builder.Configuration["Qdrant:Host"] ?? "localhost";
    var port = int.Parse(builder.Configuration["Qdrant:Port"] ?? "6334");
    return new QdrantClient(host, port);
});

// ── AI Service (provider-switched via config) ───────────────
var provider = builder.Configuration["AiProvider:Provider"] ?? "Ollama";

if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
    provider.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase))
{
    // builder.Services.AddScoped<IAIChatService, OpenAIChatService>();
    throw new InvalidOperationException(
        $"Provider '{provider}' is configured but OpenAIChatService is not yet implemented.");
}
else
{
    builder.Services.AddScoped<IAIChatService, OllamaChatService>();
}


// ── Document Parsing ─────────────────────────────────────────────────────── 
builder.Services.AddSingleton<IDocumentParser, PdfDocumentParser>();
builder.Services.AddSingleton<IDocumentParser, WordDocumentParser>();
builder.Services.AddSingleton<DocumentParserFactory>();

// ── Embedding + Vector Store ─────────────────────────────────────────────── 
builder.Services.AddScoped<IEmbeddingService, OllamaEmbeddingService>();
builder.Services.AddSingleton<IVectorStore, QdrantVectorStore>();
// Development — in-memory, zero dependencies
builder.Services.AddSingleton<IVectorStore, InMemoryVectorStore>();
builder.Services.AddScoped<RagService>();


// ── Core AI Chat + RAG ───────────────────────────────────────────────────── 
builder.Services.AddScoped<IAIChatService, OllamaChatService>();
builder.Services.AddScoped<IDocumentIndexer, DocumentIndexer>();
builder.Services.AddScoped<IDocumentChatService, DocumentChatService>();

// Job infrastructure
builder.Services.AddSingleton<IJobStore, InMemoryJobStore>();
builder.Services.AddSingleton<DocumentIndexingQueue>();
builder.Services.AddHostedService<DocumentIndexingWorker>();

// ── API Infra ──────────────────────────────────────────────
// In Program.cs — add Semantic Kernel with Ollama as OpenAI-compatible endpoint 
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<AiProviderOptions>>().Value;
    return Kernel.CreateBuilder()
        .AddOpenAIChatCompletion(
            modelId: opts.DefaultModel,
            apiKey: opts.ApiKey.Length > 0 ? opts.ApiKey : "ollama",
            endpoint: new Uri(opts.BaseUrl))
        .Build();
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new() { Title = "Document Chatbot API", Version = "v1" }));
builder.Services.AddCors(o => o.AddPolicy("Default", p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
builder.Services.AddHealthChecks()
    .AddCheck("qdrant", () =>
    {
        return HealthCheckResult.Healthy("Qdrant configured");
    });

builder.Services.AddCors(o => o.AddPolicy("Default", p =>
    p.WithOrigins(builder.Configuration
                         .GetSection("Cors:AllowedOrigins")
                         .Get<string[]>() ?? [])
     .AllowAnyMethod()
     .AllowAnyHeader()
     .AllowCredentials()));

builder.Services.AddHealthChecks();

// Program.cs — before builder.Build()
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(10);
});

// Also increase the request body timeout for large uploads
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 52_428_800; // 50 MB
});

var app = builder.Build();

// ── Middleware pipeline ────────────────────────────────────
app.UseSerilogRequestLogging();
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseCors("Default");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");


app.Run();