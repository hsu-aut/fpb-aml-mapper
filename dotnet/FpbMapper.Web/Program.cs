using Aml.Engine.CAEX;
using FpbMapper.Conversion;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.SetIsOriginAllowed(origin =>
            {
                var host = new Uri(origin).Host;
                return host == "fpbjs.net"
                    || host.EndsWith(".fpbjs.net")
                    || host == "localhost";
            })
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("X-Conversion-Warnings"));
});
var app = builder.Build();

app.UseCors();

// JSON -> AML
app.MapPost("/api/to-aml", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var json = await reader.ReadToEndAsync();

    try
    {
        var result = FpbJsonToCaex.Convert(json);
        var tmpFile = Path.GetTempFileName();
        try
        {
            result.Value.SaveToFile(tmpFile, true);
            var xml = await File.ReadAllTextAsync(tmpFile);
            if (result.Warnings.Count > 0)
                ctx.Response.Headers["X-Conversion-Warnings"] = System.Text.Json.JsonSerializer.Serialize(result.Warnings);
            ctx.Response.ContentType = "application/xml";
            await ctx.Response.WriteAsync(xml);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }
    catch (Exception ex)
    {
        ctx.Response.StatusCode = 400;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

// AML -> JSON
app.MapPost("/api/to-json", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var aml = await reader.ReadToEndAsync();

    try
    {
        var doc = CAEXDocument.LoadFromString(aml);
        var result = CaexToFpbJson.Convert(doc);
        if (result.Warnings.Count > 0)
            ctx.Response.Headers["X-Conversion-Warnings"] = System.Text.Json.JsonSerializer.Serialize(result.Warnings);
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(result.Value);
    }
    catch (Exception ex)
    {
        ctx.Response.StatusCode = 400;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

app.Run();
