using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using MongoDB.Driver;
using MongoRedisApi.Configuration;
using MongoRedisApi.Models;

namespace MongoRedisApi.Services;

public class ProductService : IProductService
{
    private readonly IMongoCollection<Product> _collection;
    private readonly IDistributedCache _cache;
    private readonly ILogger<ProductService> _logger;

    private const string AllProductsCacheKey = "products:all";

    // Cache entries expire after 5 minutes absolute; slides back on access up to 2 min.
    private static readonly DistributedCacheEntryOptions DefaultCacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
        SlidingExpiration = TimeSpan.FromMinutes(2)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ProductService(
        IMongoClient mongoClient,
        MongoDbSettings settings,
        IDistributedCache cache,
        ILogger<ProductService> logger)
    {
        var database = mongoClient.GetDatabase(settings.DatabaseName);
        _collection = database.GetCollection<Product>(settings.CollectionName);
        _cache = cache;
        _logger = logger;

        EnsureIndexesAsync().GetAwaiter().GetResult();
    }

    // ------------------------------------------------------------------ //
    //  Public API                                                          //
    // ------------------------------------------------------------------ //

    public async Task<List<Product>> GetAllAsync()
    {
        _logger.LogInformation("GetAllAsync: checking cache for key '{Key}'", AllProductsCacheKey);
        var cached = await _cache.GetStringAsync(AllProductsCacheKey);
        if (cached is not null)
        {
            _logger.LogDebug("Cache HIT — {Key}", AllProductsCacheKey);
            var cachedProducts = JsonSerializer.Deserialize<List<Product>>(cached, JsonOptions)!;
            _logger.LogInformation("GetAllAsync: returned {Count} product(s) from cache", cachedProducts.Count);
            return cachedProducts;
        }

        _logger.LogDebug("Cache MISS — {Key}; querying MongoDB", AllProductsCacheKey);
        using var allCursor = await _collection.FindAsync(Builders<Product>.Filter.Empty);
        var products = await allCursor.ToListAsync();
        _logger.LogInformation("GetAllAsync: fetched {Count} product(s) from MongoDB", products.Count);

        await _cache.SetStringAsync(
            AllProductsCacheKey,
            JsonSerializer.Serialize(products, JsonOptions),
            DefaultCacheOptions);
        _logger.LogDebug("GetAllAsync: stored results in cache under key '{Key}'", AllProductsCacheKey);

        return products;
    }

    public async Task<Product?> GetByIdAsync(string id)
    {
        var cacheKey = ProductCacheKey(id);
        _logger.LogInformation("GetByIdAsync: checking cache for key '{Key}'", cacheKey);
        var cached = await _cache.GetStringAsync(cacheKey);
        if (cached is not null)
        {
            _logger.LogDebug("Cache HIT — {Key}", cacheKey);
            _logger.LogInformation("GetByIdAsync: product {Id} served from cache", id);
            return JsonSerializer.Deserialize<Product>(cached, JsonOptions);
        }

        _logger.LogDebug("Cache MISS — {Key}; querying MongoDB", cacheKey);
        using var singleCursor = await _collection.FindAsync(Builders<Product>.Filter.Eq(p => p.Id, id));
        var product = await singleCursor.FirstOrDefaultAsync();

        if (product is null)
        {
            _logger.LogWarning("GetByIdAsync: product {Id} not found in MongoDB", id);
            return null;
        }

        _logger.LogInformation("GetByIdAsync: product {Id} fetched from MongoDB and cached", id);
        await _cache.SetStringAsync(
            cacheKey,
            JsonSerializer.Serialize(product, JsonOptions),
            DefaultCacheOptions);

        return product;
    }

    public async Task<Product> CreateAsync(Product product)
    {
        product.CreatedAt = DateTime.UtcNow;
        _logger.LogInformation(
            "CreateAsync: inserting product — Name: {Name}, Category: {Category}, Price: {Price}",
            product.Name, product.Category, product.Price);
        await _collection.InsertOneAsync(product);
        _logger.LogInformation("CreateAsync: product inserted with ID {Id}", product.Id);
        await InvalidateListCacheAsync();
        _logger.LogDebug("CreateAsync: invalidated list cache key '{Key}'", AllProductsCacheKey);
        return product;
    }

    public async Task<bool> UpdateAsync(string id, Product product)
    {
        product.Id = id;
        _logger.LogInformation("UpdateAsync: replacing product {Id}", id);
        var result = await _collection.ReplaceOneAsync(p => p.Id == id, product);

        if (result.ModifiedCount == 0)
        {
            _logger.LogWarning("UpdateAsync: product {Id} not found or no changes applied (ModifiedCount=0)", id);
            return false;
        }

        _logger.LogInformation("UpdateAsync: product {Id} replaced successfully", id);
        await Task.WhenAll(
            _cache.RemoveAsync(ProductCacheKey(id)),
            InvalidateListCacheAsync());
        _logger.LogDebug("UpdateAsync: invalidated cache entries for product {Id}", id);

        return true;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        _logger.LogInformation("DeleteAsync: deleting product {Id}", id);
        var result = await _collection.DeleteOneAsync(p => p.Id == id);

        if (result.DeletedCount == 0)
        {
            _logger.LogWarning("DeleteAsync: product {Id} not found (DeletedCount=0)", id);
            return false;
        }

        _logger.LogInformation("DeleteAsync: product {Id} deleted successfully", id);
        await Task.WhenAll(
            _cache.RemoveAsync(ProductCacheKey(id)),
            InvalidateListCacheAsync());
        _logger.LogDebug("DeleteAsync: invalidated cache entries for product {Id}", id);

        return true;
    }

    // ------------------------------------------------------------------ //
    //  Helpers                                                             //
    // ------------------------------------------------------------------ //

    private static string ProductCacheKey(string id) => $"products:{id}";

    private Task InvalidateListCacheAsync() =>
        _cache.RemoveAsync(AllProductsCacheKey);

    /// <summary>Creates indexes on first startup if they don't already exist.</summary>
    private async Task EnsureIndexesAsync()
    {
        _logger.LogInformation("EnsureIndexesAsync: ensuring MongoDB indexes on startup");
        var categoryIndex = Builders<Product>.IndexKeys.Ascending(p => p.Category);
        await _collection.Indexes.CreateOneAsync(
            new CreateIndexModel<Product>(categoryIndex));
        _logger.LogInformation("EnsureIndexesAsync: index on 'Category' field confirmed");
    }
}
