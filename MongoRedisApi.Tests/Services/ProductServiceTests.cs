using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Moq;
using MongoRedisApi.Configuration;
using MongoRedisApi.Models;
using MongoRedisApi.Services;

namespace MongoRedisApi.Tests.Services;

/// <summary>
/// Unit tests for ProductService.
///
/// KEY CONCEPT — What is a unit test?
///   A unit test verifies a single piece of logic in isolation. "Isolation" means
///   we replace every external dependency (MongoDB, Redis) with a fake (Mock) that
///   we fully control. This lets tests run instantly without a real database.
///
/// KEY CONCEPT — What is Moq?
///   Moq is a mocking library. A Mock<T> creates an in-memory stand-in for any
///   interface. You tell it what to return for specific calls, then later verify
///   those calls were (or were not) made.
///
/// KEY CONCEPT — Why interfaces matter for testability
///   ProductService depends on IMongoCollection&lt;T&gt; and IDistributedCache — both
///   interfaces. Moq can replace them. If the service accepted concrete classes
///   instead, swapping them out would be impossible.
/// </summary>
public class ProductServiceTests
{
    // ------------------------------------------------------------------
    // Shared options — must match the ones inside ProductService exactly
    // so that the JSON round-trip in cache-hit tests works correctly.
    // ------------------------------------------------------------------
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ------------------------------------------------------------------
    // Mocks — created fresh for every test (constructor runs per test).
    // ------------------------------------------------------------------
    private readonly Mock<IMongoClient> _mockClient = new();
    private readonly Mock<IMongoDatabase> _mockDatabase = new();
    private readonly Mock<IMongoCollection<Product>> _mockCollection = new();
    private readonly Mock<IMongoIndexManager<Product>> _mockIndexManager = new();
    private readonly Mock<IDistributedCache> _mockCache = new();

    // Cache key constants mirrored from the service (no magic strings in tests).
    private const string AllKey = "products:all";
    private static string ItemKey(string id) => $"products:{id}";

    public ProductServiceTests()
    {
        // Wire up the MongoDB dependency chain:
        //   IMongoClient → IMongoDatabase → IMongoCollection → IMongoIndexManager
        _mockClient
            .Setup(c => c.GetDatabase(It.IsAny<string>(), null))
            .Returns(_mockDatabase.Object);

        _mockDatabase
            .Setup(d => d.GetCollection<Product>(It.IsAny<string>(), null))
            .Returns(_mockCollection.Object);

        _mockCollection
            .Setup(c => c.Indexes)
            .Returns(_mockIndexManager.Object);

        // EnsureIndexesAsync() is called in the constructor — stub it out.
        _mockIndexManager
            .Setup(i => i.CreateOneAsync(
                It.IsAny<CreateIndexModel<Product>>(),
                It.IsAny<CreateOneIndexOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("category_1");
    }

    // ------------------------------------------------------------------
    // Factory method — builds the real ProductService with mock dependencies.
    // ------------------------------------------------------------------
    private ProductService CreateService() =>
        new(
            _mockClient.Object,
            new MongoDbSettings
            {
                ConnectionString = "mongodb://localhost",
                DatabaseName    = "test",
                CollectionName  = "Products"
            },
            _mockCache.Object,
            NullLogger<ProductService>.Instance  // silences log output during tests
        );

    // ================================================================== //
    //  Helper: simulate a Redis cache miss for a given key               //
    // ================================================================== //
    private void SetupCacheMiss(string key) =>
        _mockCache
            .Setup(c => c.GetAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);  // null = not in cache

    // ================================================================== //
    //  Helper: simulate a Redis cache hit — serialize value to bytes     //
    // ================================================================== //
    private void SetupCacheHit<T>(string key, T value)
    {
        // GetStringAsync (extension) internally calls GetAsync and converts
        // the byte[] to a string using UTF-8. We reverse that here.
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));
        _mockCache
            .Setup(c => c.GetAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(bytes);
    }

    // ================================================================== //
    //  Helper: allow SetAsync calls without throwing (void-like stub)    //
    // ================================================================== //
    private void SetupCacheSet() =>
        _mockCache
            .Setup(c => c.SetAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

    // ================================================================== //
    //  Helper: allow RemoveAsync calls without throwing                  //
    // ================================================================== //
    private void SetupCacheRemove() =>
        _mockCache
            .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

    // ================================================================== //
    //  Helper: set up FindAsync to return a fake cursor with given items //
    // ================================================================== //
    private void SetupFindCursor(IEnumerable<Product> items)
    {
        // IAsyncCursor<T> is the MongoDB abstraction for a streaming result set.
        // ToListAsync() / FirstOrDefaultAsync() call MoveNextAsync + Current.
        var cursor = new Mock<IAsyncCursor<Product>>();
        cursor
            .SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)   // first call: there is a batch
            .ReturnsAsync(false); // second call: no more batches
        cursor
            .Setup(c => c.Current)
            .Returns(items.ToList());

        _mockCollection
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<Product>>(),
                It.IsAny<FindOptions<Product, Product>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);
    }

