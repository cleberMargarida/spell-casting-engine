using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net.Http.Json;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;
using Serilog.Formatting.Compact;

// Enable OTel SDK internal diagnostics — surfaces OTLP export failures to stderr
// so you can see auth errors, network issues, etc. without a debugger attached
AppContext.SetSwitch("OpenTelemetry.Experimental.EnableInMemoryRetry", true);

// This name is used as the ActivitySource name AND the OTEL_SERVICE_NAME fallback.
// It must match the AddSource() call below so spans are actually captured.
const string ServiceName = "spell-caster";

var builder = WebApplication.CreateBuilder(args);

// Serilog: write structured JSON to stdout.
// .WithSpan() automatically injects the active trace/span IDs into every log line
// as @tr and @sp fields — this is what links logs to traces in New Relic.
builder.Host.UseSerilog((context, config) => config
    .WriteTo.Console(new CompactJsonFormatter())
    .Enrich.WithSpan());

// OpenTelemetry setup — traces AND logs share the same OTLP exporter (UseOtlpExporter).
// Endpoint/headers/protocol are read from env vars: OTEL_EXPORTER_OTLP_ENDPOINT,
// OTEL_EXPORTER_OTLP_HEADERS, OTEL_EXPORTER_OTLP_PROTOCOL (set in docker-compose / .env).
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(ServiceName))
    .WithTracing(t => t
        .AddSource(ServiceName)               // capture spans from our own ActivitySource
        .AddAspNetCoreInstrumentation()       // auto-span for every incoming HTTP request
        .AddHttpClientInstrumentation())      // auto-span for every outgoing HttpClient call
    .WithLogging()                            // route OTel log records through the pipeline
    .UseOtlpExporter();                       // unified exporter — one config, both signals

// Named HttpClient for arcane-engine. The base URL comes from config key "ArcaneEngine:Url"
// which is set via the ArcaneEngine__Url env var (ASP.NET Core maps __ → : in config keys).
builder.Services.AddHttpClient("arcane-engine", client =>
{
    var url = builder.Configuration["ArcaneEngine:Url"] ?? "http://localhost:5000";
    client.BaseAddress = new Uri(url);
});

var app = builder.Build();

// ActivitySource is the .NET equivalent of an OTel Tracer — use it to create manual spans.
var activitySource = new ActivitySource(ServiceName);

app.MapPost("/cast-spell", async (SpellRequest request, IHttpClientFactory httpClientFactory) =>
{
    // Start a child span inside the auto-generated ASP.NET Core span.
    // "cast-spell.request" is the span name visible in New Relic's trace waterfall.
    using var activity = activitySource.StartActivity("cast-spell.request");

    // Span attributes become searchable dimensions in New Relic (filter by spell.type, etc.)
    activity?.SetTag("spell.type", request.SpellType);
    activity?.SetTag("spell.power", request.Power);

    var client = httpClientFactory.CreateClient("arcane-engine");

    // W3C traceparent header is injected automatically by HttpClientInstrumentation,
    // which propagates the trace context to arcane-engine so spans link correctly.
    var response = await client.PostAsJsonAsync("/resolve-spell", request);

    if (!response.IsSuccessStatusCode)
    {
        // Mark the span as failed — this sets the OTel status and shows as an error in NR
        activity?.SetStatus(ActivityStatusCode.Error, "arcane-engine call failed");
        Log.Error("Arcane engine call failed with status {StatusCode}, traceId={TraceId}, spanId={SpanId}",
            (int)response.StatusCode,
            Activity.Current?.TraceId.ToString(),
            Activity.Current?.SpanId.ToString());
        return Results.StatusCode((int)response.StatusCode);
    }

    var result = await response.Content.ReadFromJsonAsync<SpellResult>()
        ?? throw new InvalidOperationException("Empty response from arcane-engine");

    // Attach the outcome as attributes so you can query them in NRQL:
    // SELECT * FROM Span WHERE spell.result = 'backfire'
    activity?.SetTag("spell.result", result.Result);
    activity?.SetTag("spell.damage", result.Damage);

    // Structured log — Serilog + WithSpan() will stamp @tr/@sp on this automatically
    Log.Information("Spell processed: type={SpellType}, power={Power}, result={Result}, damage={Damage}, traceId={TraceId}, spanId={SpanId}",
        request.SpellType,
        request.Power,
        result.Result,
        result.Damage,
        Activity.Current?.TraceId.ToString(),
        Activity.Current?.SpanId.ToString());

    // Return traceId to the caller so the frontend can display it for manual lookup in NR
    return Results.Ok(new
    {
        result = result.Result,
        damage = result.Damage,
        traceId = Activity.Current?.TraceId.ToString()
    });
});

app.Run();

// Minimal record types for JSON deserialization — no extra ceremony needed in a POC
record SpellRequest(string SpellType, int Power);
record SpellResult(string Result, int Damage);
