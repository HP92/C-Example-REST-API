using Microsoft.AspNetCore.Mvc;
using MongoRedisApi.Models;
using MongoRedisApi.Services;

namespace MongoRedisApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly IProductService _service;
    private readonly ILogger<ProductsController> _logger;

    public ProductsController(IProductService service, ILogger<ProductsController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>Returns all products (served from Redis cache when available).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<Product>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<Product>>> GetAll()
    {
        _logger.LogInformation("GET /api/products — retrieving all products");
        var products = await _service.GetAllAsync();
        _logger.LogInformation("GET /api/products — returning {Count} product(s)", products.Count);
        return Ok(products);
    }

    /// <summary>Returns a single product by its MongoDB ObjectId.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Product), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Product>> GetById(string id)
    {
        _logger.LogInformation("GET /api/products/{Id} — retrieving product", id);

        if (!IsValidObjectId(id))
        {
            _logger.LogWarning("GET /api/products/{Id} — bad request: invalid ObjectId format", id);
            return BadRequest($"'{id}' is not a valid product ID. Expected a 24-character hex string.");
        }

        var product = await _service.GetByIdAsync(id);
        if (product is null)
        {
            _logger.LogWarning("GET /api/products/{Id} — not found", id);
            return NotFound($"Product with ID '{id}' was not found.");
        }

        _logger.LogInformation("GET /api/products/{Id} — product found and returned", id);
        return Ok(product);
    }

    /// <summary>Creates a new product and invalidates the list cache.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(Product), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Product>> Create([FromBody] Product product)
    {
        _logger.LogInformation(
            "POST /api/products — creating product: Name={Name}, Category={Category}, Price={Price}",
            product.Name, product.Category, product.Price);

        var created = await _service.CreateAsync(product);
        _logger.LogInformation("POST /api/products — product created with ID {Id}", created.Id);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Replaces an existing product and invalidates the relevant cache entries.
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(string id, [FromBody] Product product)
    {
        _logger.LogInformation("PUT /api/products/{Id} — updating product", id);

        if (!IsValidObjectId(id))
        {
            _logger.LogWarning("PUT /api/products/{Id} — bad request: invalid ObjectId format", id);
            return BadRequest($"'{id}' is not a valid product ID. Expected a 24-character hex string.");
        }

        var updated = await _service.UpdateAsync(id, product);
        if (!updated)
        {
            _logger.LogWarning("PUT /api/products/{Id} — not found", id);
            return NotFound($"Product with ID '{id}' was not found.");
        }

        _logger.LogInformation("PUT /api/products/{Id} — product updated successfully", id);
        return NoContent();
    }

    /// <summary>Deletes a product and removes it from the cache.</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id)
    {
        _logger.LogInformation("DELETE /api/products/{Id} — deleting product", id);

        if (!IsValidObjectId(id))
        {
            _logger.LogWarning("DELETE /api/products/{Id} — bad request: invalid ObjectId format", id);
            return BadRequest($"'{id}' is not a valid product ID. Expected a 24-character hex string.");
        }

        var deleted = await _service.DeleteAsync(id);
        if (!deleted)
        {
            _logger.LogWarning("DELETE /api/products/{Id} — not found", id);
            return NotFound($"Product with ID '{id}' was not found.");
        }

        _logger.LogInformation("DELETE /api/products/{Id} — product deleted successfully", id);
        return NoContent();
    }

    // Returns true only for 24-character hex strings (MongoDB ObjectId format).
    private static bool IsValidObjectId(string id) =>
        id.Length == 24 && id.All(char.IsAsciiHexDigit);
}