    // ================================================================== //
    //  GetAllAsync                                                        //
    // ================================================================== //

    [Fact]
    public async Task GetAllAsync_CacheMiss_QueriesMongoDbAndWritesCache()
    {
        // Arrange
        var products = new List<Product>
        {
            new() { Id = "aaa", Name = "Widget",  Price = 9.99m  },
            new() { Id = "bbb", Name = "Gadget",  Price = 19.99m }
        };

        SetupCacheMiss(AllKey);
        SetupFindCursor(products);
        SetupCacheSet();

        var service = CreateService();

        // Act
        var result = await service.GetAllAsync();

        // Assert
        Assert.Equal(2, result.Count);

        // MongoDB WAS queried
        _mockCollection.Verify(c => c.FindAsync(
            It.IsAny<FilterDefinition<Product>>(),
            It.IsAny<FindOptions<Product, Product>>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Result WAS written into the cache
        _mockCache.Verify(c => c.SetAsync(
            AllKey,
            It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAllAsync_CacheHit_ReturnsCachedDataWithoutMongoDb()
    {
        // Arrange
        var products = new List<Product> { new() { Id = "aaa", Name = "Widget" } };
        SetupCacheHit(AllKey, products);

        var service = CreateService();

        // Act
        var result = await service.GetAllAsync();

        // Assert
        Assert.Single(result);
        Assert.Equal("Widget", result[0].Name);

        // MongoDB was NOT touched
        _mockCollection.Verify(c => c.FindAsync(
            It.IsAny<FilterDefinition<Product>>(),
            It.IsAny<FindOptions<Product, Product>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ================================================================== //
    //  GetByIdAsync                                                       //
    // ================================================================== //

    [Fact]
    public async Task GetByIdAsync_CacheMiss_QueriesMongoDbAndWritesCache()
    {
        // Arrange
        const string id = "abc123";
        var product = new Product { Id = id, Name = "Widget", Price = 9.99m };

        SetupCacheMiss(ItemKey(id));
        SetupFindCursor(new[] { product });
        SetupCacheSet();

        var service = CreateService();

        // Act
        var result = await service.GetByIdAsync(id);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Widget", result.Name);

        _mockCache.Verify(c => c.SetAsync(
            ItemKey(id),
            It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetByIdAsync_CacheHit_ReturnsCachedProductWithoutMongoDb()
    {
        // Arrange
        const string id = "abc123";
        var product = new Product { Id = id, Name = "Widget" };
        SetupCacheHit(ItemKey(id), product);

        var service = CreateService();

        // Act
        var result = await service.GetByIdAsync(id);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Widget", result!.Name);

        _mockCollection.Verify(c => c.FindAsync(
            It.IsAny<FilterDefinition<Product>>(),
            It.IsAny<FindOptions<Product, Product>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_ReturnsNull()
    {
        // Arrange
        const string id = "nonexistent";
        SetupCacheMiss(ItemKey(id));
        SetupFindCursor(Array.Empty<Product>());  // empty cursor → no product

        var service = CreateService();

        // Act
        var result = await service.GetByIdAsync(id);

        // Assert
        Assert.Null(result);

        // Cache should NOT be written when the product does not exist
        _mockCache.Verify(c => c.SetAsync(
            It.IsAny<string>(),
            It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ================================================================== //
    //  CreateAsync                                                        //
    // ================================================================== //

    [Fact]
    public async Task CreateAsync_InsertsIntoMongoDbAndInvalidatesListCache()
    {
        // Arrange
        var product = new Product { Name = "New Widget", Price = 9.99m };

        _mockCollection
            .Setup(c => c.InsertOneAsync(product, null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        SetupCacheRemove();

        var service = CreateService();

        // Act
        var result = await service.CreateAsync(product);

        // Assert
        Assert.Equal("New Widget", result.Name);

        _mockCollection.Verify(
            c => c.InsertOneAsync(product, null, It.IsAny<CancellationToken>()),
            Times.Once);

        // The list cache key should be invalidated so the next GET re-fetches from DB
        _mockCache.Verify(
            c => c.RemoveAsync(AllKey, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ================================================================== //
    //  UpdateAsync                                                        //
    // ================================================================== //

    [Fact]
    public async Task UpdateAsync_WhenProductExists_ReturnsTrue_AndInvalidatesCache()
    {
        // Arrange
        const string id = "abc123";
        var product = new Product { Name = "Updated Widget" };

        var replaceResult = new Mock<ReplaceOneResult>();
        replaceResult.Setup(r => r.ModifiedCount).Returns(1);  // 1 document was modified

        _mockCollection
            .Setup(c => c.ReplaceOneAsync(
                It.IsAny<FilterDefinition<Product>>(),
                product,
                It.IsAny<ReplaceOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(replaceResult.Object);

        SetupCacheRemove();

        var service = CreateService();

        // Act
        var result = await service.UpdateAsync(id, product);

        // Assert
        Assert.True(result);
        _mockCache.Verify(c => c.RemoveAsync(ItemKey(id), It.IsAny<CancellationToken>()), Times.Once);
        _mockCache.Verify(c => c.RemoveAsync(AllKey,      It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_WhenProductNotFound_ReturnsFalse_AndDoesNotTouchCache()
    {
        // Arrange
        const string id = "notexist";
        var product = new Product { Name = "Ghost" };

        var replaceResult = new Mock<ReplaceOneResult>();
        replaceResult.Setup(r => r.ModifiedCount).Returns(0);  // nothing was changed

        _mockCollection
            .Setup(c => c.ReplaceOneAsync(
                It.IsAny<FilterDefinition<Product>>(),
                product,
                It.IsAny<ReplaceOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(replaceResult.Object);

        var service = CreateService();

        // Act
        var result = await service.UpdateAsync(id, product);

        // Assert
        Assert.False(result);
        _mockCache.Verify(
            c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ================================================================== //
    //  DeleteAsync                                                        //
    // ================================================================== //

    [Fact]
    public async Task DeleteAsync_WhenProductExists_ReturnsTrue_AndInvalidatesCache()
    {
        // Arrange
        const string id = "abc123";
        var deleteResult = new Mock<DeleteResult>();
        deleteResult.Setup(r => r.DeletedCount).Returns(1);

        _mockCollection
            .Setup(c => c.DeleteOneAsync(
                It.IsAny<FilterDefinition<Product>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(deleteResult.Object);

        SetupCacheRemove();

        var service = CreateService();

        // Act
        var result = await service.DeleteAsync(id);

        // Assert
        Assert.True(result);
        _mockCache.Verify(c => c.RemoveAsync(ItemKey(id), It.IsAny<CancellationToken>()), Times.Once);
        _mockCache.Verify(c => c.RemoveAsync(AllKey,      It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_WhenProductNotFound_ReturnsFalse_AndDoesNotTouchCache()
    {
        // Arrange
        const string id = "notexist";
        var deleteResult = new Mock<DeleteResult>();
        deleteResult.Setup(r => r.DeletedCount).Returns(0);

        _mockCollection
            .Setup(c => c.DeleteOneAsync(
                It.IsAny<FilterDefinition<Product>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(deleteResult.Object);

        var service = CreateService();

        // Act
        var result = await service.DeleteAsync(id);

        // Assert
        Assert.False(result);
        _mockCache.Verify(
            c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
