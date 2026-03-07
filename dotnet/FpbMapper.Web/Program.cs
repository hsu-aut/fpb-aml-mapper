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
            .AllowAnyMethod());
});
var app = builder.Build();

app.UseCors();

// JSON -> AML
app.MapPost("/api/to-aml", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var json = await reader.ReadToEndAsync();

    try
    {
        var doc = FpbJsonToCaex.Convert(json);
        var tmpFile = Path.GetTempFileName();
        try
        {
            doc.SaveToFile(tmpFile, true);
            var xml = await File.ReadAllTextAsync(tmpFile);
            return Results.Text(xml, "application/xml");
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 400);
    }
});

// AML -> JSON
app.MapPost("/api/to-json", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var aml = await reader.ReadToEndAsync();

    try
    {
        var doc = CAEXDocument.LoadFromString(aml);
        var json = CaexToFpbJson.Convert(doc);
        return Results.Text(json, "application/json");
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 400);
    }
});

app.Run();
