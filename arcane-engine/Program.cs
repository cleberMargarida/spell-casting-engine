using System.Diagnostics;
using System.Diagnostics.Tracing;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;
using Serilog.Formatting.Compact;

// Enable OTel SDK internal diagnostics — surfaces OTLP export failures to stderr
AppContext.SetSwitch("OpenTelemetry.Experimental.EnableInMemoryRetry", true);

const string ServiceName = "arcane-engine";

var builder = WebApplication.CreateBuilder(args);

// Serilog: structured JSON console output.
// WithSpan() injects @tr (traceId) and @sp (spanId) into every log line automatically,
// which New Relic uses to correlate log entries with the distributed trace.
builder.Host.UseSerilog((context, config) => config
    .WriteTo.Console(new CompactJsonFormatter())
    .Enrich.WithSpan());

// OpenTelemetry setup — traces AND logs go through one unified OTLP exporter.
// NOTE: we deliberately do NOT use Npgsql.OpenTelemetry auto-instrumentation here.
// That would add a 4th auto-generated span and break the intentional 3-span trace design:
//   1. ASP.NET Core span (auto)  2. arcane-engine.resolve-spell (manual)  3. db.persist-spell (manual)
// Instead, we create the db span manually to stay in full control.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(ServiceName))
    .WithTracing(t => t
        .AddSource(ServiceName)             // capture our own manual spans
        .AddAspNetCoreInstrumentation())    // auto-span for every incoming HTTP request
    .WithLogging()                          // route OTel log records through the pipeline
    .UseOtlpExporter();                     // unified exporter — reads OTEL_EXPORTER_OTLP_* env vars

// Build NpgsqlDataSource (connection pool) from config.
// In Docker the connection string is injected via ConnectionStrings__Default env var,
// which ASP.NET Core maps to ConnectionStrings:Default in the config system.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Database=spells;Username=postgres;Password=postgres";
var dataSource = NpgsqlDataSource.Create(connectionString);
builder.Services.AddSingleton(dataSource); // one pool for the lifetime of the app

var app = builder.Build();
var activitySource = new ActivitySource(ServiceName);

// Read DB failure flag once at startup — toggle with SIMULATE_DB_FAILURE=true in docker-compose
var simulateDbFailure = builder.Configuration["SIMULATE_DB_FAILURE"]?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

app.MapPost("/resolve-spell", async (SpellRequest request, NpgsqlDataSource db) =>
{
    // Span 2 of 3 in the distributed trace — child of the ASP.NET Core auto-span.
    // spell-caster's HttpClientInstrumentation injected W3C traceparent into the request header,
    // and AspNetCoreInstrumentation here picks it up to continue the same trace automatically.
    using var activity = activitySource.StartActivity("arcane-engine.resolve-spell");
    activity?.SetTag("spell.type", request.SpellType);
    activity?.SetTag("spell.power", request.Power);

    // Simulate realistic latency between 100ms and 2000ms.
    // This makes the latency widgets in the New Relic dashboard actually interesting.
    await Task.Delay(Random.Shared.Next(100, 2001));

    // --- Spell outcome determination ---
    string result;
    int damage;
    var roll = Random.Shared.NextDouble(); // random 0.0–1.0 for probability checks

    if (request.Power > 8 && roll < 0.30)
    {
        // High-power spells have a 30% chance of backfiring — caster takes no damage, spell fails
        result = "backfire";
        damage = 0;
    }
    else if (request.Power >= 7 && roll < 0.20)
    {
        // Power ≥7 has a 20% chance of critical hit — double multiplier (×20 instead of ×15)
        result = "critical";
        damage = request.Power * 20;
    }
    else
    {
        // Standard success — linear damage scaling with power
        result = "success";
        damage = request.Power * 15;
    }

    // Stamp outcome attributes on the span so NRQL can filter by them:
    // SELECT count(*) FROM Span WHERE spell.result = 'backfire' FACET spell.type
    activity?.SetTag("spell.result", result);
    activity?.SetTag("spell.damage", damage);

    // DB failure simulation — useful for demonstrating error propagation in New Relic.
    // Set SIMULATE_DB_FAILURE=true in .env and restart to trigger error spans and logs.
    if (simulateDbFailure)
    {
        activity?.SetStatus(ActivityStatusCode.Error, "Simulated DB failure");
        Log.Error("Simulated DB failure: type={SpellType}, power={Power}, traceId={TraceId}, spanId={SpanId}",
            request.SpellType,
            request.Power,
            Activity.Current?.TraceId.ToString(),
            Activity.Current?.SpanId.ToString());
        throw new InvalidOperationException("Simulated database failure");
    }

    // Span 3 of 3 — manual span wrapping the DB INSERT.
    // We create this manually instead of using Npgsql auto-instrumentation to keep exactly
    // 3 spans per trace. The span ends when the using block exits (success or exception).
    using (var dbSpan = activitySource.StartActivity("db.persist-spell"))
    {
        await using var conn = await db.OpenConnectionAsync();
        // Parameterized query — $1, $2, $3, $4 placeholders prevent SQL injection
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO spells (spell_type, power, result, damage) VALUES ($1, $2, $3, $4)", conn)
        {
            Parameters =
            {
                new() { Value = request.SpellType },
                new() { Value = request.Power },
                new() { Value = result },
                new() { Value = damage }
            }
        };
        await cmd.ExecuteNonQueryAsync();
    } // dbSpan disposed here — span end timestamp is recorded

    // Structured log — WithSpan() auto-stamps @tr/@sp so New Relic links this log to the trace
    Log.Information("Spell processed: type={SpellType}, power={Power}, result={Result}, damage={Damage}, traceId={TraceId}, spanId={SpanId}",
        request.SpellType,
        request.Power,
        result,
        damage,
        Activity.Current?.TraceId.ToString(),
        Activity.Current?.SpanId.ToString());

    return Results.Ok(new { result, damage });
});

app.Run();

// Minimal record for JSON deserialization from spell-caster's POST body
record SpellRequest(string SpellType, int Power);
