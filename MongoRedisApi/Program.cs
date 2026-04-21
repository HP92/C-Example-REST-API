using MongoDB.Driver;
using MongoRedisApi.Configuration;
using MongoRedisApi.Services;

var builder = WebApplication.CreateBuilder(args);

// --- MongoDB ---
// Single IMongoClient registered as singleton; creating it once avoids
// expensive reconnections and allows the driver to manage its connection pool.
var mongoSettings = builder.Configuration
    .GetSection("MongoDbSettings")
    .Get<MongoDbSettings>()!;

builder.Services.AddSingleton(mongoSettings);
builder.Services.AddSingleton<IMongoClient>(_ =>
{
    var clientSettings = MongoClientSettings.FromConnectionString(mongoSettings.ConnectionString);
    clientSettings.ServerSelectionTimeout = TimeSpan.FromSeconds(30);
    clientSettings.ConnectTimeout = TimeSpan.FromSeconds(15);
    return new MongoClient(clientSettings);
});

// --- Redis distributed cache ---
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("Redis");
    options.InstanceName = "MongoRedisApi:";
});

// --- Application services ---
builder.Services.AddScoped<IProductService, ProductService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "MongoRedisApi", Version = "v1" });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();

app.Run();
